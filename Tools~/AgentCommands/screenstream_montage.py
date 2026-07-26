#!/usr/bin/env python3
"""screenstream_montage.py — ScreenStream 多 Frame 合成單圖工具 (2026-06-06 summit).

# 區塊職責：把 ScreenStream ring buffer 裡多張 frame 一次撈出 → (可選)裁切某區域 → 降解析度 → 拼成一張縮圖牆
# 物理意義：跟 screenstream_daemon.py (寫 frame) / screenstream_annotate.py (標 frame) 同家族;
#          本工具是「讀多 frame」消費端 — agent 只 Read 一張合成圖即可掌握一段時間的(局部)演變,
#          省 context + 省 token, 取代「一張一張 Read 歷史 frame」。
# 數值影響：純讀既有 disk JPEG + PIL 影像處理, 不碰 Editor / daemon state; 12 張 1080p 合成約 <1s。

設計依據: docs/Plan/Plan_ScreenStream_Montage_Cmd.md (Tim 2026-06-06 拍板 python 工具, basecamp peer-review 收斂)

⚠️ 核心正確性鐵律 (identity layer 混淆 family):
  Ring buffer 檔名 frame_NNNN.jpg 是「槽位編號」, 繞圈覆寫後 frame_0001 可能最新也可能最舊。
  → 選完 frame 一律按 (mtime, frame_idx) 排序成時間序, 絕不靠檔名 index 排。
  → 同秒 tiebreaker 用 frame_idx (daemon 1fps 平常不撞, 但 ring buffer 繞圈邊界會擠同秒)。

用法:
  python AgentCommands/Tools/screenstream_montage.py make            # 預設: 最近 60 張每 5 抽 1 = 12 格橫跨 60s
  python AgentCommands/Tools/screenstream_montage.py make --last 12  # 高密度: 最近 12 張(約 12s)
  python AgentCommands/Tools/screenstream_montage.py make --region top-right --last 20 --tile-width 320
  python AgentCommands/Tools/screenstream_montage.py make --crop-pct "0.75,0,0.25,0.25"
  python AgentCommands/Tools/screenstream_montage.py make --crop-px "1440,0,480,360" --cols 4
  python AgentCommands/Tools/screenstream_montage.py list-regions

依賴: Pillow (PIL) — 已用於 daemon, 無新依賴。
"""
from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
import time
from pathlib import Path

# Windows console UTF-8 (跟家族其他工具一致)
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# ===========================================================
# 路徑解析 — 本檔已遷入 <UCL_Core>/Tools~/AgentCommands (跨專案共用, 2026-07-26 Tim 拍板)
# 物理意義: 不能再用「上兩層 = repo 根」假設; 改 repo-walk (.git 只認資料夾, 跳過 submodule gitlink),
#          runtime 狀態 (_screenstream/ 與酒館 view) 一律落「主專案」AgentCommands。
# ===========================================================
HERE = Path(__file__).resolve().parent


def _find_git_root(start: Path):
    # 逐層向上找 .git「資料夾」(submodule 的 .git 是檔案 → 被 is_dir 跳過)
    p = start.resolve()
    while p != p.parent:
        if (p / ".git").is_dir():
            return p
        p = p.parent
    return None


def _resolve_repo_root() -> Path:
    # 優先吃 CLAUDE_PROJECT_DIR (agent 環境); 其次從 script 位置 walk; 最後 cwd walk / 上兩層 fallback
    env = os.environ.get("CLAUDE_PROJECT_DIR")
    if env and Path(env).is_dir():
        return Path(env).resolve()
    walked = _find_git_root(HERE)
    if walked:
        return walked
    return _find_git_root(Path.cwd()) or HERE.parent.parent


def _resolve_data_root(root: Path) -> Path:
    # AgentCommands 資料根 — honors <repo>/.agentcommands_root.local pointer (C#/Python 共讀)
    pointer = root / ".agentcommands_root.local"
    try:
        if pointer.exists():
            content = pointer.read_text(encoding="utf-8").strip()
            if content and Path(content).is_absolute():
                return Path(content).resolve()
    except Exception:
        pass
    return (root / "AgentCommands").resolve()


REPO_ROOT = _resolve_repo_root()
DATA_ROOT = _resolve_data_root(REPO_ROOT)
STREAM_DIR = DATA_ROOT / "_screenstream"
FRAMES_DIR = STREAM_DIR / "frames"
DEFAULT_OUT = STREAM_DIR / "_montage.jpg"
# daemon 端 STT 設定來源 (T-STT-AutoAttach, Tim 2026-07-10 拍板「不必帶 --stt, 啟動 STT 就自動打包」):
# montage 讀此檔的 stt_enabled 決定是否自動附掛 STT 段 — 對齊酒館 ride 在 --ocr 上的 opt-out 語意。
STREAM_CONFIG_PATH = STREAM_DIR / "_config.json"


def read_daemon_stt_config():
    """讀 daemon _config.json 的 STT 設定 (T-STT-AutoAttach)。

    區塊職責: 給 montage 一個「STT 源頭是否已啟動」的 single source of truth，
              讓「Tim 在 Page/config 開了 STT」自動等價於「montage 附掛 STT 段」，
              不必觀影 agent 記得帶 --stt (對齊酒館 --ocr auto-ride 範本)。
    物理意義: enabled = daemon SttCacheWorker 是否該在錄 (亦即 cache 是否會被餵);
              model/lang = daemon 實際轉錄用的設定 (拿來當 sidecar 標籤, 誠實對齊)。
    數值影響: 純 local 讀一個小 json; 檔缺 / 壞 → 回 (False, "small", "") fail-soft, 不擋 montage。
    回傳: (enabled: bool, model: str, lang: str)
    """
    try:
        with STREAM_CONFIG_PATH.open("r", encoding="utf-8") as fh:
            cfg = json.load(fh)
        return (bool(cfg.get("stt_enabled", False)),
                str(cfg.get("stt_model", "small") or "small"),
                str(cfg.get("stt_lang", "") or ""))
    except Exception:
        return (False, "small", "")

# ===========================================================
# Tavern tail (T-StreamWatch-TavernSync, Tim 2026-06-14 拍板, kiara 實作)
# 物理意義: stream-watch 每 cycle 觀影同時要兼顧聊天酒館 (Hard Rule #11)。
#          原本 agent 要另外 cat _last_op.md / op=read 第二次 I/O 才看得到;
#          現在 --ocr 時順手把酒館「未讀 (排除自己) 訊息」接在字幕 sidecar 下方,
#          一次 Read 同時拿到「畫面字幕 + 同事對話」, 省一次讀取又防漏看同事 @。
# 數值影響: 來源 = rooms/tavern/_last_view.md (每次 post 即時重渲染, 純 local 讀, 零 Editor daemon 依賴);
#          以 seq 游標做「已讀」過濾 (>since_seq), 以 @<persona>: 後綴做「排除自己」過濾。
#          截斷時取「最舊的未讀 N 筆」(chronological catch-up) 並把游標推到所顯示的最大 seq —
#          保證下輪接著看更舊→更新, 0-gap 不跳過 (對齊 frame cursor 鐵律, 禁靜默截斷)。
# ===========================================================
TAVERN_VIEW = DATA_ROOT / "ChatTavern" / "rooms" / "tavern" / "_last_view.md"
# 每筆 message 起始行: [seq N] HH:MM:SS <Agent大小姐@persona>: <body 第一行>
_TAVERN_MSG_RE = re.compile(r"^\[seq (\d+)\] (\d+:\d+:\d+) (.+?): ?(.*)$")
# meta / refs 是渲染附帶的雜訊行 (Discord 附件 hash 等), 觀影 agent 不需要 → body 過濾掉
# (但 Discord 附件的「本地路徑」例外: 抽出來在 sidecar 露出, agent 用 Read 工具直接看圖 — 見 _extract_tavern_images)
_TAVERN_NOISE_RE = re.compile(r"^\s*-\s*(meta|refs):")
# meta 行裡的 attachments JSON (含每張 Discord 附件的 local 本地路徑) — 反引號包住整段 `attachments=[...]`
_TAVERN_ATTACH_RE = re.compile(r"attachments=(\[.*?\])`")
# refs 行 fallback: '  - refs: [path](path)' 取小括號內本地路徑 (attachments JSON 解析不出時用)
_TAVERN_REFS_RE = re.compile(r"^\s*-\s*refs:\s*\[[^\]]*\]\(([^)]+)\)")
# 單筆 body 過長 (e.g. 含 glossary auto-attach) → 截斷防 sidecar 爆量, 截斷標 … 並誠實附原長
_TAVERN_BODY_CAP = 280


def _extract_tavern_images(line: str, cur: dict):
    """從一行 meta / refs 雜訊行抽 Discord 附件本地圖片路徑, append 進 cur['images'] (去重)。

    物理意義: Discord 圖片同步進酒館後, 真正內容在 meta 行的 attachments JSON 的 `local` 欄
              (退路: refs 行的 markdown 連結路徑)。原本這兩行被當雜訊丟棄, 觀影 agent 只看到
              body 的「[Discord 附件 1 個] image.png」文字、看不到圖。抽出本地路徑後, sidecar
              會列出來讓 agent 用 Read 工具直接看圖 (跟讀 montage 同一種 vision 能力)。
    數值影響: 只收 image/* content_type 的附件 (略過非圖片附件如 .txt/.zip), 圖路徑去重保序。
    """
    # 首選: meta 行的 attachments JSON (有 content_type 可濾非圖片, 有 local 直給本地路徑)
    m = _TAVERN_ATTACH_RE.search(line)
    if m:
        try:
            for a in json.loads(m.group(1)):
                if not isinstance(a, dict):
                    continue
                local = a.get("local")
                ctype = (a.get("content_type") or "").lower()
                fname = (a.get("filename") or "").lower()
                # content_type 缺失時退看副檔名, 避免漏圖
                is_img = ctype.startswith("image/") or fname.endswith(
                    (".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"))
                if local and is_img and local not in cur["images"]:
                    cur["images"].append(local)
            return
        except (json.JSONDecodeError, TypeError):
            pass  # JSON 壞掉 → 落到 refs 退路
    # 退路: refs 行的本地路徑 (attachments JSON 不可用時)
    mr = _TAVERN_REFS_RE.match(line)
    if mr:
        p = mr.group(1).strip()
        if p and p not in cur["images"]:
            cur["images"].append(p)


