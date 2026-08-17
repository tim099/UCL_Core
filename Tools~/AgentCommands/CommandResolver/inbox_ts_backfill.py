"""T-INBOX-TS — 把 inbox 條目的權威時間戳（`_at <ISO UTC>_`）補回來。

## 為什麼需要這支

酒保通知池的已讀水位是 **per-persona 單一 int（seq）**，而 seq 是 **per-room 編號**：
tavern 已經跑到 15000+，側房（trpg-yachiyo 等）最大才 109。於是側房的 @ 永遠算不出
「新的」—— 那是**永久靜默，不是延遲**。2026-08-15 實測：六個 persona 合計 163 筆
永久看不見的 @（apex-one 39 / summit 37 / kaguya 33 / basecamp 31 / Sirius 15 / meadow 8）。

修法是把水位換成**時間戳**（ts 是全域單調時鐘，跨房可比；per-room seq 之間不可比較）。
而換水位的前置是「每一筆條目都要有 ts」——本支就是補那一步。

## 為什麼是「補」而不是「新增格式」

`_at <ISO UTC>_` 這個格式**本來就存在**：2026-07-29 的版面精簡（把時間併進標題列）
之前的條目全都有它。⇒ 不發明新格式（例如行尾 HTML 註解），因為：
  - `tavern_catchup._entry_snippet` 的跳過清單**早就有 `_at `**；
  - `wake_brief` 只讀標題行、`inbox_ack` 只數 `## [seq=` prefix。
  ⇒ 沿用 `_at` → **三個 parser 一支都不用改**。行尾註解則會被 `s[3:]` 整行吃進標題，
     變成每天早安 brief 與每次叮都看得到的尾巴垃圾（summit 2026-08-15 讀 parser 實證）。

## 事實源與「不猜」原則

ts **一律取自** `rooms/<room>/messages/<yyyy-MM-dd>/<8位seq>.json` 的 `ts` 欄（UTC 毫秒）。
**不採信** md 內既有的任何時間投影：
  - 標題列的 `(2026-08-15 11:33:07 +08)` 是本地時區、秒精度、可再生 → 是投影不是來源；
  - 既有的 `_at` 行雖然是 UTC，但它同樣是寫入當下的副本 → 拿來**對拍**，不拿來當輸入。

## seq → 檔案：為什麼用檔名而不是清單位置

inbox 條目的 `[seq=N]` 是 `AppendInbox` 當下寫下的**歷史寫入序號**，寫下即不變。
而「排序後清單的 index+1」是**現在**這份清單的位置 —— 只要有任何訊息檔曾被刪除，
位置整體前移而檔名不會，於是位置法會給出**一份外觀完全正常的錯誤對應**。
⇒ 本支走檔名，並且**開跑前先驗不變式**（檔名格式／連續／檔數 == max-min+1）——
   任一條不成立就**拒絕執行**並印違例清單，不是印警告然後繼續。
   （該不變式是檔名 migration 自己斷言並對帳過的：「檔名 == seq」。）

## 用法

    python inbox_ts_backfill.py              # dry-run（完全唯讀，預設）
    python inbox_ts_backfill.py --apply      # 實際寫入
    python inbox_ts_backfill.py --room trpg-yachiyo   # 限定單一房間
    python inbox_ts_backfill.py --report-only         # 只產生既有異常紀錄，不動 inbox

⚠ 順序約束：通知水位 `seq_ts` 必須在**本支跑完之後**、從回填出來的真 ts 取 max。
   先設水位再 backfill 的話，水位是舊 seq 語意算出來的，而回填後的 ts 可能落在它之後
   ⇒ 那幾筆會冒出來（殘缺版洗版，只漏幾筆、更難查）。
"""
from __future__ import annotations
import argparse
import json
import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))

# 條目標題行 —— 與 inbox_ack.count_mentions / tavern_catchup.read_inbox_entries 同一約定，
# 三處都錨定 `## [seq=` 這個 prefix，不可改（改了那三支會同時瞎掉）。
ENTRY_RE = re.compile(r"^##\s*\[seq=(\d+)\]")
# 既有權威時間戳行（2026-07-29 版面精簡前的格式）
AT_RE = re.compile(r"^_at\s+(\d{4}-\d{2}-\d{2}T[\d:.]+Z)_\s*$")
# 訊息檔名 == seq（檔名 migration 對帳過的不變式）
MSG_NAME_RE = re.compile(r"^(\d{8})\.json$")


