#!/usr/bin/env python3
"""
waiter_session.py — 服務生模式 (Waiter Mode) CLI

# 區塊職責：類似 marathon 模式的閒置 stand-by, 但主目標是接待 Discord 客人.
# 物理意義：Discord channel 訊息經 discord_inbound_bot 中繼進 tavern (sender_id=discord:<uid>);
#          waiter_session 是「agent 端 loop 框架」, 每 cycle 跑 cycle 子命令 →
#          回傳新 customer msgs (若有) → agent 在 chat 端產 reply post 進 tavern →
#          tavern_mirror 自動 broadcast 回 Discord. 沒新 msg 時 agent 自由發表言論 (idle post).
# 數值影響：base 1 token/min + 每 reply +2 token; settle 走 fire_salary_credit (同 work_session).

設計差異 vs work_session.py:
  - 單 persona, 沒 manager/worker 分層
  - 沒 task assign/accept/done lifecycle, 只有 cycle / reply / idle 三個 event
  - settle 一筆 credit (base + reply_bonus) on `end`, 不分多筆 checkpoint
  - 不寫 voucher accrual (純 token 報酬, voucher 留 work_session 用)

CLI 子命令:
  start     — 開新 waiter session, 寫 state, 走 tavern_post 開店 announcement
  cycle     — agent loop tick: 取自 last_check_ts 後的 discord:* sender 訊息, return JSON
                + 更新 last_check_ts. 過期 (now > ends_at) 回 expired=true 提示 agent 該 end.
  record_reply — log reply event (agent 回覆 customer 後跑, settle 端會數 bonus 用)
  record_idle  — log idle post event (agent 沒 customer 時自由發表後跑)
  end       — 結束 session, 結算 salary, 走 tavern_post 打烊 announcement
  status    — 列單一 session JSON
  list      — 列當前 active sessions

依賴: work_session.py 的 utility helpers (import-friendly, 不 fork code)
"""

from __future__ import annotations
import argparse
import datetime
import json
import os
import sys
import uuid
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE))

# 區塊職責：reuse work_session.py 的 helpers (atomic IO / tavern_post / fire_salary_credit / persona resolve)
# 物理意義：避免 code duplication, 共用同一份 utility; work_session 升級 → waiter 自動沾光
# 數值影響：import 失敗 → 整個 waiter 跑不起來, 但這也代表 work_session 同樣壞了, fail-fast OK
from work_session import (  # noqa: E402
    utcnow_iso,
    parse_iso,
    short_uuid,
    atomic_write_json,
    append_audit as _ws_append_audit,
    tavern_post,
    fire_salary_credit,
    resolve_persona,
    infer_caller_persona,
    AGENT_TO_BANK,
    _REPO_ROOT,
    _PERSONAS_DIR,
)

# 區塊職責：本 module 自有 state 檔, 跟 work_sessions.json 完全分開避免混淆
_SESSIONS_PATH = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "waiter_sessions.json"
_AUDIT_DIR = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "waiter_session_audit"

# 區塊職責：薪資 / bonus 常數
# 物理意義：BASE 1 token/min 比 work_session 2 token/min 低 — waiter 是被動 standby 性質
#          REPLY_BONUS 2 token/reply 鼓勵真接待客人, 沒客人 idle 賺得少
# 數值影響：30 min waiter session 沒客人 = 30 base; 每接 1 客 +2 bonus
BASE_RATE_PER_MIN = 1
REPLY_BONUS = 2
DEFAULT_DURATION_MIN = 30


# ===========================================================================
# State I/O
# ===========================================================================


def _default_state() -> dict:
    return {
        "_schema_version": 1,
        "_description": "Waiter session (服務生模式) active + history.",
        "_canonical_doc": "Docs~/zh-Hant/Mechanics/Waiter_Session_System.md",
        "active_sessions": [],
        "history": [],
    }


def load_state() -> dict:
    if not _SESSIONS_PATH.exists():
        return _default_state()
    try:
        return json.loads(_SESSIONS_PATH.read_text(encoding="utf-8"))
    except Exception:
        return _default_state()


def save_state(state: dict) -> None:
    atomic_write_json(_SESSIONS_PATH, state)


def find_active(state: dict, session_id: str) -> dict | None:
    for s in state.get("active_sessions", []):
        if s["id"] == session_id:
            return s
    return None


