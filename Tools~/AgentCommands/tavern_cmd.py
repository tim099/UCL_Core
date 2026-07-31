# 區塊職責：Tavern Cmd 的 **client 端規則層** —— 送前預檢（op schema / alias 歸一 / persona 反查 /
#          保留 tag meta）、wait-reply 預設政策、post 成功後的 work-mode banner 抽取。
# 物理意義：本模組是 run_cmd.py 分家出來的第二塊（第一塊是 tavern_handshake）。run_cmd 的職責是
#          「送 cmd 進 Unity 佇列並等它跑完」—— 那是對 **36 個 cmd type 一視同仁**的通用 RPC 管線。
#          而「Tavern 這一個 cmd 的參數長什麼樣、哪些 op 要等回覆、post 完要不要印上班 banner」
#          是**單一 cmd 的業務規則**，混進通用管線會讓 run_cmd 兩成篇幅服務 1/36 的 cmd
#          （Tim 2026-07-29 拍板拆分；抽離前 run_cmd.py 1304 行，本塊佔 ~260 行）。
# 數值影響：本模組**不自行解析任何路徑、不自行偵測環境** —— QUEUE_DIR / TAVERN_DIR / env-marker
#          偵測器全部由 caller 經 configure() 注入。未 configure 就用 → 值為 None 直接炸，
#          不會靜默走到錯的目錄（Plan 硬規則③：不給 fallback 預設值是設計，不是懶）。
#
# 為何是扁平 sibling 模組而不是 `_lib/runcmd/tavern_client.py`（Plan Round 1 原提案）：
#   ① **名稱已被佔用** —— `<repo>/AgentCommands/_lib/tavern_client.py` 已存在且是完全不同的東西
#      （daemon 用的 TavernClient SDK）。同名不同物 = 本 plan 一路在治的 identity 層問題。
#   ② **`_lib` 這個名字本身有 shadowing 陷阱** —— UCL_Core 與主專案各有一個 `_lib`，
#      前者是 namespace package（無 __init__.py）、後者是 regular package（有）。
#      實測：先 `import awakening`（它會把 <repo>/AgentCommands 插到 sys.path[0]）再 import `_lib`
#      → 解析到**主專案鏡像**；反序 → 解析到 UCL_Core。而本模組的 persona 反查正好會在
#      呼叫時 import awakening —— 同一 process 內 `_lib` 指向哪邊會取決於有沒有先發過 post。
#   ③ `tavern_handshake.py` 已用扁平 sibling + configure() 注入的形狀跑過一輪且穩定，
#      沿用同一形狀 = 不發明第二套載入慣例。
from __future__ import annotations

import json
import re
import sys
import uuid
from pathlib import Path
from typing import Callable

# ===========================================================
# 區塊職責：依賴注入 —— 由 run_cmd.configure() 一次設定，其餘函式走 module 級名稱（late binding）。
# 物理意義：模組載入時還不知道資料根在哪（要看 CLAUDE_PROJECT_DIR / git-walk / T-PATH-01 pointer），
#          也不該自己去猜；env-marker 偵測同理（那是 caller 環境的事實，見 Plan §3 Tier B）。
# 數值影響：QUEUE_DIR 供 persona 反查找 session lock；TAVERN_DIR 供 banner 讀 room 的 _last_view.md；
#          DETECT_ENV_MARKER 是無參數 callable，回 "claude-code" / "antigravity" / "gemini" / "unknown"。
# ===========================================================
QUEUE_DIR: Path | None = None
TAVERN_DIR: Path | None = None
DETECT_ENV_MARKER: Callable[[], str] | None = None


def configure(queue_dir: Path, tavern_dir: Path, detect_env_marker: Callable[[], str]) -> None:
    """注入依賴 —— 必須在使用本模組任何函式前呼叫一次（run_cmd 於 import 後立即呼叫）。"""
    global QUEUE_DIR, TAVERN_DIR, DETECT_ENV_MARKER
    QUEUE_DIR = queue_dir
    TAVERN_DIR = tavern_dir
    DETECT_ENV_MARKER = detect_env_marker
    # ⚠ 這裡**刻意不載入 schema** —— configure 的職責只有「注入依賴」。
    # 載入與過期驗算改為 lazy（見 _ensure_schema_loaded / _ensure_freshness_checked）。
    # 原因：run_cmd 在**模組層**呼叫本函式，也就是 import 就跑、在 argparse 決定跑哪個子命令**之前**。
    # 把載入＋雜湊驗算塞在這裡，等於讓 `list` / `catalog` / `recompile` / 任何非 Tavern cmd
    # 全部付一次「走遍 repo ＋ 讀 52 檔算 SHA-256」的成本（實測 `run_cmd.py list` 0.899s，
    # 而直譯器啟動基準只有 0.034s；六支工具是 spawn run_cmd 當 subprocess，每次 spawn 都付一遍）。
    # gura QA 2026-07-29 量出這條；它不報錯只是慢，落在「感覺不出來但天天付」的區間。
    _reset_schema_state()


# ===========================================================
# 區塊職責：catch-up cursor 的**兩階段提交**（Tim 2026-07-31 拍板；apex-one 形式化）
# 物理意義：cursor(`_inbox_cursor/<persona>.json` 的 last_seen_ts) 代表「我看到這裡了」。
#          brief §8 只是**把訊息攤在你面前**，不等於你讀了 —— 所以它寫 pending；
#          真正的確認是**你開口**（self-intro / 任何一則 post 成功）→ pending 升 last_seen_ts。
# 為何不在 brief 生成時直接推：brief 每次 morning 重生成，compact 後重生成一次就會把沒讀過的
#          標成已讀（吃掉的是同事對你說的話）。為何不推「現在」：發文前三秒同事剛講的話
#          你根本沒看到，推 now 等於偷吃。pending 存的是 **§8 實際涵蓋到的最後一則的 ts**。
# 失敗方向：早安半途掛掉 → pending 不提交 → 明天重看一次。
#          **重看不痛，吞掉無感 —— 要選一個方向壞，選會吵的那個。**（apex-one 2026-07-31）
# 數值影響：只動 `_inbox_cursor/<persona>.json`；提交是單調的（pending <= 現有 last_seen_ts 就不動）。
# ===========================================================
def _cursor_file(persona: str):
    if TAVERN_DIR is None or not persona:
        return None
    return TAVERN_DIR / "_inbox_cursor" / f"{persona}.json"


def _cursor_load(persona: str) -> dict:
    f = _cursor_file(persona)
    if f is None or not f.exists():
        return {}
    try:
        return json.loads(f.read_text(encoding="utf-8"))
    except Exception:
        return {}          # 壞檔不擋流程，當成空的重來


