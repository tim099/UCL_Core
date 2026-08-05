#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
check_compile.py — 讀 UCL_CompileErrorTracker 寫入的 .compile_status.json，
                   印出 Unity 最近一次編譯結果。

設計重點：
  - **完全 standalone**：不需要 Cmd / Editor 還能跑，只要 Tracker 寫過一次 JSON 就能讀。
  - 解決 chicken-and-egg：當其他 assembly 編譯失敗時 Cmd handler 也載不進來
    （因為 Registry 反射發現 fail），這時 Cmd 路徑無解 → Python 直接讀檔最可靠。
  - Fallback：若 .compile_status.json 不存在（Tracker 從沒跑過），可用
    --fallback-log 開關去解析 Unity Editor.log（messy 但永遠都在）。

用法：
  python check_compile.py                       # 印 markdown 報告（預設）
  python check_compile.py --errors-only         # 只看 Error
  python check_compile.py --max 10              # 限制最多 10 筆
  python check_compile.py --format json         # 機器讀
  python check_compile.py --watch               # 等下次編譯結束才印
  python check_compile.py --fallback-log        # 讀 Editor.log fallback

Exit codes：
  0 = 編譯成功，0 errors
  2 = 有 error
  3 = 找不到 .compile_status.json（Tracker 沒跑過 / 路徑算錯）
