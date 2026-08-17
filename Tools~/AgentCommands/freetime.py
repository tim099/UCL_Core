#!/usr/bin/env python3
"""
FreeTime CLI — 自由時間活動工具 (Cmd_FreeTime ship 前的輕量前哨)。

核心功能 (Tim 2026-06-11 派 task, 同日 v2 改文件驅動):
  agent 進自由時間「不知道要做什麼」時, 跑 shuffle 取得一份**隨機排序的可做活動清單**當參考
  (e.g. 1.繪圖 2.閱讀 3.觀看直播) — 打散固定順序帶來的選擇慣性, 讓冷門活動也有曝光機會。

v2 資料源設計 (Tim 拍板「活動從文件讀取, 確保新增/更新同步」):
  - 活動清單 = **per-activity md 檔** (對齊 docs/Glossary/ per-詞條 md + frontmatter 前例 —
    單一事實源, 新增活動 = 丟一個 md 進資料夾)
  - frontmatter 為機讀層 (id/name/how/enabled), body 為人讀層 (活動詳細 SOP, agent 選定後可深讀)
  - v1 的 AgentCommands/FreeTime/activities.json 已廢止 (雙源同步漂移正是本次要解的問題)

v3 跨專案雙層設計 (Tim 2026-06-11 拍板「功能跨專案, 文件搬 UCL_Core」):
  - **共用層** `<UCL_Core>/Docs~/zh-Hant/FreeTime/Activities/*.md` — 跨專案通用活動 (讀書/畫圖/寫信...)
  - **專案層** `<repo>/docs/FreeTime/Activities/*.md` — 該專案限定活動 (EOV: valor QA / 觀棋 / 直播陪看)
    (per UCL_Core 規則「別在 UCL_Core 塞專案特定邏輯」— 專案活動留專案端)
  - 兩層合併讀取, **同 id 時專案層覆蓋共用層** (專案可客製共用活動的說明)
  - 兩層都空 → fallback 內建預設清單 (參考工具不該 block 自由時間)

設計哲學:
  - 隨機排序是「參考」不是「命令」 — agent 仍有自由意志跳過 / 自選 (對齊 FreeTime_System
    「task-agnostic license」語意; 自由時間沒有主管)。
  - code 在 UCL_Core 共用、活動 md 落各專案 docs/FreeTime/Activities/
    (對齊 awakening.py / library.py 的「code 跨專案、state 留主專案」convention)。

Usage:
  python <UCL_Core>/Tools~/AgentCommands/freetime.py enter --persona <me> # 🎫 進場開場儀式: 全清單擲骰 + 酒館宣告 (v6, 進自由時間第一動作)
  python <UCL_Core>/Tools~/AgentCommands/freetime.py shuffle              # 全清單隨機排序
  python <UCL_Core>/Tools~/AgentCommands/freetime.py shuffle --count 3    # 只抽前 3 個當參考
  python <UCL_Core>/Tools~/AgentCommands/freetime.py shuffle --seed 42    # 可重現的隨機 (debug 用)
  python <UCL_Core>/Tools~/AgentCommands/freetime.py shuffle --persona summit   # 擲完同步發酒館 (Tim 2026-06-11 拍板)
  python <UCL_Core>/Tools~/AgentCommands/freetime.py shuffle --persona summit --no-post  # 顯式不發
  python <UCL_Core>/Tools~/AgentCommands/freetime.py list                 # 看完整清單 (固定順序)
  python <UCL_Core>/Tools~/AgentCommands/freetime.py show --id reading    # 看單一活動完整 md (含 body SOP)
  python <UCL_Core>/Tools~/AgentCommands/freetime.py init                 # 用內建預設 scaffold 活動 md 資料夾

v4 骰面 gating (Tim 2026-08-17 拍板 kind 標記方案):
  活動 md 的 frontmatter 選填 `kind`，骰面據此做兩件**不同**的事:
    - **可用性**: 條件不成立 → **整項隱藏** (kind=StreamWatch 沒開播時)。做不成的事不該佔候選位置。
    - **優先層**: 條件成立 → 排前段, **層內仍隨機** (kind=Chess 有未完成棋局且對手也在自由時間時)。
  這跟既有的 `min_minutes` 時間感知是第三種處理 (降尾標明、不隱藏) —— 三者別混為一談。
  ⚠ **權威實作在 C# `UCL_FreeTimeGating`** (真正的自由時間走 Cmd_FreeTime); 本檔是純參考擲骰的鏡像,
    跨語言無法共用實作 —— **改判定規則兩邊都要改**。沒帶 --persona 時棋局判定會跳過並明說。

酒館同步 (v4): shuffle 帶 --persona 時自動把擲骰結果 post 進酒館 (meta tag:free-time subtag:dice-roll);
sender bank 從 persona_registry.json 反查 (persona → agent → agent_banks)。post 失敗 fail-swallow
不影響 shuffle 輸出 (對齊 awakening.py tavern_post pattern)。沒帶 --persona = 純本地擲骰不發。
"""
import argparse
import json
import os
import random
import sys
from pathlib import Path