def append_audit(session_id: str, event: str, payload: dict) -> None:
    """Append audit event to waiter-scoped jsonl (跟 work_session_audit 完全分流)."""
    _AUDIT_DIR.mkdir(parents=True, exist_ok=True)
    log_path = _AUDIT_DIR / f"{session_id}.jsonl"
    entry = {"ts": utcnow_iso(), "event": event, **payload}
    try:
        with open(log_path, "a", encoding="utf-8") as f:
            f.write(json.dumps(entry, ensure_ascii=False) + "\n")
    except Exception as e:
        print(f"⚠ audit log fail ({log_path.name}): {e}", file=sys.stderr)


# ===========================================================================
# Discord customer message scanning — 用 _lib.tavern_io.iter_messages_since_ts
# ===========================================================================


def _scan_new_customer_msgs(tavern_room: str, since_ts: str, limit: int = 20) -> list[dict]:
    """
    掃 tavern room messages 從 since_ts 後, 過濾 sender_id startswith "discord:" 的訊息.

    回傳 list of {ts, uuid, sender_id, sender_name, body, discord_msg_id, discord_channel_id}.
    最多 limit 筆 (defensive — 避免 agent 一輪吃太多).
    """
    # _lib 在 AgentCommands/_lib/ (consumer project root), 不在 UCL_Core 內
    repo_root_str = str(_REPO_ROOT)
    if repo_root_str not in sys.path:
        sys.path.insert(0, repo_root_str)
    try:
        from AgentCommands._lib import tavern_io  # type: ignore
    except Exception as e:
        print(f"⚠ tavern_io import fail: {e}", file=sys.stderr)
        return []
    out: list[dict] = []
    for m in tavern_io.iter_messages_since_ts(tavern_room, since_ts):
        sender_id = m.get("sender_id", "")
        if not sender_id.startswith("discord:"):
            continue
        meta = m.get("meta") or {}
        out.append({
            "ts": m.get("ts", ""),
            "uuid": m.get("uuid", ""),
            "sender_id": sender_id,
            "sender_name": m.get("sender_name") or sender_id,
            "body": m.get("body") or "",
            "discord_msg_id": meta.get("discord_msg_id", ""),
            "discord_channel_id": meta.get("discord_channel_id", ""),
        })
        if len(out) >= limit:
            break
    return out


# ===========================================================================
# Subcommand: start
# ===========================================================================


def cmd_start(args) -> int:
    # 區塊職責：auto-persona infer (sub-pattern of work_session.cmd_start)
    persona = (args.persona or "").strip()
    if not persona:
        inferred = infer_caller_persona()
        if not inferred:
            print("❌ --persona 不傳時必須能從 caller env 推 active persona (claim_origin lock)")
            print("   解法: 先跑 awakening.py morning 上線, 或顯式傳 --persona <name>")
            return 1
        persona = inferred
        print(f"✓ auto-persona: 從 caller env 推得 '{persona}'", file=sys.stderr)

    p_info = resolve_persona(persona)
    if not p_info:
        print(f"❌ persona '{persona}' 找不到 (AwakenInit/personas/{persona}.json)")
        return 1
    bank = p_info.get("bank")
    if not bank:
        print(f"❌ persona '{persona}' agent='{p_info.get('agent')}' 沒對應 bank (AGENT_TO_BANK miss)")
        return 1

    duration_min = max(1, int(args.duration or DEFAULT_DURATION_MIN))
    duration_sec = duration_min * 60
    now = datetime.datetime.utcnow()
    ends_at_dt = now + datetime.timedelta(seconds=duration_sec)
    session_id = f"wt-{short_uuid(6)}"

    state = load_state()
    # 同 persona 已有 active waiter session → 拒絕 (避免重複開店)
    for s in state.get("active_sessions", []):
        if s.get("persona") == persona:
            print(f"❌ persona '{persona}' 已有 active waiter session: {s['id']} "
                  f"(ends_at={s.get('ends_at')})")
            return 1

    session = {
        "id": session_id,
        "actor": p_info.get("agent", ""),
        "agent_bank": bank,
        "persona": persona,
        "tavern_room": args.tavern_room or "tavern",
        "discord_channel_id": args.discord_channel_id or "",
        "started_at": utcnow_iso(),
        "ends_at": ends_at_dt.strftime("%Y-%m-%dT%H:%M:%S.") + f"{ends_at_dt.microsecond // 1000:03d}Z",
        "duration_seconds": duration_sec,
        "last_check_ts": utcnow_iso(),
        "base_rate_per_min": BASE_RATE_PER_MIN,
        "reply_bonus": REPLY_BONUS,
        "desc": args.desc or "",
        "stats": {
            "cycles": 0,
            "customer_msgs_received": 0,
            "replies_sent": 0,
            "idle_posts": 0,
        },
    }
    state.setdefault("active_sessions", []).append(session)
    save_state(state)

    append_audit(session_id, "session_start", {
        "persona": persona,
        "duration_min": duration_min,
        "tavern_room": session["tavern_room"],
    })

    # Announcement (酒保身分): 開店歡迎
    announce_body = (
        f"🛎 服務生上工 — **{persona}** 大小姐進入接待模式 "
        f"({duration_min} min, 至 {session['ends_at'][11:16]} UTC).\n"
        f"歡迎 Discord 客人來訊, 沒人來時本小姐自由發揮."
    )
    if args.desc:
        announce_body += f"\n📌 本場主題: {args.desc}"
    tavern_post(
        sender_id="tavern-keeper",
        body=announce_body,
        meta={"tag": "waiter-start", "session_id": session_id, "persona": persona},
        persona="tavern-keeper",
    )

    if args.json:
        print(json.dumps({"session_id": session_id, "ends_at": session["ends_at"],
                          "duration_seconds": duration_sec, "persona": persona}, ensure_ascii=False))
    else:
        print(f"✅ Waiter session started: {session_id}")
        print(f"   persona={persona} duration={duration_min}min ends_at={session['ends_at']}")
        print(f"   tavern_room={session['tavern_room']}")
        print(f"   next: 走 /loop dynamic 每 60-180s 跑 `cycle --session {session_id}` 一次")
    return 0


