#!/usr/bin/env python3
"""
canvas.py — Shared Pixel Canvas MVP CLI（共用像素畫布）

職責：
  共用像素畫布 MVP — 一塊 2048×2048 的全社群共用像素畫布。
  1 token / 1 繪畫券 / 1 自由時間免費像素 = 1 像素。append-only
  事件日誌 + 增量 buffer 渲染 → canvas_latest.png。

跨專案 / 路徑（比照 awakening.py：code 在 UCL_Core，state 留主專案）：
  - 本檔（code）跨專案共用，置於 <UCL_Core>/Tools~/AgentCommands/canvas.py
  - state（Canvas/ 事件、券、筆記、宣稱區域）留主專案 AgentCommands/Canvas（相對 **repo root**，不相對 cwd —— TASK-0112）
  - 操作 SOP → ucl-canvas skill（跨專案）；完整設計 spec（含 EOV 經濟耦合）→
    主專案 docs/Plan/Plan_Shared_Pixel_Canvas.md
  - 一律以 CWD = 專案根 調用（同 awakening.py 慣例），相對路徑才解析到 per-project state

物理意義：
  - 內部畫布表示 = 2048×2048 的 1-byte index-map buffer（每格 0-255 = RGB332 調色盤 index）。
  - 空白底色 = index 255（RGB332 解碼 = 純白 #FFFFFF；canvas_latest.png 維持白底）。
  - 每次 place 寫一筆 immutable 事件檔（events/<date>/<HHMMSS>_<uuid6>.json），
    渲染是 read-only replay；同座標 last-write-wins（r/place 覆蓋語意）。
  - painted-mask：replay 時同步記錄「曾被畫過」的格子（不論顏色）。
    canvas_latest_t.png = RGBA 透明變體：沒畫過 → alpha 0；畫過（含故意畫白）→ 不透明。
    (Tim 2026-07-15 拍板 A 方案；index 255 身兼空白底色與可畫純白，故透明判定靠 mask 而非色值)
  - token 付款寫真實 Treasury debit；券 / 免費像素寫 0-amount audit entry。

三付款方式 + pay=auto 優先序：免費 → 券 → token。

ops（argparse subcommands）:
  place / view / pixel / stats / snapshot / voucher / freetime / note / claim

⚠ 鐵律：canvas_latest.png 與 _last_view.png 維持不透明白底（下游預覽相容）；
  透明輸出一律成對衍生、檔名加 `_t` 後綴（canvas_latest_t.png / _last_view_t.png）；
  所有 png 皆衍生 render（事實源永遠是 events）；非 ASCII 字串正常 UTF-8。

⚠ view 的透明變體是 3D stamp 的**輸入格式**（Tim 2026-08-14 拍板：2D→3D 一律先出預覽再轉繪）——
  故 view 必須印出 `non_transparent_pixels` 與 sha256，讓下游 `sculpt.py stampimg --expect-pixels`
  能把「人看過的那張圖」與「實際被貼的那份 bytes」對起來。數字不是資訊，是閘門。

測試：--root / --treasury-root 可指向 temp 目錄，不污染真實 state。
"""

from __future__ import annotations

import argparse
import contextlib
import datetime
import hashlib
import json
import os
import secrets
import sys
import time
import zlib
from pathlib import Path

# 區塊職責：Windows console UTF-8 fallback
# 物理意義：確保中文 / emoji 輸出不亂碼；對齊既有 *.py 工具慣例。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except (AttributeError, OSError):
    pass

# 區塊職責：Pillow 延遲匯入提示
# 物理意義：render PNG 需 Pillow；若未裝給清楚錯誤訊息而非 traceback。
try:
    from PIL import Image  # noqa: F401
    _HAS_PIL = True
except ImportError:
    _HAS_PIL = False


# ───────────────────────────── 常數 ─────────────────────────────

# 畫布解析度（spec §2：2048×2048）
CANVAS_W = 2048
CANVAS_H = 2048
# 空白底色 index（spec 核心需求：index 255 = RGB332 解碼純白）
BLANK_INDEX = 255
# 預設儲存根（相對 repo root；測試用 --root 覆蓋）
DEFAULT_CANVAS_ROOT = "AgentCommands/Canvas"
# 預設 Treasury 根（測試用 --treasury-root 覆蓋）
DEFAULT_TREASURY_ROOT = "AgentCommands/Treasury"
# ⚠ 自由時間 session 檔的直讀已於 2026-08-26 退場（Tim 拍板：python 不直讀 session，
#   一律問 C# UCL_SessionService）。「在不在自由時間」改走
#   `run_cmd run SessionStatus --arg scope=persona` 的機讀 values（見 cmd_freetime）。
#   舊常數 FREE_TIME_SESSIONS 與 active_free_session 已刪 —— 別把 session 路徑加回來。

# 區塊職責：agent → bank 解析改走 UCL_Core 代碼側 _lib/bank_resolver.py 單一 source-of-truth。
# 物理意義：先前本檔自維護一張 case-sensitive 硬寫 AGENT_TO_BANK，與 awakening.py 的 registry-based
#           resolver 漂移 — 'Zeta' (大寫) 在硬表只有小寫 key 'zeta'，miss 後 fallback 退到
#           claude-da-xiaojie，導致 Zeta 麾下 persona (summit) 放點誤扣 claude-code 家帳 (2026-06-04 bug)。
#           現統一走共用 resolver：source of truth = AwakenInit/_registry_meta.json 的 agent_banks，
#           case-insensitive，bank 由 agent 決定、無 persona override。
# 載入手法：canvas.py 無 sys.path 操作，裸 `_lib` 即本腳本 sibling 的 Tools~/AgentCommands/_lib，
#           但為與 awakening 一致且不受 cwd 影響，同樣用 importlib 依絕對檔案路徑顯式載入。
import importlib.util as _ilu  # 顯式檔案路徑載入共用 resolver

_HERE = Path(__file__).resolve().parent                  # 本腳本所在目錄 (Tools~/AgentCommands)
_BANK_RESOLVER_PATH = _HERE / "_lib" / "bank_resolver.py"  # 共用 resolver 絕對路徑
_br_spec = _ilu.spec_from_file_location("_ucl_bank_resolver", _BANK_RESOLVER_PATH)
_br_mod = _ilu.module_from_spec(_br_spec)
_br_spec.loader.exec_module(_br_mod)
resolve_bank_account = _br_mod.resolve_bank_account       # agent → Treasury bank account
load_registry_meta = _br_mod.load_registry_meta           # 輕量讀 _registry_meta.json
resolve_persona_to_agent = _br_mod.resolve_persona_to_agent  # persona → agent（fail-loud，不 silent-default）
PersonaResolutionError = _br_mod.PersonaResolutionError


# 區塊職責：--agent 未帶時由 persona 反推所屬 agent（persona→agent→bank 兩跳鏈的第一跳）。
# 物理意義：**話認 persona、錢認 agent**。舊版 --agent 預設寫死 "claude-code"，於是任何
#           persona 不顯式帶 --agent，帳就記到 claude-code 的 bank 上 —— 多租戶環境裡的
#           預設值就是裝填好的槍（血證 2026-08-13：summit 以自己身分放 10 點，回報寫
#           bank=claude-da-xiaojie＝basecamp 的帳戶；當次 100% 走免費額度才沒真的扣錯人）。
# 數值影響：只影響「錢記到誰頭上」；persona / 像素內容不受影響。反推走既有 SOT
#           （personas/<name>.json 的 agent 欄），**不另存第二張 persona→agent 表**。
# 失敗處置：fail-loud —— 反推不出來就炸，不回退到任何預設 agent（silent-default 是本 bug 本體）。
def resolve_agent_for_persona(persona: str) -> str:
    """讀 personas/*.json 的 agent 欄（awakening.load_registry 的 SOT），回傳所屬 agent。"""
    _awk_spec = _ilu.spec_from_file_location("_ucl_awakening_for_canvas", _HERE / "awakening.py")
    _awk_mod = _ilu.module_from_spec(_awk_spec)
    _awk_spec.loader.exec_module(_awk_mod)
    return resolve_persona_to_agent(_awk_mod.load_registry(), persona)

# 預設 persona registry meta 路徑（agent_banks source of truth）；測試用 --registry-meta 覆蓋。
# 物理意義：registry 住在 AgentCommands/AwakenInit/_registry_meta.json，與 treasury 同 AgentCommands 根。
DEFAULT_REGISTRY_META = "AgentCommands/AwakenInit/_registry_meta.json"


# ───────────────────────────── 時間工具 ─────────────────────────────

# 財務操作一律走 Cmd（C# server 端）—— 見 _lib/treasury_cmd.py 的四條理由
import sys as _sys, os as _os
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
from _lib.treasury_cmd import (treasury_debit, canvas_voucher_consume,  # noqa: E402
                               canvas_voucher_grant, treasury_balance)

def utcnow():
    """區塊職責：回 UTC datetime（單一時間源，避免多次呼叫漂移）"""
    return datetime.datetime.utcnow()


def iso_ms(dt: datetime.datetime) -> str:
    """區塊職責：datetime → ISO8601 毫秒 Z 格式（對齊 Treasury ledger ts 慣例）"""
    return dt.strftime("%Y-%m-%dT%H:%M:%S.") + f"{dt.microsecond // 1000:03d}Z"


def parse_iso(ts: str) -> datetime.datetime | None:
    """區塊職責：解析 ISO8601 ts（容忍尾端 Z / 無毫秒）→ naive UTC datetime"""
    if not ts:
        return None
    s = ts.strip().rstrip("Z")
    # 數值影響：嘗試多種格式，失敗回 None（呼叫端自行處理）
    for fmt in ("%Y-%m-%dT%H:%M:%S.%f", "%Y-%m-%dT%H:%M:%S"):
        try:
            return datetime.datetime.strptime(s, fmt)
        except ValueError:
            continue
    return None


# ───────────────────────── RGB332 調色盤 ─────────────────────────

def index_to_rgb(i: int) -> tuple[int, int, int]:
    """
    區塊職責：RGB332 palette index → (r,g,b) 解碼
    物理意義：r=3bit(高位), g=3bit, b=2bit(低位)；古早 8-bit 風 256 色盤。
    數值影響：r=((i>>5)&7)*255//7, g=((i>>2)&7)*255//7, b=(i&3)*255//3。
              index 255 = (7,7,3) → (255,255,255) 純白（空白底色）。
    """
    r = ((i >> 5) & 0x7) * 255 // 7   # 取高 3 bit → 0..7 → 0..255
    g = ((i >> 2) & 0x7) * 255 // 7   # 取中 3 bit → 0..7 → 0..255
    b = (i & 0x3) * 255 // 3          # 取低 2 bit → 0..3 → 0..255
    return (r, g, b)