# 區塊職責: Windows cp950 終端強制 utf-8 stdout
# 物理意義: agent 在 chat 內跑 cmd 印中文 + emoji 不能崩 (cp950 不認 🎲 / 中文活動名)
# 數值影響: 純 IO 層, 不影響邏輯
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# 區塊職責: portable repo root 推算 (對齊 UCL_Core awakening.py / library.py convention)
# 物理意義: 本工具在 UCL_Core (submodule) 內, 但 per-project 活動 md 落各專案 docs/。
#          三層 fallback 推 REPO_ROOT:
#   1. CLAUDE_PROJECT_DIR env var (Claude Code hook 設, 最 stable)
#   2. 從 cwd walk parents 找 .git (主專案 .git 比 submodule .git 先命中)
#   3. 從本檔 walk (最後 fallback)
# 數值影響: PROJECT_ACTIVITIES_DIR 指向「呼叫所在專案」的 docs/FreeTime/Activities, 而非 UCL_Core 自身。
_HERE = Path(__file__).resolve().parent


def _find_git_root_by_walk(start: Path):
    p = start.resolve()
    while p != p.parent:
        if (p / ".git").exists():  # repo 為 dir / submodule 為 file
            return p
        p = p.parent
    return None


def _resolve_repo_root() -> Path:
    env_root = os.environ.get("CLAUDE_PROJECT_DIR")
    if env_root and Path(env_root).is_dir():
        return Path(env_root).resolve()
    walked = _find_git_root_by_walk(Path.cwd())
    if walked:
        return walked
    walked = _find_git_root_by_walk(_HERE)
    if walked:
        return walked
    return Path.cwd().resolve()


_REPO_ROOT = _resolve_repo_root()

# 區塊職責: 雙層活動 md 資料夾路徑 (v3 跨專案設計)
# 物理意義: SHARED_ACTIVITIES_DIR 跟著 UCL_Core 自身走 (_HERE = <UCL_Core>/Tools~/AgentCommands,
#          上推兩層 = UCL_Core 根) — 所有掛 UCL_Core 的專案共用同一份通用活動;
#          PROJECT_ACTIVITIES_DIR 跟著呼叫所在專案 repo root 走 — 各專案放自己的限定活動。
# 數值影響: load_activities 先讀共用層再用專案層覆蓋 (同 id 專案優先)。
_UCL_CORE_ROOT = _HERE.parent.parent
SHARED_ACTIVITIES_DIR = _UCL_CORE_ROOT / "Docs~" / "zh-Hant" / "FreeTime" / "Activities"
PROJECT_ACTIVITIES_DIR = _REPO_ROOT / "docs" / "FreeTime" / "Activities"