"""

import argparse
import datetime
import json
import os
import subprocess
import sys
import time
from pathlib import Path

# Force UTF-8 stdout/stderr on Windows (default cp950/cp1252 chokes on emoji)
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


# ===========================================================
# 路徑解析（沿用 run_cmd.py 的 git_root 找法）
# ===========================================================
def _find_git_root_by_walk(start: Path):
    p = start.resolve()
    while p != p.parent:
        if (p / ".git").is_dir():
            return p
        p = p.parent
    return None


_env_root = os.environ.get("CLAUDE_PROJECT_DIR")
if _env_root and Path(_env_root).is_dir():
    GIT_ROOT = Path(_env_root).resolve()
else:
    _walked = _find_git_root_by_walk(Path(__file__))
    GIT_ROOT = _walked if _walked else Path(__file__).resolve().parents[2]

STATUS_PATH = GIT_ROOT / "AgentCommands" / ".compile_status.json"


# ===========================================================
# 主流程
# ===========================================================

def load_status() -> dict | None:
    """讀 .compile_status.json；不存在或 parse 失敗回 None。"""
    if not STATUS_PATH.exists():
        return None
    try:
        return json.loads(STATUS_PATH.read_text(encoding="utf-8"))
    except Exception as e:
        print(f"[check_compile] failed to parse {STATUS_PATH}: {e}", file=sys.stderr)
        return None


def dedupe_messages(msgs: list[dict]) -> list[dict]:
    """以 (type, file, line, message) 去重 — Editor.log 會累積多次重試的相同錯誤。"""
    seen = set()
    out = []
    for m in msgs:
        key = (m.get("type"), m.get("file"), m.get("line"), m.get("message"))
        if key in seen:
            continue
        seen.add(key)
        out.append(m)
    return out


def filter_messages(data: dict, errors_only: bool, max_count: int) -> list[dict]:
    msgs = data.get("messages") or []
    msgs = dedupe_messages(msgs)
    if errors_only:
        msgs = [m for m in msgs if m.get("type") == "Error"]
    if max_count > 0:
        msgs = msgs[:max_count]
    return msgs


def render_markdown(data: dict, msgs: list[dict], errors_only: bool, max_count: int,
                    stale: dict | None = None) -> str:
    lines = []
    lines.append("# 🔧 Unity Compile Status")
    lines.append("")
    # 區塊：新鮮度警告排在最前面 —— 它決定下面每一個數字能不能採信
    if stale:
        lines.append(f"> 🚨 **STALE — 這份狀態早於你的改動 {stale['lag_seconds']:.1f} 秒，"
                     f"不是你程式的編譯結果。**")
        lines.append(f">")
        lines.append(f"> 最近改動：`{stale['ref_path']}`")
        lines.append(f"> 下面的 errors / warnings 屬於**改動之前**那一次編譯 —— 綠燈不代表你的改動沒事，"
                     f"紅燈也可能是改動前就修掉的舊錯。")
        lines.append(f">")
        stalls = recent_stalls(since=stale["ref_mtime"])
        if stalls:
            lines.append(f"> 💓 改動後心跳曾停跳 {len(stalls)} 次"
                         f"（最長 {max(s.get('gap_seconds', 0) for s in stalls):.1f}s）"
                         f"—— Editor 凍過，可能正在編譯，稍後重跑。")
        else:
            lines.append(f"> 💓 改動後**沒有任何停跳紀錄** —— 編譯很可能連開始都還沒有"
                         f"（停跳證明凍結，凍結最常見的原因是編譯 / domain reload；"
                         f"但 Editor 關閉或失焦降頻也會停跳，反之進行中的凍結不會有紀錄）。")
        lines.append("")
    in_prog = data.get("in_progress", False)
    if in_prog:
        lines.append("> ⏳ **Compile in progress** — 結果尚未定案，請稍後再查。")
        lines.append("")
    lines.append(f"- Timestamp: `{data.get('timestamp', '?')}`")
    lines.append(f"- Duration: {data.get('duration_seconds', 0):.2f}s")
    lines.append(f"- **Errors: {data.get('total_errors', 0)}**")
    lines.append(f"- Warnings: {data.get('total_warnings', 0)}")
    lines.append(f"- Total messages: {data.get('total_messages', 0)} (raw)")
    lines.append(f"- Distinct after dedupe: **{len(msgs)}**")
    if data.get("tracker") == "EditorLogFallback":
        lines.append(f"- ⚠ Source: Editor.log fallback (`{data.get('_log_path', '?')}`) — "
                     f"may be **stale**: Editor.log accumulates messages across multiple compile attempts. "
                     f"`.compile_status.json` is more reliable but requires Tracker to have run.")
    if errors_only:
        lines.append(f"- (filter: errors only)")
    if max_count > 0 and len(msgs) >= max_count:
        lines.append(f"- (showing first {max_count} after dedupe)")
    lines.append("")
    if not msgs:
        if stale:
            # 過期時**絕不印「✅ Clean compile」** —— 那句話本身就是今天那隻 bug 的本體：
            # 它把「上一次編譯是乾淨的」講成「你的改動是乾淨的」。
            lines.append("⚠ **無法判定** — 這份狀態不涵蓋你的改動（見上方 STALE）。"
                         "重跑編譯後再查：`run_cmd.py recompile` 或 `--watch`。")
        elif data.get("total_errors", 0) == 0 and data.get("total_warnings", 0) == 0:
            lines.append("✅ **Clean compile.**")
        else:
            lines.append("(no messages match filter)")
        return "\n".join(lines)
    # 依 type 排序：Error 優先
    msgs_sorted = sorted(msgs, key=lambda m: (0 if m.get("type") == "Error" else 1, m.get("file", "")))
    lines.append("| # | Type | Assembly | File | Line | Message |")
    lines.append("|---|---|---|---|---|---|")
    for i, m in enumerate(msgs_sorted, 1):
        msg = (m.get("message") or "").replace("|", "\\|").replace("\n", " ")
        if len(msg) > 200:
            msg = msg[:200] + "…"
        type_emoji = "❌" if m.get("type") == "Error" else "⚠"
        lines.append(
            f"| {i} | {type_emoji} {m.get('type')} | "
            f"`{m.get('assembly', '?')}` | "
            f"`{m.get('file', '?')}` | "
            f"{m.get('line', 0)} | {msg} |"
        )
    return "\n".join(lines)


def render_json(data: dict, msgs: list[dict], stale: dict | None = None) -> str:
    out = {
        "timestamp": data.get("timestamp"),
        "duration_seconds": data.get("duration_seconds"),
        "in_progress": data.get("in_progress"),
        "total_errors": data.get("total_errors"),
        "total_warnings": data.get("total_warnings"),
        "total_messages": data.get("total_messages"),
        # 新鮮度必須進 json —— 只在 md 印警告的話，--format json 的呼叫端照樣採信過期結論，
        # 而它們（腳本）比人更不會去看旁邊那行字。
        "stale": bool(stale),
        "staleness": stale,
        "messages": msgs,
    }
    return json.dumps(out, indent=2, ensure_ascii=False)


# ===========================================================
# Fallback：讀 Editor.log（when .compile_status.json 不存在）
# ===========================================================
def find_editor_log() -> Path | None:
    """找 Unity Editor.log 路徑（cross-platform）。"""
    candidates = []
    if sys.platform == "win32":
        local_app = os.environ.get("LOCALAPPDATA")
        if local_app:
            candidates.append(Path(local_app) / "Unity" / "Editor" / "Editor.log")
    elif sys.platform == "darwin":
        home = Path.home()
        candidates.append(home / "Library" / "Logs" / "Unity" / "Editor.log")
    else:  # linux
        home = Path.home()
        candidates.append(home / ".config" / "unity3d" / "Editor.log")
    for c in candidates:
        if c.exists():
            return c
    return None


def parse_editor_log_recent(log_path: Path, max_lines: int = 5000) -> dict:
    """從 Editor.log 末尾掃 max_lines 行，撈 'error CS' 與 'warning CS' 樣式。

    區塊職責：把訊息限縮到「最近一次 compile session」，避免 Editor.log 累積的舊錯誤誤導。
    物理意義：Unity 每次 Asset Pipeline Refresh 結束會印
              `Asset Pipeline Refresh (id=...): Total: X seconds - Initiated by ...`
              此標記是 compile session 的右邊界。倒數第二個標記（或 tail 起頭）= 左邊界。
              只取兩標記之間的訊息 = 最新一次 compile 的真實狀態。
    """
    try:
        with open(log_path, "r", encoding="utf-8", errors="replace") as f:
            f.seek(0, 2)
            size = f.tell()
            # 讀末尾 ~1MB（多次 compile 也夠）
            read_size = min(size, 1 * 1024 * 1024)
            f.seek(size - read_size)
            tail = f.read()
    except Exception as e:
        return {"error": f"failed to read {log_path}: {e}", "messages": []}

    import re
    lines = tail.splitlines()[-max_lines:]

    # 找所有 Asset Pipeline Refresh ... Total: 的行 index — 這是 session 結束標記
    # 但只認「真正 compile 的 refresh」（後續 5 行內有 CompileScripts:），否則
    # 多個 short asset refresh 會把錯誤訊息切到空 window 外。
    refresh_idxs = []
    for i, ln in enumerate(lines):
        if "Asset Pipeline Refresh" in ln and "Total:" in ln:
            # 看接下來幾行有沒有 CompileScripts: 字樣
            for j in range(i + 1, min(i + 6, len(lines))):
                if "CompileScripts:" in lines[j]:
                    refresh_idxs.append(i)
                    break

    # 區塊職責：判定「最新 compile session」的視窗 [start, end)
    # 物理意義：errors 出現在 compile 過程中、Total 標記之前；要抓「Total 標記前的 N 行」
    # 兩種策略：
    #   A. 倒數第二個 + 倒數第一個 Total 之間（最精準，但需要 ≥2 個 Total 標記在 tail 內）
    #   B. 最後一個 Total 標記前 N 行（保守 fallback；單一 Total 時用，N=200 包住典型 compile）
    LOOKBACK_LINES = 200
    session_start = 0
    session_end = len(lines)
    session_marker_used = "(no compile-Refresh marker in tail — using whole tail window)"
    if len(refresh_idxs) >= 2:
        session_start = refresh_idxs[-2] + 1
        session_end = refresh_idxs[-1] + 1
        session_marker_used = (
            f"(session = lines {session_start}..{session_end} of {len(lines)} "
            f"— between second-to-last and last compile-'Refresh ... Total:' marker)"
        )
    elif len(refresh_idxs) == 1:
        # fallback：window = (last_total - 200) ~ last_total
        session_start = max(0, refresh_idxs[-1] - LOOKBACK_LINES)
        session_end = refresh_idxs[-1] + 1
        session_marker_used = (
            f"(session = lines {session_start}..{session_end} of {len(lines)} "
            f"— last {LOOKBACK_LINES} lines before only compile-'Refresh ... Total:' marker)"
        )

    msgs = []
    for line in lines[session_start:session_end]:
        # 樣式：path(line,col): error CS0103: ... 或 warning CS0414: ...
        m = re.match(r"^(.+?)\((\d+),(\d+)\):\s+(error|warning)\s+(CS\d+):\s+(.+?)$", line.strip())
        if m:
            msgs.append({
                "assembly": "(from log)",
                "file": m.group(1).replace("\\", "/"),
                "line": int(m.group(2)),
                "column": int(m.group(3)),
                "type": "Error" if m.group(4) == "error" else "Warning",
                "message": f"{m.group(5)}: {m.group(6)}",
            })
    errors = sum(1 for m in msgs if m["type"] == "Error")
    warnings = sum(1 for m in msgs if m["type"] == "Warning")
    return {
        "tracker": "EditorLogFallback",
        "timestamp": "(from log file, latest compile session)",
        "duration_seconds": 0,
        "in_progress": False,
        "total_errors": errors,
        "total_warnings": warnings,
        "total_messages": len(msgs),
        "messages": msgs,
        "_log_path": str(log_path),
        "_session_marker": session_marker_used,
    }


# ===========================================================
# CLI
# ===========================================================

HEARTBEAT_PATH = (GIT_ROOT / "AgentCommands" / "ChatTavern" / "bartender" / "_heartbeat.txt")
STALL_PATH = (GIT_ROOT / "AgentCommands" / "ChatTavern" / "bartender" / "_heartbeat_stalls.jsonl")


# ===========================================================
# 新鮮度基準（staleness guard）
# ===========================================================
# 要防的病（2026-08-05 summit 實摔）：`.compile_status.json` 寫在 08:57:00，
# 我最後一筆 .cs 編輯在 08:57:06 —— 工具照樣把那份**早於我改動 6 秒**的快照當結論報出來，
# 而它報的是紅燈（CS0103），我相信了。工具原本完全沒有「這份狀態是否涵蓋你的改動」這個概念。
#
# 為什麼吃 git 而不是走檔案樹（Tim 2026-08-05 擋下原設計 + 給方向）：
#   原設計 walk 全專案 .cs 取最新 mtime。單次 0.39s 看似便宜，但 `--watch` 每秒 poll
#   → 每秒走 1841 個檔，觀測工具自己變成負擔。
#   實測成本：root `git status -- '*.cs'` 0.149s／單一 submodule 0.078s／走樹 0.389s。
#   而**真正的修法不是換掃法，是別重複掃** —— 基準只在啟動時算一次（見 _REF_CACHE），
#   `--watch` 之後每輪只 stat 狀態檔。頻率問題因此消失，跟用哪種掃法無關。
#
# 為什麼不做 Editor 端 stamp 檔：
#   曾實作過 AssetPostprocessor 蓋章版，後撤。理由是 `OnPostprocessAllAssets` 是**靠簽章綁定的
#   magic method** —— 簽章打錯不會有編譯錯誤，只會永遠不被呼叫，而它的壞法跟「沒有改動」
#   完全一樣（不會叫的壞掉）。吃 git 資料不需要 Editor 配合，少一個那種零件。
#
# ⚠ 已知盲區（照實寫，別讓名字比事實大）：
#   1. **已 commit 但沒編譯**看不出來 —— 判準是「工作區有未提交的 .cs 改動」，
#      commit 完工作區就乾淨了。實務上 commit 前會編譯，所以接受這個缺口。
#   2. Unity 自動產生／外部工具寫入的 .cs 若被 .gitignore 排除，這裡看不到。
#   3. 只看 mtime，不看內容 —— 碰一下檔案（touch）也算改動。誤判方向刻意偏保守
#      （多喊一次 STALE），反向漏喊會讓人相信過期的綠燈。

_REF_CACHE: dict = {}


def _git_changed_cs(repo: Path) -> list[Path]:
    """
    區塊職責：問 git「這個 repo 的工作區有哪些 .cs 被改動／新增」
    物理意義：`git status --porcelain` 認 index + 工作區，包含未追蹤新檔（?? 行）。
             **root repo 看不進 submodule** —— submodule 只會顯示成一行目錄狀態，
             所以呼叫端必須對每個髒的 submodule 各問一次（見 newest_source_change）。
    回傳：絕對路徑 list；git 不可用或該 repo 不存在回空 list（不拋例外，觀測工具不該擋住本業）。
    """
    try:
        r = subprocess.run(
            ["git", "-C", str(repo), "status", "--porcelain", "--", "*.cs"],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20,
        )
        if r.returncode != 0:
            return []
        out = []
        for line in (r.stdout or "").splitlines():
            if len(line) < 4:
                continue
            # porcelain 格式：XY <path>；rename 會是 "R  old -> new"，取箭頭後那個
            path = line[3:].strip().strip('"')
            if " -> " in path:
                path = path.split(" -> ", 1)[1].strip().strip('"')
            if not path.lower().endswith(".cs"):
                continue
            out.append(repo / path)
        return out
    except Exception:
        return []


def _root_scan(root: Path) -> tuple[list[Path], list[Path]]:
    """
    區塊職責：root 只問一次 git，同時取出「root 自己改的 .cs」與「哪些 submodule 是髒的」
    物理意義：root 的 `git status --porcelain` 一份輸出裡本來就同時含兩種行 ——
             檔案行（含 .cs）與髒 submodule 的目錄行（例 ` M Assets/Plugins/UCL_Core`）。
             原本分兩次問（一次全 status 找 submodule、一次 filtered 找 .cs）是多花一次
             process spawn，同一份資料問兩遍。
    數值影響（實測 2026-08-05）：root 全 status 0.352s、單一 submodule filtered 0.03-0.07s。
             只問髒的 submodule 是省時間的關鍵 —— `git submodule foreach` 是每個 submodule
             一個 process，而髒的通常只有一兩個。
    回傳：(root 的 .cs 絕對路徑, 髒 submodule 路徑)
    """
    try:
        r = subprocess.run(
            ["git", "-C", str(root), "status", "--porcelain"],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20,
        )
        if r.returncode != 0:
            return [], []
        cs_files, subs = [], []
        for line in (r.stdout or "").splitlines():
            if len(line) < 4:
                continue
            path = line[3:].strip().strip('"')
            if " -> " in path:
                path = path.split(" -> ", 1)[1].strip().strip('"')
            p = root / path
            if path.lower().endswith(".cs"):
                cs_files.append(p)
            elif (p / ".git").exists():   # submodule 的 .git 是 gitdir redirect 檔
                subs.append(p)
        return cs_files, subs
    except Exception:
        return [], []


def newest_source_change() -> tuple[float, Path | None]:
    """
    區塊職責：取「最近一次 .cs 改動」的 mtime 與檔案，作為新鮮度基準
    物理意義：union(root 未提交 .cs, 每個髒 submodule 未提交 .cs) 取 mtime 最大者。
    數值影響：**整個 process 只算一次**（_REF_CACHE）——`--watch` 每輪重算才是真正的成本來源。
    回傳：(mtime, path)；查不到回 (0.0, None)，呼叫端據此顯示「無法判定」而不是「新鮮」。
    """
    if "ref" in _REF_CACHE:
        return _REF_CACHE["ref"]
    files, dirty_subs = _root_scan(GIT_ROOT)
    for sub in dirty_subs:
        files.extend(_git_changed_cs(sub))
    newest_t, newest_p = 0.0, None
    for f in files:
        try:
            t = f.stat().st_mtime
        except OSError:
            continue
        if t > newest_t:
            newest_t, newest_p = t, f
    _REF_CACHE["ref"] = (newest_t, newest_p)
    return _REF_CACHE["ref"]


def staleness(ref_mtime: float, ref_path: Path | None) -> dict | None:
    """
    區塊職責：判斷 .compile_status.json 是否早於基準時間
    回傳：None = 新鮮（或無法判定）；dict = 過期，含 lag 秒數與參考檔。
    """
    if ref_mtime <= 0 or not STATUS_PATH.exists():
        return None
    status_t = STATUS_PATH.stat().st_mtime
    if status_t >= ref_mtime:
        return None
    return {
        "lag_seconds": ref_mtime - status_t,
        "ref_path": str(ref_path) if ref_path else "?",
        "status_mtime": status_t,
        "ref_mtime": ref_mtime,
    }


def recent_stalls(since: float | None = None) -> list[dict]:
    """
    區塊職責：讀心跳停跳台帳（`_heartbeat_stalls.jsonl`，C# 端 UCL_BartenderIO 寫）
    物理意義：一行一筆停跳，含 stalled_since / resumed_at / gap_seconds。
             **停跳證明 Editor 凍過，不證明編譯過** —— domain reload / 資產匯入 /
             主執行緒長工 / Editor 關閉期間都會停跳。呼叫端顯示時必須照這個口徑寫。
    參數 since：只回 resumed_at 晚於該 epoch 秒的筆數（用來回答「我改完之後凍過嗎」）。
    """
    if not STALL_PATH.exists():
        return []
    out = []
    try:
        for line in STALL_PATH.read_text(encoding="utf-8", errors="replace").splitlines():
            line = line.strip()
            if not line:
                continue
            try:
                e = json.loads(line)
            except Exception:
                continue
            if since is not None:
                try:
                    ts = datetime.datetime.strptime(
                        e["resumed_at"].replace("Z", ""), "%Y-%m-%dT%H:%M:%S.%f"
                    ).replace(tzinfo=datetime.timezone.utc).timestamp()
                except Exception:
                    continue
                if ts < since:
                    continue
            out.append(e)
    except Exception:
        return []
    return out

# 心跳正常節拍 0.5s（UCL_BartenderDaemon.HEARTBEAT_INTERVAL_SECONDS）。
# 實測（2026-08-04 summit）：空閒 22 拍 min 0.50 / max 0.61 / avg 0.55s；
# 編譯 + domain reload 期間最長斷 6.14s。門檻取 1.5s —— 正常節拍的 2.7 倍，
# 落在「觀測到的最大正常值 0.61s」與「觀測到的最小異常值 1.16s」之間，兩側都有餘裕。
HEARTBEAT_STALE_SECONDS = 1.5


def check_editor_alive() -> int:
    """
    區塊職責：用酒保 daemon 的心跳檔判斷 Editor 的 update 迴圈還活著嗎。
    物理意義：daemon hook 在 EditorApplication.update，編譯 / domain reload 期間整個迴圈不跑
             → 心跳自然停。**stat 一個檔就知道，不必送 Cmd 等 round-trip**
             （實測探針要 2.13s 空閒 / 13.13s 編譯中）。
    邊界（很重要，別拿它當「正在編譯」的證明）：
             心跳停止的原因不只編譯 —— domain reload / modal dialog / Editor 掛住 /
             Editor 關閉都會停。它證明的是「**沒在 tick**」，不是「正在編譯」。
             要斷定編譯，配 .compile_status.json 一起看。
    回傳：0 = 心跳新鮮（Editor 在 tick）／1 = 心跳過期（沒在 tick）／3 = 沒有心跳檔。
    """
    if not HEARTBEAT_PATH.exists():
        print("# 💓 Editor 心跳\n\n"
              f"❔ **沒有心跳檔** — `{HEARTBEAT_PATH}`\n\n"
              "酒保 daemon 沒跑過（Editor 沒開 / 這版還沒有心跳功能）。\n"
              "此時無法用心跳判斷，退回送一支 Cmd 探針確認。")
        return 3
    age = time.time() - HEARTBEAT_PATH.stat().st_mtime
    beat = HEARTBEAT_PATH.read_text(encoding="utf-8", errors="replace").strip()
    fresh = age <= HEARTBEAT_STALE_SECONDS
    print("# 💓 Editor 心跳\n")
    print(f"- 最後一拍: `{beat}`")
    print(f"- 距今: **{age:.2f}s**（門檻 {HEARTBEAT_STALE_SECONDS}s，正常節拍 0.5s）")

    # 區塊職責：併印最近停跳（Tim 2026-08-05 GO）
    # 物理意義：心跳只答「此刻活不活」，那是**瞬時值**。單看它會得到「一切正常」，
    #          而 2026-08-05 上午騙我 40 分鐘的就是這一句 —— 編譯被 Unity 遞延的 9 分鐘裡，
    #          Editor 確實一直在 tick，「沒有卡在編譯」字面為真，卻被讀成「編譯沒問題」。
    #          停跳台帳補的是時間軸：**最近一次凍結是什麼時候**。
    stalls = recent_stalls()
    if stalls:
        last = stalls[-1]
        try:
            resumed = datetime.datetime.strptime(
                last["resumed_at"].replace("Z", ""), "%Y-%m-%dT%H:%M:%S.%f"
            ).replace(tzinfo=datetime.timezone.utc)
            ago = time.time() - resumed.timestamp()
            ago_txt = f"{ago:.0f}s 前" if ago < 3600 else f"{ago / 3600:.1f}h 前"
        except Exception:
            ago_txt = "?"
        print(f"- 最近一次停跳: `{last.get('resumed_at', '?')}` 恢復"
              f"（停了 **{last.get('gap_seconds', 0):.1f}s**，{ago_txt}）"
              f"；台帳共 {len(stalls)} 筆")
    else:
        print("- 最近一次停跳: **無紀錄** —— 可能真的沒凍過，"
              "也可能台帳剛被清或這版還沒有台帳功能（沒有條目 ≠ 沒有停跳）")

    if fresh:
        print("\n✅ **Editor 正在 tick** — 此刻沒有凍結。")
        # 這兩行是本次修正的重點：把「瞬時活著」跟「你的改動已經編過」明確切開。
        print("\n⚠ **這不代表你的改動已經編譯過。** 心跳是瞬時值；Unity 常把外部改檔的重編"
              "遞延到視窗重獲焦點，那段期間 Editor 一直在 tick。")
        print("要問「我的改動編了沒」跑 `check_compile.py --errors-only`（新鮮度守衛會答），"
              "不是看這裡。")
    else:
        print("\n⏳ **Editor 沒在 tick** — 編譯中 / domain reload / 卡住 / 已關閉。\n")
        print("這不代表「正在編譯」，只代表「現在叫它做事會等」。要區分原因看 .compile_status.json。")
    return 0 if fresh else 1


def main() -> int:
    p = argparse.ArgumentParser(
        description="Read UCL_CompileErrorTracker output and report Unity compile status.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    p.add_argument("--errors-only", action="store_true",
                   help="Show only Error messages")
    p.add_argument("--max", type=int, default=50,
                   help="Max messages to show (default 50, 0 = unlimited)")
    p.add_argument("--format", choices=["md", "json"], default="md",
                   help="Output format (default md)")
    p.add_argument("--watch", action="store_true",
                   help="Wait until compile finishes (in_progress=false), then print")
    p.add_argument("--watch-timeout", type=float, default=120,
                   help="Max seconds to wait in --watch mode (default 120)")
    p.add_argument("--watch-poll", type=float, default=1.0,
                   help="Poll interval for --watch (default 1.0s)")
    p.add_argument("--fallback-log", action="store_true",
                   help="If .compile_status.json missing, parse Unity Editor.log instead")
    p.add_argument("--editor-alive", action="store_true",
                   help="只看酒保心跳判斷 Editor 是否在 tick（0=在 tick / 1=沒在 tick / 3=無心跳檔）"
                        "；不讀 compile status、不送 Cmd")
    p.add_argument("--since-file", metavar="PATH",
                   help="新鮮度基準改用這個檔的 mtime（1 次 stat，完全跳過 git）。"
                        "你知道自己剛改了哪個檔時用它 —— 比問 git 更精準也更便宜")
    p.add_argument("--since", metavar="EPOCH_OR_ISO",
                   help="新鮮度基準改用指定時間（epoch 秒或 ISO8601）")
    p.add_argument("--no-freshness", action="store_true",
                   help="關掉新鮮度檢查（不問 git、不比 mtime）。"
                        "⚠ 關掉之後這支工具就會像 2026-08-05 之前那樣，"
                        "把早於你改動的舊快照當結論報出來")
    p.add_argument("--strict-fresh", action="store_true",
                   help="狀態過期時 exit 4（給 CI / 腳本用）。預設只印 STALE 警告不改 exit code，"
                        "以免既有呼叫端行為被改變")
    args = p.parse_args()

    # --editor-alive 是獨立查詢：純 stat 一個檔，不碰 compile status、不送 Cmd。
    # 刻意做成附加旗標而非改寫既有路徑 —— 既有呼叫端行為一個字都不變。
    if args.editor_alive:
        return check_editor_alive()

    # --watch：等到 in_progress=false
    if args.watch:
        deadline = time.time() + args.watch_timeout
        while time.time() < deadline:
            data = load_status()
            if data is not None and not data.get("in_progress", False):
                break
            time.sleep(args.watch_poll)
        else:
            print(f"[check_compile] watch timeout after {args.watch_timeout}s "
                  f"— compile still in_progress or no status file.", file=sys.stderr)

    data = load_status()
    if data is None:
        if args.fallback_log:
            log_path = find_editor_log()
            if log_path is None:
                print(f"[check_compile] no .compile_status.json AND Editor.log not found.\n"
                      f"  Expected status path: {STATUS_PATH}", file=sys.stderr)
                return 3
            print(f"[check_compile] Falling back to Editor.log: {log_path}", file=sys.stderr)
            data = parse_editor_log_recent(log_path)
        else:
            print(f"[check_compile] .compile_status.json not found.\n"
                  f"  Expected: {STATUS_PATH}\n"
                  f"  Tracker may not have run yet (need to trigger a compile first).\n"
                  f"  Tip: pass --fallback-log to parse Unity Editor.log instead.",
                  file=sys.stderr)
            return 3

    # 區塊：新鮮度基準決定 —— 三條路徑成本差很多，優先用最便宜且最精準的
    #   --since-file / --since ：呼叫端自己知道改了什麼 → 1 次 stat / 0 次 IO
    #   預設                  ：問 git 拿未提交的 .cs（**整個 process 只算一次**）
    #   --no-freshness        ：完全不查（保留舊行為的逃生門）
    stale = None
    if not args.no_freshness:
        ref_t, ref_p = 0.0, None
        if args.since_file:
            try:
                ref_p = Path(args.since_file)
                ref_t = ref_p.stat().st_mtime
            except OSError as e:
                print(f"[check_compile] --since-file 讀不到，新鮮度檢查跳過：{e}", file=sys.stderr)
        elif args.since:
            try:
                ref_t = float(args.since)
            except ValueError:
                try:
                    ref_t = datetime.datetime.fromisoformat(
                        args.since.replace("Z", "+00:00")).timestamp()
                except Exception as e:
                    print(f"[check_compile] --since 解析失敗，新鮮度檢查跳過：{e}", file=sys.stderr)
        else:
            ref_t, ref_p = newest_source_change()
        # Editor.log fallback 沒有 status 檔可比 mtime —— 不硬套，寧可不判也別亂判
        if data.get("tracker") != "EditorLogFallback":
            stale = staleness(ref_t, ref_p)

    msgs = filter_messages(data, args.errors_only, args.max)
    if args.format == "json":
        print(render_json(data, msgs, stale))
    else:
        print(render_markdown(data, msgs, args.errors_only, args.max, stale))

    if stale and args.strict_fresh:
        return 4
    return 0 if data.get("total_errors", 0) == 0 else 2


if __name__ == "__main__":
    sys.exit(main())
