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

CLI 用法 (standalone debug):
  python subtitle_ocr.py <frame_path> [--y-pct 0.85] [--h-pct 0.13]
  → stdout 印 OCR 結果一行

import 用法 (給 montage 等 caller):
  from subtitle_ocr import ocr_subtitle_band, is_available
  if is_available():
      text = ocr_subtitle_band(Path("frame.jpg"), y_pct=0.78, h_pct=0.12)
      # text: "字幕內容" 或 "" (空字串 = 該幀無字幕 / OCR 沒讀到)

daemon-side 並行 cache 用法 (T-OCR-Pipeline, Tim 2026-06-10 拍板「錄製時就自動產生, 多執行緒並行」):
  pool = OcrWorkerPool(cache_dir, y_pct=0.78, h_pct=0.12, workers=2)
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

# 預設字幕帶位置 (2026-06-10 Tim ground-truth 校準: 1080p 字幕實測 y≈860~950px → 0.78~0.90)
# 物理意義: 舊預設 0.85/0.13 起裁點過低, 會切掉字幕上半 + 混進底部 audio viz strip → 0 hits
DEFAULT_Y_PCT = 0.78
DEFAULT_H_PCT = 0.12
DEFAULT_MIN_CONF = 0.5

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


def get_init_error() -> Optional[str]:
    """給 caller 用的 debug — 若 OCR 不可用, 印這個錯讓 user 知道為何."""
    if not _ocr_init_attempted:
        _try_init()
    return _ocr_init_error


# ===========================================================
# Core OCR function
# ===========================================================


def ocr_subtitle_band(
    frame_path: Path,
    y_pct: float = DEFAULT_Y_PCT,
    h_pct: float = DEFAULT_H_PCT,
    min_confidence: float = DEFAULT_MIN_CONF,
) -> str:
    """從一張 frame 裁字幕帶 + 跑 OCR → 回字串.

    # 區塊職責：給 caller 一個「丟 frame 路徑回字幕文字」的 one-call API
    # 物理意義：y_pct/h_pct 是字幕帶位置比例 (0~1, 解析度無關); 預設 0.85,0.13 = 底部 13% (Vivy 字幕常用位置).
    #          字幕辨識率隨字體大小急降, 故必走原始 frame, 別用縮圖牆 tile.
    # 數值影響：min_confidence 過濾低信度文字塊 (預設 0.5); 同一幀多行字幕用換行 (\n) 串接;
    #          OCR 失敗 / 該幀無字幕 → 回 ""  (空字串, 對齊 caller 期望單一字串型別).
    """
    if not is_available():
        return ""
    return _ocr_band_with_engine(_ocr_engine, frame_path, y_pct, h_pct, min_confidence)


def _crop_band_to_array(frame_path: Path, y_pct: float, h_pct: float):
    """裁字幕帶 → numpy array; 任何失敗回 None.

    # 區塊職責: ocr_subtitle_band / OcrWorkerPool 共用的 crop 前處理
    # 物理意義: y_pct/h_pct 比例制 (解析度無關); clamp 防越界
    """
    try:
        from PIL import Image
        import numpy as np
    except Exception:
        return None
    try:
        with Image.open(frame_path) as img:
            w, h = img.size
            y0 = int(h * y_pct)
            y1 = min(int(h * (y_pct + h_pct)), h)
            if y1 <= y0:
                return None
            crop = img.crop((0, y0, w, y1))
            return np.array(crop.convert("RGB"))
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
            if conf is None or conf < min_confidence:
                continue
            if not text or not text.strip():
                continue
            top_y = min(p[1] for p in box)
            entries.append((top_y, text.strip()))
        entries.sort(key=lambda t: t[0])
        return "\n".join(t for _, t in entries)
    except Exception:
        return ""


def _ocr_band_with_engine(engine, frame_path: Path, y_pct: float, h_pct: float,
                          min_confidence: float) -> str:
    """指定 engine 跑「crop 字幕帶 → OCR → 字串」(pool worker 跟全域單例共用此核心)."""
    arr = _crop_band_to_array(frame_path, y_pct, h_pct)
    if arr is None:
        return ""
    try:
        # RapidOCR API: 回 (results, elapse) — results = [ [box, text, confidence], ... ] 或 None
        result, _elapse = engine(arr)
    except Exception:
        return ""
    return _parse_ocr_result(result, min_confidence)


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


def read_cached_text(cache_dir: Path, frame_path: Path, mtime: float,
                     y_pct: Optional[float] = None,
                     h_pct: Optional[float] = None,
                     treat_skipped_as_miss: bool = False) -> Optional[str]:
    """讀 daemon 預產 OCR cache. 命中回 text (可為空字串=無字幕), miss/stale 回 None.

    # 物理意義: None vs "" 語意嚴格區分 — None=caller 該 fallback 自己 OCR; ""=確定無字幕;
    #          caller 傳 y_pct/h_pct 時驗 cache 產出帶位一致 (caller 自帶校準帶 ≠ daemon 帶 → 不可用);
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
        if y_pct is not None and abs(float(data.get("y_pct", -1)) - y_pct) > BAND_TOLERANCE:
            return None    # caller 要的字幕帶位置跟 cache 產出時不同 → 不對版
        if h_pct is not None and abs(float(data.get("h_pct", -1)) - h_pct) > BAND_TOLERANCE:
            return None
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
                 y_pct: float = DEFAULT_Y_PCT,
                 h_pct: float = DEFAULT_H_PCT,
                 min_confidence: float = DEFAULT_MIN_CONF,
                 workers: int = 2,
                 max_backlog: int = 64,
                 adaptive: bool = True):
        self.cache_dir = Path(cache_dir)
        self.y_pct = y_pct
        self.h_pct = h_pct
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
        text = _ocr_band_with_engine(engine, frame_path, self.y_pct, self.h_pct,
                                     self.min_confidence)
        payload = {
            "mtime": mtime,
            "text": text,
            "y_pct": self.y_pct,
            "h_pct": self.h_pct,
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


def main():
    ap = argparse.ArgumentParser(description="PaddleOCR 字幕帶 OCR helper.")
    ap.add_argument("frame", help="原始 frame 路徑 (1080p 等高解析度)")
    ap.add_argument("--y-pct", type=float, default=DEFAULT_Y_PCT,
                    help=f"字幕帶上邊位置 (比例 0~1, 預設 {DEFAULT_Y_PCT}, 2026-06-10 Tim 實測校準)")
    ap.add_argument("--h-pct", type=float, default=DEFAULT_H_PCT,
                    help=f"字幕帶高度比例 (預設 {DEFAULT_H_PCT})")
    ap.add_argument("--min-confidence", type=float, default=0.5,
                    help="過濾低信度 (預設 0.5)")
    args = ap.parse_args()

    frame_path = Path(args.frame)
    if not frame_path.exists():
        print(f"ERROR: frame not found: {frame_path}", file=sys.stderr)
        return 1

    if not is_available():
        err = get_init_error() or "unknown"
        print(f"ERROR: RapidOCR 不可用 — {err}", file=sys.stderr)
        return 2

    text = ocr_subtitle_band(frame_path,
                             y_pct=args.y_pct,
                             h_pct=args.h_pct,
                             min_confidence=args.min_confidence)
    if text:
        print(text)
    else:
        print("(no subtitle detected)", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