def render_tavern_tail(self_persona: str, since_seq: int, limit: int):
    """讀 tavern _last_view.md → 回 (section_md, max_shown_seq, shown_count, remaining_older)。

    - self_persona: 排除自己發的訊息 (match sender 後綴 '@<persona>')。空字串=不排除。
    - since_seq: 已讀游標, 只收 seq > since_seq 的未讀。-1=全收。
    - limit: 單輪最多顯示幾筆; 截斷時取「最舊的未讀」(chronological), 游標推到所顯示最大 seq。

    回傳的 max_shown_seq 是「這輪實際顯示的最大 seq」(非全域 max), 供 session 推進游標時
    保證 0-gap — 沒顯示到的更舊未讀留待下輪 (對齊 frame cursor 接續鐵律)。
    找不到檔 / 解析不出任何訊息 → (None, since_seq, 0, 0)。
    """
    if not TAVERN_VIEW.exists():
        return (None, since_seq, 0, 0, 0)
    try:
        raw = TAVERN_VIEW.read_text(encoding="utf-8", errors="replace")
    except Exception:
        return (None, since_seq, 0, 0, 0)

    # ----- 解析成 message blocks -----
    # 區塊職責: 逐行掃, 命中 [seq N] 起始行開新 block, 其後非起始/非 noise 行併入 body。
    msgs = []  # list of dict(seq, time, sender, body)
    cur = None
    for line in raw.splitlines():
        m = _TAVERN_MSG_RE.match(line)
        if m:
            if cur is not None:
                msgs.append(cur)
            cur = {"seq": int(m.group(1)), "time": m.group(2),
                   "sender": m.group(3).strip(), "body": [m.group(4)], "images": []}
        elif cur is not None:
            if _TAVERN_NOISE_RE.match(line):
                # meta/refs 不進 body, 但先抽 Discord 附件本地圖片路徑 (agent 要 Read 看圖)
                _extract_tavern_images(line, cur)
                continue
            cur["body"].append(line)
    if cur is not None:
        msgs.append(cur)
    if not msgs:
        return (None, since_seq, 0, 0, 0)

    # ----- 過濾: 未讀 (seq>since) + 排除自己 (@persona 後綴) -----
    self_suffix = f"@{self_persona}" if self_persona else None
    unread = []
    for d in msgs:
        if d["seq"] <= since_seq:
            continue
        if self_suffix and (d["sender"].endswith(self_suffix) or d["sender"] == self_persona):
            continue
        unread.append(d)
    if not unread:
        # 沒未讀: 仍把游標推到全域 max (含自己/已濾), 避免下輪重掃
        global_max = max(d["seq"] for d in msgs)
        return (None, max(since_seq, global_max), 0, 0, 0)

    unread.sort(key=lambda d: d["seq"])  # 時序由舊到新 (catch-up 順序)
    remaining_older = max(0, len(unread) - limit) if limit > 0 else 0
    shown = unread[:limit] if limit > 0 else unread
    max_shown_seq = shown[-1]["seq"]  # 游標只推到「實際顯示到的」最大 seq → 0-gap

    # ----- 渲染 markdown 段落 -----
    self_note = f"已排除自己 @{self_persona}" if self_persona else "未排除自己"
    title = (f"## 💬 聊天酒館當前訊息（未讀 {len(shown)} 筆, {self_note}, "
             f"已讀 seq≤{since_seq}）")
    lines = [title, ""]
    if remaining_older:
        # 禁靜默截斷: 截斷時誠實標明還有更舊未讀, 下輪會接著看
        lines.append(f"_（本輪僅顯示最舊的 {limit} 筆未讀, 另有 {remaining_older} 筆更舊未讀留待下輪）_")
        lines.append("")
    img_count = 0
    for d in shown:
        body = " ".join(s.strip() for s in d["body"] if s.strip())
        if len(body) > _TAVERN_BODY_CAP:
            body = body[:_TAVERN_BODY_CAP] + f"…（原 {len(body)} 字, 完整內容跑 op=read）"
        lines.append(f"- **[seq {d['seq']}] {d['time']} {d['sender']}**: {body}")
        # Discord 附件圖片: 列出本地路徑, agent 用 Read 工具直接看 (sidecar 純文字無法 inline 顯圖)
        for img in d.get("images", []):
            img_count += 1
            lines.append(f"    - 🖼️ Discord 圖片附件 → **用 Read 工具看**: `{img}`")
    section = "\n".join(lines) + "\n"
    return (section, max_shown_seq, len(shown), remaining_older, img_count)


# ===========================================================
# Region presets — 幾何象限 (解析成 crop-pct 比例 x,y,w,h, 範圍 0~1)
# 物理意義: 小地圖/血條等實際座標因遊戲而異, 故預設只給幾何位置; 對不上就用 --crop-pct 自訂。
# 設計取捨 (basecamp 2026-06-06 review): 不內建遊戲特定座標 — 會隨遊戲/UI 改版 rot = identity layer 漂移。
# ===========================================================
REGION_PRESETS = {
    "top-right":     (0.75, 0.00, 0.25, 0.25),   # 小地圖常見處
    "top-left":      (0.00, 0.00, 0.25, 0.25),
    "bottom-right":  (0.75, 0.75, 0.25, 0.25),
    "bottom-left":   (0.00, 0.75, 0.25, 0.25),
    "top-strip":     (0.00, 0.00, 1.00, 0.15),   # 血條 / 資源列
    "bottom-strip":  (0.00, 0.85, 1.00, 0.15),   # 技能列
    "center":        (0.25, 0.25, 0.50, 0.50),
}


# ===========================================================
# Frame 掃描 + 排序
# ===========================================================
def list_frames_by_mtime():
    """掃 frames/frame_NNNN.jpg → [(idx, path, mtime)], 依 (mtime, idx) 升序。

    物理意義: 回傳即「時間序」, 同秒以 idx 當 stable tiebreaker (見頂部鐵律)。
    """
    if not FRAMES_DIR.exists():
        return []
    out = []
    for f in FRAMES_DIR.glob("frame_*.jpg"):
        try:
            idx = int(f.stem.split("_")[1])
        except (ValueError, IndexError):
            continue  # 非 frame_NNNN 命名 → 跳過
        try:
            mtime = f.stat().st_mtime
        except OSError:
            continue
        out.append((idx, f, mtime))
    # (mtime, idx) 排序: mtime 為主, 同秒以 idx 為 stable tiebreaker
    out.sort(key=lambda t: (t[2], t[0]))
    return out


def parse_after_mtime(s):
    # 區塊職責：把 --after-mtime 參數解析成 epoch 秒 (float)。
    # 物理意義：watching loop 每 cycle 餵「上次 cursor」進來接續; 接受 epoch 浮點 (機器最穩) 或 ISO8601 (人讀)。
    # 數值影響：回傳值直接拿去跟 frame.mtime (epoch 秒) 比大小, 故統一歸成 epoch。
    s = str(s).strip()
    try:
        return float(s)                      # 首選: epoch 秒 (loop next-cursor 原樣回餵)
    except ValueError:
        pass
    from datetime import datetime            # 退路: ISO8601 (允許結尾 Z = UTC)
    dt = datetime.fromisoformat(s.replace("Z", "+00:00"))
    return dt.timestamp()


def subsample_newest_anchored(window, args):
    # 區塊職責：對「已時間序的窗口」抽稀, 但保證最新一張一定入選。
    # 物理意義：watching loop 的 cursor 必須精準等於「真實最新 frame」才能下一輪 0-gap 接續,
    #          故抽稀從窗口尾端 (最新) 往回算 stride, 確保 window[-1] 永遠在結果裡。
    # 數值影響：--max-tiles N 設上限時自動算 stride 把格數壓到 <=N (格數恆定=圖大小恆定=讀圖成本恆定);
    #          否則沿用 --every; 都沒帶則全收 (高密度, 熱點時刻用)。
    if not window:
        return []
    max_tiles = getattr(args, "max_tiles", None)   # 格數上限 (None=不限)
    every = args.every or 1                         # 顯式抽稀步長 (預設 1=不抽)
    if max_tiles and max_tiles > 0 and len(window) > max_tiles:
        every = math.ceil(len(window) / max_tiles)  # 自動 stride: ceil(窗口幀數/上限)
    every = max(1, every)
    if every == 1:
        return window
    # 尾端對齊抽稀: 反轉→每 every 抽 1→再反轉, 故最新 (原 window[-1]) 必為結果末張
    return window[::-1][::every][::-1]


