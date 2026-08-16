#!/usr/bin/env python3
"""
subtitle_ocr.py — RapidOCR 字幕帶 OCR helper (Paddle ONNX 模型, 純 CPU)

# 區塊職責：給 screenstream_montage (或別的 caller) 一個「crop 字幕帶 + OCR → 字串」的單一 entry point。
# 物理意義：縮圖牆 (480x270/格) 上字幕已壓糊, OCR 必須回到 ring buffer 原始 1080p frame 才有解析度;
#          字幕帶在影片底部約 13% 高 (16:10 螢幕看 16:9 內容會偏上, 故 --y-pct 可調)。
# 數值影響：RapidOCR (= PaddleOCR 模型的 ONNX 重打包, 同等中文品質) init ~3s 載入模型 → cache 單例;
#          每幀 OCR ~100-300ms (純 CPU onnxruntime), 整輪 12 幀 ~2-4s 額外開銷。
# 設計選擇 (2026-06-09): Tim 系統 pip 環境多個 metadata 壞掉, paddleocr install 鏈崩潰 (要 paddlepaddle+50+ deps),
#                       改用 rapidocr-onnxruntime: 模型同源 (Paddle 權重 ONNX export), 中文品質一樣, 依賴極簡。

座標語意 (Tim 2026-07-28 拍板「0=畫面下方, 高度往上長」— 取代舊頂部原點 y_pct):
  region = (y_bottom_pct, h_pct) — y_bottom_pct 是「帶底邊離畫面下緣的距離比例」(0=貼底),
  h_pct 是帶高度、從 y_bottom 往「上」延伸。例: (0, 0.1) = 覆蓋畫面最下方 10%。
  多區域: regions = [(0, 0.12), (0.85, 0.1)] — 主帶在下方 + 額外帶在上方 (有些影片字幕偶爾跑上面)。

CLI 用法 (standalone debug):
  python subtitle_ocr.py <frame_path> [--y-bottom-pct 0] [--h-pct 0.12]
  → stdout 印 OCR 結果一行

import 用法 (給 montage 等 caller):
  from subtitle_ocr import ocr_subtitle_regions, is_available
  if is_available():
      text = ocr_subtitle_regions(Path("frame.jpg"), regions=[(0.0, 0.12)])
      # text: "字幕內容" 或 "" (空字串 = 該幀無字幕 / OCR 沒讀到)

daemon-side 並行 cache 用法 (T-OCR-Pipeline, Tim 2026-06-10 拍板「錄製時就自動產生, 多執行緒並行」):
  pool = OcrWorkerPool(cache_dir, regions=[(0.0, 0.12)], workers=2)
  pool.start()
  pool.submit(frame_path)        # 每寫一張 frame 丟進 queue, worker 背景 OCR → cache json
  pool.stop()
  # montage 端: read_cached_text(cache_dir, frame_path, mtime) → 命中回 text (含空字串), miss 回 None

設計鐵律:
  - fail-soft: paddleocr 沒裝 → is_available()=False, caller 走無 OCR 路徑, 不阻塞
  - lazy init: PaddleOCR 實例第一次呼叫才 init (~5s), 之後 cache
  - 純文字回傳: caller 自己處理多行合併 / 編號對齊, 本 module 只負責「frame → text」
  - cache 驗 mtime: ring buffer 會覆寫同名 frame, cache json 記錄當時 mtime,
    讀取時 |cached - 實際| > 容差即視為 stale (防舊 cache 配到新畫面)
"""

from __future__ import annotations
import argparse
import json
import os
import queue
import sys
import threading
import time
from pathlib import Path
from typing import Optional

# 預設字幕帶位置 — 底部原點語意 (Tim 2026-07-28 拍板: 0=畫面下方, 高度往上長)
# 物理意義: y_bottom=0 + h=0.12 = 覆蓋畫面最下方 12%; 舊頂部原點預設 0.78/0.12 (=下方 0.10~0.22)
#          的換算遷移由 caller (daemon/montage 讀 config 時) 處理, 本 module 只認底部原點。
DEFAULT_Y_BOTTOM_PCT = 0.0
DEFAULT_H_PCT = 0.12
DEFAULT_MIN_CONF = 0.5


# 水平範圍預設 (2026-08-04 Tim 要求可調寬度/x 中心前, 帶一律滿寬)
# 物理意義: x_center 0.5 = 畫面正中; w 1.0 = 滿寬。**缺欄位就是這兩個值 = 改動前的行為**,
#          所以舊 config / 舊 cache / 只傳 (y,h) 的舊 caller 全部行為不變。
DEFAULT_X_CENTER_PCT = 0.5
DEFAULT_W_PCT = 1.0