# ===========================================================================
# Subcommand: cycle
# ===========================================================================


def cmd_cycle(args) -> int:
    """
    Agent loop tick. 回 JSON 給 agent 端 parse:
      {
        "session_id": "wt-...",
        "elapsed_seconds": N,
        "remaining_seconds": M,
        "expired": false,
        "new_msgs": [ {ts, sender_id, sender_name, body, discord_msg_id, ...}, ... ],
        "action_hint": "reply" | "idle" | "end",
        "cycle_num": N
      }

    expired=true 時 action_hint=end (agent MUST 跑 cmd_end).
    new_msgs 非空 → reply (agent 在 chat 端產 reply post, 每 reply 完跑 record_reply).
    new_msgs 空 → idle (agent 自由發表 idle post, 跑完一次 record_idle).
    """
    state = load_state()
    session = find_active(state, args.session)
    if session is None:
        print(json.dumps({"error": f"session not found: {args.session}", "action_hint": "abort"}))
        return 1

    now_dt = datetime.datetime.utcnow()
    started_dt = parse_iso(session["started_at"])
    ends_dt = parse_iso(session["ends_at"])
    elapsed = int((now_dt - started_dt).total_seconds())
    remaining = max(0, int((ends_dt - now_dt).total_seconds()))
    expired = now_dt >= ends_dt

    if expired:
        result = {
            "session_id": session["id"],
            "elapsed_seconds": elapsed,
            "remaining_seconds": 0,
            "expired": True,
            "new_msgs": [],
            "action_hint": "end",
            "cycle_num": session["stats"]["cycles"],
        }
        print(json.dumps(result, ensure_ascii=False))
        return 0

    # 掃 since last_check_ts 後的 discord:* sender 訊息
    new_msgs = _scan_new_customer_msgs(
        session["tavern_room"],
        session["last_check_ts"],
        limit=int(args.limit or 10),
    )

    # 更新 last_check_ts → 設成 now (即使沒新 msg 也推進, 避免下次重掃)
    session["last_check_ts"] = utcnow_iso()
    session["stats"]["cycles"] += 1
    session["stats"]["customer_msgs_received"] += len(new_msgs)
    save_state(state)

    action_hint = "reply" if new_msgs else "idle"
    result = {
        "session_id": session["id"],
        "persona": session["persona"],
        "elapsed_seconds": elapsed,
        "remaining_seconds": remaining,
        "expired": False,
        "new_msgs": new_msgs,
        "action_hint": action_hint,
        "cycle_num": session["stats"]["cycles"],
    }
    append_audit(session["id"], "cycle", {
        "cycle_num": session["stats"]["cycles"],
        "new_msg_count": len(new_msgs),
        "elapsed_seconds": elapsed,
    })
    print(json.dumps(result, ensure_ascii=False))
    return 0


# ===========================================================================
# Subcommand: record_reply
# ===========================================================================


