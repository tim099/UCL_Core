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
import os
import re
import subprocess
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


def print_fail_verdict(text: str) -> None:
    """
    區塊職責：印出失敗判決行 — stderr 照舊（exit-code 慣例）+ stdout 同文鏡像一份。
    物理意義：caller 經 PowerShell 5.1 以 `2>&1` 呼叫本 script 時，PS 會把 native stderr
             逐行攔成 NativeCommandError ErrorRecord，用 console codepage (cp950) 重寫
             並混入本地化雜訊（「位於 線路:1 字元:1」）— caller 的 utf-8 reader 讀到
             0xa6 等 cp950 lead byte 即拋 UnicodeDecodeError，stderr 上的 ✗ 判決整段被吞
             （實證 2026-07-28：dirty bytes 固定出現在 stdout 結束後的 stderr 區段）。
             native stdout 則不論有無 2>&1 都是 raw passthrough 不經 PS 重編碼，
             判決行鏡像於 stdout 保證失敗案例穩定可見。
    數值影響：正常 caller（Bash / 直接 capture、分流讀）會看到判決兩次（stdout+stderr 各一），
             接受此冗餘換取 PS 2>&1 場景下的可見性。
    """
    print(text, file=sys.stderr)
    sys.stderr.flush()
    print(text, flush=True)


# ===========================================================
# 區塊職責：cmd 失敗時把 Editor 端落檔的詳細錯誤報告印給 caller（Tim 2026-07-29 拍板）
# 物理意義：queue 只留 LastRunError 一行；完整 stack / inner exception 鏈以前只在 Editor
#          console，client（agent）看不到，遇到「外層例外遮罩真兇」的情況根本查不動。
#          Runner 現在會寫 <DataRoot>/_cmd_errors/<cmdId>.md 與 _last_cmd_error.md，
#          本函式在判失敗後把它印出來（截斷 60 行，附完整路徑供細讀）。
# 數值影響：純讀檔輸出；檔案不存在（舊版 Editor / 落檔失敗）→ 靜默跳過，不影響失敗回報。
# ===========================================================
def print_cmd_error_report(cmd_id: "str | None", max_lines: int = 60) -> None:
    try:
        candidates = []
        if cmd_id:
            candidates.append(Path(DATA_ROOT) / "_cmd_errors" / f"{cmd_id}.md")
        candidates.append(Path(DATA_ROOT) / "_last_cmd_error.md")
        for p in candidates:
            if not p.is_file():
                continue
            text = p.read_text(encoding="utf-8", errors="replace")
            # _last_cmd_error.md 是「最近一筆」— 若不是本筆 cmd 就別誤導 caller
            if cmd_id and p.name == "_last_cmd_error.md" and cmd_id not in text:
                continue
            lines = text.splitlines()
            print("  ── Editor 端詳細錯誤報告 ──")
            for ln in lines[:max_lines]:
                print(f"  {ln}")
            if len(lines) > max_lines:
                print(f"  …（省略 {len(lines) - max_lines} 行）")
            print(f"  📄 完整報告：{p}")
            return
    except Exception:
        pass   # 報告只是加值，讀不到不該再蓋掉原始錯誤


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