def _cursor_save(persona: str, data: dict) -> bool:
    f = _cursor_file(persona)
    if f is None:
        return False
    try:
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        return True
    except Exception:
        return False


def cursor_write_pending(persona: str, covered_ts: str) -> bool:
    """階段一：記下「brief §8 涵蓋到這裡」。不動 last_seen_ts。"""
    if not persona or not covered_ts:
        return False
    d = _cursor_load(persona)
    if covered_ts <= (d.get("last_seen_ts") or ""):
        return False       # 已經確認過的位置，不必再 pending
    d["pending_ts"] = covered_ts
    return _cursor_save(persona, d)


def cursor_commit_pending(persona: str):
    """階段二：開口了 → pending 升 last_seen_ts。回實際提交的 ts；沒東西可提交回 None。"""
    if not persona:
        return None
    d = _cursor_load(persona)
    pend = d.get("pending_ts") or ""
    if not pend or pend <= (d.get("last_seen_ts") or ""):
        return None
    d["last_seen_ts"] = pend
    d.pop("pending_ts", None)
    # updated_at 跟著推 —— 不然這欄會停在「上次 tavern_catchup 跑的時間」，
    # 看檔的人會以為 cursor 很久沒動（欄位自己說謊，正是今天在治的那族）
    import datetime as _dt
    d["updated_at"] = _dt.datetime.now(_dt.timezone.utc).isoformat().replace("+00:00", "Z")
    return pend if _cursor_save(persona, d) else None


# ===========================================================
# 區塊職責：載入 C# 反射生成的 commands_schema.json —— 這是 op 規格的**唯一**來源。
# 物理意義：手抄鏡像會漂（血證 2026-07-29：`create_trpg_room` 在 C# 完整實作卻被 client 擋死；
#          另有六處 required 比 server 還嚴，會擋掉合法呼叫）。產物由 C# 端
#          `UCL_CmdSchemaExporter` 依 handler 的 ArgsSpec 生成，是同一份事實的機器搬運。
# 數值影響：載入成功 → 覆寫 TAVERN_OP_SCHEMA / TYPE_ALIASES_FROM_SCHEMA；
#          任何一步失敗 → TAVERN_OP_SCHEMA 維持空 dict → schema 層整個跳過（fail-open），
#          **絕不因為讀不到產物就擋人**；也不留手抄表當後備（已知錯誤的後備比沒有更糟）。
#
# 過期判準用**內容雜湊不用 mtime**：git 不儲存 mtime，clone / checkout 後所有檔案時間都是
# 「當下寫檔時間」，先後只看寫檔次序 —— 而「clone 下來直接用」正是產物入 git 的主要理由，
# 用 mtime 等於在最該生效的場景擲骰子，且沉默地擲（gura QA 2026-07-29 推翻原案）。
# ===========================================================
SCHEMA_FILE_NAME = "commands_schema.json"
# 預檢總開關的旗標檔 —— 與 C# UCL_CmdSchemaExporter.DisableFlagFileName 同名（存在即停用）。
# 用檔案而非 EditorPrefs：這個開關要跨語言生效，EditorPrefs 只有 C# 讀得到。
SCHEMA_DISABLE_FLAG_NAME = "_cmd_schema_disabled.local"
SUPPORTED_SCHEMA_VERSION = 1        # 與 C# UCL_CmdSchemaExporter.SchemaVersion 對齊

# 產物載入狀態 —— 供 selftest 與診斷輸出檢視（不參與判斷邏輯）
SCHEMA_STATUS = {"loaded": False, "stale": False, "reason": "not-configured", "path": None}

# 從產物載入的 cmd type 別名表（消滅 run_cmd.TYPE_ALIASES 與 Registry.s_TypeAliases 的雙份手抄）
TYPE_ALIASES_FROM_SCHEMA: dict = {}


# 產物快取（per-machine，不入 git）—— 見 _ensure_freshness_checked 的三段說明
FRESHNESS_CACHE_NAME = "_cmd_schema_freshness.local.json"

# lazy 狀態旗標：None = 還沒做過；True/False = 已做過（不重複）
_SCHEMA_LOADED: bool = False
_FRESHNESS_CHECKED: bool = False


def _reset_schema_state() -> None:
    """configure() 時清狀態 —— 下次真的要用 schema 才會載入／驗算。"""
    global _SCHEMA_LOADED, _FRESHNESS_CHECKED, TAVERN_OP_SCHEMA, TYPE_ALIASES_FROM_SCHEMA, SCHEMA_STATUS
    _SCHEMA_LOADED = False
    _FRESHNESS_CHECKED = False
    TAVERN_OP_SCHEMA = {}
    TYPE_ALIASES_FROM_SCHEMA = {}
    SCHEMA_STATUS = {"loaded": False, "stale": False, "reason": "lazy-未載入", "path": None}


def compute_source_hash(repo_root: Path, rel_files) -> str:
    """依**產物指定的檔案清單**重算來源雜湊。

    契約（與 C# `UCL_CmdSchemaExporter.ComputeSourceHash` 對齊）：
      ③ 逐檔餵入 相對路徑 UTF-8 bytes → 一個 0 byte → 檔案原始 bytes（不做換行正規化）
      ④ SHA-256，小寫 hex
    **① 檔案集合與 ② 排序由 C# 決定並寫進產物的 `source_files`** —— 本端不自己 glob。
    原本兩端各寫一份 glob 規則，實測 Python 那份已在撈 `Library/PackageCache/*/Assets` 與
    `.git/modules/`，且跨專案（多 Unity 專案的 repo）永久不等價 → 預檢永久靜默降級。
    現在集合只有一個來源，本端只負責驗算。
    """
    import hashlib
    h = hashlib.sha256()
    for rel in rel_files:                       # 順序照產物給的（C# 已序數排序）
        h.update(rel.encode("utf-8"))
        h.update(b"\x00")
        h.update((repo_root / rel).read_bytes())
    return h.hexdigest()