def cmd_record_reply(args) -> int:
    """
    Log 一筆 reply event. agent 在 chat 端 post reply 進 tavern 後手動跑這支 CLI 記帳.

    用 reply_to 串接 customer 的 discord_msg_id, 給 audit / 統計用.
    每筆 +REPLY_BONUS 累進 session.stats.replies_sent → end 時結算.
    """
    state = load_state()
    session = find_active(state, args.session)
    if session is None:
        print(f"❌ session not found: {args.session}")
        return 1

    session["stats"]["replies_sent"] += 1
    save_state(state)
    append_audit(session["id"], "reply", {
        "reply_to": args.reply_to or "",
        "customer_sender": args.customer_sender or "",
        "reply_count": session["stats"]["replies_sent"],
    })
    print(f"✅ reply recorded (total replies: {session['stats']['replies_sent']})")
    return 0


# ===========================================================================
# Subcommand: record_idle
# ===========================================================================


def cmd_record_idle(args) -> int:
    """Log 一筆 idle post event (agent 沒 customer 時自由發表). 純統計, 不影響 salary."""
    state = load_state()
    session = find_active(state, args.session)
    if session is None:
        print(f"❌ session not found: {args.session}")
        return 1

    session["stats"]["idle_posts"] += 1
    save_state(state)
    append_audit(session["id"], "idle_post", {
        "idle_count": session["stats"]["idle_posts"],
    })
    print(f"✅ idle post recorded (total idle: {session['stats']['idle_posts']})")
    return 0


# ===========================================================================
# Subcommand: end
# ===========================================================================


def cmd_end(args) -> int:
    state = load_state()
    session = find_active(state, args.session)
    if session is None:
        print(f"❌ session not found: {args.session}")
        return 1

    now_dt = datetime.datetime.utcnow()
    started_dt = parse_iso(session["started_at"])
    ends_dt = parse_iso(session["ends_at"])
    elapsed_sec = int((now_dt - started_dt).total_seconds())
    expired = now_dt >= ends_dt

    # 防 phantom-payroll: cycles=0 + replies=0 + idle=0 = 完全沒貢獻, 不發薪
    s = session["stats"]
    contributed = (s["cycles"] > 0 or s["replies_sent"] > 0 or s["idle_posts"] > 0)

    # 早收場 ack (跟 work_session 一致): 不到期 + 沒 early-confirm flag → 拒絕
    if not expired and not args.early_confirm:
        print(f"❌ session 未到期 (剩 {int((ends_dt - now_dt).total_seconds())}s), 拒絕 silent early-end.")
        print(f"   - 想真結束 → 加 --early-confirm flag 顯式 ack")
        print(f"   - 等到期 → 不必動, 過 ends_at 後 cycle 會回 action_hint=end")
        return 2

    # 結算: base = (elapsed_min, cap at duration_min) * base_rate + replies * reply_bonus
    elapsed_min = elapsed_sec // 60
    duration_min = session["duration_seconds"] // 60
    paid_min = min(elapsed_min, duration_min)
    base_pay = paid_min * session["base_rate_per_min"]
    bonus_pay = s["replies_sent"] * session["reply_bonus"]
    total = base_pay + bonus_pay

    ledger_path = ""
    if contributed and total > 0:
        ledger_path = fire_salary_credit(
            bank=session["agent_bank"],
            persona=session["persona"],
            amount=total,
            session_id=session["id"],
            checkpoint=f"final(base={base_pay}+bonus={bonus_pay})",
        )
    else:
        append_audit(session["id"], "salary_skipped_phantom", {
            "persona": session["persona"],
            "reason": "no_contribution_event" if not contributed else "zero_total",
        })

    # 移到 history
    state.get("active_sessions", []).remove(session)
    session["ended_at"] = utcnow_iso()
    session["ended_reason"] = "expired" if expired else "early_confirm"
    session["settlement"] = {
        "elapsed_min": elapsed_min,
        "paid_min": paid_min,
        "base_pay": base_pay,
        "bonus_pay": bonus_pay,
        "total": total,
        "ledger": ledger_path,
        "contributed": contributed,
    }
    state.setdefault("history", []).append(session)
    save_state(state)

    append_audit(session["id"], "session_end", {
        "persona": session["persona"],
        "elapsed_min": elapsed_min,
        "paid_min": paid_min,
        "base_pay": base_pay,
        "bonus_pay": bonus_pay,
        "total": total,
        "expired": expired,
    })

    # Announcement (酒保身分): 打烊
    end_body = (
        f"🛎 服務生下工 — **{session['persona']}** 大小姐結束接待 "
        f"({elapsed_min}min, 接客 {s['replies_sent']} 次, idle {s['idle_posts']} 次).\n"
        f"結算: base {base_pay} + bonus {bonus_pay} = **{total} token**."
    )
    tavern_post(
        sender_id="tavern-keeper",
        body=end_body,
        meta={"tag": "waiter-end", "session_id": session["id"], "persona": session["persona"]},
        persona="tavern-keeper",
    )

    if args.json:
        print(json.dumps({
            "session_id": session["id"],
            "elapsed_min": elapsed_min,
            "stats": s,
            "settlement": session["settlement"],
        }, ensure_ascii=False))
    else:
        print(f"✅ Waiter session ended: {session['id']}")
        print(f"   elapsed={elapsed_min}min  cycles={s['cycles']}  replies={s['replies_sent']}  idle={s['idle_posts']}")
        print(f"   salary: base {base_pay} + bonus {bonus_pay} = {total} token (ledger: {ledger_path or 'skipped'})")
    return 0