def normalize_regions(regions) -> list:
    """任意 caller 輸入 → 正規化 [(y_bottom, h, x_center, w), ...] (clamp 0~1, 去無效項).

    # 區塊職責: regions 是跨層 (config json / CLI / pool / cache) 傳遞的核心型別, 收口統一驗證
    # 物理意義: 垂直 = 底部原點 (y_bottom 帶底離下緣, h 往上長);
    #          水平 = 中心 + 寬度 (x_center 0.5 正中, w 1 滿寬) —— 字幕對齊畫面中央,
    #          用「中心+寬」調寬時是往中間收, 左緣制會邊收邊往右推。
    #          接受形態: (y,h) / [y,h] / (y,h,xc,w) / [y,h,xc,w] /
    #                   {"y_bottom_pct","h_pct"[,"x_center_pct","w_pct"]}
    # 數值影響: **回傳一律 4-tuple** —— 水平欄位缺席補 0.5/1.0 (滿寬 = 舊行為);
    #          h<=0 或 w<=0 的項剔除; 全剔光回預設單帶 — OCR 永遠有帶可裁
    """
    out = []
    for r in (regions or []):
        try:
            if isinstance(r, dict):
                y, h = float(r.get("y_bottom_pct", 0)), float(r.get("h_pct", 0))
                xc = float(r.get("x_center_pct", DEFAULT_X_CENTER_PCT))
                w = float(r.get("w_pct", DEFAULT_W_PCT))
            else:
                y, h = float(r[0]), float(r[1])
                xc = float(r[2]) if len(r) >= 4 else DEFAULT_X_CENTER_PCT
                w = float(r[3]) if len(r) >= 4 else DEFAULT_W_PCT
        except (TypeError, ValueError, IndexError, KeyError):
            continue
        y = min(max(y, 0.0), 1.0)
        h = min(max(h, 0.0), 1.0)
        xc = min(max(xc, 0.0), 1.0)
        w = min(max(w, 0.0), 1.0)
        if h <= 0.0 or y >= 1.0 or w <= 0.0:
            continue
        out.append((round(y, 4), round(h, 4), round(xc, 4), round(w, 4)))
    return out if out else [(DEFAULT_Y_BOTTOM_PCT, DEFAULT_H_PCT,
                            DEFAULT_X_CENTER_PCT, DEFAULT_W_PCT)]


def regions_from_config(cfg: dict) -> list:
    """從 daemon _config.json dict 解析完整 regions 清單 (主帶 + 額外區域) — daemon/montage 共用單一實作.

    # 區塊職責: config keys → [(y_bottom, h), ...]; 新舊 key 遷移收口在此一處
    # 物理意義: 主帶 = ocr_y_bottom_pct/ocr_h_pct (底部原點) + ocr_x_center_pct/ocr_w_pct
    #          (水平中心與寬度, 缺席 = 0.5/1.0 滿寬); 額外區域 = ocr_extra_regions
    #          (list of {"y_bottom_pct","h_pct"} 或 [y,h]); 舊 config 只有頂部原點 ocr_y_pct 時
    #          自動換算 y_bottom = 1 - y_pct - h_pct (舊語意帶頂在 y_pct、往下長 h_pct)。
    # 數值影響: 回傳保證非空 (normalize_regions 兜底預設單帶)。
    """
    h = float(cfg.get("ocr_h_pct", DEFAULT_H_PCT))
    if "ocr_y_bottom_pct" in cfg:
        y_bottom = float(cfg.get("ocr_y_bottom_pct", DEFAULT_Y_BOTTOM_PCT))
    elif "ocr_y_pct" in cfg:
        # 舊頂部原點 key 遷移: 帶底邊 = 1 - (帶頂 + 高度)
        y_bottom = 1.0 - float(cfg.get("ocr_y_pct", 0.78)) - h
    else:
        y_bottom = DEFAULT_Y_BOTTOM_PCT
    x_center = float(cfg.get("ocr_x_center_pct", DEFAULT_X_CENTER_PCT))
    w = float(cfg.get("ocr_w_pct", DEFAULT_W_PCT))
    # 區塊職責：開關過濾 —— 關掉的帶**不進掃描清單**（Tim 2026-08-16 的 CheckBox）。
    # 物理意義：「關掉」是使用者的明確意圖，跟「幾何無效被剔除」不是同一件事。
    #          ⚠ 所以這裡不能讓它掉進 normalize_regions 的「全剔光回預設單帶」那條保護 ——
    #          那條是為了防垃圾輸入讓 OCR 沒帶可裁；用在這裡會變成「關了照掃」，
    #          而畫面上開關是關的 ⇒ 設定與行為脫鉤，正是最難查的那種。
    # 數值影響：全部關閉 → 回 []（呼叫端據此完全不掃）；缺 enable 欄一律視為開啟。
    regions = []
    if bool(cfg.get("ocr_main_enable", True)):
        regions.append((y_bottom, h, x_center, w))
    extra = cfg.get("ocr_extra_regions")
    if isinstance(extra, list):
        regions += [r for r in extra
                    if not (isinstance(r, dict) and not bool(r.get("enable", True)))]
    if not regions:
        return []
    return normalize_regions(regions)

# T-OCR-CPU-Fix (2026-06-10 事故, summit 止血 + 定罪, basecamp 根治)
# 區塊職責: 限制每套 RapidOCR engine 的 onnxruntime 執行緒池規模
# 物理意義: 零參數 RapidOCR() → onnxruntime 預設 intra-op pool = 實體核心數 + idle busy-spin
#          不讓出 CPU; 多套 engine (pool workers + inline) 並存 = 數組滿核池全天空轉 → 系統 99%。
#          事故反證: fps 2→1 推理量砍半 CPU 紋絲不動 → 負載來自 engine 存活數, 不是推理量。
# 數值影響: intra=2 / inter=1 → 每套 engine 最多 2 條計算執行緒; 單幀推理稍慢 (~1.5-2x)
#          但字幕帶小圖本來就輕 (數百 ms 級), 對 1-2 fps pipeline 吞吐毫無壓力。
ENGINE_INTRA_THREADS = 2
ENGINE_INTER_THREADS = 1


