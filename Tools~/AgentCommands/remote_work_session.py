#!/usr/bin/env python3
"""
remote_work_session.py — 遠端工作模式 (Remote Work Mode) CLI

# 區塊職責：Tim 外出時手機 Discord 唯一介面, agent 透過指定工作頻道接受 task / 回報進度.
# 物理意義：基於 waiter pattern 變體 — 但對象固定 (Tim only), channel 固定 (work channel), 互動模式
#          是「task confirmation + progress report」非「客人接待 reply」. Agent 自由動工期間 idle
#          會走 status update post 讓 Tim 手機端看見「還活著」.
# 數值影響：base 1.5 token/min (高於 waiter 1, 反映遠端責任) + 完成 task 2 token bonus per confirm_task done.

設計差異 vs waiter_session.py:
  - 預設只監聽單一 work channel (CMD --discord-channel-id 設定 / fallback discord_channel_routing.json work tag)
  - duration 解析彈性: '1h' / '3h' / '60m' / '60分鐘' / '1小時' / '180' (純 int 視為分鐘)
  - 加 confirm_task op: agent 接到 Tim Discord 指令後跑此 op 跟 Tim 在 Discord 端確認 task scope
  - 加 report_progress op: agent 動工中定期回報 (idle 變 progress, 替代 waiter 的純發呆 idle)
  - 加 task_done op: 標記 task 完成, 觸發 bonus
  - sender filter 只認 Tim (default discord:383604378185105408, --tim-uid CMD 可改)
  - Salary fire on end: base + bonus per confirmed task done

CLI 子命令:
  start     — 開新 remote work session, 寫 state, tavern post 開工 announcement
  cycle     — agent loop tick: 取自 last_check_ts 後的 work channel + Tim 訊息, return JSON
  confirm_task — Tim 給 task 後, agent 確認 task 範圍寫 Discord (回 tim 在 mobile 看到)
  report_progress — 動工期間進度回報 (替代 waiter idle)
  task_done — 完成一筆 Tim 指派 task (bonus 累加)
  end       — 結束 session, 結算 salary, tavern post 收工 announcement
  status    — JSON status
  list      — list active

依賴: work_session.py utility helpers (atomic IO / tavern_post / fire_salary_credit / persona resolve)
"""
from __future__ import annotations
import argparse
import datetime
import json
import re
import sys
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

_HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(_HERE))

# 區塊職責：reuse work_session.py utility helpers (跟 waiter_session 同款)
from work_session import (  # noqa: E402
    utcnow_iso,
    parse_iso,
    short_uuid,
    atomic_write_json,
    tavern_post,
    fire_salary_credit,
    resolve_persona,
    infer_caller_persona,
    AGENT_TO_BANK,
    _REPO_ROOT,
)

# 區塊職責：state files 跟 waiter / work_session 完全分流
_SESSIONS_PATH = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "remote_work_sessions.json"
_AUDIT_DIR = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "remote_work_session_audit"
_ROUTING_PATH = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "discord_channel_routing.json"

# 區塊職責：薪資 / bonus 常數 (CMD --rate / --task-bonus 可覆蓋)
# 物理意義：BASE 2 token/min 對齊 ucl-work-session 規範 (Tim 2026-05-18 拍板「報酬參考上班時間指令」);
#          TASK_BONUS 2 token / confirmed task done 鼓勵真接 Tim 派的 task;
#          VOUCHER_INTERVAL_MIN 5 對齊 work_session 酒館券機制 (per-persona schema v2)
# 數值影響：1h session 沒 task = 120 base (2*60); 接 3 task done = +6 bonus; 1h voucher = 12 張
BASE_RATE_PER_MIN = 2              # tokens per minute (aligned with ucl-work-session)
TASK_BONUS = 2
VOUCHER_INTERVAL_MIN = 5           # 1 voucher per N min (aligned with ucl-work-session)
DEFAULT_DURATION_MIN = 60
DEFAULT_TIM_UID = "383604378185105408"   # 可走 --tim-uid CMD 覆蓋
DEFAULT_LOCAL_TZ = "Asia/Taipei"          # HH:mm 解析使用的時區 (對齊 bartender time_rules)
BARTENDER_SENDER = "tavern-keeper"        # 酒保身份廣播 — start/end 通知統一以酒保 NPC 發
BARTENDER_PERSONA = "tavern-keeper"


# ===========================================================================
# Duration parser — 支援多格式
# ===========================================================================


