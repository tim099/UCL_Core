#!/usr/bin/env python3
"""
persona_ocr_locate.py — 在桌面畫面上找 persona session token `##<persona>##` 的文字座標。

# 區塊職責: 只做「擷取畫面 → OCR → 回報 token 的螢幕座標」; **不移動游標、不點擊、不輸入**。
#          操控端一律在 C# (UCL_RemoteWindowControl)，本檔是純判讀端 (Tim 2026-08-02 拍板)。
# 物理意義: token 是使用者手動命名 session 的唯一字串 (例 `##Basecamp##`)，OCR 只認完整相等，
#          不採 substring/模糊，因為誤中的代價是「游標落在別人的 session 上」。
# 數值影響: 命中數必須恰為 1 才回 ok；0 或 >1 都回 ok=false 並附 near-miss 診斷，讓失敗有聲音。

用法:
  python persona_ocr_locate.py --persona basecamp
  python persona_ocr_locate.py --persona basecamp --monitor 1 --region 0,0,0.35,1 --attempts 4
  python persona_ocr_locate.py --token "##basecamp##" --min-confidence 0.4 --save-shot shot.png

掃描範圍是**矩形** (--region x,y,w,h 四個 0~1 比例)，不是字幕帶那種只有上下的橫帶 —— session 清單
固定在視窗左側，掃全桌面既慢又多出一堆同名的誤判來源。

stdout: 單行 JSON (見 build_result)。exit code:
  0 = 唯一命中 / 2 = 0 命中 / 3 = 多重命中 / 4 = OCR 不可用 / 5 = 擷取失敗 / 6 = 參數錯誤

座標語意: 一律回 **virtual desktop 實體像素**，與 Win32 SetCursorPos 同一座標系。
"""

from __future__ import annotations
import argparse
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

EXIT_OK = 0
EXIT_NO_MATCH = 2
EXIT_MULTI_MATCH = 3
EXIT_OCR_UNAVAILABLE = 4
EXIT_CAPTURE_FAIL = 5
EXIT_BAD_ARGS = 6

DEFAULT_MIN_CONF = 0.4          # session 標題字體小，比字幕帶的 0.5 略放寬；命中仍要求完整相等
MAX_NEAR_MISS = 8               # 診斷用：只留前幾筆，避免把整個桌面的文字倒進 stdout


def _set_dpi_aware_once() -> bool:
    """讓本 process 以實體像素看世界 — 失敗回 False，由 capture 端用縮放補償。

    # 物理意義: 非 DPI-aware 的 process 拿到的是被 Windows 虛擬化過的縮放畫面，
    #          OCR 座標會跟 SetCursorPos 的實體座標差一個縮放比 (150% 縮放下差 1.5 倍)。
    # 數值影響: 成功 → scale 恆為 1.0；失敗 → 仍可用 virtual metrics / 影像尺寸算出比例補回來。
    """
    import ctypes
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)   # PROCESS_PER_MONITOR_DPI_AWARE
        return True
    except Exception:
        pass
    try:
        return bool(ctypes.windll.user32.SetProcessDPIAware())
    except Exception:
        return False


# ⚠ 必須在**任何**螢幕座標查詢之前執行一次（2026-08-02 實測踩到）：
#   monitor 列舉先跑、DPI 宣告後跑 → 列舉拿到虛擬化座標（2560 寬回報成 1707），
#   擷取卻是實體像素，兩者拼起來的 bbox 是歪的，而且看起來很像「螢幕真的只有那麼大」。
DPI_AWARE = _set_dpi_aware_once()


def _virtual_screen_rect():
    """virtual desktop 的 (left, top, width, height)，實體像素；失敗回 None。"""
    import ctypes
    try:
        m = ctypes.windll.user32.GetSystemMetrics
        return m(76), m(77), m(78), m(79)   # SM_XVIRTUALSCREEN / Y / CX / CY
    except Exception:
        return None


