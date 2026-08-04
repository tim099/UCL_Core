# 區塊職責：酒館同步握手（wait-reply）+ 酒保 NPC + T38 per-message 訊息讀取層。
# 物理意義：本模組是 run_cmd.py 分家出來的一塊 —— run_cmd 的職責是「送 cmd 進 Unity 佇列並等它跑完」，
#          而「發完訊息後在 client 端等對方回覆」是另一件事：它不碰佇列、不進 Editor，
#          純粹在檔案系統上輪詢 rooms/<room>/messages/。兩者混在一檔會讓 run_cmd 膨脹到難維護
#          （Tim 2026-07-29 拍板抽離；抽離前 run_cmd.py 1860 行，本塊佔 548 行）。
# 數值影響：本模組不自行解析路徑 —— TAVERN_DIR / GIT_ROOT 由 caller 經 configure() 注入。
#          這是刻意的：資料根有 override 規則（T-PATH-01）住在 run_cmd，複製一份必然漂移。
#          未呼叫 configure() 就用本模組的函式 → 路徑為 None 會直接炸，不會靜默走到錯的目錄。
# 設計取捨：
#   - **讀取層對外公開**（_iter_room_messages / _latest_message_key）：tavern_query.py 與
#     tavern_catchup.py 目前各有一份自己的 per-message 走訪實作，共三份。日後應收斂到本模組，
#     但那是獨立工作，本次不動它們（改動範圍越界會讓這次的回歸驗證失焦）。
#   - **判決碼常數公開**（WAIT_REPLY_*）：caller 要能區分「等了沒人回」與「根本沒等成」。
#     見 docs/Glossary/same-code-mute.md「同碼失聲」。
from __future__ import annotations

import json
import os
import sys
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path

# ===========================================================
# 區塊職責：路徑注入 —— 由 run_cmd.configure() 一次設定，其餘函式全部走這些 module 級名稱
# 物理意義：模組載入時還不知道資料根在哪（要看 CLAUDE_PROJECT_DIR / git-walk / 環境覆寫），
#          所以先留 None，等 caller 注入。函式內部是**呼叫時**才解析這些名稱（late binding），
#          所以 configure() 只要在第一次實際使用前跑過即可。
# 數值影響：全部 Path 物件；HANDSHAKE_* 是旗標檔，BARTENDER_* 是台詞庫與狀態檔。
# ===========================================================
TAVERN_DIR: Path | None = None
GIT_ROOT: Path | None = None
HANDSHAKE_CANCEL_FLAG: Path | None = None      # Editor 按「中止握手」時 touch
HANDSHAKE_ACTIVE_FLAG: Path | None = None      # 握手期間每輪 touch，Editor 靠 mtime 判活躍
HANDSHAKE_START_FILE: Path | None = None       # 內容 = wait_start，Editor 算酒保倒數用
HANDSHAKE_HURRY_FLAG: Path | None = None       # Editor 按「催促酒保」時 touch
BARTENDER_LINES_PATH: Path | None = None       # 台詞庫
BARTENDER_STATE_PATH: Path | None = None       # 連喝計數 / cooldown

HANDSHAKE_HURRY_OFFSET_SEC = 30.0
# 酒保 NPC：wait > BARTENDER_TRIGGER_SEC 時隨機插話 → 緩解長 wait 沉默
BARTENDER_TRIGGER_SEC = float(os.environ.get("UCL_BARTENDER_TRIGGER_SEC", "450"))  # 450s ≈ 7.5 min；慢速模式 wait=480s 內不會被打斷
BARTENDER_REST_HINT_DRINKS = 3   # 達此值不 mute 酒保，但 agent 看到計數該自決收 turn
BARTENDER_COOLDOWN_SEC = 90      # 兩次酒保 post 至少隔 90 秒（防一場 wait 內噴太密）
BARTENDER_CHECK_INTERVAL_SEC = max(2.0, min(BARTENDER_TRIGGER_SEC * 0.5, 5.0))  # 檢查頻率自適應