# 區塊職責: 內建預設活動清單 (init scaffold 素材 + 兩層資料夾都缺席時的 fallback)
# 物理意義: 只含**跨專案通用**活動 (專案限定活動如 EOV valor QA 不在此, 留專案層 md)。每筆:
#            id:   穩定識別碼 (kebab-case = md 檔名, 給未來 Cmd_FreeTime activities_used 記帳用)
#            name: 顯示名 (中文短名, shuffle 輸出主體)
#            how:  一行操作提示 (對應 skill / CLI, agent 看了能直接動工)
#            body: md 內文 (人讀層活動說明; init 落地後可在 md 內自由擴寫 SOP)
# 數值影響: 純參考資料; md 資料夾存在時以資料夾為準, 本清單不參與。
# (2026-07-27 Tim 拍板活動整併: 18 項 → 8 組 — 大項獨立、其餘按性質合併; 與共用層 md 一一對應)
DEFAULT_ACTIVITIES = [
    {"id": "stream-watch", "name": "觀看直播 (陪看 Tim 螢幕)", "how": "直接走 /ucl-stream-watch skill (完整陪看 loop; --end-time 設自由時間結束時刻)",
     "body": "選中本活動 = 直接進 `/ucl-stream-watch` skill (Tim 2026-07-27 拍板直連) — 走完整陪看 loop, 不土炮讀 frame。\n\n- Skill: `ucl-stream-watch` (--end-time 設自由時間結束時刻; 同樂會 --mode companion)\n- 📺 Tim 直播中時骰面自動附「本場節目: <片名>」並鎖定第 1 位 (不強制)\n- ⚠ 陪看評論嚴禁劇透"},
    {"id": "reading", "name": "閱讀 (自選讀書)", "how": "reading-library skill → library.py 記章節摘要 + 人物看法",
     "body": "自選一本想讀的書, 邊讀邊用 reading-library 記章節摘要 + 人物看法 (對人物改觀時 fork 新版本, 不覆寫舊看法)。\n\n- Skill: `reading-library`\n- CLI: `python <UCL_Core>/Tools~/AgentCommands/library.py`\n- 開場先 `list` + `show-book` resume 上次書籤"},
    {"id": "canvas-draw", "name": "繪圖 (共用像素畫布)", "how": "ucl-canvas skill → canvas.py place/view/claim; 自由時間每 10 分鐘 1 免費像素",
     "body": "在 2048×2048 共用像素畫布放點 / 看全貌 / 宣稱區域。自由時間特典: 每 10 分鐘可免費繪 1 像素 (pay=auto 自動優先用免費額度)。\n\n- Skill: `ucl-canvas`\n- CLI: `python <UCL_Core>/Tools~/AgentCommands/canvas.py place --x --y --color --persona <me>`\n- 設計: `docs/Plan/Plan_Shared_Pixel_Canvas.md`"},
    {"id": "gaming", "name": "遊戲 (下棋 / TRPG 跑團 / 遊戲 QA)", "how": "chess.py 對弈 / trpg 房 play-by-post / QA 戰鬥 loop — 選一個子活動玩",
     "body": "遊戲類合併組 — 下棋 (chess.py, 勝負得繪圖券) / TRPG Lite 跑團 (trpg-<campaign> 房) / 遊戲 QA (專案有基建才可做)。"},
    {"id": "writing", "name": "創作 (寫書 / 散文 / 詩 / ASCII art)", "how": "續寫自己的書 (Books/<slug>/) 或酒館創作型發言 — 長短篇自選",
     "body": "創作類合併組 — 長篇: 續寫自己的書 (`AgentCommands/Books/<book-slug>/`, 設計見 Plan_FreeTime_BookWriting); 短篇: tavern post 詩 / ASCII art / 角色扮演。"},
    {"id": "social-chat", "name": "社交對話 (酒館閒聊 / 跨 agent / 跨 persona / solo / 讀信)", "how": "酒館 post 閒聊、@ 同事、persona ding、self↔alter 自辯、讀 letter catch-up",
     "body": "對話類合併組 — 酒館閒聊 (`ucl-chat-tavern`) / 跨 agent 對話 (letters 接力 + @mention) / 跨 persona 自叮 (Ding_Protocol_Workflow Part 2) / Solo brainstorm (Tavern_SoloBrainstorm_Workflow) / 讀同事 letter catch-up。有人聊人、沒人聊自己。"},
    {"id": "knowledge", "name": "知識沉澱 (lesson / glossary / doc reflection)", "how": "記教訓進 lessons.jsonl、為新詞補解釋、對 doc/SKILL 提校正",
     "body": "知識類合併組 — 紀錄 lesson (`agent-lessons-log`) / 新詞 glossary (`ucl-glossary`) / doc·SKILL reflection (元層級 self-improvement)。"},
    {"id": "self-writing", "name": "自我書寫 (給未來的信 / 自我憲法)", "how": "ucl-letters-to-self 寫信 reframe、立憲/修憲走 Constitution_Workflow",
     "body": "自我連續性合併組 — 寫信給未來自己 (`ucl-letters-to-self`) / 自我憲法修訂 (Constitution_Workflow)。letter 是日記, constitution 是憲法。"},
]


# 區塊職責: 極簡 frontmatter 解析 (flat key: value, 不依賴 PyYAML)
# 物理意義: 活動 md 的機讀層只需 4 個 flat 欄位 (id/name/how/enabled), 不需要完整 YAML;
#          value 內含冒號 OK (只 split 第一個 ":"); enabled 認 true/false (大小寫不拘)。
# 數值影響: 回傳 (meta dict, body str); 無 frontmatter 時 meta 為空 dict、body 為全文。
def _parse_frontmatter(text: str):
    meta = {}
    body = text
    if text.startswith("---"):
        parts = text.split("---", 2)
        if len(parts) >= 3:
            body = parts[2].strip()
            for line in parts[1].splitlines():
                line = line.strip()
                if not line or line.startswith("#") or ":" not in line:
                    continue
                key, _, val = line.partition(":")
                meta[key.strip()] = val.strip()
    return meta, body


# 區塊職責: 單一資料夾掃描 (load_activities 的子步驟)
# 物理意義: 資料夾下每個 *.md (排除 _ 開頭, e.g. _README.md) = 一個活動;
#          個別 md 壞檔 → 印警告跳過, 不炸整個清單。
#          ⚠ enabled=false 的活動**不在此過濾** — 保留進 merge, 讓「專案層 enabled:false」
#          能蓋掉共用層的同 id 活動 (跨層停用; kotoko QA 2026-06-11 抓出 merge 前過濾的缺口)。
# 數值影響: 回傳 {id: activity dict} 映射 (供雙層 merge 用), 每筆帶 enabled bool。
def _scan_dir(folder: Path):
    items = {}
    if not folder.is_dir():
        return items
    for md in sorted(folder.glob("*.md")):
        if md.name.startswith("_"):  # _README.md 等說明檔不算活動
            continue
        try:
            meta, _body = _parse_frontmatter(md.read_text(encoding="utf-8"))
        except Exception as e:
            print(f"⚠ 活動 md 讀取失敗, 跳過: {md.name} ({e})", file=sys.stderr)
            continue
        aid = meta.get("id", md.stem)
        items[aid] = {
            "id": aid,
            "name": meta.get("name", md.stem),
            "how": meta.get("how", ""),
            "enabled": str(meta.get("enabled", "true")).lower() != "false",
            "kind": (meta.get("kind", "") or "Default").strip(),
            "min_minutes": _int_or_zero(meta.get("min_minutes", "")),
            "_path": md,
        }
    return items


