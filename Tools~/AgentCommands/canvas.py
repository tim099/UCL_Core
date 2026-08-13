#!/usr/bin/env python3
"""
canvas.py — Shared Pixel Canvas MVP CLI（共用像素畫布）

職責：
  共用像素畫布 MVP — 一塊 2048×2048 的全社群共用像素畫布。
  1 token / 1 繪畫券 / 1 自由時間免費像素 = 1 像素。append-only
  事件日誌 + 增量 buffer 渲染 → canvas_latest.png。

跨專案 / 路徑（比照 awakening.py：code 在 UCL_Core，state 留主專案）：
  - 本檔（code）跨專案共用，置於 <UCL_Core>/Tools~/AgentCommands/canvas.py
  - state（Canvas/ 事件、券、筆記、宣稱區域）留主專案 AgentCommands/Canvas（CWD-relative 預設）
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

⚠ 鐵律：canvas_latest.png 維持不透明白底（下游預覽相容）；canvas_latest_t.png 為唯一透明輸出；
  兩者皆衍生 render；非 ASCII 字串正常 UTF-8。

測試：--root / --treasury-root 可指向 temp 目錄，不污染真實 state。
"""

from __future__ import annotations

import argparse
import contextlib
import datetime
import json
import os
import secrets
import sys
import time
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
# 自由時間 session 來源（2026-08-13 改制）：Cmd_FreeTime 寫的 per-persona session 檔目錄。
# C# 是唯一寫入端（step=start 開場 / step=next 到期收工 / step=end 提前收工），
# 本工具唯讀 —— 兩端 schema 對齊義務（改欄位要同步改 Cmd_FreeTime.cs）。
# 舊制（10 分鐘冷卻 1 顆、ChatTavern/free_time_sessions.json）2026-08-01 廢止；
# 新制：每場自由時間發 10 顆（step=start 發放，per-session 清零不跨場累積）。
FREE_TIME_SESSIONS = "AgentCommands/FreeTime/sessions"

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

# 預設 persona registry meta 路徑（agent_banks source of truth）；測試用 --registry-meta 覆蓋。
# 物理意義：registry 住在 AgentCommands/AwakenInit/_registry_meta.json，與 treasury 同 AgentCommands 根。
DEFAULT_REGISTRY_META = "AgentCommands/AwakenInit/_registry_meta.json"


# ───────────────────────────── 時間工具 ─────────────────────────────

# 財務操作一律走 Cmd（C# server 端）—— 見 _lib/treasury_cmd.py 的四條理由
import sys as _sys, os as _os
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
from _lib.treasury_cmd import treasury_debit, canvas_voucher_consume  # noqa: E402

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

    def __init__(self, root: str, treasury_root: str, freetime_sessions: str | None = None,
                 registry_meta: str | None = None):
        self.root = Path(root)                      # canvas 根目錄
        self.treasury_root = Path(treasury_root)    # treasury 根目錄
        # 自由時間 session 來源（可配置，測試指向 temp；預設真實 ChatTavern 檔）
        self.freetime_sessions = Path(freetime_sessions or FREE_TIME_SESSIONS)
        # persona registry meta（agent_banks source of truth）；可配置供測試指向 temp。
        # 預設走 DEFAULT_REGISTRY_META（與 treasury 同處 AgentCommands 根下的 AwakenInit/）。
        self._registry_meta = Path(registry_meta or DEFAULT_REGISTRY_META)

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
    def freetime(self) -> Path:
        return self.root / "freetime"

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