def enumerate_monitors():
    """列舉實體 monitor → [{index,x,y,w,h,primary}]；失敗回 []。

    # 物理意義: 與 screenstream_daemon 的 _enumerate_monitors_win 同一套 Win32 來源與座標系
    #          (virtual desktop)，所以後台兩頁選到的「螢幕 1」指的是同一塊。
    """
    import ctypes

    class RECT(ctypes.Structure):
        _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                    ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

    class MONITORINFOEXW(ctypes.Structure):
        _fields_ = [("cbSize", ctypes.c_ulong), ("rcMonitor", RECT),
                    ("rcWork", RECT), ("dwFlags", ctypes.c_ulong),
                    ("szDevice", ctypes.c_wchar * 32)]

    try:
        user32 = ctypes.windll.user32
        MonitorEnumProc = ctypes.WINFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p,
                                             ctypes.POINTER(RECT), ctypes.c_void_p)
        user32.GetMonitorInfoW.argtypes = [ctypes.c_void_p, ctypes.POINTER(MONITORINFOEXW)]
        user32.GetMonitorInfoW.restype = ctypes.c_int
        user32.EnumDisplayMonitors.argtypes = [ctypes.c_void_p, ctypes.c_void_p, MonitorEnumProc, ctypes.c_void_p]
        found = []

        def _cb(hmon, _hdc, _rect, _data):
            info = MONITORINFOEXW()
            info.cbSize = ctypes.sizeof(MONITORINFOEXW)
            if user32.GetMonitorInfoW(hmon, ctypes.byref(info)):
                r = info.rcMonitor
                found.append({"index": len(found), "x": r.left, "y": r.top,
                              "w": r.right - r.left, "h": r.bottom - r.top,
                              "primary": bool(info.dwFlags & 0x1)})
            return 1

        user32.EnumDisplayMonitors(None, None, MonitorEnumProc(_cb), None)
        return found
    except Exception:
        return []


def resolve_bbox(monitor: str, region: str):
    """(monitor, region) → 擷取用 bbox (x0,y0,x1,y1)，virtual desktop 實體座標；失敗回 None（=全桌面）。

    monitor: "all"（預設）/ "primary" / "0","1",…（實體 monitor index）
    region:  "x,y,w,h" 四個 0~1 比例，相對**選定 monitor**的左上角；空字串 = 整塊。
    """
    rect = _virtual_screen_rect()
    base = None
    monitors = enumerate_monitors()
    key = (monitor or "all").strip().lower()
    if key == "primary":
        base = next((m for m in monitors if m["primary"]), monitors[0] if monitors else None)
    elif key not in ("", "all"):
        try:
            idx = int(key)
            if 0 <= idx < len(monitors):
                base = monitors[idx]
        except ValueError:
            base = None
    if base is None:
        if not rect:
            return None
        base = {"x": rect[0], "y": rect[1], "w": rect[2], "h": rect[3]}

    x0, y0, w, h = base["x"], base["y"], base["w"], base["h"]
    if region.strip():
        try:
            rx, ry, rw, rh = [float(v) for v in region.split(",")]
        except ValueError:
            raise ValueError(f"--region 格式應為 x,y,w,h 四個 0~1 比例，收到「{region}」")
        rx = min(max(rx, 0.0), 1.0)
        ry = min(max(ry, 0.0), 1.0)
        rw = min(max(rw, 0.0), 1.0 - rx)
        rh = min(max(rh, 0.0), 1.0 - ry)
        if rw <= 0 or rh <= 0:
            raise ValueError(f"--region 寬高必須大於 0（clamp 後得到 w={rw}, h={rh}）")
        x0, y0 = x0 + int(w * rx), y0 + int(h * ry)
        w, h = int(w * rw), int(h * rh)
    return (x0, y0, x0 + w, y0 + h)