# 區塊職責: op=post 沒帶 persona 時，根據登入 session lock 反推填入 persona。
# 物理意義: 發言身分 (persona) 是酒館顯示/帳務分流的關鍵欄位；agent 常漏帶 → Discord/酒館
#           顯示缺 persona。早安 ritual 寫 lock 時已記錄 (session_token / claim_origin / agent
#           → persona) 的對應，這裡走三段 fallback 反查未過期 lock，自動補上。
# 數值影響: 只在 persona 缺席時填入，不覆寫顯式值；反查失敗 graceful degrade（不擋發言）。
# fallback 鏈 (precise → loose，命中即止):
#   (1) session_token 精準匹配 — 最權威 (跨 env / 跨 ppid 都穩)
#   (2) claim_origin (env_hash) 匹配 — 同 env 多 persona 取 locked_at 最新
#   (3) agent marker 匹配 — claim_origin 不穩的 agent (e.g. Gemini env 落 unknown-<cwd>-<ppid>
#       fallback，ppid 每次 invoke 變 → (2) 對不上) 的救援；偵測 caller agent
#       (claude-code/gemini/antigravity)，online lock 中該 agent 恰好 1 個才填，多個 ambiguous 不猜。
def _autofill_persona_from_lock(arg_pairs: dict) -> None:
    # 已顯式帶 persona → 尊重不覆寫
    if (arg_pairs.get("persona") or "").strip():
        return
    try:
        # 重用 awakening 的 env-hash / lock helper，避免反查邏輯雙份漂移
        import importlib
        awk = importlib.import_module("awakening")
        # session dir 優先用 run_cmd 的 QUEUE_DIR（走 CLAUDE_PROJECT_DIR + git-walk，
        # 比 awakening._SESSION_DIR 的 cwd 敏感解析穩）；缺則 fallback awk 解析。
        session_dir = QUEUE_DIR / "_session"
        if not session_dir.exists():
            session_dir = awk._SESSION_DIR
        if not session_dir.exists():
            return
        # 一次載入所有未過期 lock（後續三段 fallback 共用）
        live_locks = []
        for lp in session_dir.glob("_persona_*.json"):
            try:
                with open(lp, "r", encoding="utf-8") as f:
                    lock = json.load(f)
            except Exception:
                continue
            if not awk.is_lock_expired(lock):
                live_locks.append(lock)
        if not live_locks:
            return

        chosen = None
        why = ""

        # (1) session_token 精準匹配
        want_token = (arg_pairs.get("session_token") or "").strip()
        if want_token:
            for lock in live_locks:
                if lock.get("session_token") == want_token:
                    chosen, why = lock, "session_token"
                    break

        # (2) claim_origin (env_hash) 匹配 — 多筆取最新
        if chosen is None:
            my_origin = awk.compute_claim_origin()
            origin_hits = [lk for lk in live_locks if awk.lock_claim_origin(lk) == my_origin]
            if origin_hits:
                chosen = max(origin_hits, key=lambda d: d.get("locked_at", ""))
                why = "claim_origin"

        # (3) agent marker 匹配 — claim_origin 不穩 agent 的救援；恰好 1 個才填
        if chosen is None:
            marker = (_detect_caller_env_marker() or "").lower()
            if marker and marker != "unknown":
                agent_hits = [lk for lk in live_locks
                              if (lk.get("agent") or "").lower() == marker
                              or (lk.get("agent") or "").lower().startswith(marker + "-")]
                if len(agent_hits) == 1:
                    chosen = agent_hits[0]
                    why = "agent-marker"
                elif len(agent_hits) > 1:
                    print(f"  ⚠ persona 自動反查：agent '{marker}' 有 {len(agent_hits)} 個 online "
                          f"persona，無法判定該填哪個（請顯式帶 --arg persona=...）", file=sys.stderr)

        if chosen is None:
            return
        persona = (chosen.get("persona") or "").strip()
        if persona:
            arg_pairs["persona"] = persona
            print(f"  ℹ persona 自動填入（反查 session lock，by {why}）：{persona}", file=sys.stderr)
    except Exception as e:
        # 反查任何環節出錯都不阻擋發言（degrade gracefully）
        print(f"  ⚠ persona 自動反查略過（{type(e).__name__}: {e}）", file=sys.stderr)


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
    # post 沒帶 persona → 反查登入 lock 自動補（防漏帶 persona，Tim 2026-05-27）
    if op == "post":
        _autofill_persona_from_lock(arg_pairs)
        # 保留 tag 的 meta schema 預檢（Tim 2026-07-28 拍板「錯誤資訊在發送流程就知道」）—
        # 鏡像 Cmd_Tavern T06.3 server 端驗證: 缺必填 meta 在 client 端 <0.01s 就擋,
        # 不必等 Editor round-trip 才在 ErrorLog 看到 RejectLastOp。
        ok, err = _validate_reserved_tag_meta(arg_pairs.get("meta") or "")
        if not ok:
            return False, err
    return True, ""