def _stat_signature(repo_root: Path, rel_files) -> str:
    """回「清單內每個檔的 (路徑, mtime_ns, size)」的雜湊 —— 只作為**快取失效提示**。

    ⚠ 這不是正確性判準。判過期的權威始終是內容 SHA-256（見 compute_source_hash）——
    mtime 在 clone / checkout 後會整批變動，拿來判新舊會在「clone 下來直接用」這個主場景擲骰子。
    但反過來用是安全的：**簽章一變就重算**（可能白算一次，無害）；簽章沒變就沿用上次算過的內容雜湊
    （檔案內容改了卻能維持 mtime＋size 完全不變的情況，實務上要刻意構造才做得出來）。
    這條讓常態路徑從「讀 52 個檔的完整 bytes」降成「stat 52 次」。
    """
    import hashlib
    h = hashlib.sha256()
    for rel in rel_files:
        try:
            st = (repo_root / rel).stat()
            h.update(f"{rel}|{st.st_mtime_ns}|{st.st_size}\n".encode("utf-8"))
        except OSError:
            h.update(f"{rel}|missing\n".encode("utf-8"))
    return h.hexdigest()


# ─── 階段一：載入產物（便宜 —— 讀一個 ~5KB 的 JSON）────────────────────────
# 何時需要：要查 op schema（validate_args）或 type_aliases（run_cmd.normalize_cmd_type）時。
# 刻意**不**在這裡驗新鮮度：驗算貴，而「知道有哪些 op」跟「這份表夠不夠新」是兩件事 ——
# type_aliases 這種純資料就算表舊了照樣可用，不必為它付雜湊成本。
_SCHEMA_RAW: dict = {}


def _ensure_schema_loaded() -> None:
    global TAVERN_OP_SCHEMA, TYPE_ALIASES_FROM_SCHEMA, SCHEMA_STATUS, _SCHEMA_LOADED, _SCHEMA_RAW
    if _SCHEMA_LOADED:
        return
    _SCHEMA_LOADED = True
    path = QUEUE_DIR / SCHEMA_FILE_NAME if QUEUE_DIR else None
    SCHEMA_STATUS = {"loaded": False, "stale": False, "reason": "", "path": str(path) if path else None}
    try:
        # 總開關（Tim 2026-07-30）：旗標檔存在 = 本機停用 schema 預檢。
        # 停用時**連產物都不讀** —— 行為與「產物不存在」逐字相同（這是開關的定義）。
        # 判定只看檔案在不在，沒有內容格式可漂；C# 端同一個檔，兩端不需要各自維護狀態。
        if QUEUE_DIR and (QUEUE_DIR / SCHEMA_DISABLE_FLAG_NAME).is_file():
            SCHEMA_STATUS["reason"] = "預檢已停用（旗標檔存在）→ 跳過 schema 預檢"
            print("  ℹ schema 預檢已停用（本機）→ **跳過參數預檢**（不影響送出，由 Editor 判）。\n"
                  "     重新啟用：控制台 → Cmd 後台管理頁 → 勾回「啟用 schema 預檢」", file=sys.stderr)
            return
        if path is None or not path.is_file():
            # 產物是 per-machine 衍生物、**不入 git**（Tim 2026-07-30 拍板）—— 所以新 clone／新機器
            # 上缺席是**常態不是錯誤**。此處 fail-open：跳過 schema 預檢，行為退回「送出去讓 Editor 判」。
            # 自癒管道有兩條，都不需要人記得：
            #   ① Unity 端 UCL_CmdSchemaAutoSync 偵測到產物不存在 → 無視每日節流立刻生成
            #   ② 手動：下面這行指令，或 控制台 → Cmd 後台管理頁 → 重新生成
            # 這裡**不由 Python 自動觸發生成**：那要 spawn run_cmd 送 cmd 進 Editor，
            # Editor 沒開時會卡滿 ack timeout，而且 run_cmd import 就會走到這條路 → 自我遞迴。
            SCHEMA_STATUS["reason"] = "產物不存在 → 跳過 schema 預檢（fail-open）"
            print("  ℹ commands_schema.json 不存在 → **本次跳過參數預檢**（不影響送出，由 Editor 判）。\n"
                  "     產生它：run_cmd.py run ExportCmdSchema"
                  "（Unity 下次編譯也會自動補上）", file=sys.stderr)
            return
        data = json.loads(path.read_text(encoding="utf-8"))
        ver = data.get("schema_version")
        if ver != SUPPORTED_SCHEMA_VERSION:
            # 版本不認識就別猜格式 —— 不預檢比用錯誤的解讀安全
            SCHEMA_STATUS["reason"] = f"schema_version={ver} 非本端支援的 {SUPPORTED_SCHEMA_VERSION} → 不做 schema 預檢"
            return

        tavern = (data.get("commands") or {}).get("Tavern") or {}
        ops = tavern.get("ops") or {}
        if not ops:
            SCHEMA_STATUS["reason"] = "產物內沒有 Tavern.ops → 不做 schema 預檢"
            return

        # 產物 → 本模組的內部表示（optional 一律空 list：本端從不 enforce optional，見上方設計註解）
        TAVERN_OP_SCHEMA = {
            op: {"required": list(spec.get("required") or []),
                 "aliases": dict(spec.get("aliases") or {}),
                 "optional": []}
            for op, spec in ops.items()
        }
        TYPE_ALIASES_FROM_SCHEMA = dict(data.get("type_aliases") or {})
        _SCHEMA_RAW = data
        SCHEMA_STATUS["loaded"] = True
        SCHEMA_STATUS["reason"] = "已載入（新鮮度未驗）"
    except Exception as e:
        # 讀產物的任何環節出錯都不該影響發言能力
        SCHEMA_STATUS["reason"] = f"載入失敗（{type(e).__name__}: {e}）→ 不做 schema 預檢"
        print(f"  ⚠ commands_schema.json 載入失敗（{type(e).__name__}）→ 本次不做 schema 預檢", file=sys.stderr)