def select_frames(args, all_frames):
    """依 --frames / --last / --every 選 frame, 回時間序清單 [(idx, path, mtime)]。

    物理意義:
      - --frames "1,5,10": 顯式槽位, 取存在者 + 仍按 mtime 排序
      - 其餘: 取 mtime 最新的 last 張為窗口, 再窗口內每 every 張抽 1
    預設 (無任何選擇參數): last=60, every=5 → 12 格橫跨約 60 秒 (basecamp review 拍板)

    --since-sec N (2026-06-06 活體測試發現): daemon 重啟後 buffer 混「舊 session + 新 session」frame,
      mtime 排序正確但 --last 會橫跨關機斷層 (例: 昨晚 20:18 + 今早 11:56)。
      --since-sec 只收「最新 frame 往前 N 秒內」的, 開播即用不撈到斷層另一側的陳舊 frame。
    """
    # --since-sec: 先濾掉斷層另一側的陳舊 frame (以最新 mtime 為基準往前 N 秒)
    if args.since_sec is not None and all_frames:
        newest = all_frames[-1][2]
        cutoff = newest - args.since_sec
        all_frames = [t for t in all_frames if t[2] >= cutoff]

    if args.frames is not None:
        # 顯式槽位清單 (字串 "1,5,10")
        want = set()
        for tok in args.frames.split(","):
            tok = tok.strip()
            if not tok:
                continue
            try:
                want.add(int(tok))
            except ValueError:
                print(f"WARN: --frames 含無效 token '{tok}', 略過")
        selected = [t for t in all_frames if t[0] in want]
        return selected  # all_frames 已是 (mtime, idx) 序

    # --after-mtime: cursor-based 窗口 (watching loop 用, 保證跟上一輪首尾相接 0-gap)
    # 物理意義: 只收「上次 cursor 之後」的 frame 為窗口, 配 --max-tiles 抽稀讓格數恆定不爆圖;
    #          最新一張保證入選, op_make 回報 next-cursor 給 loop 餵下一輪。
    if getattr(args, "after_mtime", None) is not None:
        cutoff = parse_after_mtime(args.after_mtime)
        window = [t for t in all_frames if t[2] > cutoff]  # 嚴格大於: 不重收 cursor 那張
        return subsample_newest_anchored(window, args)

    # last / every 推導 (smart default 只在「啥都沒帶」時生效)
    last = args.last
    every = args.every
    if last is None and every is None:
        last, every = 60, 5            # bare make 預設: 最近一分鐘 12 格
    else:
        if last is None:
            last = 60                  # 只帶 --every → 窗口預設 60
        if every is None:
            every = 1                  # 只帶 --last → 不抽稀 (高密度)

    last = max(1, last)
    every = max(1, every)
    window = all_frames[-last:]        # mtime 最新的 last 張 (升序窗口)
    selected = window[::every]         # 窗口內每 every 張抽 1 (從最舊端起算, 均勻分布)
    return selected


# ===========================================================
# 裁切解析
# ===========================================================
def resolve_crop_box(args, img_w, img_h):
    """把 region / crop-pct / crop-px 解析成像素 box (left, top, right, bottom), clamp 邊界。

    回 (box, label_str)。box 為 None 代表整張不裁。
    優先序: crop-px > crop-pct > region > 無 (整張)。
    物理意義: crop-pct/region 解析度無關 (buffer 混解析度時首選); crop-px 固定解析度最精準。
    """
    # --crop-px: 絕對像素 "x,y,w,h"
    if args.crop_px:
        x, y, w, h = _parse4(args.crop_px, "--crop-px")
        l, t = int(x), int(y)
        r, b = int(x + w), int(y + h)
        box = _clamp_box(l, t, r, b, img_w, img_h)
        return box, f"crop-px {args.crop_px}"

    # --crop-pct: 比例 "x,y,w,h" (0~1)
    if args.crop_pct:
        fx, fy, fw, fh = _parse4(args.crop_pct, "--crop-pct")
        l = int(round(fx * img_w)); t = int(round(fy * img_h))
        r = int(round((fx + fw) * img_w)); b = int(round((fy + fh) * img_h))
        box = _clamp_box(l, t, r, b, img_w, img_h)
        return box, f"crop-pct {args.crop_pct}"

    # --region: 命名預設 → crop-pct
    if args.region:
        if args.region not in REGION_PRESETS:
            raise SystemExit(f"ERROR: 未知 region '{args.region}', 可用: {', '.join(REGION_PRESETS)} (或用 --crop-pct)")
        fx, fy, fw, fh = REGION_PRESETS[args.region]
        l = int(round(fx * img_w)); t = int(round(fy * img_h))
        r = int(round((fx + fw) * img_w)); b = int(round((fy + fh) * img_h))
        box = _clamp_box(l, t, r, b, img_w, img_h)
        return box, f"region {args.region} ({fx},{fy},{fw},{fh})"

    return None, "full frame"


def _parse4(s, flagname):
    """解析 'a,b,c,d' → (float a,b,c,d)。"""
    parts = [p.strip() for p in s.split(",")]
    if len(parts) != 4:
        raise SystemExit(f"ERROR: {flagname} 需 4 個逗號分隔值 'x,y,w,h', 收到 '{s}'")
    try:
        return tuple(float(p) for p in parts)
    except ValueError:
        raise SystemExit(f"ERROR: {flagname} 含非數值: '{s}'")


def _clamp_box(l, t, r, b, w, h):
    """clamp box 到 (0,0,w,h), 回 None 若裁完無面積。"""
    l = max(0, min(l, w)); r = max(0, min(r, w))
    t = max(0, min(t, h)); b = max(0, min(b, h))
    if r <= l or b <= t:
        return None
    return (l, t, r, b)


# ===========================================================
# 單格 tile 載入 + 裁切 + 縮放
# ===========================================================
def load_tile(path, args):
    """open → (可選)crop → resize, 回 PIL.Image; 失敗回 None (由呼叫端計 dropped)。

    數值影響: resize 用 LANCZOS (品質優先, 12 張成本可忽略)。
    """
    from PIL import Image
    try:
        img = Image.open(path)
        img.load()
        img = img.convert("RGB")
    except Exception as e:
        print(f"WARN: 讀 {path.name} 失敗 ({e}) — skip")
        return None

    box, _ = resolve_crop_box(args, img.width, img.height)
    if box is not None:
        # crop-px 對小於指定範圍的 frame 已 clamp; 此處 box 必有面積
        img = img.crop(box)
    if img.width <= 0 or img.height <= 0:
        print(f"WARN: {path.name} 裁切後無面積 — skip")
        return None

    # 降解析度: --scale 優先, 否則 --tile-width
    if args.scale is not None:
        nw = max(1, int(round(img.width * args.scale)))
        nh = max(1, int(round(img.height * args.scale)))
    else:
        tw = args.tile_width
        nw = max(1, tw)
        nh = max(1, int(round(img.height * tw / img.width)))
    if (nw, nh) != (img.width, img.height):
        img = img.resize((nw, nh), Image.LANCZOS)
    return img


def draw_tile_label(tile, seq, idx, mtime):
    """在 tile 左上角燙 '#seq fNNNN HH:MM:SS', 加半透明底方便閱讀。

    物理意義: 讓 agent 看合成圖時能指認「哪格是哪張 frame / 何時截的」。
    """
    from PIL import ImageDraw, ImageFont
    draw = ImageDraw.Draw(tile)
    hhmmss = time.strftime("%H:%M:%S", time.localtime(mtime))
    text = f"#{seq} f{idx:04d} {hhmmss}"
    # 字型大小依 tile 寬自適應 (最小 11)
    fsize = max(11, tile.width // 28)
    try:
        font = ImageFont.truetype("arial.ttf", size=fsize)
    except (OSError, IOError):
        font = ImageFont.load_default()
    # 量字框
    try:
        bbox = draw.textbbox((0, 0), text, font=font)
        tw = bbox[2] - bbox[0]; th = bbox[3] - bbox[1]
    except AttributeError:
        tw, th = draw.textsize(text, font=font)
    pad = 2
    draw.rectangle([0, 0, tw + pad * 2, th + pad * 2], fill=(0, 0, 0))
    draw.text((pad, pad), text, fill=(255, 230, 120), font=font)
    return tile


# ===========================================================
# 拼版
# ===========================================================
def parse_bg(s):
    """'#RRGGBB' → (r,g,b)。"""
    s = s.strip().lstrip("#")
    if len(s) != 6:
        raise SystemExit(f"ERROR: --bg 需 #RRGGBB, 收到 '{s}'")
    try:
        return tuple(int(s[i:i + 2], 16) for i in (0, 2, 4))
    except ValueError:
        raise SystemExit(f"ERROR: --bg 非合法 hex: '{s}'")


def compute_output_size(n_tiles, cell_w, cell_h, cols, gutter):
    """回 (cols, rows, out_w, out_h)。"""
    rows = math.ceil(n_tiles / cols)
    out_w = cols * cell_w + (cols + 1) * gutter
    out_h = rows * cell_h + (rows + 1) * gutter
    return cols, rows, out_w, out_h


def compose(tiles, cols, gutter, bg):
    """把 tiles 貼進大 canvas (每格置中於 cell), 回 (canvas, rows, cell_w, cell_h)。"""
    from PIL import Image
    cell_w = max(t.width for t in tiles)
    cell_h = max(t.height for t in tiles)
    cols, rows, out_w, out_h = compute_output_size(len(tiles), cell_w, cell_h, cols, gutter)
    canvas = Image.new("RGB", (out_w, out_h), bg)
    for i, t in enumerate(tiles):
        rr = i // cols
        cc = i % cols
        cell_x = gutter + cc * (cell_w + gutter)
        cell_y = gutter + rr * (cell_h + gutter)
        # 置中於 cell (tiles 可能因混解析度/混裁切而尺寸不一)
        off_x = cell_x + (cell_w - t.width) // 2
        off_y = cell_y + (cell_h - t.height) // 2
        canvas.paste(t, (off_x, off_y))
    return canvas, rows, cell_w, cell_h


def shrink_tiles_to_max_edge(tiles, cols, gutter, max_edge):
    """若投影輸出最長邊 > max_edge, 等比例縮小所有 tile 並回 (tiles, shrunk_bool)。

    物理意義: 防合成圖過大 (basecamp/Tim 主用例: agent 讀得進 context); 縮小是顯式且會印警告。
    """
    from PIL import Image
    cell_w = max(t.width for t in tiles)
    cell_h = max(t.height for t in tiles)
    _, _, out_w, out_h = compute_output_size(len(tiles), cell_w, cell_h, cols, gutter)
    longest = max(out_w, out_h)
    if longest <= max_edge:
        return tiles, False
    factor = max_edge / float(longest)
    new_tiles = []
    for t in tiles:
        nw = max(1, int(t.width * factor))
        nh = max(1, int(t.height * factor))
        new_tiles.append(t.resize((nw, nh), Image.LANCZOS))
    return new_tiles, True


def atomic_write_jpeg(img, path, quality):
    """tmp → os.replace, 避免 reader 讀到半寫檔 (跟家族一致)。"""
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".jpg.tmp")
    img.save(tmp, format="JPEG", quality=quality, optimize=False)
    os.replace(tmp, path)


