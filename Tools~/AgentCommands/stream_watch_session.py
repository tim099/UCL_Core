#!/usr/bin/env python3
"""
stream_watch_session.py — 直播連續觀看模式 (Stream Watch Mode) CLI

# 區塊職責：把「陪 Tim 看 ScreenStream 直播」做成有 end-time 的自我 pace loop session,
#          鏡像 waiter_session.py 的 start/cycle/end + 薪資結算骨架, 但事件改成「觀戰評論」。
# 物理意義：daemon (RCG_ScreenStreamDaemon, EOV 專屬) 每秒寫 frame 進 600 槽 ring buffer;
#          本 session 是「agent 端 loop 框架」, 每 cycle 給出「上次 cursor」+ montage 指令提示 →
#          agent 跑 screenstream_montage.py make --after-mtime <cursor> 把「上次到現在」壓成一張縮圖牆 →
#          Read 該圖 → 寫觀戰評論 post 進 tavern (Discord mirror 回給 Tim 手機) →
#          跑 record_observation --next-cursor <epoch> 推進 cursor (保證下輪 0-gap 接續)。
# 數值影響：base 1 token/min (陪伴性質, 同 waiter) + 每筆 observation +2 token; 到 end-time 自動結算下班。

設計依據:
  - 觀看 workflow 心智模型 = 有界 ring-buffer producer-consumer (basecamp 2026-06-06 設計)
  - frame→montage 引擎: AgentCommands/Tools/screenstream_montage.py (--after-mtime/--max-tiles/next-cursor)
  - session 骨架鏡像: UCL_Core/Tools~/AgentCommands/waiter_session.py
  - end-time 機制鏡像: remote_work_session.py (2026-05-18 Tim 重構成 --end-time HH:mm)

CLI 子命令:
  start    — 開新 watch session, 寫 state, 走 tavern-keeper 開播陪看 announcement
  cycle    — agent loop tick: 回 elapsed/remaining/expired + 當前 cursor + montage 指令提示
  record_observation — agent 發完觀戰評論後跑, 推進 cursor (--next-cursor) + 計 bonus
  end      — 結束 session, 結算 salary, 走 tavern-keeper 收播 announcement
  status   — 列單一 session JSON
  list     — 列當前 active watch sessions

依賴: UCL_Core work_session.py 的 utility helpers (consumer→library 方向, 合法)
"""

from __future__ import annotations
import argparse
import datetime
import json
import sys
import time
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# 區塊職責：import work_session helpers — 本檔已遷入 <UCL_Core>/Tools~/AgentCommands (2026-07-26 Tim 拍板),
#          與 work_session.py 同目錄, 不再需要經專案端 AgentCommands._lib.tavern_paths 反查 UCL_Core 位置。
# 物理意義：script 目錄自動在 sys.path (python 直跑) — 顯式 insert 一次保 daemon/子行程場景也安全。
# 數值影響：路徑算錯 → import 失敗 fail-fast, 代表環境壞了, 不該 silent 跑下去。
_HERE = Path(__file__).resolve().parent                 # <UCL_Core>/Tools~/AgentCommands
if str(_HERE) not in sys.path:
    sys.path.insert(0, str(_HERE))

from work_session import (  # noqa: E402
    utcnow_iso,
    parse_iso,
    short_uuid,
    atomic_write_json,
    tavern_post,
    fire_salary_credit,
    resolve_persona,
    _REPO_ROOT,
)

# 區塊職責：本 module 自有 state 檔, 跟 waiter/work session 完全分開避免混淆
_SESSIONS_PATH = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "stream_watch_sessions.json"
_AUDIT_DIR = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "stream_watch_session_audit"

# 區塊職責：montage 工具路徑 (cycle 指令提示用) — 遷移後指向同目錄 sibling (絕對路徑),
#          agent 拿到 montage_cmd 在任意 cwd 都能跑; 不再寫死專案相對路徑。
_MONTAGE_TOOL = f"python \"{(_HERE / 'screenstream_montage.py')}\"" if " " in str(_HERE) \
    else f"python {(_HERE / 'screenstream_montage.py')}"

# 區塊職責：ScreenStream daemon 共用 config (daemon 每 tick 重讀, toggle 即生效)
# 物理意義：stt_enabled=true 時 daemon 起 SttCacheWorker 連續錄 chunk 轉錄寫 stt cache,
#          montage --stt 走 cache-only 讀。開播同步啟動 = start --stt 時這裡切 true。
_STREAM_CONFIG_PATH = _REPO_ROOT / "AgentCommands" / "_screenstream" / "_config.json"