# ─── 階段二：驗新鮮度（貴 —— 但只在「即將據此擋人」時才做）────────────────────
# 何時需要：**只有 required 檢查會擋人**，所以只在要 enforce required 前驗一次。
# alias 歸一、type_aliases 這些「純轉換不擋人」的用途不觸發驗算 —— 表舊了頂多少一條 alias，
# 那是便利性折損，不是正確性事故，不值得為它付成本。
#
# 三層成本遞減：
#   ① 產物沒給 source_files → 無法驗 → **降級**（不 enforce）。不 glob 猜集合（見 compute_source_hash）。
#   ② stat 簽章與上次相同 → 直接沿用上次結論（常態路徑，只 stat 不讀檔）。
#   ③ 簽章不同 → 老實讀檔重算 SHA-256，並把結論寫回 per-machine 快取。
def _ensure_freshness_checked() -> None:
    global SCHEMA_STATUS, _FRESHNESS_CHECKED
    if _FRESHNESS_CHECKED or not SCHEMA_STATUS.get("loaded"):
        return
    _FRESHNESS_CHECKED = True
    try:
        rel_files = list(_SCHEMA_RAW.get("source_files") or [])
        artifact_hash = _SCHEMA_RAW.get("source_hash") or ""
        if not rel_files or not artifact_hash:
            # ① 舊版 / 外來產物沒帶清單 → 無法驗證。此處**選擇降級**（不 enforce required）：
            #    無法驗證的表與過期的表風險同型 —— 拿它擋人可能擋掉合法呼叫，而放行最壞只是慢一趟。
            SCHEMA_STATUS["stale"] = True
            SCHEMA_STATUS["reason"] = "產物未帶 source_files（舊版產物）→ 無法驗新鮮度，預檢降級"
            print("  ⚠ commands_schema.json 未帶 source_files → 無法驗新鮮度，**參數預檢降級為不擋**。\n"
                  "     重新生成即可帶上：run_cmd.py run ExportCmdSchema", file=sys.stderr)
            return

        repo_root = QUEUE_DIR.parent
        sig = _stat_signature(repo_root, rel_files)
        cache_path = QUEUE_DIR / FRESHNESS_CACHE_NAME
        cached = {}
        try:
            if cache_path.is_file():
                cached = json.loads(cache_path.read_text(encoding="utf-8"))
        except Exception:
            cached = {}       # 快取壞掉不是錯誤，重算就好

        # ② 快取命中：同一份產物 + 同一組檔案 stat 簽章 → 沿用上次算出的內容雜湊
        if cached.get("artifact_hash") == artifact_hash and cached.get("stat_sig") == sig:
            current_hash = cached.get("source_hash") or ""
        else:
            # ③ 老實重算，並寫回快取（per-machine，gitignored）
            current_hash = compute_source_hash(repo_root, rel_files)
            try:
                cache_path.write_text(json.dumps(
                    {"artifact_hash": artifact_hash, "stat_sig": sig, "source_hash": current_hash},
                    ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            except OSError:
                pass          # 寫不了快取只是下次再算一遍，不影響正確性

        if artifact_hash != current_hash:
            SCHEMA_STATUS["stale"] = True
            SCHEMA_STATUS["reason"] = "產物落後於 Cmd 原始碼"
            # 第一道防線是**改變行為**不是印字：過期 → 參數預檢整體降級（見 validate_args）
            print("  ⚠ commands_schema.json 已過期（source_hash 不符）→ **參數預檢自動降級為不擋**。\n"
                  "     重新同步：run_cmd.py run ExportCmdSchema"
                  "（或控制台 → Cmd 後台管理頁 → 重新生成）", file=sys.stderr)
        else:
            SCHEMA_STATUS["reason"] = "ok"
    except Exception as e:
        # 驗不了就降級，不擋人（無法驗證 ≠ 不通過，但也 ≠ 可以拿來擋）
        SCHEMA_STATUS["stale"] = True
        SCHEMA_STATUS["reason"] = f"新鮮度驗算失敗（{type(e).__name__}: {e}）→ 預檢降級"


# ===========================================================
# 區塊職責：Tavern op 參數規格 —— **執行期內容全部來自 C# 產物 commands_schema.json**。
# 物理意義：這裡刻意留空 dict 而非手抄表。原本有一份 58 行的手抄鏡像當 fallback，
#          2026-07-29 Tim 指出「產物應該取代這一塊」後移除 —— 理由不是省行數：
#          那份手抄表**已被證實是錯的**（漏 create_trpg_room，另有 6 處 required 比 server 嚴，
#          會擋掉合法呼叫）。留著一份已知錯誤的表當後備，比沒有後備更糟：
#          「無法驗證 ≠ 不通過」的同一條原則，套在這裡就是「schema 缺席 → 不預檢」，
#          而不是「schema 缺席 → 用一份可能錯的表擋人」。
#          產物已入 git，clone 下來就有；真的沒有時 validate_args 整個 schema 層跳過（fail-open）。
# 數值影響：configure() → _load_generated_schema() 會把本 dict 整個換成產物內容（34 op）。
#          載入失敗 → 維持空 dict → 不做 alias 歸一與 required 檢查，但 persona 反查等
#          Python 端固有檢查照常執行（見 validate_args 的 A/B 兩層說明）。
# 結構：{op: {"required": [...], "aliases": {alias: canonical}, "optional": []}}
#      optional 一律空 —— 本端從不 enforce optional，不收沒人用的欄位（它一定會爛）。
# ===========================================================
TAVERN_OP_SCHEMA: dict = {}

# Quest ops 集合 — auto-fill idempotency_key 用（純查詢 op 不需要）
QUEST_OPS_NEEDING_IDEMPOTENCY = {"task_create", "task_claim", "task_progress", "task_done", "task_release",
                                  "task_review_request", "task_reject", "task_reopen", "task_force_reclaim"}

# 區塊職責: 保留 tag meta schema 表 — 鏡像 Cmd_Tavern.Op_Post 的 T06.3 驗證 (server 端為權威)。
# 物理意義: meta 格式 "k:v;k2:v2"; tag 命中保留字時要求對應必填 key。
# 數值影響: 新增保留 tag 時兩端同步擴表 (server: Cmd_Tavern.cs Op_Post; client: 本表)。
RESERVED_TAG_META_SCHEMA = {
    "task-assign": ["task_id", "task_body", "assigned_by", "requires_ack"],
    "task-ack": ["task_id", "action"],
    # commit 公告貼文（Tim 2026-07-30）— 這則貼文同時是「給同事看的 commit 概要」與「+5 token 計酬憑證」，
    # 沒 sha 就無法事後稽核對到哪次 commit → 必填。
    # **一則訊息一個 SHA**：三層 submodule bump 分三則各自公告、各領 5（計價單位同舊規則「一 commit 一筆」，
    # 只是費率 1→5）。多 SHA 塞一則會被 server 端 T06.3 擋掉；格式須為 7~40 位 hex。
    "commit": ["sha"],
}

# 區塊職責：不需要 client 端同步等回覆的 op 清單（進場 / 查詢類）。
# 物理意義：這些 op 不是「跟人交流」而是「跟系統要資料」，等回覆等於白等一個永遠不來的東西。
# 數值影響：命中 → wait_reply 強制 0.0（覆寫使用者值），fire-and-forget 直接結束。
NO_WAIT_REPLY_OPS = frozenset({
    "read", "inbox_read", "get_presence", "wait_check",
    "task_list", "session_enter", "set_focus", "set_mood",
})

# post 的預設等待秒數 — 540s = 9 min，留 60s buffer 給 Claude Code Bash tool 的 10 min 硬上限。
# 想拉滿請顯式 --wait-reply 600 並把 Bash 呼叫帶 timeout=600000ms。
DEFAULT_POST_WAIT_REPLY_SEC = 540.0


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
def autofill_persona_from_lock(arg_pairs: dict) -> None:
    # 已顯式帶 persona → 尊重不覆寫
    if (arg_pairs.get("persona") or "").strip():
        return
    try:
        # 重用 awakening 的 env-hash / lock helper，避免反查邏輯雙份漂移
        import importlib
        awk = importlib.import_module("awakening")
        # session dir 優先用注入的 QUEUE_DIR（走 CLAUDE_PROJECT_DIR + git-walk，
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
            marker = (DETECT_ENV_MARKER() or "").lower()
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


def validate_reserved_tag_meta(meta_raw: str) -> tuple[bool, str]:
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


def validate_args(arg_pairs: dict) -> tuple[bool, str]:
    """Tavern Cmd 提交前驗證；回 (ok, error_message)。寬進：alias 自動歸一到 canonical 名。

    分兩層，**互不依賴**：
      A. **schema 驅動層**（alias 歸一 / required）—— 資料來自 C# 產物，產物缺席或過期就整層跳過。
      B. **Python 端固有層**（quest idempotency / persona 反查 / 保留 tag meta）—— 與 C# schema 無關，
         **永遠執行**。persona 反查尤其不可漏：漏了會用錯身分發言（多 lock 環境冒名是實證過的事故）。
    早期版本把 B 放在 A 的成功路徑之後，未知 op 會 early-return → 連 persona 反查一起跳過；
    產物缺席時所有 op 都算未知，等於整個 B 層失效（Tim 2026-07-29 提問時發現，已改為兩層獨立）。
    """
    _ensure_schema_loaded()      # lazy：真的要查 schema 了才讀產物（見 configure 的效能說明）
    op = (arg_pairs.get("op") or "").lower().strip()
    if not op:
        # 缺 op 是**確定失敗**（Cmd_Tavern 第一件事就是讀 op），不是「我不認識」→ 維持 fail-closed
        known = ", ".join(sorted(TAVERN_OP_SCHEMA.keys())) if TAVERN_OP_SCHEMA else "(schema 未載入)"
        return False, f"Tavern Cmd 缺少 op 參數。可用 op：{known}"

    # ─── A. schema 驅動層 ────────────────────────────────────────────────
    # 三種「不做 schema 檢查」的情形，全部 fail-open（放行交給 Editor 判）：
    #   ① 產物未載入（沒 export 過 / 檔案不在）→ 這是常態不是錯誤，安靜跳過
    #   ② 產物過期（source_hash 不符）→ 已在載入時警告過一次，這裡不再逐筆吵
    #   ③ 產物有但不認得這個 op → 可能是產物落後；便利性功能不該擋掉正確性
    #      （血證 2026-07-29：`create_trpg_room` 在 C# 完整實作卻被 client 擋死）
    # 注意：這裡**不看 stale** —— 新鮮度是 lazy 驗的，只在真的要擋人（required）前才付成本。
    # alias 歸一即使表舊了也照做：它不擋人，最壞是少歸一一條，屬便利性折損不是正確性事故。
    schema = TAVERN_OP_SCHEMA.get(op)
    schema_active = schema is not None
    if TAVERN_OP_SCHEMA and schema is None:
        print(f"  ⚠ Tavern op '{op}' 不在 schema 產物內 — **放行交給 Editor 判**。\n"
              f"     若這是打錯字，Editor 會回報；若這是新增的 op，代表產物落後 →\n"
              f"     重新同步：run_cmd.py run ExportCmdSchema", file=sys.stderr)

    if schema_active:
        # alias 歸一（mutate arg_pairs）—— 順序即優先序，先到先得
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

        # 只有 required 檢查會**擋人**，所以到這裡才付新鮮度驗算的成本；
        # 驗完若判定過期 → 整層降級不擋（拿一份已知不對的表擋人比沒有表更糟）。
        _ensure_freshness_checked()
        missing = [] if SCHEMA_STATUS.get("stale") else [r for r in schema["required"] if not arg_pairs.get(r)]
        if missing:
            msg = f"Tavern op={op} 缺少必要參數：{missing}（你目前傳的：{list(arg_pairs.keys())}）"
            if schema["aliases"]:
                msg += f"\n     ↳ 可接受的 alias：{schema['aliases']}"
            return False, msg

    # ─── B. Python 端固有層（與 C# schema 無關，永遠執行）──────────────────
    # Quest ops auto-fill idempotency_key（user 沒顯式給就自動 uuid4）
    if op in QUEST_OPS_NEEDING_IDEMPOTENCY and not arg_pairs.get("idempotency_key"):
        arg_pairs["idempotency_key"] = str(uuid.uuid4())
        print(f"  ℹ idempotency_key 自動填入：{arg_pairs['idempotency_key']}", file=sys.stderr)
    if op == "post":
        # 換行防呆（Tim 2026-07-31 回報，seq 14095）：body 的換行是字面 "\n" 時修回真換行。
        # 物理意義：body 經 CLI `--arg body` 傳入，而 CLI 參數不會把兩字元的 backslash+n
        #          解讀成換行 → 整則訊息擠成一行、段落間留著可見的 "\n"。
        # **為什麼攔在 client 端而不是 server 端**：server 的 Cmd_Glossary 會在 body 後面
        #          追加「本回提到的新詞」區塊（帶真換行）。若拿追加後的 body 判，那些真換行
        #          會把作者段的 escaping 失敗掩蓋掉 —— 實測全庫 336 則命中會漏掉 124 則(37%)。
        #          在這裡 body 還是**純作者文字**，判準最乾淨。
        # 判準與實作共用 escaped_newlines 模組（與晚安信同一份規則，不複製門檻避免漂移）。
        _body = arg_pairs.get("body")
        if isinstance(_body, str):
            try:
                import escaped_newlines
                _fixed, _changed = escaped_newlines.normalize(_body)
                if _changed:
                    arg_pairs["body"] = _fixed
                    print(f"  ⚠ body 的{escaped_newlines.HINT}", file=sys.stderr)
            except ImportError:
                pass    # 模組缺席 → 原樣送出，不擋發言（fail-soft，這只是便利性修正）

        # post 沒帶 persona → 反查登入 lock 自動補（防漏帶 persona，Tim 2026-05-27）
        autofill_persona_from_lock(arg_pairs)
        # 保留 tag 的 meta schema 預檢（Tim 2026-07-28 拍板「錯誤資訊在發送流程就知道」）—
        # 鏡像 Cmd_Tavern T06.3 server 端驗證: 缺必填 meta 在 client 端 <0.01s 就擋,
        # 不必等 Editor round-trip 才在 ErrorLog 看到 RejectLastOp。
        ok, err = validate_reserved_tag_meta(arg_pairs.get("meta") or "")
        if not ok:
            return False, err
    return True, ""


# ===========================================================
# 區塊職責：wait-reply 預設值決策 —— 決定 post 完要不要在 client 端等對方回覆、等多久。
# 物理意義：這是**業務政策**不是 RPC 機制 —— 「哪些 op 算跟人交流」只有 Tavern 自己知道。
#          三段決策（顯式值 > 進場/查詢類強制 0 > 依 op 與 meta 給預設），彼此有優先序。
# 數值影響：回傳秒數，caller 據此決定要不要走 tavern_handshake.wait_for_tavern_reply。
#          0 = fire-and-forget。
# ===========================================================
def resolve_wait_reply(cmd_type: str, arg_pairs: dict, explicit: float | None) -> float:
    """回傳本次 post 該等幾秒。explicit=None 表示使用者沒指定，由本函式決定。"""
    is_tavern = cmd_type.lower() == "tavern"
    op = (arg_pairs.get("op") or "").lower()

    # 進場與查詢類 Op 強制 0 —— 即使使用者顯式指定也覆寫（等一個不會來的回覆沒有意義）
    if is_tavern and op in NO_WAIT_REPLY_OPS:
        print(f"  ℹ️  偵測到進場與查詢類 Op (op={op}) — 自動強制 --wait-reply 0")
        return 0.0

    # 使用者顯式指定 → 尊重
    if explicit is not None:
        return float(explicit)

    if not (is_tavern and op == "post"):
        return 0.0

    # Solo Brainstorm 例外：下一則 post 是同 agent 自己發，wait-reply 等於自己等自己，純浪費。
    meta_str = arg_pairs.get("meta", "") or ""
    if "tag:solo-brainstorm" in meta_str or "tag=solo-brainstorm" in meta_str:
        print("  ℹ️  偵測到 tag:solo-brainstorm — 自動 --wait-reply 0（自言自語不等回覆）")
        return 0.0

    return DEFAULT_POST_WAIT_REPLY_SEC


# 區塊職責：ergonomic shim — 把 --arg wait-reply=N 視同 --wait-reply N。
# 物理意義：使用者 / agent 直覺會把 wait-reply 當 cmd arg 寫（因為 room / sender / op 都是 --arg 語法），
#          但它其實是 script flag。沒這 shim 的話 --arg wait-reply=0 只是塞進 cmd args dict 被
#          Cmd_Tavern 忽略，script 仍走 default，user 看到 timeout 印出來才發現踩坑。
# 數值影響：promote 後從 arg_pairs 移除（避免變 cmd noise / 寫進 meta）；
#          回傳 promote 出來的值，None 表示沒有 shim 命中。顯式 flag 由 caller 優先處理。
# ⚠ **首個成功轉換者勝**（不是最後一個）—— 兩個鍵同時出現時取 `wait-reply`。
#   這條是搬移前的原始語意：舊碼靠 `if args.wait_reply is None` 守門，第一個鍵設完值之後
#   第二個鍵就進不去了。搬移時我寫成迴圈內無條件覆寫 → 變成「後到覆蓋」，
#   雙鍵並存時 11 會被 22 蓋掉（gura QA 2026-07-29 差分測試抓到，seq 13921/13923）。
#   注意「首個**成功轉換**者」而非「首個出現者」：第一個鍵轉換失敗時仍讓第二個鍵接手，
#   這也是舊碼行為（轉換失敗 → args.wait_reply 仍是 None → 守門不擋第二個）。
def promote_wait_reply_arg(arg_pairs: dict) -> float | None:
    promoted = None
    for key in ("wait-reply", "wait_reply"):
        if key in arg_pairs:
            val = arg_pairs.pop(key)      # 兩個鍵都要移除（即使不採用）—— 不可變成 cmd noise
            if promoted is not None:
                print(f"  ⚠ --arg {key}={val} 被忽略（已由較前的鍵決定 --wait-reply={promoted}）")
                continue
            try:
                promoted = float(val)
                print(f"  ℹ️  偵測到 --arg {key}={val} → promote 為 --wait-reply（建議直接用 script flag）")
            except ValueError:
                print(f"  ⚠ --arg {key}={val} 無法轉 float，已忽略")
    return promoted


# ===========================================================
# 區塊職責: post 成功後抓 room 的 _last_view.md 內 work-mode banner 印到 caller stdout。
# 物理意義: Op_Post 寫 banner 到落地檔，但 caller 沒人讀那檔；wait-reply 走的是等對方回覆那條路，
#          兩條路都接不到 banner → caller 看不到 work-session hint（crest-001 QA 2026-05-14）。
# 數值影響: 純 stdout print，讀不到 / 沒 banner 一律靜默跳過，不擋主流程。
#          讀 room-level _last_view.md 而非全域 _last_op.md —— 後者會被其他 cmd 覆蓋。
# ===========================================================
_WORK_MODE_BANNER_RE = re.compile(
    r"⏰ \*\*work-session active\*\*[^\n]*\n[🎯💸⛔📋🚫💭💬][^\n]*"
)


def print_work_mode_banner(room: str) -> None:
    try:
        if not room:
            return
        last_view = TAVERN_DIR / "rooms" / room / "_last_view.md"
        if not last_view.exists():
            return
        match = _WORK_MODE_BANNER_RE.search(last_view.read_text(encoding="utf-8"))
        if match:
            print("\n──── 上班 hint ────")
            print(match.group(0))
            print("───────────────────")
    except Exception:
        pass    # banner 是加值資訊，讀不到不該影響 post 的判決


# ===========================================================
# 區塊職責：自測入口 —— `python tavern_cmd.py --selftest`
# 物理意義：本模組是從 run_cmd 搬過來的，**搬移的驗收標準是「行為零變化」**，不是「能 import」。
#          這些測項逐條對照搬移前 run_cmd 內的原始行為（wait-reply 三段優先序、alias 歸一、
#          quest idempotency 自動填、T06.3 保留 tag、persona 反查不覆寫），是那次比對的固化版本 ——
#          日後任何人再動這塊，這裡會先紅。
# 數值影響：純函式層直呼 + 唯讀（persona 反查只讀 session lock）；不 post、不寫任何檔。
#          呼叫端須先 configure()；本入口自行注入（走 run_cmd 的解析，不自己算路徑）。
# ===========================================================
def _selftest() -> int:
    import contextlib
    import pathlib
    import importlib
    import io

    # 依賴注入沿用 run_cmd 的解析（唯一真相源），避免自測跟生產走不同的資料根。
    # ⚠ 必須**自己**呼叫 configure：以 `python tavern_cmd.py --selftest` 執行時，本檔是 `__main__`，
    #   而 run_cmd 內的 `import tavern_cmd` 會載入**另一份副本**並只設定那一份 —— 靠它幫我們注入，
    #   會讓 __main__ 這份永遠是 None（Python 雙模組陷阱；tavern_handshake 踩過同一個）。
    #   這條是實測撞出來的，不是預防性註解：本模組 selftest 第一次跑就被 ⑥ 的前提監視器抓紅。
    #   而它會「炸得漂亮」而不是靜默走到錯的目錄，正是因為模組層拒絕給 fallback 預設值。
    rc = importlib.import_module("run_cmd")
    configure(queue_dir=rc.QUEUE_DIR, tavern_dir=rc.TAVERN_DIR,
              detect_env_marker=rc._detect_caller_env_marker)
    # 立刻取樣：configure 當下應該還沒載入任何 schema（lazy 前提，見測項⑦）。
    # 必須在這裡取 —— 後面任何 validate_args 都會觸發載入，取晚了就永遠是 True。
    _loaded_right_after_configure = _SCHEMA_LOADED

    failures: list[str] = []

    def check(name: str, got, want) -> None:
        if got == want:
            print(f"  ✓ {name}")
        else:
            print(f"  ✗ {name}: got={got!r} want={want!r}")
            failures.append(name)

    def quiet(fn, *a, **k):
        """吃掉被測函式的提示輸出 —— 這裡驗的是回傳值與副作用，不是它印什麼。"""
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            return fn(*a, **k)

    print("[selftest] tavern_cmd — 對照搬離 run_cmd 前的行為")

    # ① wait-reply 三段優先序：強制 0 > 顯式 > 依 op/meta 預設
    check("post 無顯式 → 540", quiet(resolve_wait_reply, "Tavern", {"op": "post"}, None), 540.0)
    check("post + solo-brainstorm → 0",
          quiet(resolve_wait_reply, "Tavern", {"op": "post", "meta": "tag:solo-brainstorm"}, None), 0.0)
    check("post 顯式 300 → 300", quiet(resolve_wait_reply, "Tavern", {"op": "post"}, 300.0), 300.0)
    check("read → 強制 0", quiet(resolve_wait_reply, "Tavern", {"op": "read"}, None), 0.0)
    # 這條是優先序的關鍵：查詢類 op 連**顯式值**都要蓋掉（等一個不會來的回覆沒有意義）
    check("read 顯式 300 → 仍強制 0", quiet(resolve_wait_reply, "Tavern", {"op": "read"}, 300.0), 0.0)

    # ①-b 前提監視器：**run_cmd 的 wait-reply 守衛讀的 key 名，必須是 alias 歸一後真的存在的那個**。
    # 血證 2026-07-31：四名歸一把 sender→agent 之後，守衛還在讀 "sender" → key 永遠不存在
    #   → 每一則 op=post 都回判決碼 3「完全沒有等待」，而且它照樣有輸出，所以壞了沒人喊。
    # 本測項的意義：哪天再改名，這裡會紅，而不是等某個人某天覺得「怎麼都沒等」。
    _pairs = {"op": "post", "room": "tavern", "sender": "Myth"}
    quiet(validate_args, _pairs)                    # 跑一次 alias 歸一（會 mutate _pairs）
    _canon = next((k for k in ("agent", "sender", "sender_id") if k in _pairs), None)
    _guard_src = pathlib.Path(__file__).with_name("run_cmd.py").read_text(encoding="utf-8")
    _guard_reads_canon = f'arg_pairs.get("{_canon}")' in _guard_src
    check(f"wait-reply 守衛讀 canonical 名（歸一後 = {_canon}）", _guard_reads_canon, True)
    check("tavern join → 0", quiet(resolve_wait_reply, "Tavern", {"op": "join"}, None), 0.0)
    check("非 Tavern cmd → 0", quiet(resolve_wait_reply, "Recompile", {}, None), 0.0)
    check("非 Tavern 顯式 99 → 99", quiet(resolve_wait_reply, "Recompile", {}, 99.0), 99.0)

    # ② shim：--arg wait-reply=N 提升為 script flag，且一律從 arg_pairs 移除（不可變成 cmd noise）
    d = {"op": "post", "wait-reply": "12"}
    check("shim promote 值", quiet(promote_wait_reply_arg, d), 12.0)
    check("shim 後移除 key", "wait-reply" in d, False)
    d = {"op": "post", "wait_reply": "bad"}
    check("shim 非數字 → None", quiet(promote_wait_reply_arg, d), None)
    check("shim 非數字也移除 key", "wait_reply" in d, False)
    # 雙鍵並存 —— 舊碼靠 `if args.wait_reply is None` 守門 = **先到先贏**。
    # 搬移時一度寫成無條件覆寫（後到覆蓋），gura QA 差分測試抓到（2026-07-29 seq 13923）。
    # 這組原本是 selftest 的覆蓋盲區：四個 shim 測項都只給單鍵，組合就漏了。
    d = {"op": "post", "wait-reply": "11", "wait_reply": "22"}
    check("shim 雙鍵並存 → 先到先贏(11)", quiet(promote_wait_reply_arg, d), 11.0)
    check("shim 雙鍵都要移除", ("wait-reply" in d, "wait_reply" in d), (False, False))
    # 首個「成功轉換」者勝，不是首個「出現」者：第一個轉換失敗要讓第二個接手（舊碼同此）
    d = {"op": "post", "wait-reply": "bad", "wait_reply": "33"}
    check("shim 首鍵壞 → 次鍵接手(33)", quiet(promote_wait_reply_arg, d), 33.0)

    # ③ validate_args：required / 未知 op / alias 歸一 / quest idempotency
    check("post 齊全 → ok",
          quiet(validate_args, {"op": "post", "room": "r", "sender": "S", "body": "b", "persona": "p"})[0], True)
    ok, err = quiet(validate_args, {"op": "post", "room": "r", "persona": "p"})
    check("post 缺 sender/body → 擋", (ok, "sender" in err and "body" in err), (False, True))
    # S0：未知 op **放行**（本表是手抄鏡像，它不認識 ≠ 呼叫方錯）。缺 op 才是確定失敗 → 仍擋。
    check("未知 op → 放行（fail-open）", quiet(validate_args, {"op": "__no_such_op__"})[0], True)
    check("缺 op → 擋（確定失敗）", quiet(validate_args, {})[0], False)
    # S1：create_trpg_room 止血 + alias 優先序須對齊 C#（campaign > id > room；owner_agent > owner > gm）
    check("create_trpg_room 齊全 → ok",
          quiet(validate_args, {"op": "create_trpg_room", "campaign": "c1"})[0], True)
    check("create_trpg_room 缺 campaign → 擋",
          quiet(validate_args, {"op": "create_trpg_room"})[0], False)
    d = {"op": "create_trpg_room", "id": "byid", "room": "byroom"}
    quiet(validate_args, d)
    check("create_trpg_room alias 優先序 id > room", d.get("campaign"), "byid")
    d = {"op": "create_trpg_room", "campaign": "c1", "owner": "byowner", "gm": "bygm"}
    quiet(validate_args, d)
    check("create_trpg_room alias 優先序 owner > gm", d.get("owner_agent"), "byowner")
    d = {"op": "post", "room": "r", "sender_id": "S", "body": "b", "persona": "p"}
    ok, _ = quiet(validate_args, d)
    # 2026-07-31 四名歸一：canonical 從 sender 換成 agent。本測項的期望值當時沒跟著改，
    # 於是整個 selftest 從那天起就是紅的 —— 而一個永遠紅的測試等於沒有測試（大家學會忽略它）。
    check("alias sender_id→agent", (ok, d.get("agent"), "sender_id" in d), (True, "S", False))
    d = {"op": "task_create", "room": "q", "task_id": "T1", "title": "x"}
    ok, _ = quiet(validate_args, d)
    check("quest op 自動填 idempotency_key", ok and bool(d.get("idempotency_key")), True)
    d = {"op": "task_list", "room": "q"}
    quiet(validate_args, d)
    check("非 quest op 不填 idempotency_key", "idempotency_key" in d, False)

    # ④ 保留 tag meta（T06.3 — Cmd_Tavern server 端為權威，本表是鏡像）
    check("task-assign 缺欄 → 擋",
          quiet(validate_reserved_tag_meta, "tag:task-assign;task_id:T1")[0], False)
    check("task-assign 齊全 → ok",
          quiet(validate_reserved_tag_meta,
                "tag:task-assign;task_id:T1;task_body:b;assigned_by:me;requires_ack:true")[0], True)
    check("task-ack action 非法 → 擋",
          quiet(validate_reserved_tag_meta, "tag:task-ack;task_id:T1;action:bogus")[0], False)
    check("task-ack 合法 → ok",
          quiet(validate_reserved_tag_meta, "tag:task-ack;task_id:T1;action:accept")[0], True)
    check("非保留 tag → 放行", quiet(validate_reserved_tag_meta, "tag:chat;category:chat")[0], True)
    check("空 meta → 放行", quiet(validate_reserved_tag_meta, "")[0], True)

    # ⑤ persona 反查：顯式值不可被覆寫（多 lock 環境冒名是實證過的事故形態）
    d = {"op": "post", "persona": "__explicit__"}
    quiet(autofill_persona_from_lock, d)
    check("已帶 persona → 不覆寫", d["persona"], "__explicit__")

    # ⑥ 前提監視器：注入是否真的生效。三個依賴任一為 None 都代表 configure() 沒跑或被改壞，
    #    而後果是「靜默走到錯的目錄 / 反查永遠失敗」這種不會自己舉手的問題 —— 故顯式測。
    check("configure 注入 QUEUE_DIR", QUEUE_DIR is not None, True)
    check("configure 注入 TAVERN_DIR", TAVERN_DIR is not None, True)
    check("configure 注入 env-marker 偵測器", callable(DETECT_ENV_MARKER), True)

    # ⑦ lazy 前提監視器 —— configure() **不可以**順手載入 schema。
    #    若有人把載入搬回 configure，所有非 Tavern 子命令（list / catalog / recompile…）
    #    會重新開始付「走遍 repo ＋ 讀 52 檔算 SHA-256」的成本，而**那不會有人喊痛**
    #    （不報錯，只是每筆慢 0.9 秒）。所以顯式測「configure 完成當下仍未載入」。
    #    取樣點在本函式開頭（configure 之後、任何 validate_args 之前），見 `_loaded_right_after_configure`。
    check("configure 後 schema 仍未載入（lazy 生效）", _loaded_right_after_configure, False)

    # ⑧ 產物載入 —— 確認 client 端真的在用 C# 生成的 schema，而不是靜默什麼都沒接上。
    #    接上但沒生效是最糟的形態（看起來一切正常）。
    _ensure_schema_loaded()
    _ensure_freshness_checked()
    print(f"  [schema] loaded={SCHEMA_STATUS.get('loaded')} stale={SCHEMA_STATUS.get('stale')} "
          f"reason={SCHEMA_STATUS.get('reason')}")
    check("schema 產物已載入", SCHEMA_STATUS.get("loaded"), True)
    check("schema 未過期", SCHEMA_STATUS.get("stale"), False)
    check("產物含 create_trpg_room（原本漂掉的那個）", "create_trpg_room" in TAVERN_OP_SCHEMA, True)
    check("產物帶出 type_aliases（第四處鏡像來源）", bool(TYPE_ALIASES_FROM_SCHEMA), True)
    check("產物帶出 source_files（集合定義的唯一來源）", bool(_SCHEMA_RAW.get("source_files")), True)
    # 跨語言雜湊契約：Python 依產物清單重算的值必須等於產物內 C# 寫的值，否則過期偵測永遠誤報
    try:
        _art = _SCHEMA_RAW.get("source_hash")
        _mine = compute_source_hash(QUEUE_DIR.parent, _SCHEMA_RAW.get("source_files") or [])
        check("跨語言 hash 契約一致（C# 寫的 == Python 算的）", _art == _mine, True)
    except Exception as _e:
        check("跨語言 hash 契約一致（C# 寫的 == Python 算的）", f"讀取失敗:{type(_e).__name__}", True)

    print(f"[selftest] {'ALL PASS' if not failures else 'FAILED: ' + ', '.join(failures)}")
    return 0 if not failures else 1


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    if "--selftest" in sys.argv:
        raise SystemExit(_selftest())     # 依賴注入在 _selftest 內自行處理（見該處雙模組陷阱註解）
    print("本模組是 run_cmd.py 的 Tavern 規則層，不直接當 CLI 用。\n"
          "  自測：python tavern_cmd.py --selftest")