def capture_screen(bbox=None):
    """擷取指定 bbox（None = 整個 virtual desktop）→ (PIL.Image, capture_meta)。

    # 物理意義: 沿用 ScreenStream 的 PIL.ImageGrab(all_screens=True) 慣例；
    #          origin 取實際擷取範圍的左上角（多螢幕時可能是負值），OCR 座標一律加回它。
    """
    from PIL import ImageGrab
    dpi_aware = DPI_AWARE
    rect = _virtual_screen_rect()
    if bbox:
        img = ImageGrab.grab(bbox=bbox, all_screens=True)
        origin_x, origin_y = bbox[0], bbox[1]
        want_w, want_h = bbox[2] - bbox[0], bbox[3] - bbox[1]
    else:
        img = ImageGrab.grab(all_screens=True)
        origin_x, origin_y = (rect[0], rect[1]) if rect else (0, 0)
        want_w, want_h = (rect[2], rect[3]) if rect else img.size
    width, height = img.size
    # 影像尺寸 vs 要求的擷取尺寸不一致 → 幾乎都是 DPI 虛擬化；用比例補回實體座標。
    scale_x = (want_w / width) if width else 1.0
    scale_y = (want_h / height) if height else 1.0
    meta = {
        "origin_x": origin_x, "origin_y": origin_y,
        "image_width": width, "image_height": height,
        "capture_width": want_w, "capture_height": want_h,
        "virtual_width": rect[2] if rect else width,
        "virtual_height": rect[3] if rect else height,
        "scale_x": round(scale_x, 6), "scale_y": round(scale_y, 6),
        "dpi_aware": dpi_aware,
    }
    return img, meta


def normalize(text: str) -> str:
    """比對用正規化 — 去所有空白 + casefold。

    # 物理意義: OCR 常在 `##` 與名字之間塞空格、大小寫也不保證 (`##Basecamp##` vs `##basecamp##`)，
    #          這些不是「不同的 token」。
    """
    return "".join(text.split()).casefold()


# 區塊職責: 把 `##<persona>##` 從「OCR 一定讀得一模一樣」的假設，降到「兩側必須有分隔符」的實測形狀。
# 物理意義: 2026-08-02 實測 ##Basecamp##：同一畫面 OCR 讀出 `#Basecamp##Bsr`（跟旁邊的 Bar 標籤併成一塊、
#          吃掉一個 #）與 `+#Basecamp*`（項目符號被讀成 +、結尾 ## 被讀成 *）。要求整塊完整相等 = 永遠 0 命中。
# 數值影響: 名字本身仍要求逐字相等（不做編輯距離），只放寬「兩側各至少一個分隔字元」；
#          純聊天裡提到 basecamp（沒有 # 包夾）不會命中。
DELIMITERS = "#*+＃"


def _delimiter_match(norm_text: str, name: str) -> bool:
    """norm_text 裡是否有一段「分隔符 + name + 分隔符」。"""
    start = norm_text.find(name)
    while start >= 0:
        before = norm_text[start - 1] if start > 0 else ""
        after_index = start + len(name)
        after = norm_text[after_index] if after_index < len(norm_text) else ""
        if before in DELIMITERS and before and after in DELIMITERS and after:
            return True
        start = norm_text.find(name, start + 1)
    return False


def locate(img, token: str, min_confidence: float):
    """OCR 整張畫面 → (命中清單, near-miss 清單)。座標仍是影像像素。"""
    import numpy as np
    import subtitle_ocr

    engine = subtitle_ocr.get_engine()
    if engine is None:
        raise RuntimeError(subtitle_ocr.get_init_error() or "RapidOCR 不可用")

    result, _elapse = engine(np.array(img.convert("RGB")))
    name = normalize(token).strip(DELIMITERS)
    matches, near = [], []
    for item in (result or []):
        if not item or len(item) < 3:
            continue
        box, text, conf = item[0], item[1], item[2]
        try:
            conf_f = float(conf)
        except (TypeError, ValueError):
            conf_f = 0.0
        if not text or not text.strip():
            continue
        norm = normalize(text)
        entry = {
            "text": text.strip(),
            "confidence": round(conf_f, 4),
            "box": [[float(p[0]), float(p[1])] for p in box],
        }
        if name and _delimiter_match(norm, name) and conf_f >= min_confidence:
            matches.append(entry)
        elif len(near) < MAX_NEAR_MISS and name and name in norm:
            # 診斷用：名字出現但兩側沒有分隔符（或信度不足）時留證，
            # 才分得出是 OCR 讀歪了、session 沒命名、還是只是聊天裡提到這個名字。
            near.append(entry)
    # 由上到下、再由左到右 — 讓 index 在同一畫面下穩定可重現（不依 OCR 回傳順序）。
    matches.sort(key=lambda m: (min(p[1] for p in m["box"]), min(p[0] for p in m["box"])))
    return matches, near


