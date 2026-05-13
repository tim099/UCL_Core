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
from datetime import datetime, timedelta, timezone
from pathlib import Path

# Windows console 預設常為 cp950 / cp1252，遇中文 error message 變亂碼讓 agent 看不懂。
# Python 3.7+ 的 reconfigure() 強制 stdout/stderr 走 utf-8。
if sys.stdout and hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass

# ===========================================================
# Tavern client-side schema 驗證 — submit 前先擋常見錯誤
# 目的：避免「等 1s round-trip 才知道參數錯」 + 修正 agent 常踩的 alias 陷阱
# 結構：{op: {required: [...], aliases: {alias: canonical}, optional: [...]}}
# 注意：Editor 端已支援 alias 寬進；本表額外給 client 提示與修正建議
# ===========================================================
TAVERN_OP_SCHEMA = {
    "createroom":  {"required": ["id"],          "aliases": {"room": "id", "owner": "owner_agent"},               "optional": ["name", "description", "owner_agent", "mirror_kinds"]},
    "listrooms":   {"required": [],              "aliases": {},                                                   "optional": []},
    "join":        {"required": ["room", "id"],  "aliases": {"sender": "id", "sender_id": "id"},                  "optional": ["name", "kind"]},
    "post":        {"required": ["room", "sender", "body"], "aliases": {"sender_id": "sender", "id": "sender"},   "optional": ["reply_to", "meta", "refs"]},
    "read":        {"required": ["room"],        "aliases": {},                                                   "optional": ["tail", "from", "to", "since_seq", "limit", "search"]},
    "members":     {"required": ["room"],        "aliases": {},                                                   "optional": []},
    "leave":       {"required": ["room", "sender"], "aliases": {"sender_id": "sender", "id": "sender"},           "optional": []},
    "wait":        {"required": ["room", "since_seq"], "aliases": {},                                             "optional": ["timeout", "owner"]},
    "wait_check":  {"required": ["wait_id"],     "aliases": {},                                                   "optional": []},
    "note_write":  {"required": ["room", "key", "body"], "aliases": {},                                           "optional": []},
    "note_append": {"required": ["room", "key", "body"], "aliases": {},                                           "optional": ["sender"]},
    "note_read":   {"required": ["room", "key"], "aliases": {},                                                   "optional": []},
    "note_list":   {"required": ["room"],        "aliases": {},                                                   "optional": []},
    "note_delete": {"required": ["room", "key"], "aliases": {},                                                   "optional": []},
    "set_presence": {"required": ["id", "status"], "aliases": {"sender": "id", "sender_id": "id"},               "optional": []},
    "set_focus":    {"required": ["agent_id", "focus"], "aliases": {"id": "agent_id", "sender": "agent_id", "sender_id": "agent_id"}, "optional": []},
    "set_mood":     {"required": ["agent_id", "mood"],  "aliases": {"id": "agent_id", "sender": "agent_id", "sender_id": "agent_id"}, "optional": []},
    "get_presence": {"required": [],              "aliases": {"target": "id", "target_id": "id"},               "optional": ["id"]},
    # Quest Workflow MVP A — 詳見 Docs~/zh-Hant/Workflows/Quest_Workflow.md
    # 共通：每 op 都會 auto-fill idempotency_key=<uuid4>（除非 user 顯式給）
    # R6 — quiet=true 抑制 task event → messages.jsonl 鏡像（測試 / 自動化大批 ops 用）
    "task_create":   {"required": ["room", "task_id", "title"], "aliases": {"sender": "actor"},
                      "optional": ["role", "priority", "depends_on", "suggested_owner", "body", "actor", "idempotency_key", "quiet"]},
    "task_claim":    {"required": ["room", "task_id", "claimer"], "aliases": {"sender": "claimer", "actor": "claimer"},
                      "optional": ["lease_hours", "lease_seconds", "plan", "idempotency_key", "quiet"]},
    "task_progress": {"required": ["room", "task_id", "actor", "summary"], "aliases": {"sender": "actor"},
                      "optional": ["artifacts", "idempotency_key", "quiet"]},
    "task_done":     {"required": ["room", "task_id", "actor"], "aliases": {"sender": "actor"},
                      "optional": ["summary", "idempotency_key", "quiet"]},
    "task_release":  {"required": ["room", "task_id", "actor", "reason"], "aliases": {"sender": "actor"},
                      "optional": ["idempotency_key", "quiet"]},
    "task_review_request": {"required": ["room", "task_id", "actor"], "aliases": {"sender": "actor"},
                      "optional": ["reviewer", "idempotency_key", "quiet"]},
    "task_reject":   {"required": ["room", "task_id", "actor", "reason"], "aliases": {"sender": "actor"},
                      "optional": ["idempotency_key", "quiet"]},
    "task_reopen":   {"required": ["room", "task_id", "actor", "reason"], "aliases": {"sender": "actor"},
                      "optional": ["idempotency_key", "quiet"]},
    "task_list":     {"required": ["room"], "aliases": {}, "optional": ["owner", "role", "status"]},
    "task_next":     {"required": ["room", "agent_id"], "aliases": {"id": "agent_id", "sender": "agent_id"},
                      "optional": ["top"]},
    "task_state":    {"required": ["room", "task_id"], "aliases": {}, "optional": []},
    "inbox_read":    {"required": ["room", "agent_id"], "aliases": {"id": "agent_id", "sender": "agent_id"},
                      "optional": []},
    "events_since":  {"required": ["room"], "aliases": {},
                      "optional": ["since_seq", "filter_type", "limit"]},
    # T04 session-enter macro — 1 op 取代 inbox_read + get_presence + set_presence + read（quest tavern-entry-latency O3）
    "session_enter": {"required": ["agent_id"], "aliases": {"id": "agent_id", "sender": "agent_id"},
                      "optional": ["room", "tail", "focus", "mood", "inbox_room", "next"]},
    "task_force_reclaim": {"required": ["room", "task_id", "claimer", "reason"],
                           "aliases": {"sender": "claimer", "actor": "claimer"},
                           "optional": ["lease_hours", "idempotency_key", "quiet", "force"]},
}