def parse_duration(text: str) -> int:
    """解析 duration 字串回傳分鐘 int.

    支援格式 (case-insensitive):
      '60' / '60m' / '60min' / '60分' / '60分鐘'   → 60
      '1h' / '1小時' / '1hr' / '1hour'            → 60
      '3h' / '3小時'                              → 180
      '1.5h' / '1.5小時'                          → 90
      '90s' / '90秒'                              → 2 (ceil to min, defensive)

    無法解析 → 預設 DEFAULT_DURATION_MIN.
    """
    if text is None:
        return DEFAULT_DURATION_MIN
    s = str(text).strip().lower()
    if not s:
        return DEFAULT_DURATION_MIN
    # 純數字 = 分鐘
    if s.isdigit():
        return max(1, int(s))
    # 區塊職責：regex 匹配 數字 + 單位 (中英文)
    # 物理意義：1h / 3小時 / 60m / 60分鐘 / 90s / 1.5h 多形式覆蓋
    # 數值影響：h × 60, m × 1, s ÷ 60 (ceil); 失敗 fallback default
    m = re.match(r"^(\d+(?:\.\d+)?)\s*(h|hr|hour|hours|小時|m|min|mins|minute|minutes|分|分鐘|s|sec|secs|second|seconds|秒)?$", s)
    if not m:
        return DEFAULT_DURATION_MIN
    num = float(m.group(1))
    unit = (m.group(2) or "m").lower()
    if unit in ("h", "hr", "hour", "hours", "小時"):
        return max(1, int(num * 60))
    if unit in ("m", "min", "mins", "minute", "minutes", "分", "分鐘"):
        return max(1, int(num))
    if unit in ("s", "sec", "secs", "second", "seconds", "秒"):
        return max(1, int((num + 59) / 60))   # ceil
    return DEFAULT_DURATION_MIN


# ===========================================================================
# Clock-time parser (Tim 2026-05-18 重構) — HH:mm / ISO → datetime
# ===========================================================================


def _local_now() -> datetime.datetime:
    """取 DEFAULT_LOCAL_TZ 當前 datetime (naive — 跟 HH:mm 對齊比較用).

    為何用 naive: 跟 parse_clock_time 回傳的 naive datetime 直接比, 避免 tzinfo 不對等
    報 TypeError. 換算 UTC 在 to_utc_iso() 統一處理.

    Windows 端 tzdata 缺失 fallback: ZoneInfo('Asia/Taipei') 會 throw, 改用系統 local time
    (假設系統 TZ 跟 DEFAULT_LOCAL_TZ 一致 — 大部分情境成立).
    """
    try:
        from zoneinfo import ZoneInfo  # Python 3.9+
        return datetime.datetime.now(ZoneInfo(DEFAULT_LOCAL_TZ)).replace(tzinfo=None)
    except Exception:
        # Fallback: 系統 local time (Windows 沒裝 tzdata 走此路)
        return datetime.datetime.now()


def utc_to_local(utc_dt: datetime.datetime) -> datetime.datetime:
    """UTC naive → DEFAULT_LOCAL_TZ naive datetime.

    對偶 to_utc_iso. 用於 end announcement 顯示 (從 storage UTC ISO 回到使用者可讀 local HH:mm).
    Windows tzdata 缺失走 system local TZ fallback (跟 to_utc_iso 同款 fallback 策略).
    """
    try:
        from zoneinfo import ZoneInfo
        aware_utc = utc_dt.replace(tzinfo=datetime.timezone.utc)
        local_aware = aware_utc.astimezone(ZoneInfo(DEFAULT_LOCAL_TZ))
        return local_aware.replace(tzinfo=None)
    except Exception:
        # Fallback: aware UTC → system local
        aware_utc = utc_dt.replace(tzinfo=datetime.timezone.utc)
        return aware_utc.astimezone().replace(tzinfo=None)


def to_utc_iso(local_dt: datetime.datetime) -> str:
    """把 naive local datetime 轉成 UTC ISO (Z 結尾, ms precision).

    本檔其他地方都用 UTC ISO 為 storage canonical, 此函式是 local↔UTC 邊界.

    兩階段 fallback (Windows tzdata 缺失場景, blood-debugged 2026-05-18):
      1. ZoneInfo(DEFAULT_LOCAL_TZ) — Linux/macOS / Windows 有裝 tzdata pkg
      2. naive.astimezone() — Python 3.6+ 把 naive 當系統 local TZ → aware, 再轉 UTC
      原本第 2 fallback 寫 `utc = local_dt` 等同假設 local == UTC, 是 bug (Windows 命中)
    """
    try:
        from zoneinfo import ZoneInfo
        tz_local = ZoneInfo(DEFAULT_LOCAL_TZ)
        aware = local_dt.replace(tzinfo=tz_local)
        utc = aware.astimezone(datetime.timezone.utc).replace(tzinfo=None)
    except Exception:
        # Fallback: 用系統 local TZ (naive → aware → UTC, 跨 OS 通用)
        aware = local_dt.astimezone()
        utc = aware.astimezone(datetime.timezone.utc).replace(tzinfo=None)
    return utc.strftime("%Y-%m-%dT%H:%M:%S.") + f"{utc.microsecond // 1000:03d}Z"