def configure(tavern_dir: Path, git_root: Path) -> None:
    """注入資料根 —— 必須在使用本模組任何函式前呼叫一次（run_cmd 於 import 後立即呼叫）。"""
    global TAVERN_DIR, GIT_ROOT
    global HANDSHAKE_CANCEL_FLAG, HANDSHAKE_ACTIVE_FLAG, HANDSHAKE_START_FILE, HANDSHAKE_HURRY_FLAG
    global BARTENDER_LINES_PATH, BARTENDER_STATE_PATH
    TAVERN_DIR = tavern_dir
    GIT_ROOT = git_root
    HANDSHAKE_CANCEL_FLAG = tavern_dir / "_handshake_cancel.flag"
    HANDSHAKE_ACTIVE_FLAG = tavern_dir / "_handshake_active.flag"
    HANDSHAKE_START_FILE = tavern_dir / "_handshake_start.txt"
    HANDSHAKE_HURRY_FLAG = tavern_dir / "_handshake_hurry.flag"
    BARTENDER_LINES_PATH = tavern_dir / "bartender_lines.json"
    BARTENDER_STATE_PATH = tavern_dir / "_bartender_state.json"


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
        sys.executable, str(Path(__file__).with_name("run_cmd.py")),
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


# ===========================================================
# 區塊職責：T38 per-message 檔案結構的讀取層（取代已消失的 messages.jsonl）
# 物理意義：T38 起訊息落在 rooms/<room>/messages/<YYYY-MM-DD>/<檔名>.json，一檔一則。
#          歷史上有**兩種檔名世代**，兩者在同一日期夾內都是字典序遞增：
#            - 2026-05-08 ~ 07-27：<HHMMSS>_<MMM>_<UUID6>.json（時間前綴）
#            - 2026-07-28 起：     <seq 補零 8 位>.json
#          所以排序鍵取 (日期夾名, 檔名) 對兩代都成立 —— 不依賴任何 seq 欄位。
# 數值影響：⚠ 訊息 JSON **內部沒有 seq 欄位**（兩代都沒有）。舊版 wait-reply 讀 jsonl 時
#          靠 msg["seq"]，直接沿用會恆為 0 → 每則訊息都判定為「比下界舊」→ 永遠等不到回覆，
#          而且外觀看起來完全正常。那會是第二隻「同碼失聲」，本次修復刻意避開。
#          要顯示 seq 只能從數字檔名推導（推不出就顯示 uuid）。
# ===========================================================

def _is_same_persona(msg: dict, persona: str) -> bool:
    """訊息是不是「某個 persona」發的。

    規格（Tim 2026-08-04）：**wait 一律以 persona 為身分主體**。
    訊息上的 `sender_id` 實際承載的是 agent_id（Myth / Altair / zeta），
    而 agent 層基本上只有 bank / token 相關操作才用得到。
    等人回話等的是「那個人格」不是「那個帳號」—— 一個 agent 底下可有多個 persona。

    血證（2026-08-04 Round S）：`--wait-reply-from gura` 等不到 gura ——
    她的訊息 sender_id='Myth'、sender_persona='gura'，只比 sender_id 完全比不中。
    **對所有「agent 名 ≠ persona 名」的人（Myth/gura、Altair/apex-one、zeta/summit…）
    這個過濾器從來沒有命中過**，而且它靜默等到 timeout，看起來只像「對方沒回」。

    為什麼負向測試抓不到：過濾器「永遠不命中」時，所有「不該命中」的測試也照樣通過。
    只有正向測試（該命中時真的命中）照得出這種壞法。

    邊界：sender_persona 缺席（persona 欄加入前的舊訊息）才退回 sender_id。
    不是每一層都比 —— 比多會讓「A 的 agent 名恰好等於 B 的 persona 名」誤命中。
    """
    if not msg or not persona:
        return False
    want = persona.casefold()
    sp = (msg.get("sender_persona") or "").strip()
    if sp:
        return sp.casefold() == want
    return (msg.get("sender_id") or "").casefold() == want