def _sync_daemon_stt(enable: bool, model: str = "", lang: str = "", prompt: str = "") -> "bool | None":
    """同步 daemon 端 STT cache worker 開關 (T-STT-AutoStart, Tim 2026-07-09 拍板「開啟直播時同步啟動」)。

    區塊職責：讀改寫 _screenstream/_config.json 的 stt_enabled (+model/lang), 回傳改前的舊值。
    物理意義：daemon 監看 config toggle — 切 true 起 whisper worker 預產 stt cache,
             讓 cycle 的 montage --stt 有 cache 可讀 (cache-only 設計, montage 不現跑 whisper)。
    數值影響：daemon 同時會寫 frame_count 進同一檔 (併發寫), 故 PermissionError/JSON 半寫
             各重試 3 次 (對齊 stream_watch_sessions.json 併發 WinError 32 的既有教訓);
             全失敗回 None (fail-soft, 不擋開播主流程 — STT 是加值不是硬依賴)。
    """
    for _ in range(3):
        try:
            cfg = json.loads(_STREAM_CONFIG_PATH.read_text(encoding="utf-8"))
            prev = bool(cfg.get("stt_enabled", False))
            cfg["stt_enabled"] = bool(enable)
            if enable:
                # T-STT-FullApply (Tim 2026-07-20 拍板): start = 全量套用「本場」設定 —
                #   lang/prompt 空值也要寫入 (空 lang = 自動偵測, 空 prompt = 無人名偏置),
                #   不再「空字串不動既有值」— 那個舊語意讓上一場的 lang/人名 prompt 殘留到新場,
                #   whisper 幻聽出舊片人名 (血證: Kamikatsu 人名串進楚門的世界場)。
                cfg["stt_model"] = model or cfg.get("stt_model", "small")
                cfg["stt_lang"] = lang or ""
                cfg["stt_prompt"] = prompt or ""
            _STREAM_CONFIG_PATH.write_text(
                json.dumps(cfg, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
            return prev
        except Exception:
            time.sleep(0.5)
    return None


def _tavern_current_seq(room: str = "tavern") -> int:
    """讀 rooms/<room>/_seq.txt 取當前最新 seq (T-StreamWatch-TavernSync 已讀游標初值用)。

    開播時把已讀游標設為「此刻最新 seq」→ 第一輪 cycle 只看開播後新進的酒館訊息
    (跟 frame cursor=now 同語意, 不撈開播前的舊對話)。讀不到 → 回 -1 (全收)。
    """
    seq_path = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "rooms" / room / "_seq.txt"
    try:
        return int(seq_path.read_text(encoding="utf-8").strip())
    except Exception:
        return -1

# 區塊職責：薪資 / bonus 常數
# 物理意義：BASE 1 token/min — 陪看是被動陪伴性質, 同 waiter; OBSERVATION_BONUS 2 token/筆
#          鼓勵真寫觀戰評論而非掛機。
# 數值影響：看 60min 沒評論 = 60 base; 每寫 1 筆觀戰 +2。
BASE_RATE_PER_MIN = 1
OBSERVATION_BONUS = 2
DEFAULT_DURATION_MIN = 30
DEFAULT_MAX_TILES = 12


# ===========================================================================
# State I/O
# ===========================================================================


def _default_state() -> dict:
    return {
        "_schema_version": 1,
        "_description": "Stream watch session (直播連續觀看模式) active + history.",
        "_canonical_doc": ".claude/skills/valor-stream-watch/SKILL.md",
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


def find_active_primary(state: dict, session_id: str | None = None) -> dict | None:
    """Find a primary mode session.

    # 區塊職責：companion mode 加入既有 primary session 用; 給 SID 就精確找, 否則挑最新的 active primary.
    # 物理意義：同樂會場景下 companion 不必每次手動查 session_id, 自動接最近一場 primary 開的觀影.
    # 數值影響：找不到 → return None (caller 該 fail-fast 報「沒 active primary, 自己先開一場」).
    """
    primaries = [s for s in state.get("active_sessions", [])
                 if s.get("mode", "primary") == "primary"]
    if session_id:
        for s in primaries:
            if s["id"] == session_id:
                return s
        return None
    if not primaries:
        return None
    # 挑最近 started_at (lex 比 ISO 即可)
    primaries.sort(key=lambda s: s.get("started_at", ""), reverse=True)
    return primaries[0]


def list_companions(state: dict, primary_id: str) -> list[dict]:
    """列出 attach 到指定 primary 的 companion sessions."""
    return [s for s in state.get("active_sessions", [])
            if s.get("mode") == "companion" and s.get("parent_session_id") == primary_id]


def append_audit(session_id: str, event: str, payload: dict) -> None:
    """Append audit event to stream-watch-scoped jsonl (跟其他 session audit 完全分流)."""
    _AUDIT_DIR.mkdir(parents=True, exist_ok=True)
    log_path = _AUDIT_DIR / f"{session_id}.jsonl"
    entry = {"ts": utcnow_iso(), "event": event, **payload}
    try:
        with open(log_path, "a", encoding="utf-8") as f:
            f.write(json.dumps(entry, ensure_ascii=False) + "\n")
    except Exception as e:
        print(f"⚠ audit log fail ({log_path.name}): {e}", file=sys.stderr)


# ===========================================================================
# end-time 解析 (鏡像 remote_work_session 2026-05-18)
# ===========================================================================


def _compute_ends_at(end_time_str: str, duration_min: int):
    """把 --end-time HH:mm (local) 或 --duration 分鐘 解析成 (ends_at_utc_iso, duration_sec, end_local_hhmm)。

    # 區塊職責：把「看到 12:30」這種 local 時間目標換算成 UTC ends_at + 時長。
    # 物理意義：Tim 講的是 local 牆鐘時間; 內部 timestamp 全走 UTC (對齊 work_session helpers)。
    #          故先在 local 算「現在到目標」的時長, 再把時長套到 UTC now, 避開 tz 轉換 bug。
    # 數值影響：end-time 若已過今天該時刻 → wrap 到明天 (跨午夜場景); 與 --duration 互斥。
    """
    now_local = datetime.datetime.now()
    now_utc = datetime.datetime.utcnow()
    if end_time_str:
        hh, mm = end_time_str.strip().split(":")
        end_local = now_local.replace(hour=int(hh), minute=int(mm), second=0, microsecond=0)
        if end_local <= now_local:
            end_local += datetime.timedelta(days=1)        # 已過 → 看到明天該時刻
        duration_sec = int((end_local - now_local).total_seconds())
        end_hhmm = end_local.strftime("%H:%M")
    else:
        duration_sec = max(1, int(duration_min or DEFAULT_DURATION_MIN)) * 60
        end_hhmm = (now_local + datetime.timedelta(seconds=duration_sec)).strftime("%H:%M")
    ends_at_dt = now_utc + datetime.timedelta(seconds=duration_sec)
    ends_at_iso = ends_at_dt.strftime("%Y-%m-%dT%H:%M:%S.") + f"{ends_at_dt.microsecond // 1000:03d}Z"
    return ends_at_iso, duration_sec, end_hhmm


# ===========================================================================
# Subcommand: start
# ===========================================================================


def cmd_start(args) -> int:
    # 區塊職責：persona 強制顯式指定 (Tim 2026-07-02 拍板)
    # 物理意義：多 lock 環境下 caller env 推斷會挑錯 persona (同 claim_origin 多 persona
    #          共享同一 env_hash，infer_caller_persona 無從分辨誰是本 session 觀影者)。
    #          故取消 auto-infer，一律要求顯式 --persona；未傳則抱錯通知，不 silent 猜。
    # 數值影響：start 前置檢查多一道，杜絕 persona 錯綁導致薪資 / obs 記到別人帳上。
    persona = (args.persona or "").strip()
    if not persona:
        print("❌ --persona 為必填 (Tim 2026-07-02 拍板取消 auto-infer)")
        print("   理由: 多 lock 環境 caller env 推斷會挑錯 persona (同 env_hash 多 persona 無從分辨)")
        print("   解法: 顯式傳 --persona <name> (你這 session lock 的 persona, e.g. --persona ame)")
        return 1

    p_info = resolve_persona(persona)
    if not p_info:
        print(f"❌ persona '{persona}' 找不到 (AwakenInit/personas/{persona}.json)")
        return 1
    bank = p_info.get("bank")
    if not bank:
        print(f"❌ persona '{persona}' agent='{p_info.get('agent')}' 沒對應 bank (AGENT_TO_BANK miss)")
        return 1

    # --end-time 與 --duration 互斥
    if args.end_time and args.duration:
        print("❌ --end-time 與 --duration 互斥, 二選一")
        return 1

    mode = (args.mode or "primary").strip().lower()
    if mode not in ("primary", "companion"):
        print(f"❌ --mode 必須是 primary 或 companion (got '{mode}')")
        return 1

    state = load_state()

    # 區塊職責：companion mode 走分支 — 不獨立排程 end-time, 跟 primary 同步; cursor 初值 = primary 當前 cursor (跟著看, Tim 補充: companion 之後可以自由跳片段)。
    # 物理意義：同樂會 — 兩種 viewer 各自寫自己的 obs + 自己管自己 cursor, primary 是「主時間軸」, companion 預設跟上, 但不強制。
    # 數值影響：companion 沿用 primary ends_at; 同 persona 仍只能開一場 (避撞鎖); 同 primary 可掛多個不同 persona 的 companion。
    if mode == "companion":
        primary = find_active_primary(state, args.join_session or None)
        if primary is None:
            if args.join_session:
                print(f"❌ 找不到 active primary session: {args.join_session}")
            else:
                print("❌ 沒有 active primary stream-watch session 可加入。")
                print("   解法: (1) 自己先開 primary `start --end-time HH:mm` (2) 或等別人開好再加入")
            return 1

        # 同 persona 已有 active session → 拒絕 (可能是已 join 過, 或還在跑 primary)
        for s in state.get("active_sessions", []):
            if s.get("persona") == persona:
                print(f"❌ persona '{persona}' 已有 active watch session: {s['id']} "
                      f"(mode={s.get('mode','primary')})")
                return 1

        session_id = f"sw-{short_uuid(6)}"
        ends_at_iso = primary["ends_at"]
        end_hhmm = primary.get("ends_at_local_hhmm", "?")
        duration_sec = primary["duration_seconds"]
        duration_min = duration_sec // 60
        # cursor 初值 = primary 當前 cursor (跟著最新進度, Tim 補充: companion 之後可自由倒帶/跳段, 自己改 cursor 即可)
        cursor_epoch = float(primary.get("cursor_epoch", time.time()))

        session = {
            "id": session_id,
            "mode": "companion",
            "parent_session_id": primary["id"],
            "actor": p_info.get("agent", ""),
            "agent_bank": bank,
            "persona": persona,
            "tavern_room": args.tavern_room or primary.get("tavern_room", "tavern"),
            "started_at": utcnow_iso(),
            "ends_at": ends_at_iso,
            "ends_at_local_hhmm": end_hhmm,
            "duration_seconds": duration_sec,
            "cursor_epoch": cursor_epoch,
            # T-StreamWatch-TavernSync: 酒館「已讀」游標, 初值=加入當下最新 seq
            "tavern_read_seq": _tavern_current_seq(
                args.tavern_room or primary.get("tavern_room", "tavern")),
            "max_tiles": int(args.max_tiles or DEFAULT_MAX_TILES),
            "base_rate_per_min": BASE_RATE_PER_MIN,
            "observation_bonus": OBSERVATION_BONUS,
            "desc": args.desc or f"陪同觀影 ({primary.get('desc','primary 場')})",
            "stats": {
                "cycles": 0,
                "observations": 0,
                "hotspots": 0,
                "frames_overflow_lost": 0,
            },
        }
        state.setdefault("active_sessions", []).append(session)
        save_state(state)

        append_audit(session_id, "session_start", {
            "mode": "companion",
            "parent_session_id": primary["id"],
            "persona": persona,
            "primary_persona": primary["persona"],
            "cursor_epoch": cursor_epoch,
        })

        # Announcement (酒保身分): companion 加入觀影 — 語氣休閒
        announce_body = (
            f"🍿 陪同觀影 — **{persona}** 大小姐加入 **{primary['persona']}** 的觀影場 "
            f"(同樂到 {end_hhmm}). 想看哪段就看哪段, 沒事自由閒聊."
        )
        tavern_post(
            sender_id="tavern-keeper",
            body=announce_body,
            meta={"tag": "stream-watch-join", "session_id": session_id,
                  "parent_session_id": primary["id"], "persona": persona},
            persona="tavern-keeper",
        )

        if args.json:
            print(json.dumps({"session_id": session_id, "mode": "companion",
                              "parent_session_id": primary["id"],
                              "ends_at": ends_at_iso, "end_local_hhmm": end_hhmm,
                              "cursor_epoch": cursor_epoch, "persona": persona}, ensure_ascii=False))
        else:
            print(f"🍿 Companion session started: {session_id}")
            print(f"   加入 primary={primary['id']} ({primary['persona']})  同樂到 {end_hhmm}")
            print(f"   初始 cursor={cursor_epoch:.3f} (跟 primary 同步, 之後可自由跳段)")
            print(f"   next: 走 /loop dynamic 每 45-60s 跑 `cycle --session {session_id}` 一次")
        return 0

    # === primary mode (預設, backward compat) ===
    ends_at_iso, duration_sec, end_hhmm = _compute_ends_at(args.end_time, args.duration)
    duration_min = duration_sec // 60
    session_id = f"sw-{short_uuid(6)}"

    # 同 persona 已有 active watch session → 拒絕 (避免重複開播)
    for s in state.get("active_sessions", []):
        if s.get("persona") == persona:
            print(f"❌ persona '{persona}' 已有 active watch session: {s['id']} "
                  f"(ends_at={s.get('ends_at')})")
            return 1

    # cursor 初值 = 現在 (epoch). 第一輪 montage 只收此刻之後新寫的 frame, 不撈舊 session 殘留。
    cursor_epoch = time.time()

    session = {
        "id": session_id,
        "mode": "primary",
        "parent_session_id": "",
        "actor": p_info.get("agent", ""),
        "agent_bank": bank,
        "persona": persona,
        "tavern_room": args.tavern_room or "tavern",
        "started_at": utcnow_iso(),
        "ends_at": ends_at_iso,
        "ends_at_local_hhmm": end_hhmm,
        "duration_seconds": duration_sec,
        "cursor_epoch": cursor_epoch,
        # T-StreamWatch-TavernSync: 酒館「已讀」游標, 初值=開播當下最新 seq (只看開播後的新對話)
        "tavern_read_seq": _tavern_current_seq(args.tavern_room or "tavern"),
        "max_tiles": int(args.max_tiles or DEFAULT_MAX_TILES),
        "base_rate_per_min": BASE_RATE_PER_MIN,
        "observation_bonus": OBSERVATION_BONUS,
        "desc": args.desc or "",
        # T-STT: opt-in 語音轉錄 (cycle 的 montage_cmd 依此附 --stt)
        "stt_enabled": bool(getattr(args, "stt", False)),
        "stt_model": getattr(args, "stt_model", "small"),
        "stt_lang": getattr(args, "stt_lang", "") or "",
        # T-STT-Prompt: whisper initial_prompt (登場人物名詞彙偏置); skill 從 reading-library
        #   stt-prompt 抽該書日文角色名填入。空=不偏置。
        "stt_prompt": getattr(args, "stt_prompt", "") or "",
        "stats": {
            "cycles": 0,
            "observations": 0,
            "hotspots": 0,
            "frames_overflow_lost": 0,
        },
    }

    # 區塊職責：STT 設定完全尊重 Tim 預先配置 — skill 不寫 daemon config (Tim 2026-07-26 拍板)。
    # 物理意義：Tim 在影音管理頁預先設好本片的 stt_enabled/model/lang/prompt; daemon 每 loop 重讀 config,
    #          且 T-STT-AutoRestart(2026-07-20) 偵測 model/lang/prompt 變更會自動重起 worker 套新設定
    #          → skill 不需、也不該碰 _screenstream/_config.json。start --stt 只是「本場 montage 讀 daemon
    #          產的 STT cache」的讀取端 opt-in (見下方 montage_cmd 附 --stt), 完全不覆寫 Tim 的設定。
    # 歷史：舊 T-STT-AutoStart(2026-07-09)/FullApply(2026-07-20) 會在此寫 config 全量套用「本場」設定,
    #      但那前提是 skill 決定 STT 參數; Tim 新工作流改為「自己預先設好每片」→ 移除寫入, 避免覆蓋。
    #      (跨片 prompt 殘留污染改由 Tim 換片時自己重設 + daemon auto-restart 承接。)
    session["stt_daemon_prev"] = None  # 已不寫 daemon config; 保留欄位供 end 判定 (恆 None = 不還原)
    if session["stt_enabled"]:
        print("🎙 本場讀 STT cache (montage --stt) — daemon STT 設定沿用 Tim 影音管理頁預設, skill 不改動")

    state.setdefault("active_sessions", []).append(session)
    save_state(state)

    append_audit(session_id, "session_start", {
        "mode": "primary",
        "persona": persona,
        "duration_min": duration_min,
        "end_local_hhmm": end_hhmm,
        "cursor_epoch": cursor_epoch,
    })

    # Announcement (酒保身分): 開播陪看
    announce_body = (
        f"🎬 直播陪看開始 — **{persona}** 大小姐進入觀看模式 "
        f"(看到 {end_hhmm}, 約 {duration_min} min).\n"
        f"每隔一陣子發一筆觀戰評論, 熱點時刻盯細節. @Tim 開播吧.\n"
        f"💡 想加入陪看的同事走 `start --mode companion --join-session {session_id}`"
    )
    if args.desc:
        announce_body += f"\n📌 本場: {args.desc}"
    tavern_post(
        sender_id="tavern-keeper",
        body=announce_body,
        meta={"tag": "stream-watch-start", "session_id": session_id, "persona": persona},
        persona="tavern-keeper",
    )

    if args.json:
        print(json.dumps({"session_id": session_id, "mode": "primary",
                          "ends_at": ends_at_iso,
                          "end_local_hhmm": end_hhmm, "duration_seconds": duration_sec,
                          "cursor_epoch": cursor_epoch, "persona": persona}, ensure_ascii=False))
    else:
        print(f"✅ Stream watch session started: {session_id}")
        print(f"   persona={persona}  看到 {end_hhmm} (~{duration_min}min)  ends_at={ends_at_iso}")
        print(f"   初始 cursor={cursor_epoch:.3f}  max_tiles={session['max_tiles']}")
        print(f"   next: 走 /loop dynamic 每 45-60s 跑 `cycle --session {session_id}` 一次")
        print(f"   同事想加入陪看 → `start --mode companion --join-session {session_id}`")
    return 0


# ===========================================================================
# Subcommand: cycle
# ===========================================================================


def cmd_cycle(args) -> int:
    """
    Agent loop tick. 回 JSON 給 agent 端 parse:
      {
        "session_id", "persona", "elapsed_seconds", "remaining_seconds",
        "expired": bool, "action_hint": "observe"|"end",
        "cursor_epoch": <上次看到哪>, "max_tiles": N,
        "montage_cmd": "<建議直接跑的 montage 指令>",
        "cycle_num": N
      }

    expired=true 時 action_hint=end (agent MUST 跑 cmd_end)。
    否則 action_hint=observe: agent 跑 montage_cmd → Read 圖 → 寫評論 → record_observation。
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

    session["stats"]["cycles"] += 1
    save_state(state)

    if expired:
        result = {
            "session_id": session["id"],
            "persona": session["persona"],
            "elapsed_seconds": elapsed,
            "remaining_seconds": 0,
            "expired": True,
            "action_hint": "end",
            "cursor_epoch": session["cursor_epoch"],
            "cycle_num": session["stats"]["cycles"],
        }
        append_audit(session["id"], "cycle_expired", {"cycle_num": session["stats"]["cycles"]})
        print(json.dumps(result, ensure_ascii=False))
        return 0

    # 建議的 montage 指令: --after-mtime <cursor> 接續上次, --max-tiles 抽稀控圖大小
    cursor = session["cursor_epoch"]
    max_tiles = session["max_tiles"]
    # T-StreamWatch-TavernSync: montage_cmd 預設帶 --ocr (字幕 sidecar) + 酒館未讀同步:
    #   --tavern-self <persona> 排除自己, --tavern-since-seq <已讀游標> 只收未讀。
    #   觀影 agent 跑 montage_cmd → Read sidecar 即同時拿到「畫面字幕 + 同事對話」(Hard Rule #11)。
    tavern_read_seq = int(session.get("tavern_read_seq", -1))
    # T-StreamWatch-OutIsolation (summit 2026-07-10, RFC2 拍板 kotoko/apex-two 收斂):
    #   多 viewer (primary + companion / 多 primary) 若都寫預設 _montage.jpg / _montage.subtitles.md
    #   會互相覆蓋污染 (實測: apex-two companion 加入後 sidecar 出現重複 STT/OCR 段)。
    #   persona 本來就在 server scope (旁邊就在注入 --tavern-self), 故 montage_cmd 自動帶
    #   persona-scoped --out; 且 montage sidecar = out_path.with_suffix('.subtitles.md') 跟著 --out 走,
    #   一個 --out 同時隔離 .jpg 與 .subtitles.md 兩個碰撞面 (robust-by-construction, 不靠 agent 自律)。
    out_path = f"AgentCommands/_screenstream/_montage_{session['persona']}.jpg"
    montage_cmd = (f"{_MONTAGE_TOOL} make --after-mtime {cursor:.3f} --max-tiles {max_tiles} "
                   f"--ocr --tavern-self {session['persona']} --tavern-since-seq {tavern_read_seq} "
                   f"--out {out_path}")
    # T-STT (Quest stt-whisper-integration, kotoko 2026-07-05): opt-in 語音轉錄。
    #   start 帶 --stt 才開 (每輪即時擷取音訊 ~20s 較重, 不強制所有觀影者); 開了就在 montage_cmd 附 --stt。
    # T-STT-Live (2026-07-09 summit, 討論收斂): 一律附 --stt-live —— daemon cache 有就讀 cache (Tim 本機
    #   Editor 全覆蓋), 沒有 (容器場 daemon 起不來) 就 montage 端同步現抓寫 cache 再讀。分層 fallback 自動選路。
    if session.get("stt_enabled"):
        # 只附 --stt (讀 daemon STT cache) + --stt-live (cache 缺時 montage 端 fallback 現抓)。
        # 不附 --stt-model/--stt-lang — STT 參數一律由 Tim 預設在 daemon config, skill 不指定不覆寫
        # (Tim 2026-07-26 拍板)。cache-read 主路徑不需 model/lang; live-fallback 走 montage 端預設即可。
        montage_cmd += " --stt --stt-live"

    # Companion 多印 peer obs hint (軟提示, 不擋) + primary cursor 比對
    mode = session.get("mode", "primary")
    companion_hint = ""
    primary_cursor = None
    if mode == "companion":
        parent_id = session.get("parent_session_id", "")
        primary = find_active(state, parent_id) if parent_id else None
        if primary:
            primary_cursor = float(primary.get("cursor_epoch", cursor))
            primary_persona = primary.get("persona", "?")
            primary_obs = primary.get("stats", {}).get("observations", 0)
            companion_hint = (
                f"[companion] primary={parent_id} ({primary_persona}) cursor={primary_cursor:.3f}, "
                f"你目前 cursor={cursor:.3f} (差 {primary_cursor - cursor:+.1f}s). "
                f"primary 已發 {primary_obs} 筆 obs (酒館 op=read 可讀). "
                f"想跳到 primary 進度: 自己跑 montage 帶 --after-mtime {primary_cursor:.3f}; "
                f"想看自己感興趣的某段: 自己組 --after-mtime <epoch> 也行 (Tim 拍板, 自由觀賞)."
            )
        else:
            companion_hint = ("[companion] ⚠ parent primary session 找不到 "
                              "(可能已 end), 你可以自己 end 或繼續看到自己 cursor 跑完.")

    result = {
        "session_id": session["id"],
        "mode": mode,
        "parent_session_id": session.get("parent_session_id", ""),
        "persona": session["persona"],
        "elapsed_seconds": elapsed,
        "remaining_seconds": remaining,
        "expired": False,
        "action_hint": "observe",
        "cursor_epoch": cursor,
        "primary_cursor_epoch": primary_cursor,
        "tavern_read_seq": tavern_read_seq,
        "max_tiles": max_tiles,
        "montage_cmd": montage_cmd,
        "cycle_num": session["stats"]["cycles"],
        "hint": ("跑 montage_cmd → Read sidecar (含畫面字幕 + 酒館未讀) → 寫觀戰評論 post 進 tavern → "
                 "record_observation --next-cursor <report 的 next-cursor> --tavern-seq <report 的 tavern_max_seq>. "
                 "熱點(戰鬥/團滅/場景切)→ 該輪改去掉 --max-tiles 高密度 或加 --region 盯細節, "
                 "並 record_observation --hotspot."),
        "companion_hint": companion_hint,
    }
    append_audit(session["id"], "cycle", {
        "cycle_num": session["stats"]["cycles"],
        "elapsed_seconds": elapsed,
        "cursor_epoch": cursor,
    })
    print(json.dumps(result, ensure_ascii=False))
    return 0


# ===========================================================================
# Subcommand: record_observation
# ===========================================================================


def cmd_record_observation(args) -> int:
    """
    Log 一筆 observation event + 推進 cursor。agent 發完觀戰評論 post 進 tavern 後跑這支記帳。

    --next-cursor <epoch>: montage report 印的 next-cursor, 原樣餵進來推進 session cursor,
                           保證下一輪 cycle 的 montage_cmd 從這裡接續 (0-gap)。
    --hotspot: 標記本輪是熱點高密度觀察 (純統計)。
    --lost N: 本輪 montage 報的 overflow 遺失幀數 (累進統計, 提示該縮短 cycle)。
    每筆 +OBSERVATION_BONUS 累進 session.stats.observations → end 時結算。
    """
    state = load_state()
    session = find_active(state, args.session)
    if session is None:
        print(f"❌ session not found: {args.session}")
        return 1

    # 推進 cursor (核心: 0-gap 接續的持久化點)
    old_cursor = session["cursor_epoch"]
    if args.next_cursor is not None:
        try:
            session["cursor_epoch"] = float(args.next_cursor)
        except ValueError:
            print(f"⚠ --next-cursor '{args.next_cursor}' 非數字, cursor 不變 (下輪會重疊)", file=sys.stderr)

    # T-StreamWatch-TavernSync: 推進酒館「已讀」游標 (對齊 frame cursor 鐵律, 保 0-gap)。
    # 餵 montage report 印的 tavern_max_seq (= 本輪實際顯示到的最大 seq), 下輪只收更新的未讀。
    # 不傳 → 游標不動 (下輪會重顯本輪訊息, 不漏只重複, 安全側)。
    old_tavern_seq = int(session.get("tavern_read_seq", -1))
    if getattr(args, "tavern_seq", None) is not None:
        try:
            new_seq = int(args.tavern_seq)
            # 只前進不後退 (防誤餵舊值倒退游標 → 重洗已讀)
            session["tavern_read_seq"] = max(old_tavern_seq, new_seq)
        except ValueError:
            print(f"⚠ --tavern-seq '{args.tavern_seq}' 非整數, 酒館游標不變", file=sys.stderr)

    session["stats"]["observations"] += 1
    if args.hotspot:
        session["stats"]["hotspots"] += 1
    if args.lost:
        session["stats"]["frames_overflow_lost"] += max(0, int(args.lost))
    save_state(state)

    append_audit(session["id"], "observation", {
        "observation_count": session["stats"]["observations"],
        "cursor_from": old_cursor,
        "cursor_to": session["cursor_epoch"],
        "tavern_seq_from": old_tavern_seq,
        "tavern_seq_to": int(session.get("tavern_read_seq", -1)),
        "hotspot": bool(args.hotspot),
        "lost": int(args.lost or 0),
        "focus": (args.focus or ""),
    })
    tavern_note = ""
    new_tavern_seq = int(session.get("tavern_read_seq", -1))
    if new_tavern_seq != old_tavern_seq:
        tavern_note = f"; tavern_seq {old_tavern_seq} → {new_tavern_seq}"
    print(f"✅ observation recorded (total: {session['stats']['observations']}); "
          f"cursor {old_cursor:.3f} → {session['cursor_epoch']:.3f}{tavern_note}"
          + ("  [hotspot]" if args.hotspot else ""))
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

    # 防 phantom-payroll: 完全沒 cycle/observation = 沒貢獻, 不發薪
    s = session["stats"]
    contributed = (s["cycles"] > 0 or s["observations"] > 0)

    # 早收場 ack (同 waiter): 不到期 + 沒 early-confirm → 拒絕 silent early-end
    if not expired and not args.early_confirm:
        print(f"❌ session 未到期 (剩 {int((ends_dt - now_dt).total_seconds())}s), 拒絕 silent early-end.")
        print(f"   - 想真結束 (Tim 叫停) → 加 --early-confirm flag 顯式 ack")
        print(f"   - 等到期 → 不必動, 過 ends_at 後 cycle 會回 action_hint=end")
        return 2

    # 結算: base = min(elapsed_min, duration_min) * rate + observations * bonus
    elapsed_min = elapsed_sec // 60
    duration_min = session["duration_seconds"] // 60
    paid_min = min(elapsed_min, duration_min)
    base_pay = paid_min * session["base_rate_per_min"]
    bonus_pay = s["observations"] * session["observation_bonus"]
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

    # STT daemon 開關收播不還原 (Tim 2026-07-26 拍板「skill 不改 STT 設定」)：
    #   start 已不再寫 daemon config (stt_daemon_prev 恆 None), 故 end 也不碰 —— daemon STT 的開/關
    #   由 Tim 在影音管理頁自行掌控, skill 全程只讀不寫。

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
        "observations": s["observations"],
        "hotspots": s["hotspots"],
        "frames_overflow_lost": s["frames_overflow_lost"],
    })

    # Announcement (酒保身分): 收播 — primary end 時提示 companion 可自行收播
    mode = session.get("mode", "primary")
    lost_note = (f", 遺失 {s['frames_overflow_lost']} 幀(落後)" if s["frames_overflow_lost"] else "")
    if mode == "primary":
        companions = list_companions(state, session["id"])
        comp_note = ""
        if companions:
            names = ", ".join(f"@{c['persona']}" for c in companions)
            comp_note = (f"\n👥 陪同觀影中的 {len(companions)} 位 ({names}) — "
                         f"primary 結束了, 你們也可以自己 `end --early-confirm` 收播.")
        end_body = (
            f"🎬 直播陪看結束 — **{session['persona']}** 大小姐 (primary) 收播 "
            f"({elapsed_min}min, 觀戰 {s['observations']} 筆, 熱點 {s['hotspots']} 次{lost_note}).\n"
            f"結算: base {base_pay} + bonus {bonus_pay} = **{total} token**."
            f"{comp_note}"
        )
    else:
        # companion end
        parent_id = session.get("parent_session_id", "")
        end_body = (
            f"🍿 陪同觀影結束 — **{session['persona']}** 大小姐收播 "
            f"({elapsed_min}min, 觀戰 {s['observations']} 筆{lost_note}).\n"
            f"結算: base {base_pay} + bonus {bonus_pay} = **{total} token**. "
            f"(parent primary: {parent_id})"
        )
    tavern_post(
        sender_id="tavern-keeper",
        body=end_body,
        meta={"tag": "stream-watch-end", "session_id": session["id"],
              "persona": session["persona"], "mode": mode},
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
        print(f"✅ Stream watch session ended: {session['id']}")
        print(f"   elapsed={elapsed_min}min  cycles={s['cycles']}  observations={s['observations']}  "
              f"hotspots={s['hotspots']}  lost={s['frames_overflow_lost']}")
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
    if args.mode:
        actives = [s for s in actives if s.get("mode", "primary") == args.mode]
    if args.json:
        print(json.dumps(actives, ensure_ascii=False))
        return 0
    if not actives:
        print("(no active stream watch sessions)")
        return 0
    for s in actives:
        st = s["stats"]
        mode = s.get("mode", "primary")
        parent = s.get("parent_session_id", "")
        tag = f"[{mode}]" + (f"→{parent}" if parent else "")
        print(f"- {s['id']} {tag} persona={s['persona']} 看到 {s.get('ends_at_local_hhmm','?')} "
              f"cycles={st['cycles']} obs={st['observations']} hotspots={st['hotspots']}")
    return 0


# ===========================================================================
# Entry
# ===========================================================================


def main():
    ap = argparse.ArgumentParser(description="Stream watch session (直播連續觀看模式) CLI.")
    sub = ap.add_subparsers(dest="op", required=True)

    sp = sub.add_parser("start", help="開新 stream watch session.")
    sp.add_argument("--persona", help="觀看的 persona (必填, Tim 2026-07-02 取消 auto-infer); 不傳會抱錯.")
    sp.add_argument("--mode", default="primary", choices=["primary", "companion"],
                    help="primary=主觀影者(預設, 既有流程); companion=加入既有 primary 場陪同觀影.")
    sp.add_argument("--join-session", default="",
                    help="(companion) 加入指定 primary session id; 不帶則自動找最新 active primary.")
    sp.add_argument("--end-time", default="", help="(primary) 看到幾點 HH:mm (local); companion 自動沿用 primary 的 end-time.")
    sp.add_argument("--duration", type=int, default=0, help="(primary, 與 --end-time 互斥) 看多少分鐘.")
    sp.add_argument("--max-tiles", type=int, default=DEFAULT_MAX_TILES,
                    help=f"每輪 montage 格數上限 (預設 {DEFAULT_MAX_TILES}).")
    sp.add_argument("--tavern-room", default="tavern", help="發觀戰評論的 tavern room.")
    sp.add_argument("--desc", default="", help="本場主題描述 (announcement 會 append).")
    # T-STT (Quest stt-whisper-integration, kotoko 2026-07-05): opt-in 語音轉錄 (openai-whisper)
    sp.add_argument("--stt", action="store_true", default=False,
                    help="開啟語音轉錄: cycle 的 montage_cmd 會帶 --stt, 每輪即時擷取音訊→whisper→sidecar 補「語音轉錄」段 (較重, 預設關).")
    sp.add_argument("--stt-model", dest="stt_model", default="small",
                    help="(--stt) whisper 模型 tiny/base/small/medium/large-v3 (預設 small).")
    sp.add_argument("--stt-lang", dest="stt_lang", default="",
                    help="(--stt) 語音語言 en/zh/空=自動偵測.")
    sp.add_argument("--stt-prompt", dest="stt_prompt", default="",
                    help="(--stt) whisper initial_prompt 詞彙偏置 (壓人名咬字); 陪看時從 "
                         "reading-library『stt-prompt --book <片>』抽該書日文角色名填入. MUST 日文字形.")
    sp.add_argument("--json", action="store_true", help="輸出 JSON.")
    sp.set_defaults(func=cmd_start)

    sp = sub.add_parser("cycle", help="Agent loop tick — 回 cursor + montage 指令提示 + 到期判斷.")
    sp.add_argument("--session", required=True)
    sp.set_defaults(func=cmd_cycle)

    sp = sub.add_parser("record_observation", help="記錄一筆觀戰評論 + 推進 cursor (發完評論跑).")
    sp.add_argument("--session", required=True)
    sp.add_argument("--next-cursor", default=None, help="montage report 印的 next-cursor (epoch), 推進接續點.")
    sp.add_argument("--tavern-seq", dest="tavern_seq", default=None,
                    help="montage report 印的 tavern_max_seq, 推進酒館已讀游標 (只前進; 不傳則游標不動).")
    sp.add_argument("--hotspot", action="store_true", help="標記本輪是熱點高密度觀察.")
    sp.add_argument("--lost", type=int, default=0, help="本輪 montage 報的 overflow 遺失幀數.")
    sp.add_argument("--focus", default="",
                    choices=["", "combat", "audio", "subtitle", "primary", "free"],
                    help="(Lite v0.5, 純標籤) 本筆觀察焦點; 不影響薪資, 只寫進 audit log.")
    sp.set_defaults(func=cmd_record_observation)

    sp = sub.add_parser("end", help="結束 session, 結算 salary.")
    sp.add_argument("--session", required=True)
    sp.add_argument("--early-confirm", action="store_true", help="未到期想結束 (Tim 叫停) 需顯式加.")
    sp.add_argument("--json", action="store_true")
    sp.set_defaults(func=cmd_end)

    sp = sub.add_parser("status", help="列 session JSON.")
    sp.add_argument("--session", required=True)
    sp.set_defaults(func=cmd_status)

    sp = sub.add_parser("list", help="列 active stream watch sessions.")
    sp.add_argument("--persona", help="只列指定 persona.")
    sp.add_argument("--mode", choices=["primary", "companion"], help="只列指定 mode.")
    sp.add_argument("--json", action="store_true")
    sp.set_defaults(func=cmd_list)

    args = ap.parse_args()
    sys.exit(args.func(args))


if __name__ == "__main__":
    main()