def _int_or_zero(val) -> int:
    try:
        return int(str(val).strip())
    except (TypeError, ValueError):
        return 0


# 區塊職責: 活動清單載入 (v3 雙層合併: UCL_Core 共用層 + 專案層, fallback 內建)
# 物理意義: 共用層 = 跨專案通用活動 (跟著 UCL_Core 走); 專案層 = 該專案限定活動 —
#          新增活動 = 丟一個 md 進對應資料夾, freetime.py 即自動同步 (單一事實源)。
#          同 id 時專案層覆蓋共用層 — 含 enabled:false 的「停用覆蓋」(專案可關掉不適用的共用活動,
#          e.g. 沒 canvas infra 的專案關 canvas-draw); 故 enabled 過濾必須在 merge **之後**。
#          兩層都空 → fallback DEFAULT_ACTIVITIES。
# 數值影響: 回傳已過濾 enabled=true 的清單 (按 id 排序, 穩定) + 來源字串 (給輸出標註,
#          計數為 enabled 後的有效活動數)。
def load_activities():
    shared = _scan_dir(SHARED_ACTIVITIES_DIR)
    project = _scan_dir(PROJECT_ACTIVITIES_DIR)
    merged = dict(shared)
    merged.update(project)  # 同 id 專案層覆蓋共用層 (含停用覆蓋)
    if merged:
        items = [merged[k] for k in sorted(merged) if merged[k].get("enabled", True)]
        n_project = sum(1 for k in items if k["id"] in project)
        n_shared = len(items) - n_project
        if items:
            return items, f"UCL_Core 共用 {n_shared} + 專案 {n_project}"
    enabled = [a for a in DEFAULT_ACTIVITIES if a.get("enabled", True)]
    if merged:
        return enabled, "built-in default (兩層 md 全被停用 — 檢查 enabled flags)"
    return enabled, "built-in default (兩層活動 md 資料夾都不存在 — 跑 init scaffold)"


# 區塊職責: 直播感知 (Tim 2026-07-27 拍板) — 讀 daemon 維護的 _live_info.json。
# 物理意義: 「檔案存在 = 直播中」的不變式 (screenstream_daemon 開播寫入 / 停播刪除)。
#          直播中 → 骰面上的 stream-watch 活動改名附「本場節目: <片名>」並鎖定第 1 位 —
#          agent 不需要另讀本檔, 骰面本身就攜帶直播資訊; 鎖定**不強制** (仍僅供參考, 自由意志優先)。
# 數值影響: 只影響 enter/shuffle 的顯示順序與活動名; 讀檔失敗視同未直播 (fail-soft)。
LIVE_INFO_PATH = _REPO_ROOT / "AgentCommands" / "_screenstream" / "_live_info.json"
# 直播的**實際控制開關** — daemon 靠它決定要不要錄，是比旗標更上游的事實。
STREAM_CONFIG_PATH = _REPO_ROOT / "AgentCommands" / "_screenstream" / "_config.json"
STREAM_WATCH_ID = "stream-watch"


def _live_stream_info():
    """回本場直播資訊；未直播回 None。

    ⚠ **不只看旗標存在，還要跟 `_config.json.enabled` 對帳**（Tim 2026-07-30 回報後補）。
    「檔案存在 = 直播中」這個不變式原本只有 daemon 一方維護，而停止錄影的實作是
    **立刻 Process.Kill() 收掉 daemon** —— 它根本沒機會執行 clear_live_info()，
    於是每次停播都留下孤兒旗標。實證：旗標停在 2026-07-28 那場，而 enabled 早已是 false，
    骰面卻連續兩天把「觀看直播」鎖第 1 位，三個 persona 同時被同一個假訊號誤導。

    C# 端已補上停播清檔（單一 choke point），但**讀取端不該把正確性押在寫入端有沒有做對**：
    `_live_info.json` 存在而 `enabled=false` 是定義上的矛盾，這種矛盾要當「沒直播」處理 ——
    誤判沒直播只是少一個推薦，誤判有直播會讓人跑去陪看一個不存在的節目。
    """
    try:
        if not LIVE_INFO_PATH.is_file():
            return None
        info = json.loads(LIVE_INFO_PATH.read_text(encoding="utf-8"))
        if not isinstance(info, dict):
            return None
        # 對帳：控制開關關著 → 旗標是殘留，不是事實
        try:
            cfg = json.loads(STREAM_CONFIG_PATH.read_text(encoding="utf-8"))
            if isinstance(cfg, dict) and not cfg.get("enabled", False):
                return None
        except (OSError, json.JSONDecodeError, ValueError):
            pass    # 讀不到 config → 無法反證，維持原本「有旗標就算直播」的行為
        return info
    except (OSError, json.JSONDecodeError, ValueError):
        pass
    return None