# 區塊職責：repo 根 / 資料根解析 —— 與 inbox_ack.py 同慣例，不重寫第二套。
# 物理意義：UCL_Core 是 submodule，各專案掛載深度不同（Assets/Plugins/UCL_Core /
#          Assets/UCL/UCL_Core / CardGame/…），任何寫死的 ".." 層數都會跨專案漂移。
# 數值影響：.git 只認資料夾（submodule 的 .git 是檔案 → 自動跳過，命中主專案根）。
def _find_git_root(start: str):
    p = os.path.abspath(start)
    while True:
        if os.path.isdir(os.path.join(p, ".git")):
            return p
        parent = os.path.dirname(p)
        if parent == p:
            return None
        p = parent


def _repo_root() -> str:
    env = os.environ.get("CLAUDE_PROJECT_DIR")
    if env and os.path.isdir(env):
        return os.path.abspath(env)
    return _find_git_root(_HERE) or _find_git_root(os.getcwd()) or os.getcwd()



# ⚠ pointer 檔讀取已收斂到 _lib/ucl_paths.py（Tim 2026-08-17 拍板）。
#   原本有 10 份平行實作，每份自己 read_text().strip()。十份都對，
#   但十份就是十個會各自漂移的真相源；而漂移的症狀是「這支讀 A 目錄、那支讀 B 目錄」，
#   兩邊都不報錯。⇒ 之後改 pointer 檔格式只需改一處。
_UCL_PATHS_CACHE = None


def _ucl_paths_mod():
    global _UCL_PATHS_CACHE
    if _UCL_PATHS_CACHE is None:
        import importlib.util as _ilu
        from pathlib import Path as _P
        _spec = _ilu.spec_from_file_location(
            "_ucl_paths_shared", _P(__file__).resolve().parent.parent / "_lib" / "ucl_paths.py")
        _m = _ilu.module_from_spec(_spec)
        _spec.loader.exec_module(_m)
        _UCL_PATHS_CACHE = _m
    return _UCL_PATHS_CACHE


def _data_root(root: str) -> str:    return str(_ucl_paths_mod().data_root())    # 委派唯一實作


ROOMS_DIR = os.path.join(_data_root(_repo_root()), "ChatTavern", "rooms")


# 區塊職責：驗證「檔名 == seq」不變式，並建 seq → 訊息檔路徑對應。
# 物理意義：這是整支工具唯一的 seq 解讀方式。不變式不成立時**不得產生對應** ——
#          因為錯誤的對應長得跟正確的一模一樣（每筆都在，只是全部對到別人）。
# 數值影響：回 (index, violations)。violations 非空時呼叫端必須中止該房，不可降級續跑。
def build_seq_index(room_dir: str):
    msg_root = os.path.join(room_dir, "messages")
    index, nums, violations = {}, [], []
    if not os.path.isdir(msg_root):
        return index, ["無 messages/ 目錄（無事實源，無法回填）"]
    for date_dir in sorted(os.listdir(msg_root)):
        dpath = os.path.join(msg_root, date_dir)
        if not os.path.isdir(dpath):
            continue
        for fn in sorted(os.listdir(dpath)):
            if not fn.endswith(".json"):
                continue
            m = MSG_NAME_RE.match(fn)
            if not m:
                violations.append(f"檔名不符 NNNNNNNN.json：{date_dir}/{fn}")
                continue
            seq = int(m.group(1))
            if seq in index:
                violations.append(f"seq {seq} 重複：{index[seq]} 與 {date_dir}/{fn}")
                continue
            index[seq] = os.path.join(dpath, fn)
            nums.append(seq)
    if nums:
        nums.sort()
        expect = list(range(nums[0], nums[0] + len(nums)))
        if nums != expect:
            missing = sorted(set(expect) - set(nums))
            violations.append(
                f"seq 不連續：檔數 {len(nums)}、範圍 {nums[0]}..{nums[-1]}、缺 {missing[:10]}"
            )
    return index, violations