def _clear_handshake_flags() -> None:
    """清掉握手旗標 — active / start / hurry 三個都清。

    區塊職責：讓「Editor 端看到的握手狀態」與「client 端是否真的在等」不會脫鉤。
    物理意義：Editor 讀 HANDSHAKE_ACTIVE_FLAG 的 mtime 決定「中止握手」按鈕是否亮、
             讀 START 檔算酒保倒數。任何離開等待的路徑都必須清，包含**提早 return 的路徑**。
    數值影響：舊版只在 polling loop 的 finally 清，所以 short-circuit 的早退路徑碰不到旗標 ——
             上一輪 crash 殘留的 active flag 會讓 Editor 一直顯示幽靈握手。
    """
    for flag in (HANDSHAKE_ACTIVE_FLAG, HANDSHAKE_START_FILE, HANDSHAKE_HURRY_FLAG):
        try:
            if flag.is_file():
                flag.unlink()
        except OSError:
            pass


def _room_messages_root(room: str) -> Path:
    """一房的 per-message 檔根目錄（T38 結構）。"""
    return TAVERN_DIR / "rooms" / room / "messages"


def _room_seq_file(room: str) -> Path:
    """一房的 seq 水位檔 — 任何新訊息落地都會推進它的 mtime，polling 拿它當廉價變更信號。"""
    return TAVERN_DIR / "rooms" / room / "_seq.txt"


def _iter_room_messages(room: str, scan_date_dirs: int = 2):
    """走訪一房最近幾個日期夾的訊息，yield ((日期夾名, 檔名), msg_dict)；鍵的字典序 = 時間序。

    只掃最新 scan_date_dirs 個日期夾：wait 窗口最長十分鐘，唯一需要跨夾的情境是跨午夜，兩個就夠。
    單檔讀壞（正在寫入 / JSON 不完整）→ 跳過該檔，不擋整輪 polling。
    """
    root = _room_messages_root(room)
    if not root.is_dir():
        return
    try:
        date_dirs = sorted((d for d in root.iterdir() if d.is_dir()), key=lambda p: p.name)
    except OSError:
        return
    for ddir in date_dirs[-scan_date_dirs:]:
        try:
            files = sorted(ddir.glob("*.json"), key=lambda p: p.name)
        except OSError:
            continue
        for fp in files:
            try:
                msg = json.loads(fp.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError, UnicodeDecodeError):
                continue
            if isinstance(msg, dict):
                yield (ddir.name, fp.name), msg


def _msg_display_id(key: tuple, msg: dict) -> str:
    """給人看的訊息識別：數字檔名（新世代 = seq 補零）→ 'seq N'；否則退 uuid，再退檔名。"""
    stem = key[1].rsplit(".", 1)[0]
    if stem.isdigit():
        return f"seq {int(stem)}"
    uuid = msg.get("uuid")
    return f"uuid {uuid}" if uuid else stem


def _latest_message_key(room: str, sender_id: str | None = None):
    """最新一則訊息的排序鍵；給 sender_id 時只看該**人**（persona 層優先）。找不到回 None。

    ⚠ 這裡的比對必須跟 `_is_same_persona` 用同一個身分層 —— 血證（2026-08-04）：
      wait 的匹配改成 persona 層之後，這裡還留著 `msg["sender_id"] != sender_id`（agent 層）。
      於是 caller 傳 persona 名（summit）時，比不中自己 sender_id='Zeta' 的新訊息，
      往回撈到一則舊的 sender_id='summit' 畸形發文當 baseline，
      **把該筆之後的所有歷史訊息都當成「新回覆」→ 0.0 秒就 got-reply**。
      判決碼 0 看起來完全正常，實際上一秒都沒等、而且匹配的是測試開始前的訊息。
      「同一語意兩處實作，只改了一處」——本 repo 最常復發的那一族。
    """
    latest = None
    for key, msg in _iter_room_messages(room):
        if sender_id and not _is_same_persona(msg, sender_id):
            continue
        if latest is None or key > latest:
            latest = key
    return latest


# wait-reply 判決碼 — 刻意讓「無法判定」與「等了但沒人回」分開（同碼失聲的解藥）
WAIT_REPLY_GOT = 0        # 收到回覆
WAIT_REPLY_TIMEOUT = 1    # 真的等了，窗口內無人回
WAIT_REPLY_CANCELLED = 2  # 使用者從酒館頁中止握手
WAIT_REPLY_UNAVAILABLE = 3  # 結構性無法等待 —— 根本沒等，不是 timeout