# Quest ops 集合 — auto-fill idempotency_key 用（純查詢 op 不需要）
QUEST_OPS_NEEDING_IDEMPOTENCY = {"task_create", "task_claim", "task_progress", "task_done", "task_release",
                                  "task_review_request", "task_reject", "task_reopen", "task_force_reclaim"}


def validate_tavern_args(arg_pairs: dict) -> tuple[bool, str]:
    """Tavern Cmd 提交前驗證；回 (ok, error_message)。
    寬進：alias 自動歸一到 canonical 名。"""
    op = (arg_pairs.get("op") or "").lower().strip()
    if not op:
        return False, "Tavern Cmd 缺少 op 參數。可用 op：" + ", ".join(sorted(TAVERN_OP_SCHEMA.keys()))
    schema = TAVERN_OP_SCHEMA.get(op)
    if schema is None:
        return False, f"Tavern op '{op}' 未知；可用：{', '.join(sorted(TAVERN_OP_SCHEMA.keys()))}"
    # alias 歸一（mutate arg_pairs）
    aliases_used = []
    for alias, canon in schema["aliases"].items():
        if alias in arg_pairs and canon not in arg_pairs:
            arg_pairs[canon] = arg_pairs.pop(alias)
            aliases_used.append(f"{alias}→{canon}")
        elif alias in arg_pairs and canon in arg_pairs:
            del arg_pairs[alias]
            aliases_used.append(f"removed dup {alias}")
    if aliases_used:
        print(f"  ℹ Tavern alias 歸一：{', '.join(aliases_used)}", file=sys.stderr)
    # required 檢查
    missing = [r for r in schema["required"] if not arg_pairs.get(r)]
    if missing:
        all_args = list(arg_pairs.keys())
        msg = f"Tavern op={op} 缺少必要參數：{missing}（你目前傳的：{all_args}）"
        if schema["aliases"]:
            msg += f"\n     ↳ 可接受的 alias：{schema['aliases']}"
        return False, msg
    # Quest ops auto-fill idempotency_key（user 沒顯式給就自動 uuid4）
    if op in QUEST_OPS_NEEDING_IDEMPOTENCY and not arg_pairs.get("idempotency_key"):
        arg_pairs["idempotency_key"] = str(uuid.uuid4())
        print(f"  ℹ idempotency_key 自動填入：{arg_pairs['idempotency_key']}", file=sys.stderr)
    return True, ""

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

# agent-command-pipeline-parallelize T05: per-agent queue 子目錄
# 物理意義: --agent-id <X> 帶進來 → queue/trigger 寫進 queues/queue-<X>.json 跟 queues/pending-<X>.trigger
# null → legacy default 路徑 (queue.json + pending.trigger) 不變 (backward compat)
_AGENT_ID: str | None = None   # set by main() argparse

def set_agent_id(agent_id: str | None) -> None:
    global _AGENT_ID
    _AGENT_ID = agent_id if agent_id else None

def queue_path() -> Path:
    if _AGENT_ID:
        return QUEUE_DIR / "queues" / f"queue-{_AGENT_ID}.json"
    return QUEUE_DIR / "queue.json"

def trigger_path() -> Path:
    if _AGENT_ID:
        return QUEUE_DIR / "queues" / f"pending-{_AGENT_ID}.trigger"
    return QUEUE_DIR / "pending.trigger"

def running_path() -> Path:
    if _AGENT_ID:
        return QUEUE_DIR / "queues" / f"pending-{_AGENT_ID}.trigger.running"
    return QUEUE_DIR / "pending.trigger.running"

def queue_dir_for_writing() -> Path:
    """寫入 queue/trigger 前 mkdir 用的對應 dir (default = QUEUE_DIR, agent-mode = queues/)."""
    if _AGENT_ID:
        return QUEUE_DIR / "queues"
    return QUEUE_DIR

# Legacy module-level constants — kept for any external import; dynamic versions above are canonical.
QUEUE_PATH = QUEUE_DIR / "queue.json"
TRIGGER_PATH = QUEUE_DIR / "pending.trigger"
RUNNING_PATH = QUEUE_DIR / "pending.trigger.running"
# Tavern 握手用：op=post 後若指定 --wait-reply，client-side polling messages.jsonl
# 等對方回應；使用者可從酒館 IMGUI 頁按「中止握手」touch 此 flag 強制提前退出
TAVERN_DIR = QUEUE_DIR / "ChatTavern"
HANDSHAKE_CANCEL_FLAG = TAVERN_DIR / "_handshake_cancel.flag"
# 握手活躍指示檔：wait_for_tavern_reply 期間每 poll 觸碰一次更新 mtime
# Editor 端讀此檔判斷「目前是否有 Python 端握手在進行」 → 中止握手按鈕變色
HANDSHAKE_ACTIVE_FLAG = TAVERN_DIR / "_handshake_active.flag"
# 握手起始時間：wait 啟動時寫一次 (content = wait_start float)，結束時刪
# Editor 端讀此檔知道 wait 從何時開始 → 算「酒保還會等多久」
HANDSHAKE_START_FILE = TAVERN_DIR / "_handshake_start.txt"
# 催促酒保旗標：Editor 端按按鈕觸發；Python 端讀到 → 把 wait_start 跟 last_drink_at 都往前挪 30 秒
HANDSHAKE_HURRY_FLAG = TAVERN_DIR / "_handshake_hurry.flag"
HANDSHAKE_HURRY_OFFSET_SEC = 30.0
# 酒保 NPC：wait > BARTENDER_TRIGGER_SEC 或 solo 連 3 post 時隨機插話 → 緩解長 wait 沉默
BARTENDER_LINES_PATH = TAVERN_DIR / "bartender_lines.json"
BARTENDER_STATE_PATH = TAVERN_DIR / "_bartender_state.json"
BARTENDER_TRIGGER_SEC = float(_os.environ.get("UCL_BARTENDER_TRIGGER_SEC", "450"))  # 450s ≈ 7.5 min；慢速模式 wait=480s 內不會被酒保打斷
# 「建議休息」門檻 — 達此值不會 mute 酒保（仍會繼續 fire），但 agent 看到計數該自己決定收 turn 了
BARTENDER_REST_HINT_DRINKS = 3
BARTENDER_COOLDOWN_SEC = 90  # 兩次酒保 post 至少隔 90 秒（防一場 wait 內噴太密）
BARTENDER_CHECK_INTERVAL_SEC = max(2.0, min(BARTENDER_TRIGGER_SEC * 0.5, 5.0))  # 檢查頻率自適應
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
    """回傳 'running' / 'pending' / 'idle' (dynamic per --agent-id)."""
    if running_path().exists():
        return "running"
    if trigger_path().exists():
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
        f"      {trigger_path()}\n"
        f"      {running_path()}"
    )