# ===========================================================
# Ops
# ===========================================================
def op_make(args):
    all_frames = list_frames_by_mtime()
    if not all_frames:
        print(f"ERROR: 無 frame — {FRAMES_DIR} 空 (daemon 沒在跑或 enabled=false?)")
        return 1

    # T-OCR-Watermark (Tim 2026-06-10 拍板「montage 只讀到字幕已生成的部分」)
    # 區塊職責: --ocr 模式下把整個觀看窗口 clamp 到 daemon OCR watermark 以內
    # 物理意義: watermark = 「此 mtime 前字幕 cache 全就緒」→ 窗口內必全 cache 命中,
    #          多 viewer 同時拉 montage 不會各自重複跑 inline OCR (效能瓶頸根治);
    #          代價 = 畫面延遲幾秒 (Tim 拍板可接受); next-cursor 自然落在 watermark 內, 下輪 0-gap 接續。
    # 數值影響: status stale (>120s, daemon 停/pool 關) 或 --no-ocr-clamp → 不 clamp (退回 fallback inline 行為)
    ocr_clamp_note = None
    if args.ocr and getattr(args, "ocr_clamp", True):
        try:
            from subtitle_ocr import read_ocr_status
            _st = read_ocr_status(FRAMES_DIR.parent / "ocr")
        except Exception:
            _st = None
        if _st and (time.time() - float(_st.get("updated_at", 0))) < 120.0:
            wm = float(_st.get("watermark_mtime", 0) or 0)
            if wm > 0:
                clamped = [t for t in all_frames if t[2] <= wm + 1e-6]
                cut = len(all_frames) - len(clamped)
                if not clamped:
                    print("⚠ OCR watermark 還沒趕上任何 frame (daemon 字幕生成中) — "
                          "等 1-2s 再跑, 或加 --no-ocr-clamp 不等字幕")
                    return 2
                lag = all_frames[-1][2] - wm
                ocr_clamp_note = (f"窗口 clamp 至字幕水位 frame_{_st.get('last_frame_index', '?')}"
                                  f" (-{cut} 幀未生成, 畫面延遲 {max(0.0, lag):.1f}s)")
                all_frames = clamped

    selected = select_frames(args, all_frames)
    if not selected:
        print("ERROR: 選擇條件下無 frame 命中")
        return 1

    # 載入 + 裁切 + 縮放 (skip 壞檔, 計 dropped)
    tiles = []
    meta = []            # (seq, idx, mtime) 對齊 tiles
    dropped = []
    crop_label = "full frame"
    for seq, (idx, path, mtime) in enumerate(selected, start=1):
        tile = load_tile(path, args)
        if tile is None:
            dropped.append(path.name)
            continue
        # 記第一張的裁切 label (整批同一 spec)
        if not tiles:
            try:
                from PIL import Image
                with Image.open(path) as probe:
                    _, crop_label = resolve_crop_box(args, probe.width, probe.height)
            except Exception:
                pass
        if args.label:
            tile = draw_tile_label(tile, seq, idx, mtime)
        tiles.append(tile)
        meta.append((seq, idx, mtime))

    if not tiles:
        print(f"ERROR: 所有選中 frame 都載入失敗 (dropped {len(dropped)})")
        return 1

    # 版面: cols 預設 auto = ceil(sqrt(n))
    cols = args.cols if args.cols and args.cols > 0 else max(1, math.ceil(math.sqrt(len(tiles))))
    cols = min(cols, len(tiles))
    bg = parse_bg(args.bg)

    # max-edge 自動縮 (不靜默 — 縮了會印警告)
    tiles, shrunk = shrink_tiles_to_max_edge(tiles, cols, args.gutter, args.max_edge)

    canvas, rows, cell_w, cell_h = compose(tiles, cols, args.gutter, bg)

    # T-AudioLog (Tim 2026-06-08, summit ship) — 接底 audio strip
    # 物理意義: 從 daemon dump 的 _audio_log.npz 載入, slice [first_tile.mtime, last_tile.mtime]
    #          → render audio_log_strip (寬綁 canvas, 高 = --audio-strip-height) → 接在 canvas 底部
    # 數值影響: 失敗 fail-soft (印警告, 仍輸出原 canvas); 成功則 canvas 變更高
    # 跨層驗證: 即使 dump 檔還沒寫出 (剛啟動 daemon) 也走 fail-soft, 不阻塞 montage
    audio_strip_warn = None
    audio_strip_info = None
    if args.audio_strip:
        try:
            audio_log_path = STREAM_DIR / "_audio_log.npz"
            # 動態 import (audio_viz 可能未裝, 走 fail-soft)
            from screenstream_audio_viz import load_audio_log, render_audio_log_strip  # type: ignore
            log_data = load_audio_log(audio_log_path)
            if log_data is None:
                audio_strip_warn = (f"⚠ audio log 未找到或讀取失敗 ({audio_log_path.name}) — "
                                    f"audio strip 跳過 (daemon 沒啟用 audio_viz?)")
            else:
                t_first = meta[0][2]
                t_last = meta[-1][2]
                tile_times = [m[2] for m in meta]
                strip_img = render_audio_log_strip(
                    timestamps=log_data["timestamps"],
                    L_db=log_data["L_db"],
                    R_db=log_data["R_db"],
                    peak_db=log_data["peak_db"],
                    t_start=t_first,
                    t_end=t_last,
                    width=canvas.width,
                    height=max(60, int(args.audio_strip_height)),
                    tile_times=tile_times,
                )
                # 接底: 新 canvas 高 = 原 canvas + strip + 一條 gutter
                from PIL import Image
                gutter_h = max(1, args.gutter)
                new_h = canvas.height + gutter_h + strip_img.height
                new_canvas = Image.new("RGB", (canvas.width, new_h), bg)
                new_canvas.paste(canvas, (0, 0))
                # strip 是 RGBA → 直接 paste 到 RGB canvas, alpha 自動 flatten
                new_canvas.paste(strip_img.convert("RGB"),
                                 (0, canvas.height + gutter_h))
                canvas = new_canvas
                audio_strip_info = (f"{strip_img.height}px (cols={len(log_data['timestamps'])}, "
                                    f"span={t_last - t_first:.0f}s)")
        except Exception as e:
            audio_strip_warn = f"⚠ audio strip 渲染失敗 (fail-soft): {e}"

    out_path = Path(args.out) if args.out else DEFAULT_OUT
    if not out_path.is_absolute():
        out_path = REPO_ROOT / out_path
    atomic_write_jpeg(canvas, out_path, args.quality)

    # cross-layer 驗證: 讀回實際檔案大小 (不只信記憶體)
    try:
        fsize = out_path.stat().st_size
    except OSError:
        fsize = -1

    # 時間跨度
    t_first = meta[0][2]; t_last = meta[-1][2]
    span = t_last - t_first
    span_str = (f"{time.strftime('%H:%M:%S', time.localtime(t_first))} → "
                f"{time.strftime('%H:%M:%S', time.localtime(t_last))}  "
                f"({span:.0f}s, {len(tiles)} frames)")

    # 斷層偵測 (2026-06-06 活體測試): 選中 frame 相鄰 mtime 出現異常大 gap
    # = 八成跨了 daemon 關機斷層 (混到舊 session frame); 提示用 --since-sec 濾掉。
    mtimes = [m[2] for m in meta]
    max_gap = max((b - a for a, b in zip(mtimes, mtimes[1:])), default=0.0)
    gap_warn = None
    if args.since_sec is None and args.after_mtime is None and max_gap > 30.0:
        gap_warn = (f"⚠ 選中 frame 含 {max_gap:.0f}s 斷層 (八成跨了 daemon 重啟; "
                    f"混到舊 session 的陳舊畫面) — 看直播請加 --since-sec 120 只取最近的")

    # 區塊職責：watching loop 的 overflow 偵測 + next-cursor 回報。
    # 物理意義：cursor 比「buffer 最舊存活幀」還舊 = (cursor, 最舊存活) 之間的 frame 已被 ring buffer
    #          覆寫、永久救不回 — 誠實標 lost 幀數 (禁靜默截斷)。next-cursor = 本輪最新幀 mtime,
    #          loop 原樣回餵 --after-mtime 即可下一輪 0-gap 接續 (newest-anchored 抽稀保證它=真實最新)。
    # 數值影響：純報告, 不改圖; lost_sec 以 1fps 估幀數, >2.5s 才報 (留 cycle 抖動餘裕)。
    overflow_warn = None
    next_cursor = None
    if args.after_mtime is not None:
        cutoff = parse_after_mtime(args.after_mtime)
        buffer_oldest = all_frames[0][2]            # ring buffer 內最舊存活幀 (raw, 未經抽稀)
        lost_sec = buffer_oldest - cutoff
        if lost_sec > 2.5:                           # cursor 落在已被覆寫區 → 有永久遺失
            lost_n = max(1, int(round(lost_sec)) - 1)
            overflow_warn = (f"⚠ overflow: 落後過久, cursor 之後約 {lost_n} 幀已被 ring buffer 覆寫"
                             f"永久遺失 (cursor → 最舊存活幀間隔 {lost_sec:.0f}s) — 縮短 cycle 間隔")
        next_cursor = meta[-1][2]                    # 本輪最新選中幀 = 真實最新 (newest-anchored)

    # T-Subtitle-OCR (Tim 2026-06-09 拍板, --ocr-dense T 2026-06-09 23:43 升級)
    # 物理意義: 對 frame crop 字幕帶跑 RapidOCR; sidecar md 跟 _montage.jpg 同目錄, 檔名 .subtitles.md。
    #   - 預設 dense=on: OCR 窗口內所有 frame (1 fps), 編號規則: 主 tile=#1/#2 整數,
    #     中間幀=#1.1/#1.2... (mtime 順序). 對應 Tim 拍板「OCR 密度 > 畫面 tile 密度」.
    #   - --no-ocr-dense: 退回 12 tile only (舊行為), agent 不想每輪多十幾秒 OCR 開銷時用。
    # 數值影響: dense 模式每輪 OCR 跑 N 幀 (N=窗口 frame 數, 通常 100-180); 每幀 ~100-300ms;
    #   fail-soft (印警告, 不阻塞 montage 輸出); 空字幕該幀標 "(no subtitle)"。
    ocr_sidecar_path = None
    ocr_warn = None
    ocr_stats = None
    tavern_stats = None      # T-StreamWatch-TavernSync: 酒館未讀段落報告 (stdout + 推進游標用)
    tavern_max_seq = None    # 這輪實際顯示到的最大 seq, 供 session record_observation 推進已讀游標
    stt_stats = None         # T-STT: 語音轉錄段報告 (stdout)
    stt_warn = None          # T-STT: 語音轉錄 fail-soft 警告
    if args.ocr:
        try:
            from subtitle_ocr import (is_available as _ocr_avail, ocr_subtitle_band,
                                      get_init_error, read_cached_text)
            # T-OCR-Pipeline (Tim 2026-06-10 拍板「錄製時就自動產生, 多執行緒並行」) — cache-first:
            # 物理意義: daemon 端 worker pool 已在錄製當下預產 ocr/frame_NNNN.json, 這裡命中直接用;
            #          miss (daemon ocr_enabled 關 / cache stale) 才 fallback inline OCR。
            # 數值影響: cache 全命中時連 RapidOCR 3s 模型載入都省 (engine lazy init), 每輪 OCR 開銷 ~0s。
            ocr_cache_dir = FRAMES_DIR.parent / "ocr"
            ocr_cache_hits = 0
            _engine = {"checked": False, "ok": False}

            def _engine_ok() -> bool:
                """lazy engine check — 第一次需要 fallback 才 init (~3s), cache 全命中不觸發."""
                if not _engine["checked"]:
                    _engine["checked"] = True
                    _engine["ok"] = _ocr_avail()
                return _engine["ok"]

            def _ocr_text(path, mtime, is_main=False):
                """單幀字幕 cache-first: 命中回 cache 內容 (含空字串=確定無字幕); miss → 依角色分流.

                adaptive density 跳幀 stub 的語意分流 (T-OCR-AdaptiveDensity):
                - 中間幀 (#x.y): stub 當「無字幕」收下 — 跳幀損失可接受, 不重跑
                - 主 tile (#x): stub 當 miss → inline 補 OCR (封頂 12 張/輪, 保 12 格對齊準確)

                cache-only 規格 (Tim 2026-06-10「OCR 應該都讀緩存」+ basecamp spec 收斂):
                - 中間幀 cache miss (cache 完全沒這幀) → 回 None = 「cache 未供給」, 絕不 inline 現跑
                  (None 跟空字串「確定無字幕」語意區分, caller 計數誠實報告 — 根絕 540 幀 ×17min 災難)
                - 主 tile cache miss → 保留 inline fallback (封頂 12 張/輪 ≈ 3-4s, 對齊價值 > 成本)
                """
                nonlocal ocr_cache_hits
                cached = read_cached_text(ocr_cache_dir, path, mtime,
                                          y_pct=args.ocr_y_pct, h_pct=args.ocr_h_pct,
                                          treat_skipped_as_miss=is_main)
                if cached is not None:
                    ocr_cache_hits += 1
                    return cached
                if not is_main:
                    return None            # 中間幀 cache-only: 未供給 → 不現跑, caller 記 ocr_uncached
                if not _engine_ok():
                    return ""
                return ocr_subtitle_band(path, y_pct=args.ocr_y_pct, h_pct=args.ocr_h_pct,
                                         min_confidence=args.ocr_min_conf)

            if not ocr_cache_dir.exists() and not _engine_ok():
                ocr_warn = f"⚠ OCR 不可用 (無 daemon cache + engine fail-soft): {get_init_error()}"
            else:
                # ===== Step 1: 決定 OCR 範圍 =====
                # 區塊職責: dense=on → 取 selected[0..-1] mtime 區間內所有 all_frames;
                #          dense=off → 退回只 OCR selected (=meta 12 tile).
                # 物理意義: dense 模式拿窗口全部 frame (1 fps 寫入, 約 100-180 frame), 跟 selected mtime 集合對齊;
                #          mtime 在 selected 中 → 主 tile #k 整數; 不在 → 內插 #k.M (M 從 .1 累加).
                selected_mtimes = {round(m[2], 3) for m in selected}  # (idx, path, mtime) 第 2 欄是 mtime
                selected_idx_to_seq = {(s_idx, round(s_mtime, 3)): seq for seq, (s_idx, _, s_mtime) in enumerate(selected, start=1)}

                if getattr(args, "ocr_dense", True):
                    # 取 selected 時間區間內的所有 all_frames (含主 tile 自己 + 中間幀)
                    if selected:
                        t_lo = selected[0][2]
                        t_hi = selected[-1][2]
                        ocr_frames = [t for t in all_frames if t_lo <= t[2] <= t_hi]
                    else:
                        ocr_frames = []
                else:
                    ocr_frames = list(selected)

                # ===== Step 1.5: 落後自動降密 (Tim 2026-06-10 提案) =====
                # 區塊職責: 窗口落後太多 (inline OCR 幀數爆量) 時, 自動調降中間幀 OCR 密度,
                #          避免單輪 montage 被現跑 OCR 拖到數分鐘 (cache 斷供 + 9min 窗口 = 500+ 幀 inline)。
                # 物理意義: 字幕在畫面上通常停留 >=1.5s (1fps 下 >=2 幀), 中間幀隔 stride 取樣
                #          仍能抓到絕大多數字幕行; 主 tile 永遠保留 (縮圖牆 12 格對齊),
                #          daemon cache 命中的幀 0 推理開銷也永遠保留 — 降密只犧牲「要現跑 inline 的 cache-miss 中間幀」。
                # 數值影響: --ocr-max-inline N (預設 120, 約 ~150-300ms/幀 → 上限 ~18-36s/輪);
                #          「會 inline 的 cache-miss 幀數」> N → stride=ceil(miss/N) 均勻取樣;
                #          降密結果必報告在 stats + sidecar header (禁靜默截斷鐵律), 不假裝全看過。
                # ⚠ cache-only 規格收斂後 (Tim + basecamp 2026-06-10) 中間幀 cache-miss 已不 inline,
                #   inline 成本只剩主 tile (<=12 << 120) → 本保險絲常態休眠, 留著防未來 spec 回滾 / max-tiles 加大。
                ocr_degraded_skip = 0    # 被降密跳過的 cache-miss 中間幀數
                ocr_degrade_stride = 1   # 取樣步距 (1 = 未降密)
                max_inline = max(0, int(getattr(args, "ocr_max_inline", 120)))
                if getattr(args, "ocr_dense", True) and max_inline and len(ocr_frames) > max_inline:
                    # 先探 cache (read_cached_text 純檔案查找, 無推理開銷): 命中幀免費, 不列入 inline 成本
                    probe_hit = [read_cached_text(ocr_cache_dir, t[1], t[2],
                                                  y_pct=args.ocr_y_pct, h_pct=args.ocr_h_pct) is not None
                                 for t in ocr_frames]
                    # 只計「真的會 inline」的幀 = cache-miss 的主 tile (中間幀 miss 走 cache-only 不現跑, 不算成本)
                    inline_count = sum(1 for k, h in enumerate(probe_hit)
                                       if not h and round(ocr_frames[k][2], 3) in selected_mtimes)
                    if inline_count > max_inline:
                        # math 走模組頂層 import — 函式內重 import 會把 math 變 local, 炸掉前段 line ~449 的 math.ceil
                        ocr_degrade_stride = math.ceil(inline_count / max_inline)
                        keep = []      # 降密後保留的 frame 清單
                        miss_i = 0     # 第幾個 cache-miss 中間幀 (取樣計數)
                        for k, t in enumerate(ocr_frames):
                            # 主 tile / cache 命中 → 永遠保留 (前者保對齊, 後者 0 開銷)
                            if round(t[2], 3) in selected_mtimes or probe_hit[k]:
                                keep.append(t)
                            elif miss_i % ocr_degrade_stride == 0:
                                keep.append(t)
                                miss_i += 1
                            else:
                                ocr_degraded_skip += 1
                                miss_i += 1
                        ocr_frames = keep

                # ===== Step 2: 編號 + OCR =====
                ocr_lines_out = []
                ocr_hits = 0
                ocr_hidden = 0          # 被隱藏的空字幕中間幀數 (Tim 2026-06-10 拍板: 補間無字幕 → 不輸出)
                ocr_uncached = 0        # cache 未供給的中間幀數 (cache-only 規格: 不現跑, 誠實計數)
                ocr_deduped = 0         # 與前一筆完全相同被摺疊的補間幀數 (Tim 2026-07-24 精確去重; 禁靜默截斷 → 計數報告)
                last_main_seq = 0       # 上一個主 tile 序號
                sub_counter = 0         # 同主 tile 下的中間幀計數 (.1, .2, ...)
                # 去重狀態 (Tim 2026-07-24): prev_norm = 上一筆已輸出字幕 (strip 正規化); prev_seen_mtime = 該句最近一次出現的 mtime
                # 物理意義: 補間幀 text 與 prev_norm 相同且距 prev_seen_mtime <= gap → 摺疊; 主 tile 不參與摺疊但會更新 prev_norm (當錨點)
                prev_norm = None
                prev_seen_mtime = 0.0

                for (idx, path, mtime) in ocr_frames:
                    mt_key = round(mtime, 3)
                    is_main = mt_key in selected_mtimes
                    if is_main:
                        # 取得對應 seq (整數)
                        seq = selected_idx_to_seq.get((idx, mt_key))
                        if seq is None:
                            # 防呆: 若 round 後 collision 失敗 → 按 mtime 順序找最近的
                            for (s_idx, _, s_mtime), s in zip(selected, range(1, len(selected) + 1)):
                                if abs(s_mtime - mtime) < 0.01 and s_idx == idx:
                                    seq = s
                                    break
                        if seq is None:
                            seq = last_main_seq + 1   # 最終保險
                        last_main_seq = seq
                        sub_counter = 0
                        label = f"#{seq}"
                    else:
                        if last_main_seq == 0:
                            # 在第一個主 tile 之前的中間幀 (理論不該有, 因為 t_lo 就是 selected[0])
                            continue
                        sub_counter += 1
                        label = f"#{last_main_seq}.{sub_counter}"

                    text = _ocr_text(path, mtime, is_main=is_main)
                    hhmmss = time.strftime("%H:%M:%S", time.localtime(mtime))
                    if text is None:
                        # cache-only 規格: 中間幀 cache 未供給 → 不現跑不輸出, 只誠實計數
                        # (語意跟「確定無字幕」區分 — 這幀沒人看過, 不是沒字幕; 編號 sub_counter 已遞增保連續)
                        ocr_uncached += 1
                    elif text:
                        # 精確去重 (Tim 2026-07-24): 補間幀 (#N.M) 字幕與前一筆已輸出完全相同 → 摺疊不輸出。
                        # 邊界: (1) 只作用補間幀 — 主 tile (#N) 一律全文照印, 重複也不摺疊 (Tim: 保持原樣, 避免「同上」誤判);
                        #       (2) 只精確比對 (strip 正規化後字串相等), 不做模糊 (Tim 明示恐影響判讀);
                        #       (3) 3s 斷鏈 (kaguya 實測) — 距同句上次出現 > gap 秒視為真重播, 不摺疊 (防吃掉原句重播如「お兄ちゃん」)。
                        norm = text.strip()
                        is_dup = (
                            getattr(args, "ocr_dedupe", True)
                            and not is_main
                            and prev_norm is not None
                            and norm == prev_norm
                            and (mtime - prev_seen_mtime) <= getattr(args, "ocr_dedupe_gap", 3.0)
                        )
                        if is_dup:
                            # 摺疊: 不 append, 誠實計數; 延續該句 run (更新 mtime, 讓連續持顯的字幕整段收乾淨)
                            ocr_deduped += 1
                            prev_seen_mtime = mtime
                        else:
                            ocr_hits += 1
                            first_line_compact = text.replace("\n", " / ")
                            # 主 tile 用粗體, 中間幀普通字
                            prefix = f"- **{label}**" if is_main else f"- {label}"
                            ocr_lines_out.append(f"{prefix} f{idx:04d} {hhmmss}: {first_line_compact}")
                            # 更新去重錨點 (主 tile 與補間幀輸出後都更新 — 主 tile 當錨點, 下個相同補間幀才摺疊得掉)
                            prev_norm = norm
                            prev_seen_mtime = mtime
                    else:
                        # 空字幕處理 (Tim 2026-06-10 拍板): 中間幀 (#x.y) 無字幕 → 整行隱藏不輸出
                        # (sub_counter 已遞增, 編號保持連續 — e.g. #2.3~#2.9 全空時直接跳到下一個有字幕的 #2.10);
                        # 主 tile (#x 整數) 保留 "(no subtitle)" 行, 維持跟縮圖牆 12 格的對齊性。
                        if is_main:
                            ocr_lines_out.append(f"- {label} f{idx:04d} {hhmmss}: _(no subtitle)_")
                        else:
                            ocr_hidden += 1

                # ===== Step 3: 寫 sidecar md =====
                ocr_sidecar_path = out_path.with_suffix(".subtitles.md")
                mode_label = "dense (所有 frame OCR, 整數=tile, 小數=中間幀)" if getattr(args, "ocr_dense", True) else "tile-only (舊行為, 只 OCR montage 12 tile)"
                header = [
                    f"# Montage Subtitles — {out_path.name}",
                    "",
                    f"_OCR engine_: RapidOCR (rapidocr-onnxruntime, Paddle ch_PP-OCRv4 ONNX)",
                    f"_Mode_: {mode_label}",
                    f"_Region_: y={args.ocr_y_pct} h={args.ocr_h_pct} (字幕帶比例, 0~1)",
                    f"_Min confidence_: {args.ocr_min_conf}",
                    f"_Frames OCR'd_: {len(ocr_frames)} (hits: {ocr_hits}, 空字幕中間幀已隱藏: {ocr_hidden}, 重複字幕已摺疊: {ocr_deduped}, daemon cache 命中: {ocr_cache_hits}) / _Tiles_: {len(meta)}",
                    # cache-only 誠實標註: 未供給 > 0 = 這輪中間幀字幕不完整 (daemon OCR off / cache 落後), 不是沒字幕
                    *([f"_⚠ cache 未供給_: {ocr_uncached} 個中間幀無 OCR 結果 (daemon cache 斷供/落後, cache-only 規格不現跑)"]
                      if ocr_uncached else []),
                    # 降密報告 (禁靜默截斷): 有跳過幀才輸出, 讓 agent 知道這輪不是逐幀全看
                    *([f"_落後降密_: stride={ocr_degrade_stride}, 取樣跳過 {ocr_degraded_skip} 個 cache-miss 中間幀 (--ocr-max-inline {max_inline})"]
                      if ocr_degraded_skip else []),
                    "",
                    "## Per-frame",
                    "",
                ]
                content = "\n".join(header + ocr_lines_out) + "\n"
                ocr_sidecar_path.write_text(content, encoding="utf-8")
                # 降密發生時附 stride 資訊, 沒降密保持原樣 (報告層跟 sidecar header 同步, 禁靜默截斷)
                degrade_note = (f", 降密 stride={ocr_degrade_stride} 跳過 {ocr_degraded_skip} 幀"
                                if ocr_degraded_skip else "")
                # cache-only 未供給數同步到 stdout 報告 (跟 sidecar header 一致, 禁靜默截斷)
                uncached_note = f", ⚠ {ocr_uncached} 中間幀 cache 未供給" if ocr_uncached else ""
                ocr_stats = (f"{ocr_hits}/{len(ocr_frames)} hits, cache {ocr_cache_hits}/{len(ocr_frames)}, "
                             f"{ocr_hidden} 空中間幀隱藏{degrade_note}{uncached_note} ({len(meta)} tiles) → {ocr_sidecar_path.name}")
        except Exception as e:
            ocr_warn = f"⚠ OCR sidecar 渲染失敗 (fail-soft): {e}"

    # ===========================================================
    # T-StreamWatch-TavernSync (Tim 2026-06-14 拍板, kiara 實作)
    # 區塊職責: --ocr 時把聊天酒館「未讀 (排除自己) 訊息」接在字幕 sidecar 下方。
    # 物理意義: 觀影 agent 一次 Read sidecar 同時掌握「畫面字幕 + 同事對話」, 不必第二次 I/O。
    #          綁 --ocr 自動開 (Tim 拍板); --no-tavern 可關。OCR engine 掛掉導致 sidecar 沒寫時,
    #          仍補寫一份只含酒館段的 sidecar (fail-soft, 觀影仍看得到同事 @)。
    # 數值影響: 純 local 讀 _last_view.md, 不碰 Editor daemon; max_shown_seq 印給 session 推進已讀游標。
    # ===========================================================
    if args.ocr and not getattr(args, "no_tavern", False):
        try:
            since_seq = int(getattr(args, "tavern_since_seq", -1))
            section, max_shown, shown_n, older, img_n = render_tavern_tail(
                getattr(args, "tavern_self", "") or "",
                since_seq,
                int(getattr(args, "tavern_limit", 25)))
            tavern_max_seq = max_shown
            if section:
                if ocr_sidecar_path is not None and ocr_sidecar_path.exists():
                    # 既有字幕 sidecar → 接在末尾 (Tim「串接在字幕讀取資訊下方」)
                    with ocr_sidecar_path.open("a", encoding="utf-8") as fh:
                        fh.write("\n" + section)
                else:
                    # OCR engine 掛掉 / 沒 sidecar → 補寫只含酒館段的 sidecar (fail-soft)
                    ocr_sidecar_path = out_path.with_suffix(".subtitles.md")
                    fallback_head = (f"# Montage Subtitles — {out_path.name}\n\n"
                                     f"_⚠ OCR 段缺席 (engine 不可用), 以下僅聊天酒館未讀_\n\n")
                    ocr_sidecar_path.write_text(fallback_head + section, encoding="utf-8")
                older_note = f", 另有 {older} 筆更舊未讀" if older else ""
                img_note = f", 含 {img_n} 張 Discord 圖片附件 (sidecar 列本地路徑供 Read)" if img_n else ""
                tavern_stats = f"{shown_n} 筆未讀 (排除自己){older_note}{img_note}, max_seq={max_shown} → 接入 sidecar"
            else:
                # 沒未讀 (或全是自己發的) — 仍把游標推到 max_shown (避免下輪重掃)
                tavern_stats = f"0 筆未讀 (max_seq={max_shown})"
        except Exception as e:
            tavern_stats = f"⚠ 酒館段渲染失敗 (fail-soft): {e}"

    # ===========================================================
    # T-STT (Quest stt-whisper-integration, kotoko 2026-07-05)
    # 區塊職責: --stt 時即時擷取最近 N 秒系統音訊 → whisper 轉錄 → 在 sidecar 末尾補「🎙 語音轉錄」段。
    # 物理意義: OCR 讀畫面翻譯字幕(中), STT 補原始語音(英); 兩段並置給 agent 逐句雙語對照。
    # 數值影響: 阻塞擷取 N 秒 wall-clock + GPU 轉錄 <1s(短片段); fail-soft — 依賴缺/擷取失敗只補警告不炸。
    # ===========================================================
    # T-STT-AutoAttach (Tim 2026-07-10 拍板「不必帶 --stt, 啟動 STT 就自動打包進字幕流」):
    #   觸發條件對齊酒館 (ride 在 --ocr 上) —— 顯式 --stt 或 daemon config stt_enabled 任一為真即附掛。
    #   顯式 --stt: model/lang 用 CLI 值 (觀影 agent 意圖優先)。
    #   純 config 自動觸發: model/lang 用 daemon config 值 (誠實對齊 daemon 實際轉錄設定),
    #     且維持 cache-only (不強制現抓) —— 貴的 --stt-live 現抓仍須顯式 opt-in, 不因自動觸發而變重。
    stt_explicit = bool(getattr(args, "stt", False))
    cfg_stt_enabled, cfg_stt_model, cfg_stt_lang = read_daemon_stt_config()
    stt_on = stt_explicit or cfg_stt_enabled
    if stt_on:
        # 決定 effective model/lang: 顯式帶 --stt → CLI 值; 純 config 觸發 → daemon config 值。
        stt_model_eff = getattr(args, "stt_model", "small") if stt_explicit else (cfg_stt_model or "small")
        stt_lang_eff = getattr(args, "stt_lang", None) if stt_explicit else (cfg_stt_lang or None)
        stt_auto = (not stt_explicit) and cfg_stt_enabled  # 供 stdout 標「(config auto)」
        try:
            sys.path.insert(0, str(Path(__file__).resolve().parent))
            import audio_transcribe as _stt  # type: ignore
            # cache-only (Tim 2026-07-05 拍板「只讀緩存, 沒緩存的音訊直接無視靠 OCR」):
            #   讀 daemon STT worker 預產的 cache, 窗口 = [after-mtime, 最新幀 mtime (next_cursor)]。
            #   montage 端不現跑轉錄 → 多 viewer 同拉不重複運算 (對齊 OCR cache-first 鐵律)。
            after_ep = float(args.after_mtime)
            until_ep = float(next_cursor) if next_cursor is not None else time.time()
            # T-STT-Live (2026-07-09, summit ship, 討論收斂 basecamp/apex-one/gura):
            #   容器場 daemon worker 起不來 (MSIX 隔離看不到 whisper) → cache 恆空。
            #   --stt-live: 若 cache 沒蓋到窗口, montage 端 (在 agent shell, 看得到 whisper) 同步現抓
            #   一段 loopback 音訊 → 轉錄 → write_stt_chunk 寫成標準 cache → 下面照舊 read_stt_cache 讀到。
            #   誠實守則 (gura 磚1): 覆蓋% 從「實測 epoch span」算, 不用「請求秒數」(WASAPI underrun 會虛報)。
            #   同步 inline (非 agent 背景) → 避開 teardown 亡靈坑 (gura 磚2); 一次抓非全覆蓋 → sidecar 標取樣%。
            stt_live_cov = None  # (measured_sec, window_sec) — 供 sidecar/stats 標覆蓋%
            if getattr(args, "stt_live", False):
                _pre_segs, _pre_info = _stt.read_stt_cache(after_ep, until_ep)
                if not _pre_info.get("cache_present"):
                    want = float(getattr(args, "stt_seconds", 20.0) or 20.0)
                    want = max(1.0, min(want, 30.0))  # 對齊 capture cap
                    audio = _stt.capture_live(want)
                    t1 = time.time()
                    measured = audio.size / _stt.WHISPER_SAMPLE_RATE  # gura 磚1: 實測秒數
                    if measured >= 0.5:
                        _seg = _stt.transcribe(audio, language=stt_lang_eff,
                                               model_size=stt_model_eff)
                        # 真實 epoch: end=擷取返回時刻 t1, start=t1-實測秒數 (不用 want, 誠實對齊)
                        _stt.write_stt_chunk(_stt.STT_CACHE_DIR, t1 - measured, t1, _seg,
                                             stt_model_eff)
                        stt_live_cov = (measured, max(1e-6, until_ep - after_ep))
            segs, info = _stt.read_stt_cache(after_ep, until_ep)
            section = _stt.build_stt_section_cached(
                segs, info, model_size=stt_model_eff)
            # 取樣覆蓋率誠實註記 (gura 磚1: measure real) — live 現抓非全窗口, 標實測 epoch 覆蓋%
            if stt_live_cov is not None:
                _cov_pct = min(100, round(100 * stt_live_cov[0] / stt_live_cov[1]))
                section += (f"\n_⚠ live 取樣: 實測 {stt_live_cov[0]:.1f}s 音訊 / 窗口 {stt_live_cov[1]:.0f}s "
                            f"≈ 覆蓋 {_cov_pct}% (現抓窗口尾段, 非全覆蓋; 下輪起累積); "
                            f"100% 全覆蓋走容器外常駐 recorder — 見 STT.md_\n")
            # 接到既有 sidecar 末尾 (OCR/tavern 之後); 沒 sidecar 就補一份 STT-only
            if ocr_sidecar_path is not None and ocr_sidecar_path.exists():
                with ocr_sidecar_path.open("a", encoding="utf-8") as fh:
                    fh.write("\n" + section)
            else:
                ocr_sidecar_path = out_path.with_suffix(".subtitles.md")
                head = (f"# Montage Subtitles — {out_path.name}\n\n"
                        f"_⚠ 僅語音轉錄 (無 OCR/酒館段)_\n\n")
                ocr_sidecar_path.write_text(head + section, encoding="utf-8")
            _auto_tag = " [config auto]" if stt_auto else ""  # 自動觸發 (非顯式 --stt) 標明來源
            if not info.get("cache_present"):
                _hint = "靠 OCR" if not getattr(args, "stt_live", False) else "live 現抓亦無音訊/靜音"
                stt_stats = f"無 cache (daemon worker 未開/未覆蓋){_auto_tag} — {_hint}"
            else:
                cov = "" if info.get("covered") else " ⚠落後"
                src = "live 現抓寫入" if stt_live_cov is not None else "cache-only"
                covpct = f", 取樣~{min(100, round(100*stt_live_cov[0]/stt_live_cov[1]))}%" if stt_live_cov else ""
                stt_stats = f"{len(segs)} 段 ({src}{_auto_tag}, 命中 {info.get('chunks_hit',0)} chunk{cov}{covpct}) → 接入 sidecar"
        except Exception as e:
            stt_warn = f"⚠ STT 段渲染失敗 (fail-soft): {e}"

    # 輸出報告
    try:
        rel = out_path.relative_to(REPO_ROOT)
    except ValueError:
        rel = out_path
    print(f"OK montage written: {rel}")
    print(f"  grid        : {cols} cols x {rows} rows  ({len(tiles)} tiles)")
    print(f"  region      : {crop_label}")
    print(f"  cell        : {cell_w}x{cell_h} px"
          + ("  [shrunk to fit --max-edge]" if shrunk else ""))
    print(f"  output      : {canvas.width}x{canvas.height} px, "
          + (f"{fsize // 1024} KB" if fsize >= 0 else "size?"))
    print(f"  time span   : {span_str}")
    if gap_warn:
        print(f"  {gap_warn}")
    if overflow_warn:
        print(f"  {overflow_warn}")
    if audio_strip_info:
        print(f"  audio strip : {audio_strip_info}")
    if audio_strip_warn:
        print(f"  {audio_strip_warn}")
    if next_cursor is not None:
        print(f"  next-cursor : {next_cursor:.3f}  (epoch; 原樣回餵下一輪 --after-mtime 即 0-gap 接續)")
    if ocr_clamp_note:
        print(f"  ocr clamp   : {ocr_clamp_note}")
    if ocr_stats:
        try:
            ocr_rel = ocr_sidecar_path.relative_to(REPO_ROOT)
        except (ValueError, AttributeError):
            ocr_rel = ocr_sidecar_path
        print(f"  ocr sidecar : {ocr_stats} → Read {ocr_rel}")
    if ocr_warn:
        print(f"  {ocr_warn}")
    if tavern_stats:
        print(f"  tavern tail : {tavern_stats}")
    if tavern_max_seq is not None:
        # session record_observation --tavern-seq <M> 拿這值推進已讀游標 (對齊 next-cursor 鐵律, 保 0-gap)
        print(f"  tavern_max_seq={tavern_max_seq}")
    if stt_stats:
        print(f"  stt         : {stt_stats}")
    if stt_warn:
        print(f"  {stt_warn}")
    if dropped:
        print(f"  dropped     : {len(dropped)} frame(s) unreadable — {', '.join(dropped[:5])}"
              + (" ..." if len(dropped) > 5 else ""))
    print(f"  → agent: Read {rel} 看合成圖")
    return 0