def to_screen(entry: dict, meta: dict) -> dict:
    """影像座標 → virtual desktop 實體座標，並補上中心點 (C# 只讀 center_x/center_y)。"""
    xs = [p[0] for p in entry["box"]]
    ys = [p[1] for p in entry["box"]]
    left = meta["origin_x"] + min(xs) * meta["scale_x"]
    right = meta["origin_x"] + max(xs) * meta["scale_x"]
    top = meta["origin_y"] + min(ys) * meta["scale_y"]
    bottom = meta["origin_y"] + max(ys) * meta["scale_y"]
    out = dict(entry)
    out["screen_left"] = int(round(left))
    out["screen_top"] = int(round(top))
    out["screen_right"] = int(round(right))
    out["screen_bottom"] = int(round(bottom))
    out["center_x"] = int(round((left + right) / 2.0))
    out["center_y"] = int(round((top + bottom) / 2.0))
    return out


def build_result(ok: bool, reason: str, token: str, meta: dict, matches: list, near: list,
                 selected: int = -1) -> dict:
    return {
        "ok": ok,
        "reason": reason,
        "token": token,
        "capture": meta,
        "match_count": len(matches),
        "selected_index": selected,
        "matches": matches,
        "near_misses": near,
    }


def emit(payload: dict, code: int) -> int:
    print(json.dumps(payload, ensure_ascii=False))
    return code