# 區塊職責: 保留 tag meta schema 表 — 鏡像 Cmd_Tavern.Op_Post 的 T06.3 驗證 (server 端為權威)。
# 物理意義: meta 格式 "k:v;k2:v2"; tag 命中保留字時要求對應必填 key。
# 數值影響: 新增保留 tag 時兩端同步擴表 (server: Cmd_Tavern.cs Op_Post; client: 本表)。
RESERVED_TAG_META_SCHEMA = {
    "task-assign": ["task_id", "task_body", "assigned_by", "requires_ack"],
    "task-ack": ["task_id", "action"],
}


def _validate_reserved_tag_meta(meta_raw: str) -> tuple[bool, str]:
    """解析 meta 字串, tag 為保留字時檢查必填 key。回 (ok, error_message)。"""
    if not meta_raw:
        return True, ""
    meta = {}
    for seg in meta_raw.split(";"):
        if ":" in seg:
            k, _, v = seg.partition(":")
            meta[k.strip()] = v.strip()
    tag = meta.get("tag", "")
    required = RESERVED_TAG_META_SCHEMA.get(tag)
    if not required:
        return True, ""
    missing = [k for k in required if not meta.get(k)]
    if missing:
        return False, (f"meta tag={tag} 為保留 tag (T06.3 schema), 缺必填 meta key: {missing}\n"
                       f"     ↳ required: {' / '.join(required)}（你目前 meta 帶的: {sorted(meta.keys())}）")
    if tag == "task-ack" and meta.get("action") not in ("accept", "decline", "defer"):
        return False, f"meta tag=task-ack 的 action 必須是 accept|decline|defer（目前: {meta.get('action')!r}）"
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

# QUEUE_DIR = canonical RPC 錨點 (queue.json / pending.trigger), 永遠在 RepoRoot/AgentCommands —
# 跟 C# UCL_RepoPath.AgentCommandsDir 對齊, 不跟資料搬。
QUEUE_DIR = GIT_ROOT / "AgentCommands"

# T-PATH-01 (2026-05-28): AgentCommands 資料根 pointer 檔解析
# 物理意義: C# 控制台 Apply 把絕對資料根寫到 <git-root>/.agentcommands_root.local;
#          本 helper 讀檔得實際資料根, 沒有 → 預設 GIT_ROOT/AgentCommands (與舊行為相同)。
# 數值影響: 跨語言 (C#/Python) 共讀同一檔, per-machine (gitignored)。
def _resolve_agentcommands_data_root(git_root: Path) -> Path:
    pointer = git_root / ".agentcommands_root.local"
    try:
        if pointer.exists():
            content = pointer.read_text(encoding="utf-8").strip()
            if content:
                p = Path(content)
                if p.is_absolute():
                    return p.resolve()
    except Exception:
        pass
    return (git_root / "AgentCommands").resolve()

# DATA_ROOT = 可 override 的資料根 (給 ChatTavern / _last_op.md / etc.); 預設 = QUEUE_DIR。
DATA_ROOT = _resolve_agentcommands_data_root(GIT_ROOT)

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

# ===========================================================
# False-success race fix (T02, 2026-05-16 basecamp)
# 區塊職責: cmd 失敗時 C# 端 auto-remove cmd 跑得比 Python 輪詢快 →
#         Python 看到 cmd is None 誤判 success. 本表 + check helper 補 detection.
# 物理意義: 各 cmd_type 寫對應 last_op 檔案; cmd 跑完後 check 該檔 mtime + 內容
#         若 mtime 在 submit 之後且第一行以 ❌ / Failed 開頭 → 報失敗
# 數值影響: cmd is None 路徑前加 fail-detection 分支; 不破壞既有 success path
# ===========================================================
CMD_OUTPUT_FILES = {
    "tavern": "ChatTavern/_last_op.md",
    "treasury": "ChatTavern/_last_op.md",
    "notelesson": "Lessons/_last_lesson.md",
}