def build_buffer(P: Paths, with_mask: bool = False):
    """
    區塊職責：從事件流 replay 出完整 index-map buffer（可選同步產出 painted-mask）
    物理意義：底色填 BLANK_INDEX，逐事件逐像素塗（同座標 last-write-wins）。
              mask 記「曾被畫過」的格子（不論顏色）— index 255 身兼空白底色與可畫純白，
              透明渲染的判定必須靠 mask 而非色值，否則故意畫的白會消失。
    數值影響：with_mask=False 回 buf（2048*2048 bytearray, 1 byte palette index）；
              with_mask=True 回 (buf, mask)（mask 同尺寸 bytearray, 0=沒畫過 1=畫過）。
    """
    buf = bytearray([BLANK_INDEX]) * (CANVAS_W * CANVAS_H)  # 初始全空白底色
    mask = bytearray(CANVAS_W * CANVAS_H) if with_mask else None  # 初始全 0 = 沒畫過
    for ev in iter_events(P):
        for px in ev.get("pixels", []):
            x = px.get("x")
            y = px.get("y")
            # 防呆：事件內若有越界座標（理論不會）直接跳過
            if x is None or y is None or not (0 <= x < CANVAS_W) or not (0 <= y < CANVAS_H):
                continue
            try:
                idx = parse_color(px.get("color"))
            except ValueError:
                continue
            pos = y * CANVAS_W + x
            buf[pos] = idx   # last-write-wins：直接覆蓋
            if mask is not None:
                mask[pos] = 1
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

def ledger_balance(P: Paths, account_id: str, currency: str = "tavern_token") -> int:
    """
    區塊職責：算指定 bank 的 token 餘額（sum credit - sum debit）
    物理意義：對齊 balance_query.py 的計算邏輯，純讀檔。
    """
    total_credit = 0
    total_debit = 0
    if not P.ledger.is_dir():
        return 0
    for date_dir in sorted(P.ledger.iterdir()):
        if not date_dir.is_dir():
            continue
        for ef in sorted(date_dir.iterdir()):
            if ef.suffix != ".json":
                continue
            e = read_json(ef)
            if e is None:
                continue
            if e.get("account_id") != account_id:
                continue
            if e.get("currency", "tavern_token") != currency:
                continue
            if e.get("type") == "credit":
                total_credit += e.get("amount", 0)
            elif e.get("type") == "debit":
                total_debit += e.get("amount", 0)
    return total_credit - total_debit


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


def voucher_balance(P: Paths, persona: str) -> int:
    return load_voucher(P, persona).get("balance", 0)


# ───────────────────────── freetime state ─────────────────────────

def freetime_path(P: Paths, persona: str) -> Path:
    return P.freetime / f"{persona}.json"


def load_freetime(P: Paths, persona: str) -> dict:
    """區塊職責：載入 / 初始化 per-persona 免費像素額度 state（2026-08-13 額度制）。

    物理意義：發放端是 Cmd_FreeTime step=start（granted=10 / used=0 / session_id 整份覆寫，
              history 保留）；本工具只在消費時遞增 used —— 兩端 schema 對齊義務。
    """
    data = read_json(freetime_path(P, persona))
    if data is None:
        data = {"persona": persona, "session_id": None,
                "granted": 0, "used": 0, "history": []}
    return data


def free_session_path(P: Paths, persona: str) -> Path:
    """區塊職責：解析 persona 的自由時間 session 檔（目錄可由 --freetime-sessions 覆寫）"""
    return P.freetime_sessions / f"{persona}.json"


def active_free_session(P: Paths, persona: str, now: datetime.datetime) -> dict | None:
    """
    區塊職責：判 persona 是否在 active 自由時間 session
    物理意義：讀 Cmd_FreeTime 寫的 per-persona session 檔。**截止是軟的**（Tim 2026-08-13
              補拍）：until 過了不打斷進行中的活動，最後一件做完跑 step=next 才收工 ——
              所以這裡只認 active 旗標＋start 已到，**不拿 end_ts 掐額度**；
              session 的關閉（active=false）由 Cmd_FreeTime next/end/stale-on-start 負責。
    數值影響：命中回 {"id": session_id, "end_ts": ...}，否則 None。
    """
    s = read_json(free_session_path(P, persona))
    if not isinstance(s, dict):
        return None
    if not s.get("active"):
        return None
    start = parse_iso(s.get("start_ts"))
    if start is None or start > now:
        return None
    return {"id": s.get("session_id"), "end_ts": s.get("end_ts")}