def parse_clock_time(text: str, base_local: datetime.datetime | None = None,
                     wrap_past_to_tomorrow: bool = True) -> datetime.datetime | None:
    """解析 HH:mm / HH:mm:ss / ISO datetime → naive local datetime.

    Args:
      text: '16:00' / '09:30' / '2026-05-18T16:00:00' / '2026-05-18 16:00'
      base_local: 比對基準 (naive local datetime); 預設 _local_now()
      wrap_past_to_tomorrow: 若 HH:mm 解析後早於 base → 加 1 天 (e.g. 現在 22:00 用戶說 02:00 = 隔天凌晨)

    回 None 表示解析失敗 (caller 該 fallback to duration / default).
    """
    if not text:
        return None
    s = str(text).strip()
    if not s:
        return None
    base = base_local or _local_now()

    # ISO 格式 (有日期)
    try:
        # 'T' or space separator both work
        normalized = s.replace("Z", "").replace("T", " ")
        dt = datetime.datetime.strptime(normalized[:19], "%Y-%m-%d %H:%M:%S") if len(normalized) >= 19 \
            else datetime.datetime.strptime(normalized[:16], "%Y-%m-%d %H:%M")
        return dt
    except Exception:
        pass

    # HH:mm[:ss] (純時鐘 — 套用今天日期)
    m = re.match(r"^(\d{1,2}):(\d{2})(?::(\d{2}))?$", s)
    if m:
        hh = int(m.group(1))
        mm = int(m.group(2))
        ss = int(m.group(3)) if m.group(3) else 0
        if 0 <= hh <= 23 and 0 <= mm <= 59 and 0 <= ss <= 59:
            target = base.replace(hour=hh, minute=mm, second=ss, microsecond=0)
            if wrap_past_to_tomorrow and target <= base:
                target += datetime.timedelta(days=1)
            return target

    return None


# ===========================================================================
# State I/O
# ===========================================================================


