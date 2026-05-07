"""
Agent Command CLI wrapper — 讓 AI agent / 人類用 CLI 提交與等待 Agent Command 執行結果。

實際執行仍由 Unity Editor 完成，但本工具新增 lock-file 自動觸發機制：

  ┌──────────┐ submit + write trigger    ┌─────────────────────────┐
  │  Python  │ ─────────────────────────▶│ AgentCommands/queue.json │
  │ (this)   │                           │ AgentCommands/pending.trigger │
  └──────────┘                           └─────────────────────────┘
                                                  │
                                                  │ EditorApplication.update (1Hz)
                                                  ▼
                                ┌────────────────────────────────────┐
                                │  UCL_AgentCommandWatcher           │
                                │   1. File.Move trigger → .running  │
                                │   2. Runner.RunAsync()             │
                                │   3. finally: Trigger.Clear()      │
                                └────────────────────────────────────┘

子命令：
  1. submit    — 寫 queue.json + 寫 pending.trigger（前置 ensure_idle）
  2. wait      — 輪詢 .running 消失 → 讀 queue.json 判定 cmd 結果
  3. run       — submit + wait（一次跑完）
  4. recompile — 觸發 Unity 重編 + 等到 .compile_status.json 推進（agent 改完 .cs 後逼 Unity 接收）
  5. list      — 列出 queue 內現存指令
  6. catalog   — 顯示 commands_catalog.md

使用範例：
    # 把 ResolveAssetReferences 加進 queue 並等到 Unity 執行完
    # 注意：outputPath 是 Cmd 內部相對 Unity project root（CardGame/）→ 實際檔案在 CardGame/AgentCommands/
    #       --output-file 是 wrapper 相對 git root → 必須帶 CardGame/ 前綴才能找到產物
    python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py run ResolveAssetReferences \\
        --arg assetType=RCG_StoryData --arg assetIds=AbandonedTemple \\
        --arg maxDepth=3 --arg format=md \\
        --arg outputPath=AgentCommands/asset_refs_AbandonedTemple.md \\
        --output-file CardGame/AgentCommands/asset_refs_AbandonedTemple.md

    # 只 submit 不 wait
    python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py submit ExportCommandCatalog

依賴前提：Unity Editor 開著 + UCL_AgentCommandWatcher 啟用（預設啟用，可在
UCL_AgentCommandsPage 上 toggle）。Watcher 停用時，Python 仍會寫 trigger，但需
人工按 Tools/UCL/Agent Commands/Run Pending 才會執行。
"""
from __future__ import annotations

import argparse
import json
import sys
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path

# ===========================================================
# 路徑解析 — 跨專案通用（不假設 UCL_Core 放在哪一層）
# ===========================================================
# 上層專案可能把 UCL_Core 放在不同位置（CardGame/Assets/UCL/ 或 Assets/UCL/ 或 root）
# 解析優先序：
#   1. 環境變數 CLAUDE_PROJECT_DIR（Claude Code hook 注入；最權威）
#   2. 從本檔位置往上找第一個含 .git 「資料夾」（避開 submodule 的 .git file）
#   3. fallback：用 parents[2]（UCL_Core 根）

import os as _os


def _find_git_root_by_walk(start: Path):
    p = start.resolve()
    while p != p.parent:
        if (p / ".git").is_dir():
            return p
        p = p.parent
    return None


_env_root = _os.environ.get("CLAUDE_PROJECT_DIR")
if _env_root and Path(_env_root).is_dir():
    GIT_ROOT = Path(_env_root).resolve()
else:
    _walked = _find_git_root_by_walk(Path(__file__))
    GIT_ROOT = _walked if _walked else Path(__file__).resolve().parents[2]