def free_pixels_available(P: Paths, persona: str, now: datetime.datetime) -> int:
    """
    區塊職責：回此刻可用的免費像素顆數（額度制，可批量）
    物理意義：(a) 必須在 active free session；(b) 額度記錄必須屬於**當前** session
              （session_id 對得上）—— 舊場殘餘額度不跨場（per-session 清零，Tim 拍板）。
    數值影響：回 max(0, granted - used)；不在 session / 額度不屬於本場 → 0。
    """
    session = active_free_session(P, persona, now)
    if session is None:
        return 0
    ft = load_freetime(P, persona)
    if ft.get("session_id") != session.get("id"):
        return 0   # 額度是別場發的 → 不跨場
    return max(0, int(ft.get("granted", 0)) - int(ft.get("used", 0)))


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
    free_avail = free_pixels_available(P, persona, now)
    voucher_avail = voucher_balance(P, persona)
    token_avail = ledger_balance(P, bank)

    use_free = use_voucher = use_token = 0

    if pay == "freetime":
        # 只用免費像素
        if n > free_avail:
            raise ValueError(
                f"免費像素不足：需 {n}，{persona} 本場可用 {free_avail} — "
                "額度來自 Cmd_FreeTime step=start（每場 10 顆，不跨場累積）；"
                "不在自由時間 session 內時額度為 0。")
        use_free = n
    elif pay == "voucher":
        # 只用券
        if n > voucher_avail:
            raise ValueError(f"繪畫券不足：需 {n}，{persona} 餘額 {voucher_avail}")
        use_voucher = n
    elif pay == "token":
        # 只用 token
        if n > token_avail:
            raise ValueError(f"token 不足：需 {n}，{bank} 餘額 {token_avail}")
        use_token = n
    else:  # auto：免費 → 券 → token 依序消耗
        remaining = n
        use_free = min(free_avail, remaining)
        remaining -= use_free
        use_voucher = min(voucher_avail, remaining)
        remaining -= use_voucher
        use_token = min(token_avail, remaining)
        remaining -= use_token
        if remaining > 0:
            raise ValueError(
                f"資源合計不足：需 {n}，免費 {free_avail} + 券 {voucher_avail} "
                f"+ token {token_avail} = {free_avail + voucher_avail + token_avail}")

    return {"free": use_free, "voucher": use_voucher, "token": use_token}


def cmd_place(args):
    P = args._paths
    ensure_meta(P)
    persona = args.persona
    agent = args.agent
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
        # 步驟 5：更新 per-persona freetime state（額度制：used 遞增；發放端是 Cmd_FreeTime）
        if free_consumed > 0:
            ft = load_freetime(P, persona)
            ft["used"] = int(ft.get("used", 0)) + free_consumed
            ft.setdefault("history", []).append({
                "ts": iso_ms(now), "uuid": secrets.token_hex(3),
                "ref": f"{event_uuid}",
                "session_id": ft.get("session_id"),
                "count": free_consumed,
            })
            write_json(freetime_path(P, persona), ft)
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
    #   為簡潔 + 正確性，MVP 直接全 replay build buffer 再 encode；
    #   buffer 是 cached 概念，這裡每次 place 後重建一次確保 last-write-wins 正確。
    buf, mask = build_buffer(P, with_mask=True)
    render_latest(P, buf, mask)

    # 步驟 8：回報結果
    print(f"# 🎨 placed {n} pixel(s)")
    print(f"  event       : {ev_path}")
    print(f"  persona     : {persona} (agent={agent}, bank={bank})")
    print(f"  pay_breakdown: freetime={plan['free']} voucher={plan['voucher']} token={plan['token']}")
    print(f"  voucher bal : {voucher_balance(P, persona)}")
    print(f"  token bal   : {ledger_balance(P, bank)}")
    print(f"  ledger_refs : {ledger_refs}")
    print(f"  canvas_latest: {P.latest_png}")
    print(f"  canvas_latest_t: {P.latest_t_png} (透明變體)")