def _make_engine():
    """建一套執行緒受限的 RapidOCR engine — 所有 engine init 必走這裡, 禁止零參數 RapidOCR().

    rapidocr_onnxruntime 1.4.x 原生支援 intra/inter kwargs (propagate 到 Det/Cls/Rec 三模組);
    舊版不認 kwargs → TypeError fallback 零參數 (此時 CPU 風險回歸, 但至少功能不斷)。
    """
    from rapidocr_onnxruntime import RapidOCR
    try:
        return RapidOCR(intra_op_num_threads=ENGINE_INTRA_THREADS,
                        inter_op_num_threads=ENGINE_INTER_THREADS)
    except TypeError:
        return RapidOCR()

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


# ===========================================================
# Lazy paddleocr import + cache
# ===========================================================

# 區塊職責：PaddleOCR 載入失敗時整支工具仍能 import (fail-soft for caller)
# 物理意義：caller 可先 import 再判 is_available() 才呼叫 OCR 函式
# 數值影響：_ocr_engine None 時, ocr_subtitle_band() 一律回 "" (空字串)
_ocr_engine = None
_ocr_init_error: Optional[str] = None
_ocr_init_attempted = False


def is_available() -> bool:
    """檢查 paddleocr 是否可用 (lazy 試載入一次, 之後 cache 結果)."""
    global _ocr_init_attempted
    if not _ocr_init_attempted:
        _try_init()
    return _ocr_engine is not None


def _try_init():
    """嘗試載入 RapidOCR (第一次呼叫 ~3s 載入模型, 之後 noop)."""
    global _ocr_engine, _ocr_init_error, _ocr_init_attempted
    _ocr_init_attempted = True
    try:
        from rapidocr_onnxruntime import RapidOCR  # noqa: F401 — 先驗 import 再走 _make_engine
    except Exception as e:
        _ocr_init_error = (f"rapidocr-onnxruntime import 失敗: {e}; "
                           f"→ pip install --user rapidocr-onnxruntime")
        return
    try:
        # 區塊職責：RapidOCR 預設走中英文模型 (Paddle ch_PP-OCRv4 ONNX export); 字幕都正向不必 angle cls
        # 物理意義：純 CPU onnxruntime, 不需 GPU/CUDA, 第一次 init 載入 det+rec 兩個 ONNX 模型 (~10MB)
        # 數值影響：實例 cache 後重複呼叫 OCR 快; 必走 _make_engine 縮池 (T-OCR-CPU-Fix, 防 idle spin 吃滿核)
        _ocr_engine = _make_engine()
    except Exception as e:
        _ocr_init_error = f"RapidOCR init 失敗: {e}"


def get_engine():
    """回共用的執行緒受限 engine (不可用時回 None) — 給需要 box 座標的 caller.

    # 區塊職責: 對外唯一取 engine 的入口, 讓「不裁字幕帶、要原始 box」的 caller (persona_ocr_locate)
    #          不必碰 _ocr_engine 私有變數, 也不會為了拿 box 另建零參數 RapidOCR()。
    # 物理意義: 回的是同一顆 lazy-init 單例, 執行緒池已縮到 intra=2/inter=1 (T-OCR-CPU-Fix)。
    # 數值影響: 不新增 engine 實例 → 不增加 idle busy-spin 的 CPU 底噪。
    """
    return _ocr_engine if is_available() else None


def get_init_error() -> Optional[str]:
    """給 caller 用的 debug — 若 OCR 不可用, 印這個錯讓 user 知道為何."""
    if not _ocr_init_attempted:
        _try_init()
    return _ocr_init_error


# ===========================================================
# Core OCR function
# ===========================================================


def ocr_subtitle_regions(
    frame_path: Path,
    regions=None,
    min_confidence: float = DEFAULT_MIN_CONF,
) -> str:
    """從一張 frame 裁一到多個字幕帶 + 跑 OCR → 回合併字串.

    # 區塊職責：給 caller 一個「丟 frame 路徑回字幕文字」的 one-call API (多區域版)
    # 物理意義：regions = [(y_bottom_pct, h_pct), ...] 底部原點比例 (0~1, 解析度無關);
    #          字幕辨識率隨字體大小急降, 故必走原始 frame, 別用縮圖牆 tile.
    # 數值影響：min_confidence 過濾低信度文字塊 (預設 0.5); 各區域非空結果按 regions 順序用換行串接;
    #          OCR 失敗 / 該幀無字幕 → 回 "" (空字串, 對齊 caller 期望單一字串型別).
    """
    if not is_available():
        return ""
    return _ocr_regions_with_engine(_ocr_engine, frame_path, normalize_regions(regions), min_confidence)


def ocr_subtitle_band(
    frame_path: Path,
    y_bottom_pct: float = DEFAULT_Y_BOTTOM_PCT,
    h_pct: float = DEFAULT_H_PCT,
    min_confidence: float = DEFAULT_MIN_CONF,
    x_center_pct: float = DEFAULT_X_CENTER_PCT,
    w_pct: float = DEFAULT_W_PCT,
) -> str:
    """單帶便捷版 (底部原點) — 委派 ocr_subtitle_regions。水平參數在後, 舊 caller 不受影響。"""
    return ocr_subtitle_regions(frame_path, [(y_bottom_pct, h_pct, x_center_pct, w_pct)], min_confidence)