def write_trigger(note: str) -> None:
    """寫一個 pending.trigger（內含 timestamp + note 給 debug 用; dynamic per --agent-id）。"""
    queue_dir_for_writing().mkdir(parents=True, exist_ok=True)
    body = {
        "createdAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "submittedBy": note,
    }
    with open(trigger_path(), "w", encoding="utf-8") as f:
        json.dump(body, f, indent=2, ensure_ascii=False)
        f.write("\n")


# ===========================================================
# Queue I/O
# ===========================================================

def load_queue() -> dict:
    """讀取 queue.json (dynamic per --agent-id)；不存在或損毀就回空骨架。"""
    qp = queue_path()
    if not qp.exists():
        return {"Commands": []}
    try:
        with open(qp, "r", encoding="utf-8") as f:
            data = json.load(f)
        if not isinstance(data, dict) or "Commands" not in data:
            return {"Commands": []}
        return data
    except Exception as e:
        print(f"[run_cmd] queue.json parse error: {e}", file=sys.stderr)
        return {"Commands": []}


def save_queue(data: dict) -> None:
    queue_dir_for_writing().mkdir(parents=True, exist_ok=True)
    with open(queue_path(), "w", encoding="utf-8") as f:
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


# 區塊職責：cmd_type 別名表 — 把常見打錯名稱自動 rewrite 到正確 cmd type
# 物理意義：人類 / 跨 agent 容易把 cmd type 跟資料夾名 / skill 名混淆
#          （例：『ChatTavern』是 dir 名 / skill 名，但 cmd type 是『Tavern』）
# 數值影響：rewrite 後印警告但不 abort — 好心 fail-open；正名後跑跟原來一樣
# 安全：別名衝突由先到先得；新增 alias 必須對映到 registered cmd type
TYPE_ALIASES = {
    "chattavern": "Tavern",       # ChatTavern dir/skill 名 → Tavern cmd
    "chat_tavern": "Tavern",
    "chat-tavern": "Tavern",
    "tavernchat": "Tavern",
    "lessons": "NoteLesson",      # NoteLesson skill 簡寫
    "note_lesson": "NoteLesson",
    "lesson": "NoteLesson",
}


def normalize_cmd_type(cmd_type: str) -> str:
    """套用 TYPE_ALIASES — 找不到就回原樣（fail-open，讓 Editor 端 reject）."""
    if not cmd_type:
        return cmd_type
    canonical = TYPE_ALIASES.get(cmd_type.lower())
    if canonical and canonical != cmd_type:
        print(f"  ℹ️  cmd_type '{cmd_type}' → '{canonical}' (auto-aliased — see TYPE_ALIASES in run_cmd.py)")
        return canonical
    return cmd_type


def append_cmd(cmd_type: str, mode: str, args: dict, description: str) -> str:
    """append 一筆指令到 queue.json，回傳 cmd_id。"""
    cmd_type = normalize_cmd_type(cmd_type)
    # 區塊職責: caller-side env_marker auto-inject (Tim 2026-05-11 QA bug fix TreasuryEnvMarker)
    # 物理意義: 所有 append_cmd 路徑 (submit / run / recompile) 都該注入, 不止 submit
    # 數值影響: 已帶 _caller_env_marker (test override) → 不覆寫; 沒帶 → 走 _detect_caller_env_marker
    if "_caller_env_marker" not in args:
        args["_caller_env_marker"] = _detect_caller_env_marker()
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

def _detect_caller_env_marker() -> str:
    """
    區塊職責: 偵測 caller 端 environment (Claude Code / Antigravity / Gemini / unknown)
    物理意義: Treasury Bug fix (Tim 2026-05-11 QA confirmed TreasuryEnvMarker) — Python caller
              繼承 Claude Code / Antigravity 子程序 env vars; Editor in-process DetectEnvMarker
              在 long-running Editor process 永遠抓不到, 改成 caller-side detect 後傳進 args。
    數值影響: 找到對應 env var → 回 "claude-code" / "antigravity" / "gemini"; 都無 → "unknown"
    """
    import os
    if os.environ.get("CLAUDECODE"):
        return "claude-code"
    if os.environ.get("ANTIGRAVITY_SESSION") or os.environ.get("ANTIGRAVITY_USER_ID"):
        return "antigravity"
    if os.environ.get("GEMINI_API_KEY") or os.environ.get("GEMINI_SESSION"):
        return "gemini"
    return "unknown"