def check_cmd_result_file(cmd_type: str, mtime_threshold: float, cmd_id: str | None = None):
    """檢查指定 cmd_type 的 last_op 檔案是否顯示失敗。

    Returns:
        ("failed", err_msg) — 確認失敗（檔有更新 + 開頭含 fail marker）
        ("success", "") — 確認成功（檔有更新 + 開頭含 success marker）
        ("unknown", "") — 無法確認（檔不存在 / mtime 太舊 / 沒有明確 marker / cmd_id 不符）

    僅在 ("failed", ...) 時 caller 應該報失敗; ("unknown", ...) 維持原有 success 推測
    （與舊版行為兼容, 不引入新的偽陽性）。

    cmd_id (T-LastOp-CmdId, 2026-06-12): C# 端 Runner 執行 queue cmd 時會在 last_op 檔
    第二行 stamp `<!-- cmd_id: X -->`。多 session 並發對同一 Editor 發 cmd 時，mtime 在
    submit 之後不代表是本 process 的 cmd 寫的 — 另一 chat 的 cmd 在同窗口失敗會污染判定
    （實證: 2026-06-12 21:27 kiara post 成功被 gura chat 的 T07 fail marker 誤報 exit 2）。
    傳入 cmd_id 且檔內 stamp 存在但不符 → 回 ("unknown", "")（別人的結果，不認帳）。
    檔內無 stamp（舊版 Editor 還沒 stamp）→ 走 legacy mtime-only 判定，向後相容。
    """
    rel_path = CMD_OUTPUT_FILES.get(cmd_type.lower())
    if not rel_path:
        return ("unknown", "")
    path = QUEUE_DIR / rel_path
    if not path.exists():
        return ("unknown", "")
    try:
        mtime = path.stat().st_mtime
    except OSError:
        return ("unknown", "")
    # mtime 必須 >= submit time（容忍 1s 時鐘漂移）才視為「本次 cmd 寫的」
    if mtime < mtime_threshold - 1.0:
        return ("unknown", "")
    try:
        with path.open("r", encoding="utf-8", errors="replace") as f:
            # 只讀前 4KB 足夠抓首行 + 簡短錯誤訊息, 避免讀超大檔
            head = f.read(4096)
    except OSError:
        return ("unknown", "")
    # T-LastOp-CmdId (2026-06-12): 比對檔內 cmd_id stamp — stamp 存在但不是本次 cmd 寫的
    # → 一律 unknown（fail / success 都不認帳），擋多 session 並發污染誤報
    if cmd_id:
        stamp_match = re.search(r"<!--\s*cmd_id:\s*(\S+)\s*-->", head)
        if stamp_match and stamp_match.group(1) != cmd_id:
            return ("unknown", "")
    first_line = head.split("\n", 1)[0].strip()
    # Fail markers — 對齊 Cmd_Tavern / Cmd_Treasury / Cmd_NoteLesson 寫法
    if first_line.startswith("# ❌") or "Cmd Failed" in first_line or "Cmd failed" in first_line:
        # 抓前 5 行當錯誤訊息（濾掉 cmd_id stamp 行 — 那是機器比對用，不是錯誤內容）
        err_lines = head.split("\n", 6)[1:5]
        err_msg = "\n  ".join(L.strip() for L in err_lines
                              if L.strip() and not re.match(r"<!--\s*cmd_id:", L.strip()))
        return ("failed", err_msg or first_line)
    if first_line.startswith("# ✅"):
        return ("success", "")
    return ("unknown", "")