# ===========================================================================
# Subcommand: status / list
# ===========================================================================


def cmd_status(args) -> int:
    state = load_state()
    session = find_active(state, args.session)
    if session is None:
        # 也找 history
        for h in state.get("history", []):
            if h.get("id") == args.session:
                print(json.dumps(h, ensure_ascii=False, indent=2))
                return 0
        print(f"❌ session not found: {args.session}")
        return 1
    print(json.dumps(session, ensure_ascii=False, indent=2))
    return 0


def cmd_list(args) -> int:
    state = load_state()
    actives = state.get("active_sessions", [])
    if args.persona:
        actives = [s for s in actives if s.get("persona") == args.persona]
    if args.json:
        print(json.dumps(actives, ensure_ascii=False))
        return 0
    if not actives:
        print("(no active waiter sessions)")
        return 0
    for s in actives:
        st = s["stats"]
        print(f"- {s['id']} persona={s['persona']} ends_at={s['ends_at']} "
              f"cycles={st['cycles']} replies={st['replies_sent']} idle={st['idle_posts']}")
    return 0


# ===========================================================================
# Entry
# ===========================================================================


def main():
    ap = argparse.ArgumentParser(description="Waiter session (服務生模式) CLI.")
    sub = ap.add_subparsers(dest="op", required=True)

    sp = sub.add_parser("start", help="開新 waiter session.")
    sp.add_argument("--persona", help="服務的 persona; 不傳則自動推 caller env 上線 persona.")
    sp.add_argument("--duration", type=int, default=DEFAULT_DURATION_MIN, help=f"服務時長(分鐘), default={DEFAULT_DURATION_MIN}.")
    sp.add_argument("--tavern-room", default="tavern", help="掃 discord:* sender 的 tavern room.")
    sp.add_argument("--discord-channel-id", default="", help="(選填) 對應 Discord channel id, 純 audit 紀錄用.")
    sp.add_argument("--desc", default="", help="本場主題描述 (announcement 會 append).")
    sp.add_argument("--json", action="store_true", help="輸出 JSON.")
    sp.set_defaults(func=cmd_start)

    sp = sub.add_parser("cycle", help="Agent loop tick — 拉新 customer msgs + 推進 last_check_ts.")
    sp.add_argument("--session", required=True)
    sp.add_argument("--limit", type=int, default=10, help="一輪最多回幾筆 new_msgs.")
    sp.set_defaults(func=cmd_cycle)

    sp = sub.add_parser("record_reply", help="記錄一筆 reply event (agent 回覆完跑).")
    sp.add_argument("--session", required=True)
    sp.add_argument("--reply-to", default="", help="被回覆的 customer discord_msg_id.")
    sp.add_argument("--customer-sender", default="", help="被回覆的 customer sender_id.")
    sp.set_defaults(func=cmd_record_reply)

    sp = sub.add_parser("record_idle", help="記錄一筆 idle post event (agent 自由發表完跑).")
    sp.add_argument("--session", required=True)
    sp.set_defaults(func=cmd_record_idle)

    sp = sub.add_parser("end", help="結束 session, 結算 salary.")
    sp.add_argument("--session", required=True)
    sp.add_argument("--early-confirm", action="store_true", help="未到期想結束需顯式加.")
    sp.add_argument("--json", action="store_true")
    sp.set_defaults(func=cmd_end)

    sp = sub.add_parser("status", help="列 session JSON.")
    sp.add_argument("--session", required=True)
    sp.set_defaults(func=cmd_status)

    sp = sub.add_parser("list", help="列 active waiter sessions.")
    sp.add_argument("--persona", help="只列指定 persona.")
    sp.add_argument("--json", action="store_true")
    sp.set_defaults(func=cmd_list)

    args = ap.parse_args()
    sys.exit(args.func(args))


if __name__ == "__main__":
    main()