QUEUE_DIR = GIT_ROOT / "AgentCommands"
QUEUE_PATH = QUEUE_DIR / "queue.json"
TRIGGER_PATH = QUEUE_DIR / "pending.trigger"
RUNNING_PATH = QUEUE_DIR / "pending.trigger.running"
# UCL_CompileErrorTracker 寫入：mtime 推進 + errors/warnings 計數 → recompile 子命令的「完成」依據
COMPILE_STATUS_PATH = QUEUE_DIR / ".compile_status.json"
# Cmd 輸出（如 commands_catalog.md）落在 CardGame/AgentCommands/，避開外層 git root + submodule
CARDGAME_AGENT_CMDS = GIT_ROOT / "CardGame" / "AgentCommands"
CATALOG_PATH = CARDGAME_AGENT_CMDS / "commands_catalog.md"

# ensure_idle 預設值
DEFAULT_ACK_TIMEOUT = 60.0   # 等前一輪 trigger 消失的最久秒數
DEFAULT_POLL_INTERVAL = 1.0
DEFAULT_RUN_TIMEOUT = 120    # wait 階段整體超時


# ===========================================================
# Trigger 狀態 / ensure_idle
# ===========================================================

def trigger_state() -> str:
    """回傳 'running' / 'pending' / 'idle'。"""
    if RUNNING_PATH.exists():
        return "running"
    if TRIGGER_PATH.exists():
        return "pending"
    return "idle"


def ensure_idle(timeout_sec: float = DEFAULT_ACK_TIMEOUT,
                poll_interval: float = DEFAULT_POLL_INTERVAL) -> None:
    """
    在寫入新 trigger 前 block，直到 pending / running 都不存在。

    若 timeout 內仍有殘留 → SystemExit（強迫使用者人工介入），避免：
      - Editor 沒開但有殘留 trigger → 永遠等不到接手
      - Editor crash 留下 .running → 永遠等不到清除
    """
    deadline = time.time() + timeout_sec
    last_state = None
    first = True
    while time.time() < deadline:
        state = trigger_state()
        if state == "idle":
            if not first:
                print("  ✓ idle, proceeding.")
            return
        if state != last_state:
            print(f"  ... previous batch is '{state}', waiting (timeout {timeout_sec:.0f}s)...")
            last_state = state
        first = False
        time.sleep(poll_interval)

    state = trigger_state()
    raise SystemExit(
        f"[run_cmd] Previous batch still '{state}' after {timeout_sec:.0f}s.\n"
        f"  - Check Unity Editor is open with UCL_AgentCommandWatcher enabled.\n"
        f"  - If Editor crashed or watcher is off, manually delete:\n"
        f"      {TRIGGER_PATH}\n"
        f"      {RUNNING_PATH}"
    )


def write_trigger(note: str) -> None:
    """寫一個 pending.trigger（內含 timestamp + note 給 debug 用）。"""
    QUEUE_DIR.mkdir(parents=True, exist_ok=True)
    body = {
        "createdAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "submittedBy": note,
    }
    with open(TRIGGER_PATH, "w", encoding="utf-8") as f:
        json.dump(body, f, indent=2, ensure_ascii=False)
        f.write("\n")


# ===========================================================
# Queue I/O
# ===========================================================

def load_queue() -> dict:
    """讀取 queue.json；不存在或損毀就回空骨架。"""
    if not QUEUE_PATH.exists():
        return {"Commands": []}
    try:
        with open(QUEUE_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)
        if not isinstance(data, dict) or "Commands" not in data:
            return {"Commands": []}
        return data
    except Exception as e:
        print(f"[run_cmd] queue.json parse error: {e}", file=sys.stderr)
        return {"Commands": []}