def main() -> int:
    ap = argparse.ArgumentParser(description="OCR 定位 persona session token，只回座標不操控游標")
    ap.add_argument("--persona", default="", help="persona 名稱，會組成 ##<persona>## 當 token")
    ap.add_argument("--token", default="", help="直接指定完整 token（優先於 --persona）")
    ap.add_argument("--min-confidence", type=float, default=DEFAULT_MIN_CONF)
    ap.add_argument("--index", type=int, default=-1,
                    help="明示選第幾個（0-based，順序=由上到下）；給了就蓋過 --select")
    ap.add_argument("--select", default="leftmost", choices=["leftmost", "topmost", "strict"],
                    help="多重命中時怎麼選：leftmost=最靠畫面左側（預設）/ topmost=最上面 / strict=不選、直接失敗")
    ap.add_argument("--monitor", default="all", help="all（預設）/ primary / 實體 monitor index")
    ap.add_argument("--region", default="",
                    help="矩形掃描範圍 x,y,w,h（0~1 比例，相對選定 monitor 左上角）；空=整塊")
    ap.add_argument("--initial-delay", type=float, default=0.0,
                    help="第一次擷取前先等幾秒（給剛切到前景的視窗畫完）")
    ap.add_argument("--attempts", type=int, default=1, help="找不到時重試幾次（含第一次）")
    ap.add_argument("--attempt-delay", type=float, default=0.5, help="每次重試之間等幾秒")
    ap.add_argument("--save-shot", default="", help="診斷用：把這次擷取的畫面存檔（預設不存）")
    ap.add_argument("--preview", default="",
                    help="只擷取當前畫面存到這個路徑就結束（不跑 OCR）— 給後台 rect 預覽底圖用")
    ap.add_argument("--preview-max-width", type=int, default=640, help="預覽圖最長邊縮到幾 px")
    ap.add_argument("--list-monitors", action="store_true", help="印出 monitor 清單 JSON 後結束")
    args = ap.parse_args()

    if args.list_monitors:
        print(json.dumps({"ok": True, "monitors": enumerate_monitors(),
                          "dpi_aware": DPI_AWARE}, ensure_ascii=False))
        return EXIT_OK

    # 區塊職責: 預覽底圖 — 只抓圖不 OCR，所以不必付 RapidOCR 的冷啟動（~3-6s），按鈕按下去是即時的。
    # 物理意義: 預覽抓的是「整塊選定 monitor」，rect 由後台畫在上面；若預覽只抓 rect 內，
    #          就會變成「拿裁好的圖去調裁切範圍」，永遠看不到自己漏掉了什麼。
    if args.preview:
        try:
            bbox = resolve_bbox(args.monitor, "")
            img, meta = capture_screen(bbox)
            if img.width > args.preview_max_width > 0:
                ratio = args.preview_max_width / img.width
                img = img.resize((args.preview_max_width, max(1, int(img.height * ratio))))
            img.convert("RGB").save(args.preview)
            print(json.dumps({"ok": True, "preview": args.preview, "capture": meta}, ensure_ascii=False))
            return EXIT_OK
        except Exception as e:
            print(json.dumps({"ok": False, "reason": f"預覽擷取失敗: {e}"}, ensure_ascii=False))
            return EXIT_CAPTURE_FAIL

    token = args.token.strip() or (f"##{args.persona.strip()}##" if args.persona.strip() else "")
    if not token:
        return emit(build_result(False, "缺少 --persona 或 --token", "", {}, [], []), EXIT_BAD_ARGS)

    try:
        bbox = resolve_bbox(args.monitor, args.region)
    except ValueError as e:
        return emit(build_result(False, str(e), token, {}, [], []), EXIT_BAD_ARGS)

    # 區塊職責: 重試迴圈 — 剛被帶到前景的視窗常常還沒畫完，第一張抓到的是舊內容或空白。
    # 物理意義: 重擷取＋重 OCR 都在同一個 process 內，RapidOCR 模型只載入一次（~3-6s），
    #          所以多試幾次的邊際成本是每次 ~0.3-1s，而不是再付一次冷啟動。
    # 數值影響: attempts 是「含第一次」的總次數；命中就立刻跳出，不會白等剩下的 delay。
    if args.initial_delay > 0:
        time.sleep(args.initial_delay)
    meta, matches, near = {}, [], []
    attempts = max(1, args.attempts)
    for attempt in range(attempts):
        if attempt > 0 and args.attempt_delay > 0:
            time.sleep(args.attempt_delay)
        try:
            img, meta = capture_screen(bbox)
        except Exception as e:
            return emit(build_result(False, f"畫面擷取失敗: {e}", token, meta, [], []), EXIT_CAPTURE_FAIL)
        if args.save_shot:
            try:
                img.save(args.save_shot)
            except Exception as e:
                print(f"WARN: 截圖存檔失敗: {e}", file=sys.stderr)
        try:
            matches, near = locate(img, token, args.min_confidence)
        except Exception as e:
            return emit(build_result(False, f"OCR 不可用: {e}", token, meta, [], []), EXIT_OCR_UNAVAILABLE)
        meta["attempt"] = attempt + 1
        meta["attempts"] = attempts
        if matches:
            break

    matches = [to_screen(m, meta) for m in matches]
    near = [to_screen(n, meta) for n in near]
    if not matches:
        return emit(build_result(False, f"畫面上找不到 {token}", token, meta, matches, near), EXIT_NO_MATCH)
    if len(matches) == 1:
        return emit(build_result(True, "唯一命中", token, meta, matches, near, 0), EXIT_OK)

    # 區塊職責: 多重命中的選擇政策。
    # 物理意義: 多重命中是常態不是異常 —— session 標題列與側邊清單一定同時出現同一個 token，
    #          對話區裡提到自己名字也會多冒出來。預設取**最左**：session 清單在視窗左緣，
    #          標題列與對話內容都在它右邊（2026-08-02 實測 側邊 x=383 / 標題列 x=980）。
    # 數值影響: 以 box 左緣排序、同 x 時取較上面那個；--index 一給就完全蓋過本政策。
    if 0 <= args.index < len(matches):
        return emit(build_result(True, f"{len(matches)} 個命中，依 --index 選第 {args.index} 個",
                                 token, meta, matches, near, args.index), EXIT_OK)
    if args.select == "strict":
        return emit(build_result(False, f"{token} 命中 {len(matches)} 次，strict 模式不自行選擇",
                                 token, meta, matches, near), EXIT_MULTI_MATCH)
    if args.select == "topmost":
        chosen = min(range(len(matches)), key=lambda i: (matches[i]["screen_top"], matches[i]["screen_left"]))
        why = "取最上面"
    else:
        chosen = min(range(len(matches)), key=lambda i: (matches[i]["screen_left"], matches[i]["screen_top"]))
        why = "取最靠左"
    return emit(build_result(True, f"{len(matches)} 個命中，{why}（第 {chosen} 個）",
                             token, meta, matches, near, chosen), EXIT_OK)


if __name__ == "__main__":
    sys.exit(main())