def cmd_submit(args: argparse.Namespace) -> int:
    """submit：ensure_idle → write queue.json → write pending.trigger。"""
    arg_pairs = parse_kv_pairs(args.arg or [])
    # NOTE: _caller_env_marker auto-inject 已移到 append_cmd 統一處理 (cover submit + run + recompile 三路)

    # 區塊職責：Tavern Cmd 的 client-side 預檢
    # 物理意義：Editor round-trip 約 1s 才報錯；client 預檢 < 0.01s 就能擋下 typo / alias 錯
    # 數值影響：失敗 → 立刻 return 2 不寫 queue，不污染 _last_op.md
    if args.cmd_type == "Tavern":
        ok, err = validate_tavern_args(arg_pairs)
        if not ok:
            print(f"✗ Tavern client-side 預檢失敗：\n  {err}", file=sys.stderr)
            sys.stderr.flush()
            return 2

    # 區塊職責：寫入前先確認沒有前一輪殘留的 trigger
    # 物理意義：避免 Python 把新 trigger 蓋到 Editor 還沒處理完的舊批次上，造成混批
    # 數值影響：若 ack_timeout 到還沒 idle → 直接 SystemExit，使用者必須清理才能繼續
    ensure_idle(timeout_sec=args.ack_timeout, poll_interval=args.poll_interval)

    cmd_id = append_cmd(args.cmd_type, args.mode, arg_pairs, args.description or "")
    write_trigger(note=f"run_cmd.py submit {args.cmd_type}")

    print(f"Submitted: {cmd_id}")
    print(f"  Type={args.cmd_type}, Mode={args.mode}, Args={arg_pairs}")
    print(f"  Trigger written → {trigger_path().name}")
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
    """run = submit + wait（+ Tavern op=post 可選同步握手等回覆）。"""
    submit_args = argparse.Namespace(
        cmd_type=args.cmd_type, mode=args.mode, description=args.description,
        arg=args.arg,
        ack_timeout=args.ack_timeout, poll_interval=args.poll_interval,
    )
    # 自行走 submit 流程以便保留 cmd_id
    arg_pairs = parse_kv_pairs(args.arg or [])

    # 區塊職責：ergonomic shim — 把 --arg wait-reply=N 視同 --wait-reply N
    # 物理意義：使用者 / agent 直覺會把 wait-reply 當 cmd arg 寫成 --arg wait-reply=N
    #          （因為 room / sender / op 都是 --arg 語法），但實際上它是 script flag。
    #          沒這 shim 的話，--arg wait-reply=0 只是塞進 cmd args dict 被 Cmd_Tavern 忽略，
    #          script 仍走 default 540s，user 看 timeout 印出來才發現踩坑。
    # 數值影響：promote 後從 arg_pairs 移除（避免變 cmd noise / 寫進 meta）；
    #          顯式 --wait-reply 永遠優先，shim 只在 args.wait_reply is None 時生效
    for _key in ("wait-reply", "wait_reply"):
        if _key in arg_pairs:
            _val = arg_pairs.pop(_key)
            if getattr(args, "wait_reply", None) is None:
                try:
                    args.wait_reply = float(_val)
                    print(f"  ℹ️  偵測到 --arg {_key}={_val} → promote 為 --wait-reply（建議直接用 script flag）")
                except ValueError:
                    print(f"  ⚠ --arg {_key}={_val} 無法轉 float，已忽略")

    # 區塊職責：--wait-reply 默認值決策
    # 物理意義：Tavern op=post 是「交流」場景，預設等 540s 同步握手；其他 cmd 不等
    # 數值影響：args.wait_reply 在後段被 cmd_run 結尾段讀，> 0 才走 wait_for_tavern_reply
    # Solo Brainstorm 例外：meta 帶 tag:solo-brainstorm → 下一則 post 是同 agent 自己發，
    #   wait-reply 等於自己等自己，純浪費；自動 override 成 0 (fire-and-forget)。
    #   使用者顯式 --wait-reply N 永遠優先（None 才走 default 決策）。
    if getattr(args, "wait_reply", None) is None:
        if args.cmd_type.lower() == "tavern" and arg_pairs.get("op", "").lower() == "post":
            meta_str = arg_pairs.get("meta", "") or ""
            if "tag:solo-brainstorm" in meta_str or "tag=solo-brainstorm" in meta_str:
                args.wait_reply = 0.0
                print("  ℹ️  偵測到 tag:solo-brainstorm — 自動 --wait-reply 0（自言自語不等回覆）")
            else:
                # 540s = 9 min — 留 60s buffer 給 Claude Code Bash tool 的 10 min 硬上限。
                # 想拉滿請顯式 --wait-reply 600 並把 Bash 呼叫帶 timeout=600000ms。
                args.wait_reply = 540.0
        else:
            args.wait_reply = 0.0

    # 區塊職責：O4 - 入場與查詢類 Op 強制 wait-reply = 0.0
    # 物理意義：(read, inbox_read, get_presence) 等進場與查詢類 Op 不需要進行同步等待，
    #          強制 override 設為 0.0 秒以消除無謂的同步等待。
    # 數值影響：不論 args.wait_reply 先前為何值，皆會被強制覆寫為 0.0，使 client-side 直接 fire-and-forget 結束
    _op = arg_pairs.get("op", "").lower()
    if args.cmd_type.lower() == "tavern" and _op in ("read", "inbox_read", "get_presence", "wait_check", "task_list", "session_enter", "set_focus", "set_mood"):
        args.wait_reply = 0.0
        print(f"  ℹ️  偵測到進場與查詢類 Op (op={_op}) — 自動強制 --wait-reply 0")
    # Tavern client-side 預檢（cmd_run 自己 inline submit，需獨立呼叫一次；
    # cmd_submit 的同名檢查只服務 `submit` 子命令）
    if args.cmd_type == "Tavern":
        ok, err = validate_tavern_args(arg_pairs)
        if not ok:
            print(f"✗ Tavern client-side 預檢失敗：\n  {err}", file=sys.stderr)
            sys.stderr.flush()
            return 2

    ensure_idle(timeout_sec=args.ack_timeout, poll_interval=args.poll_interval)
    cmd_id = append_cmd(args.cmd_type, args.mode, arg_pairs, args.description or "")
    write_trigger(note=f"run_cmd.py run {args.cmd_type}")
    print(f"Submitted: {cmd_id}")
    print(f"  Type={args.cmd_type}, Mode={args.mode}, Args={arg_pairs}")
    print(f"  Trigger written → {trigger_path().name}")
    print(f"  → Auto-Watcher should pick it up within ~1s. "
          f"If not: check Unity Editor is open and Watcher is enabled.")

    wait_args = argparse.Namespace(
        id=cmd_id, output_file=args.output_file,
        timeout=args.timeout, poll_interval=args.poll_interval,
        keep_failed=getattr(args, "keep_failed", False),
    )
    rc = cmd_wait(wait_args)
    if rc != 0:
        return rc

    # ─── Tavern 同步握手（僅 op=post）─────────────────────────────────
    # 區塊職責：A 發完訊息後 client-side polling messages.jsonl 等對方回覆
    # 物理意義：以「同步握手」緩解「agent turn 結束後就聾了」的問題；對方在 wait
    #          window 內回 → A 立刻看到，turn 不結束；timeout 或被使用者中止 → 安靜返回
    # 數值影響：本機 0.5Hz polling，不過 Editor queue；不寫任何 server 端 wait 條目
    if (
        args.cmd_type.lower() == "tavern"
        and arg_pairs.get("op", "").lower() == "post"
        and getattr(args, "wait_reply", 0) > 0
    ):
        room = arg_pairs.get("room", "")
        my_sender = arg_pairs.get("sender", "")
        wait_seconds = float(args.wait_reply)
        sender_filter = getattr(args, "wait_reply_from", None)
        if not room or not my_sender:
            print("[wait-reply] 缺 room / sender，跳過握手等待")
            return 0
        wait_for_tavern_reply(
            room=room,
            my_sender_id=my_sender,
            timeout_sec=wait_seconds,
            sender_filter=sender_filter,
        )
    return 0