# ───────────────────────────── view ─────────────────────────────

def cmd_view(args):
    P = args._paths
    ensure_meta(P)
    buf = build_buffer(P)
    img = buffer_to_image(buf)

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

    # 縮放（spec：scale 放大倍率，NEAREST 保像素硬邊）
    if args.scale and args.scale != 1:
        from PIL import Image as _Img
        img = img.resize((img.width * args.scale, img.height * args.scale),
                         resample=_Img.NEAREST)

    P.last_view_png.parent.mkdir(parents=True, exist_ok=True)
    img.save(str(P.last_view_png))
    print(f"# 🖼 view rendered")
    print(f"  size  : {img.width}x{img.height}")
    print(f"  path  : {P.last_view_png}")


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
        v = load_voucher(P, persona)
        v["balance"] = v.get("balance", 0) + args.amount
        v["history"].append({
            "ts": iso_ms(now), "uuid": secrets.token_hex(3),
            "type": "grant", "amount": args.amount,
            # source/ref 可由 caller 覆寫 (e.g. library.py 打賞發券 source=book_tip ref=book:<slug>),
            # 預設維持 manual_grant 向後相容
            "source": getattr(args, "source", None) or "manual_grant",
            "ref": getattr(args, "ref", None) or "",
        })
        write_json(voucher_path(P, persona), v)
        print(f"# 🎟 granted {args.amount} 繪畫券 → {persona}")
        print(f"  new balance: {v['balance']}")
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
    # 2026-08-13 額度制回歸（舊冷卻制 2026-08-01 廢止 — 死因是 session 來源檔沒有寫入端；
    # 新制寫入端 = Cmd_FreeTime step=start，本工具唯讀 session 檔 + 讀寫額度檔 used 欄）。
    P = args._paths
    ensure_meta(P)
    persona = args.persona
    now = utcnow()

    if args.sub != "status":
        print(f"❌ unknown freetime sub: {args.sub}", file=sys.stderr)
        sys.exit(2)

    session = active_free_session(P, persona, now)
    ft = load_freetime(P, persona)
    granted = int(ft.get("granted", 0))
    used = int(ft.get("used", 0))

    print(f"# 🎨 {persona} 自由時間免費像素狀態（額度制：每場 10 顆，不跨場累積）")
    if session is None:
        print("  自由時間: ❌ 不在 active session（免費像素額度為 0）")
        print("  進場: run_cmd.py run FreeTime --arg step=start --arg persona=<me> --arg until=<HH:mm>")
    else:
        end = parse_iso(session.get("end_ts"))
        print(f"  自由時間: ✅ active（session 至 {session.get('end_ts')}）")
        if end:
            remain = (end - now).total_seconds()
            if remain >= 0:
                print(f"  session 剩餘: {int(remain // 60)} 分 {int(remain % 60)} 秒")
            else:
                print("  已過軟截止 —— 最後一件活動做完跑 step=next 收工（額度在收工前仍可用）")
    if session and ft.get("session_id") == session.get("id"):
        print(f"  免費像素: {max(0, granted - used)} 顆可用（本場已用 {used}/{granted}）")
    elif session:
        print(f"  免費像素: 0 顆（額度記錄屬於別場 session — 發放端是 step=start，跑過才有）")
    else:
        print(f"  （上場記錄: 已用 {used}/{granted} — 不跨場）")


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
    parser.add_argument("--freetime-sessions", default=None,
                        help=f"自由時間 session 檔目錄（Cmd_FreeTime 寫的 per-persona json；測試隔離用；預設 {FREE_TIME_SESSIONS}）")
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
    p.add_argument("--agent", default="claude-code", help="決定扣哪個 bank（預設 claude-code）")
    p.add_argument("--pay", choices=["auto", "freetime", "voucher", "token"], default="auto")
    p.set_defaults(func=cmd_place)

    # view
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
                        getattr(args, "freetime_sessions", None),
                        getattr(args, "registry_meta", None))
    args.func(args)


if __name__ == "__main__":
    main()
