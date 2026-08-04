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
import json
import os
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


def render_markdown(data: dict, msgs: list[dict], errors_only: bool, max_count: int) -> str:
    lines = []
    lines.append("# 🔧 Unity Compile Status")
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
        if data.get("total_errors", 0) == 0 and data.get("total_warnings", 0) == 0:
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


def render_json(data: dict, msgs: list[dict]) -> str:
    out = {
        "timestamp": data.get("timestamp"),
        "duration_seconds": data.get("duration_seconds"),
        "in_progress": data.get("in_progress"),
        "total_errors": data.get("total_errors"),
        "total_warnings": data.get("total_warnings"),
        "total_messages": data.get("total_messages"),
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
    if fresh:
        print("\n✅ **Editor 正在 tick** — 沒有卡在編譯 / domain reload。")
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

    msgs = filter_messages(data, args.errors_only, args.max)
    if args.format == "json":
        print(render_json(data, msgs))
    else:
        print(render_markdown(data, msgs, args.errors_only, args.max))

    return 0 if data.get("total_errors", 0) == 0 else 2


if __name__ == "__main__":
    sys.exit(main())