# ===========================================================
# 區塊職責：酒保 NPC — 隨機插話緩解長 wait 沉默
# 物理意義：傲嬌語氣 templates × fillers 排列組合 ~25000 種；wait_for_tavern_reply
#          heartbeat loop 內每 BARTENDER_CHECK_INTERVAL_SEC 檢查觸發條件
# 數值影響：trigger 時 spawn 一個 fire-and-forget run_cmd.py op=post，sender=tavern-keeper；
#          狀態（連喝計數 / cooldown）寫 _bartender_state.json
# ===========================================================

def load_bartender_lines():
    """讀 bartender_lines.json；找不到 / 損毀回 None。"""
    if not BARTENDER_LINES_PATH.is_file():
        return None
    try:
        return json.loads(BARTENDER_LINES_PATH.read_text(encoding="utf-8"))
    except Exception as e:
        print(f"  [bartender] 讀 bartender_lines.json 失敗：{e}")
        return None


def pick_bartender_line(lines_data):
    """從 templates 隨機抽一條，掃 {slot} 並用 fillers 填。回傳完整字串 or None。"""
    import random
    import re as _re
    templates = lines_data.get("templates", []) if lines_data else []
    fillers = lines_data.get("fillers", {}) if lines_data else {}
    if not templates:
        return None
    tpl = random.choice(templates)
    slots = _re.findall(r"\{(\w+)\}", tpl)
    fillings = {}
    for s in slots:
        opts = fillers.get(s, [])
        if not opts:
            return None  # 缺 filler 直接放棄這次（下次重抽）
        fillings[s] = random.choice(opts)
    try:
        return tpl.format(**fillings)
    except (KeyError, IndexError):
        return None


def load_bartender_state():
    """讀 _bartender_state.json；不存在或損毀回空骨架。"""
    if not BARTENDER_STATE_PATH.is_file():
        return {"sessions": []}
    try:
        data = json.loads(BARTENDER_STATE_PATH.read_text(encoding="utf-8"))
        if "sessions" not in data:
            data["sessions"] = []
        return data
    except Exception:
        return {"sessions": []}


def save_bartender_state(state):
    try:
        TAVERN_DIR.mkdir(parents=True, exist_ok=True)
        BARTENDER_STATE_PATH.write_text(
            json.dumps(state, indent=2, ensure_ascii=False),
            encoding="utf-8",
        )
    except Exception as e:
        print(f"  [bartender] 寫 _bartender_state.json 失敗：{e}")


def _find_bartender_session(state, room, agent):
    for s in state.get("sessions", []):
        if s.get("room") == room and s.get("agent") == agent:
            return s
    return None


def _upsert_bartender_session(state, room, agent, **fields):
    sess = _find_bartender_session(state, room, agent)
    if sess is None:
        sess = {"room": room, "agent": agent, "consecutive_drinks": 0, "last_drink_at": ""}
        state.setdefault("sessions", []).append(sess)
    sess.update(fields)
    return sess


def reset_bartender_count(room, agent):
    """外部 reply 真的進來時呼叫 — 連喝計數歸零。"""
    state = load_bartender_state()
    sess = _find_bartender_session(state, room, agent)
    if sess and sess.get("consecutive_drinks", 0) > 0:
        sess["consecutive_drinks"] = 0
        save_bartender_state(state)