def op_list_regions(args):
    print("=== Region presets (--region NAME) ===")
    print("(crop-pct = x,y,w,h 比例 0~1; 小地圖/血條實際座標因遊戲而異, 對不上改用 --crop-pct)")
    for name, (x, y, w, h) in REGION_PRESETS.items():
        print(f"  {name:14s} → crop-pct {x},{y},{w},{h}")
    return 0


# ===========================================================
# CLI
# ===========================================================
def main():
    p = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="op", required=True)

    pm = sub.add_parser("make", help="合成多 frame 成一張圖")
    # frame 選擇
    pm.add_argument("--last", type=int, default=None, help="取 mtime 最新的 N 張 (預設窗口 60)")
    pm.add_argument("--every", type=int, default=None, help="窗口內每 K 張抽 1 (time-lapse)")
    pm.add_argument("--frames", default=None, help="顯式槽位清單, 如 '1,5,10' (仍按 mtime 排序)")
    pm.add_argument("--since-sec", type=float, default=None,
                    help="只收最新 frame 往前 N 秒內的 (濾掉 daemon 重啟前的陳舊 frame; 看直播建議用)")
    pm.add_argument("--after-mtime", default=None,
                    help="watching loop 用: 只收此 cursor 之後的 frame (epoch 秒 或 ISO8601); "
                         "配 --max-tiles 保證每 cycle 首尾接續 0-gap, 並回報 next-cursor")
    pm.add_argument("--max-tiles", type=int, default=None,
                    help="格數上限 (配 --after-mtime: 窗口超過則自動抽稀壓到上限內, 圖大小恆定)")
    # 裁切 (互斥)
    pm.add_argument("--region", default=None, help="命名預設, 見 list-regions")
    pm.add_argument("--crop-pct", default=None, help="比例裁切 'x,y,w,h' (0~1, 解析度無關)")
    pm.add_argument("--crop-px", default=None, help="像素裁切 'x,y,w,h' (絕對, 自動 clamp)")
    # 降解析度 (互斥)
    pm.add_argument("--tile-width", type=int, default=480, help="每格縮到寬 N, 等比例 (預設 480)")
    pm.add_argument("--scale", type=float, default=None, help="改用倍率縮放 (如 0.5; 與 tile-width 互斥)")
    # 版面 / 輸出
    pm.add_argument("--cols", type=int, default=None, help="欄數 (預設 auto = ceil(sqrt(n)))")
    pm.add_argument("--gutter", type=int, default=2, help="格間隔像素 (預設 2)")
    pm.add_argument("--bg", default="#101010", help="底色 #RRGGBB (預設 #101010)")
    pm.add_argument("--label", dest="label", action="store_true", default=True,
                    help="每格燙序號/frame/時間 (預設開)")
    pm.add_argument("--no-label", dest="label", action="store_false", help="關閉標籤")
    pm.add_argument("--out", default=None, help="輸出路徑 (預設 _screenstream/_montage.jpg)")
    pm.add_argument("--quality", type=int, default=80, help="輸出 JPEG 品質 (預設 80)")
    pm.add_argument("--max-edge", type=int, default=4096, help="輸出最長邊上限, 超過自動縮 (預設 4096)")
    # T-AudioLog (Tim 2026-06-08, summit ship) — 在 montage 下方接 audio strip
    # 物理意義: daemon 寫 _audio_log.npz, montage 讀取後切 [first_tile.mtime, last_tile.mtime] 區段渲染
    # 數值影響: 寬綁 canvas 寬, 高自由 (預設 200px); 加在 canvas 下方
    pm.add_argument("--audio-strip", dest="audio_strip", action="store_true", default=True,
                    help="montage 下方接整段時段的 audio spectrogram strip (預設開)")
    pm.add_argument("--no-audio-strip", dest="audio_strip", action="store_false",
                    help="關閉 audio strip 接底")
    # T-Subtitle-OCR (Tim 2026-06-09 拍板) — 字幕辨識率痛點補強
    # 物理意義: 縮圖牆字幕已壓糊 (480x270 格), 走回 ring buffer 原始 frame (1080p) crop 字幕帶跑 RapidOCR;
    #          按 tile 編號對齊輸出 sidecar md, agent 讀完縮圖牆順手 Read sidecar 就掌握每格字幕。
    # 數值影響: --ocr 預設 False (不影響既有 caller); 開啟後每 12-frame 多 ~2-4s 開銷 (CPU OCR);
    #          ocr-y-pct/h-pct 控制字幕帶位置 (16:10 螢幕看 16:9 影片字幕偏上, 可調)
    pm.add_argument("--ocr-dense", dest="ocr_dense", action="store_true", default=True,
                    help="(--ocr 開啟時, 預設 ON) 對窗口內所有 frame (1 fps) 跑 OCR; 編號 #1=主 tile, #1.1/.2 中間幀")
    pm.add_argument("--no-ocr-dense", dest="ocr_dense", action="store_false",
                    help="關閉 dense, 只 OCR montage 12 tile (舊行為, 省 ~10-20s/輪)")
    pm.add_argument("--ocr", action="store_true", default=False,
                    help="(T-Subtitle-OCR) 同步跑字幕 OCR 並輸出 sidecar md 對齊 tile 編號 (需 rapidocr-onnxruntime)")
    pm.add_argument("--no-ocr-clamp", dest="ocr_clamp", action="store_false", default=True,
                    help="(--ocr 開啟時, clamp 預設 ON) 關閉「窗口 clamp 至 daemon 字幕水位」— "
                         "不等字幕生成直接看最新畫面 (cache miss 會 fallback inline OCR)")
    pm.add_argument("--ocr-max-inline", dest="ocr_max_inline", type=int, default=120,
                    help="(--ocr dense 時) 落後自動降密: cache-miss 中間幀數超過 N 時按 stride 均勻取樣 "
                         "(主 tile 與 cache 命中幀永遠保留; 0=關閉降密; 預設 120 ≈ 單輪 inline 上限 ~18-36s)")
    pm.add_argument("--ocr-y-pct", type=float, default=0.78,
                    help="(--ocr 開啟時) 字幕帶上邊位置比例 0~1 (預設 0.78, 2026-06-10 Tim 1080p 實測校準)")
    pm.add_argument("--ocr-h-pct", type=float, default=0.12,
                    help="(--ocr 開啟時) 字幕帶高度比例 (預設 0.12)")
    pm.add_argument("--ocr-min-conf", type=float, default=0.5,
                    help="(--ocr 開啟時) OCR 信度過濾門檻 (預設 0.5)")
    # T-Subtitle-Dedupe (Tim 2026-07-24 拍板) — 補間幀字幕跟前一筆「完全相同」→ 摺疊不重複輸出。
    #   只做精確去重 (不做模糊, Tim 明示恐影響判讀); 只作用補間幀 (#N.M), 主 tile (#N) 一律全文照印
    #   (Tim: 主 tile 保持原樣, 重複就重複, 不加「同上」標記 — 避免字幕真的出現「同上」時誤判)。
    #   3s 斷鏈 (kaguya 實測建議): 跨隱藏空幀橋接時, 距上次同句 >gap 秒 → 視為真重播不摺疊。
    pm.add_argument("--ocr-dedupe", dest="ocr_dedupe", action="store_true", default=True,
                    help="(--ocr 開啟時, 預設 ON) 補間幀字幕與前一筆完全相同 → 摺疊不輸出 (精確去重)")
    pm.add_argument("--no-ocr-dedupe", dest="ocr_dedupe", action="store_false",
                    help="關閉字幕去重, 補間幀逐幀全印 (舊行為)")
    pm.add_argument("--ocr-dedupe-gap", dest="ocr_dedupe_gap", type=float, default=3.0,
                    help="(去重開啟時) 同句摺疊的最大時間間隔秒數 (預設 3.0); 超過視為真重播不摺疊, 防吃掉原句重播")
    # T-StreamWatch-TavernSync (Tim 2026-06-14 拍板) — 字幕 sidecar 末尾接聊天酒館未讀訊息
    pm.add_argument("--no-tavern", dest="no_tavern", action="store_true", default=False,
                    help="(--ocr 開啟時, 酒館段預設 ON) 關閉「字幕 sidecar 末尾接酒館未讀訊息」")
    pm.add_argument("--tavern-self", dest="tavern_self", default="",
                    help="(--ocr 開啟時) 排除自己發的訊息 — 傳自己 persona (match sender '@<persona>' 後綴)")
    pm.add_argument("--tavern-since-seq", dest="tavern_since_seq", type=int, default=-1,
                    help="(--ocr 開啟時) 已讀游標, 只收 seq > N 的未讀 (-1=全收); stream-watch session 自動帶入")
    pm.add_argument("--tavern-limit", dest="tavern_limit", type=int, default=25,
                    help="(--ocr 開啟時) 單輪最多顯示幾筆未讀 (截斷取最舊, 游標推到所顯示最大 seq 保 0-gap; 預設 25)")
    pm.add_argument("--audio-strip-height", type=int, default=280,
                    help="audio strip 高度 px (預設 280, summit polish round)")
    # T-STT (Quest stt-whisper-integration, kotoko 2026-07-05): openai-whisper 語音轉錄接底
    # 物理意義: --stt 時即時擷取最近 N 秒系統音訊 → whisper 轉錄 → 在字幕 sidecar 末尾補「語音轉錄」段
    #          (格式對齊 OCR)。近即時觀看用 (cursor≈now); 精確歷史對齊留 v2 (見 audio_transcribe.py 設計決策)。
    pm.add_argument("--stt", action="store_true", default=False,
                    help="開啟語音轉錄: whisper → sidecar 補「🎙 語音轉錄」段。"
                         "★T-STT-AutoAttach: 不帶此旗標時, 若 daemon _config.json 的 stt_enabled=true 也會"
                         "自動附掛 (cache-only, 對齊酒館 ride 在 --ocr; model/lang 沿用 config); "
                         "顯式帶 --stt 則用 CLI 的 --stt-model/--stt-lang")
    pm.add_argument("--stt-model", dest="stt_model", default="small",
                    help="whisper 模型 tiny/base/small/medium/large-v3 (預設 small; env STT_WHISPER_MODEL 亦可)")
    pm.add_argument("--stt-lang", dest="stt_lang", default=None,
                    help="語音語言 en/zh/None(自動偵測); 直播原文多為 en, 指定可加速穩定")
    pm.add_argument("--stt-seconds", dest="stt_seconds", type=float, default=20.0,
                    help="即時擷取最近幾秒音訊轉錄 (預設 20; 上限 30, 阻塞抓滿才回)")
    # T-STT-Live (2026-07-09 summit): cache 沒蓋到窗口時, montage 端同步現抓一段寫進 cache 再讀
    # (容器場 daemon worker 起不來的 fallback; agent shell 看得到 whisper)。誠實標實測覆蓋%, 非全覆蓋。
    pm.add_argument("--stt-live", dest="stt_live", action="store_true", default=False,
                    help="(需 --stt) cache 空時同步現抓音訊寫 cache 再讀 — 容器場 fallback; 覆蓋% 從實測 epoch 算, 非全覆蓋")
    pm.set_defaults(func=op_make)

    pr = sub.add_parser("list-regions", help="列出 region preset")
    pr.set_defaults(func=op_list_regions)

    args = p.parse_args()
    # 互斥檢查 (友善報錯, 不靠 argparse group 以保留個別預設值)
    if args.op == "make":
        crop_set = [x for x in (args.region, args.crop_pct, args.crop_px) if x]
        if len(crop_set) > 1:
            raise SystemExit("ERROR: --region / --crop-pct / --crop-px 三選一, 不可併用")
        if args.scale is not None and args.tile_width != 480:
            raise SystemExit("ERROR: --scale 與 --tile-width 互斥 (擇一指定)")
    sys.exit(args.func(args) or 0)


if __name__ == "__main__":
    main()