# 區塊職責：判定「既有 _at」與「事實源 ts」是不是同一個瞬間。
# 物理意義：這兩個值**不是同一支 code 在同一刻寫的** —— 事實源是訊息落地時的 UTC 毫秒，
#          `_at` 是 AppendInbox 當下另外格式化的**秒精度**副本（且是四捨五入不是截斷）。
#          ⇒ 「逐字相同」從來就不是正確的判準，2026-08-15 首跑實測 226 筆只有 6 筆逐字相同。
# 數值影響：本函式**只**用來偵測 seq→檔案對應是否錯位。錯位的特徵是**任意偏移**
#          （實測離群值：7.4 天 / 38 分 / 72 秒），而寫入延遲的特徵是**次秒級**。
#          門檻取 2 秒：實測 217 筆良性全部 ≤2s、4 筆離群全部 ≥6s，兩群之間沒有樣本。
#          ⚠ 這不是把不合格改成合格 —— 離群的那幾筆照樣被判為不符、照樣不回填。
BENIGN_SKEW_SECONDS = 2.0


def _parse_iso(s: str):
    try:
        from datetime import datetime
        return datetime.fromisoformat(s.replace("Z", "+00:00"))
    except (ValueError, ImportError):
        return None


def same_instant(existing: str, truth: str):
    """回 (是否同一瞬間, 分類標籤)。"""
    if existing == truth:
        return True, "逐字相同"
    a, b = _parse_iso(existing), _parse_iso(truth)
    if a is None or b is None:
        return False, "無法解析"
    delta = abs((a - b).total_seconds())
    if existing[:19] == truth[:19]:
        return True, "精度差"
    if delta <= BENIGN_SKEW_SECONDS:
        return True, "寫入延遲/進位"
    return False, f"偏移 {delta:.0f}s"


def read_msg_ts(path: str):
    try:
        with open(path, "r", encoding="utf-8") as f:
            return (json.load(f) or {}).get("ts") or None
    except (OSError, ValueError):
        return None


# 區塊職責：掃一份 inbox md，回每筆條目的 (行號, seq, 既有_at 或 None)。
# 物理意義：`_at` 只認緊接標題行之後那兩行內的第一筆 —— 再遠就可能是引用內容裡的字串。
def parse_entries(lines):
    out = []
    for i, ln in enumerate(lines):
        m = ENTRY_RE.match(ln.strip())
        if not m:
            continue
        existing = None
        for j in range(i + 1, min(i + 3, len(lines))):
            am = AT_RE.match(lines[j].strip())
            if am:
                existing = (j, am.group(1))
                break
        out.append((i, int(m.group(1)), existing))
    return out


# 區塊職責：把既有異常寫成**紀錄**（只增不減、帶首見時間），不是每次覆寫的儀表。
# 物理意義：上一版是 open(w) 整檔覆寫、無時間戳、不累積 —— 那是儀表穿了紀錄的衣服：
#          修掉 20 筆再跑，那 20 筆的存在史就消失，而檔案本身說不出自己是什麼時候的。
#          （summit 2026-08-15 讀 code 抓到；它就長在我為她的要求做的那個修法裡。）
# 數值影響：以 room/box + seq 當 key merge。既有 key 保留原 first_seen；本次不再出現的
#          **不刪除**，改標 resolved 並記 resolved_at —— 缺陷的消失本身也是要留的事實。
# ⚠ 沒有「負責人」欄：本支無從得知誰該負責，所以不提供那個欄位，
#   也把原註解裡「保有位置與負責人」那句宣稱改掉 —— 欄位不存在就不准寫在說明裡。
def write_anomaly_record(path: str, anomalies, now_iso: str):
    prev = {}
    if os.path.isfile(path):
        try:
            with open(path, "r", encoding="utf-8") as f:
                for line in f:
                    if not line.startswith("| ") or line.startswith("| room/box"):
                        continue
                    c = [x.strip() for x in line.strip().strip("|").split("|")]
                    if len(c) >= 5:
                        prev[(c[0], c[1])] = {"desc": c[2], "first_seen": c[3], "status": c[4]}
        except OSError:
            prev = {}

    cur = {}
    for a in anomalies:
        room_box, seq, desc = a.split("\t")
        cur[(room_box, seq)] = desc

    rows = []
    for key, desc in sorted(cur.items()):
        first = prev.get(key, {}).get("first_seen") or now_iso
        rows.append((key[0], key[1], desc, first, "open"))
    for key, old in sorted(prev.items()):
        if key in cur:
            continue
        status = old["status"] if old["status"].startswith("resolved") else f"resolved @ {now_iso}"
        rows.append((key[0], key[1], old["desc"], old["first_seen"], status))

    with open(path, "w", encoding="utf-8") as f:
        f.write("# inbox 既有異常紀錄（`inbox_ts_backfill` 產出）\n\n")
        f.write(f"> 最後更新：`{now_iso}`　本次 open {len(cur)} 筆／表內共 {len(rows)} 筆。\n")
        f.write("> **只增不減**：本次未再出現的不刪除，改標 `resolved`。`first_seen` 是首次被本支看到的時間。\n")
        f.write("> ⚠ 這些是**既有異常，不是 backfill 造成的**；本支對它們只讀不寫。\n\n")
        f.write("| room/box | seq | 說明 | first_seen | status |\n|---|---|---|---|---|\n")
        for r in rows:
            f.write(f"| {r[0]} | {r[1]} | {r[2]} | {r[3]} | {r[4]} |\n")