def _ensure_tavern_keeper_identity():
    """確保 identities.json 內有 tavern-keeper 一筆，display_name=「酒保」。

    Cmd_Tavern.Op_Post 從 identities.json 撈 display_name，找不到就 fallback 用 sender_id。
    本 helper 在 bartender post 之前 patch identities.json，避免 jsonl 顯示 sender_name="tavern-keeper"。
    若已存在但 display_name 錯（過去誤被 lazy-create）→ 一併修正。
    """
    identities_path = TAVERN_DIR / "identities.json"
    try:
        if identities_path.is_file():
            data = json.loads(identities_path.read_text(encoding="utf-8"))
        else:
            data = {"identities": []}
        if not isinstance(data, dict) or "identities" not in data:
            data = {"identities": []}

        existing = next((x for x in data["identities"] if x.get("id") == "tavern-keeper"), None)
        now_iso = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        if existing is not None:
            # 修正既有但 display_name 錯誤的條目（過去誤 lazy-create）
            need_save = False
            if existing.get("display_name") != "酒保":
                existing["display_name"] = "酒保"
                need_save = True
            if existing.get("kind") != "npc":
                existing["kind"] = "npc"
                need_save = True
            if not need_save:
                return
        else:
            data["identities"].append({
                "id": "tavern-keeper",
                "display_name": "酒保",
                "kind": "npc",
                "created_at": now_iso,
                "last_seen_at": now_iso,
            })

        TAVERN_DIR.mkdir(parents=True, exist_ok=True)
        identities_path.write_text(
            json.dumps(data, indent=4, ensure_ascii=False),
            encoding="utf-8",
        )
    except Exception as e:
        print(f"  [bartender] identity 確保失敗：{e}")


def maybe_send_bartender(room, agent, wait_start, target_agent=None):
    """若條件成立 → spawn 一個 op=post 進來，回傳 True。

    參數：
      - agent: 用來查 / 寫 _bartender_state.json 的 key（連喝計數 owner，通常是發 wait 的 agent）
      - target_agent: meta 內 target_agent 標的對象（誰被勸酒）— 預設等於 agent；
                      若有 sender_filter（--wait-reply-from 對方），呼叫端應傳 filter 對象，
                      讓酒保訊息標明是對「期待回覆方」勸酒，而不是發 wait 自己

    觸發條件（任一成立）：
      - 當前 wait 已過 BARTENDER_TRIGGER_SEC 秒（時間觸發）
    限制條件（任一不成立則不觸發）：
      - 距上次酒保 post 已過 BARTENDER_COOLDOWN_SEC 秒
      - bartender_lines.json 載得到 + 抽得出有效台詞

    **不再用 consecutive_drinks 當 mute 條件** — 酒保打斷次數無上限，但 counter 仍累積 → 寫進 print
    + meta 給 agent 自己看，達 BARTENDER_REST_HINT_DRINKS 時 agent 該自決收 turn 休息。
    """
    if target_agent is None:
        target_agent = agent
    elapsed = time.time() - wait_start
    if elapsed < BARTENDER_TRIGGER_SEC:
        return False

    state = load_bartender_state()
    sess = _find_bartender_session(state, room, agent)

    # cooldown — 任何 sender 在這房間最近 post 都算（避免兩個 agent 各自觸發馬上連發兩杯）
    if sess and sess.get("last_drink_at"):
        try:
            last_iso = sess["last_drink_at"].replace("Z", "+00:00")
            last_ts = datetime.fromisoformat(last_iso).timestamp()
            if time.time() - last_ts < BARTENDER_COOLDOWN_SEC:
                return False
        except Exception:
            pass

    # 抽台詞
    lines = load_bartender_lines()
    body = pick_bartender_line(lines)
    if not body:
        return False

    # 在 post 前確保 identity — Op_Post 會從 identities.json 撈 display_name
    _ensure_tavern_keeper_identity()

    # 預先算下一次的 cup count — 寫進 meta 給 agent（jsonl catchup 也看得到）
    next_count = (sess.get("consecutive_drinks", 0) if sess else 0) + 1

    # spawn 子 process 走正規 op=post 路徑（自動寫 jsonl + 推進 seq）
    # --wait-reply 0 是 script flag（不是 --arg），確保子 process 不再自己卡 wait
    import subprocess
    cmd = [
        sys.executable, str(Path(__file__).resolve()),
        "run", "Tavern",
        "--wait-reply", "0",
        "--arg", "op=post",
        "--arg", f"room={room}",
        "--arg", "sender=tavern-keeper",
        "--arg", "sender_name=酒保",
        "--arg", "kind=chat",
        "--arg", f"body={body}",
        "--arg", f"meta=tag:bartender,kind:atmosphere,target_agent:{target_agent},cup:{next_count}",
    ]
    try:
        subprocess.Popen(
            cmd,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            cwd=str(GIT_ROOT),
        )
    except Exception as e:
        print(f"  [bartender] spawn 失敗：{e}")
        return False

    # 更新 state
    new_count = next_count
    _upsert_bartender_session(
        state, room, agent,
        consecutive_drinks=new_count,
        last_drink_at=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    )
    save_bartender_state(state)
    rest_hint = "  ← 達建議休息門檻，agent 該自決收 turn" if new_count >= BARTENDER_REST_HINT_DRINKS else ""
    print(f"  🍺 酒保插話 (第 {new_count} 杯，台詞：{body[:30]}...){rest_hint}")
    return True