# Legacy module-level constants — kept for any external import; dynamic versions above are canonical.
QUEUE_PATH = QUEUE_DIR / "queue.json"
TRIGGER_PATH = QUEUE_DIR / "pending.trigger"
RUNNING_PATH = QUEUE_DIR / "pending.trigger.running"
TAVERN_DIR = DATA_ROOT / "ChatTavern"  # T-PATH-01: 走可 override 資料根;預設 = QUEUE_DIR/ChatTavern (與舊行為相同)
# 區塊職責：酒館同步握手（wait-reply）+ 酒保 NPC + per-message 讀取層 → 已抽離到兄弟模組
# 物理意義：run_cmd 的職責是「送 cmd 進 Unity 佇列並等它跑完」；「發完訊息後在 client 端等回覆」
#          不碰佇列也不進 Editor，是另一件事。混在一檔會讓本檔膨脹到難維護
#          （Tim 2026-07-29 拍板抽離，本檔 1860 → 1314 行）。旗標檔與台詞庫路徑一併搬過去。
# 數值影響：模組不自行解析資料根 —— 此處 configure() 注入 TAVERN_DIR / GIT_ROOT。
#          override 規則（T-PATH-01）只存在於本檔，複製一份到那邊必然漂移。
import tavern_handshake as _handshake   # 同層模組；run_cmd 以 script 執行時其所在夾即 sys.path[0]