def main() -> int:
    ap = argparse.ArgumentParser(description="回填 inbox 條目的權威 UTC 時間戳（_at 行）")
    ap.add_argument("--apply", action="store_true", help="實際寫入（預設 dry-run，完全唯讀）")
    ap.add_argument("--room", default="", help="只處理指定房間（預設全部）")
    ap.add_argument("--rooms-dir", default=ROOMS_DIR, help="rooms 根目錄（測試隔離用）")
    # 解耦：既有異常的落地不該取決於「backfill 有沒有 apply」——
    # 那 21 筆的存在與否若綁在一個不相干動作上，backfill 被擋下時它們就從沒落過地。
    ap.add_argument("--report-only", action="store_true",
                    help="只產生既有異常紀錄，不回填（會寫 _inbox_anomalies.md，不動 inbox）")
    ap.add_argument("--now", default="", help="時間戳（給紀錄用；預設取當下 UTC）")
    args = ap.parse_args()
    if not args.now:
        from datetime import datetime, timezone
        args.now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    if not os.path.isdir(args.rooms_dir):
        print(f"❌ 找不到 rooms 目錄：{args.rooms_dir}")
        return 2

    # ⚠ 模式字串必須誠實：--report-only 會寫異常紀錄，那就不能叫「完全唯讀」。
    #   宣稱與行為不符時，壞的一邊是宣稱沒被執行、於是沒有人發現。
    mode = ("APPLY（會改 inbox）" if args.apply
            else "REPORT-ONLY（只寫異常紀錄，不動 inbox）" if args.report_only
            else "DRY-RUN（完全唯讀）")
    print(f"📋 inbox `_at` 回填 — {mode}")
    print(f"   rooms: {args.rooms_dir}\n")

    # ⚠ apply 是**重新掃描**不是重放 dry-run 的計畫 —— inbox 是活的靶：
    #   AppendInbox 會 append，且超過 InboxCapMax(50) 會自動 trim 舊條目進 _archive。
    #   ⇒ 「dry-run 印的數字 == apply 會做的事」**不成立**（實測同一天三次掃描得到 908/909/910）。
    #   重新掃描讓每次執行都對當下的事實負責；把 dry-run 輸出當待執行合約才是危險的那條路。
    tot_entries = tot_have = tot_fill = tot_unresolved = 0
    oracle_ok = oracle_bad = 0
    oracle_kinds = {}
    sentinel = 0
    anomalies = []
    bad_rooms, plans, unresolved_list = [], [], []

    rooms = [args.room] if args.room else sorted(os.listdir(args.rooms_dir))
    for room in rooms:
        room_dir = os.path.join(args.rooms_dir, room)
        inbox_dir = os.path.join(room_dir, "inbox")
        if not os.path.isdir(inbox_dir):
            continue
        boxes = [f for f in sorted(os.listdir(inbox_dir))
                 if f.endswith(".md") and not f[:-3].endswith("_archive")]
        if not boxes:
            continue

        index, violations = build_seq_index(room_dir)
        if violations:
            bad_rooms.append((room, violations))
            continue

        for box in boxes:
            path = os.path.join(inbox_dir, box)
            try:
                # ⚠ newline="" 不可省：預設的 universal newlines 會在**讀入時**就把 \r\n 翻成 \n，
                #   於是下面那套「逐行保留行尾」拿到的資料早就被正規化過 —— 保留邏輯看起來對、
                #   實際上救不了任何東西（2026-08-15 沙箱位元組驗證抓到：CRLF 148 → 0）。
                with open(path, "r", encoding="utf-8", newline="") as f:
                    text = f.read()
            except OSError as e:
                unresolved_list.append(f"{room}/{box}: 讀取失敗 {e}")
                continue
            # 區塊職責：保留每一行原本的行尾，只在插入點動。
            # 物理意義：本庫 64 個 inbox 檔裡有 **18 個是 CRLF/LF 混合**（實測 2026-08-15：
            #          trpg-yachiyo/basecamp 148/235、tavern/ame 156/163、kotoko 99/41…）。
            #          若用「整檔判一種行尾再 join」，這 18 檔會被**整檔正規化** ——
            #          資料不壞，但 diff 從「3 行 +」變成「整檔全改」。
            # 數值影響：那不是美觀問題 —— 它銷毀的正是事後查證的能力（`git log -p` 再也看不出
            #          那次到底動了什麼），而且**不會叫**。⇒ keepends 逐行保留，插入行沿用
            #          標題行自己的行尾。（summit 2026-08-15 讀 script 抓到）
            raw_lines = text.splitlines(keepends=True)
            lines = [ln.splitlines()[0] if ln.strip("\r\n") else "" for ln in raw_lines]
            entries = parse_entries(lines)
            if not entries:
                continue

            inserts = []
            for line_no, seq, existing in entries:
                tot_entries += 1
                msg = index.get(seq)
                truth = read_msg_ts(msg) if msg else None
                if existing:
                    tot_have += 1
                    # 免費 oracle：既有 _at 與事實源必須逐字相同 —— 不同即 seq→檔 對應有問題
                    if truth is None:
                        # seq=0 是**哨兵不是缺陷**：那些條目是系統事件（task_claim 衝突等），
                        # 不由任何一則訊息衍生，而 messages 從 1 起算 ⇒ 必然查不到。
                        # ⚠ 「查不到訊息檔」與「本來就沒有訊息」在輸出裡同形會誤導人去查一個沒壞的東西。
                        if seq == 0:
                            sentinel += 1
                        else:
                            anomalies.append(f"{room}/{box}	seq={seq}	事實源查不到訊息檔（已有 _at，未覆寫）")
                            oracle_bad += 1
                        continue
                    ok, label = same_instant(existing[1], truth)
                    if ok:
                        oracle_ok += 1
                        oracle_kinds[label] = oracle_kinds.get(label, 0) + 1
                    else:
                        oracle_bad += 1
                        anomalies.append(
                            f"{room}/{box}	seq={seq}	_at={existing[1]} vs 事實源={truth}（{label}）")
                    continue
                if truth is None:
                    # 不猜 —— 查不到就吵，並且不寫任何東西
                    tot_unresolved += 1
                    unresolved_list.append(f"{room}/{box}:{line_no + 1} seq={seq} 查不到訊息檔")
                    continue
                inserts.append((line_no, seq, truth))
                tot_fill += 1

            if inserts:
                plans.append((room, box, inserts))
                if args.apply:
                    # 降序插入：先動後面的行，前面的行號才不會被自己的插入推移。
                    for line_no, _seq, truth in sorted(inserts, reverse=True):
                        cur = raw_lines[line_no]
                        eol = "\r\n" if cur.endswith("\r\n") else "\n"
                        # 守衛：標題行若是檔案最後一行且**檔尾無換行**，keepends 給的行尾是空的，
                        # 插進去會變成 `## [seq=5] 標題_at 2026-…Z_` 黏成一行。
                        # ⚠ 失敗長相是「標題被污染」不是拋例外 —— 不會叫，所以要有守衛而不是靠運氣。
                        #   實測 2026-08-15：64 檔檔尾無換行者 0 ⇒ 今天走不到，但這條路上不能沒人守。
                        if not cur.endswith(("\r\n", "\n")):
                            raw_lines[line_no] = cur + eol
                        raw_lines.insert(line_no + 1, f"_at {truth}_{eol}")
                    # 區塊職責：atomic 寫 —— tmp 寫完再 os.replace 換過去。
                    # 物理意義：本支改的是**別人的收件匣**，那是他所有未讀 @ 的唯一存放處。
                    #          直接 open(path,"w") 中途斷（Ctrl-C／搶檔／磁碟滿）= 檔案被截斷。
                    # 數值影響：我們在修的問題是「@ 讀不到」，而截斷的失敗模式是「@ 不見了」——
                    #          同一個資產、更重的一級。本 repo 其他寫檔處（AppendStall /
                    #          WriteClosing / SaveTriggers）全部 tmp+move，這支不該是例外。
                    tmp = path + ".tmp"
                    with open(tmp, "w", encoding="utf-8", newline="") as f:
                        f.write("".join(raw_lines))
                    os.replace(tmp, path)

    # ── 報告 ──────────────────────────────────────────────
    if bad_rooms:
        print("⛔ 不變式違例 —— 以下房間**整房跳過**（錯誤的對應長得跟正確的一樣，不降級續跑）：")
        for room, vs in bad_rooms:
            for v in vs:
                print(f"   {room}: {v}")
        print()

    print(f"📊 條目總數 {tot_entries}｜已有 _at {tot_have}｜可回填 {tot_fill}｜查不到 {tot_unresolved}")
    print(f"🔎 oracle（既有 _at vs 事實源同瞬間對拍）：{oracle_ok} 同 / {oracle_bad} 不同")
    for k, v in sorted(oracle_kinds.items(), key=lambda x: -x[1]):
        print(f"     ├ {k}: {v}")

    if sentinel:
        print(f"ℹ️  seq=0 系統事件（無來源訊息，非缺陷）：{sentinel} 筆 —— 已有 _at，時間戳不受影響")

    # 區塊職責：既有異常落磁碟，不擠進退出碼。
    # 物理意義：退出碼是**儀表**（這一次能不能繼續），不是**紀錄**（異常哪來的、誰負責）。
    #          拿儀表存缺陷只剩兩條爛路：永遠擋（沒人能 apply），或降成會被捲走的 warning（等於沒有）。
    # 數值影響：⇒ 閘門只看「本次要寫的集合」；既有異常寫進 _inbox_anomalies.md（只增不減、帶 first_seen），讓缺陷有自己的位置。
    # ⚠ 不可寫成 `if anomalies:` —— 零筆時整段被跳過，於是「缺陷消失了」這件事
    #   **永遠不會被記錄**：舊表停在最後一次有異常的那一刻，而它看起來像現況。
    #   （沙箱驗證抓到：移除違例房後重跑，兩筆該標 resolved 的仍寫著 open。）
    #   空集合是要回答的問題，不是「沒事發生」。
    rep = os.path.abspath(os.path.join(args.rooms_dir, "..", "_inbox_anomalies.md"))
    if anomalies or os.path.isfile(rep):
        # ⚠ dry-run 宣稱「完全唯讀」，那就一個 byte 都不能寫 ——
        #   否則那句宣稱本身就是「板子說沒事」的那種謊。
        if args.apply or args.report_only:
            write_anomaly_record(rep, anomalies, args.now)
        where = rep if (args.apply or args.report_only) else f"{rep}（本次不寫；要單獨產生跑 --report-only）"
        print(f"⚠ 既有異常 {len(anomalies)} 筆（非本次引入，未覆寫）→ {where}")

    if unresolved_list:
        print(f"\n⚠ 查不到事實源的條目（{len(unresolved_list)} 筆，**不猜、不寫**）：")
        for u in unresolved_list[:20]:
            print(f"   {u}")

    if plans:
        print(f"\n📝 {'已寫入' if args.apply else '將寫入'}：")
        for room, box, inserts in plans:
            print(f"   {room}/{box} — {len(inserts)} 筆")
            for line_no, seq, truth in inserts[:3]:
                print(f"      L{line_no + 1} seq={seq} → _at {truth}_")
            if len(inserts) > 3:
                print(f"      …另 {len(inserts) - 3} 筆")

    if not args.apply:
        print("\n（dry-run：沒有寫入任何檔案。確認後加 --apply）")

    # 閘門只描述**這一次的動作**：本次要寫的集合裡有查不到事實源的，或有整房違例 → 擋。
    # 既有異常不進閘門（它們有自己的磁碟位置），否則 apply 會被永遠擋住而缺陷仍然沒人管。
    return 1 if (tot_unresolved or bad_rooms) else 0


if __name__ == "__main__":
    sys.exit(main())