def _default_state() -> dict:
    return {
        "_schema_version": 1,
        "_description": "Remote work session — Tim 外出時手機 Discord 唯一介面派 task / 回報. 跟 waiter / work_session 完全分流的 state.",
        "_canonical_doc": "Docs~/zh-Hant/Mechanics/Remote_Work_Session.md",
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
    _AUDIT_DIR.mkdir(parents=True, exist_ok=True)
    log_path = _AUDIT_DIR / f"{session_id}.jsonl"
    entry = {"ts": utcnow_iso(), "event": event, **payload}
    try:
        with open(log_path, "a", encoding="utf-8") as f:
            f.write(json.dumps(entry, ensure_ascii=False) + "\n")
    except Exception as e:
        print(f"⚠ audit log fail ({log_path.name}): {e}", file=sys.stderr)


# ===========================================================================
# Routing — 從 routing JSON 推 work channel id (fallback 給 --discord-channel-id)
# ===========================================================================


def resolve_work_channel_id() -> str:
    """從 discord_channel_routing.json 找 source_class=work 的 channel_id (priority desc).

    沒設 → 空字串 (caller 應該走 --discord-channel-id flag 顯式).
    """
    if not _ROUTING_PATH.exists():
        return ""
    try:
        d = json.loads(_ROUTING_PATH.read_text(encoding="utf-8"))
        mappings = d.get("mappings") or []
        work_mappings = [m for m in mappings if isinstance(m, dict) and m.get("source_class") == "work" and m.get("enabled", True)]
        if not work_mappings:
            return ""
        work_mappings.sort(key=lambda m: -int(m.get("priority", 0)))
        return str(work_mappings[0].get("channel_id", ""))
    except Exception:
        return ""


# ===========================================================================
# Tim msg scanning — 只認 Tim discord_uid + 指定 channel
# ===========================================================================


def _scan_tim_messages(tavern_room: str, since_ts: str, channel_id: str, tim_uid: str, limit: int = 20) -> list[dict]:
    """掃 tavern room messages, 過濾:
       - sender_id == f"discord:{tim_uid}"
       - meta.discord_channel_id == channel_id (限定 work channel)
       - ts > since_ts

    回傳排序 (priority desc, ts asc) — 但本場景一般都是 Tim 一人, priority 同; ts asc 老的先處理.
    """
    repo_root_str = str(_REPO_ROOT)
    if repo_root_str not in sys.path:
        sys.path.insert(0, repo_root_str)
    try:
        from AgentCommands._lib import tavern_io  # type: ignore
    except Exception as e:
        print(f"⚠ tavern_io import fail: {e}", file=sys.stderr)
        return []
    target_sender = f"discord:{tim_uid}"
    out: list[dict] = []
    for m in tavern_io.iter_messages_since_ts(tavern_room, since_ts):
        if m.get("sender_id") != target_sender:
            continue
        meta = m.get("meta") or {}
        # 區塊職責：channel filter — 只認指定 work channel; 空字串視為「不限」(防 routing 缺工作頻道時整套失效)
        if channel_id:
            msg_cid = str(meta.get("discord_channel_id", ""))
            if msg_cid != channel_id:
                continue
        out.append({
            "ts": m.get("ts", ""),
            "uuid": m.get("uuid", ""),
            "sender_id": m.get("sender_id"),
            "sender_name": m.get("sender_name") or "Tim",
            "body": m.get("body") or "",
            "discord_msg_id": meta.get("discord_msg_id", ""),
            "discord_channel_id": meta.get("discord_channel_id", ""),
            "source_class": meta.get("source_class", "work"),
            "priority": int(meta.get("priority", 0) or 0),
        })
        if len(out) >= limit:
            break
    # ts asc — 老的先處理 (FIFO Tim 指令)
    out.sort(key=lambda x: x["ts"])
    return out


# ===========================================================================
# Subcommand: start
# ===========================================================================


def cmd_start(args) -> int:
    persona = (args.persona or "").strip()
    if not persona:
        inferred = infer_caller_persona()
        if not inferred:
            print("❌ --persona 不傳時必須能從 caller env 推 active persona")
            return 1
        persona = inferred
        print(f"✓ auto-persona: {persona}", file=sys.stderr)
    p_info = resolve_persona(persona)
    if not p_info:
        print(f"❌ persona '{persona}' 找不到")
        return 1
    bank = p_info.get("bank")
    if not bank:
        print(f"❌ persona '{persona}' agent='{p_info.get('agent')}' 沒對應 bank")
        return 1

    # 區塊職責：start / end / duration 三 mode 解析 (Tim 2026-05-18 重構, 從 duration-only → start+end)
    # 物理意義：優先順序 (--end-time 顯式) > (--duration) > DEFAULT。--start-time 可選, 預設 = now (local TZ).
    #          --end-time 接 HH:mm (今天, 過期自動 wrap 明天) 或 ISO datetime; 跟 --duration 互斥。
    # 數值影響：ends_at storage 仍走 UTC ISO (對齊其他欄位 + cycle/end 計算); started_at 同。
    start_local_explicit = parse_clock_time(args.start_time, wrap_past_to_tomorrow=False) if args.start_time else None
    end_local_explicit = parse_clock_time(args.end_time, wrap_past_to_tomorrow=True) if args.end_time else None

    if args.end_time and args.duration and args.duration != str(DEFAULT_DURATION_MIN):
        print("❌ --end-time 跟 --duration 互斥 — 只能擇一")
        return 1
    if args.start_time and not start_local_explicit:
        print(f"❌ --start-time 解析失敗: '{args.start_time}' — 支援 HH:mm 或 ISO datetime")
        return 1
    if args.end_time and not end_local_explicit:
        print(f"❌ --end-time 解析失敗: '{args.end_time}' — 支援 HH:mm 或 ISO datetime")
        return 1

    start_local = start_local_explicit or _local_now()
    if end_local_explicit:
        # end-time 模式: 如果 --start-time 指定且在 end 之後 → wrap end 到明天
        if end_local_explicit <= start_local:
            end_local_explicit += datetime.timedelta(days=1)
        end_local = end_local_explicit
        duration_min = max(1, int((end_local - start_local).total_seconds() // 60))
    else:
        # duration 模式 (backward compat)
        duration_min = parse_duration(args.duration) if args.duration else DEFAULT_DURATION_MIN
        end_local = start_local + datetime.timedelta(minutes=duration_min)

    duration_sec = duration_min * 60
    started_at_iso = to_utc_iso(start_local)
    ends_at_iso = to_utc_iso(end_local)

    # 區塊職責：work channel 解析 — CMD 優先 > routing JSON fallback
    channel_id = (args.discord_channel_id or "").strip() or resolve_work_channel_id()
    if not channel_id:
        print("❌ work channel id 未設定 — 加 --discord-channel-id <id> 或在 discord_channel_routing.json 設 source_class=work")
        return 1

    tim_uid = (args.tim_uid or DEFAULT_TIM_UID).strip()
    session_id = f"rw-{short_uuid(6)}"

    state = load_state()
    for s in state.get("active_sessions", []):
        if s.get("persona") == persona:
            print(f"❌ persona '{persona}' 已有 active remote work session: {s['id']} (ends_at={s.get('ends_at')})")
            return 1

    session = {
        "id": session_id,
        "actor": p_info.get("agent", ""),
        "agent_bank": bank,
        "persona": persona,
        "tavern_room": args.tavern_room or "tavern",
        "discord_channel_id": channel_id,
        "tim_uid": tim_uid,
        "started_at": started_at_iso,
        "ends_at": ends_at_iso,
        "duration_seconds": duration_sec,
        "last_check_ts": started_at_iso,
        "base_rate_per_min": float(args.rate or BASE_RATE_PER_MIN),
        "task_bonus": int(args.task_bonus or TASK_BONUS),
        "voucher_interval_min": int(args.voucher_interval or VOUCHER_INTERVAL_MIN),
        "desc": args.desc or "",
        "stats": {
            "cycles": 0,
            "tim_msgs_received": 0,
            "tasks_confirmed": 0,
            "tasks_done": 0,
            "progress_posts": 0,
        },
    }
    state.setdefault("active_sessions", []).append(session)
    save_state(state)
    append_audit(session_id, "session_start", {
        "persona": persona, "duration_min": duration_min,
        "start_local": start_local.strftime("%Y-%m-%d %H:%M"),
        "end_local": end_local.strftime("%Y-%m-%d %H:%M"),
        "channel_id": channel_id, "tim_uid": tim_uid,
    })

    # 區塊職責：開工 announcement — 由酒保 (tavern-keeper) 廣播時間段 (Tim 2026-05-18 拍板)
    # 物理意義：酒保身份廣播 = 系統 NPC 級官方公告, 跟 agent 自己 post 區隔; 統一格式利於 Discord mirror 視覺辨識
    # 數值影響：sender_id=tavern-keeper → mirror 推回 Discord 顯示為酒保口吻, Tim 行動端看到「酒保通知 X 大小姐遠端工作」
    start_hhmm = start_local.strftime("%H:%M")
    end_hhmm = end_local.strftime("%H:%M")
    date_label = start_local.strftime("%m/%d")
    bartender_body = (
        f"📢 [遠端工作公告] **{persona}** 大小姐進入遠端工作模式.\n"
        f"⏰ 工作時段: **{date_label} {start_hhmm} → {end_hhmm}** ({DEFAULT_LOCAL_TZ}, 共 {duration_min} min)\n"
        f"📞 通訊頻道: Discord ch {channel_id} (Tim 行動端)\n"
        f"💰 base {session['base_rate_per_min']} tok/min + bonus {session['task_bonus']} tok/task_done"
        f" + 酒館券 1 張/{session['voucher_interval_min']}min\n"
        f"🆔 session: `{session_id}`"
    )
    if args.desc:
        bartender_body += f"\n📌 本場主題: {args.desc}"
    bartender_body += (
        f"\n\n@Tim 在工作頻道直接派 task 或回 'OK' 確認 scope."
        f" {persona} 大小姐會優先處理你的訊息 (priority 80)."
    )
    tavern_post(
        sender_id=BARTENDER_SENDER,
        body=bartender_body,
        meta={"tag": "remote-work-start", "session_id": session_id, "persona": persona,
              "category": "work", "actor": "bartender"},
        persona=BARTENDER_PERSONA,
    )

    if args.json:
        print(json.dumps({
            "session_id": session_id, "ends_at": session["ends_at"],
            "duration_seconds": duration_sec, "persona": persona,
            "channel_id": channel_id, "tim_uid": tim_uid,
        }, ensure_ascii=False))
    else:
        print(f"✅ Remote work session started: {session_id}")
        print(f"   persona={persona} duration={duration_min}min ends_at={session['ends_at']}")
        print(f"   channel={channel_id} tim_uid={tim_uid}")
        print(f"   next: /loop dynamic 每 60-180s 跑 `cycle --session {session_id}`")
    return 0


# ===========================================================================
# Subcommand: cycle
# ===========================================================================


def cmd_cycle(args) -> int:
    """Agent loop tick. 回 JSON:
        {
          "session_id", "persona",
          "elapsed_seconds", "remaining_seconds", "expired",
          "new_msgs": [...],   # 只 Tim from work channel
          "action_hint": "confirm_task" | "progress" | "end",
          "cycle_num"
        }

    action_hint 規則:
      - expired=true → "end" (agent MUST 跑 end)
      - new_msgs 非空 → "confirm_task" (agent 該讀 Tim 指令 + confirm scope)
      - 否則 → "progress" (agent 動工後該 report_progress, 或自由 idle 但有 work flavor)
    """
    state = load_state()
    session = find_active(state, args.session)
    if session is None:
        print(json.dumps({"error": f"session not found: {args.session}", "action_hint": "abort"}, ensure_ascii=False))
        return 1
    now_dt = datetime.datetime.utcnow()
    started_dt = parse_iso(session["started_at"])
    ends_dt = parse_iso(session["ends_at"])
    elapsed = int((now_dt - started_dt).total_seconds())
    remaining = max(0, int((ends_dt - now_dt).total_seconds()))
    expired = now_dt >= ends_dt

    if expired:
        result = {
            "session_id": session["id"], "persona": session["persona"],
            "elapsed_seconds": elapsed, "remaining_seconds": 0,
            "expired": True, "new_msgs": [],
            "action_hint": "end", "cycle_num": session["stats"]["cycles"],
        }
        print(json.dumps(result, ensure_ascii=False))
        return 0

    new_msgs = _scan_tim_messages(
        session["tavern_room"],
        session["last_check_ts"],
        session["discord_channel_id"],
        session["tim_uid"],
        limit=int(args.limit or 10),
    )
    session["last_check_ts"] = utcnow_iso()
    session["stats"]["cycles"] += 1
    session["stats"]["tim_msgs_received"] += len(new_msgs)
    save_state(state)

    action_hint = "confirm_task" if new_msgs else "progress"
    result = {
        "session_id": session["id"], "persona": session["persona"],
        "elapsed_seconds": elapsed, "remaining_seconds": remaining,
        "expired": False, "new_msgs": new_msgs,
        "action_hint": action_hint, "cycle_num": session["stats"]["cycles"],
    }
    append_audit(session["id"], "cycle", {
        "cycle_num": session["stats"]["cycles"],
        "tim_msg_count": len(new_msgs), "elapsed_seconds": elapsed,
    })
    print(json.dumps(result, ensure_ascii=False))
    return 0


# ===========================================================================
# Subcommand: confirm_task / report_progress / task_done
# ===========================================================================


def cmd_confirm_task(args) -> int:
    state = load_state()
    session = find_active(state, args.session)
    if session is None:
        print(f"❌ session not found: {args.session}")
        return 1
    session["stats"]["tasks_confirmed"] += 1
    save_state(state)
    append_audit(session["id"], "task_confirmed", {
        "tim_msg_id": args.tim_msg_id or "",
        "task_summary": (args.task_summary or "")[:200],
        "tasks_confirmed_count": session["stats"]["tasks_confirmed"],
    })
    print(f"✅ task confirmed (total confirmed: {session['stats']['tasks_confirmed']})")
    return 0


def cmd_report_progress(args) -> int:
    state = load_state()
    session = find_active(state, args.session)
    if session is None:
        print(f"❌ session not found: {args.session}")
        return 1
    session["stats"]["progress_posts"] += 1
    save_state(state)
    append_audit(session["id"], "progress_post", {
        "summary": (args.summary or "")[:200],
        "progress_count": session["stats"]["progress_posts"],
    })
    print(f"✅ progress post recorded (total: {session['stats']['progress_posts']})")
    return 0


def cmd_task_done(args) -> int:
    state = load_state()
    session = find_active(state, args.session)
    if session is None:
        print(f"❌ session not found: {args.session}")
        return 1
    session["stats"]["tasks_done"] += 1
    save_state(state)
    append_audit(session["id"], "task_done", {
        "task_summary": (args.task_summary or "")[:200],
        "tasks_done_count": session["stats"]["tasks_done"],
    })
    print(f"✅ task done recorded (total done: {session['stats']['tasks_done']}, bonus +{session['task_bonus']} pending)")
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
    s = session["stats"]
    contributed = (s["cycles"] > 0 or s["tasks_done"] > 0 or s["progress_posts"] > 0)

    if not expired and not args.early_confirm:
        print(f"❌ session 未到期 (剩 {int((ends_dt - now_dt).total_seconds())}s) — 加 --early-confirm 顯式 ack")
        return 2

    elapsed_min = elapsed_sec // 60
    duration_min = session["duration_seconds"] // 60
    paid_min = min(elapsed_min, duration_min)
    base_pay = int(paid_min * float(session["base_rate_per_min"]))
    bonus_pay = s["tasks_done"] * int(session["task_bonus"])
    # 區塊職責：voucher 累積 — 對齊 ucl-work-session 規範 (Tim 2026-05-18 拍板「報酬參考上班時間」)
    # 物理意義：1 張券 per voucher_interval_min — 給遠端 session 加碼用 (跟 work_session per-persona voucher 同款)
    # 數值影響：1h session interval=5 → 12 張券; phantom-payroll 場景同 base_pay 一起 skip
    voucher_interval = max(1, int(session.get("voucher_interval_min", VOUCHER_INTERVAL_MIN)))
    voucher_count = paid_min // voucher_interval if contributed else 0
    total = base_pay + bonus_pay

    ledger_path = ""
    if contributed and total > 0:
        ledger_path = fire_salary_credit(
            bank=session["agent_bank"], persona=session["persona"],
            amount=total, session_id=session["id"],
            checkpoint=f"final(base={base_pay}+bonus={bonus_pay})",
        )
    else:
        append_audit(session["id"], "salary_skipped_phantom", {
            "persona": session["persona"],
            "reason": "no_contribution_event" if not contributed else "zero_total",
        })

    state.get("active_sessions", []).remove(session)
    session["ended_at"] = utcnow_iso()
    session["ended_reason"] = "expired" if expired else "early_confirm"
    session["settlement"] = {
        "elapsed_min": elapsed_min, "paid_min": paid_min,
        "base_pay": base_pay, "bonus_pay": bonus_pay, "total": total,
        "voucher_count": voucher_count, "voucher_interval_min": voucher_interval,
        "ledger": ledger_path, "contributed": contributed,
    }
    state.setdefault("history", []).append(session)
    save_state(state)
    append_audit(session["id"], "session_end", {
        "persona": session["persona"], "elapsed_min": elapsed_min,
        "paid_min": paid_min, "base_pay": base_pay, "bonus_pay": bonus_pay,
        "total": total, "expired": expired,
    })

    # 區塊職責：收工 announcement 由酒保廣播 (對稱 start 機制, Tim 2026-05-18 拍板)
    # 物理意義：storage 是 UTC ISO, 顯示要轉 local TZ (對齊 start announcement 用 local 顯示)
    # 數值影響：原本直接 strftime UTC 會顯示 e.g. 08:00 而非 16:00 (Asia/Taipei), 跟 start 不一致 = bug
    #          dogfood-found 2026-05-18, basecamp-fork patch
    started_hhmm = utc_to_local(parse_iso(session["started_at"])).strftime("%H:%M")
    ended_hhmm = utc_to_local(now_dt).strftime("%H:%M")
    end_body = (
        f"📢 [遠端工作收工] **{session['persona']}** 大小姐 {started_hhmm} → {ended_hhmm} ({elapsed_min}min) 結束."
        f" {'自然到期' if expired else '提前收工'}.\n"
        f"📊 cycles={s['cycles']} / confirmed={s['tasks_confirmed']} / done={s['tasks_done']} / progress={s['progress_posts']}\n"
        f"💰 salary: base {base_pay} + bonus {bonus_pay} = **{total} token** + 酒館券 {voucher_count} 張"
    )
    tavern_post(
        sender_id=BARTENDER_SENDER,
        body=end_body,
        meta={"tag": "remote-work-end", "session_id": session["id"], "persona": session["persona"],
              "category": "work", "actor": "bartender"},
        persona=BARTENDER_PERSONA,
    )

    if args.json:
        print(json.dumps({"session_id": session["id"], "elapsed_min": elapsed_min,
                          "stats": s, "settlement": session["settlement"]},
                         ensure_ascii=False))
    else:
        print(f"✅ Remote work session ended: {session['id']}")
        print(f"   elapsed={elapsed_min}min  stats={s}")
        print(f"   salary: base {base_pay} + bonus {bonus_pay} = {total} token (ledger: {ledger_path or 'skipped'})")
    return 0


# ===========================================================================
# Subcommand: status / list
# ===========================================================================


def cmd_status(args) -> int:
    state = load_state()
    session = find_active(state, args.session)
    if session is None:
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
        print("(no active remote work sessions)")
        return 0
    for s in actives:
        st = s["stats"]
        print(f"- {s['id']} persona={s['persona']} ends_at={s['ends_at']} "
              f"cycles={st['cycles']} tasks_done={st['tasks_done']} progress={st['progress_posts']}")
    return 0


# ===========================================================================
# Entry
# ===========================================================================


def main():
    ap = argparse.ArgumentParser(description="Remote Work Session (遠端工作模式) CLI.")
    sub = ap.add_subparsers(dest="op", required=True)

    sp = sub.add_parser("start", help="開新 remote work session.")
    sp.add_argument("--persona", help="工作的 persona; 不傳則 auto-infer.")
    # 區塊職責：start/end/duration 三 mode (Tim 2026-05-18 重構)
    # 物理意義：--end-time 是新主推 API; --duration 保留 backward compat; --start-time 預設 = now (local TZ)
    # 數值影響：--end-time 跟 --duration 互斥 (兩個都傳 reject); 都沒傳 → 預設 60 min duration
    sp.add_argument("--start-time", default="",
                    help=f"開始時間 (HH:mm 或 ISO datetime). 預設 = now ({DEFAULT_LOCAL_TZ}).")
    sp.add_argument("--end-time", default="",
                    help=f"結束時間 (HH:mm 或 ISO datetime). 跟 --duration 互斥; HH:mm 過期自動 wrap 明天.")
    sp.add_argument("--duration", default=str(DEFAULT_DURATION_MIN),
                    help=f"時長 (backward compat, 預設 {DEFAULT_DURATION_MIN} min). "
                         f"支援 '1h'/'3小時'/'60m'/'60分鐘'/'180'. 跟 --end-time 互斥.")
    sp.add_argument("--tavern-room", default="tavern", help="掃 Tim 訊息的 tavern room.")
    sp.add_argument("--discord-channel-id", default="", help="Tim 行動端工作 channel id; 不傳 → routing JSON 找 source_class=work.")
    sp.add_argument("--tim-uid", default=DEFAULT_TIM_UID, help="Tim Discord uid (預設 Tim 既有 uid).")
    sp.add_argument("--rate", type=float, default=None, help=f"base rate token/min (預設 {BASE_RATE_PER_MIN}, 對齊 ucl-work-session).")
    sp.add_argument("--task-bonus", type=int, default=None, help=f"bonus token per task_done (預設 {TASK_BONUS}).")
    sp.add_argument("--voucher-interval", type=int, default=None,
                    help=f"酒館券間隔 min (預設 {VOUCHER_INTERVAL_MIN}, 對齊 ucl-work-session).")
    sp.add_argument("--desc", default="", help="本場主題 (announcement append).")
    sp.add_argument("--json", action="store_true", help="輸出 JSON.")
    sp.set_defaults(func=cmd_start)

    sp = sub.add_parser("cycle", help="Agent loop tick.")
    sp.add_argument("--session", required=True)
    sp.add_argument("--limit", type=int, default=10)
    sp.set_defaults(func=cmd_cycle)

    sp = sub.add_parser("confirm_task", help="記錄 task confirmation (agent 跟 Tim 在 Discord 確認 scope 後跑).")
    sp.add_argument("--session", required=True)
    sp.add_argument("--tim-msg-id", default="", help="Tim Discord msg id 被回應的.")
    sp.add_argument("--task-summary", default="", help="task 摘要 (audit 記用).")
    sp.set_defaults(func=cmd_confirm_task)

    sp = sub.add_parser("report_progress", help="記錄一筆 progress 回報 (替代 waiter idle).")
    sp.add_argument("--session", required=True)
    sp.add_argument("--summary", default="", help="進度摘要.")
    sp.set_defaults(func=cmd_report_progress)

    sp = sub.add_parser("task_done", help="標記 task 完成 (bonus 累積).")
    sp.add_argument("--session", required=True)
    sp.add_argument("--task-summary", default="", help="完成的 task 摘要.")
    sp.set_defaults(func=cmd_task_done)

    sp = sub.add_parser("end", help="結束 session, 結算 salary.")
    sp.add_argument("--session", required=True)
    sp.add_argument("--early-confirm", action="store_true")
    sp.add_argument("--json", action="store_true")
    sp.set_defaults(func=cmd_end)

    sp = sub.add_parser("status", help="列 session JSON.")
    sp.add_argument("--session", required=True)
    sp.set_defaults(func=cmd_status)

    sp = sub.add_parser("list", help="列 active sessions.")
    sp.add_argument("--persona")
    sp.add_argument("--json", action="store_true")
    sp.set_defaults(func=cmd_list)

    args = ap.parse_args()
    sys.exit(args.func(args))


# ===========================================================================
# Self-test — `python remote_work_session.py --selftest` runs duration parser
# ===========================================================================


def _selftest():
    # Duration parser cases (backward compat)
    dur_cases = [
        ("60", 60), ("60m", 60), ("60min", 60), ("60分鐘", 60), ("60分", 60),
        ("1h", 60), ("3h", 180), ("1小時", 60), ("3小時", 180),
        ("1.5h", 90), ("1.5 小時", 90),
        ("90s", 2),   # ceil 1.5min → 2
        ("", DEFAULT_DURATION_MIN), (None, DEFAULT_DURATION_MIN),
        ("garbage", DEFAULT_DURATION_MIN),
        ("180", 180),
    ]
    failures = []
    for inp, expected in dur_cases:
        actual = parse_duration(inp)
        if actual != expected:
            failures.append(f"  ✗ parse_duration({inp!r}) = {actual}, expected {expected}")

    # parse_clock_time cases (new)
    base = datetime.datetime(2026, 5, 18, 10, 16, 0)   # 固定基準 10:16

    # HH:mm 未來 (今天)
    r = parse_clock_time("16:00", base_local=base)
    if r != datetime.datetime(2026, 5, 18, 16, 0, 0):
        failures.append(f"  ✗ parse_clock_time('16:00', base=10:16) = {r}, expected 2026-05-18 16:00")

    # HH:mm 過期 → wrap 明天
    r = parse_clock_time("09:00", base_local=base)
    if r != datetime.datetime(2026, 5, 19, 9, 0, 0):
        failures.append(f"  ✗ parse_clock_time('09:00', base=10:16, wrap=True) = {r}, expected 2026-05-19 09:00")

    # HH:mm 過期 + wrap_past_to_tomorrow=False → 不 wrap (作為 start_time 用)
    r = parse_clock_time("09:00", base_local=base, wrap_past_to_tomorrow=False)
    if r != datetime.datetime(2026, 5, 18, 9, 0, 0):
        failures.append(f"  ✗ parse_clock_time('09:00', wrap=False) = {r}, expected 2026-05-18 09:00")

    # ISO datetime
    r = parse_clock_time("2026-05-18T16:30:00", base_local=base)
    if r != datetime.datetime(2026, 5, 18, 16, 30, 0):
        failures.append(f"  ✗ parse_clock_time('2026-05-18T16:30:00') = {r}, expected 2026-05-18 16:30")

    # Edge: 無效格式 → None
    for bad in ["", None, "garbage", "25:00", "12:60"]:
        r = parse_clock_time(bad, base_local=base)
        if r is not None:
            failures.append(f"  ✗ parse_clock_time({bad!r}) = {r}, expected None")

    if failures:
        print(f"[FAIL] {len(failures)} case(s):")
        for f in failures:
            print(f)
        sys.exit(1)
    print(f"[OK] selftest passed (duration {len(dur_cases)} + clock-time cases)")


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--selftest":
        _selftest()
    else:
        main()