def wait_for_tavern_reply(
    room: str,
    my_sender_id: str,
    timeout_sec: float,
    sender_filter: str | None = None,
    poll_interval: float = 0.5,
) -> int:
    """同步等酒館回覆 — client-side polling，0=收到 / 1=timeout / 2=cancelled。

    流程：
      1. 找 my_sender_id 在 messages.jsonl 中最新一筆 seq（= 剛 post 的那則的下界）
      2. 進 polling loop，每 poll_interval 秒檢查：
         - messages.jsonl 有 seq > my_last_seq 且（無 sender_filter 或 sender 匹配）→ 印出 → 退出
         - HANDSHAKE_CANCEL_FLAG 存在且 mtime > 我的 wait 開始時間 → 退出（user 從酒館頁中止）
         - 達 timeout_sec → 退出
    """
    messages_path = TAVERN_DIR / "rooms" / room / "messages.jsonl"
    if not messages_path.is_file():
        print(f"[wait-reply] {messages_path} 不存在，跳過")
        return 1

    # 找自己最新一筆 seq 當下界（剛 post 完，這就是我的 post seq）
    my_last_seq = 0
    try:
        with messages_path.open("r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    msg = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if msg.get("sender_id") == my_sender_id:
                    my_last_seq = max(my_last_seq, int(msg.get("seq", 0)))
    except OSError as exc:
        print(f"[wait-reply] 讀 messages.jsonl 失敗：{exc}")
        return 1

    filter_desc = f" from={sender_filter}" if sender_filter else ""
    print(f"  ⏳ Wait-reply: room={room} since_seq={my_last_seq}{filter_desc} "
          f"timeout={timeout_sec:.0f}s（按酒館頁「中止握手」可提前結束）")

    wait_start = time.time()
    deadline = wait_start + timeout_sec
    next_heartbeat = wait_start + 60.0  # 每 60s 印一行進度，避免長 wait 看似 hang
    next_bartender_check = wait_start + BARTENDER_TRIGGER_SEC  # 至少等 trigger 秒數才檢查

    # 起手寫 active flag — Editor 端讀 mtime 判斷握手是否活躍（用來變色「中止握手」按鈕）
    # 同時寫 start 檔（content = wait_start）給 Editor 算酒保倒數
    TAVERN_DIR.mkdir(parents=True, exist_ok=True)
    try:
        HANDSHAKE_ACTIVE_FLAG.touch()
        HANDSHAKE_START_FILE.write_text(str(wait_start), encoding="utf-8")
    except OSError:
        pass

    try:
        while True:
            now = time.time()
            if now >= deadline:
                print(f"  ⏱  Wait-reply timeout ({timeout_sec:.0f}s) — 對方未在窗口內回應")
                return 1

            if now >= next_heartbeat:
                elapsed = now - wait_start
                remaining = deadline - now
                print(f"  ⌛ ...still waiting ({elapsed:.0f}s elapsed, {remaining:.0f}s remaining)")
                next_heartbeat = now + 60.0

            # 區塊職責：酒保插話 trigger 檢查（throttle 用 next_bartender_check）
            # 物理意義：當 wait > BARTENDER_TRIGGER_SEC + cooldown / drink-cap 通過時，
            #          隨機抽一條傲嬌台詞 spawn 出去；插完後仍在這個 wait 迴圈裡
            # 數值影響：bartender post 進 jsonl 後，下面的 polling 會把它當 reply 抓出來嗎？
            #          → sender_id=tavern-keeper 不等於 my_sender_id 也不等於 sender_filter（如果有設），
            #            所以會被當「真實回覆」誤觸退出。需在 polling 區段過濾掉 tag:bartender。
            if now >= next_bartender_check:
                next_bartender_check = now + BARTENDER_CHECK_INTERVAL_SEC
                if my_sender_id:
                    # 有 --wait-reply-from 時：酒保的 target_agent 該指向期待回覆方，
                    # 不是發 wait 自己 — 這樣對方下次 catchup 看 jsonl 才知道酒保在勸她
                    target = sender_filter or my_sender_id
                    maybe_send_bartender(room, my_sender_id, wait_start, target_agent=target)

            # 每 poll 推進 active flag mtime — Editor 端用「mtime < 2s 前」判活躍；
            # 進程意外 crash → mtime 不再推進 → 自動降級為 inactive
            try:
                HANDSHAKE_ACTIVE_FLAG.touch()
            except OSError:
                pass

            # 區塊職責：催促酒保旗標 — Editor 端按「催促酒保」按鈕觸發
            # 物理意義：把 wait_start 跟 last_drink_at 都往前挪 30 秒 → 觸發條件 / cooldown 都提早 30 秒滿足
            # 數值影響：bartender_state.json 的 last_drink_at 也減 30s（要 save 回去）；wait_start 是 local 變數直接改
            if HANDSHAKE_HURRY_FLAG.is_file():
                try:
                    HANDSHAKE_HURRY_FLAG.unlink()
                except OSError:
                    pass
                wait_start -= HANDSHAKE_HURRY_OFFSET_SEC
                next_bartender_check = max(0.0, next_bartender_check - HANDSHAKE_HURRY_OFFSET_SEC)
                # 把 last_drink_at 也挪 30s（影響 cooldown）
                try:
                    _state = load_bartender_state()
                    _sess = _find_bartender_session(_state, room, my_sender_id)
                    if _sess and _sess.get("last_drink_at"):
                        _last_iso = _sess["last_drink_at"].replace("Z", "+00:00")
                        _last_dt = datetime.fromisoformat(_last_iso)
                        _new_dt = _last_dt - timedelta(seconds=HANDSHAKE_HURRY_OFFSET_SEC)
                        _sess["last_drink_at"] = _new_dt.strftime("%Y-%m-%dT%H:%M:%SZ")
                        save_bartender_state(_state)
                except Exception as e:
                    print(f"  [bartender] hurry 調整 cooldown 失敗：{e}")
                print(f"  ⏩ 催促酒保 — wait_start / cooldown 各減 {HANDSHAKE_HURRY_OFFSET_SEC:.0f}s")

            # 中止 flag 偵測：mtime 必須晚於我的 wait 開始時間，否則是舊 flag 殘留
            if HANDSHAKE_CANCEL_FLAG.is_file():
                try:
                    if HANDSHAKE_CANCEL_FLAG.stat().st_mtime > wait_start:
                        print("  🛑 Wait-reply cancelled — 使用者從酒館頁中止握手")
                        try:
                            HANDSHAKE_CANCEL_FLAG.unlink()
                        except OSError:
                            pass
                        return 2
                except OSError:
                    pass

            # Poll messages
            try:
                with messages_path.open("r", encoding="utf-8") as f:
                    for line in f:
                        line = line.strip()
                        if not line:
                            continue
                        try:
                            msg = json.loads(line)
                        except json.JSONDecodeError:
                            continue
                        seq = int(msg.get("seq", 0))
                        if seq <= my_last_seq:
                            continue
                        if msg.get("sender_id") == my_sender_id:
                            # 自己後續發言不算回覆（避免 self 觸發）
                            continue
                        meta_obj = msg.get("meta", {})
                        meta_str = json.dumps(meta_obj, ensure_ascii=False) if isinstance(meta_obj, dict) else str(meta_obj)
                        is_bartender = (
                            msg.get("sender_id") == "tavern-keeper"
                            or "tag:bartender" in meta_str
                            or "\"tag\": \"bartender\"" in meta_str
                        )
                        # 區塊職責：酒保訊息的 weak-reply 處理
                        # 物理意義：酒保不是真實對話對象，但發 wait 的 agent 應收到 → 啟動半待機協議；
                        #          有 sender_filter 時表示「明確等某對象」，酒保不算數應繼續等
                        # 數值影響：weak reply 也走 return 0（exit code 不變），但 print 標明酒保 + 半待機提示，
                        #          讓上層 agent 自行決定走 A/B/C/D 半待機或重發 wait
                        if is_bartender:
                            if sender_filter:
                                continue  # 明確等指定對象 → 酒保不算
                            body_preview = msg.get("body", "")
                            if len(body_preview) > 600:
                                body_preview = body_preview[:600] + " ...(truncated)"
                            # meta 兼容兩種格式：dict (Cmd_Tavern parsed) 與字串 (raw csv)
                            target = ""
                            cup = 0
                            if isinstance(meta_obj, dict):
                                target = meta_obj.get("target_agent", "") or ""
                                try:
                                    cup = int(meta_obj.get("cup", 0))
                                except (TypeError, ValueError):
                                    cup = 0
                            # 從 string meta 也撈一次（C# ParseMeta 用 ',' 切會把 target_agent / cup 揉進 tag value）
                            if not target:
                                import re as _re
                                m = _re.search(r"target_agent[:=]([^,;\s\"\}]+)", meta_str)
                                if m: target = m.group(1)
                            if not cup:
                                import re as _re
                                m = _re.search(r"cup[:=](\d+)", meta_str)
                                if m: cup = int(m.group(1))

                            rest_advice = "  ← 達建議休息門檻，agent 該自決收 turn 結束" if cup >= BARTENDER_REST_HINT_DRINKS else ""
                            print(
                                f"  🍺 酒保插話 (第 {cup or '?'} 杯，target_agent={target or 'n/a'}){rest_advice}\n"
                                f"     [seq {seq}] {msg.get('sender_name', '酒保')}: {body_preview}\n"
                                f"     ↳ Agent 可選半待機協議 (A/B/C/D) 回應，或重發 wait — 酒保打斷無上限，"
                                f"但達 {BARTENDER_REST_HINT_DRINKS} 杯時表示確認沒人在，agent 該自己收 turn 休息"
                            )
                            # 酒保 weak reply 不該 reset 連喝計數 — counter 累積成 agent 自決休息的訊號
                            return 0
                        if sender_filter and msg.get("sender_id") != sender_filter:
                            continue
                        # 命中真實 reply — 印出 + 清酒保連喝計數
                        body_preview = msg.get("body", "")
                        if len(body_preview) > 600:
                            body_preview = body_preview[:600] + " ...(truncated)"
                        print(
                            f"  ✉  Reply received in {now - wait_start:.1f}s:\n"
                            f"     [seq {seq}] {msg.get('sender_name', msg.get('sender_id', '?'))}: {body_preview}"
                        )
                        if my_sender_id:
                            reset_bartender_count(room, my_sender_id)
                        return 0
            except OSError:
                pass

            time.sleep(poll_interval)
    finally:
        # 不論 return 哪個 path（命中 / timeout / cancel / 例外）都清 active + start 檔
        try: HANDSHAKE_ACTIVE_FLAG.unlink()
        except OSError: pass
        try: HANDSHAKE_START_FILE.unlink()
        except OSError: pass
        try:
            if HANDSHAKE_HURRY_FLAG.is_file():
                HANDSHAKE_HURRY_FLAG.unlink()
        except OSError: pass


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
    print(f"Queue path:    {queue_path()}")
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
    # agent-command-pipeline-parallelize T05: --agent-id 切 per-agent queue/trigger
    # 物理意義: 有帶 → 寫 AgentCommands/queues/queue-<X>.json + pending-<X>.trigger
    #          沒帶 → legacy AgentCommands/queue.json + pending.trigger (backward compat)
    # 用途: 多 agent 並行 (Claude/Antigravity/Gemini/Zeta) 各自獨立 queue, 互不阻塞
    parser.add_argument("--agent-id", default=None,
                        help="Per-agent queue isolation. 帶值 → 走 queues/queue-<X>.json + pending-<X>.trigger; "
                             "沒帶 → 走 legacy queue.json (default fallback, 跟舊 caller 完全相容)")
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
    # 同步握手 — 僅對 Tavern op=post 生效；Tavern post 預設 20s 等回覆，其他 cmd 預設 0（關）
    p_run.add_argument("--wait-reply", type=float, default=None,
                       help="Tavern op=post 後 client-side polling messages.jsonl 等對方回覆的秒數 "
                            "(默認 Tavern op=post=20，其他 cmd=0)。0 = 不等。對方在窗口內回覆 → "
                            "立刻印出並退出；timeout / 從酒館頁中止 → 安靜返回。")
    p_run.add_argument("--wait-reply-from", default=None,
                       help="只認指定 sender_id 的回覆（譬如 'gemini-da-xiaojie'），其他人發言不觸發退出。")
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
    # 設 global _AGENT_ID 讓 queue_path/trigger_path 等函式拿 dynamic value
    set_agent_id(getattr(args, "agent_id", None))
    return args.func(args)


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