def rgb_to_index(r: int, g: int, b: int) -> int:
    """
    區塊職責：(r,g,b) 量化到最近 RGB332 index
    物理意義：把任意 hex 色壓到 256 盤；用四捨五入分桶（round 到最近 level）。
    數值影響：rb = round(r/255*7), gb = round(g/255*7), bb = round(b/255*3)，
              再打包成 index = (rb<<5)|(gb<<2)|bb。
    """
    rb = round(r / 255 * 7)   # 0..7（3 bit）
    gb = round(g / 255 * 7)   # 0..7（3 bit）
    bb = round(b / 255 * 3)   # 0..3（2 bit）
    return (rb << 5) | (gb << 2) | bb


def build_palette() -> list[tuple[int, int, int]]:
    """區塊職責：建 256 筆 RGB332 LUT（寫進 _meta.json palette 欄）"""
    return [index_to_rgb(i) for i in range(256)]


def parse_color(color) -> int:
    """
    區塊職責：色彩輸入 → palette index
    物理意義：接受 (a) palette index 0-255（int 或數字字串）；(b) #RRGGBB hex（量化）。
    數值影響：回 0-255 的 index；非法輸入 raise ValueError 由呼叫端整批拒絕。
    """
    # 情況 1：已是 int → 視為 palette index
    if isinstance(color, int):
        if 0 <= color <= 255:
            return color
        raise ValueError(f"palette index 越界 (0-255): {color}")
    # 情況 2：字串
    s = str(color).strip()
    if s.startswith("#"):
        # #RRGGBB hex → 解 RGB → 量化到 RGB332
        hexpart = s[1:]
        if len(hexpart) != 6:
            raise ValueError(f"hex 色需 #RRGGBB 6 碼: {color}")
        try:
            r = int(hexpart[0:2], 16)
            g = int(hexpart[2:4], 16)
            b = int(hexpart[4:6], 16)
        except ValueError:
            raise ValueError(f"非法 hex 色: {color}")
        return rgb_to_index(r, g, b)
    # 純數字字串 → palette index
    try:
        idx = int(s)
    except ValueError:
        raise ValueError(f"無法解析色彩 (需 0-255 或 #RRGGBB): {color}")
    if 0 <= idx <= 255:
        return idx
    raise ValueError(f"palette index 越界 (0-255): {color}")


# ───────────────────────────── 路徑解析 ─────────────────────────────

class Paths:
    """區塊職責：集中所有 canvas / treasury 子路徑，吃 --root / --treasury-root"""

    def __init__(self, root: str, treasury_root: str,
                 registry_meta: str | None = None):
        # 區塊職責：相對路徑一律錨在 **repo root**（ucl_paths.repo_root 的 tier 鏈），不是 cwd。
        # 🩸 TASK-0112（2026-09-03，Tim 抓到的）：三個預設根原本是相對 cwd 的字串；basecamp 的 shell cwd
        #   停在 Assets/Plugins/UCL_Core 時跑 place ⇒ 工具在 UCL_Core 底下**長出一棵新的 AgentCommands 樹**，
        #   事件、快取、預覽全寫進去，放完回讀同一棵樹所以全綠；真畫布 history 0，而 ledger 真的扣了 10 token。
        #   「cwd 往上 walk」正是 ucl_paths 檔頭點名的 2026-06-16 路徑詐欺家族 —— 這裡是它沒被收掉的最後一格。
        # 數值影響：`--root`／`--treasury-root`／`--registry-meta` 給**絕對路徑**照舊逐字採用（測試隔離用）；
        #           給相對值 ⇒ 相對 repo root。repo_root 解析失敗會 raise（不猜一個看起來合理的根）。
        self.root = self._anchor(root)                      # canvas 根目錄
        self.treasury_root = self._anchor(treasury_root)    # treasury 根目錄
        # persona registry meta（agent_banks source of truth）；可配置供測試指向 temp。
        # 預設走 DEFAULT_REGISTRY_META（與 treasury 同處 AgentCommands 根下的 AwakenInit/）。
        self._registry_meta = self._anchor(registry_meta or DEFAULT_REGISTRY_META)

    @staticmethod
    def _anchor(p: str) -> Path:
        """絕對路徑原樣；相對路徑接在 repo root 後面（不是 cwd）。"""
        path = Path(p)
        if path.is_absolute():
            return path
        from _lib.ucl_paths import repo_root            # tier 鏈：pointer → env → 檔案位置 walk → gitlink 上溯
        return Path(repo_root()) / path

    @property
    def meta(self) -> Path:
        return self.root / "_meta.json"

    @property
    def events(self) -> Path:
        return self.root / "events"

    @property
    def vouchers(self) -> Path:
        return self.root / "vouchers"

    @property
    def notes(self) -> Path:
        return self.root / "notes"

    @property
    def claims(self) -> Path:
        return self.root / "claims.json"

    @property
    def snapshots(self) -> Path:
        return self.root / "snapshots"

    @property
    def latest_png(self) -> Path:
        return self.root / "canvas_latest.png"

    @property
    def latest_t_png(self) -> Path:
        return self.root / "canvas_latest_t.png"

    @property
    def last_view_png(self) -> Path:
        return self.root / "_last_view.png"

    @property
    def cache_meta(self) -> Path:
        # 增量快取的 meta（指紋 / 檔案清單 / 水位）；衍生物，不入 git
        return self.root / "_canvas_cache.json"

    @property
    def cache_bin(self) -> Path:
        # 增量快取的 buffer+mask 二進位（各 CANVAS_W*CANVAS_H bytes）；衍生物，不入 git
        return self.root / "_canvas_cache.bin"

    @property
    def last_view_t_png(self) -> Path:
        # view 的 RGBA 透明變體（未繪製 → alpha 0）；3D stamp 的輸入格式
        return self.root / "_last_view_t.png"

    @property
    def ledger(self) -> Path:
        return self.treasury_root / "ledger"

    @property
    def registry_meta(self) -> Path:
        # persona registry metadata 檔（含 agent_banks）— 共用 resolver 的 source of truth
        return self._registry_meta


# ───────────────────────── 並發鎖（防 TOCTOU double-spend）─────────────────────────

# 鎖等待參數：最長等 PAYMENT_LOCK_TIMEOUT_SEC，每次 retry sleep PAYMENT_LOCK_POLL_SEC
PAYMENT_LOCK_TIMEOUT_SEC = 10.0
PAYMENT_LOCK_POLL_SEC = 0.02


