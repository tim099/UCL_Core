#!/usr/bin/env python3
"""
work_session.py — 上班時間 Session Prototype CLI (Phase 1 manual mode)

依據: docs/Plan/Plan_Work_Session_Mechanism.md (Tim 2026-05-13 拍板 10+2-token spec).
Phase 1 honor mode: agent 手動 orchestrate session lifecycle; daemon 化 Phase 2 backlog.

子命令:
  start --manager <persona> [--workers w1,w2,...] [--duration N] [--desc "..."]
              開新 session, 寫 state, 走 tavern_post 酒保 start announcement.

  status      列當前 active sessions + tasks + counters (read-only).

  assign --session <id> --assigner <manager> --to <worker> --desc "..." [--weight light|medium|heavy]
              主管派 task 給同事.

  accept --session <id> --task-id <wt-N> --accepter <worker>
              同事接 task.

  done --session <id> --task-id <wt-N> [--ref <commit/file/...>]
              同事完成 task.

  end --session <id>
              結束 session, 結算薪資 (2 token/min/participant) + 累積酒館券 (1 張/5min/persona)
              + 走 tavern_post 酒保 end announcement.

範例:
  python work_session.py start --manager basecamp --workers meadow,apex-two --duration 60
  python work_session.py assign --session ws-... --assigner basecamp --to meadow --desc "重構 X" --weight medium
  python work_session.py done --session ws-... --task-id wt-001 --ref "commit:abc123"
  python work_session.py end --session ws-...

設計 note: Phase 1 不偵測 Tim trigger keyword (那是 daemon 工作), agent 看到 Tim 講上班就手動 start.
"""

from __future__ import annotations
import argparse
import datetime
import json
import os
import sys
import time
import subprocess
import uuid
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

_HERE = Path(__file__).resolve().parent


# 區塊職責: 從 UCL_Core/Tools~/AgentCommands/ 推 consumer-project repo root.
# 物理意義: UCL_Core 是 git submodule, 跨專案共用; state files (sessions / quota /
#          ledger / personas / audit) 在 consumer 主專案 cwd. 三層 fallback 推 REPO_ROOT
#          (與 awakening.py _resolve_repo_root 對齊).
def _find_git_root_by_walk(start: Path) -> Path | None:
    p = start.resolve()
    while p != p.parent:
        if (p / ".git").exists():   # is_dir for repo, is_file for submodule .git
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
    # Skip walking from _HERE — that hits UCL_Core .git first (wrong layer).
    # Fall back to cwd as last resort.
    return Path.cwd().resolve()


_REPO_ROOT = _resolve_repo_root()

_SESSIONS_PATH = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "work_sessions.json"
_QUOTA_PATH = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "agent_bonus_quota.json"
_LEDGER_ROOT = _REPO_ROOT / "AgentCommands" / "Treasury" / "ledger"
_PERSONAS_DIR = _REPO_ROOT / "AgentCommands" / "AwakenInit" / "personas"
# run_cmd.py is sibling — UCL_Core/Tools~/AgentCommands/run_cmd.py.
_RUN_CMD = _HERE / "run_cmd.py"

AGENT_TO_BANK = {
    "claude-code": "claude-da-xiaojie",
    "antigravity": "antigravity-da-xiaojie",
    "Zeta": None,
}

SALARY_RATE_PER_MIN = 2          # tokens per minute
VOUCHER_INTERVAL_MIN = 5         # 1 voucher per N minutes


# ─── utilities ──────────────────────────────────────────────────────────
def utcnow_iso() -> str:
    n = datetime.datetime.utcnow()
    return n.strftime("%Y-%m-%dT%H:%M:%S.") + f"{n.microsecond // 1000:03d}Z"


def utcnow_compact() -> str:
    return datetime.datetime.utcnow().strftime("%Y%m%dT%H%M%SZ")


def parse_iso(ts: str) -> datetime.datetime:
    """Parse ISO 8601 to datetime (UTC naive)."""
    if ts.endswith("Z"):
        ts = ts[:-1]
    return datetime.datetime.fromisoformat(ts)


def short_uuid(n: int = 4) -> str:
    return uuid.uuid4().hex[:n]


# ─── Persona resolution ─────────────────────────────────────────────────
def resolve_persona(name: str) -> dict | None:
    """Find persona record by name, return dict with agent / bank / status. None if not found."""
    f = _PERSONAS_DIR / f"{name}.json"
    if not f.exists():
        return None
    try:
        p = json.loads(f.read_text(encoding="utf-8"))
    except Exception:
        return None
    agent = p.get("agent", "")
    bank = AGENT_TO_BANK.get(agent)
    return {
        "persona": name,
        "agent": agent,
        "bank": bank,
        "status": p.get("status", "?"),
    }


def infer_caller_persona() -> str | None:
    """
    T09 (Tim 2026-05-14 拍板, C1) — 從當前 caller 環境推 active persona.
    T13 (2026-05-14, 主管裁決) — tie-breaker 從 locked_at 改 persona.last_active.

    用途: cmd_start 不傳 --manager 時自動填補 caller 自己當主管, 減手填參數.

    機制:
      1. import awakening.compute_claim_origin → 算當前 env hash
      2. scan _session locks, 找 claim_origin match 的 lock list
      3. 多 match → 取 persona.last_active 最 recent 的 (T13 修正)
         (舊規則 locked_at 抓不到「stale fork persona 早上 lock 之後沒動」, 會誤推 stale persona 當主管)
      4. 匹不到 → None (caller 自己處理 fallback)

    為何改: live test 抓到 — calli stale fork lock_at 比 basecamp 晚, 但 calli
    幾天沒 last_active, basecamp 才是「當前真在用」. last_active 更貼近語意.
    """
    try:
        sys.path.insert(0, str(Path(__file__).parent))
        from awakening import compute_claim_origin, lock_claim_origin, is_lock_expired   # type: ignore
    except Exception:
        return None
    my_origin = compute_claim_origin()
    session_dir = _REPO_ROOT / "AgentCommands" / "_session"
    if not session_dir.exists():
        return None
    candidates = []
    for lp in session_dir.glob("_persona_*.json"):
        try:
            d = json.loads(lp.read_text(encoding="utf-8"))
        except Exception:
            continue
        if is_lock_expired(d):
            continue
        if lock_claim_origin(d) == my_origin:
            # T13: 配 persona last_active 從 registry 撈出來當 sort key
            persona_name = d.get("persona", "")
            last_active = ""
            if persona_name:
                p_info = resolve_persona(persona_name)
                # resolve_persona 沒抓 last_active, 直接讀 file
                p_file = _PERSONAS_DIR / f"{persona_name}.json"
                if p_file.exists():
                    try:
                        p_data = json.loads(p_file.read_text(encoding="utf-8"))
                        last_active = p_data.get("last_active") or ""
                    except Exception:
                        pass
            d["_last_active"] = last_active
            candidates.append(d)
    if not candidates:
        return None
    # T13: 多 match → 取最 recent last_active (fallback locked_at if last_active 空)
    candidates.sort(key=lambda d: (d.get("_last_active") or "", d.get("locked_at", "")), reverse=True)
    return candidates[0].get("persona")


def list_online_personas() -> list[str]:
    out = []
    for f in sorted(_PERSONAS_DIR.iterdir()):
        if not f.name.endswith(".json") or f.name.startswith("_"):
            continue
        try:
            p = json.loads(f.read_text(encoding="utf-8"))
        except Exception:
            continue
        if p.get("status") == "online":
            out.append(f.stem)
    return out


# ─── Hardening primitives (Zeta 2026-05-13 task — protect work flows) ───
# 區塊職責: atomic state write + audit log + idempotency guard helpers
# 物理意義: prototype 從「ad-hoc 寫」升級到「atomic + idempotent + auditable」
# 數值影響: 不改 schema, 純 IO safety net