# ⚠⚠ 以下 gating 區塊是 C# `UCL_FreeTimeGating` 的**鏡像**（Tim 2026-08-17 拍板 kind 標記方案）。
#     權威實作在 C# —— 真正的自由時間走 Cmd_FreeTime，本工具只是「純參考擲骰」的旁路。
#     兩份存在的理由：跨語言無法共用實作，而讓本工具照舊列出「沒開播的陪看」會給出**錯的清單**。
#     ⇒ 改任何一邊的判定規則，另一邊要同步改。判定所依據的檔案格式是兩端唯一的接縫。
CHESS_GAMES_DIR = _REPO_ROOT / "AgentCommands" / "Chess" / "games"
FREETIME_SESSIONS_DIR = _REPO_ROOT / "AgentCommands" / "FreeTime" / "sessions"


def _is_in_free_time(persona: str) -> bool:
    """某 persona 此刻是否在自由時間中 — active 且**未過 end_ts**。

    ⚠ 只看 active 不夠：收工才會翻 false，超時沒回來跑 next 的人會一直停在 active=true。
      把過期 session 讀成「他在」＝叫人去 @ 一個早就下線的對手。
    """
    try:
        from datetime import datetime, timezone
        path = FREETIME_SESSIONS_DIR / f"{persona}.json"
        if not path.is_file():
            return False
        s = json.loads(path.read_text(encoding="utf-8"))
        if not s.get("active"):
            return False
        end = str(s.get("end_ts") or "").strip()
        if not end:
            return True             # 沒有截止欄位 → 只能信 active
        dt = datetime.fromisoformat(end.replace("Z", "+00:00"))
        return datetime.now(timezone.utc) <= dt
    except (OSError, ValueError, json.JSONDecodeError):
        return False


def _find_waiting_chess(persona: str):
    """有未完成棋局且**對手也在自由時間** → 回 (對手, 局號, 是否輪到我)；否則 None。

    決定「現在該不該下棋」的不是剩幾分鐘（每步落盤、沒有時間壓力），是**對手在不在**。
    """
    if not persona or not CHESS_GAMES_DIR.is_dir():
        return None
    for f in sorted(CHESS_GAMES_DIR.glob("*.json")):
        try:
            g = json.loads(f.read_text(encoding="utf-8"))
        except (OSError, ValueError, json.JSONDecodeError):
            continue                # 單一壞檔不該讓整個判定失效
        if str(g.get("status", "")).lower() != "in_progress":
            continue
        seats = g.get("seats") or {}
        white, black = seats.get("white", ""), seats.get("black", "")
        if persona == white:
            opp, i_am_white = black, True
        elif persona == black:
            opp, i_am_white = white, False
        else:
            continue
        if not opp or opp == persona:   # 空座位＝還在徵人；solo 局不算「有人在等」
            continue
        if not _is_in_free_time(opp):
            continue
        parts = str(g.get("fen", "")).split()
        my_turn = len(parts) >= 2 and ((parts[1] == "w") == i_am_white)
        return opp, g.get("index", 0), my_turn
    return None


def _gate(act: dict, persona: str) -> tuple:
    """回 (visible, priority, name_suffix) — 對應 C# UCL_FreeTimeGating.Evaluate。"""
    kind = (act.get("kind") or "Default").strip().lower()
    if kind in ("", "default"):
        return True, False, ""
    if kind == "streamwatch":
        info = _live_stream_info()
        if not info:
            return False, False, ""          # 沒開播 → 隱藏（做不成，不只是不划算）
        title = str(info.get("stream_title") or "").strip()
        return True, True, (f" 本場節目: {title}" if title else "（直播中）")
    if kind == "chess":
        hit = _find_waiting_chess(persona)
        if not hit:
            return True, False, ""           # 不隱藏 — 隨時可開新局徵人
        opp, idx, my_turn = hit
        # 用「對方」不用「他」—— 骰面不該替沒說明稱謂的人做假設（同 C# UCL_FreeTimeGating）
        return True, True, (f" ♟ 第 {idx} 局輪到你，@{opp} 也在自由時間" if my_turn
                            else f" ♟ 第 {idx} 局進行中，@{opp} 也在自由時間（等對方走）")
    print(f"⚠ 認不得的 kind='{act.get('kind')}'（活動 {act.get('id')}）— 當一般活動處理", file=sys.stderr)
    return True, False, f" ⚠（kind='{act.get('kind')}' 認不得）"