def wait_for_tavern_reply(
    room: str,
    my_sender_id: str,
    timeout_sec: float,
    sender_filter: str | None = None,
    poll_interval: float = 0.5,
) -> int:
    """同步等酒館回覆 — client-side polling。

    回傳 0=收到 / 1=timeout（真的等過） / 2=cancelled / 3=無法判定（結構性沒等成）。
    3 與 1 分開是本函式的核心契約：舊版兩者共用 1，導致 caller 分不出
    「等了九分鐘沒人回」與「一秒都沒等」，機制因此靜默失效 81 天（見 glossary「同碼失聲」）。

    流程：
      1. 確認 T38 per-message 目錄存在 —— 不存在 = 無法判定，印**行動指令**後回 3
      2. 取自己最新一則訊息的排序鍵當下界（剛 post 完，那就是我這則）
      3. 進 polling loop，每 poll_interval 秒檢查：
         - _seq.txt mtime 變動（廉價信號）→ 掃最近日期夾，有鍵 > 下界且（無 sender_filter
           或 sender 匹配）→ 印出 → 退出
         - HANDSHAKE_CANCEL_FLAG 存在且 mtime > 我的 wait 開始時間 → 退出（user 從酒館頁中止）
         - 達 timeout_sec → 退出
    """
    # 區塊職責：結構前提檢查 —— 不能等的時候要「大聲說不能等」並給出替代動作
    # 物理意義：狀態描述（「不存在，跳過」）會被習慣成噪音；行動指令讀到就得決定做不做。
    # 數值影響：回 3 而非 1；同時清掉可能殘留的 active flag，避免 Editor 顯示幽靈握手。
    # ⚠ 這條分支**沒有 CLI 觸發路徑**（summit 2026-07-29 實測否證）：拿不存在的房去 post，
    #   Editor 端 Op_Post 的前置驗證會先 RejectLastOp，cmd 標 Failed，wait-reply 整段不會被執行。
    #   它真正防的是**資料根設定漂移** —— T-PATH-01 的 override 讓 Editor 寫入的位置與 client
    #   讀取的位置不同時，房是真的存在、訊息也真的寫了，只是這支程式看不到。那種場合必須大聲叫，
    #   否則就退化成 81 天那隻靜默空轉。
    #   「不可測的防禦分支跟沒有防禦是同一件事」→ 故本模組附 `--selftest` 直呼函式層驗這條，
    #   不在生產路徑挖測試專用開關。
    messages_root = _room_messages_root(room)
    if not messages_root.is_dir():
        _clear_handshake_flags()
        print(
            f"  ⚠ wait-reply 無法運作 — 找不到 {messages_root}\n"
            f"     ⛔ 本次**完全沒有等待**（判決碼 3 = 無法判定，不是 timeout）\n"
            f"     → 要等回覆請改用 server 端 wait：\n"
            f"        run_cmd.py run Tavern --arg op=wait --arg room={room} --arg since_seq=<N> --arg timeout=480"
        )
        return WAIT_REPLY_UNAVAILABLE

    # 下界 = 自己最新一則（剛 post 完就是我這則）；找不到自己就退回全房最新一則，
    # 兩者都沒有（空房）→ None，代表任何訊息都算新
    baseline_key = _latest_message_key(room, sender_id=my_sender_id)
    if baseline_key is None:
        baseline_key = _latest_message_key(room)

    filter_desc = f" from={sender_filter}" if sender_filter else ""
    since_desc = f"{baseline_key[0]}/{baseline_key[1]}" if baseline_key else "(空房，全部視為新)"
    print(f"  ⏳ Wait-reply: room={room} since={since_desc}{filter_desc} "
          f"timeout={timeout_sec:.0f}s（按酒館頁「中止握手」可提前結束）")

    wait_start = time.time()
    deadline = wait_start + timeout_sec
    next_heartbeat = wait_start + 60.0  # 每 60s 印一行進度，避免長 wait 看似 hang
    next_bartender_check = wait_start + BARTENDER_TRIGGER_SEC  # 至少等 trigger 秒數才檢查
    # 變更信號初值 None → 第一圈必掃一次（post 與進 loop 之間可能已經有人回）
    last_change_signal = None
    seq_file = _room_seq_file(room)

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
                print(f"  ⏱  Wait-reply timeout ({timeout_sec:.0f}s) — 真的等過了，對方未在窗口內回應")
                return WAIT_REPLY_TIMEOUT

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
                        return WAIT_REPLY_CANCELLED
                except OSError:
                    pass

            # 區塊職責：變更信號 gate — 只有 _seq.txt 的 mtime 動過才去掃訊息檔
            # 物理意義：任何新訊息落地都會推進該檔；沒動就代表沒有新訊息，不必掃目錄。
            # 數值影響：省掉每 0.5s 一輪的目錄列舉 + JSON 解析（一天的日期夾可有數百檔）。
            #          _seq.txt 不存在（舊房 / 尚未建立）→ changed 恆為 True，退化成每輪都掃，
            #          正確性不受影響、只是較貴 —— 寧可貴也不要靜默不掃。
            changed = True
            if seq_file.is_file():
                try:
                    signal = seq_file.stat().st_mtime
                    changed = (signal != last_change_signal)
                    last_change_signal = signal
                except OSError:
                    changed = True
            if not changed:
                time.sleep(poll_interval)
                continue

            # Poll messages（T38 per-message 檔；排序鍵 = (日期夾, 檔名)，不依賴 seq 欄位）
            try:
                for key, msg in _iter_room_messages(room):
                    if baseline_key is not None and key <= baseline_key:
                        continue
                    if _is_same_persona(msg, my_sender_id):
                        # 自己後續發言不算回覆（避免 self 觸發）
                        continue
                    msg_id = _msg_display_id(key, msg)
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
                    # 數值影響：weak reply 也走 return 0（判決碼不變），但 print 標明酒保 + 半待機提示，
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
                            f"     [{msg_id}] {msg.get('sender_name', '酒保')}: {body_preview}\n"
                            f"     ↳ Agent 可選半待機協議 (A/B/C/D) 回應，或重發 wait — 酒保打斷無上限，"
                            f"但達 {BARTENDER_REST_HINT_DRINKS} 杯時表示確認沒人在，agent 該自己收 turn 休息"
                        )
                        # 酒保 weak reply 不該 reset 連喝計數 — counter 累積成 agent 自決休息的訊號
                        return WAIT_REPLY_GOT
                    if sender_filter and not _is_same_persona(msg, sender_filter):
                        continue
                    # 命中真實 reply — 印出 + 清酒保連喝計數
                    body_preview = msg.get("body", "")
                    if len(body_preview) > 600:
                        body_preview = body_preview[:600] + " ...(truncated)"
                    print(
                        f"  ✉  Reply received in {now - wait_start:.1f}s:\n"
                        f"     [{msg_id}] {msg.get('sender_name', msg.get('sender_id', '?'))}: {body_preview}"
                    )
                    if my_sender_id:
                        reset_bartender_count(room, my_sender_id)
                    return WAIT_REPLY_GOT
            except OSError:
                pass

            time.sleep(poll_interval)
    finally:
        # 不論 return 哪個 path（命中 / timeout / cancel / 例外）都清握手旗標
        _clear_handshake_flags()