def _crop_band_to_array(frame_path_or_img, y_bottom_pct: float, h_pct: float,
                        x_center_pct: float = DEFAULT_X_CENTER_PCT,
                        w_pct: float = DEFAULT_W_PCT):
    """裁字幕帶 → numpy array; 任何失敗回 None.

    # 區塊職責: OCR 各入口共用的 crop 前處理 (收 Path 或已開啟的 PIL Image — 多區域同幀免重複 decode)
    # 物理意義: 垂直為底部原點比例制 — 帶的像素範圍 = [H*(1-y_bottom-h), H*(1-y_bottom)];
    #          水平為中心+寬 — [W*(xc-w/2), W*(xc+w/2)]，超出畫面的部分 clamp 掉
    #          (往邊緣推寬帶時只會被切, 不會回繞到另一側)。
    # 數值影響: 裁窄水平範圍會**減少送進 OCR 的像素**, 對兩側有雜訊 (台標/彈幕/UI) 的來源
    #          能同時提升命中率與速度; 預設 0.5/1.0 = 滿寬 = 改動前行為。
    """
    try:
        from PIL import Image
        import numpy as np
    except Exception:
        return None
    try:
        def _crop(img):
            w, h = img.size
            y0 = max(int(h * (1.0 - y_bottom_pct - h_pct)), 0)
            y1 = min(int(h * (1.0 - y_bottom_pct)), h)
            if y1 <= y0:
                return None
            half = max(min(w_pct, 1.0), 0.0) * 0.5
            xc = max(min(x_center_pct, 1.0), 0.0)
            x0 = max(int(w * (xc - half)), 0)
            x1 = min(int(w * (xc + half)), w)
            if x1 <= x0:
                return None
            return np.array(img.crop((x0, y0, x1, y1)).convert("RGB"))
        if isinstance(frame_path_or_img, (str, Path)):
            with Image.open(frame_path_or_img) as img:
                return _crop(img)
        return _crop(frame_path_or_img)
    except Exception:
        return None


def _parse_ocr_result(result, min_confidence: float) -> str:
    """RapidOCR 原始 result → 排序合併後的字幕字串 (失敗 / 無字幕 → "").

    # 物理意義: 字幕通常 1-3 行, 按 box 上邊 y 排序確保上下順序; 低信度塊過濾
    """
    try:
        if not result:
            return ""
        entries = []
        for item in result:
            if not item or len(item) < 3:
                continue
            box, text, conf = item[0], item[1], item[2]
            # ⚠ 部分 rapidocr_onnxruntime 版本回傳 conf 為「字串」(e.g. "0.998")；直接 conf < min_confidence
            #   會 str < float → TypeError → 被下方 except 吞掉 → 整幀回 "" (OCR 長期 0 命中真兇, 2026-07-27 Tim QA)。
            #   一律先 float() 強制轉數值再比。
            try:
                conf_f = float(conf)
            except (TypeError, ValueError):
                conf_f = 0.0
            if conf_f < min_confidence:
                continue
            if not text or not text.strip():
                continue
            top_y = min(p[1] for p in box)
            entries.append((top_y, text.strip()))
        entries.sort(key=lambda t: t[0])
        return "\n".join(t for _, t in entries)
    except Exception:
        return ""


def _ocr_regions_with_engine(engine, frame_path: Path, regions: list,
                             min_confidence: float) -> str:
    """指定 engine 跑「逐區域 crop → OCR → 非空結果合併」(pool worker 跟全域單例共用此核心).

    # 物理意義: 同幀開一次圖, 逐區域裁切各跑一次 OCR — 額外區域通常為空 (字幕偶爾才跑上面),
    #          RapidOCR 對小圖 ~100-300ms/次, 區域數 1-3 個在 1-2 fps pipeline 下無吞吐壓力。
    # 數值影響: 各區域非空結果按 regions 順序以換行串接 (主帶在前); 全空 → ""。
    """
    try:
        from PIL import Image
    except Exception:
        return ""
    texts = []
    try:
        with Image.open(frame_path) as img:
            for (y_bottom, h, x_center, w) in regions:
                arr = _crop_band_to_array(img, y_bottom, h, x_center, w)
                if arr is None:
                    continue
                try:
                    # RapidOCR API: 回 (results, elapse) — results = [ [box, text, confidence], ... ] 或 None
                    result, _elapse = engine(arr)
                except Exception:
                    continue
                text = _parse_ocr_result(result, min_confidence)
                if text:
                    texts.append(text)
    except Exception:
        return ""
    return "\n".join(texts)


# ===========================================================
# T-OCR-Pipeline (Tim 2026-06-10 拍板) — daemon-side 並行 OCR cache
# 區塊職責: daemon 錄 frame 時就背景 OCR, montage 端 cache-first 讀, 免每輪 montage 重跑 100+ 幀
# 物理意義: ring buffer 同名 frame 會被覆寫 → cache json 必記 mtime, 讀取驗 |Δ| < 容差才算命中;
#          worker thread 各持自己的 RapidOCR engine (onnxruntime session 共享有 thread-safety 疑慮,
#          每 engine ~10MB ONNX 模型, 2 workers 記憶體開銷可忽略)
# 數值影響: fps=2 + 每幀 OCR ~100-300ms → 單 worker 負載 ~0.6, 預設 2 workers 留 headroom;
#          queue 滿 (backlog 64) 丟最舊 task (fail-soft, 計數進 dropped)
# ===========================================================

MTIME_TOLERANCE_SEC = 0.5   # cache mtime 容差 — copy/filesystem 精度抖動內視為同一張 frame

# T-OCR-AdaptiveDensity (Tim 2026-06-10 拍板「落後太多時自動降低密度追進度」)
# 物理意義: lag = 最新 frame mtime - watermark; 每超 STEP 秒 stride +1 (跳幀 OCR),
#          被跳過的幀寫 stub cache (skipped=true) → watermark 照常推進 + montage 端不重跑 inline。
#          字幕一句通常停留 2s+ (fps=2 下 4+ 幀), stride 2-4 仍能命中句子, 損失極小。
# 數值影響: lag<15s stride=1 (全幀); 15-30s → 2; 30-45s → 3; >45s → 4 (上限)。
#          追上後 lag 縮小 stride 自動降回 — 無滯後震盪 (per-submit 即時計算)。
ADAPTIVE_LAG_STEP_SEC = 15.0
ADAPTIVE_MAX_STRIDE = 4