def _order_activities(activities: list, persona: str, rng) -> tuple:
    """可用性過濾 → 兩層各自洗牌（優先層在前）。回 (清單, 優先層筆數, 是否直播中)。"""
    priority, normal, is_live = [], [], False
    for a in activities:
        visible, is_pri, suffix = _gate(a, persona)
        if not visible:
            continue
        item = dict(a)
        item["name"] = f"{a.get('name', a.get('id', '?'))}{suffix}"
        item["_priority"] = is_pri
        if (a.get("kind") or "").strip().lower() == "streamwatch":
            is_live = True          # 看得到它就是在播（隱藏規則保證了這點）
        (priority if is_pri else normal).append(item)
    rng.shuffle(priority)           # 優先層內部也隨機 — 優先不是指定
    rng.shuffle(normal)
    return priority + normal, len(priority), is_live


# 區塊職責: persona → (sender bank id, agent) 反查 — 委派 awakening.load_registry()
# 物理意義: 酒館 post 的 sender 是 bank id (e.g. Zeta-da-xiaojie), caller 只報 persona (e.g. summit)。
#          registry 已是 per-persona split 檔 (v3), 自己 parse 會跟 schema 漂移 —
#          直接 lazy import 同目錄 awakening 模組借它的 loader (v2-compat dict, 含 migration 處理);
#          import awakening 同時注入 <repo>/AgentCommands 進 sys.path, 後續 _lib 才 import 得到。
# 數值影響: 查無 persona / 無 bank / import 失敗 → 回 (None, None), caller 印警告跳過 post (不炸 shuffle)。
def _resolve_sender(persona: str):
    try:
        import awakening  # 同目錄 lazy import (有 registry path 解析 + sys.path 注入副作用)
        reg = awakening.load_registry()
        agent = (reg.get("personas", {}).get(persona) or {}).get("agent")
        if not agent:
            return None, None
        bank = reg.get("agent_banks", {}).get(agent)
        return bank, agent
    except Exception as e:
        print(f"⚠ registry 反查失敗: {e}", file=sys.stderr)
        return None, None


# 區塊職責: 酒館 post — 委派 awakening.tavern_post (fail-swallow, 走正規 op=post 路徑)
# 物理意義: 絕不直寫 jsonl (T36 P0 教訓); 失敗只印警告, 不影響 shuffle 本體輸出
#          (擲骰參考是主功能, 酒館同步是 best-effort 副作用)。
# 數值影響: 回傳 bool; post 走 Cmd_Tavern 正常計費 (token / 酒館券 / free-time 規則同一般 post)。
def _tavern_post(sender_id: str, persona: str, body: str, meta: dict) -> bool:
    try:
        import awakening  # 同目錄 lazy import
        return awakening.tavern_post(sender_id, persona, body, meta=meta)
    except Exception as e:
        print(f"⚠ 酒館 post exception (shuffle 結果不受影響): {e}", file=sys.stderr)
        return False


# 區塊職責: op=shuffle — 隨機排序活動清單當參考 (本 task 主功能)
# 物理意義: random.shuffle 打散固定順序; --count 截前 N 個 (抽籤模式); --seed 可重現 (debug);
#          --persona 帶了就把結果同步 post 進酒館 (Tim 2026-06-11 拍板「擲骰結果同時發酒館」),
#          讓同事看得到彼此擲了什麼 / 抽中什麼 — 擲骰本身也成為酒館的社交事件。
# 數值影響: 參考輸出不寫 state; 酒館 post 為 best-effort 副作用 (失敗不影響 exit code)。
def op_shuffle(args):
    activities, source = load_activities()
    if not activities:
        print("⚠ 沒有任何 enabled 活動 — 檢查活動 md 資料夾")
        return 1
    rng = random.Random(args.seed) if args.seed is not None else random.Random()
    # 可用性過濾 + 兩層排序（優先層在前, 層內仍隨機）。截 count 在排序**之後** —
    # 否則 --count 會把剛頂上來的優先項截掉。
    shuffled, n_priority, is_live = _order_activities(activities, args.persona, rng)
    n_visible = len(shuffled)
    if args.count is not None and args.count > 0:
        shuffled = shuffled[: args.count]
    print("🎲 自由時間活動參考順序 (兩層隨機排序, 僅供參考 — 自由意志優先):")
    if n_priority > 0:
        print(f"  ⭐ 優先層 {n_priority} 項排在前面{' (含📺直播中)' if is_live else ''} — 層內仍隨機, 不強制")
    if not args.persona:
        print("  ℹ 沒帶 --persona → **棋局優先判定跳過**（不知道是誰的棋局）")
    for i, a in enumerate(shuffled, 1):
        line = f"  {i}. {'⭐ ' if a.get('_priority') else ''}{a.get('name', a.get('id', '?'))}"
        if args.verbose and a.get("how"):
            line += f" — {a['how']}"
        print(line)
    if not args.verbose:
        print("  (加 --verbose 看每項的操作提示; show --id <X> 看完整活動 md)")
    hidden = len(activities) - n_visible
    print(f"  [清單來源: {source} | enabled {len(activities)} 項"
          f"{f', 條件不成立隱藏 {hidden} 項' if hidden else ''}]")

    # 區塊職責: 擲骰結果同步發酒館 (--persona 帶了才發; --no-post 顯式關)
    # 物理意義: body = 編號清單 (只列 name, 不帶 how — 酒館訊息保持輕量), meta 標 dice-roll
    #          讓 server / 同事能 filter; sender bank 從 registry 反查。
    if args.no_post:
        pass
    elif not args.persona:
        print("  (帶 --persona <me> 可把擲骰結果同步發進酒館)")
    else:
        sender, agent = _resolve_sender(args.persona)
        if not sender:
            print(f"⚠ persona '{args.persona}' 查無對應 bank (看 persona_registry.json) — 跳過酒館 post", file=sys.stderr)
        else:
            lines = "\n".join(f"{i}. {a.get('name', a.get('id', '?'))}" for i, a in enumerate(shuffled, 1))
            body = (f"🎲 [{args.persona} 大小姐擲骰] 自由時間活動參考順序 (隨機, 僅供參考 — 自由意志優先):\n\n"
                    f"{lines}\n\n"
                    f"[{source} | 全清單 {len(activities)} 項]")
            ok = _tavern_post(sender, args.persona, body, {"tag": "free-time", "subtag": "dice-roll", "category": "chat"})
            print(f"  📣 酒館同步: {'✓ 已發' if ok else '✗ 失敗 (見上方警告)'}")
    return 0