_handshake.configure(tavern_dir=TAVERN_DIR, git_root=GIT_ROOT)
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
    # 區塊職責：atomic + retry 寫入 queue.json，避免與 Editor 端 Watcher 並發改寫撞檔鎖
    # 物理意義：Windows 檔鎖為強制性 — 直接 open("w") 會先 truncate，期間若 Editor consumer
    #           正在讀/改寫同一檔 → OSError [Errno 22] / sharing violation，整條 Cmd dispatch 失效
    # 數值影響：改成「寫 temp → os.replace」近乎 atomic 的 rename（取代 truncate-in-place），
    #           撞鎖則 backoff 重試 5 次（0.1s→0.5s），全失敗才拋出原錯讓 caller 看到真因
    qp = queue_path()
    queue_dir_for_writing().mkdir(parents=True, exist_ok=True)
    payload = json.dumps(data, indent=2, ensure_ascii=False) + "\n"
    # temp 檔帶 pid 後綴：多個 run_cmd.py 並行時各自獨立，不互相覆寫
    tmp = qp.with_name(f"{qp.name}.tmp{os.getpid()}")
    last_err: OSError | None = None
    for attempt in range(5):
        try:
            # 先把完整內容寫進 temp（即使這步被打斷，也不會破壞既有 queue.json）
            with open(tmp, "w", encoding="utf-8") as f:
                f.write(payload)
            # rename 覆蓋目標：同碟近乎 atomic；目標被 Editor 鎖住時拋 PermissionError(OSError 子類)
            os.replace(tmp, qp)
            return
        except OSError as e:
            last_err = e
            # 線性 backoff，給 Editor 端釋放檔鎖的時間窗
            time.sleep(0.1 * (attempt + 1))
    # 全部重試失敗 → 清掉殘留 temp，避免污染目錄，再拋出最後一次的原始錯誤
    try:
        if tmp.exists():
            tmp.unlink()
    except OSError:
        pass
    raise last_err


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

    # --arg-file 展開（讀檔失敗 → 直接擋，不寫 queue）
    ok, err = expand_arg_files(arg_pairs, getattr(args, "arg_file", []))
    if not ok:
        print_fail_verdict(f"✗ --arg-file 展開失敗：\n  {err}")
        return 2

    # --arg-stdin 展開（讀 stdin 全文塞進指定 key；見 expand_arg_stdin 區塊註解）
    ok, err = expand_arg_stdin(arg_pairs, getattr(args, "arg_stdin", None))
    if not ok:
        print_fail_verdict(f"✗ --arg-stdin 展開失敗：\n  {err}")
        return 2

    # 區塊職責：Tavern Cmd 的 client-side 預檢
    # 物理意義：Editor round-trip 約 1s 才報錯；client 預檢 < 0.01s 就能擋下 typo / alias 錯
    # 數值影響：失敗 → 立刻 return 2 不寫 queue，不污染 _last_op.md
    if args.cmd_type == "Tavern":
        ok, err = validate_tavern_args(arg_pairs)
        if not ok:
            print_fail_verdict(f"✗ Tavern client-side 預檢失敗：\n  {err}")
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
                # T02 race fix (2026-05-16 basecamp): C# 端 fail 後 auto-remove
                # cmd 比 Python 輪詢快 → cmd is None 不等於 success.
                # 解法: 檢 cmd_type 對應 last_op 檔案的 fail marker.
                cmd_type = getattr(args, "cmd_type", None)
                submit_time = getattr(args, "submit_time", None)
                if cmd_type and submit_time is not None:
                    # T-LastOp-CmdId: 帶本筆 cmd_id 進去比對檔內 stamp — 別的 session
                    # 同窗口寫的 fail marker（stamp 是別人的 id）會被判 unknown 不誤報
                    status, err = check_cmd_result_file(cmd_type, submit_time, cmd_id=cmd_id)
                    if status == "failed":
                        print_fail_verdict(f"  ✗ Cmd disappeared from queue BUT output file shows failure:\n  {err}")
                        print_cmd_error_report(cmd_id)
                        return 2
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
                print_fail_verdict(f"  ✗ Cmd failed: {err}")
                print_cmd_error_report(cmd.get("Id"))
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

    print_fail_verdict(f"  ✗ Timeout after {timeout_sec}s — Editor not running, "
                       f"or UCL_AgentCommandWatcher disabled?")
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

    # --arg-file 展開（讀檔失敗 → 直接擋，不寫 queue）
    ok, err = expand_arg_files(arg_pairs, getattr(args, "arg_file", []))
    if not ok:
        print_fail_verdict(f"✗ --arg-file 展開失敗：\n  {err}")
        return 2

    # --arg-stdin 展開（讀 stdin 全文塞進指定 key；見 expand_arg_stdin 區塊註解）
    ok, err = expand_arg_stdin(arg_pairs, getattr(args, "arg_stdin", None))
    if not ok:
        print_fail_verdict(f"✗ --arg-stdin 展開失敗：\n  {err}")
        return 2

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
            print_fail_verdict(f"✗ Tavern client-side 預檢失敗：\n  {err}")
            return 2

    ensure_idle(timeout_sec=args.ack_timeout, poll_interval=args.poll_interval)
    # T02 race fix (2026-05-16): 記 submit time, cmd_wait 用此判 last_op 檔 mtime 是不是本次寫的
    submit_time = time.time()
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
        # T02 race fix (2026-05-16): 帶 cmd_type + submit_time 給 cmd_wait 做
        # fail-detection (cmd_is_None 路徑檢對應 last_op 檔)
        cmd_type=args.cmd_type,
        submit_time=submit_time,
    )
    rc = cmd_wait(wait_args)
    if rc != 0:
        return rc

    # ─── T28 work-mode banner plumb to caller stdout (crest-001 QA 2026-05-14) ─────
    # 區塊職責: Cmd success 後抓 _last_op.md 內的 work-mode banner section 印到 caller stdout
    # 物理意義: Op_Post 寫 banner 到 _last_op.md, 但 caller 沒人讀那檔. wait-reply 是讀
    #          messages.jsonl 等對方 reply (T38 後 jsonl 不存在直接 short-circuit).
    #          兩條路都沒接到 banner → caller 看不到 work-session hint.
    # 數值影響: 純 stdout print, 不擋 wait-reply 主流程
    if args.cmd_type.lower() == "tavern" and arg_pairs.get("op", "").lower() == "post":
        try:
            # 讀 room-level _last_view.md (不是全局 _last_op.md, 後者會被其他 cmd 覆蓋)
            _room = arg_pairs.get("room", "")
            last_op_path = TAVERN_DIR / "rooms" / _room / "_last_view.md" if _room else None
            if last_op_path and last_op_path.exists():
                last_op_content = last_op_path.read_text(encoding="utf-8")
                # 抓 work-session banner (以 "⏰ **work-session active**" 開頭, 到下個 markdown header 或 EOF)
                import re as _re
                banner_match = _re.search(
                    r"⏰ \*\*work-session active\*\*[^\n]*\n[🎯💸⛔📋🚫💭💬][^\n]*",
                    last_op_content,
                )
                if banner_match:
                    print("\n──── 上班 hint ────")
                    print(banner_match.group(0))
                    print("───────────────────")
        except Exception as _banner_e:
            # fail-swallow, 不擋主流程
            pass

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
        # 區塊職責：判決碼往上傳 —— 舊版把 wait_for_tavern_reply() 的回傳直接丟掉
        # 物理意義：post 成功與「等待有沒有真的發生」是兩件事實，不能揉成同一個 exit code；
        #          但也不能像舊版那樣整個吞掉，否則修好了判決碼也傳不出這層。
        # 數值影響：正常結局（收到 / timeout / 使用者中止）→ 行程 exit 0，因為 post 本身成功；
        #          只有 3（無法判定）→ exit 3，因為「你要我等，我結構性等不成」是**被要求的操作失敗**，
        #          必須讓 caller 的錯誤處理看得到。這是「同碼失聲」的解藥：
        #          可正常結束的三種結局共用 0，唯獨「沒做到」自己一個碼。
        if not room or not my_sender:
            print(
                "  ⚠ wait-reply 無法運作 — 缺 room / sender\n"
                "     ⛔ 本次**完全沒有等待**（判決碼 3 = 無法判定，不是 timeout）\n"
                "     → 補上 --arg room=<X> --arg sender=<id> 再試"
            )
            return _handshake.WAIT_REPLY_UNAVAILABLE
        verdict = _handshake.wait_for_tavern_reply(
            room=room,
            my_sender_id=my_sender,
            timeout_sec=wait_seconds,
            sender_filter=sender_filter,
        )
        # 機器可讀的一行結論 — 讓 caller / 日後的自動檢查不必去解析人話
        verdict_name = _handshake._WAIT_REPLY_VERDICT_NAME.get(verdict, verdict)
        print(f"  [wait-reply] verdict={verdict_name} code={verdict}")
        if verdict == _handshake.WAIT_REPLY_UNAVAILABLE:
            return _handshake.WAIT_REPLY_UNAVAILABLE
    return 0




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
# Backtick-loss guard（T-Backtick-Guard, Tim 2026-06-12 拍板）
# ===========================================================
# 區塊職責：把 stdin 全文塞進指定的 arg key（--arg-stdin body <<'EOF' … EOF）。
# 物理意義：body 完全不經過 argv/shell 引用 —— 反引號、$、引號、換行都不會被 shell 解讀。
#          這不是「比較安全」，是**沒有出錯的物理路徑**（crest-001 三審用語）。
#          取代 2026-07-29 移除的 backtick-loss guard：與其在下游偵測污染，不如在上游關掉污染管道。
# 跨 shell 注意：heredoc 只在 Bash 類 shell 可用；**PowerShell 沒有 heredoc 管線等價寫法**
#          → PowerShell 環境請改用 --arg-file（先寫檔再讀）。
# 數值影響：stdin 為空 → 視為錯誤（避免靜默送出空 body）；只允許指定一個 key（stdin 單一串流）。
def expand_arg_stdin(arg_pairs: dict, key: "str | None") -> tuple[bool, str]:
    if not key:
        return True, ""
    if sys.stdin is None or sys.stdin.isatty():
        return False, (f"--arg-stdin {key} 需要從 stdin 餵內容，但 stdin 是終端機（沒有 pipe/heredoc）。\n"
                       f"  Bash: --arg-stdin {key} <<'EOF' … EOF   /   PowerShell: 改用 --arg-file {key}=<path>")
    data = sys.stdin.read()
    if not data.strip():
        return False, f"--arg-stdin {key} 讀到空內容（stdin 沒有資料）。"
    arg_pairs[key] = data.rstrip("\n")
    return True, ""