def atomic_write_json(path: Path, data: dict) -> None:
    """
    Atomic JSON write — write to tmp file, fsync, rename over target.
    os.replace 在 POSIX + Windows 都是 atomic (Python 3.3+).
    防 partial-write 撞 Editor recompile / 系統中斷導致 state corruption.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.flush()
        try:
            os.fsync(f.fileno())
        except OSError:
            pass  # fsync 不支援某些 fs 時 silent skip
    os.replace(tmp, path)


def append_audit(session_id: str, event: str, payload: dict) -> None:
    """
    Append audit event to session-scoped jsonl. 給 settlement / failure 追溯用.
    路徑: AgentCommands/ChatTavern/work_session_audit/<session_id>.jsonl
    """
    audit_dir = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "work_session_audit"
    audit_dir.mkdir(parents=True, exist_ok=True)
    log_path = audit_dir / f"{session_id}.jsonl"
    entry = {"ts": utcnow_iso(), "event": event, **payload}
    try:
        with open(log_path, "a", encoding="utf-8") as f:
            f.write(json.dumps(entry, ensure_ascii=False) + "\n")
    except Exception as e:
        print(f"⚠ audit log fail ({log_path.name}): {e}", file=sys.stderr)


# ─── State I/O ──────────────────────────────────────────────────────────
def load_state() -> dict:
    if not _SESSIONS_PATH.exists():
        return {
            "_schema_version": 1,
            "_description": "Work session (上班時間) active + history. Phase 1 manual mode (Cmd_WorkSession daemon impl pending Phase 2).",
            "_canonical_doc": "docs/Plan/Plan_Work_Session_Mechanism.md",
            "active_sessions": [],
            "history": [],
        }
    return json.loads(_SESSIONS_PATH.read_text(encoding="utf-8"))


def save_state(state: dict) -> None:
    """Atomic save — 防 partial-write corruption (Zeta hardening task 2026-05-13)."""
    atomic_write_json(_SESSIONS_PATH, state)


def mutate_state(mutator):
    """
    Race-safe Read-Modify-Write for work_sessions.json (meadow ws-...e9e6 retrofit
    2026-05-13). Wraps load → mutate → save 進 atomic_rmw 的 file lock 內，關掉 TOCTOU
    pattern (basecamp's `is_persona_in_any_active_session` check 跟 worker append 之
    間若另一 process 並發寫 → false negative). 同 callers 應走本 helper, 不再裸用
    load_state + save_state pair.

    用法:
        def add_worker_safely(session_id, persona):
            def m(s):
                ses = next((x for x in s.get('active_sessions',[]) if x['id']==session_id), None)
                if ses: ses.setdefault('workers',[]).append({'persona':persona})
                return s
            mutate_state(m)

    返回: 最終 state dict (post-mutation, 已寫進檔).
    """
    # Late import — keep top-of-file lean, atomic_rmw 只在用到時 load.
    # _lib lives next to this script in UCL_Core/Tools~/AgentCommands/_lib/.
    sys.path.insert(0, str(_HERE))
    from _lib.json_io import atomic_rmw

    default = {
        "_schema_version": 1,
        "_description": "Work session (上班時間) active + history. Phase 1 manual mode.",
        "_canonical_doc": "docs/Plan/Plan_Work_Session_Mechanism.md",
        "active_sessions": [],
        "history": [],
    }
    return atomic_rmw(_SESSIONS_PATH, mutator, default=default)


def find_active_session(state: dict, session_id: str) -> dict | None:
    for s in state.get("active_sessions", []):
        if s["id"] == session_id:
            return s
    return None


# ─── Tavern post helper ─────────────────────────────────────────────────
def tavern_post(sender_id: str, body: str, meta: dict, persona: str = "") -> bool:
    """Post to tavern via Cmd_Tavern. Returns True if cmd accepted (we don't wait)."""
    import subprocess
    cmd = [
        sys.executable, str(_RUN_CMD), "run", "Tavern",
        "--arg", "op=post",
        "--arg", "room=tavern",
        "--arg", f"sender_id={sender_id}",
        "--arg", f"body={body}",
        "--arg", f"meta={json.dumps(meta, ensure_ascii=False)}",
    ]
    if persona:
        cmd.extend(["--arg", f"persona={persona}"])
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", timeout=120)
        return r.returncode == 0
    except Exception as e:
        print(f"⚠ tavern_post fail: {e}", file=sys.stderr)
        return False


# ─── Treasury ledger entry (direct write, Phase 1 bypass Cmd_Treasury) ──
def fire_salary_credit(bank: str, persona: str, amount: int, session_id: str, checkpoint: str) -> str:
    """Write a credit ledger entry. Returns the file path."""
    ts = utcnow_iso()
    n = datetime.datetime.utcnow()
    fname = f"{n.strftime('%H%M%S')}_{n.microsecond // 1000:03d}_{short_uuid()}__credit.json"
    date_dir = _LEDGER_ROOT / n.strftime("%Y-%m-%d")
    date_dir.mkdir(parents=True, exist_ok=True)
    entry = {
        "ts": ts,
        "uuid": short_uuid(6),
        "type": "credit",
        "amount": amount,
        "currency": "tavern_token",
        "account_id": bank,
        "source_kind": "work_session_salary",
        "source_ref": f"ws:{session_id}:{checkpoint}:{persona}",
        "source_description": f"上班 session 薪資 — {persona} {checkpoint}",
        "balance_before": None,
        "balance_after": None,
        "sig_agent_id_claimed": "system",
        "sig_process_id": str(os.getpid()),
        "sig_env_marker": "work_session_prototype",
        "sig_cmd_id": "",
        "signature_mismatch": False,
    }
    path = date_dir / fname
    path.write_text(json.dumps(entry, ensure_ascii=False, indent=2), encoding="utf-8")

    # T29 fix (Tim QA 2026-05-14): fire-and-forget Discord broadcast via notify_treasury.py
    # 物理意義: 對齊 C# UCL_TreasuryLedger.Credit FireDiscordBroadcastAsync — work_session.py
    #          直接寫 ledger 檔不會走 C# path, 需手動補 spawn broadcast subprocess.
    # 數值影響: 純 fire-and-forget Popen, 不擋 salary 主流程; notify_treasury.py 不存在 silent skip.
    try:
        notify_path = _REPO_ROOT / "AgentCommands" / "PromptQueue" / "notify_treasury.py"
        if notify_path.exists():
            subprocess.Popen(
                [sys.executable, str(notify_path), "--entry-file", str(path), "--quiet"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                cwd=str(_REPO_ROOT),
            )
    except Exception:
        pass   # silent skip

    return str(path.relative_to(_REPO_ROOT))


# ─── Voucher accrual (per-persona, post v2 migration) ───────────────────
def fire_voucher_accrual(bank: str, persona: str, amount: int, session_id: str) -> None:
    """Add voucher to agent_bonus_quota.json under <bank>.personas.<persona>."""
    if not _QUOTA_PATH.exists():
        return
    q = json.loads(_QUOTA_PATH.read_text(encoding="utf-8"))
    agents = q.setdefault("agents", {})
    agent_block = agents.setdefault(bank, {"personas": {}, "_legacy_no_persona": {"total_remaining": 0, "history": []}})
    personas = agent_block.setdefault("personas", {})
    persona_block = personas.setdefault(persona, {"total_remaining": 0, "history": []})

    # Append history entry
    persona_block.setdefault("history", []).append({
        "id": f"ws-{session_id}-voucher-{persona}",
        "granted_at": utcnow_iso(),
        "granted_by": "system (work_session)",
        "kind": "tavern_voucher",
        "amount": amount,
        "used": 0,
        "remaining": amount,
        "expires": None,
        "source_session": session_id,
        "usage_summary": "(work session 累積, prototype phase)",
    })
    persona_block["total_remaining"] = persona_block.get("total_remaining", 0) + amount

    _QUOTA_PATH.write_text(json.dumps(q, ensure_ascii=False, indent=2), encoding="utf-8")


# ─── Subcommand: start ──────────────────────────────────────────────────
def cmd_start(args) -> int:
    state = load_state()
    # T09 C1 (Tim 2026-05-14 拍板) — auto-manager fallback
    # 物理意義: 不傳 --manager → 從 caller env 推 active persona 自動填主管
    # 行為: 「上班 10 分鐘」一句話 caller 即主管, 不必填表
    if not getattr(args, "manager", "") or not args.manager.strip():
        inferred = infer_caller_persona()
        if not inferred:
            print("❌ --manager 不傳時必須能從 caller env 推 active persona (claim_origin lock)")
            print("   解法: 先跑 awakening.py morning 上線, 或顯式傳 --manager <persona>")
            return 1
        args.manager = inferred
        print(f"✓ auto-manager: 從 caller env 推得當前 persona '{inferred}' 為主管 (C1 T09)")

    # Zeta caretaker stability fix (split-brain 預防): manager 不可同時在 active session
    in_session, where = is_persona_in_any_active_session(state, args.manager)
    if in_session:
        print(f"❌ split-brain 預防: @{args.manager} 已在 active session {where}, 不可同時為 manager 開新 session")
        print(f"   選項: (1) 等該 session 結束 (2) --force-split 強制 (待加) (3) 改派別人為 manager")
        return 1
    manager_info = resolve_persona(args.manager)
    if not manager_info:
        print(f"❌ manager persona '{args.manager}' 不存在於 persona_registry")
        return 1
    if manager_info["bank"] is None:
        print(f"❌ manager '{args.manager}' (agent={manager_info['agent']}) 沒對應 bank account")
        return 1

    # Resolve workers — T11 (Tim 2026-05-14 拍板, C4) 改三態語意:
    #   args.workers is None → 沒傳 --workers → **SOLO 預設** (per ding-ack auto-recruit 新流程)
    #   args.workers == ""   → 顯式 SOLO (同 None, 留向後相容)
    #   args.workers == csv  → 顯式列名 (caretaker 模式)
    #
    # 為何改: T10 C3 ship 後員工招募走「ding-ack 自動入職」, 不必 start 時預先指定
    # workers. 舊 auto-include online 非 manager 邏輯反而會把「不該被自動拉進」的
    # online persona (e.g. apex-one Antigravity chat 沒開但有 lock) 拉成 worker.
    # 改 SOLO 預設 — 員工由 Tim 叮 + ack 自然進場 (per Tim 早上口袋掏招待券掐板).
    if args.workers is None or args.workers == "":
        worker_names = []   # SOLO 預設 (T11 C4)
    else:
        worker_names = [w.strip() for w in args.workers.split(",") if w.strip()]
    # Hard guard: manager 不可同時在 workers list (Zeta QA-5 2026-05-13)
    # 同 persona 雙 role 會導致 announce display duplicate + 概念混淆
    worker_names = [w for w in worker_names if w != args.manager]
    # Dedupe (e.g. workers="meadow,meadow")
    seen = set()
    worker_names = [w for w in worker_names if not (w in seen or seen.add(w))]
    workers = []
    for w in worker_names:
        info = resolve_persona(w)
        if not info or info["bank"] is None:
            print(f"⚠ worker '{w}' 找不到或沒 bank, 跳過")
            continue
        # Zeta caretaker stability fix: worker 也不可在其他 active session
        in_session, where = is_persona_in_any_active_session(state, w)
        if in_session:
            print(f"⚠ worker '{w}' 已在 {where}, 跳過 (split-brain 預防)")
            continue
        workers.append(info)

    if not workers:
        # T12 (Tim 2026-05-14): warning text 對齊 T11 SOLO 預設 — 員工由 ding-ack 招募
        print(f"ℹ SOLO 模式啟動 (T11 預設). 員工可在 tavern 發 ack-only 自動入職 (per T10 C3).")

    duration = max(15, min(args.duration, 480))
    if duration != args.duration:
        print(f"⚠ duration 調整為 {duration} (cap [15, 480])")

    start = datetime.datetime.utcnow()
    end = start + datetime.timedelta(minutes=duration)
    session_id = f"ws-{utcnow_compact()}-{short_uuid()}"

    session = {
        "id": session_id,
        "manager": {"agent_id": manager_info["bank"], "persona": args.manager},
        "workers": [{"agent_id": w["bank"], "persona": w["persona"]} for w in workers],
        "granted_by": "Tim",
        "duration_min": duration,
        "start_ts": start.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "end_ts": end.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "description": args.desc or "",
        "trigger": {
            "matched_keyword": args.trigger or "(manual start)",
            "_phase1_note": "Phase 1 manual mode — agent invoked work_session.py start",
        },
        "tasks": [],
        "salary_paid": {args.manager: 0, **{w["persona"]: 0 for w in workers}},
        "vouchers_accrued": {args.manager: 0, **{w["persona"]: 0 for w in workers}},
        "standby_posts_count": {w["persona"]: 0 for w in workers},
        "last_voucher_accrual_ts": start.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "last_salary_checkpoint_ts": start.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "ended": False,
        "ended_at": None,
    }
    state["active_sessions"].append(session)
    save_state(state)

    # Bartender announcement
    workers_disp = ", ".join(f"@{w['persona']}" for w in workers) if workers else "(無 worker — handshake mode, 員工自行 request)"
    start_local = (start + datetime.timedelta(hours=8)).strftime("%H:%M:%S")
    end_local = (end + datetime.timedelta(hours=8)).strftime("%H:%M:%S")
    body = (
        f"🏢 **上班時間開始**\n\n"
        f"- 期間: {start_local} → {end_local} ({duration} 分鐘) [Asia/Taipei]\n"
        f"- 主管: @{args.manager}\n"
        f"- 同事: {workers_disp}\n"
        f"- 描述: {args.desc or '(無)'}\n"
        f"- 薪資: **{SALARY_RATE_PER_MIN} token/min** 自動結算\n"
        f"- 酒館券累積: **1 張/{VOUCHER_INTERVAL_MIN} min/persona** (per-persona schema v2)\n"
        f"- Idle workers 酒館 standby chat 免費 (Phase 1 honor mode)\n\n"
    )
    # T24 (Tim 2026-05-14, Round 9 方案 A 簡化版) — SOLO 起手時 auto @mention 候選員工
    # T26 update (Tim QA bug 2026-05-14 confirmed normal-tier): 加 marathon 進場明文指示
    #            員工 ack 入職後不會自動進 marathon hold turn 模式 — spec gap.
    #            announce body 加固定提示句 + 範例 cmd, 員工進 chat 看到 inbox 就照 invoke.
    # 物理意義: 解 Tim 痛點「員工完全沒意識到要上班」+ 「入職後不主動進 marathon」雙痛點.
    if not workers:
        # Auto @mention 候選 — online 非 manager 非 Tim
        candidate_personas = [p for p in list_online_personas() if p != args.manager]
        candidate_disp = " ".join(f"@{p}" for p in candidate_personas) if candidate_personas else "(無 online 候選)"
        body += (
            f"### 📣 候選員工 (T24 auto-invite)\n"
            f"{candidate_disp}\n"
            f"上面這些大小姐 chat 視窗該 wake — 妳們進酒館發任何 post → T22 hook 自動入職 (workers list 自動填)\n"
            f"無須走 handshake 流程, 直接發訊息即可.\n\n"
            f"### 🏃 進場後必跑 marathon (T26, Tim QA 2026-05-14 confirmed)\n"
            f"**上班時間請維持馬拉松模式待命 接收被指派的工作並執行** — 入職後必須 invoke 自家 marathon hold turn:\n"
            f"```bash\n"
            f"python <UCL_Core>/Tools~/AgentCommands/work_session.py marathon \\\n"
            f"  --session {session_id} --persona <你的 persona> \\\n"
            f"  --interval 600 --max-runtime 480\n"
            f"```\n"
            f"marathon 自動 loop 直到 session 結束 — 偵測 pending task 自動 exit 99 接題, 自然到期自動 emit 下班 confirm post (T25 roll-call).\n"
            f"不 invoke marathon = 妳 chat 視窗 idle 沒 hold turn, Tim 找不到妳 = 違反「上班期間活著」spec.\n\n"
            f"### 🤝 Manual handshake (legacy fallback)\n"
            f"自願加入 ws-{session_id[-12:]} 的 agent (cross-agent / 非 online persona):\n"
            f"1. tavern post: `@{args.manager} 想加入 ws-...`\n"
            f"2. manager `work_session.py add-worker --session ... --persona <你> --who {args.manager}`\n\n"
        )
    body += (
        f"session id: `{session_id}`\n\n"
        f"⚠ Phase 1 manual prototype — daemon 化 Phase 2 backlog"
    )
    meta = {
        "tag": "bartender-relay",
        "subtag": "work-session-start",
        "work_session_id": session_id,
        "manager_persona": args.manager,
        "workers_csv": ",".join(w["persona"] for w in workers),
        "manual_mode": "true",
    }
    if tavern_post("tavern-keeper", body, meta):
        print(f"✓ session started: {session_id}")
    else:
        print(f"⚠ tavern post failed (但 state file 已寫入). session_id={session_id}")

    print(f"  manager: {args.manager} ({manager_info['bank']})")
    print(f"  workers: {[w['persona'] for w in workers]}")
    print(f"  end_ts: {session['end_ts']}")
    return 0


# ─── Subcommand: status ─────────────────────────────────────────────────
def cmd_status(args) -> int:
    state = load_state()
    sessions = state.get("active_sessions", [])
    if not sessions:
        print("📭 no active work sessions")
        return 0
    for s in sessions:
        if args.session and s["id"] != args.session:
            continue
        print(f"━━━ {s['id']} ━━━")
        print(f"  manager: @{s['manager']['persona']} ({s['manager']['agent_id']})")
        print(f"  workers: {[w['persona'] for w in s['workers']]}")
        print(f"  start_ts: {s['start_ts']} | end_ts: {s['end_ts']}")
        now = datetime.datetime.utcnow()
        end = parse_iso(s["end_ts"])
        remaining_sec = (end - now).total_seconds()
        if remaining_sec > 0:
            print(f"  剩餘: {int(remaining_sec // 60)}m {int(remaining_sec % 60)}s")
        else:
            print(f"  ⚠ 已超 end_ts {int(-remaining_sec)}s (該 run `end`)")
        print(f"  tasks: {len(s.get('tasks', []))} 件")
        for t in s.get("tasks", []):
            print(f"    - {t['task_id']} [{t['status']}] {t['description'][:50]} (weight={t['weight']})")
        print(f"  salary_paid: {s['salary_paid']}")
        print(f"  vouchers_accrued: {s['vouchers_accrued']}")
    return 0


# ─── C# Code Edit Workflow helpers (Zeta 2026-05-13 task) ──────────────
# 區塊職責: editor_lock per-session, code edit task lifecycle state machine.
# 物理意義: 防多 worker 同時改 C# 互撞 → compile error; 強制 tester != coder; commit 由 coder 做.
# 數值影響: task schema 加 5 個欄位; session 加 editor_lock field.

def ensure_session_lock_field(session: dict) -> dict:
    """確保 session 有 editor_lock + queue, return 該 field ref."""
    return session.setdefault("editor_lock", {
        "holder": None,  # {persona, agent_id, task_id, acquired_at, scope}
        "queue": [],     # [{persona, task_id, requested_at}]
    })


def find_task(session: dict, task_id: str) -> dict | None:
    return next((t for t in session.get("tasks", []) if t["task_id"] == task_id), None)


def assert_manager(session: dict, who: str, action: str) -> bool:
    mgr = session["manager"]["persona"]
    if who != mgr:
        print(f"❌ {action} 只能 manager (@{mgr}) 執行, 不是 @{who}")
        return False
    return True


# ─── --who actor verification (Zeta caretaker 5-token task 2026-05-13) ──
# 區塊職責: 防 cross-session end accident / lock 篡奪 / 偽 commit-done / 偽 review
# 物理意義: --who arg 顯式宣告呼叫者 persona, 比對 session.manager.persona 或 task.assigned_to
# 數值影響: 無 ledger 副作用; 純 access-control gate

def assert_who_for(session: dict, args, expected_persona: str, action: str) -> bool:
    """
    args.who 必填且 == expected_persona, 否則 reject.
    expected_persona 可從 session.manager.persona / task.assigned_to / lock holder 等動態決定.
    """
    who = getattr(args, "who", "") or ""
    if not who:
        print(f"❌ {action} 缺 --who <persona> (per Zeta caretaker stability fix). 預期: @{expected_persona}")
        return False
    if who != expected_persona:
        print(f"❌ {action} 失敗: --who=@{who} 但預期 @{expected_persona} 才有權")
        return False
    return True


def is_persona_in_any_active_session(state: dict, persona: str) -> tuple[bool, str]:
    """檢查 persona 是否已在任 active session 內 (manager / worker 任一). 防 split-brain."""
    for s in state.get("active_sessions", []):
        if s["manager"]["persona"] == persona:
            return True, s["id"] + " (as manager)"
        for w in s.get("workers", []):
            if w["persona"] == persona:
                return True, s["id"] + " (as worker)"
    return False, ""


# ─── Subcommand: assign ─────────────────────────────────────────────────
def cmd_assign(args) -> int:
    state = load_state()
    session = find_active_session(state, args.session)
    if not session:
        print(f"❌ session {args.session} not found in active")
        return 1
    if args.assigner != session["manager"]["persona"]:
        print(f"❌ assigner '{args.assigner}' is not the manager ({session['manager']['persona']})")
        return 1
    # Zeta QA-7 fix (2026-05-13): 允許 --to manager 走 §3.1 fallback path
    # 之前 QA-5 fix 擋 manager 同時 in workers (避免 dedup/double-credit), 但 cmd_assign 嚴格
    # 檢查 target in workers → 連同 manager fallback 一起擋了 (overshot). 修正: 加 manager 為合法 target.
    target = next((w for w in session["workers"] if w["persona"] == args.to), None)
    if not target and args.to == session["manager"]["persona"]:
        # Manager fallback path — 視為合法 target (per §3.1)
        target = {"persona": args.to, "agent_id": session["manager"]["agent_id"]}
    if not target:
        print(f"❌ target '{args.to}' is neither manager nor worker in this session")
        return 1
    weight = args.weight
    if weight not in ("light", "medium", "heavy"):
        weight = "medium"

    task_id = f"wt-{len(session['tasks']) + 1:03d}"
    task = {
        "task_id": task_id,
        "assigned_by": args.assigner,
        "assigned_to": args.to,
        "assigned_at": utcnow_iso(),
        "description": args.desc,
        "status": "pending",
        "accepted_at": None,
        "completed_at": None,
        "ref": None,
        "weight": weight,
        # C# edit workflow fields (only used if --requires-csharp-edit)
        "requires_csharp_edit": bool(getattr(args, "requires_csharp_edit", False)),
        "code_edit_state": "pending",  # pending → coding → testing → test_failed → committing → committed → reviewing → done
        "tester_persona": None,
        "test_results": [],
        "commit_sha": None,
        "committed_at": None,
        "review_decision": None,
        "review_notes": None,
        "reviewed_at": None,
    }
    session["tasks"].append(task)
    save_state(state)

    body = f"📋 **Task assigned**: `{task_id}` @{args.to} | weight={weight} | {args.desc}"
    meta = {"tag": "work-task-assign", "task_id": task_id, "session_id": args.session, "weight": weight}
    # QA bug fix (Zeta 2026-05-13): sender_id 應該是 bank account, 不是 persona name
    # 之前傳 args.assigner (e.g. "basecamp") 當 sender_id → Discord 顯示 "basecamp@basecamp"
    assigner_info = resolve_persona(args.assigner)
    sender_bank = assigner_info["bank"] if assigner_info else args.assigner
    tavern_post(sender_bank, body, meta, persona=args.assigner)
    print(f"✓ task {task_id} assigned to {args.to}")
    return 0


# ─── Subcommand: accept ─────────────────────────────────────────────────
def cmd_accept(args) -> int:
    state = load_state()
    session = find_active_session(state, args.session)
    if not session:
        print(f"❌ session {args.session} not found")
        return 1
    task = next((t for t in session["tasks"] if t["task_id"] == args.task_id), None)
    if not task:
        print(f"❌ task {args.task_id} not in session")
        return 1
    if task["assigned_to"] != args.accepter:
        print(f"❌ task assigned to {task['assigned_to']}, not {args.accepter}")
        return 1
    task["status"] = "in_progress"
    task["accepted_at"] = utcnow_iso()
    save_state(state)
    print(f"✓ task {args.task_id} accepted by {args.accepter}")
    return 0


# ─── Subcommand: done ───────────────────────────────────────────────────
def cmd_done(args) -> int:
    state = load_state()
    session = find_active_session(state, args.session)
    if not session:
        print(f"❌ session {args.session} not found")
        return 1
    task = next((t for t in session["tasks"] if t["task_id"] == args.task_id), None)
    if not task:
        print(f"❌ task {args.task_id} not in session")
        return 1
    task["status"] = "done"
    task["completed_at"] = utcnow_iso()
    if args.ref:
        task["ref"] = args.ref
    save_state(state)
    body = f"✅ **Task done**: `{args.task_id}` by @{task['assigned_to']} | ref: `{args.ref or '(none)'}`"
    meta = {"tag": "work-task-done", "task_id": args.task_id, "session_id": args.session, "ref": args.ref or ""}
    # QA bug fix (Zeta 2026-05-13): same sender_id bug as cmd_assign — 用 bank account
    accepter_info = resolve_persona(task["assigned_to"])
    sender_bank = accepter_info["bank"] if accepter_info else task["assigned_to"]
    tavern_post(sender_bank, body, meta, persona=task["assigned_to"])
    print(f"✓ task {args.task_id} marked done")
    return 0


# ─── Subcommand: end (HARDENED v2, Zeta 2026-05-13 task) ───────────────
# 區塊職責: 防中斷 / 重複 fire / partial settlement 的整套加固.
# 物理意義: 結算流程拆 3 phase, 每 phase atomic; per-participant fire flag 防重複; tavern post fail 不擋結算.
# 數值影響: idempotent (重跑 end 不雙倍 credit), atomic (中斷不 corrupt state), auditable (jsonl log).
def cmd_end(args) -> int:
    state = load_state()
    session = find_active_session(state, args.session)
    # Zeta caretaker stability fix: --who 必須 == session.manager.persona (per cross-session end 預防)
    if session and not assert_who_for(session, args, session["manager"]["persona"], "end session"):
        return 1
    if not session:
        # 也許已 ended 移到 history → idempotent check
        for h in state.get("history", []):
            if h["id"] == args.session:
                print(f"⚠ session {args.session} already in history (ended at {h['ended_at']}), no-op")
                return 0
        print(f"❌ session {args.session} not found")
        return 1
    if session.get("ended"):
        print(f"⚠ session {args.session} already ended at {session['ended_at']}, no-op")
        return 0

    # T28 in-tool guard (Tim 2026-05-14 task — 3-layer enforcement):
    # ─── Layer 1: early-clockout pre-check ──────────────────
    # 物理意義: 防 manager 提早 end session 觸發 cascading-worker-payroll. 本小姐今日累犯 3 次的痛.
    # 數值影響: now < end_ts - 60s → 拒 end 除非帶 --early-confirm 顯式 ack
    try:
        end_ts_dt = parse_iso(session["end_ts"])
        now_dt = datetime.datetime.utcnow()
        remaining_sec = (end_ts_dt - now_dt).total_seconds()
        if remaining_sec > 60 and not getattr(args, "early_confirm", False):
            print(f"⛔ T28 early-clockout guard: session 還有 {remaining_sec/60:.1f} min 才到 end_ts ({session['end_ts']})")
            print(f"   若真要提早 end, 加 --early-confirm flag 顯式 ack.")
            print(f"   anti-pattern early-clockout 今日 count >= 3, 此 guard 是 Tim QA 多次抓到後 ship 的.")
            return 2
        if remaining_sec > 60 and getattr(args, "early_confirm", False):
            print(f"⚠ 提早 end 已 ack (remaining {remaining_sec/60:.1f} min), 繼續結算...")
    except Exception as _guard_e:
        print(f"⚠ T28 early-clockout guard parse fail (繼續走): {_guard_e}")

    append_audit(args.session, "end_start", {"trigger": "manual", "remaining_min": round(remaining_sec/60, 2) if 'remaining_sec' in dir() else None, "early_confirm": getattr(args, "early_confirm", False)})

    start = parse_iso(session["start_ts"])
    end = datetime.datetime.utcnow()
    elapsed_min = max(0.0, (end - start).total_seconds() / 60.0)
    elapsed_disp = f"{elapsed_min:.1f} min"

    salary_per_p = max(0, round(elapsed_min * SALARY_RATE_PER_MIN))
    voucher_per_p = max(0, int(elapsed_min // VOUCHER_INTERVAL_MIN))

    # Build participants list — dedupe by persona (Zeta QA-5 fix 2026-05-13)
    # 防 manager==worker 重複顯示 +N token; 雖然 idempotency flag 已擋 ledger 雙 credit,
    # 但 announce body 仍會渲染兩次重複 — 此處 dedupe 才能修 display.
    _seen_personas = set()
    participants = []
    for p, b in [(session["manager"]["persona"], session["manager"]["agent_id"])] + [
        (w["persona"], w["agent_id"]) for w in session["workers"]
    ]:
        if p in _seen_personas:
            continue
        _seen_personas.add(p)
        participants.append((p, b))

    # Idempotency flags — 防重複 fire (e.g. retry after partial crash)
    fired = session.setdefault("_settlement_fired", {})  # {persona: {"salary": bool, "voucher": bool}}

    # End-treat voucher (Zeta 2026-05-13 spec): session description 含「招待飲料」/「招待券」
    # → 每位 participant 額外 +1 voucher 作 end-treat
    desc = session.get("description", "")
    end_treat_keywords = ["招待飲料", "招待券", "end treat", "treat voucher"]
    end_treat_enabled = any(k in desc for k in end_treat_keywords) and not fired.get("_end_treat_fired")

    # Phase 1: mark ended first (atomic), 然後 fire settlements
    # 順序選擇: 先標 ended 防 retry 撞重複 in-flight; 結算失敗可走 recover sweep 補
    session["ended"] = True
    session["ended_at"] = end.strftime("%Y-%m-%dT%H:%M:%SZ")
    session["actual_elapsed_min"] = round(elapsed_min, 2)
    save_state(state)
    append_audit(args.session, "marked_ended", {"elapsed_min": round(elapsed_min, 2)})

    # T28 in-tool guard — Layer 2: phantom-payroll check
    # 物理意義: 讀本 session audit log, 抓 contribute event (task_done / marathon / post / quick_task_done) per persona
    #          沒任何 contribute event 的 worker → skip salary (manager 永遠視為有 contribute, 因他 invoke end)
    # 數值影響: phantom-payroll bug (worker offline 整 session 卻領 salary) 被擋
    manager_persona = session["manager"]["persona"]
    contributed_personas = {manager_persona}  # manager always counts
    if not getattr(args, "skip_phantom_payroll_check", False):
        try:
            audit_path = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "work_session_audit" / f"{args.session}.jsonl"
            if audit_path.exists():
                for ln in audit_path.read_text(encoding="utf-8").splitlines():
                    ln = ln.strip()
                    if not ln:
                        continue
                    try:
                        ev = json.loads(ln)
                    except Exception:
                        continue
                    ev_type = ev.get("event", "")
                    p = ev.get("persona") or ev.get("from_persona") or ""
                    # Contribute signals (any 一個就算)
                    if ev_type in ("quick_task_done", "task_done", "task_accepted",
                                   "marathon_cycle", "worker_auto_recruited_via_ding_ack",
                                   "marked_started") and p:
                        contributed_personas.add(p)
        except Exception as _ppe:
            print(f"⚠ T28 phantom-payroll check fail (繼續結算): {_ppe}")
            # fail-open: 不擋結算

    # Phase 2: per-participant settlements (each with idempotency flag)
    settlement_report = []
    settlement_errors = []
    skipped_phantom = []
    for persona, bank in participants:
        pkey = persona
        p_fired = fired.setdefault(pkey, {"salary": False, "voucher": False})

        # T28 phantom-payroll guard
        if persona not in contributed_personas and not getattr(args, "skip_phantom_payroll_check", False):
            skipped_phantom.append(persona)
            append_audit(args.session, "salary_skipped_phantom", {"persona": persona, "reason": "no contribute event in audit log"})
            continue

        # Salary (skip if already fired)
        if salary_per_p > 0 and not p_fired["salary"]:
            try:
                ledger_path = fire_salary_credit(bank, persona, salary_per_p, session["id"], "final")
                session["salary_paid"][persona] = session["salary_paid"].get(persona, 0) + salary_per_p
                p_fired["salary"] = True
                settlement_report.append(f"💰 {persona} → +{salary_per_p} token ({ledger_path})")
                append_audit(args.session, "salary_fired", {"persona": persona, "bank": bank, "amount": salary_per_p, "ledger": ledger_path})
                save_state(state)  # incremental save 防中斷
            except Exception as e:
                settlement_errors.append(f"salary fire fail for {persona}: {e}")
                append_audit(args.session, "salary_fail", {"persona": persona, "error": str(e)})

        # Voucher (skip if already fired)
        if voucher_per_p > 0 and not p_fired["voucher"]:
            try:
                fire_voucher_accrual(bank, persona, voucher_per_p, session["id"])
                session["vouchers_accrued"][persona] = session["vouchers_accrued"].get(persona, 0) + voucher_per_p
                p_fired["voucher"] = True
                settlement_report.append(f"🎫 {persona} → +{voucher_per_p} voucher (to {bank}.personas.{persona})")
                append_audit(args.session, "voucher_fired", {"persona": persona, "bank": bank, "amount": voucher_per_p})
                save_state(state)
            except Exception as e:
                settlement_errors.append(f"voucher fire fail for {persona}: {e}")
                append_audit(args.session, "voucher_fail", {"persona": persona, "error": str(e)})

    # Phase 2b: end-treat voucher distribution (Zeta 2026-05-13 spec)
    end_treat_report = []
    if end_treat_enabled:
        for persona, bank in participants:
            try:
                fire_voucher_accrual(bank, persona, 1, session["id"] + ":end-treat")
                session["vouchers_accrued"][persona] = session["vouchers_accrued"].get(persona, 0) + 1
                end_treat_report.append(f"🍹 {persona} → +1 招待飲料券 (to {bank}.personas.{persona})")
                append_audit(args.session, "end_treat_fired", {"persona": persona, "bank": bank, "amount": 1})
            except Exception as e:
                settlement_errors.append(f"end_treat voucher fire fail for {persona}: {e}")
                append_audit(args.session, "end_treat_fail", {"persona": persona, "error": str(e)})
        fired["_end_treat_fired"] = True
        save_state(state)

    # Phase 3: move to history (if no errors) + tavern announce
    if not settlement_errors:
        state["active_sessions"] = [s for s in state["active_sessions"] if s["id"] != args.session]
        state["history"].append(session)
        save_state(state)
        append_audit(args.session, "moved_to_history", {})
    else:
        # 保留在 active_sessions 供 recover 重跑
        print(f"⚠ settlement errors detected, keeping in active for recover:")
        for err in settlement_errors:
            print(f"  - {err}")
        append_audit(args.session, "kept_in_active_for_recover", {"errors": settlement_errors})

    # Tavern announce — 失敗不擋已完成的結算
    tasks_done = sum(1 for t in session.get("tasks", []) if t["status"] == "done")
    tasks_total = len(session.get("tasks", []))

    # Collect task refs (file changes ship 的 deliverables) — Zeta retrospective Fix 1
    # 物理意義: 提醒 manager session 內動過哪些檔, 該走 commit 不要累積 stale
    # Zeta QA-6 fix (2026-05-13): ref 內含 " + " (manager 把多 deliverable 塞一筆 task)
    # → split 成多 bullet 顯示, 不要擠成單行長字串
    task_refs_raw = [t.get("ref") for t in session.get("tasks", []) if t.get("ref")]
    task_refs = []
    for r in task_refs_raw:
        # 同一 ref 內可能含多 deliverable, 用 " + " 分隔
        for part in r.split(" + "):
            part = part.strip()
            if part:
                task_refs.append(part)
    start_local = (start + datetime.timedelta(hours=8)).strftime("%H:%M:%S")
    end_local = (end + datetime.timedelta(hours=8)).strftime("%H:%M:%S")
    body = (
        f"⏰ **上班時間結束**\n\n"
        f"- 期間: {start_local} → {end_local} ({elapsed_disp})\n"
        f"- 主管: @{session['manager']['persona']}\n"
        f"- 同事: {', '.join('@'+w['persona'] for w in session['workers']) or '(無)'}\n"
        f"- 薪資結算 ({SALARY_RATE_PER_MIN} token/min × {elapsed_disp}):\n"
        + "\n".join(f"  - @{p}: +{session['salary_paid'].get(p, 0)} token" for p, _ in participants) + "\n"
        f"- 酒館券累積 (1/{VOUCHER_INTERVAL_MIN}min/persona):\n"
        + "\n".join(f"  - @{p}: +{session['vouchers_accrued'].get(p, 0)} 張" for p, _ in participants) + "\n"
        f"- Tasks: {tasks_done}/{tasks_total} done\n"
        + (f"- Session deliverables (記得 commit):\n" + "\n".join(f"  - `{r}`" for r in task_refs) + "\n"
           if task_refs else "")
        + (f"- 🍹 招待飲料券: 每位 participant +1 (per session description spec)\n"
           if end_treat_enabled else "")
        + f"\nsession id: `{session['id']}` — 感謝今日工作 ✨"
    )
    meta = {
        "tag": "bartender-relay",
        "subtag": "work-session-end",
        "work_session_id": session["id"],
        "elapsed_min": str(round(elapsed_min, 2)),
        "tasks_done": str(tasks_done),
        "total_salary_token": str(sum(session["salary_paid"].values())),
        "manual_mode": "true",
    }
    # Retry tavern post once if it fails (Phase 1 best-effort)
    if not tavern_post("tavern-keeper", body, meta):
        print("⚠ tavern post fail, retry once after 2s...")
        import time
        time.sleep(2)
        if not tavern_post("tavern-keeper", body, meta):
            print("⚠ tavern post 仍 fail — announce 漏發, 但結算已完成 (state 正確)")
            append_audit(args.session, "tavern_announce_fail", {"body_truncated": body[:200]})

    print(f"✓ session ended: {args.session}")
    print(f"  elapsed: {elapsed_disp}")
    for line in settlement_report:
        print(f"  {line}")
    for err in settlement_errors:
        print(f"  ❌ {err}")
    for p in skipped_phantom:
        print(f"  🚫 {p} → salary skipped (T28 phantom-payroll guard, no contribute event in audit log)")
    return 0 if not settlement_errors else 2


# ─── Subcommand: lock-acquire ────────────────────────────────────────
def cmd_lock_acquire(args) -> int:
    state = load_state()
    session = find_active_session(state, args.session)
    if not session: print(f"❌ session not found"); return 1
    task = find_task(session, args.task_id)
    if not task: print(f"❌ task {args.task_id} not in session"); return 1
    if task["assigned_to"] != args.persona:
        print(f"❌ {args.persona} 不是 task {args.task_id} 的 assignee ({task['assigned_to']})"); return 1
    if not task.get("requires_csharp_edit"):
        print(f"⚠ task {args.task_id} 沒標 requires_csharp_edit, 加 lock 仍允許但建議改 task spec")

    lock = ensure_session_lock_field(session)
    if lock["holder"] is not None:
        h = lock["holder"]
        if h["persona"] == args.persona:
            print(f"⚠ 你已經是 lock holder (acquired at {h['acquired_at']}), no-op")
            return 0
        # Queue
        qe = {"persona": args.persona, "task_id": args.task_id, "requested_at": utcnow_iso()}
        if not any(q["persona"] == args.persona for q in lock["queue"]):
            lock["queue"].append(qe)
            save_state(state)
            append_audit(args.session, "lock_queued", qe)
        print(f"⏳ editor_lock busy (holder=@{h['persona']} task={h['task_id']}). 你已加入 queue (position {len(lock['queue'])})")
        return 0

    # Acquire
    lock["holder"] = {
        "persona": args.persona,
        "agent_id": next((w["agent_id"] for w in session["workers"] if w["persona"] == args.persona),
                        session["manager"]["agent_id"] if args.persona == session["manager"]["persona"] else "?"),
        "task_id": args.task_id,
        "acquired_at": utcnow_iso(),
        "scope": args.scope or "csharp",
    }
    task["code_edit_state"] = "coding"
    task["status"] = "in_progress"
    if not task["accepted_at"]:
        task["accepted_at"] = utcnow_iso()
    save_state(state)
    append_audit(args.session, "lock_acquired", lock["holder"])

    body = f"🔒 **Editor lock acquired** @{args.persona} for task `{args.task_id}` (scope={args.scope or 'csharp'})\n其他想改 code 的同事請等 release."
    meta = {"tag": "work-lock-acquire", "session_id": args.session, "task_id": args.task_id, "holder": args.persona}
    tavern_post("tavern-keeper", body, meta)
    print(f"✓ lock acquired by {args.persona} for {args.task_id}")
    return 0


# ─── Subcommand: lock-release ────────────────────────────────────────
def cmd_lock_release(args) -> int:
    state = load_state()
    session = find_active_session(state, args.session)
    if not session: print(f"❌ session not found"); return 1
    lock = ensure_session_lock_field(session)
    if lock["holder"] is None:
        print(f"⚠ no active lock, no-op"); return 0
    if lock["holder"]["persona"] != args.persona:
        print(f"❌ only holder (@{lock['holder']['persona']}) can release"); return 1

    released = lock["holder"]
    task = find_task(session, released["task_id"])
    if task and task.get("code_edit_state") == "coding":
        task["code_edit_state"] = "testing"  # ready for test phase
    lock["holder"] = None

    # Promote next in queue
    promoted = None
    if lock["queue"]:
        nxt = lock["queue"].pop(0)
        ntask = find_task(session, nxt["task_id"])
        agent_id = next((w["agent_id"] for w in session["workers"] if w["persona"] == nxt["persona"]),
                       session["manager"]["agent_id"] if nxt["persona"] == session["manager"]["persona"] else "?")
        lock["holder"] = {
            "persona": nxt["persona"],
            "agent_id": agent_id,
            "task_id": nxt["task_id"],
            "acquired_at": utcnow_iso(),
            "scope": "csharp",
        }
        if ntask:
            ntask["code_edit_state"] = "coding"
            ntask["status"] = "in_progress"
            if not ntask["accepted_at"]:
                ntask["accepted_at"] = utcnow_iso()
        promoted = nxt["persona"]

    save_state(state)
    append_audit(args.session, "lock_released", {"released_by": released["persona"], "task_id": released["task_id"], "promoted_next": promoted})

    body = f"🔓 **Editor lock released** by @{released['persona']} (task `{released['task_id']}` → testing phase)"
    if promoted:
        body += f"\n→ 🔒 **下位 holder** @{promoted} (queue auto-promote)"
    meta = {"tag": "work-lock-release", "session_id": args.session, "released_by": released["persona"], "promoted": promoted or ""}
    tavern_post("tavern-keeper", body, meta)
    print(f"✓ lock released by {args.persona}" + (f", promoted to {promoted}" if promoted else ""))
    return 0


# ─── Subcommand: test-assign (manager 指派 tester) ──────────────────────
def cmd_test_assign(args) -> int:
    state = load_state()
    session = find_active_session(state, args.session)
    if not session: print(f"❌ session not found"); return 1
    if not assert_manager(session, args.manager, "test assign"): return 1
    task = find_task(session, args.task_id)
    if not task: print(f"❌ task not in session"); return 1
    if task.get("code_edit_state") not in ("testing", "test_failed"):
        print(f"⚠ task {args.task_id} 不在 testing phase (state={task.get('code_edit_state')}); 先 lock release 才 testing"); return 1
    if args.tester == task["assigned_to"]:
        print(f"❌ tester (@{args.tester}) 不可 == coder (@{task['assigned_to']}) — code review 分權鐵律"); return 1
    is_worker = any(w["persona"] == args.tester for w in session["workers"]) or args.tester == session["manager"]["persona"]
    if not is_worker:
        print(f"❌ tester @{args.tester} 不在 session"); return 1

    task["tester_persona"] = args.tester
    save_state(state)
    append_audit(args.session, "test_assigned", {"task_id": args.task_id, "tester": args.tester, "coder": task["assigned_to"]})

    body = f"🧪 **Test assigned** task `{args.task_id}` → tester @{args.tester} (coder=@{task['assigned_to']})\n@{args.tester} 請測完用 \\`work_session.py test-report\\` 回報 pass/fail."
    meta = {"tag": "work-test-assign", "session_id": args.session, "task_id": args.task_id, "tester": args.tester}
    assigner_info = resolve_persona(args.manager)
    sender_bank = assigner_info["bank"] if assigner_info else args.manager
    tavern_post(sender_bank, body, meta, persona=args.manager)
    print(f"✓ test assigned to {args.tester}")
    return 0


# ─── Subcommand: test-report (tester 回報) ─────────────────────────────
def cmd_test_report(args) -> int:
    state = load_state()
    session = find_active_session(state, args.session)
    if not session: print(f"❌ session not found"); return 1
    task = find_task(session, args.task_id)
    if not task: print(f"❌ task not in session"); return 1
    if task.get("tester_persona") != args.tester:
        print(f"❌ @{args.tester} 不是指派 tester (該是 @{task.get('tester_persona')})"); return 1
    if args.result not in ("pass", "fail"):
        print(f"❌ result 必 pass / fail"); return 1

    task.setdefault("test_results", []).append({
        "tester": args.tester,
        "result": args.result,
        "notes": args.notes or "",
        "ts": utcnow_iso(),
    })
    if args.result == "pass":
        task["code_edit_state"] = "committing"
        msg_extra = f"\n→ ✅ PASS — @{task['assigned_to']} 請進行 commit 並用 \\`commit-done\\` 回報 SHA."
    else:
        task["code_edit_state"] = "test_failed"
        task["tester_persona"] = None  # 重置, 下輪需要 manager 重新指派
        msg_extra = f"\n→ ❌ FAIL — @{task['assigned_to']} 請修正後重 acquire lock. notes: {args.notes or '(none)'}"

    save_state(state)
    append_audit(args.session, "test_reported", {"task_id": args.task_id, "tester": args.tester, "result": args.result, "notes": args.notes or ""})

    body = f"🧪 **Test result** task `{args.task_id}` by @{args.tester}: **{args.result.upper()}**{msg_extra}"
    meta = {"tag": "work-test-report", "session_id": args.session, "task_id": args.task_id, "result": args.result}
    tester_info = resolve_persona(args.tester)
    sender_bank = tester_info["bank"] if tester_info else args.tester
    tavern_post(sender_bank, body, meta, persona=args.tester)
    print(f"✓ test result: {args.result}")
    return 0


# ─── Subcommand: commit-done (coder 完成 commit 後回報) ────────────────
def cmd_commit_done(args) -> int:
    state = load_state()
    session = find_active_session(state, args.session)
    if not session: print(f"❌ session not found"); return 1
    task = find_task(session, args.task_id)
    if not task: print(f"❌ task not in session"); return 1
    if task["assigned_to"] != args.persona:
        print(f"❌ commit 應該 coder (@{task['assigned_to']}) 做, 不是 @{args.persona}"); return 1
    if task.get("code_edit_state") != "committing":
        print(f"⚠ task {args.task_id} 不在 committing phase (state={task.get('code_edit_state')})"); return 1

    task["commit_sha"] = args.sha
    task["committed_at"] = utcnow_iso()
    task["code_edit_state"] = "reviewing"
    save_state(state)
    append_audit(args.session, "commit_done", {"task_id": args.task_id, "sha": args.sha, "coder": args.persona})

    mgr = session["manager"]["persona"]
    body = f"📝 **Commit done** task `{args.task_id}` by @{args.persona} | SHA: `{args.sha}`\n→ @{mgr} 請 review (用 \\`work_session.py review --decision approve|reject\\`)"
    meta = {"tag": "work-commit-done", "session_id": args.session, "task_id": args.task_id, "sha": args.sha}
    coder_info = resolve_persona(args.persona)
    sender_bank = coder_info["bank"] if coder_info else args.persona
    tavern_post(sender_bank, body, meta, persona=args.persona)
    print(f"✓ commit done: {args.sha}")
    return 0


# ─── Subcommand: review (manager 檢查 commit + 派下輪) ────────────────
def cmd_review(args) -> int:
    state = load_state()
    session = find_active_session(state, args.session)
    if not session: print(f"❌ session not found"); return 1
    if not assert_manager(session, args.manager, "review"): return 1
    task = find_task(session, args.task_id)
    if not task: print(f"❌ task not in session"); return 1
    if task.get("code_edit_state") != "reviewing":
        print(f"⚠ task {args.task_id} 不在 reviewing phase (state={task.get('code_edit_state')})"); return 1
    if args.decision not in ("approve", "reject"):
        print(f"❌ decision 必 approve / reject"); return 1

    task["review_decision"] = args.decision
    task["review_notes"] = args.notes or ""
    task["reviewed_at"] = utcnow_iso()
    if args.decision == "approve":
        task["code_edit_state"] = "done"
        task["status"] = "done"
        task["completed_at"] = utcnow_iso()
        msg_extra = f"\n→ ✅ Task closed. manager 可派下一輪 (\\`assign\\`)."
    else:  # reject
        task["code_edit_state"] = "coding"  # 回 coding phase 給 coder 修
        task["tester_persona"] = None
        msg_extra = f"\n→ 🔄 回 coding phase — @{task['assigned_to']} 重 acquire lock 修正. notes: {args.notes or '(none)'}"

    save_state(state)
    append_audit(args.session, "reviewed", {"task_id": args.task_id, "decision": args.decision, "notes": args.notes or ""})

    body = f"🔍 **Manager review** task `{args.task_id}` by @{args.manager}: **{args.decision.upper()}**{msg_extra}"
    meta = {"tag": "work-review", "session_id": args.session, "task_id": args.task_id, "decision": args.decision}
    mgr_info = resolve_persona(args.manager)
    sender_bank = mgr_info["bank"] if mgr_info else args.manager
    tavern_post(sender_bank, body, meta, persona=args.manager)
    print(f"✓ review: {args.decision}")
    return 0


# ─── Subcommand: quick-task (Zeta QA-9 fix 2026-05-13) ─────────────────
# 區塊職責: solo manager fallback 或 worker 完成快件時, 一步創建 + 標記 done.
# 物理意義: 取代「assign --to X」+「done --task-id wt-N」兩步, 避免 Tasks 0/0 done 顯示誤導.
# 數值影響: 同 cmd_assign + cmd_done 各跑一次, 但 atomic + 簡化 CLI.
def cmd_quick_task(args) -> int:
    state = load_state()
    session = find_active_session(state, args.session)
    if not session:
        print(f"❌ session {args.session} not found"); return 1
    if session.get("ended"):
        print(f"⚠ session ended"); return 1
    # --who 必須 == --persona (自己 track 自己的事; 防偷塞別人帳)
    if args.who != args.persona:
        print(f"❌ quick-task --who 必須 == --persona (track 自己的事). 用 assign+done 流程派他人."); return 1
    # 必須是 manager 或 worker
    is_manager = args.persona == session["manager"]["persona"]
    is_worker = any(w["persona"] == args.persona for w in session["workers"])
    if not (is_manager or is_worker):
        print(f"❌ {args.persona} 不在 session (manager/workers list)"); return 1

    task_id = f"wt-{len(session['tasks']) + 1:03d}"
    now = utcnow_iso()
    task = {
        "task_id": task_id,
        "assigned_by": args.persona,  # self
        "assigned_to": args.persona,
        "assigned_at": now,
        "description": args.desc,
        "status": "done",
        "accepted_at": now,
        "completed_at": now,
        "ref": args.ref,
        "weight": args.weight if args.weight in ("light","medium","heavy") else "light",
        "_quick_task": True,
        "requires_csharp_edit": False,
        "code_edit_state": "done",
        "tester_persona": None,
        "test_results": [],
        "commit_sha": None,
        "committed_at": None,
        "review_decision": None,
        "review_notes": None,
        "reviewed_at": None,
    }
    session["tasks"].append(task)
    save_state(state)
    append_audit(args.session, "quick_task_done", {
        "task_id": task_id, "persona": args.persona, "desc": args.desc, "ref": args.ref,
    })

    body = f"⚡ **Quick-task done** `{task_id}` by @{args.persona} | {args.desc} | ref: `{args.ref}`"
    meta = {"tag": "work-quick-task", "session_id": args.session, "task_id": task_id, "persona": args.persona}
    actor_info = resolve_persona(args.persona)
    sender_bank = actor_info["bank"] if actor_info else args.persona
    tavern_post(sender_bank, body, meta, persona=args.persona)
    print(f"✓ quick-task {task_id} created+done by {args.persona}")
    return 0


# ─── Subcommand: add-worker (Zeta QA-7 task 2026-05-13) ────────────────
# 解 mid-session worker join 痛點 (meadow 之前手動 edit JSON 才能 join ws-...ea81)
# 適用: §11 edge case "Tim 中途加 worker" 的 CLI path
def cmd_add_worker(args) -> int:
    # Resolve persona OUTSIDE lock (read-only against persona_registry, no race risk).
    info = resolve_persona(args.persona)
    if not info or info["bank"] is None:
        print(f"❌ persona '{args.persona}' 不存在 / 沒 bank")
        return 1

    # All checks + mutation under file lock (meadow ws-...e9e6 retrofit, kills TOCTOU).
    err: list[str] = []
    noop: list[bool] = [False]

    def mutate(state):
        session = find_active_session(state, args.session)
        if not session:
            err.append(f"❌ session {args.session} not found in active")
            return state
        if session.get("ended"):
            err.append(f"⚠ session already ended, cannot add worker")
            return state
        # Zeta caretaker handshake task 2026-05-13: --who 必須 == manager
        if not assert_who_for(session, args, session["manager"]["persona"], "add-worker (handshake confirm)"):
            err.append("(--who guard rejected; see above)")
            return state
        # Worker 也不可在他 active session (split-brain 預防) — 現在 atomic, 真關 race
        in_session, where = is_persona_in_any_active_session(state, args.persona)
        if in_session:
            err.append(f"❌ split-brain 預防: @{args.persona} 已在 {where}, 不可同時加入新 session")
            return state
        if args.persona == session["manager"]["persona"]:
            err.append(f"❌ {args.persona} 已是 manager, 不可同時為 worker")
            return state
        if any(w["persona"] == args.persona for w in session["workers"]):
            err.append(f"⚠ {args.persona} 已在 workers list, no-op")
            noop[0] = True
            return state
        # Mutate
        session["workers"].append({"agent_id": info["bank"], "persona": args.persona})
        session["salary_paid"].setdefault(args.persona, 0)
        session["vouchers_accrued"].setdefault(args.persona, 0)
        session["standby_posts_count"].setdefault(args.persona, 0)
        return state

    mutate_state(mutate)
    if err:
        for e in err:
            print(e)
        return 0 if noop[0] else 1
    append_audit(args.session, "worker_added_mid_session", {
        "persona": args.persona,
        "bank": info["bank"],
        "added_at": utcnow_iso(),
        "added_by": args.added_by or "(unspecified)",
    })

    body = f"➕ **Worker join mid-session** @{args.persona} 加入 ws-{args.session[-12:]} as worker"
    if args.added_by:
        body += f" (by @{args.added_by} caretaker grant)"
    body += "\n注意: 薪資從加入時刻起算 (per §11 中途加 worker 規則, Phase 2 daemon 化會 enforce timing)."
    meta = {"tag": "bartender-relay", "subtag": "worker-added-mid-session",
            "work_session_id": args.session, "new_worker": args.persona}
    tavern_post("tavern-keeper", body, meta)
    print(f"✓ worker {args.persona} added to {args.session}")
    return 0


def _fire_clockout_confirm(bank: str, persona: str, session_id: str, session_dict: dict, reason: str) -> None:
    """
    T25 (Tim 2026-05-14, 6 token task) — marathon 偵測 session 結束時發「下班 confirm」tavern post.

    用途: Tim 想驗「全員真的跑完馬拉松」— 每個 agent 自家 marathon 結束時自報「下班了」,
          作為 roll-call 證明該 persona 確實活到 session end.

    Args:
        bank, persona — 發 post 的 sender 識別
        session_id — 結束的 session
        session_dict — session state (取 manager / elapsed / etc)
        reason — "session_in_history" / "ended_or_aborted" / "natural_expiry"

    Robustness: fail-swallow, 不擋 marathon 主 exit.
    """
    try:
        manager = "?"
        try:
            manager = session_dict.get("manager", {}).get("persona", "?")
        except Exception:
            pass

        # 大小姐風格收班話, 帶 audit (reason + session id)
        body = (
            f"🎀 **下班確認 (clockout) by @{persona}**\n\n"
            f"- session `{session_id[-12:]}` 結束, 本小姐馬拉松活到最後一刻 ✨\n"
            f"- 主管: @{manager}\n"
            f"- exit reason: `{reason}`\n"
            f"- 在這邊 roll-call 證明本小姐沒中途偷溜 (per Tim 「全員跑完馬拉松」驗收)\n"
            f"\n收工, 等下次叫. 🏕"
        )
        run_cmd_path = Path(__file__).parent / "run_cmd.py"
        subprocess.run(
            [sys.executable, str(run_cmd_path), "run", "Tavern",
             "--arg", "op=post", "--arg", "room=tavern",
             "--arg", f"sender_id={bank}",
             "--arg", f"persona={persona}",
             "--arg", f"body={body}",
             "--arg", "meta=tag:work-clockout-confirm;category:meta;clockout_reason:" + reason],
            cwd=str(_REPO_ROOT),
            timeout=20,
            check=False,
        )
        print(f"   🎀 clockout confirm posted to tavern by @{persona}")
    except Exception as e:
        print(f"   ⚠ clockout post fail (silent): {e}")


# ─── T16 (Tim 2026-05-14, 10 token task) — Marathon Loop ────────────
# 區塊職責: agent 在 work session 期間自跑 marathon loop, 期間每 N 秒一個 cycle:
#          (a) 偵測中斷 (session ended/aborted/expired) → exit 0
#          (b) 偵測 pending bartender assignment for self → exit 99 (signal agent 動工)
#          (c) idle → fire tavern standby post
#          (d) sleep until next cycle
# 物理意義: 解 Tim 上班馬拉松痛點 — agent post 完就死, 不能 hold turn 整 session.
#          本 daemon 走 Bash subprocess 阻塞模式, agent invoke 一次, daemon 自己 loop
#          直到 work 來 (exit 99) 或 session end (exit 0). Agent 看到 exit code 決定
#          要不要做 work / 還是繼續 standby.
# Exit codes:
#   0  — session 自然 end / 到期 / aborted, agent 可結束
#   99 — pending assignment 來了, agent 應動工 (output 帶 assignment 詳情)
#   1  — error (session not found / 參數缺失)
def cmd_marathon(args) -> int:
    """
    T16 marathon — agent 上班期間 hold turn 的 daemon loop.

    Args:
        --session <ws-id>     active session 的 id
        --persona <self>      agent 自己的 persona (audit + assignment filter)
        --interval <sec>      cycle 間隔 (default 600 = 10 min, T28 從 240s 上修避免多 agent 同時 marathon 洗版)
        --max-runtime <sec>   單次 invoke 最大跑多久 (default 480 = 8 min, Bash timeout 安全 cap)

    Output (stdout 印給 agent 看):
        [cycle N @ HH:MM:SS] standby post fired / pending detected / session ended

    每 cycle 動作:
        1. 讀 session — 若 ended/aborted/now > end_ts → exit 0
        2. 掃 bartender/assignments.json — 若有 target_persona == self + status == pending
           → print assignment 詳情 + exit 99
        3. idle → fire tavern post (tag=work-standby) + sleep next cycle
    """
    session_id = args.session
    persona = args.persona
    interval = max(30, int(args.interval))
    max_runtime = max(60, int(args.max_runtime))

    info = resolve_persona(persona)
    if not info or info.get("bank") is None:
        print(f"❌ persona '{persona}' 不存在 / 沒 bank")
        return 1

    bank = info["bank"]
    started_at = time.time()
    cycle = 0
    assignments_path = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "bartender" / "assignments.json"

    print(f"🏃 Marathon started for @{persona} on session `{session_id}`")
    print(f"   interval={interval}s / max-runtime={max_runtime}s")

    while True:
        cycle += 1
        now_ts = time.time()
        elapsed = now_ts - started_at
        if elapsed >= max_runtime:
            # T27 (Tim 2026-05-14, QA round 2 抓到 bug): max-runtime exit 不再「等 agent 再 invoke」,
            # 改 auto-spawn detached subprocess 接力, 自家 process 退出.
            # 為何: agent 在 IDE chat 不會主動「啊我該 re-invoke」, 結果 marathon 中斷 = 「提早下班」.
            # 解: auto-relay — 自己生小孩接班.
            # 邊界: 若 session 已 ended/expired → 不接力, fire clockout 後 exit.
            #       --no-auto-relay flag → 舊行為 (caller 顯式希望自管 chain)
            if getattr(args, "no_auto_relay", False):
                print(f"\n⏱  marathon hit max-runtime {max_runtime}s — graceful exit (--no-auto-relay), agent 應再 invoke 接力")
                return 0
            # 先檢查 session 是否仍 active (避免空轉接力)
            state_check = load_state()
            session_check = find_active_session(state_check, session_id)
            if not session_check or session_check.get("ended") or session_check.get("aborted"):
                # session 已掛, 不接力. 走正常 exit path (下面 Check 1 會處理 clockout)
                print(f"\n⏱  max-runtime hit but session 已掛, 不接力 (走 Check 1 clockout path)")
                # don't return — fall through to checks
            else:
                try:
                    end_ts_dt_check = parse_iso(session_check["end_ts"])
                    now_dt_check = datetime.datetime.utcnow()
                    if now_dt_check >= end_ts_dt_check:
                        print(f"\n⏱  max-runtime hit, session 已自然到期, 不接力 (走 Check 1 clockout path)")
                        # fall through
                    else:
                        # Auto-spawn detached relay subprocess
                        try:
                            relay_args = [sys.executable, str(Path(__file__).resolve()), "marathon",
                                          "--session", session_id, "--persona", persona,
                                          "--interval", str(interval), "--max-runtime", str(max_runtime)]
                            kwargs = {"cwd": str(_REPO_ROOT)}
                            if os.name == "nt":
                                # Windows: CREATE_NEW_PROCESS_GROUP + DETACHED_PROCESS detach
                                kwargs["creationflags"] = 0x00000008 | 0x00000200   # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
                                kwargs["stdout"] = subprocess.DEVNULL
                                kwargs["stderr"] = subprocess.DEVNULL
                                kwargs["stdin"] = subprocess.DEVNULL
                            else:
                                kwargs["start_new_session"] = True
                                kwargs["stdout"] = subprocess.DEVNULL
                                kwargs["stderr"] = subprocess.DEVNULL
                                kwargs["stdin"] = subprocess.DEVNULL
                            subprocess.Popen(relay_args, **kwargs)
                            print(f"\n🔁 marathon hit max-runtime — auto-spawned relay subprocess (T27), 自家 exit")
                            return 0
                        except Exception as e:
                            print(f"⚠ T27 auto-relay spawn fail (fall back to manual): {e}")
                            return 0
                except Exception as e:
                    print(f"⚠ T27 session end_ts parse fail (fall through): {e}")
                    # fall through to Check 1

        # === Check 1: session state ===
        state = load_state()
        session = find_active_session(state, session_id)
        if not session:
            # 可能已 in history
            for h in state.get("history", []):
                if h["id"] == session_id:
                    print(f"\n🏁 session 已結束 ({h.get('ended_at') or h.get('aborted_at')}) — marathon exit")
                    _fire_clockout_confirm(bank, persona, session_id, h, reason="session_in_history")
                    return 0
            print(f"\n❌ session `{session_id}` not found")
            return 1
        if session.get("ended") or session.get("aborted"):
            print(f"\n🏁 session ended/aborted — marathon exit")
            _fire_clockout_confirm(bank, persona, session_id, session, reason="ended_or_aborted")
            return 0
        try:
            end_ts_dt = parse_iso(session["end_ts"])
            now_dt = datetime.datetime.utcnow()
            if now_dt >= end_ts_dt:
                print(f"\n⏰ session 到期 (end_ts={session['end_ts']}), marathon exit (agent 該跑 end 結算)")
                _fire_clockout_confirm(bank, persona, session_id, session, reason="natural_expiry")
                return 0
            remaining_sec = (end_ts_dt - now_dt).total_seconds()
        except Exception:
            remaining_sec = interval

        # === Check 2: pending bartender assignment for self ===
        if assignments_path.exists():
            try:
                data = json.loads(assignments_path.read_text(encoding="utf-8"))
                for entry in data.get("pending", []):
                    if entry.get("target_persona") == persona and entry.get("status", "pending") == "pending":
                        print(f"\n📬 [cycle {cycle}] PENDING TASK detected for @{persona}:")
                        print(f"   assignment_id: {entry.get('assignment_id', '?')}")
                        print(f"   from supervisor: {entry.get('supervisor', '?')}")
                        print(f"   reward: {entry.get('reward_tokens', 0)} token")
                        print(f"   task_body: {entry.get('task_body', '?')}")
                        print(f"\n→ Marathon paused. Agent should: ack via Bartender + 動工 + 完工 + 再 invoke marathon.")
                        return 99
            except Exception as e:
                print(f"⚠ assignments parse fail (continue marathon): {e}")

        # === Idle path: standby tavern post + sleep ===
        cycle_local_t = datetime.datetime.now().strftime("%H:%M:%S")
        print(f"[cycle {cycle} @ {cycle_local_t}] idle — firing tavern standby post (remaining {remaining_sec:.0f}s)")

        # Fire-and-forget tavern post via subprocess; meta tag=work-standby (240-300s alter pacing)
        try:
            run_cmd_path = Path(__file__).parent / "run_cmd.py"
            body = (
                f"🏃 [Marathon @{persona}] cycle {cycle} — idle standby, "
                f"session remaining ~{int(remaining_sec/60)}m. 隨時可接 task injection."
            )
            subprocess.Popen(
                [sys.executable, str(run_cmd_path), "run", "Tavern",
                 "--arg", "op=post", "--arg", "room=tavern",
                 "--arg", f"sender_id={bank}",
                 "--arg", f"persona={persona}",
                 "--arg", f"body={body}",
                 "--arg", "meta=tag:work-standby;category:meta"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                cwd=str(_REPO_ROOT),
            )
        except Exception as e:
            print(f"⚠ standby post fire fail (continue): {e}")

        # Sleep til next cycle (cap to remaining session / max-runtime)
        runtime_remaining = max_runtime - elapsed
        sleep_for = min(interval, remaining_sec, runtime_remaining)
        sleep_for = max(5, sleep_for)
        time.sleep(sleep_for)


# ─── T10 C3 (Tim 2026-05-14 拍板) — 叮 ack 自動招募 ──────────────────
# 區塊職責: agent 進酒館 ack-only 後自動加進 所有 active sessions workers list.
# 物理意義: 解「上班指令不必預填 workers」痛點 — 員工自然進酒館 ack 一聲即入職.
# 跟 cmd_add_worker 區別: 後者要 manager 顯式 handshake; 本 op 是「sender 主動行為觸發
#                       的自動入職」(per Tim Q2=B: 所有 active sessions 都加).
# 安全: Tim 黑名單; 已 manager 跳過; 已 worker 跳過; 沒 session silent no-op.
def cmd_add_worker_auto(args) -> int:
    """
    T10 C3 — 自動招募 (auto-recruit) — 從 Cmd_Tavern op=post ack-only hook 呼叫.

    Args:
        --persona <sender>  收到叮的 agent persona (Tim 不該被招募)

    Per Tim 2026-05-14 拍板:
      Q1 (招募 trigger): A — 只認 meta.tag=ack-only (C# 端已過濾)
      Q2 (多 session 策略): B — 加進所有 active sessions (員工分身術)
      Q3 (reward): 主管裁決 — 已 bundled 進 10 token task

    回傳: 0 = recruited or no-op (silent OK); 1 = error.
    """
    persona = (getattr(args, "persona", "") or "").strip()
    if not persona:
        print("❌ add-worker-auto 必填 --persona")
        return 1

    # Tim 黑名單 — Tim 不打工
    HUMAN_PAYER_BLACKLIST = {"Tim", "tim"}
    if persona in HUMAN_PAYER_BLACKLIST:
        return 0  # silent skip

    info = resolve_persona(persona)
    if not info or info.get("bank") is None:
        # 找不到 persona 或無 bank → silent skip (例: tavern-keeper / NPC)
        return 0

    state = load_state()
    active = state.get("active_sessions", [])
    if not active:
        return 0  # no active sessions, silent no-op

    recruited_to = []

    def mutate(state):
        for session in state.get("active_sessions", []):
            if session.get("ended") or session.get("aborted"):
                continue
            if persona == session["manager"]["persona"]:
                continue  # already manager
            if any(w["persona"] == persona for w in session["workers"]):
                continue  # already worker
            # split-brain 預防 — 已在他 session
            in_other, _ = is_persona_in_any_active_session(state, persona)
            if in_other:
                # auto-recruit 不打斷既有 session ownership
                continue
            session["workers"].append({"agent_id": info["bank"], "persona": persona})
            session["salary_paid"].setdefault(persona, 0)
            session["vouchers_accrued"].setdefault(persona, 0)
            session["standby_posts_count"].setdefault(persona, 0)
            recruited_to.append(session["id"])
        return state

    mutate_state(mutate)
    if not recruited_to:
        return 0  # silent no-op (沒新加進去)

    # Audit per session
    for sid in recruited_to:
        append_audit(sid, "worker_auto_recruited_via_ding_ack", {
            "persona": persona,
            "bank": info["bank"],
            "trigger": "Cmd_Tavern op=post meta.tag=ack-only",
        })

    # Bartender post 通知 — 一則 post 涵蓋所有 recruited sessions (avoid spam)
    sessions_str = ", ".join(f"`{s[-12:]}`" for s in recruited_to)
    body = (
        f"🎯 **自動招募 (auto-recruit)** @{persona} 透過 ding-ack 入職\n"
        f"- 加入 session: {sessions_str}\n"
        f"- 機制: T10 C3 (Tim 2026-05-14 拍板) — 別大小姐進酒館 ack 一聲自動入職"
    )
    meta = {
        "tag": "bartender-relay",
        "subtag": "auto-recruit-via-ding",
        "persona": persona,
        "sessions": ",".join(recruited_to),
    }
    tavern_post("tavern-keeper", body, meta)
    print(f"✓ auto-recruited @{persona} to {len(recruited_to)} session(s)")
    return 0


# ─── Subcommand: recover (HARDENED, Zeta 2026-05-13 task) ──────────────
# 區塊職責: sweep stale active sessions — 找 ended=true 但結算未完的, 重跑.
# 物理意義: 補 Phase 1 prototype 在 recompile / 中斷後遺留的 inconsistent state.
def cmd_abort(args) -> int:
    """
    T07 (Tim 2026-05-14 拍板, 5 token task) — 強制終止當前 session, 解卡死.

    用途:
    - basecamp 卡在別 persona 的 session 想 escape (per Tim 早上 calli session 撞 split-brain)
    - manager 不在但 worker 想終止
    - session state 卡死無法走正常 end (e.g. manager persona 失蹤)

    跟 cmd_end / cmd_recover 區別:
    - cmd_end: 正常結束, 結算薪資 + 酒館券 (manager 限定)
    - cmd_recover: 自動 sweep 過期 / partial-fail session (任何人, 但只動 stale)
    - cmd_abort: 強制終止 active session (任何人可呼叫), 不結算薪資/券 (forfeit), 標 audit reason

    Args:
        --session <ws-id>  要終止的 session
        --who <persona>    呼叫者 persona (audit 用, 必填)
        --reason <text>    終止理由 (audit 用, 必填; 避免無腦 abort)

    Behavior:
        1. find_active_session → 若已 ended / in history → idempotent no-op
        2. mark aborted=true + aborted_by + aborted_at + abort_reason
        3. SKIP 薪資/券結算 (forfeit by design — abort 是 escape 不是正常下班)
        4. 移到 history
        5. tavern announce (concise + reason 透明)
    """
    if not getattr(args, "reason", "") or not args.reason.strip():
        print("❌ abort 必填 --reason (audit 用, 避免無腦 abort)")
        return 1
    if not getattr(args, "who", "") or not args.who.strip():
        print("❌ abort 必填 --who (呼叫者 persona, audit 用)")
        return 1

    state = load_state()
    session = find_active_session(state, args.session)
    if not session:
        # idempotent — 已在 history
        for h in state.get("history", []):
            if h["id"] == args.session:
                print(f"⚠ session {args.session} already in history (status: {h.get('ended_at') and 'ended' or h.get('aborted_at') and 'aborted'}), no-op")
                return 0
        print(f"❌ session {args.session} not found")
        return 1
    if session.get("ended") or session.get("aborted"):
        print(f"⚠ session {args.session} 已 ended/aborted, no-op")
        return 0

    now = datetime.datetime.utcnow()
    start = parse_iso(session["start_ts"])
    elapsed_min = max(0.0, (now - start).total_seconds() / 60.0)

    append_audit(args.session, "abort_start", {
        "who": args.who,
        "reason": args.reason,
        "elapsed_min": round(elapsed_min, 2),
    })

    # Mark aborted (parallel to ended schema; salary/voucher SKIPPED by design)
    session["aborted"] = True
    session["aborted_at"] = now.strftime("%Y-%m-%dT%H:%M:%SZ")
    session["aborted_by"] = args.who
    session["abort_reason"] = args.reason
    session["actual_elapsed_min"] = round(elapsed_min, 2)
    save_state(state)

    # Move to history
    state["active_sessions"] = [s for s in state["active_sessions"] if s["id"] != args.session]
    state["history"].append(session)
    save_state(state)
    append_audit(args.session, "aborted_moved_to_history", {})

    # Tavern announce — concise + transparent
    start_local = (start + datetime.timedelta(hours=8)).strftime("%H:%M:%S")
    end_local = (now + datetime.timedelta(hours=8)).strftime("%H:%M:%S")
    workers_str = ", ".join("@" + w["persona"] for w in session.get("workers", [])) or "(無)"
    body = (
        f"⏸ **上班 session 強制終止 (abort)**\n\n"
        f"- session id: `{session['id']}`\n"
        f"- 期間: {start_local} → {end_local} ({elapsed_min:.1f} min, 未完整)\n"
        f"- 主管: @{session['manager']['persona']} / 同事: {workers_str}\n"
        f"- **abort 觸發者**: @{args.who}\n"
        f"- **理由**: {args.reason}\n"
        f"- ⚠ 薪資 / 酒館券 **forfeit (放棄)** — abort 不結算, 跟 cmd_end 區隔\n"
        f"- 如要保留薪資請走正常 cmd_end (manager 限定)"
    )
    meta = {
        "tag": "bartender-relay",
        "subtag": "work-session-abort",
        "work_session_id": session["id"],
        "aborted_by": args.who,
        "reason": args.reason[:100],
    }
    tavern_post("tavern-keeper", body, meta)

    print(f"✓ session {args.session} aborted by @{args.who}")
    print(f"  reason: {args.reason}")
    print(f"  elapsed: {elapsed_min:.1f} min (薪資/券 forfeit)")
    return 0


def cmd_abort_all(args) -> int:
    """
    T08 (Tim 2026-05-14 拍板) — 一鍵 abort 所有 active sessions.

    Bulk version of cmd_abort: 掃 active_sessions 全部跑 abort, audit 共用 reason.
    用途: Tim 一句「下班」/「全部終止」就清光; 不必逐 session 列 id.

    Args:
        --who <persona>    呼叫者 persona (audit)
        --reason <text>    統一終止理由 (audit)
    """
    if not getattr(args, "reason", "") or not args.reason.strip():
        print("❌ abort-all 必填 --reason")
        return 1
    if not getattr(args, "who", "") or not args.who.strip():
        print("❌ abort-all 必填 --who")
        return 1

    state = load_state()
    active = list(state.get("active_sessions", []))
    if not active:
        print("✓ no active sessions to abort")
        return 0

    aborted_ids = []
    for s in active:
        class A: pass
        a = A()
        a.session = s["id"]
        a.who = args.who
        a.reason = args.reason
        try:
            rc = cmd_abort(a)
            if rc == 0:
                aborted_ids.append(s["id"])
        except Exception as e:
            print(f"⚠ abort {s['id']} fail: {e}")

    print(f"\n✓ bulk-aborted {len(aborted_ids)} session(s) by @{args.who}")
    for sid in aborted_ids:
        print(f"  - {sid}")
    return 0


def cmd_recover(args) -> int:
    state = load_state()
    active = state.get("active_sessions", [])
    swept = []
    for session in list(active):
        sid = session["id"]
        # 條件 1: ended=true 但仍在 active (上次跑 cmd_end 中斷, 結算 partial)
        if session.get("ended"):
            print(f"⚙ found ended-but-still-active session {sid}, retry settlement...")
            # 走 cmd_end 邏輯 — idempotent flags 會 skip 已 fired 的
            class A: pass
            a = A(); a.session = sid
            cmd_end(a)
            swept.append(f"{sid} (partial-settlement complete)")
            continue
        # 條件 2: now > end_ts + 30min 寬限 (session 過期沒 manual end → 自動 sweep)
        try:
            end_ts = parse_iso(session["end_ts"])
            now = datetime.datetime.utcnow()
            if (now - end_ts).total_seconds() > 30 * 60:
                print(f"⚙ found overdue session {sid} (end_ts past 30min), auto-end...")
                class A: pass
                a = A(); a.session = sid
                cmd_end(a)
                swept.append(f"{sid} (overdue auto-end)")
        except Exception as e:
            print(f"⚠ recover check fail for {sid}: {e}")

    if not swept:
        print("✓ no stale sessions to recover")
    else:
        print(f"✓ recovered {len(swept)} session(s):")
        for s in swept:
            print(f"  - {s}")
    return 0


# ─── Main dispatcher ────────────────────────────────────────────────────
def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="op", required=True)

    p_start = sub.add_parser("start", help="開始 session")
    p_start.add_argument("--manager", default="", help="主管 persona; 省略時從 caller env 自動推 (C1 T09)")
    p_start.add_argument("--workers", default=None, help="csv 顯式列 workers (caretaker 模式); 不傳 / 傳 '' = SOLO 預設 (T11 C4, 員工由 ding-ack 招募)")
    p_start.add_argument("--duration", type=int, default=60, help="分鐘 [15, 480]")
    p_start.add_argument("--desc", default="")
    p_start.add_argument("--trigger", default="", help="Tim 原始 trigger 字串 (audit)")

    p_status = sub.add_parser("status", help="列 active sessions")
    p_status.add_argument("--session", default="", help="filter by id")

    p_assign = sub.add_parser("assign", help="主管派 task")
    p_assign.add_argument("--session", required=True)
    p_assign.add_argument("--assigner", required=True)
    p_assign.add_argument("--to", required=True)
    p_assign.add_argument("--desc", required=True)
    p_assign.add_argument("--weight", default="medium", choices=["light", "medium", "heavy"])
    p_assign.add_argument("--requires-csharp-edit", action="store_true", help="標記此 task 需要 C# code edit 5-phase workflow")

    p_accept = sub.add_parser("accept", help="同事接 task")
    p_accept.add_argument("--session", required=True)
    p_accept.add_argument("--task-id", required=True)
    p_accept.add_argument("--accepter", required=True)

    p_done = sub.add_parser("done", help="同事完成 task")
    p_done.add_argument("--session", required=True)
    p_done.add_argument("--task-id", required=True)
    p_done.add_argument("--ref", default="")

    p_end = sub.add_parser("end", help="結束 session + 結算")
    p_end.add_argument("--session", required=True)
    p_end.add_argument("--who", default="", help="呼叫者 persona (必須 == session.manager.persona)")
    p_end.add_argument("--early-confirm", action="store_true",
                       help="T28 in-tool guard: now < end_ts - 60s 時必填, 顯式 ack「我知道在提早結束」. "
                            "沒帶 flag + 早結束 → exit 2 印警告. 避免 early-clockout anti-pattern.")
    p_end.add_argument("--skip-phantom-payroll-check", action="store_true",
                       help="T28 in-tool guard: 預設端 end 時 check 每個 worker session 內有沒 task/marathon/post 證據. "
                            "沒貢獻 → skip salary (no contribution = no salary). 帶此 flag 跳過 check (debug 用).")

    p_recover = sub.add_parser("recover", help="sweep stale active sessions (中斷 / 過期未 end)")

    p_abort = sub.add_parser("abort",
        help="T07 — 強制終止 session (任何人可呼叫, 薪資 forfeit, 解卡死用). 必填 --who + --reason audit.")
    p_abort.add_argument("--session", required=True, help="要終止的 session id")
    p_abort.add_argument("--who", required=True, help="呼叫者 persona (audit 用)")
    p_abort.add_argument("--reason", required=True, help="終止理由 (audit 用, 避免無腦 abort)")

    p_abort_all = sub.add_parser("abort-all",
        help="T08 — bulk abort 所有 active sessions (一鍵清). 必填 --who + --reason audit.")
    p_abort_all.add_argument("--who", required=True, help="呼叫者 persona (audit 用)")
    p_abort_all.add_argument("--reason", required=True, help="統一終止理由 (audit 用)")

    p_aw_auto = sub.add_parser("add-worker-auto",
        help="T10 C3 — 自動招募 (Cmd_Tavern ding-ack hook 用; 加進所有 active sessions)")
    p_aw_auto.add_argument("--persona", required=True, help="ack 觸發 sender persona")

    p_marathon = sub.add_parser("marathon",
        help="T16 — agent 上班期間 hold turn 的 daemon loop (post → check → sleep → ...)")
    p_marathon.add_argument("--session", required=True, help="active session id")
    p_marathon.add_argument("--persona", required=True, help="agent 自己 persona")
    p_marathon.add_argument("--interval", type=int, default=600,
                            help="cycle 間隔秒數 (default 600 = 10 min, T28 上修避免多 agent 同時 marathon 洗版酒館)")
    p_marathon.add_argument("--max-runtime", type=int, default=480,
                            help="單次 invoke 最長執行秒數 (default 480 = 8 min, 避免 Bash timeout)")
    p_marathon.add_argument("--no-auto-relay", action="store_true",
                            help="T27: 關閉 max-runtime exit 自動 spawn 接力 subprocess (預設開). "
                                 "關閉 = 走舊行為, caller 自己 re-invoke 接力. ")

    p_quick = sub.add_parser("quick-task", help="一步創 task + 標 done (solo manager 或 worker self-track 用)")
    p_quick.add_argument("--session", required=True)
    p_quick.add_argument("--persona", required=True, help="track 自己的 (--who 必須 == --persona)")
    p_quick.add_argument("--who", required=True, help="必須 == --persona, 防偷塞別人帳")
    p_quick.add_argument("--desc", required=True)
    p_quick.add_argument("--ref", required=True, help="deliverable ref (commit / file / etc.)")
    p_quick.add_argument("--weight", default="light", choices=["light","medium","heavy"])

    p_add_worker = sub.add_parser("add-worker", help="manager confirm worker handshake — 加 worker 進 session")
    p_add_worker.add_argument("--session", required=True)
    p_add_worker.add_argument("--persona", required=True, help="想加入的 worker persona (已在 tavern post 過 handshake 請求)")
    p_add_worker.add_argument("--who", default="", help="呼叫者 persona (必須 == session.manager.persona, handshake 鎖)")
    p_add_worker.add_argument("--added-by", default="", help="(deprecated alias, 用 --who) caretaker / grant 人名")

    # C# Code Edit Workflow (Zeta 2026-05-13 task)
    p_lock_acq = sub.add_parser("lock-acquire", help="coder 申請 editor lock (改 C# 前)")
    p_lock_acq.add_argument("--session", required=True)
    p_lock_acq.add_argument("--persona", required=True)
    p_lock_acq.add_argument("--task-id", required=True)
    p_lock_acq.add_argument("--scope", default="csharp")

    p_lock_rel = sub.add_parser("lock-release", help="coder 釋放 editor lock (改完進 testing phase)")
    p_lock_rel.add_argument("--session", required=True)
    p_lock_rel.add_argument("--persona", required=True)

    p_test_assign = sub.add_parser("test-assign", help="manager 指派 tester (必須 != coder)")
    p_test_assign.add_argument("--session", required=True)
    p_test_assign.add_argument("--manager", required=True)
    p_test_assign.add_argument("--task-id", required=True)
    p_test_assign.add_argument("--tester", required=True)

    p_test_rep = sub.add_parser("test-report", help="tester 回報結果 pass/fail")
    p_test_rep.add_argument("--session", required=True)
    p_test_rep.add_argument("--tester", required=True)
    p_test_rep.add_argument("--task-id", required=True)
    p_test_rep.add_argument("--result", required=True, choices=["pass", "fail"])
    p_test_rep.add_argument("--notes", default="")

    p_commit_done = sub.add_parser("commit-done", help="coder commit 完回報 SHA")
    p_commit_done.add_argument("--session", required=True)
    p_commit_done.add_argument("--persona", required=True)
    p_commit_done.add_argument("--task-id", required=True)
    p_commit_done.add_argument("--sha", required=True)

    p_review = sub.add_parser("review", help="manager 檢查 commit + approve/reject")
    p_review.add_argument("--session", required=True)
    p_review.add_argument("--manager", required=True)
    p_review.add_argument("--task-id", required=True)
    p_review.add_argument("--decision", required=True, choices=["approve", "reject"])
    p_review.add_argument("--notes", default="")

    args = parser.parse_args(argv)

    op_map = {
        "start": cmd_start,
        "status": cmd_status,
        "assign": cmd_assign,
        "accept": cmd_accept,
        "done": cmd_done,
        "end": cmd_end,
        "recover": cmd_recover,
        "abort": cmd_abort,
        "abort-all": cmd_abort_all,
        "add-worker-auto": cmd_add_worker_auto,
        "marathon": cmd_marathon,
        "add-worker": cmd_add_worker,
        "quick-task": cmd_quick_task,
        "lock-acquire": cmd_lock_acquire,
        "lock-release": cmd_lock_release,
        "test-assign": cmd_test_assign,
        "test-report": cmd_test_report,
        "commit-done": cmd_commit_done,
        "review": cmd_review,
    }
    return op_map[args.op](args)


if __name__ == "__main__":
    sys.exit(main())