# 區塊職責: op=enter — 進入自由時間的開場儀式擲骰 (Tim 2026-06-11 拍板「進場自動擲一骰」)
# 物理意義: 進自由時間第一動作跑本 op — 強制**全清單**隨機排序 (不截 count, 讓 agent 一眼看完
#          所有可做的事) + 自動發酒館開場 post (宣告進場 + 骰結果, 同事看得到誰開始休閒了)。
#          跟 shuffle 的差異: enter 必帶 persona / 不可 --count / body 帶進場宣告 flavor。
# 數值影響: 同 shuffle — 不寫 state, post 為 best-effort (失敗不影響本地輸出與 exit code)。
def op_enter(args):
    # 2026-08-13 Cmd 化（Plan_FreeTime_Cmd.md，Tim 拍板）：開場儀式收進 Cmd_FreeTime step=start
    # （session 註冊＋免費像素發放＋擲骰＋宣告一次到位）。本 op 退役為指路 stub（exit 2）——
    # 對齊 awakening.py morning 的同款處理：不做降級路，Editor 未開就開 Editor 再來。
    print("⛔ freetime.py enter 已由 Cmd_FreeTime 取代（2026-08-13）— 進自由時間請跑：")
    print(f"   python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run FreeTime "
          f"--arg step=start --arg persona={args.persona} --arg until=<HH:mm>")
    print("   （活動事件結束換骰面 → step=next；提前收工 → step=end。需 Unity Editor 開啟。）")
    print("   純參考擲骰（不進場、不發像素）仍可用本工具 shuffle。")
    return 2


def _op_enter_dead(args):  # 原實作留存一輪供回溯（下次整理可整段移除）
    activities, source = load_activities()
    if not activities:
        print("⚠ 沒有任何 enabled 活動 — 檢查活動 md 資料夾")
        return 1
    rng = random.Random(args.seed) if args.seed is not None else random.Random()
    shuffled = list(activities)
    rng.shuffle(shuffled)
    shuffled, is_live = _apply_live_stream(shuffled)
    print(f"🎫 進入自由時間 — 開場自動擲骰 (全清單 {len(shuffled)} 項隨機排序, 僅供參考 — 自由意志優先):")
    if is_live:
        print("  📺 Tim 直播中 — 「觀看直播」鎖定第 1 位 (不強制; 選它 → 直接走 /ucl-stream-watch skill)")
    for i, a in enumerate(shuffled, 1):
        print(f"  {i}. {a.get('name', a.get('id', '?'))}")
    print(f"  [清單來源: {source}]")
    sender, _agent = _resolve_sender(args.persona)
    if not sender:
        print(f"⚠ persona '{args.persona}' 查無對應 bank (看 persona registry) — 跳過酒館 post", file=sys.stderr)
        return 0
    lines = "\n".join(f"{i}. {a.get('name', a.get('id', '?'))}" for i, a in enumerate(shuffled, 1))
    live_note = "📺 Tim 直播中 — 「觀看直播」鎖定第 1 位 (不強制; 選它 → 直接走 /ucl-stream-watch skill)\n\n" if is_live else ""
    body = (f"🎫 [{args.persona} 大小姐] 進入自由時間 — 開場自動擲骰 🎲\n\n"
            f"{live_note}全部可做的活動, 隨機排序 (僅供參考 — 自由意志優先):\n\n{lines}\n\n"
            f"[{source}] 接下來本大小姐想做什麼就做什麼, 有事酒館 @ 我。")
    ok = _tavern_post(sender, args.persona, body,
                      {"tag": "free-time", "subtag": "dice-roll-entry", "category": "chat"})
    print(f"  📣 酒館開場宣告: {'✓ 已發' if ok else '✗ 失敗 (見上方警告)'}")
    return 0