# 判決碼 → 名稱，給 caller 印機器可讀的一行結論（verdict=<name> code=<n>）
_WAIT_REPLY_VERDICT_NAME = {
    WAIT_REPLY_GOT: "got-reply",
    WAIT_REPLY_TIMEOUT: "timeout",
    WAIT_REPLY_CANCELLED: "cancelled",
    WAIT_REPLY_UNAVAILABLE: "unavailable",
}


# ===========================================================
# 區塊職責：自測入口 —— `python tavern_handshake.py --selftest`
# 物理意義：本模組有一條 CLI 走不到的防禦分支（unavailable / code 3）。summit 2026-07-29 判
#          「不可測的防禦分支跟沒有防禦是同一件事」—— 所以這裡提供函式層的可重複驗證，
#          而不是在生產路徑挖一個測試專用開關（那會讓生產程式帶著只為測試存在的入口）。
# 數值影響：全部唯讀（不 post、不寫訊息、不動 config）；唯一副作用是可能清掉殘留的握手旗標。
#          退出碼 0 = 全通過，1 = 有項目失敗（可直接掛 CI / pre-commit）。
# ===========================================================

def _selftest(room: str = "tavern") -> int:
    import importlib

    # 路徑注入沿用 run_cmd 的解析（唯一真相源），避免自測跟生產走不同的資料根。
    # ⚠ 必須**自己**呼叫 configure：以 `python tavern_handshake.py --selftest` 執行時，
    #   本檔是 `__main__`，而 run_cmd 內的 `import tavern_handshake` 會載入**另一份副本**
    #   並只設定那一份 —— 靠它幫我們注入會讓 __main__ 這份永遠是 None（Python 雙模組陷阱）。
    #   注意這裡沒有靜默降級：忘了這步就是 TypeError 當場炸，而不是走到錯的目錄。
    rc = importlib.import_module("run_cmd")
    configure(tavern_dir=rc.TAVERN_DIR, git_root=rc.GIT_ROOT)
    failures = []

    def check(name: str, ok: bool, detail: str = "") -> None:
        print(f"  {'✓' if ok else '✗'} {name}{(' — ' + detail) if detail else ''}")
        if not ok:
            failures.append(name)

    print(f"[selftest] tavern_dir = {TAVERN_DIR}")

    # ① 防禦分支：房不存在 → 必須回 unavailable(3)，不可回 timeout(1)
    code = wait_for_tavern_reply(room="__selftest_no_such_room__", my_sender_id="__selftest__", timeout_sec=99)
    check("unavailable 分支回 3（不是 1）", code == WAIT_REPLY_UNAVAILABLE, f"got {code}")
    # 它必須「立刻」回 —— 回 3 卻真的睡了 99 秒等於另一種謊
    t0 = time.time()
    wait_for_tavern_reply(room="__selftest_no_such_room__", my_sender_id="__selftest__", timeout_sec=99)
    check("unavailable 立刻返回（不等待）", (time.time() - t0) < 2.0, f"{time.time() - t0:.2f}s")

    # ② 判決碼四者互異 —— 這正是本次修復的核心契約（同碼失聲的解藥）
    codes = [WAIT_REPLY_GOT, WAIT_REPLY_TIMEOUT, WAIT_REPLY_CANCELLED, WAIT_REPLY_UNAVAILABLE]
    check("四個判決碼互異", len(set(codes)) == 4, str(codes))

    # ③ 讀取層：真實房間必須掃到訊息，且排序鍵單調遞增
    keys = [k for k, _m in _iter_room_messages(room)]
    check(f"讀取層掃到訊息（room={room}）", len(keys) > 0, f"{len(keys)} 則")
    check("排序鍵單調遞增", keys == sorted(keys))

    # ④ seq 顯示 id：數字檔名要能推出 seq；**且訊息內部不該有 seq 欄位**（有的話代表 schema 變了，
    #    本模組的「不依賴 seq 欄位」前提要重新評估）
    numeric_ok = True
    has_inline_seq = False
    for k, m in _iter_room_messages(room):
        stem = k[1].rsplit(".", 1)[0]
        if stem.isdigit() and _msg_display_id(k, m) != f"seq {int(stem)}":
            numeric_ok = False
        if "seq" in m:
            has_inline_seq = True
    check("數字檔名 → 'seq N' 推導正確", numeric_ok)
    check("訊息 JSON 內確實沒有 seq 欄位（本模組前提）", not has_inline_seq)

    # ⑤ 最新鍵：全房最新 >= 任一 sender 的最新
    latest_all = _latest_message_key(room)
    check("取得全房最新鍵", latest_all is not None, str(latest_all))

    print(f"[selftest] {'ALL PASS' if not failures else 'FAILED: ' + ', '.join(failures)}")
    return 0 if not failures else 1


if __name__ == "__main__":
    if "--selftest" in sys.argv:
        raise SystemExit(_selftest())
    print("本模組是 run_cmd.py 的握手/酒保實作，不直接當 CLI 用。\n"
          "  自測：python tavern_handshake.py --selftest")
    raise SystemExit(2)