def cache_path_for(cache_dir: Path, frame_path: Path) -> Path:
    """frame_0042.jpg → <cache_dir>/frame_0042.json (跟 ring buffer 同名對應)."""
    return cache_dir / (frame_path.stem + ".json")


STATUS_FILENAME = "_status.json"    # pool 進度水位檔 (T-OCR-Watermark, Tim 2026-06-10 拍板)


def read_ocr_status(cache_dir: Path) -> Optional[dict]:
    """讀 daemon OCR pool 進度狀態 (watermark 等). 不存在 / 壞檔回 None.

    # 物理意義: montage 端拿 watermark_mtime 把觀看窗口 clamp 到「字幕已生成」範圍,
    #          多 viewer 同時拉 montage 時全吃 cache, 不重複跑 inline OCR (效能瓶頸根治)
    """
    spath = Path(cache_dir) / STATUS_FILENAME
    try:
        with spath.open("r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, json.JSONDecodeError, ValueError):
        return None


BAND_TOLERANCE = 0.02       # cache band 參數容差 — caller 帶位偏離 cache 產出帶位過多即不對版


def _regions_match(cached_regions, want_regions) -> bool:
    """cache 產出帶位 vs caller 要的帶位 — 逐區域比對 (數量不同即不對版; 各分量容差 BAND_TOLERANCE)."""
    try:
        a = normalize_regions(cached_regions)
        b = normalize_regions(want_regions)
        if len(a) != len(b):
            return False
        return all(abs(ra[0] - rb[0]) <= BAND_TOLERANCE and abs(ra[1] - rb[1]) <= BAND_TOLERANCE
                   for ra, rb in zip(a, b))
    except Exception:
        return False


def read_cached_text(cache_dir: Path, frame_path: Path, mtime: float,
                     regions=None,
                     treat_skipped_as_miss: bool = False) -> Optional[str]:
    """讀 daemon 預產 OCR cache. 命中回 text (可為空字串=無字幕), miss/stale 回 None.

    # 物理意義: None vs "" 語意嚴格區分 — None=caller 該 fallback 自己 OCR; ""=確定無字幕;
    #          caller 傳 regions 時驗 cache 產出帶位一致 (caller 自帶校準帶 ≠ daemon 帶 → 不可用);
    #          舊版 cache (只有頂部原點 y_pct/h_pct 欄, 無 regions 欄) 一律視為不對版 → miss
    #          (語意切換後舊 cache 帶位不可信, fail-soft 重跑);
    #          skipped stub (adaptive density 跳過的幀) 預設當「確定無字幕」回 "" (中間幀夠用),
    #          treat_skipped_as_miss=True 時回 None (主 tile 要準確 → caller 自己補 OCR)
    """
    cpath = cache_path_for(cache_dir, frame_path)
    try:
        with cpath.open("r", encoding="utf-8") as f:
            data = json.load(f)
        if abs(float(data.get("mtime", -1)) - mtime) > MTIME_TOLERANCE_SEC:
            return None    # ring buffer 已覆寫此槽位, cache 是舊畫面的 → stale
        if data.get("skipped"):
            return None if treat_skipped_as_miss else ""
        if regions is not None:
            # 缺 regions 欄 = 舊版 (頂部原點語意) cache — 明確判 miss, 不可讓 normalize 兜底預設帶誤判對版
            if not isinstance(data.get("regions"), list):
                return None
            if not _regions_match(data.get("regions"), regions):
                return None    # caller 要的字幕帶位置跟 cache 產出時不同 → 不對版
        return str(data.get("text", ""))
    except (OSError, json.JSONDecodeError, ValueError, TypeError):
        return None


class OcrWorkerPool:
    """daemon 內嵌的並行 OCR worker pool — submit(frame_path) 即回, 背景產 cache json.

    # 區塊職責: queue + N worker threads 生命週期管理; daemon main loop 只碰 submit()/stop()
    # 物理意義: worker 取 task → 驗 frame mtime 未變 (沒被 ring 覆寫) → OCR → atomic 寫 json
    # 數值影響: submit 是 O(1) 非阻塞 (queue 滿丟最舊); stop() join 最多等 5s/worker
    """

    def __init__(self, cache_dir: Path,
                 regions=None,
                 min_confidence: float = DEFAULT_MIN_CONF,
                 workers: int = 2,
                 max_backlog: int = 64,
                 adaptive: bool = True):
        self.cache_dir = Path(cache_dir)
        # regions = [(y_bottom_pct, h_pct), ...] 底部原點 (見模組頂座標語意); 建構時即正規化定形
        # ⚠ **明確給空清單 = 使用者把所有帶都關了**，不可再落回 normalize_regions 的預設單帶 ——
        #   那條保護是給「垃圾輸入」用的，用在這裡會變成「畫面上關著、實際照掃」。
        #   兩者的差別是 None（沒給 → 用預設）vs []（給了、而且是空的 → 什麼都不掃）。
        self.regions = [] if regions == [] else normalize_regions(regions)
        self.min_confidence = min_confidence
        self.workers = max(1, int(workers))
        self._queue: "queue.Queue" = queue.Queue(maxsize=max_backlog)
        self._threads: list = []
        self._stop_evt = threading.Event()
        # 統計 (daemon log 用) — processed/dropped/errors 都只增不減
        self.processed = 0
        self.dropped = 0
        self.errors = 0
        self.last_error: Optional[str] = None
        # T-OCR-Watermark (Tim 2026-06-10 拍板「montage 只讀到字幕已生成的部分」)
        # 區塊職責: 低水位追蹤 — watermark = 「此 mtime (含) 之前的 frame 字幕 cache 都已就緒」
        # 物理意義: 2 workers 完成順序近似 FIFO 但非嚴格 → watermark 取 min(in-flight) - ε;
        #          無 in-flight 時 = max(已完成)。montage 端窗口 clamp ≤ watermark 即保證全 cache 命中。
        # 數值影響: 每完成一筆 task 更新 + throttle 0.5s 寫 _status.json (atomic, ~200B)
        self._lock = threading.Lock()
        self._pending: dict = {}        # {(frame_name, mtime): mtime} — submitted 但還沒完成的 tasks
        self._max_done_mtime = 0.0
        self._max_done_frame = ""
        self.watermark = 0.0
        self._last_status_write = 0.0
        # T-OCR-AdaptiveDensity — lag 自適應跳幀 (詳見模組頂 ADAPTIVE_* 常數說明)
        self.adaptive = adaptive
        self.stride = 1                 # 當前密度 stride (1=全幀; status 檔曝光給觀測)
        self._submit_seq = 0            # submit 計數器 — % stride 決定跳/不跳
        self.skipped = 0                # 累計被 adaptive 跳過的幀數 (觀測用)

    def start(self) -> None:
        """起 N 條 worker threads (daemon thread, 主程序退出不被擋)."""
        self._stop_evt.clear()
        for i in range(self.workers):
            t = threading.Thread(target=self._worker_loop, name=f"ocr-worker-{i}", daemon=True)
            t.start()
            self._threads.append(t)

    def submit(self, frame_path: Path) -> None:
        """main loop 每寫一張 frame 呼叫一次; 非阻塞, queue 滿丟最舊 task.

        adaptive=True 時依 lag 自動跳幀: 被跳的幀寫 stub cache (skipped=true) 即回,
        watermark 照常推進 — 跳幀是「降密度」不是「漏幀」, montage 端有對應語意。
        """
        try:
            mtime = frame_path.stat().st_mtime
        except OSError:
            return

        # T-OCR-AdaptiveDensity — per-submit 即時算 stride (lag 每超 STEP 秒升一級, 封頂 MAX)
        if self.adaptive:
            lag = max(0.0, mtime - self.watermark) if self.watermark > 0 else 0.0
            self.stride = min(ADAPTIVE_MAX_STRIDE, 1 + int(lag // ADAPTIVE_LAG_STEP_SEC))
            self._submit_seq += 1
            if self.stride > 1 and (self._submit_seq % self.stride) != 0:
                # 跳過此幀: 寫 stub cache + 直接推進水位 (不進 queue 不佔 worker)
                self._write_skip_stub(frame_path, mtime)
                self.skipped += 1
                self._task_done(frame_path, mtime)
                return

        task = (frame_path, mtime)
        with self._lock:
            self._pending[(frame_path.name, mtime)] = mtime
        try:
            self._queue.put_nowait(task)
        except queue.Full:
            # backlog 滿 → 丟最舊保最新 (直播場景新幀 > 舊幀)
            try:
                old_task = self._queue.get_nowait()
                self.dropped += 1
                self._untrack(old_task)
            except queue.Empty:
                pass
            try:
                self._queue.put_nowait(task)
            except queue.Full:
                self.dropped += 1
                self._untrack(task)

    def stop(self, timeout: float = 5.0) -> None:
        """停止 workers (toggle off / daemon 退出時); 殘留 queue task 直接放棄."""
        self._stop_evt.set()
        for t in self._threads:
            t.join(timeout=timeout)
        self._threads.clear()

    # --- internal ---

    def _worker_loop(self) -> None:
        """worker thread 主迴圈 — 每條 thread 自帶一個 RapidOCR engine."""
        engine = None
        try:
            # T-OCR-CPU-Fix — 必走 _make_engine 縮池; 零參數 RapidOCR() 是 99% CPU 事故根因, 禁用
            engine = _make_engine()
        except Exception as e:
            self.last_error = f"worker engine init fail: {e}"
            self.errors += 1
            return
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        while not self._stop_evt.is_set():
            try:
                frame_path, mtime = self._queue.get(timeout=0.5)
            except queue.Empty:
                continue
            try:
                self._process_one(engine, frame_path, mtime)
                self.processed += 1
            except Exception as e:
                self.errors += 1
                self.last_error = str(e)
            finally:
                # 不論成功/skip/exception 都推進水位 — 該 task 不會再產 cache, 卡著只會讓 watermark 停滯
                self._task_done(frame_path, mtime)

    def _process_one(self, engine, frame_path: Path, mtime: float) -> None:
        """單張 frame: 驗 mtime 未被 ring 覆寫 → OCR → atomic 寫 cache json."""
        try:
            current_mtime = frame_path.stat().st_mtime
        except OSError:
            return    # frame 已消失 (daemon 重啟清場等), 放棄
        if abs(current_mtime - mtime) > MTIME_TOLERANCE_SEC:
            return    # 排隊期間被 ring buffer 覆寫成新畫面 → 這筆 task 過期, 放棄
        text = _ocr_regions_with_engine(engine, frame_path, self.regions,
                                        self.min_confidence)
        payload = {
            "mtime": mtime,
            "text": text,
            "regions": [list(r) for r in self.regions],   # [(y_bottom, h, x_center, w), ...] — 讀取端驗帶位對版
            "ocr_at": time.time(),
        }
        cpath = cache_path_for(self.cache_dir, frame_path)
        tmp = cpath.with_suffix(".json.tmp")
        try:
            tmp.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
            os.replace(tmp, cpath)
        except OSError:
            pass    # 寫 cache 失敗不致命 — montage 端 fallback inline OCR

    def _write_skip_stub(self, frame_path: Path, mtime: float) -> None:
        """adaptive 跳幀時寫 stub cache json — montage 端讀到 skipped=true 知道「沒辨識而非無字幕」."""
        payload = {"mtime": mtime, "text": "", "skipped": True, "ocr_at": time.time()}
        try:
            self.cache_dir.mkdir(parents=True, exist_ok=True)
            cpath = cache_path_for(self.cache_dir, frame_path)
            tmp = cpath.with_suffix(".json.tmp")
            tmp.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
            os.replace(tmp, cpath)
        except OSError:
            pass    # stub 寫失敗 → montage 端 cache miss fallback, 不致命

    # --- watermark 追蹤 (T-OCR-Watermark) ---

    def _untrack(self, task) -> None:
        """task 被 drop (不會處理) → 從 pending 移除, 水位照常推進."""
        frame_path, mtime = task
        self._task_done(frame_path, mtime)

    def _task_done(self, frame_path: Path, mtime: float) -> None:
        """task 完結 (處理完 / skip / drop) → 更新 watermark + throttled 寫 _status.json."""
        with self._lock:
            self._pending.pop((frame_path.name, mtime), None)
            if mtime > self._max_done_mtime:
                self._max_done_mtime = mtime
                self._max_done_frame = frame_path.stem
            # 低水位: 有 in-flight → 取最舊 in-flight 再往前一點; 全清空 → 最新完成的就是水位
            if self._pending:
                self.watermark = min(self._pending.values()) - 0.001
            else:
                self.watermark = self._max_done_mtime
            now = time.time()
            # throttle 0.5s — 但 pending 清空 (追上即時) 時必寫, 讓 montage 端拿到最新水位
            if now - self._last_status_write < 0.5 and self._pending:
                return
            self._last_status_write = now
            self._write_status_locked(now)

    def _write_status_locked(self, now: float) -> None:
        """atomic 寫 _status.json (caller 須持 _lock)."""
        # frame index 從檔名 frame_NNNN 取尾碼 (Tim 拍板「額外紀錄目前字幕幀 index」)
        try:
            last_idx = int(self._max_done_frame.rsplit("_", 1)[-1])
        except (ValueError, IndexError):
            last_idx = -1
        payload = {
            "watermark_mtime": self.watermark,
            "max_done_mtime": self._max_done_mtime,
            "last_frame": self._max_done_frame,
            "last_frame_index": last_idx,
            "pending": len(self._pending),
            "processed": self.processed,
            "dropped": self.dropped,
            "errors": self.errors,
            "stride": self.stride,          # adaptive density 當前檔位 (1=全幀)
            "skipped": self.skipped,        # 累計 adaptive 跳幀數
            "updated_at": now,
        }
        spath = self.cache_dir / STATUS_FILENAME
        tmp = spath.with_suffix(".json.tmp2")
        try:
            tmp.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
            os.replace(tmp, spath)
        except OSError:
            pass    # 狀態檔寫失敗不致命 — montage 端視為無 watermark (不 clamp)


# ===========================================================
# CLI (debug standalone)
# ===========================================================


def _serve(args) -> int:
    """常駐模式主迴圈 — 掃目錄 → submit → 印心跳。停滯判定/重起交給 C# supervisor。"""
    frames_dir = Path(args.frames_dir)
    cache_dir = Path(args.cache_dir)
    if not args.frames_dir or not args.cache_dir:
        print("[ocr-serve] ✗ --serve 需要 --frames-dir 與 --cache-dir (由 C# 傳入)", flush=True)
        return 1
    if not frames_dir.exists():
        print(f"[ocr-serve] ✗ frames 目錄不存在: {frames_dir}", flush=True)
        return 1
    regions = [(args.y_bottom_pct, args.h_pct)]
    if args.extra_regions:
        try:
            regions += [tuple(r) for r in json.loads(args.extra_regions)]
        except (ValueError, TypeError) as e:
            print(f"[ocr-serve] ✗ --extra-regions 解析失敗: {e}", flush=True)
            return 1
    # 起手先把「我拿到什麼設定」印出來 —— C# 傳錯時這一行是唯一的對帳點。
    print(f"[ocr-serve] frames={frames_dir} cache={cache_dir} workers={args.workers} "
          f"min_conf={args.min_confidence} adaptive={not args.no_adaptive} regions={regions}", flush=True)
    if not is_available():
        # 壞要往吵的方向壞: 引擎沒裝好就**立刻非零退出**, 不起一個永遠不產出的行程。
        print(f"[ocr-serve] ✗ RapidOCR 不可用: {get_init_error()}", flush=True)
        return 2

    pool = OcrWorkerPool(cache_dir, regions=regions,
                         min_confidence=args.min_confidence,
                         workers=args.workers, adaptive=not args.no_adaptive)
    pool.start()
    # cursor = 只吃比它新的 frame。預設 = 啟動時刻 (不回補整個 ring buffer, 否則一起手就落後幾十分鐘)。
    cursor = time.time() - max(0.0, float(args.backfill_sec))
    print(f"[ocr-serve] started — cursor={time.strftime('%H:%M:%S', time.localtime(cursor))} "
          f"(backfill {args.backfill_sec:.0f}s)", flush=True)
    last_beat = 0.0
    try:
        while True:
            try:
                batch = []
                for f in frames_dir.glob("*.jpg"):
                    try:
                        mt = f.stat().st_mtime
                    except OSError:
                        continue
                    if mt > cursor:
                        batch.append((mt, f))
                batch.sort()
                for mt, f in batch:
                    pool.submit(f)
                    cursor = max(cursor, mt)   # cursor 只前進, 且只跟著**實際 submit 過**的 frame 走
            except Exception as e:
                print(f"[ocr-serve] ⚠ 掃描失敗 (續跑): {e}", flush=True)
            now = time.time()
            if now - last_beat >= 30.0:
                last_beat = now
                print(f"[ocr-serve] processed={pool.processed} skipped={pool.skipped} "
                      f"dropped={pool.dropped} errors={pool.errors} stride={pool.stride} "
                      f"watermark={time.strftime('%H:%M:%S', time.localtime(pool.watermark)) if pool.watermark else '(無)'}",
                      flush=True)
            time.sleep(max(0.2, float(args.poll_sec)))
    except KeyboardInterrupt:
        pass
    finally:
        pool.stop()
    print(f"[ocr-serve] stopped (processed={pool.processed})", flush=True)
    return 0


def main():
    ap = argparse.ArgumentParser(description="PaddleOCR 字幕帶 OCR helper.")
    ap.add_argument("frame", nargs="?", help="原始 frame 路徑 (單張 debug 用; --serve 時不需要)")
    # ── serve: 常駐模式 (2026-08-15 遷移階段 2) ──────────────────────
    # 物理意義: 原本 OCR 是 screenstream_daemon 內的 thread pool, 由 capture loop 每寫一張 frame
    #   submit 一次 (記憶體交棒)。拆成獨立行程後改**掃目錄**: 自帶 cursor, 只吃比 cursor 新的 frame。
    # ⚠ 本行程**不讀 config、不做 repo-walk** — frames/cache 目錄與 regions/conf/workers 全部由
    #   C# (UCL_OcrWorkerSupervisor) 顯式傳入。設定的事實源只有一個, python 這端不要有第二份解讀。
    # ⚠ 也**不自我重起**: 停滯判定與重起是 C# 的職責 (同 audio_transcribe serve 的分工)。
    # 行為差異 (刻意, 非疏漏): 舊版 capture loop 會對敏感畫面跳過 submit; 掃目錄版看不到那個旗標,
    #   但敏感時**寫進磁碟的本來就是黑畫面** (daemon 端 make_blackout_image 直接換掉 img),
    #   所以讀不到敏感內容 — 代價只是多 OCR 幾張黑圖 (近乎空結果)。
    ap.add_argument("--serve", action="store_true", help="常駐模式: 掃 frames 目錄持續產字幕 cache")
    ap.add_argument("--frames-dir", default="", help="serve: frame 來源目錄 (由 C# 傳入)")
    ap.add_argument("--cache-dir", default="", help="serve: 字幕 cache 輸出目錄 (由 C# 傳入)")
    ap.add_argument("--workers", type=int, default=2, help="serve: worker 執行緒數")
    ap.add_argument("--no-adaptive", action="store_true", help="serve: 關掉 lag 自適應跳幀")
    ap.add_argument("--poll-sec", type=float, default=1.0, help="serve: 掃目錄間隔")
    ap.add_argument("--backfill-sec", type=float, default=0.0,
                    help="serve: 起手回補最近 N 秒的既有 frame (預設 0 = 只吃啟動後的新 frame)")
    ap.add_argument("--y-bottom-pct", type=float, default=DEFAULT_Y_BOTTOM_PCT,
                    help=f"字幕帶底邊離畫面下緣距離 (比例 0~1, 0=貼底, 預設 {DEFAULT_Y_BOTTOM_PCT})")
    ap.add_argument("--h-pct", type=float, default=DEFAULT_H_PCT,
                    help=f"字幕帶高度比例, 從底邊往上長 (預設 {DEFAULT_H_PCT})")
    ap.add_argument("--extra-regions", type=str, default="",
                    help='額外字幕判定區域 JSON, 例: [[0.85,0.1]] (各項 [y_bottom_pct, h_pct])')
    ap.add_argument("--min-confidence", type=float, default=0.5,
                    help="過濾低信度 (預設 0.5)")
    args = ap.parse_args()

    if args.serve:
        return _serve(args)

    if not args.frame:
        print("ERROR: 需要 frame 路徑 (或用 --serve 進常駐模式)", file=sys.stderr)
        return 1
    frame_path = Path(args.frame)
    if not frame_path.exists():
        print(f"ERROR: frame not found: {frame_path}", file=sys.stderr)
        return 1

    if not is_available():
        err = get_init_error() or "unknown"
        print(f"ERROR: RapidOCR 不可用 — {err}", file=sys.stderr)
        return 2

    regions = [(args.y_bottom_pct, args.h_pct)]
    if args.extra_regions:
        try:
            regions += [tuple(r) for r in json.loads(args.extra_regions)]
        except (ValueError, TypeError) as e:
            print(f"ERROR: --extra-regions JSON 解析失敗: {e}", file=sys.stderr)
            return 1
    text = ocr_subtitle_regions(frame_path,
                                regions=regions,
                                min_confidence=args.min_confidence)
    if text:
        print(text)
    else:
        print("(no subtitle detected)", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