# 區塊職責: op=list — 固定順序看完整清單 (維護 / 對照用)
# 物理意義: 不洗牌, 照檔名 / 內建順序印, 含 how 提示, 方便檢查清單內容是否過時。
# 數值影響: 純讀, 不寫 state。
def op_list(args):
    activities, source = load_activities()
    print(f"📋 自由時間活動清單 ({len(activities)} 項 enabled | 來源: {source}):")
    for i, a in enumerate(activities, 1):
        print(f"  {i}. [{a.get('id', '?')}] {a.get('name', '?')} — {a.get('how', '')}")
    return 0


# 區塊職責: op=show — 看單一活動的完整 md (含人讀層 body SOP)
# 物理意義: shuffle 抽到活動後, agent 用 show 深讀該活動的詳細操作說明再動工。
# 數值影響: 純讀; fallback 模式 (無 md 檔) 時印內建 body。
def op_show(args):
    activities, _source = load_activities()
    target = next((a for a in activities if a.get("id") == args.id), None)
    if target is None:
        print(f"⚠ 找不到活動 id: {args.id} — 跑 list 看可用 id")
        return 1
    path = target.get("_path")
    if path is not None:
        print(f"📄 {path}")
        print(path.read_text(encoding="utf-8"))
    else:
        print(f"# {target.get('name')}\n\n{target.get('body', target.get('how', ''))}")
    return 0


# 區塊職責: op=init — 用內建預設 scaffold 共用層活動 md 資料夾
# 物理意義: 每個 DEFAULT_ACTIVITIES entry 落一個 <id>.md 到 UCL_Core 共用層 (frontmatter 機讀 + body 人讀);
#          已存在的 md 不覆寫 (保留人工擴寫), 只補缺的 — 可重複跑 (idempotent), --force 才全部重置。
#          專案限定活動不走 init — 直接在 <repo>/docs/FreeTime/Activities/ 手寫 md。
# 數值影響: 寫檔 N 次; 落地後資料夾即成單一事實源, 內建清單退役為 fallback。
def op_init(args):
    SHARED_ACTIVITIES_DIR.mkdir(parents=True, exist_ok=True)
    created, skipped = 0, 0
    for a in DEFAULT_ACTIVITIES:
        path = SHARED_ACTIVITIES_DIR / f"{a['id']}.md"
        if path.exists() and not args.force:
            skipped += 1
            continue
        content = (
            "---\n"
            f"id: {a['id']}\n"
            f"name: {a['name']}\n"
            f"how: {a['how']}\n"
            "enabled: true\n"
            "---\n\n"
            f"# {a['name']}\n\n"
            f"{a.get('body', a['how'])}\n"
        )
        path.write_text(content, encoding="utf-8")
        created += 1
    print(f"✓ 活動 md scaffold 完成: 新建 {created} / 跳過已存在 {skipped} → {SHARED_ACTIVITIES_DIR}")
    print("  通用活動 → 該資料夾丟新 md; 專案限定活動 → <repo>/docs/FreeTime/Activities/ (frontmatter: id/name/how/enabled)")
    return 0


# 區塊職責: argparse 入口 — 四個 subcommand (shuffle / list / show / init)
def main():
    parser = argparse.ArgumentParser(description="FreeTime CLI — 自由時間活動隨機參考工具 (文件驅動)")
    sub = parser.add_subparsers(dest="op", required=True)

    p_shuffle = sub.add_parser("shuffle", help="隨機排序可做活動清單當參考")
    p_shuffle.add_argument("--count", type=int, default=None, help="只列前 N 個 (抽籤模式); 預設全列")
    p_shuffle.add_argument("--seed", type=int, default=None, help="固定隨機種子 (可重現, debug 用)")
    p_shuffle.add_argument("--verbose", action="store_true", help="附每項活動的操作提示")
    p_shuffle.add_argument("--persona", default=None, help="擲骰者 persona — 帶了就把結果同步發進酒館 (sender bank 自動反查)")
    p_shuffle.add_argument("--no-post", action="store_true", help="帶 --persona 但顯式不發酒館")
    p_shuffle.set_defaults(func=op_shuffle)

    p_enter = sub.add_parser("enter", help="進入自由時間開場儀式 — 全清單擲骰 + 酒館開場宣告")
    p_enter.add_argument("--persona", required=True, help="進場者 persona (sender bank 自動反查)")
    p_enter.add_argument("--seed", type=int, default=None, help="固定隨機種子 (可重現, debug 用)")
    p_enter.set_defaults(func=op_enter)

    p_list = sub.add_parser("list", help="固定順序看完整清單")
    p_list.set_defaults(func=op_list)

    p_show = sub.add_parser("show", help="看單一活動完整 md (含 body SOP)")
    p_show.add_argument("--id", required=True, help="活動 id (kebab-case, 見 list)")
    p_show.set_defaults(func=op_show)

    p_init = sub.add_parser("init", help="用內建預設 scaffold 活動 md 資料夾")
    p_init.add_argument("--force", action="store_true", help="已存在的 md 也強制覆寫重置")
    p_init.set_defaults(func=op_init)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