def save_queue(data: dict) -> None:
    QUEUE_DIR.mkdir(parents=True, exist_ok=True)
    with open(QUEUE_PATH, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
        f.write("\n")


def remove_cmd_from_queue(cmd_id: str) -> bool:
    """從 queue.json 移除指定 Id 的 cmd；用於 cmd_wait 偵測到 Failed 時的自動清理。

    回傳 True 表示有移除；False 表示找不到（已被別的 process 移走或 id 拼錯）。
    """
    queue = load_queue()
    cmds = queue.get("Commands", [])
    new_cmds = [c for c in cmds if c.get("Id") != cmd_id]
    if len(new_cmds) == len(cmds):
        return False
    queue["Commands"] = new_cmds
    save_queue(queue)
    return True


def make_id(cmd_type: str) -> str:
    """產生人讀且唯一的 Cmd Id。格式 yyyymmdd-HHMMSS-<short-uuid>-<type-slug>。"""
    ts = datetime.now().strftime("%Y%m%d-%H%M%S")
    short = uuid.uuid4().hex[:6]
    slug = cmd_type.lower()
    return f"{ts}-{short}-{slug}"


def append_cmd(cmd_type: str, mode: str, args: dict, description: str) -> str:
    """append 一筆指令到 queue.json，回傳 cmd_id。"""
    queue = load_queue()
    cmd_id = make_id(cmd_type)
    queue["Commands"].append({
        "Id": cmd_id,
        "Type": cmd_type,
        "Mode": mode,
        "RunCount": 0,
        "Args": args,
        "CreatedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "LastRunAt": None,
        "LastRunResult": None,
        "LastRunError": None,
        "Description": description or None,
    })
    save_queue(queue)
    return cmd_id


# ===========================================================
# 子命令：submit / wait / run / list / catalog
# ===========================================================

def cmd_submit(args: argparse.Namespace) -> int:
    """submit：ensure_idle → write queue.json → write pending.trigger。"""
    arg_pairs = parse_kv_pairs(args.arg or [])

    # 區塊職責：寫入前先確認沒有前一輪殘留的 trigger
    # 物理意義：避免 Python 把新 trigger 蓋到 Editor 還沒處理完的舊批次上，造成混批
    # 數值影響：若 ack_timeout 到還沒 idle → 直接 SystemExit，使用者必須清理才能繼續
    ensure_idle(timeout_sec=args.ack_timeout, poll_interval=args.poll_interval)

    cmd_id = append_cmd(args.cmd_type, args.mode, arg_pairs, args.description or "")
    write_trigger(note=f"run_cmd.py submit {args.cmd_type}")

    print(f"Submitted: {cmd_id}")
    print(f"  Type={args.cmd_type}, Mode={args.mode}, Args={arg_pairs}")
    print(f"  Trigger written → {TRIGGER_PATH.name}")
    return 0


def cmd_wait(args: argparse.Namespace) -> int:
    """wait：等 .running 消失 → 重讀 queue 判定 cmd 結果。"""
    cmd_id = args.id
    output_file = Path(args.output_file) if args.output_file else None
    timeout_sec = args.timeout
    poll_interval = args.poll_interval

    print(f"Waiting for {cmd_id}...")
    if output_file:
        print(f"  Watching output: {output_file}")
    print(f"  Timeout: {timeout_sec}s   Poll: every {poll_interval}s")

    deadline = time.time() + timeout_sec
    saw_running = False

    # 區塊職責：先觀察 trigger 狀態變化（pending → running → idle）
    # 物理意義：
    #   - pending 階段：trigger 寫好但 Watcher 還沒接手（Editor 沒開 / Watcher 關 / 處理中其他批次）
    #   - running 階段：Watcher 已接手，正在執行
    #   - idle（即沒有 trigger 也沒有 running） + cmd 不在 queue → OneShot 成功
    while time.time() < deadline:
        state = trigger_state()
        if state == "running" and not saw_running:
            saw_running = True
            print(f"  ... Editor picked up the trigger (now running)")

        if state == "idle":
            # 一輪結束 → 判定本筆 cmd
            queue = load_queue()
            cmd = find_cmd(queue, cmd_id)
            if cmd is None:
                print(f"  ✓ Cmd disappeared from queue → Success (OneShot completed)")
                if output_file:
                    if output_file.exists():
                        print(f"  ✓ Output file exists: {output_file}")
                    else:
                        print(f"  ⚠ Output file NOT found: {output_file}")
                return 0

            # 還在 queue → 看 LastRunResult
            result = cmd.get("LastRunResult")
            if result == "Success":
                print(f"  ✓ Repeatable cmd ran successfully (RunCount={cmd.get('RunCount', 0)})")
                return 0
            if result == "Failed":
                err = cmd.get("LastRunError") or "(no error message)"
                print(f"  ✗ Cmd failed: {err}", file=sys.stderr)
                sys.stderr.flush()
                # 區塊職責：失敗的 OneShot 預設會留在 queue.json（runner 設計如此），
                #          但這會讓「下一次 submit 時整個 batch 把舊的失敗 cmd 一起重跑」
                #          → agent / 人類體感「每次都卡住」。
                # 物理意義：失敗訊息已透過 stderr 印出 + return code 2 通知 caller，
                #          原始記錄不再有保留價值；移除 queue 條目讓下一輪乾淨。
                # 數值影響：寫回 queue.json，刪除單筆。--keep-failed 可關閉此自動清理。
                if not getattr(args, "keep_failed", False):
                    remove_cmd_from_queue(cmd_id)
                    print(f"  ↳ removed failed cmd from queue (use --keep-failed to retain)",
                          file=sys.stderr)
                    sys.stderr.flush()
                return 2

            # 沒 trigger 但 cmd 仍在且 LastRunResult 是 None → trigger 被 Watcher 接走前可能 race，
            # 也可能 Watcher 沒啟動。再等一輪。
            time.sleep(poll_interval)
            continue

        time.sleep(poll_interval)

    print(f"  ✗ Timeout after {timeout_sec}s — Editor not running, "
          f"or UCL_AgentCommandWatcher disabled?", file=sys.stderr)
    return 3


def cmd_run(args: argparse.Namespace) -> int:
    """run = submit + wait。"""
    submit_args = argparse.Namespace(
        cmd_type=args.cmd_type, mode=args.mode, description=args.description,
        arg=args.arg,
        ack_timeout=args.ack_timeout, poll_interval=args.poll_interval,
    )
    # 自行走 submit 流程以便保留 cmd_id
    arg_pairs = parse_kv_pairs(args.arg or [])
    ensure_idle(timeout_sec=args.ack_timeout, poll_interval=args.poll_interval)
    cmd_id = append_cmd(args.cmd_type, args.mode, arg_pairs, args.description or "")
    write_trigger(note=f"run_cmd.py run {args.cmd_type}")
    print(f"Submitted: {cmd_id}")
    print(f"  Type={args.cmd_type}, Mode={args.mode}, Args={arg_pairs}")
    print(f"  Trigger written → {TRIGGER_PATH.name}")
    print(f"  → Auto-Watcher should pick it up within ~1s. "
          f"If not: check Unity Editor is open and Watcher is enabled.")

    wait_args = argparse.Namespace(
        id=cmd_id, output_file=args.output_file,
        timeout=args.timeout, poll_interval=args.poll_interval,
        keep_failed=getattr(args, "keep_failed", False),
    )
    return cmd_wait(wait_args)


def cmd_recompile(args: argparse.Namespace) -> int:
    """
    觸發 Unity 重編 + 等到 compile 真正完成。

    流程：
      1. 記錄當前 .compile_status.json mtime（pre-mtime）
      2. submit Cmd_Recompile（Unity 收到後呼叫 CompilationPipeline.RequestScriptCompilation）
      3. 等 cmd 從 queue 消失（=Unity 已接手，但 compile 可能還沒跑完）
      4. poll .compile_status.json 直到 mtime > pre-mtime（=tracker 寫了新 status，compile 完成）
      5. 讀新 status 的 total_errors / total_warnings，依結果回 exit code
    """
    pre_mtime = COMPILE_STATUS_PATH.stat().st_mtime if COMPILE_STATUS_PATH.exists() else 0.0
    print(f"Recompile request — pre-compile mtime: {pre_mtime:.3f}")

    # 1) submit + ensure_idle
    ensure_idle(timeout_sec=args.ack_timeout, poll_interval=args.poll_interval)
    cmd_id = append_cmd(
        cmd_type="Recompile",
        mode="OneShot",
        args={"refresh": "true" if args.refresh else "false"},
        description=args.description or "recompile via run_cmd.py",
    )
    write_trigger(f"recompile {cmd_id}")
    print(f"  Submitted: {cmd_id}")

    # 2) wait for cmd queue removal — 這只代表「Unity 已接手 ExecuteAsync 並返回」
    deadline = time.time() + args.timeout
    while time.time() < deadline:
        time.sleep(args.poll_interval)
        q = load_queue()
        if not find_cmd(q, cmd_id):
            print("  ✓ Recompile request accepted by Unity (Cmd removed from queue).")
            break
    else:
        print(f"  ⚠ Cmd_Recompile didn't leave queue within {args.timeout}s — Unity may not be running.",
              file=sys.stderr)
        return 2

    # 3) wait for compile_status.json mtime to advance — 這才是「compile 真正完成」
    print(f"  Waiting for {COMPILE_STATUS_PATH.name} mtime to advance past {pre_mtime:.3f}...")
    deadline = time.time() + args.timeout
    while time.time() < deadline:
        if COMPILE_STATUS_PATH.exists():
            now_mtime = COMPILE_STATUS_PATH.stat().st_mtime
            if now_mtime > pre_mtime + 0.001:  # 容忍 fs 精度
                # 4) 讀新 status 報告
                try:
                    with COMPILE_STATUS_PATH.open("r", encoding="utf-8-sig") as f:
                        st = json.load(f)
                except Exception as e:
                    print(f"  ⚠ failed to parse compile_status.json: {e}", file=sys.stderr)
                    return 3
                errors = st.get("total_errors", 0)
                warnings = st.get("total_warnings", 0)
                duration = st.get("duration_seconds", 0)
                ts = st.get("timestamp", "?")
                print(f"  ✓ Compile finished at {ts} ({duration}s) — errors={errors}, warnings={warnings}")
                if errors > 0:
                    # 印前幾條訊息給呼叫端看
                    for m in (st.get("messages") or [])[:5]:
                        print(f"    × {m.get('type', '?')} {m.get('file', '')}:{m.get('line', '')} — {m.get('message', '')}")
                    return 1
                return 0
        time.sleep(args.poll_interval)
    print(f"  ⚠ compile_status.json didn't advance within {args.timeout}s.", file=sys.stderr)
    return 4


def cmd_list(args: argparse.Namespace) -> int:
    """列出 queue 內現存指令。"""
    queue = load_queue()
    cmds = queue.get("Commands", [])
    print(f"Trigger state: {trigger_state()}")
    print(f"Queue path:    {QUEUE_PATH}")
    print()
    if not cmds:
        print("(queue is empty)")
        return 0
    print(f"{'Id':<48s} {'Type':<28s} {'Mode':<12s} {'Result':<12s} {'Last Run At'}")
    print("-" * 130)
    for c in cmds:
        print(f"{(c.get('Id') or '')[:46]:<48s} "
              f"{(c.get('Type') or '')[:26]:<28s} "
              f"{(c.get('Mode') or '')[:10]:<12s} "
              f"{(c.get('LastRunResult') or '-')[:10]:<12s} "
              f"{c.get('LastRunAt') or '-'}")
    return 0


def cmd_catalog(args: argparse.Namespace) -> int:
    """顯示 commands_catalog.md 內容（若存在）。"""
    if not CATALOG_PATH.exists():
        print(f"Catalog not found: {CATALOG_PATH}")
        print("先在 UCL_AgentCommandsPage 加一筆 'ExportCommandCatalog' Cmd，或：")
        print("  python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py run ExportCommandCatalog")
        return 1
    print(CATALOG_PATH.read_text(encoding="utf-8"))
    return 0


# ===========================================================
# Helpers
# ===========================================================

def find_cmd(queue: dict, cmd_id: str) -> dict | None:
    for c in queue.get("Commands", []):
        if c.get("Id") == cmd_id:
            return c
    return None


def parse_kv_pairs(items: list[str]) -> dict:
    """把 ['key=value', 'k2=v2'] 轉成 dict。"""
    out = {}
    for item in items:
        if "=" not in item:
            print(f"[run_cmd] ignoring malformed --arg (no =): {item}", file=sys.stderr)
            continue
        k, v = item.split("=", 1)
        out[k.strip()] = v.strip()
    return out


# ===========================================================
# CLI
# ===========================================================

def add_common_submit_args(p: argparse.ArgumentParser) -> None:
    p.add_argument("--mode", default="OneShot", choices=["OneShot", "Repeatable"])
    p.add_argument("--arg", action="append", default=[],
                   help="Arg as key=value (repeatable)")
    p.add_argument("--description", default=None)
    p.add_argument("--ack-timeout", type=float, default=DEFAULT_ACK_TIMEOUT,
                   help=f"Seconds to wait for previous batch to finish before submitting "
                        f"(default {DEFAULT_ACK_TIMEOUT:.0f}s)")
    p.add_argument("--poll-interval", type=float, default=DEFAULT_POLL_INTERVAL,
                   help=f"Polling interval (default {DEFAULT_POLL_INTERVAL:.1f}s)")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Agent Command CLI — submit / wait / run / list / catalog (lock-file aware)",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    sub = parser.add_subparsers(dest="action", required=True)

    # submit
    p_submit = sub.add_parser("submit", help="Add a cmd to queue.json + write pending.trigger")
    p_submit.add_argument("cmd_type", help="Cmd Type (e.g. ResolveAssetReferences)")
    add_common_submit_args(p_submit)
    p_submit.set_defaults(func=cmd_submit)

    # wait
    p_wait = sub.add_parser("wait", help="Poll trigger + queue.json until cmd completes")
    p_wait.add_argument("id", help="Cmd Id (returned by submit)")
    p_wait.add_argument("--output-file", default=None,
                        help="Optional output file to also check existence")
    p_wait.add_argument("--timeout", type=int, default=DEFAULT_RUN_TIMEOUT,
                        help=f"Max seconds to wait (default {DEFAULT_RUN_TIMEOUT})")
    p_wait.add_argument("--poll-interval", type=float, default=DEFAULT_POLL_INTERVAL,
                        help=f"Seconds between polls (default {DEFAULT_POLL_INTERVAL:.1f})")
    p_wait.add_argument("--keep-failed", action="store_true",
                        help="On Failed, keep the cmd entry in queue.json (default: auto-remove "
                             "to prevent the next batch from re-running the dead entry).")
    p_wait.set_defaults(func=cmd_wait)

    # run = submit + wait
    p_run = sub.add_parser("run", help="Submit + wait in one shot")
    p_run.add_argument("cmd_type")
    add_common_submit_args(p_run)
    p_run.add_argument("--output-file", default=None)
    p_run.add_argument("--timeout", type=int, default=DEFAULT_RUN_TIMEOUT)
    p_run.add_argument("--keep-failed", action="store_true",
                       help="On Failed, keep the cmd entry in queue.json (default: auto-remove).")
    p_run.set_defaults(func=cmd_run)

    # recompile = submit Cmd_Recompile + wait for compile_status.json mtime to advance
    p_rc = sub.add_parser("recompile",
                          help="Trigger Unity recompile + wait until .compile_status.json updates")
    p_rc.add_argument("--no-refresh", dest="refresh", action="store_false",
                      help="Skip AssetDatabase.Refresh() (only call RequestScriptCompilation)")
    p_rc.set_defaults(refresh=True)
    p_rc.add_argument("--description", default=None)
    p_rc.add_argument("--ack-timeout", type=float, default=DEFAULT_ACK_TIMEOUT)
    p_rc.add_argument("--poll-interval", type=float, default=DEFAULT_POLL_INTERVAL)
    p_rc.add_argument("--timeout", type=int, default=DEFAULT_RUN_TIMEOUT,
                      help=f"Max seconds to wait for compile to finish (default {DEFAULT_RUN_TIMEOUT})")
    p_rc.set_defaults(func=cmd_recompile)

    # list
    p_list = sub.add_parser("list", help="List commands currently in queue.json")
    p_list.set_defaults(func=cmd_list)

    # catalog
    p_cat = sub.add_parser("catalog", help="Show commands_catalog.md content")
    p_cat.set_defaults(func=cmd_catalog)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