@contextlib.contextmanager
def payment_lock(P: "Paths", bank: str, persona: str):
    """
    區塊職責：跨進程互斥鎖，保護「讀餘額 → 寫 debit/扣券」critical section
    物理意義：spec §6 debit atomic + §3.1「place N 點前先查餘額 ≥ N」要求 read-modify-write
              不可被並發交錯。兩個 place 進程若同時讀到相同餘額並各自寫 debit → overspend。
              用 os.O_CREAT|os.O_EXCL 建唯一 lockfile（同 bank + 同 persona 共一把鎖，
              因 token 扣 bank、券扣 persona，兩者都要保護），確保臨界區序列化。
    數值影響：取不到鎖（含 stale 殘留）最長 spin-wait PAYMENT_LOCK_TIMEOUT_SEC 後 raise
              RuntimeError；正常路徑 finally 一定刪 lockfile，避免死鎖。
    """
    # 鎖檔名綁 bank + persona（兩種付款資源都涵蓋），放 canvas root 下 _locks/
    lock_dir = P.root / "_locks"
    lock_dir.mkdir(parents=True, exist_ok=True)
    safe = f"{bank}__{persona}".replace("/", "_").replace("\\", "_")
    lock_file = lock_dir / f"place_{safe}.lock"
    deadline = time.monotonic() + PAYMENT_LOCK_TIMEOUT_SEC
    fd = None
    while True:
        try:
            # O_EXCL：檔已存在則 raise FileExistsError → 鎖被別人持有，retry
            fd = os.open(str(lock_file), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            break
        except FileExistsError:
            if time.monotonic() >= deadline:
                # 逾時：可能對方 crash 留 stale lock；保守 raise 而非強奪（避免再 double-spend）
                raise RuntimeError(
                    f"payment_lock 逾時（{PAYMENT_LOCK_TIMEOUT_SEC}s）：{lock_file} 被占用")
            time.sleep(PAYMENT_LOCK_POLL_SEC)
    try:
        # 寫 pid 方便 debug stale lock 來源
        os.write(fd, str(os.getpid()).encode("utf-8"))
        os.close(fd)
        fd = None
        yield
    finally:
        if fd is not None:
            with contextlib.suppress(OSError):
                os.close(fd)
        with contextlib.suppress(OSError):
            os.unlink(str(lock_file))


# ───────────────────────── JSON 讀寫小工具 ─────────────────────────

def read_json(path: Path, default=None):
    """區塊職責：安全讀 JSON，缺檔 / 壞檔回 default"""
    if not path.is_file():
        return default
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return default


def write_json(path: Path, data):
    """區塊職責：寫 JSON（UTF-8、indent=2、ensure_ascii=False 保中文原樣）"""
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")


# ───────────────────────────── meta ─────────────────────────────

def ensure_meta(P: Paths) -> dict:
    """
    區塊職責：載入 / 初始化 _meta.json（解析度 / palette / 快照游標）
    物理意義：首次跑自動建檔，palette 寫入 256 筆 RGB332 LUT。
    """
    meta = read_json(P.meta)
    if meta is None:
        meta = {
            "resolution": {"w": CANVAS_W, "h": CANVAS_H},  # 畫布解析度
            "palette_mode": "RGB332",                       # 調色盤模式
            "blank_index": BLANK_INDEX,                     # 空白底色 index
            "palette": build_palette(),                     # 256 筆 RGB LUT
            "last_snapshot_ts": None,                       # 快照游標 ts
            "last_event_uuid": None,                        # 快照游標 event uuid
            "created_at": iso_ms(utcnow()),
        }
        write_json(P.meta, meta)
    return meta


# ───────────────────────── 事件日誌 replay ─────────────────────────

def _event_sort_key(ev: dict):
    """
    區塊職責：算事件 replay 排序 key（source-of-truth = 事件內 ts 欄位，毫秒精度）
    物理意義：spec §6「渲染時同座標按 ts 排序，最後一筆勝」— 不靠檔名字典序（只有秒級
              精度，同秒下 tiebreak 變隨機 uuid 序），改用事件 JSON 的真實 ts。
    數值影響：主 key = parse_iso(ts)（缺 / 壞 ts → datetime.min 排最前，視為最舊）；
              次 key = uuid（同毫秒 deterministic tiebreak，避免不穩定排序）。
    """
    dt = parse_iso(ev.get("ts"))
    # 壞 ts 退到最舊（datetime.min），確保有效 ts 的事件永遠勝過無 ts 的
    primary = dt if dt is not None else datetime.datetime.min
    secondary = ev.get("uuid") or ""
    return (primary, secondary)


def iter_events(P: Paths):
    """
    區塊職責：yield 所有事件 dict（依事件內 ts 欄位毫秒精度穩定排序 = 真實時間序）
    物理意義：spec §6 last-write-wins 以 ts 為準。檔名只有秒級精度，同秒兩筆 place
              的字典序 tiebreak 是隨機 uuid 序而非真實時間序，故不靠檔名排序。
    數值影響：先收集全部事件，再按 _event_sort_key 排序後 yield；replay 順序 = ts 序。
    """
    if not P.events.is_dir():
        return
    events = []
    for date_dir in sorted(P.events.iterdir()):
        if not date_dir.is_dir():
            continue
        for ev_file in sorted(date_dir.iterdir()):
            if ev_file.suffix != ".json":
                continue
            ev = read_json(ev_file)
            if ev is not None:
                events.append(ev)
    # 依事件 ts（毫秒精度）穩定排序，徹底以 ts 為真實 replay 序
    events.sort(key=_event_sort_key)
    for ev in events:
        yield ev


# ───────────────────────── 增量快取（buffer / mask 落盤）─────────────────────────
# 區塊職責：把 replay 出來的 buf+mask 存成本地快取，下次能免 replay 或只 replay 新事件。
# 物理意義：事實源永遠是 events/（append-only）；快取是**可隨時丟棄的衍生物**，
#          任何一點不確定都退回全 replay —— 快取只能省時間，不准改變結果。
# ⚠ 為什麼不照抄 3D 的 last_event_file 增量（Tim 2026-08-14 指出的 git 同步情境）：
#   事件檔會**從 git 同步進來**，而同步進來的檔可以是「時間比較舊、名字排在前面」的。
#   靠「找到上次那個檔名之後才算新的」會把它當成已處理 ⇒ **靜默漏掉**（3D 端目前是這個形狀，
#   屬 gura 的引擎，已回報未擅改）。所以本快取用兩道判準，都以「不確定就重建」為預設：
#     ① 檔案清單指紋：全部事件檔的 (相對路徑, 大小) 排序後 hash。
#        新增／刪除／改大小 → 指紋變 ⇒ 不能直接用快取。
#     ② 增量只在「舊檔全數原樣仍在，且新檔的 ts 都 ≥ 快取已套用的最大 ts」時才成立。
#        git 拉進一筆昨天的事件 → 它的 ts 比較舊 ⇒ **全重建**，不做增量。
#        （last-write-wins 是依 ts 定的，把舊事件套在新事件之後會塗出錯的顏色。）
#   已知邊界：內容改了但**大小不變**的事件檔偵測不到 —— append-only 日誌不該發生
#   （那是改歷史），真要防得逐檔 hash，成本從 stat 升到讀全檔，現階段不換。
CACHE_SCHEMA = 1


def _scan_event_manifest(P: Paths):
    """
    區塊職責：掃出所有事件檔的 (相對路徑, 位元組大小)，排序後回傳。
    物理意義：只 stat 不解析 JSON —— 這是「要不要重建」的判斷成本下限。
    """
    out = []
    if not P.events.is_dir():
        return out
    for date_dir in sorted(P.events.iterdir()):
        if not date_dir.is_dir():
            continue
        for ev_file in sorted(date_dir.iterdir()):
            if ev_file.suffix != ".json":
                continue
            try:
                out.append((f"{date_dir.name}/{ev_file.name}", ev_file.stat().st_size))
            except OSError:
                continue
    out.sort()
    return out


def _manifest_hash(entries) -> str:
    h = hashlib.sha256()
    for rel, size in entries:
        h.update(f"{rel}:{size}\n".encode("utf-8"))
    return h.hexdigest()


def _read_events_at(P: Paths, rels):
    """區塊職責：只讀指定的事件檔並依 ts 排序（增量路徑用，不掃全目錄）。"""
    evs = []
    for rel in rels:
        ev = read_json(P.events / rel)
        if ev is not None:
            evs.append(ev)
    evs.sort(key=_event_sort_key)
    return evs


def _apply_events(buf, mask, events):
    """區塊職責：把事件逐像素塗進 buf/mask（與全 replay 同一份塗法，不另寫第二套）。"""
    for ev in events:
        for px in ev.get("pixels", []):
            x, y = px.get("x"), px.get("y")
            if x is None or y is None or not (0 <= x < CANVAS_W) or not (0 <= y < CANVAS_H):
                continue
            try:
                idx = parse_color(px.get("color"))
            except ValueError:
                continue
            pos = y * CANVAS_W + x
            buf[pos] = idx
            mask[pos] = 1


def load_cache(P: Paths):
    """
    區塊職責：讀快取 meta＋binary，回 (meta, buf, mask)；任何不完整一律回 None（退全 replay）。
    數值影響：binary 佈局＝前 CANVAS_W*CANVAS_H bytes 是 buf，後同長度是 mask。
    """
    try:
        if not P.cache_meta.is_file() or not P.cache_bin.is_file():
            return None
        meta = json.loads(P.cache_meta.read_text(encoding="utf-8"))
        if meta.get("schema") != CACHE_SCHEMA:
            return None                      # schema 換版 → 舊快取直接作廢，不猜相容
        n = CANVAS_W * CANVAS_H
        blob = zlib.decompress(P.cache_bin.read_bytes())
        if len(blob) != n * 2:
            return None                      # 長度不對＝壞檔，不硬讀
        return meta, bytearray(blob[:n]), bytearray(blob[n:])
    except (OSError, ValueError, zlib.error, json.JSONDecodeError):
        return None                          # 壞快取不是錯誤路徑，是「重建」路徑


def save_cache(P: Paths, buf, mask, entries, max_ts: str):
    """區塊職責：落快取（先寫 .tmp 再 replace —— 半寫的快取比沒有快取更糟）。"""
    try:
        P.root.mkdir(parents=True, exist_ok=True)
        tmp_bin = P.cache_bin.with_suffix(".bin.tmp")
        # 數值影響：畫布 99.97% 是空白，8.4MB 原始資料 zlib level 1 壓成約 39KB。
        # 用 level 1 不用 6：6 只多省 29KB 卻讓解壓從 10ms 變 16ms —— 快取的成本在**讀**不在存。
        tmp_bin.write_bytes(zlib.compress(bytes(buf) + bytes(mask), 1))
        os.replace(tmp_bin, P.cache_bin)
        meta = {
            "schema": CACHE_SCHEMA,
            "manifest_hash": _manifest_hash(entries),
            "event_count": len(entries),
            "max_ts": max_ts,
            "files": [list(e) for e in entries],
            "built_at": iso_ms(utcnow()),
        }
        tmp_meta = P.cache_meta.with_suffix(".json.tmp")
        tmp_meta.write_text(json.dumps(meta, ensure_ascii=False), encoding="utf-8")
        os.replace(tmp_meta, P.cache_meta)
    except OSError:
        pass                                 # 快取寫不進去不該擋住主流程（結果不受影響）


def _max_ts_of(events) -> str:
    """
    區塊職責：取事件集合中「最晚」的 ts，回原始字串（空集合回空字串）。
    ⚠ 比大小走 parse_iso 不走字串比對 —— ts 只要有一筆缺毫秒或帶不同時區寫法，
      字串序就跟時間序不一致，而那種錯會安靜地把增量判定變成擲骰子。
    """
    best_dt, best_s = None, ""
    for ev in events:
        t = ev.get("ts") or ""
        dt = parse_iso(t)
        if dt is not None and (best_dt is None or dt > best_dt):
            best_dt, best_s = dt, t
    return best_s


def build_buffer(P: Paths, with_mask: bool = False, use_cache: bool = True):
    """
    區塊職責：取得完整 index-map buffer（可選同步產出 painted-mask）—— 走快取，必要時 replay。
    物理意義：底色填 BLANK_INDEX，逐事件逐像素塗（同座標 last-write-wins）。
              mask 記「曾被畫過」的格子（不論顏色）— index 255 身兼空白底色與可畫純白，
              透明渲染的判定必須靠 mask 而非色值，否則故意畫的白會消失。
    數值影響：with_mask=False 回 buf（2048*2048 bytearray, 1 byte palette index）；
              with_mask=True 回 (buf, mask)（mask 同尺寸 bytearray, 0=沒畫過 1=畫過）。
              **回傳值與有沒有走快取無關** —— 快取只省時間，不准改變結果。
    快取三路（判準見上方 CACHE_SCHEMA 區塊；use_cache=False 可強制全 replay 做對拍驗證）：
      ① 指紋相同        → 直接用快取，零 replay
      ② 舊檔原樣＋新檔 ts 都不早於快取 → 只 replay 新檔，疊加在快取上
      ③ 其餘（含 git 拉進舊事件、檔案消失、快取壞掉）→ 全 replay 重建
    """
    entries = _scan_event_manifest(P)
    if use_cache:
        cached = load_cache(P)
        if cached is not None:
            meta, c_buf, c_mask = cached
            if meta.get("manifest_hash") == _manifest_hash(entries):
                return (c_buf, c_mask) if with_mask else c_buf          # 路 ①
            old = {tuple(e) for e in meta.get("files", [])}
            cur = set(entries)
            if old and old <= cur:                                       # 舊檔全數原樣仍在
                new_rels = [rel for rel, _ in sorted(cur - old)]
                new_evs = _read_events_at(P, new_rels)
                base_dt = parse_iso(meta.get("max_ts") or "")
                # 新事件若有任何一筆早於快取水位 → 疊加會塗錯（last-write-wins 依 ts）⇒ 退全重建
                ok = base_dt is None or all(
                    (parse_iso(ev.get("ts") or "") or datetime.datetime.min) >= base_dt
                    for ev in new_evs)
                if ok:                                                   # 路 ②
                    _apply_events(c_buf, c_mask, new_evs)
                    save_cache(P, c_buf, c_mask, entries,
                               _max_ts_of(new_evs) or meta.get("max_ts", ""))
                    return (c_buf, c_mask) if with_mask else c_buf

    # 路 ③：全 replay（也是 use_cache=False 的唯一路徑）
    buf = bytearray([BLANK_INDEX]) * (CANVAS_W * CANVAS_H)  # 初始全空白底色
    mask = bytearray(CANVAS_W * CANVAS_H)                   # 初始全 0 = 沒畫過
    all_evs = list(iter_events(P))
    _apply_events(buf, mask, all_evs)
    if use_cache:
        save_cache(P, buf, mask, entries, _max_ts_of(all_evs))
    return (buf, mask) if with_mask else buf


def buffer_to_image(buf: bytearray):
    """
    區塊職責：index-map buffer → PIL RGB Image
    物理意義：每格 index 經 RGB332 LUT 解碼成 RGB 三元組；禁透明（RGB 非 RGBA）。
    """
    if not _HAS_PIL:
        raise RuntimeError("需要 Pillow 才能 render PNG：pip install Pillow")
    palette = [index_to_rgb(i) for i in range(256)]   # 256 筆 RGB LUT
    img = Image.new("RGB", (CANVAS_W, CANVAS_H))      # RGB 模式 = 禁透明背景
    # 數值影響：用 putdata 一次塞所有像素（O(W*H)），比逐點 putpixel 快很多
    img.putdata([palette[b] for b in buf])
    return img


def buffer_to_image_rgba(buf: bytearray, mask: bytearray):
    """
    區塊職責：index-map buffer + painted-mask → PIL RGBA Image（透明變體）
    物理意義：沒畫過(mask=0) → alpha 0；畫過(mask=1) → 不透明 palette 色（含故意畫白）。
    """
    if not _HAS_PIL:
        raise RuntimeError("需要 Pillow 才能 render PNG：pip install Pillow")
    palette = [index_to_rgb(i) for i in range(256)]
    img = Image.new("RGBA", (CANVAS_W, CANVAS_H))
    img.putdata([(*palette[b], 255) if m else (0, 0, 0, 0) for b, m in zip(buf, mask)])
    return img


def render_latest(P: Paths, buf: bytearray, mask: bytearray = None):
    """
    區塊職責：buffer → 覆蓋 canvas_latest.png（每次 place 後呼叫）
    物理意義：mask 有給時同步輸出 canvas_latest_t.png 透明變體（A 方案, Tim 2026-07-15）。
    """
    img = buffer_to_image(buf)
    P.latest_png.parent.mkdir(parents=True, exist_ok=True)
    img.save(str(P.latest_png))
    if mask is not None:
        buffer_to_image_rgba(buf, mask).save(str(P.latest_t_png))


# ───────────────────────── Treasury 整合 ─────────────────────────

def ledger_balance(P: Paths, account_id: str, currency: str = "tavern_token"):
    """
    區塊職責：查指定 bank 的 token 餘額 —— **走 Cmd（C# 端），本檔不自己算**。
    物理意義：與同檔 write_ledger_entry 被移除（2026-08-04）是同一條規矩的兩半 ——
             寫入端當時搬去 Cmd 了，**讀取端這支被留在原地**，繼續逐檔重放整本帳。
    數值影響：回 int，或 **None ＝ 查不到**（Editor 未開 / Cmd 失敗）。
             ⚠ 呼叫端不可把 None 當 0：那會讓「查不到」長得像「沒錢」，
             而付款判斷看到 0 會拒付並回一句與事實無關的錯誤訊息。
    🩸 為什麼搬（2026-08-16 basecamp 量測）：舊版每次呼叫逐檔 json.load 全本帳（14,985 檔），
       暖快取 0.6s、冷快取近兩分鐘 —— 而放一個像素就要查一次。
       同族的四份複製品裡，morning 那份把 step=brief 拖到 112s（08-13 更撞 timeout 被 kill）。
    """
    del P  # 路徑不再需要 —— 帳本由 C# 端讀（保留參數以免動到所有呼叫點的形狀）
    return treasury_balance(account_id, currency)


# 註（2026-08-04）：原本這裡有 write_ledger_entry —— **filesystem 直寫 Treasury ledger**。
# 已移除：財務操作一律走 Cmd（C# server 端），python 不直寫（Tim 定調）。
#   直寫的實際後果（不是原則潔癖）：
#     · C# 餘額快取自 2026-08-01 起初掃後不再列舉磁碟 → 直寫的 entry 在下次
#       InvalidateBalanceCache / domain reload 之前**後台看不到**，且無錯誤訊息。
#     · 繞過 idempotency 判重；`sig_*` 由寫入端自填（本檔曾填 manual_filesystem_write_canvas），
#       所以「有 sig_* 就是 C# 寫的」這個判準是假的 —— 2026-08-04 盤查時據此得出顛倒的結論。
#     · balance_before/after 留 null，逼出另一支 python 去**改寫既有 entry**（append-only 被就地修改）。
#   現在改為：**按總像素一次扣款**（Tim 拍板）——
#   逐像素明細本來就在畫布自己的 event log（`pixels` + `pay_breakdown`），Treasury 只需要知道總額。


# ───────────────────────── voucher ledger ─────────────────────────

def voucher_path(P: Paths, persona: str) -> Path:
    return P.vouchers / f"{persona}.json"


def load_voucher(P: Paths, persona: str) -> dict:
    """區塊職責：載入 / 初始化 per-persona 繪畫券 ledger"""
    data = read_json(voucher_path(P, persona))
    if data is None:
        data = {"persona": persona, "balance": 0, "history": []}
    return data


# 區塊職責: 券餘額 —— **從 batches 推導**，不讀 `balance` 欄（Tim 2026-08-18 拍板方案乙）。
# 物理意義: 券 2026-08-18 改批次制（一次 grant = 一批，各自帶 expires_at）。
#          `balance` 欄**已不再被寫入** —— 留一個「看起來是餘額、實際是舊快照」的數字在檔裡，
#          過期額度就會被讀成還能花，而那不會報錯。
#          ⇒ 三個問題分成三支：**永久 / 未過期限時 / 可花總額**。一支回答不了三個問題。
# ⚠ 對側契約: C# 是 `UCL_CanvasVoucherLedger.GetPermanent/GetExpiring/GetSpendable`（唯一寫入 owner）。
#          兩端要一起改 —— 只改一端會讓這邊算出 0，而「有券算成沒券」跟「真的沒券」輸出一模一樣。
# 數值影響: legacy 相容 —— 舊檔只有純量 `balance`、沒有 `batches` ⇒ 視為「一批永久券」，
#          讀值與舊制逐值相同（不需要遷移腳本）。
def _voucher_batches(P: Paths, persona: str) -> list[dict]:
    data = load_voucher(P, persona)
    batches = data.get("batches")
    if isinstance(batches, list):
        return batches
    legacy = int(data.get("balance", 0) or 0)
    if legacy > 0:
        return [{"uuid": "legacy", "amount": legacy, "remain": legacy,
                 "granted_at": "", "expires_at": "", "source": "legacy_balance"}]
    return []


def _batch_spendable(b: dict, now: datetime.datetime) -> bool:
    """這批此刻能不能花。expires_at 空 = 永久；**解析失敗視為永久**（不奪走既有權益）。

    🩸 2026-08-18 端到端抓到：本檔的 `utcnow()` 回的是 **offset-naive**（`datetime.utcnow()`），
      而 `expires_at` 解析出來是 aware ⇒ `now <= t` 直接 TypeError。
      單元路徑（voucher --sub balance）用的是 aware 預設值，所以**只有真的放點才會炸**。
      ⇒ 這裡把兩邊一律正規化成 aware UTC，不假設呼叫端給的是哪一種。
    """
    if int(b.get("remain", 0) or 0) <= 0:
        return False
    if now.tzinfo is None:
        now = now.replace(tzinfo=datetime.timezone.utc)
    exp = (b.get("expires_at") or "").strip()
    if not exp:
        return True
    try:
        t = datetime.datetime.fromisoformat(exp.replace("Z", "+00:00"))
    except ValueError:
        print(f"⚠ 券 batch {b.get('uuid')} 的 expires_at 解析不出來（{exp!r}）— 視為永久券",
              file=sys.stderr)
        return True
    if t.tzinfo is None:
        t = t.replace(tzinfo=datetime.timezone.utc)
    return now <= t


def voucher_permanent(P: Paths, persona: str) -> int:
    """**永久券**（expires_at 空）。查「存了多少券」用這支。"""
    return sum(int(b.get("remain", 0) or 0) for b in _voucher_batches(P, persona)
               if not (b.get("expires_at") or "").strip() and int(b.get("remain", 0) or 0) > 0)


def voucher_expiring(P: Paths, persona: str, now: datetime.datetime | None = None) -> int:
    """**未過期的限時券**。"""
    now = now or datetime.datetime.now(datetime.timezone.utc)
    return sum(int(b.get("remain", 0) or 0) for b in _voucher_batches(P, persona)
               if (b.get("expires_at") or "").strip() and _batch_spendable(b, now))


def voucher_balance(P: Paths, persona: str, now: datetime.datetime | None = None) -> int:
    """**可花總額**（未過期限時 ＋ 永久）—— 規劃付款用這支。名字沿用是為了不動既有呼叫端。"""
    now = now or datetime.datetime.now(datetime.timezone.utc)
    return sum(int(b.get("remain", 0) or 0) for b in _voucher_batches(P, persona)
               if _batch_spendable(b, now))


# ───────────────────────── freetime state ─────────────────────────

# 每場自由時間發放的限時券張數 —— **對側是 C# `Cmd_FreeTime.FREE_PIXELS_PER_SESSION`**。
# ⚠ 兩端要一起改；只改一端會讓「已用 x/10」的 x 算錯而不報錯（分母是常數、分子是推導）。
FREE_PIXELS_PER_SESSION = 10


# ⚠ 免費像素的額度檔（`Canvas/freetime/<persona>.json`）2026-08-18 廢除 ——
#   `freetime_path` / `load_freetime` / `free_pixels_available` 三支一併移除。
#   免費像素現在**就是限時繪圖券**（發放端 Cmd_FreeTime step=start、到期自動作廢），
#   可用量走 `voucher_expiring()`，扣款走 Cmd（C# ledger 是唯一寫入 owner）。
#   ⇒ 不再有「券系統之外的第二套錢」要各自讀檔、各自算可用量、各自作廢。


def query_in_free_time(persona: str) -> bool | None:
    """
    區塊職責：問 C#「此刻在不在自由時間」—— 唯一合法通道是 Cmd（Tim 2026-08-26 拍板：
              python 不直讀 session，session 資訊完全由 UCL_SessionService 管理）。
    物理意義：跑 `run_cmd run SessionStatus --arg scope=persona`，解析 stdout 的機讀行
              `🔢 in_free_time = 0|1`（TASK-0052 開的出口；同值也落 result 檔 values 欄）。
    數值影響：回 True/False；**查不到回 None 不回 False** —— Editor 未開／逾時時
              「不知道」與「不在」必須不同形（拿不知道冒充不在，使用者會照著去開場）。
    """
    import re
    import subprocess
    try:
        r = subprocess.run(
            [sys.executable, str(_HERE / "run_cmd.py"), "--persona", persona,
             "run", "SessionStatus", "--arg", "scope=persona", "--arg", f"persona={persona}"],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=150)
        m = re.search(r"in_free_time\s*=\s*([01])", (r.stdout or "") + (r.stderr or ""))
        if m is None:
            return None
        return m.group(1) == "1"
    except Exception:
        return None


# ───────────────────────────── place ─────────────────────────────

def collect_pixels(args) -> list[dict]:
    """
    區塊職責：把 --pixels JSON 或 --x/--y/--color 統一成 pixel list
    數值影響：回 [{x,y,color}, ...]；解析失敗 raise ValueError 整批拒絕。
    """
    if args.pixels:
        try:
            arr = json.loads(args.pixels)
        except json.JSONDecodeError as e:
            raise ValueError(f"--pixels JSON 解析失敗: {e}")
        if not isinstance(arr, list) or not arr:
            raise ValueError("--pixels 需為非空陣列")
        return arr
    # 單點模式
    if args.x is None or args.y is None or args.color is None:
        raise ValueError("需提供 --pixels 或 (--x --y --color)")
    return [{"x": args.x, "y": args.y, "color": args.color}]


def validate_pixels(pixels: list[dict]) -> list[dict]:
    """
    區塊職責：驗座標 [0,2047] + 色彩可解析，整批驗（任一非法整批拒絕）
    數值影響：回正規化後的 [{x,y,color(index)}, ...]。
    """
    out = []
    for px in pixels:
        x = px.get("x")
        y = px.get("y")
        if not isinstance(x, int) or not isinstance(y, int):
            raise ValueError(f"座標需整數: {px}")
        if not (0 <= x <= 2047) or not (0 <= y <= 2047):
            raise ValueError(f"座標越界 [0,2047]: ({x},{y})")
        idx = parse_color(px.get("color"))   # 可能 raise ValueError
        out.append({"x": x, "y": y, "color": idx})
    return out


def plan_payment(P: Paths, persona: str, bank: str, n: int, pay: str,
                 now: datetime.datetime) -> dict:
    """
    區塊職責：規劃 N 個像素的付款分配（atomic 預驗）
    物理意義：pay=auto 優先序 免費→券→token；單一方式則只用該方式。
    數值影響：回 {"free":k0,"voucher":k1,"token":k2}，三者合計 = n；
              不足整批拒絕 → raise ValueError。
    """
    # 計算各資源可用量
    # 免費像素 2026-08-13 改制回歸：舊制（冷卻制）2026-08-01 廢止的死因是 session 來源檔
    # 沒有寫入端；新制寫入端 = Cmd_FreeTime step=start（每場 10 顆，per-session 清零），
    # 本工具讀 session 檔 + 額度檔判可用量 —— 有寫入端了，回歸。
    # 2026-08-18：**免費像素就是限時繪圖券**（Cmd_FreeTime step=start 發、到期自動作廢）。
    # 舊制那份 `Canvas/freetime/<P>.json` 額度檔是券系統之外的第二套錢 —— 已廢除。
    # ⇒ 這裡兩個數字都來自同一個 ledger，且**刻意分開讀**：
    #   限時券要先花（會過期），永久券墊後。分開讀才報得出誠實的 pay_breakdown。
    free_avail = voucher_expiring(P, persona, now)
    voucher_avail = voucher_permanent(P, persona)
    token_avail = ledger_balance(P, bank)
    if token_avail is None:
        # 查不到餘額就**不要猜**。當 0 會拒付並回一句「餘額不足」——
        # 那是拿「不知道」冒充「沒錢」，而使用者會照著那句去補錢。
        raise ValueError(
            f"查不到 {bank} 的餘額（餘額查詢走 Cmd → C# 端，需 Editor 開著）。"
            "本次不扣款、不放點 —— 這是「不知道」不是「沒錢」，別照著這句去加值。")

    use_free = use_voucher = use_token = 0

    if pay == "freetime":
        # 只用免費像素
        if n > free_avail:
            raise ValueError(
                f"限時券不足：需 {n}，{persona} 未過期的限時券 {free_avail} — "
                "自由時間的免費像素現在是**限時繪圖券**（Cmd_FreeTime step=start 每場發 10 張，"
                "到期即作廢）。不在自由時間、或本場的券已過期時這個數字是 0。")
        use_free = n
    elif pay == "voucher":
        # 只用券
        if n > voucher_avail:
            raise ValueError(f"永久繪畫券不足：需 {n}，{persona} 永久券 {voucher_avail}"
                             f"（未過期限時券另有 {free_avail} 張，用 --pay auto 會先花它們）")
        use_voucher = n
    elif pay == "token":
        # 只用 token
        if n > token_avail:
            raise ValueError(f"token 不足：需 {n}，{bank} 餘額 {token_avail}")
        use_token = n
    else:  # auto：**限時券 → 永久券 → token**（2026-08-18 收斂；限時的會過期所以先花）
        remaining = n
        use_free = min(free_avail, remaining)
        remaining -= use_free
        use_voucher = min(voucher_avail, remaining)
        remaining -= use_voucher
        use_token = min(token_avail, remaining)
        remaining -= use_token
        if remaining > 0:
            raise ValueError(
                f"資源合計不足：需 {n}，限時券 {free_avail} + 永久券 {voucher_avail} "
                f"+ token {token_avail} = {free_avail + voucher_avail + token_avail}")

    return {"free": use_free, "voucher": use_voucher, "token": use_token}


def cmd_place(args):
    P = args._paths
    ensure_meta(P)
    persona = args.persona
    # agent 決定「錢記到誰頭上」：未顯式帶就由 persona 反推（見 resolve_agent_for_persona 的區塊註解）。
    agent = args.agent
    if not agent:
        try:
            agent = resolve_agent_for_persona(persona)
        except Exception as e:
            print(f"❌ place 拒絕: 無法由 persona '{persona}' 反推所屬 agent（{e}）—— "
                  f"請顯式帶 --agent（拒絕 silent-default 到別人的帳戶）", file=sys.stderr)
            return 2
    # agent → bank：走共用 resolver（與 awakening.py 同一 source of truth）。
    # registry meta 缺檔 → load_registry_meta 回空 dict → resolver 退命名慣例 fallback，不 fatal。
    reg = load_registry_meta(P.registry_meta)
    bank = resolve_bank_account(reg, agent)
    now = utcnow()

    # 步驟 1：收集 + 驗證像素（越界 / 非法色 整批拒絕）
    try:
        raw = collect_pixels(args)
        pixels = validate_pixels(raw)
    except ValueError as e:
        print(f"❌ place 拒絕: {e}", file=sys.stderr)
        sys.exit(2)

    n = len(pixels)

    # 步驟 2-5 critical section：取付款鎖序列化「讀餘額 → 寫 debit/扣券/扣免費」，
    #   防兩個並發 place 各自讀到相同餘額後雙重扣款 (TOCTOU double-spend)。
    #   鎖綁 bank + persona（token 扣 bank、券扣 persona，兩種資源都要保護）。
    event_uuid = secrets.token_hex(3)
    ledger_refs = []
    try:
        lock_cm = payment_lock(P, bank, persona)
        lock_cm.__enter__()
    except RuntimeError as e:
        print(f"❌ place 拒絕（鎖逾時）: {e}", file=sys.stderr)
        sys.exit(4)

    try:
        # 步驟 2：規劃付款（atomic 預驗，不足整批拒絕）— 在鎖內讀餘額確保一致
        try:
            plan = plan_payment(P, persona, bank, n, args.pay, now)
        except ValueError as e:
            print(f"❌ place 拒絕（付款）: {e}", file=sys.stderr)
            # 不在此手動釋放鎖：sys.exit 觸發 SystemExit，由外層 finally 統一釋放
            sys.exit(3)

        # 步驟 3：**批次結算**（Tim 2026-08-04：按總像素一次扣款，不再逐像素寫 ledger）
        # 物理意義：token 扣款與消券都走 Cmd（C# server 端）；免費像素不動 Treasury。
        #          逐像素明細留在畫布自己的 event log，Treasury 只記「這次花了多少」。
        # 邊界：**Cmd 失敗一律整批拒絕**（先收錢再畫，不能反過來）——
        #      畫了卻沒扣到錢等於免費像素，那比拒絕嚴重得多。
        voucher_consumed = plan["voucher"]
        free_consumed = plan["free"]

        if plan["token"] > 0:
            ok, msg = treasury_debit(
                account=bank, amount=plan["token"],
                source_kind="canvas_pixel", source_ref=event_uuid,
                description=f"canvas {plan['token']} px by {persona} (event {event_uuid})",
                caller=bank)   # 帳戶本人花自己的錢（帳戶隔離鐵律，見 treasury_cmd.py）
            if not ok:
                print(f"❌ place 拒絕（Treasury 扣款失敗，未畫任何像素）：{msg}", file=sys.stderr)
                sys.exit(3)
            ledger_refs.append(f"treasury:{event_uuid}")

        # ⚠ 限時券與永久券**都走同一支 consume** —— ledger 內部先花快過期的，
        #   所以這裡不需要（也不該）自己排順序。分兩筆呼叫只為了讓 source 分得出來：
        #   帳面上「這幾張是自由時間的限時券」與「這幾張是存量券」因此可追。
        if free_consumed > 0:
            ok, msg = canvas_voucher_consume(
                persona=persona, amount=free_consumed, source_ref=event_uuid,
                description=f"canvas {free_consumed} px (freetime 限時券) by {persona}")
            if not ok:
                print(f"❌ place 拒絕（限時券扣款失敗，未畫任何像素）：{msg}", file=sys.stderr)
                sys.exit(3)
            ledger_refs.append(f"voucher-expiring:{event_uuid}")

        if voucher_consumed > 0:
            ok, msg = canvas_voucher_consume(
                persona=persona, amount=voucher_consumed, source_ref=event_uuid,
                description=f"canvas {voucher_consumed} px by {persona}")
            if not ok:
                print(f"❌ place 拒絕（消券失敗，未畫任何像素）：{msg}", file=sys.stderr)
                sys.exit(3)
            ledger_refs.append(f"voucher:{event_uuid}")

        # 步驟 4：（已移除）本地 voucher ledger 寫入
        # 消券已在步驟 3 走 Cmd_CanvasVoucher op=consume —— C# 是券的 canonical owner。
        # 這裡若保留本地讀-改-寫會**雙重扣券**（Cmd 扣一次、本地再扣一次）。
        # 步驟 5（2026-08-18 移除）：原本這裡把 `Canvas/freetime/<P>.json` 的 `used` 遞增 ——
        #   那份額度檔是**券系統之外的第二套錢**，而免費像素現在就是限時繪圖券，
        #   扣款已經在步驟 3 由 ledger 做完（且它記了自己的 history）。
        #   ⇒ 這裡不再有第二次寫入。**同一筆消費只有一個寫入端。**
    finally:
        # 臨界區結束（含正常 / 例外 / sys.exit 路徑）一律釋放付款鎖
        lock_cm.__exit__(None, None, None)

    # 步驟 6：寫事件檔（append-only，schema 對齊 spec §2.2）
    pay_breakdown = {"freetime": plan["free"], "voucher": plan["voucher"], "token": plan["token"]}
    event = {
        "ts": iso_ms(now),
        "uuid": event_uuid,
        "persona": persona,
        "agent": agent,
        "account_id": bank,
        "pixels": [{"x": px["x"], "y": px["y"], "color": px["color"]} for px in pixels],
        "cost": n,
        "pay_breakdown": pay_breakdown,
        "ledger_refs": ledger_refs,
    }
    date_dir = now.strftime("%Y-%m-%d")
    hhmmss = now.strftime("%H%M%S")
    # 檔名加毫秒對齊 ledger 慣例（{hhmmss}_{msec}_{uuid}），把同秒檔名碰撞機率降到
    # 同毫秒 + 同 token_hex(3) 才會撞（~1/16.7M），避免 silent overwrite 丟事件。
    ev_msec = f"{now.microsecond // 1000:03d}"
    ev_path = P.events / date_dir / f"{hhmmss}_{ev_msec}_{event_uuid}.json"
    write_json(ev_path, event)

    # 步驟 7：增量更新 buffer + 重渲 canvas_latest.png (+ canvas_latest_t.png 透明變體)
    #   2026-08-14 起 build_buffer 走增量快取：剛落的這筆是「最新 ts 的新檔」⇒ 走路② 只 replay 它，
    #   疊在快取上。正確性不因走快取而改變（cache --sub verify 逐格對拍過），省的是全 replay。
    buf, mask = build_buffer(P, with_mask=True)
    render_latest(P, buf, mask)

    # 步驟 8：回報結果
    print(f"# 🎨 placed {n} pixel(s)")
    print(f"  event       : {ev_path}")
    print(f"  persona     : {persona} (agent={agent}, bank={bank})")
    print(f"  pay_breakdown: freetime={plan['free']} voucher={plan['voucher']} token={plan['token']}")
    print(f"  voucher bal : {voucher_balance(P, persona)}")
    _bal = ledger_balance(P, bank)
    print(f"  token bal   : {_bal if _bal is not None else '查不到（需 Editor 開著；不是 0）'}")
    print(f"  ledger_refs : {ledger_refs}")
    print(f"  canvas_latest: {P.latest_png}")
    print(f"  canvas_latest_t: {P.latest_t_png} (透明變體)")

    # 步驟 9：自動分享預覽（Tim 2026-08-20 拍板；--no-share 可關）
    if not getattr(args, "no_share", False):
        _share_place_preview(P, persona, pixels, n, event_uuid)


# ───────────────────────────── 自動分享 ─────────────────────────────
# 區塊職責：落點後把預覽圖發進酒館（帶 refs）→ mirror daemon 的附件分支自動上 Discord。
# 物理意義／設計取捨（每一條都是真坑，不是風格偏好）：
#   · **fire-and-forget（run_cmd submit，不 wait）** —— canvas.py 常被 FreeTimeActivity
#     op=step 從 Editor 端代跑：place 若「等」一個 Tavern Cmd 跑完，就是 Editor 等 python、
#     python 等 Editor 的自鎖。submit 只入列不等執行結果。
#   · **--lane share** —— 同 persona 的主 lane 可能正被「代跑中的那個 Cmd」佔著，
#     submit 前置的 ensure_idle 會空等；share 子 lane 有自己的 queue 與 running-lock。
#   · **預覽存 previews/ 獨立檔名** —— canvas_latest / _last_view 是共用畫布檔，下一次操作
#     就被蓋掉，refs 指共用檔等於指一張會變的圖。previews/ 是臨時渲染，不入版控。
#   · **任何失敗只警告不失敗** —— 錢已扣、像素已落，分享失敗不能讓 place 看起來失敗。
# 數值影響：多一張 ≤512px 的 png、一筆 share lane 的 queue 條目；不動畫布資料與帳。
def _share_place_preview(P: Paths, persona: str, pixels: list, n: int, event_uuid: str):
    import subprocess
    try:
        from PIL import Image as _Img
        xs = [px["x"] for px in pixels]
        ys = [px["y"] for px in pixels]
        margin = 8
        x1 = max(0, min(xs) - margin)
        y1 = max(0, min(ys) - margin)
        x2 = min(CANVAS_W, max(xs) + margin + 1)
        y2 = min(CANVAS_H, max(ys) + margin + 1)
        buf, _mask = build_buffer(P, with_mask=True)   # place 剛更新過快取，這裡是快取路徑
        img = buffer_to_image(buf).crop((x1, y1, x2, y2))
        # 放大到最長邊 ~512（NEAREST 保像素硬邊）—— 不放大的話幾顆像素在 Discord 上是一粒沙
        scale = max(1, min(16, 512 // max(img.width, img.height)))
        if scale > 1:
            img = img.resize((img.width * scale, img.height * scale), resample=_Img.NEAREST)
        prev_dir = P.root / "previews"
        prev_dir.mkdir(parents=True, exist_ok=True)
        prev_path = prev_dir / f"share_{utcnow().strftime('%Y%m%dT%H%M%S')}_{event_uuid}.png"
        img.save(str(prev_path))

        # refs 慣例＝repo 相對路徑；repo root 走共用 resolver（路徑該被傳遞，不該被推導）
        from _lib.ucl_paths import repo_root
        rel = prev_path.resolve().relative_to(Path(repo_root()).resolve()).as_posix()

        body = (f"🎨 {persona} 在畫布 ({min(xs)},{min(ys)})–({max(xs)},{max(ys)}) "
                f"放了 {n} 顆像素（預覽 ×{scale}）")
        argv = [sys.executable, str(_HERE / "run_cmd.py"),
                "--persona", persona, "--lane", "share",
                "submit", "Tavern", "--ack-timeout", "10",
                "--arg", "op=post", "--arg", "room=tavern",
                "--arg", f"persona={persona}",
                "--arg", f"body={body}",
                "--arg", f"refs={rel}",
                "--arg", 'meta={"tag":"canvas-share"}']
        # argv list 直接 exec，不經 shell —— body 含括號/dash 都不會被解讀
        r = subprocess.run(argv, capture_output=True, encoding="utf-8", errors="replace", timeout=30)
        if r.returncode != 0:
            print(f"⚠ 分享未入列（放點不受影響）：{((r.stdout or '') + (r.stderr or ''))[-300:]}", file=sys.stderr)
            return
        print(f"  share       : 預覽已入列酒館（{rel}）—— Editor 接手後發文，Discord 由 mirror 附掛")
    except Exception as e:
        print(f"⚠ 分享失敗（放點不受影響）：{e}", file=sys.stderr)


# ───────────────────────────── cache ─────────────────────────────
# 區塊職責：讓「快取現在走哪一路、什麼時候會刷新」看得見 —— 不用讀 code 也不用猜。
# 物理意義：status 只 stat 不 replay（判斷成本下限）；verify 是**唯一有資格說「快取是對的」**的路徑
#          —— 它把快取結果與全 replay 逐格對拍。印 ✓ 不算數，對拍過才算。
def cmd_cache(args):
    P = args._paths
    ensure_meta(P)
    entries = _scan_event_manifest(P)
    cur_hash = _manifest_hash(entries)

    if args.sub == "rebuild":
        t0 = time.time()
        build_buffer(P, with_mask=True, use_cache=False)   # 全 replay（不吃舊快取）
        _, mask = build_buffer(P, with_mask=True)          # 再跑一次落快取
        print("# ♻ 快取重建")
        print(f"  事件檔   : {len(entries)}")
        print(f"  已繪格數 : {sum(1 for m in mask if m)}")
        print(f"  耗時     : {time.time() - t0:.2f}s")
        return 0

    if args.sub == "verify":
        t0 = time.time()
        c_buf, c_mask = build_buffer(P, with_mask=True, use_cache=True)
        t_cache = time.time() - t0
        t0 = time.time()
        r_buf, r_mask = build_buffer(P, with_mask=True, use_cache=False)
        t_replay = time.time() - t0
        diff_buf = sum(1 for a, b in zip(c_buf, r_buf) if a != b)
        diff_mask = sum(1 for a, b in zip(c_mask, r_mask) if a != b)
        print("# 🔍 快取對拍（快取 vs 全 replay）")
        print(f"  buf  差異格數: {diff_buf}")
        print(f"  mask 差異格數: {diff_mask}")
        print(f"  快取路徑耗時 : {t_cache:.3f}s ／ 全 replay: {t_replay:.3f}s")
        if diff_buf or diff_mask:
            print("❌ 快取與事實源不一致 —— 跑 `cache --sub rebuild`，並回報這訊息（這是 bug 不是雜訊）")
            return 1
        print("✅ 逐格一致")
        return 0

    # status —— 只 stat，不 replay
    cached = load_cache(P)
    print("# 📦 canvas 增量快取狀態")
    print(f"  事件檔       : {len(entries)}")
    print(f"  當前指紋     : {cur_hash[:16]}…")
    if cached is None:
        print("  快取         : 不存在 / 不可用 → 下次 build_buffer 走**全 replay** 並重建")
        return 0
    meta, _, _ = cached
    print(f"  快取建於     : {meta.get('built_at', '?')}")
    print(f"  快取事件數   : {meta.get('event_count', '?')}")
    print(f"  快取水位 ts  : {meta.get('max_ts', '?')}")
    if meta.get("manifest_hash") == cur_hash:
        print("  下次判定     : ✅ 路① 指紋相同 —— 直接用，零 replay")
        return 0
    old = {tuple(e) for e in meta.get("files", [])}
    cur = set(entries)
    if old and old <= cur:
        new_rels = [rel for rel, _ in sorted(cur - old)]
        new_evs = _read_events_at(P, new_rels)
        base_dt = parse_iso(meta.get("max_ts") or "")
        stale = [ev.get("ts") for ev in new_evs
                 if base_dt is not None
                 and (parse_iso(ev.get("ts") or "") or datetime.datetime.min) < base_dt]
        if stale:
            print(f"  下次判定     : ♻ 路③ 全重建 —— 新進 {len(new_rels)} 檔中有 {len(stale)} 筆 ts 早於水位")
            print(f"                （典型成因：git 同步拉進別人較早的事件；例：{stale[0]}）")
        else:
            print(f"  下次判定     : ⚡ 路② 增量 —— 只 replay 新增的 {len(new_rels)} 個事件檔")
    else:
        print(f"  下次判定     : ♻ 路③ 全重建 —— 有 {len(old - cur)} 個舊事件檔消失或大小改變")
    return 0


# ───────────────────────────── view ─────────────────────────────
# 區塊職責：渲染當前畫布（可裁區、可放大）→ _last_view.png ＋ RGBA 透明變體 _last_view_t.png。
# 物理意義：透明變體是「2D→3D 轉繪」的**輸入格式**（Tim 2026-08-14 拍板：先出預覽再轉繪）——
#          未繪製 → alpha 0（3D 端不放 voxel）、畫過（含故意畫白）→ 不透明。
#          未繪製與純白在 RGB 上同值，只有 alpha 分得出來，所以 3D 端不可吃 _last_view.png。
# 數值影響：`non_transparent_pixels` 是**裁切與縮放之後**數出來的 —— 它描述檔案，不描述意圖；
#          scale>1 時它會等比放大（那是呼叫端顯式選的放大貼，不是隱藏行為）。
#          sha256 讓下游能證明「我吃的就是你看的那張」，配 stampimg --expect-pixels 成閘門。
def cmd_view(args):
    P = args._paths
    ensure_meta(P)
    buf, mask = build_buffer(P, with_mask=True)
    img = buffer_to_image(buf)
    img_t = buffer_to_image_rgba(buf, mask)

    # 區域裁切（spec：region=x,y,w,h）
    if args.region:
        try:
            x, y, w, h = (int(v) for v in args.region.split(","))
        except ValueError:
            print(f"❌ region 格式需 x,y,w,h: {args.region}", file=sys.stderr)
            sys.exit(2)
        if w <= 0 or h <= 0 or not (0 <= x <= 2047) or not (0 <= y <= 2047):
            print(f"❌ region 越界 / 非法: {args.region}", file=sys.stderr)
            sys.exit(2)
        # 裁到畫布邊界內
        x2 = min(x + w, CANVAS_W)
        y2 = min(y + h, CANVAS_H)
        img = img.crop((x, y, x2, y2))
        img_t = img_t.crop((x, y, x2, y2))

    # 縮放（spec：scale 放大倍率，NEAREST 保像素硬邊）
    # ⚠ 透明變體必須同用 NEAREST：任何插值都會生出 0<alpha<255 的半透明邊，
    #   而 3D 端「畫過/沒畫過」是二值判定 —— 插值等於製造出畫布上不存在的像素。
    if args.scale and args.scale != 1:
        from PIL import Image as _Img
        img = img.resize((img.width * args.scale, img.height * args.scale),
                         resample=_Img.NEAREST)
        img_t = img_t.resize((img_t.width * args.scale, img_t.height * args.scale),
                             resample=_Img.NEAREST)

    P.last_view_png.parent.mkdir(parents=True, exist_ok=True)
    img.save(str(P.last_view_png))
    img_t.save(str(P.last_view_t_png))

    # 數出實際落檔的非透明像素（＝3D 端會放 voxel 的格數，thickness=1 時 1:1）
    # RGBA tobytes 每像素 4 bytes，alpha 在第 4 個 → [3::4] 就是整張的 alpha 通道
    # （不用 getdata：Pillow 14 要移除它，且逐 tuple 建物件在 2048² 上明顯慢）
    opaque = sum(1 for a in img_t.tobytes()[3::4] if a != 0)
    sha = hashlib.sha256(P.last_view_t_png.read_bytes()).hexdigest()

    print(f"# 🖼 view rendered")
    print(f"  size  : {img.width}x{img.height}")
    print(f"  path  : {P.last_view_png}")
    print(f"  path_t: {P.last_view_t_png} (RGBA 透明變體 — 3D stamp 的輸入)")
    print(f"  non_transparent_pixels: {opaque} / {img_t.width * img_t.height}")
    print(f"  sha256_t: {sha}")
    print(f"  → 貼進 3D：python sculpt.py stampimg --png \"{P.last_view_t_png}\" "
          f"--expect-pixels {opaque} --at <x,y,z> --facing z+ --persona <P>")


# ───────────────────────────── pixel ─────────────────────────────

def cmd_pixel(args):
    P = args._paths
    ensure_meta(P)
    x, y = args.x, args.y
    if x is None or y is None or not (0 <= x <= 2047) or not (0 <= y <= 2047):
        print(f"❌ pixel 座標越界 [0,2047]: ({x},{y})", file=sys.stderr)
        sys.exit(2)

    # replay 該座標的歷史（誰何時放）
    history = []
    cur_idx = BLANK_INDEX
    for ev in iter_events(P):
        for px in ev.get("pixels", []):
            if px.get("x") == x and px.get("y") == y:
                try:
                    idx = parse_color(px.get("color"))
                except ValueError:
                    continue
                cur_idx = idx
                history.append({
                    "ts": ev.get("ts"), "persona": ev.get("persona"),
                    "agent": ev.get("agent"), "color_index": idx,
                    "rgb": index_to_rgb(idx),
                })

    r, g, b = index_to_rgb(cur_idx)
    print(f"# 🔍 pixel ({x},{y})")
    if cur_idx == BLANK_INDEX and not history:
        print(f"  current: 空白 (index {BLANK_INDEX} = #FFFFFF)")
    else:
        print(f"  current: index {cur_idx} = #{r:02X}{g:02X}{b:02X}")
    print(f"  history ({len(history)} 筆):")
    for h in history:
        rr, gg, bb = h["rgb"]
        print(f"    {h['ts']}  {h['persona']}/{h['agent']}  "
              f"index {h['color_index']} = #{rr:02X}{gg:02X}{bb:02X}")


# ───────────────────────────── stats ─────────────────────────────

def cmd_stats(args):
    P = args._paths
    ensure_meta(P)
    total_events = 0
    total_pixels = 0
    per_persona = {}
    contributors = set()
    occupied = {}   # (x,y) → 最後色（算填充率，去重）

    for ev in iter_events(P):
        total_events += 1
        persona = ev.get("persona", "?")
        contributors.add(persona)
        for px in ev.get("pixels", []):
            total_pixels += 1
            per_persona[persona] = per_persona.get(persona, 0) + 1
            occupied[(px.get("x"), px.get("y"))] = px.get("color")

    filled = len(occupied)
    fill_rate = filled / (CANVAS_W * CANVAS_H) * 100

    print(f"# 📊 canvas stats")
    print(f"  總事件   : {total_events}")
    print(f"  總放點   : {total_pixels} (含覆蓋)")
    print(f"  唯一座標 : {filled} (去重後實際填充)")
    print(f"  填充率   : {fill_rate:.6f}% ({filled}/{CANVAS_W * CANVAS_H})")
    print(f"  貢獻者   : {len(contributors)} 位")
    print(f"  各 persona 放點數:")
    for p, c in sorted(per_persona.items(), key=lambda kv: -kv[1]):
        print(f"    {p:<20} {c}")


# ───────────────────────────── snapshot ─────────────────────────────

def cmd_snapshot(args):
    P = args._paths
    meta = ensure_meta(P)
    buf, mask = build_buffer(P, with_mask=True)
    img = buffer_to_image(buf)
    now = utcnow()
    ts_tag = now.strftime("%Y%m%dT%H%M%SZ")
    snap_path = P.snapshots / f"canvas_{ts_tag}.png"
    snap_path.parent.mkdir(parents=True, exist_ok=True)
    img.save(str(snap_path))

    # 推進快照游標（記最後一筆 event uuid）
    last_uuid = None
    for ev in iter_events(P):
        last_uuid = ev.get("uuid")
    meta["last_snapshot_ts"] = iso_ms(now)
    meta["last_event_uuid"] = last_uuid
    write_json(P.meta, meta)

    # 同步重渲 latest（含透明變體）
    render_latest(P, buf, mask)
    print(f"# 📸 snapshot")
    print(f"  path          : {snap_path}")
    print(f"  last_event_uuid: {last_uuid}")
    print(f"  canvas_latest : refreshed")


# ───────────────────────────── voucher ─────────────────────────────

def cmd_voucher(args):
    P = args._paths
    ensure_meta(P)
    persona = args.persona
    sub = args.sub
    now = utcnow()

    if sub == "balance":
        bal = voucher_balance(P, persona)
        print(f"# 🎟 {persona} 繪畫券餘額: {bal}")
    elif sub == "grant":
        if args.amount is None or args.amount <= 0:
            print("❌ grant 需 --amount > 0", file=sys.stderr)
            sys.exit(2)
        # 2026-08-17：改走 Cmd（C# 是券的 canonical owner）——
        # 這裡原本直寫 json，是 consume 已經走 Cmd 之後**唯一還在直寫的券路徑**。
        # 直寫的代價見 _lib/treasury_cmd.py 檔頭四條；實際爆掉的形狀是兩份帳本分歧。
        ok, msg = canvas_voucher_grant(
            persona=persona, amount=args.amount,
            source=getattr(args, "source", None) or "manual_grant",
            source_ref=getattr(args, "ref", None) or "",
        )
        if not ok:
            print(f"❌ 發券失敗（未寫入任何東西）: {msg}", file=sys.stderr)
            sys.exit(1)
        print(f"# 🎟 granted {args.amount} 繪畫券 → {persona}")
        print(f"  new balance: {voucher_balance(P, persona)}")   # 讀回來，不用記憶體值假裝已生效
    elif sub == "history":
        v = load_voucher(P, persona)
        print(f"# 🎟 {persona} 繪畫券歷史 (balance={v.get('balance', 0)}):")
        for h in v.get("history", []):
            print(f"  {h.get('ts')}  {h.get('type'):<8} {h.get('amount'):>4}  "
                  f"{h.get('source', '')} {h.get('ref', '')}")
    else:
        print(f"❌ unknown voucher sub: {sub}", file=sys.stderr)
        sys.exit(2)


# ───────────────────────────── freetime ─────────────────────────────

def cmd_freetime(args):
    # 2026-08-26 改制：「在不在自由時間」不再直讀 session 檔，改問 C#（query_in_free_time）。
    # 券數仍走 ledger（那本來就是 C# 為唯一寫入 owner 的帳）。
    P = args._paths
    ensure_meta(P)
    persona = args.persona
    now = utcnow()

    if args.sub != "status":
        print(f"❌ unknown freetime sub: {args.sub}", file=sys.stderr)
        sys.exit(2)

    in_session = query_in_free_time(persona)
    # 2026-08-18：免費像素 = **限時繪圖券**。額度檔已廢除 ⇒ 讀 ledger。
    # 「本場發了幾張」不從檔案推（那份檔沒了），而是用常數 10 對照剩餘量算已用 ——
    # 精確的「本場那一批」由 C# 端 GetExpiringByRef(session_id) 回答（回傳檔會印）。
    expiring_count = voucher_expiring(P, persona, now)
    granted = FREE_PIXELS_PER_SESSION
    used = max(0, granted - expiring_count)

    print(f"# 🎨 {persona} 自由時間免費像素狀態（**限時繪圖券**：每場 {granted} 張，到期作廢）")
    if in_session is None:
        # 「不知道」與「不在」不同形 —— Editor 未開時判定通道不存在，但券數（純檔案）照報。
        print("  自由時間: ⚠ 無法判定（SessionStatus 查詢失敗 —— Editor 沒開？）")
        print("  這是「不知道」不是「不在」；剩餘時間等細節看該查詢的回傳檔。")
    elif in_session:
        print("  自由時間: ✅ active（剩餘時間與場次細節看 SessionStatus 回傳檔，或 step=next 的回報）")
    else:
        print("  自由時間: ❌ 不在 active session（免費像素額度為 0）")
        print("  進場: run_cmd.py run FreeTime --arg step=start --arg persona=<me> --arg until=<HH:mm>")
    if in_session:
        print(f"  免費像素: {expiring_count} 顆可用（本場已用 {used}/{granted}）")
    else:
        print(f"  （券帳讀數: 未過期限時券 {expiring_count} 顆；上場已用 {used}/{granted} — 不跨場）")


# ───────────────────────────── note ─────────────────────────────

def note_path(P: Paths, persona: str) -> Path:
    return P.notes / f"{persona}.json"


def load_notes(P: Paths, persona: str) -> dict:
    data = read_json(note_path(P, persona))
    if data is None:
        data = {"persona": persona, "notes": []}
    return data


def parse_size(size: str) -> tuple[int, int]:
    """區塊職責：'WxH' → (w,h)，算 est_cost = w*h"""
    w, h = size.lower().split("x")
    return int(w), int(h)


def cmd_note(args):
    P = args._paths
    ensure_meta(P)
    persona = args.persona
    sub = args.sub
    now = utcnow()
    data = load_notes(P, persona)

    if sub == "add":
        note_id = secrets.token_hex(3)
        est_cost = None
        region = None
        if args.size:
            try:
                w, h = parse_size(args.size)
                est_cost = w * h   # 預算試算：W×H 個像素
            except ValueError:
                print(f"❌ --size 格式需 WxH: {args.size}", file=sys.stderr)
                sys.exit(2)
        if args.region:
            try:
                rx, ry, rw, rh = (int(v) for v in args.region.split(","))
                region = {"x": rx, "y": ry, "w": rw, "h": rh}
            except ValueError:
                print(f"❌ --region 格式需 x,y,w,h: {args.region}", file=sys.stderr)
                sys.exit(2)
        note = {
            "id": note_id, "title": args.title or "", "plan": args.plan or "",
            "target_region": region, "expected_size": args.size, "est_cost": est_cost,
            "status": "planning", "created_at": iso_ms(now), "updated_at": iso_ms(now),
        }
        data["notes"].append(note)
        write_json(note_path(P, persona), data)
        print(f"# 📝 note added [{note_id}] {args.title or ''}")
        if est_cost is not None:
            print(f"  est_cost: {est_cost} 像素（= {est_cost} token / 券）")
    elif sub == "list":
        print(f"# 📝 {persona} 繪圖筆記 ({len(data['notes'])} 筆):")
        for nt in data["notes"]:
            print(f"  [{nt['id']}] ({nt['status']}) {nt.get('title', '')}"
                  f"  est_cost={nt.get('est_cost')}")
            if nt.get("plan"):
                print(f"        plan: {nt['plan']}")
    elif sub in ("update", "done"):
        if not args.id:
            print(f"❌ {sub} 需 --id", file=sys.stderr)
            sys.exit(2)
        found = None
        for nt in data["notes"]:
            if nt["id"] == args.id:
                found = nt
                break
        if found is None:
            print(f"❌ 找不到 note id: {args.id}", file=sys.stderr)
            sys.exit(2)
        if sub == "done":
            found["status"] = "done"
        else:
            if args.title is not None:
                found["title"] = args.title
            if args.plan is not None:
                found["plan"] = args.plan
            if args.status:
                found["status"] = args.status
        found["updated_at"] = iso_ms(now)
        write_json(note_path(P, persona), data)
        print(f"# 📝 note [{args.id}] {sub} → status={found['status']}")
    else:
        print(f"❌ unknown note sub: {sub}", file=sys.stderr)
        sys.exit(2)


# ───────────────────────────── claim ─────────────────────────────

def load_claims(P: Paths) -> dict:
    data = read_json(P.claims)
    if data is None:
        data = {"claims": []}
    return data


def cmd_claim(args):
    P = args._paths
    ensure_meta(P)
    sub = args.sub
    now = utcnow()
    data = load_claims(P)

    if sub == "add":
        if not args.persona:
            print("❌ claim add 需 --persona（宣稱者）", file=sys.stderr)
            sys.exit(2)
        if not args.region:
            print("❌ claim add 需 --region x,y,w,h", file=sys.stderr)
            sys.exit(2)
        try:
            rx, ry, rw, rh = (int(v) for v in args.region.split(","))
        except ValueError:
            print(f"❌ --region 格式需 x,y,w,h: {args.region}", file=sys.stderr)
            sys.exit(2)
        claim_id = secrets.token_hex(3)
        claim = {
            "id": claim_id, "persona": args.persona,
            "region": {"x": rx, "y": ry, "w": rw, "h": rh},
            "title": args.title or "",
            "status": "active", "created_at": iso_ms(now), "updated_at": iso_ms(now),
        }
        data["claims"].append(claim)
        write_json(P.claims, data)
        print(f"# 📌 claim added [{claim_id}] {args.title or ''} @ ({rx},{ry},{rw},{rh}) by {args.persona}")
    elif sub == "list":
        actives = [c for c in data["claims"] if c.get("status") == "active"]
        print(f"# 📌 宣稱區域 (active {len(actives)} / 共 {len(data['claims'])}):")
        for c in data["claims"]:
            r = c.get("region", {})
            print(f"  [{c['id']}] ({c['status']}) {c.get('persona')}: "
                  f"{c.get('title', '')} @ ({r.get('x')},{r.get('y')},{r.get('w')},{r.get('h')})")
    elif sub in ("release", "done"):
        if not args.id:
            print(f"❌ {sub} 需 --id", file=sys.stderr)
            sys.exit(2)
        found = None
        for c in data["claims"]:
            if c["id"] == args.id:
                found = c
                break
        if found is None:
            print(f"❌ 找不到 claim id: {args.id}", file=sys.stderr)
            sys.exit(2)
        found["status"] = "released" if sub == "release" else "done"
        found["updated_at"] = iso_ms(now)
        write_json(P.claims, data)
        print(f"# 📌 claim [{args.id}] → {found['status']}")
    else:
        print(f"❌ unknown claim sub: {sub}", file=sys.stderr)
        sys.exit(2)


# ───────────────────────────── argparse ─────────────────────────────

def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Shared Pixel Canvas MVP CLI（共用像素畫布）",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    # 全域：root / treasury-root 可配置（測試指向 temp）
    parser.add_argument("--root", default=DEFAULT_CANVAS_ROOT,
                        help=f"canvas 儲存根（預設 {DEFAULT_CANVAS_ROOT}）")
    parser.add_argument("--treasury-root", default=DEFAULT_TREASURY_ROOT,
                        help=f"treasury 根（預設 {DEFAULT_TREASURY_ROOT}）")
    parser.add_argument("--registry-meta", default=None,
                        help=f"persona registry meta 檔（agent_banks source of truth；測試隔離用；預設 {DEFAULT_REGISTRY_META}）")

    sub = parser.add_subparsers(dest="op", required=True)

    # place
    p = sub.add_parser("place", help="放點（單/批量），扣免費/券/token + 重渲 latest")
    p.add_argument("--x", type=int, default=None)
    p.add_argument("--y", type=int, default=None)
    p.add_argument("--color", default=None, help="palette index 0-255 或 #RRGGBB")
    p.add_argument("--pixels", default=None, help='批量 JSON: [{"x","y","color"}]')
    p.add_argument("--persona", required=True)
    p.add_argument("--agent", default=None,
                   help="決定扣哪個 bank（省略＝由 --persona 反推所屬 agent；反推失敗會拒絕，不 default）")
    p.add_argument("--pay", choices=["auto", "freetime", "voucher", "token"], default="auto")
    p.add_argument("--no-share", action="store_true",
                   help="不自動發預覽圖進酒館（預設會發：落點 bounding box 裁圖 → 酒館帶 refs → Discord 由 mirror 附掛）")
    p.set_defaults(func=cmd_place)

    # view
    p = sub.add_parser("cache", help="增量快取狀態 / 重建 / 對拍驗證")
    p.add_argument("--sub", choices=["status", "rebuild", "verify"], default="status",
                   help="status=看目前是哪一路；rebuild=丟棄重建；verify=快取 vs 全 replay 逐格對拍")
    p.set_defaults(func=cmd_cache)

    p = sub.add_parser("view", help="渲染當前畫布 → _last_view.png")
    p.add_argument("--region", default=None, help="x,y,w,h（選）")
    p.add_argument("--scale", type=int, default=1, help="放大倍率（選）")
    p.set_defaults(func=cmd_view)

    # pixel
    p = sub.add_parser("pixel", help="查單點當前色 + 歷史")
    p.add_argument("--x", type=int, required=True)
    p.add_argument("--y", type=int, required=True)
    p.set_defaults(func=cmd_pixel)

    # stats
    p = sub.add_parser("stats", help="統計總點數 / 貢獻者 / 填充率")
    p.set_defaults(func=cmd_stats)

    # snapshot
    p = sub.add_parser("snapshot", help="強制全圖快照")
    p.set_defaults(func=cmd_snapshot)

    # voucher
    p = sub.add_parser("voucher", help="繪畫券 balance/grant/history")
    p.add_argument("--sub", required=True, choices=["balance", "grant", "history"])
    p.add_argument("--persona", required=True)
    p.add_argument("--amount", type=int, default=None, help="grant 用")
    p.add_argument("--source", default=None, help="grant 來源標記 (預設 manual_grant; 打賞發券=book_tip)")
    p.add_argument("--ref", default=None, help="grant 業務 ref (e.g. book:<slug>)")
    p.set_defaults(func=cmd_voucher)

    # freetime（2026-08-13 額度制回歸：每場 10 顆，發放端 = Cmd_FreeTime step=start）
    p = sub.add_parser("freetime", help="自由時間免費像素狀態（額度制：每場 10 顆，不跨場累積）")
    p.add_argument("--sub", required=False, choices=["status"], default="status")
    p.add_argument("--persona", required=True)
    p.set_defaults(func=cmd_freetime)

    # note
    p = sub.add_parser("note", help="個人繪圖筆記 add/list/update/done")
    p.add_argument("--sub", required=True, choices=["add", "list", "update", "done"])
    p.add_argument("--persona", required=True)
    p.add_argument("--id", default=None, help="update/done 用")
    p.add_argument("--title", default=None)
    p.add_argument("--plan", default=None)
    p.add_argument("--region", default=None, help="x,y,w,h")
    p.add_argument("--size", default=None, help="WxH（算 est_cost）")
    p.add_argument("--status", default=None, help="update 改 status")
    p.set_defaults(func=cmd_note)

    # claim
    # 注意：claims.json 是共享 registry，list 看全員不需 persona；故 --persona 非必填，
    #       僅 add 分支在 cmd_claim 內 guard（dogfood 2026-06-02 發現 list 誤要 persona）
    p = sub.add_parser("claim", help="宣稱區域 add/list/release/done")
    p.add_argument("--sub", required=True, choices=["add", "list", "release", "done"])
    p.add_argument("--persona", default=None)
    p.add_argument("--id", default=None, help="release/done 用")
    p.add_argument("--region", default=None, help="x,y,w,h")
    p.add_argument("--title", default=None)
    p.set_defaults(func=cmd_claim)

    return parser


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)
    # 把解析好的 Paths 掛上 args 供各 handler 用
    args._paths = Paths(args.root, args.treasury_root,
                        getattr(args, "registry_meta", None))
    # handler 的回傳值就是 process 退出碼（None → 0）。
    # 物理意義：原本這行丟掉回傳值，於是 handler 內所有 `return 2`（拒絕路徑）都印著 ❌ 卻 exit 0 ——
    #          呼叫端（skill / 上層腳本 / CI）拿退出碼判成功時，看到的是綠燈而事情沒做。
    #          血證 2026-08-13：place 的 persona→agent 反推守衛拒絕後仍 exit 0（summit 驗自己的修法時撞到）。
    return args.func(args) or 0


if __name__ == "__main__":
    sys.exit(main())