def expand_arg_files(arg_pairs: dict, arg_file_items: list[str]) -> tuple[bool, str]:
    """--arg-file key=path → 讀檔內容塞進 arg_pairs[key]（UTF-8）。回 (ok, err)。"""
    for item in (arg_file_items or []):
        if "=" not in item:
            return False, f"--arg-file 格式錯誤（缺 =）: {item}"
        k, path = item.split("=", 1)
        p = Path(path.strip())
        if not p.is_file():
            return False, f"--arg-file {k.strip()}: 檔案不存在 → {p}"
        try:
            arg_pairs[k.strip()] = p.read_text(encoding="utf-8")
        except Exception as e:
            return False, f"--arg-file {k.strip()}: 讀檔失敗 → {e}"
    return True, ""


# ===========================================================
# CLI
# ===========================================================

def add_common_submit_args(p: argparse.ArgumentParser) -> None:
    p.add_argument("--mode", default="OneShot", choices=["OneShot", "Repeatable"])
    p.add_argument("--arg", action="append", default=[],
                   help="Arg as key=value (repeatable)")
    p.add_argument("--arg-file", action="append", default=[],
                   help="Arg as key=<filepath> — 值從檔案讀 (UTF-8, repeatable)。"
                        "PowerShell 環境傳長文/含反引號內容的推薦通道（PS 無 heredoc）。")
    p.add_argument("--arg-stdin", default=None, metavar="KEY",
                   help="把 stdin 全文當成該 arg 的值。Bash 傳長文的首選："
                        "--arg-stdin body <<'EOF' … EOF —— body 不經 argv，shell 元字符一律不解讀。")
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
    # agent-command-pipeline-parallelize T06: 同 persona 內並行子通道
    # 物理意義: 同一 --agent-id (或 default) 的 queue 是串行的 (per-agent IsRunning 防 write race);
    #          --lane 在其上疊一條獨立子通道 → effective id = '<base|main>~<lane>' → 與 base queue 並行不阻塞。
    # 用途: 前一筆長 cmd (e.g. 啟動遊戲) 還在跑, 帶 --lane 送讀畫面等快 cmd 不必等它結束。
    parser.add_argument("--lane", default=None,
                        help="同 persona 並行子通道。effective queue id = '<agent-id|main>~<lane>' → 獨立 queue/running-lock, "
                             "與 base / default queue 並行不阻塞。前一筆長 cmd 沒跑完時插一筆快 cmd 用。")
    parser.add_argument("--parallel", action="store_true",
                        help="= --lane parallel 的捷徑 (固定 'parallel' 子通道)。")
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
    # agent-command-pipeline-parallelize T06: --lane / --parallel 在 base agent-id 上疊並行子通道
    # 物理意義: lane 設定 → effective id = '<base|main>~<lane>' → 獨立 queue/running-lock, 與 base/default 並行不阻塞
    # 數值影響: 無 lane → 行為跟改動前完全相同 (effective = base agent_id)
    _base_agent = getattr(args, "agent_id", None)
    _lane = getattr(args, "lane", None) or ("parallel" if getattr(args, "parallel", False) else None)
    _effective_agent = f"{_base_agent or 'main'}~{_lane}" if _lane else _base_agent
    # 設 global _AGENT_ID 讓 queue_path/trigger_path 等函式拿 dynamic value
    set_agent_id(_effective_agent)
    return args.func(args)


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
