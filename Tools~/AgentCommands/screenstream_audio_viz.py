#!/usr/bin/env python3
"""screenstream_audio_viz.py — 音訊可視化 overlay (Audio Spectrogram).

# 區塊職責: 抓 system audio loopback → FFT log-scale bands → 推 column ring →
#          render spectrogram strip 疊進 screen capture frame 右下角
# 物理意義: agent 1 fps 截圖只看到一瞬, spectrogram 把過去 5 秒音訊壓進一張靜態圖
#          (X=time / Y=freq / Color=volume), 等於補一個 audio modality 給視覺通道
# 數值影響: AudioCapture thread ~1-2% CPU, overlay render ~0.5% CPU, +5MB RAM (ring buffer)

設計依據: docs/Plan/Plan_ScreenStream_AudioViz.md (Tim 2026-06-08 拍板, summit 自決樣式)
作者: summit (Zeta-da-xiaojie), ship 2026-06-08

依賴:
  - numpy (FFT)
  - PIL.Image / PIL.ImageDraw (overlay render)
  - soundcard (WASAPI loopback, 只在 AudioCapture 用 — 跑 mock-up 可不裝)

CLI 自驗:
  python screenstream_audio_viz.py mock     # 用假資料測 render, 輸出 _viz_mock.png
  python screenstream_audio_viz.py live 10  # 抓 10 秒真實音訊輸出 _viz_live.png
"""
from __future__ import annotations

import math
import sys
import threading
import time
from collections import deque
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

# ===========================================================
# 預設參數 (對應 Plan doc spec)
# ===========================================================
DEFAULT_SAMPLE_RATE = 44100      # WASAPI loopback 通常 44.1k/48k
DEFAULT_FFT_SIZE = 2048          # ~46ms @ 44.1k, 解析度約 21Hz/bin
DEFAULT_BANDS = 24               # log bins 數 (Tim 2026-06-08: 60→30→24, 配合更壓低高度)
DEFAULT_COLUMNS = 120            # 120 columns × 200ms = 24 sec history (Tim 2026-06-08 改長時間軸鋪底全寬)
DEFAULT_LOG_SECONDS = 600        # disk dump 用 log ring 長度 (10 min, 對齊 frame ring buffer)
DEFAULT_WINDOW_SEC = 0.2         # 每 200ms 推一 column
DEFAULT_FREQ_MIN = 60.0          # Hz, 下限
DEFAULT_FREQ_MAX = 16000.0       # Hz, 上限
DEFAULT_SIZE_1080P = (400, 90)   # px, spectrogram viz strip 尺寸 @ 1080p
DEFAULT_STEREO_EQ_SIZE_1080P = (1880, 50)  # px, stereo spectrogram 鋪底全寬, 24 bands × 2.08 px 進一步壓低避免遮中文字幕
DEFAULT_PADDING = 12             # px, 距畫面邊界
DEFAULT_BG_ALPHA = 180           # 半透明黑底
DEFAULT_BORDER_ALPHA = 220       # 邊框白
DEFAULT_BORDER_WIDTH = 2         # 邊框寬度
DEFAULT_PEAK_DECAY_DB_PER_TICK = 3.0  # peak hold dB 每 200ms tick 衰減量 (0.5s ≈ 7.5dB)

# Magma-like 5-stop colormap (normalize magnitude 0~1 → RGB)
# 物理意義: 亮度單調對應強度, agent 視覺判讀友善 (黑→紫→紅→橘→暖白)
COLORMAP_STOPS = [
    (0.0,  ( 8,   4,  30)),
    (0.25, (60,  14, 100)),
    (0.50, (140, 30,  90)),
    (0.75, (220, 100, 40)),
    (1.0,  (252, 220, 160)),
]


def colormap(v: float) -> tuple[int, int, int]:
    """Map normalized magnitude 0~1 to RGB tuple via magma-like gradient."""
    v = max(0.0, min(1.0, v))
    for i in range(len(COLORMAP_STOPS) - 1):
        v0, c0 = COLORMAP_STOPS[i]
        v1, c1 = COLORMAP_STOPS[i + 1]
        if v <= v1:
            t = (v - v0) / (v1 - v0) if v1 > v0 else 0
            return (
                int(c0[0] + (c1[0] - c0[0]) * t),
                int(c0[1] + (c1[1] - c0[1]) * t),
                int(c0[2] + (c1[2] - c0[2]) * t),
            )
    return COLORMAP_STOPS[-1][1]


def build_log_band_edges(sample_rate: int, fft_size: int,
                        bands: int, freq_min: float, freq_max: float) -> np.ndarray:
    """產生 log-scale band 切分 (in FFT bin index space).

    回傳 shape=(bands+1,) 的 bin index 陣列, 第 i 個 band 對應 bins[edges[i]:edges[i+1]].
    物理意義: log scale 對應人耳聽感, 低頻細緻、高頻寬鬆.
    """
    bin_hz = sample_rate / fft_size
    log_min = math.log10(freq_min)
    log_max = math.log10(freq_max)
    edges_hz = np.logspace(log_min, log_max, bands + 1)
    edges_bin = np.clip(np.round(edges_hz / bin_hz).astype(int), 0, fft_size // 2)
    # 確保單調遞增 (低頻 band 太細 + 取整可能撞)
    for i in range(1, len(edges_bin)):
        if edges_bin[i] <= edges_bin[i - 1]:
            edges_bin[i] = edges_bin[i - 1] + 1
    return edges_bin


def compute_fft_db(samples: np.ndarray, edges: np.ndarray,
                   silence_db: float = -120.0) -> np.ndarray:
    """從 raw samples 算出 log-scale band 的 raw dB magnitude (未 normalize).

    用於 auto-AGC 模式: AudioCapture 推 raw dB 進 ring, snapshot() 動態 normalize.

    Returns:
        shape=(bands,) raw dB values (typically -120 ~ 0 range)
    """
    if len(samples) == 0:
        return np.full(len(edges) - 1, silence_db, dtype=np.float32)

    n = len(samples)
    window = np.hanning(n)
    spec = np.fft.rfft(samples * window)
    power = np.abs(spec) ** 2 + 1e-12

    bands_db = np.empty(len(edges) - 1, dtype=np.float32)
    for i in range(len(edges) - 1):
        s, e = edges[i], edges[i + 1]
        if e <= s:
            bands_db[i] = silence_db
        else:
            bands_db[i] = 10.0 * math.log10(float(power[s:e].mean()))
    return bands_db


def compute_fft_column(samples: np.ndarray, edges: np.ndarray,
                      noise_floor_db: float = -55.0,
                      top_db: float = 0.0) -> np.ndarray:
    """從 raw samples 算出 normalized log-scale band magnitudes.

    Args:
        samples: shape=(N,) float32, mono (stereo 應已 downmix)
        edges: shape=(bands+1,) FFT bin index 切分
        noise_floor_db: 視為 0 強度的 dB 下限
        top_db: 視為 1 強度的 dB 上限

    Returns:
        shape=(bands,) normalize 0~1 magnitude
    物理意義: power → dB → 線性 normalize, 對齊 colormap 0~1 input space.
    """
    if len(samples) == 0:
        return np.zeros(len(edges) - 1, dtype=np.float32)

    # Hann window 降頻譜洩漏
    n = len(samples)
    window = np.hanning(n)
    spec = np.fft.rfft(samples * window)
    power = np.abs(spec) ** 2 + 1e-12

    # 各 band 取 power 平均 → dB
    bands_db = np.empty(len(edges) - 1, dtype=np.float32)
    for i in range(len(edges) - 1):
        s, e = edges[i], edges[i + 1]
        if e <= s:
            bands_db[i] = noise_floor_db
        else:
            bands_db[i] = 10.0 * math.log10(float(power[s:e].mean()))

    # Normalize: noise_floor → 0, top → 1
    norm = (bands_db - noise_floor_db) / (top_db - noise_floor_db)
    return np.clip(norm, 0.0, 1.0)


# ===========================================================
# AudioCapture — WASAPI loopback 抓 + FFT thread
# ===========================================================
class AudioCapture:
    """背景 thread 抓 loopback audio, 每 window_sec 推一個 FFT column 進 ring.

    物理意義: daemon main loop 隨時 snapshot() 拿最新 5 秒頻譜矩陣.
    Thread safety: ring 用 deque + lock, snapshot 回傳 numpy copy.
    """

    def __init__(self,
                 sample_rate: int = DEFAULT_SAMPLE_RATE,
                 fft_size: int = DEFAULT_FFT_SIZE,
                 bands: int = DEFAULT_BANDS,
                 columns: int = DEFAULT_COLUMNS,
                 window_sec: float = DEFAULT_WINDOW_SEC,
                 freq_min: float = DEFAULT_FREQ_MIN,
                 freq_max: float = DEFAULT_FREQ_MAX,
                 # Auto-AGC: snapshot 時用 ring 內 percentile 當 top, top-range_db 當 bottom
                 dynamic_range_db: float = 45.0,
                 top_headroom_db: float = 3.0,
                 silence_floor_db: float = -65.0,
                 top_percentile: float = 95.0,
                 # T-AudioLog (Tim 2026-06-08): 額外 log ring 給 disk dump, 給 montage 取整區段 audio
                 # 物理意義: viz _ring 24s 只夠當下 overlay; log _ring 600s 跟 frame ring buffer 對齊, dump 給 montage
                 # 數值影響: 600s × 5cols/s × (24 bands × 3 通道 × 4B + 8B ts) ≈ 890 KB RAM
                 log_seconds: float = DEFAULT_LOG_SECONDS):
        self.sample_rate = sample_rate
        self.fft_size = fft_size
        self.bands = bands
        self.columns = columns
        self.window_sec = window_sec
        self.freq_min = freq_min
        self.freq_max = freq_max
        # Auto-AGC params (2026-06-08 ship: 改自適應後黑線=真實靜音, 但動態範圍 squeeze 解了)
        self.dynamic_range_db = dynamic_range_db   # top 到 bottom 跨度 (e.g. 45 dB)
        self.top_headroom_db = top_headroom_db     # top = ring_max + headroom (留 transient buffer)
        self.silence_floor_db = silence_floor_db   # ring_max 低於此值時用此值當 baseline (防全靜音段亂 normalize)
        self.top_percentile = top_percentile       # 用 percentile (95) 而非 max, 抗單一 transient outlier
        self.edges = build_log_band_edges(sample_rate, fft_size, bands, freq_min, freq_max)
        self._ring = deque(maxlen=columns)  # 每 entry shape=(2, bands) dB (L,R)
        self._peak_db = np.full(bands, -120.0, dtype=np.float32)  # per-band peak hold (mono)
        # T-AudioLog: 平行 deque 紀錄整個 log_seconds 的 (timestamp, L_db, R_db, peak_db)
        log_cols = int(math.ceil(log_seconds / window_sec))
        self.log_cols = log_cols
        self._log_ring = deque(maxlen=log_cols)        # (3, bands) ndarray 每 entry
        self._log_timestamps = deque(maxlen=log_cols)  # epoch float 每 entry
        self._lock = threading.Lock()
        self._stop_event = threading.Event()
        self._thread: threading.Thread | None = None
        self._error: str | None = None

    def start(self) -> None:
        if self._thread and self._thread.is_alive():
            return
        self._stop_event.clear()
        self._thread = threading.Thread(target=self._loop, name="AudioCapture", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop_event.set()
        if self._thread:
            self._thread.join(timeout=2.0)

    def _auto_agc_range(self, all_db: np.ndarray) -> tuple[float, float]:
        """根據 dB 群體 percentile + silence anchor 算 (top_db, bottom_db) for normalize."""
        try:
            p_top = float(np.percentile(all_db, self.top_percentile))
        except Exception:
            p_top = float(all_db.max())
        top_db = max(p_top + self.top_headroom_db, self.silence_floor_db + self.dynamic_range_db)
        bottom_db = top_db - self.dynamic_range_db
        return top_db, bottom_db

    def _gather_ring(self) -> np.ndarray:
        """Return shape=(columns, 3, bands), 補零未滿 ring."""
        with self._lock:
            cols = list(self._ring)
        if len(cols) < self.columns:
            pad = [np.full((3, self.bands), -120.0, dtype=np.float32)] * (self.columns - len(cols))
            cols = pad + cols
        return np.stack(cols, axis=0)  # shape=(columns, 3, bands)

    def snapshot(self) -> np.ndarray:
        """Return shape=(columns, bands) normalized 0~1 mono spectrogram (BACKWARDS COMPAT).

        Mono envelope = max(L, R), auto-AGC normalize. 給舊 magma spectrogram render 用.
        """
        triple_arr = self._gather_ring()  # (columns, 3, bands)
        # Mono envelope: max(L, R), 第 0+1 通道
        mono_db = np.max(triple_arr[:, :2, :], axis=1)  # shape=(columns, bands)
        top_db, bottom_db = self._auto_agc_range(mono_db)
        norm = (mono_db - bottom_db) / (top_db - bottom_db)
        return np.clip(norm, 0.0, 1.0).astype(np.float32)

    def snapshot_stereo(self) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
        """Return (L_matrix, R_matrix, peak_matrix), 各 shape=(columns, bands) normalized 0~1.

        為 stereo spectrogram viz (Tim 2026-06-08): X=time, Y=freq, RGB=(L,R,peak).
        L/R 兩通道一起算 normalize range, peak 獨立算 — 避免 peak 衰減慢主導 B 通道過亮.
        """
        triple_arr = self._gather_ring()  # (columns, 3, bands)

        # L/R 兩通道一起算 percentile range (它們 dB 範圍接近, 一起 normalize 才公平)
        lr_db = triple_arr[:, :2, :].flatten()
        lr_top, lr_bottom = self._auto_agc_range(lr_db)

        # Peak 單獨算 range (衰減慢 → 平均比 L/R 高, 分開 normalize 避免 B 過亮)
        peak_db = triple_arr[:, 2, :].flatten()
        peak_top, peak_bottom = self._auto_agc_range(peak_db)

        def _norm(arr, top, bottom):
            return np.clip((arr - bottom) / (top - bottom), 0.0, 1.0).astype(np.float32)

        L_mat = _norm(triple_arr[:, 0, :], lr_top, lr_bottom)
        R_mat = _norm(triple_arr[:, 1, :], lr_top, lr_bottom)
        peak_mat = _norm(triple_arr[:, 2, :], peak_top, peak_bottom)
        return L_mat, R_mat, peak_mat

    @property
    def error(self) -> str | None:
        return self._error

    # T-AudioLog (Tim 2026-06-08, summit ship): 把 log ring 序列化到 disk
    # 物理意義: montage 不能讀記憶體 ring → daemon 每 N frame 觸發 atomic write npz; montage 讀檔切時段
    # 數值影響: 寫盤 ~1 MB / 600s + atomic 換檔, 對 daemon main loop 影響可忽略
    def dump_log(self, path) -> dict:
        """Atomic 把 log ring 寫成 .npz, 含 timestamps + L/R/peak raw dB 矩陣.

        Args:
            path: str | Path, 落盤位置 (建議 .npz)

        Returns:
            dict 統計: {ok, cols, span_sec, path, error?}
        物理意義: 給 screenstream_montage.py 載入後切時段渲染 audio strip 用.
        """
        from pathlib import Path as _Path
        target = _Path(path)
        try:
            with self._lock:
                cols_list = list(self._log_ring)
                ts_list = list(self._log_timestamps)
            if not cols_list:
                return {"ok": False, "cols": 0, "span_sec": 0.0, "path": str(target), "error": "empty"}
            triple_arr = np.stack(cols_list, axis=0)  # (cols, 3, bands)
            ts_arr = np.array(ts_list, dtype=np.float64)
            target.parent.mkdir(parents=True, exist_ok=True)
            # ⚠ np.savez 會自動加 .npz 副檔名, 用 open() file handle 繞過 (給定 path 字串它會 sniff)
            # 物理意義: tmp 路徑直接 .tmp + open binary write 避免「.tmp.npz」 sneak in 害 rename 找不到
            # 數值影響: savez 寫 .npz binary 進開的 handle, rename .tmp → target 完成 atomic swap
            tmp = target.with_suffix(target.suffix + ".tmp")
            with open(tmp, "wb") as fh:
                np.savez(
                    fh,
                    timestamps=ts_arr,
                    L_db=triple_arr[:, 0, :].astype(np.float32),
                    R_db=triple_arr[:, 1, :].astype(np.float32),
                    peak_db=triple_arr[:, 2, :].astype(np.float32),
                    window_sec=np.float32(self.window_sec),
                    bands=np.int32(self.bands),
                    freq_min=np.float32(self.freq_min),
                    freq_max=np.float32(self.freq_max),
                )
            import os as _os
            _os.replace(tmp, target)
            span = float(ts_arr[-1] - ts_arr[0]) if len(ts_arr) > 1 else 0.0
            return {"ok": True, "cols": len(cols_list), "span_sec": span, "path": str(target)}
        except Exception as e:
            return {"ok": False, "cols": 0, "span_sec": 0.0, "path": str(target), "error": str(e)}

    def _loop(self) -> None:
        try:
            import soundcard as sc
        except ImportError as e:
            self._error = f"soundcard import fail: {e}"
            return

        try:
            # 抓 default speaker 對應的 loopback mic (Windows WASAPI)
            spk = sc.default_speaker()
            mic = sc.get_microphone(spk.name, include_loopback=True)
        except Exception as e:
            self._error = f"loopback mic fetch fail: {e}"
            return

        samples_per_window = int(self.sample_rate * self.window_sec)
        # FFT 用 fft_size 點 — 若 samples_per_window < fft_size 補零, > 則截 tail
        try:
            with mic.recorder(samplerate=self.sample_rate, channels=2, blocksize=samples_per_window) as rec:
                while not self._stop_event.is_set():
                    try:
                        data = rec.record(numframes=samples_per_window)  # shape (N, 2)
                    except Exception as e:
                        self._error = f"record fail: {e}"
                        time.sleep(0.5)
                        continue
                    # Stereo split (不 downmix) — L+R 各算 FFT
                    L_raw = data[:, 0].astype(np.float32)
                    R_raw = data[:, 1].astype(np.float32)

                    def _to_fft_buf(arr):
                        if len(arr) >= self.fft_size:
                            return arr[-self.fft_size:]
                        b = np.zeros(self.fft_size, dtype=np.float32)
                        b[-len(arr):] = arr
                        return b

                    L_db = compute_fft_db(_to_fft_buf(L_raw), self.edges)
                    R_db = compute_fft_db(_to_fft_buf(R_raw), self.edges)

                    # Peak hold: 每 band max(L,R) 當 sample, dB 線性衰減
                    cur_max = np.maximum(L_db, R_db)
                    column_ts = time.time()  # epoch 浮點, 跟 frame.mtime 同 ref
                    with self._lock:
                        self._peak_db = np.maximum(self._peak_db - DEFAULT_PEAK_DECAY_DB_PER_TICK, cur_max)
                        # Ring 存 (3, bands) 每 column 含 (L, R, peak_at_that_moment_snapshot)
                        # peak 拿當時 tracker 的 copy → 每 cell 顯示「該時刻的 peak 殘像」
                        triple_col = np.stack([L_db, R_db, self._peak_db.copy()], axis=0)
                        self._ring.append(triple_col)
                        # T-AudioLog: 平行寫長 ring + 時間戳, 給 montage 載入後可按 cycle 區間 slice
                        self._log_ring.append(triple_col)
                        self._log_timestamps.append(column_ts)
        except Exception as e:
            self._error = f"recorder ctx fail: {e}"


# ===========================================================
# Overlay render
# ===========================================================
def render_spectrogram_strip(cols: np.ndarray,
                              size: tuple[int, int] = DEFAULT_SIZE_1080P,
                              bg_alpha: int = DEFAULT_BG_ALPHA,
                              border_alpha: int = DEFAULT_BORDER_ALPHA,
                              border_width: int = DEFAULT_BORDER_WIDTH,
                              silent_threshold: float = 0.02) -> Image.Image:
    """Render spectrogram column matrix → RGBA PIL Image.

    Args:
        cols: shape=(columns, bands), normalize 0~1
        size: (W, H) px
        silent_threshold: max(cols) 低於此值 → 標示 "♪ silent"

    Returns:
        RGBA PIL image, 可直接 alpha_composite 到 frame.
    物理意義: 把矩陣 render 成 grid bitmap, 一格一 magma 色, 加邊框跟背景.
    """
    W, H = size
    img = Image.new("RGBA", (W, H), (0, 0, 0, bg_alpha))
    draw = ImageDraw.Draw(img)

    if cols.size == 0:
        draw.rectangle([(0, 0), (W - 1, H - 1)], outline=(255, 255, 255, border_alpha), width=border_width)
        return img

    columns_n, bands_n = cols.shape
    # 每 cell 寬高 (浮點數運算後取 round)
    cell_w = W / columns_n
    cell_h = H / bands_n
    is_silent = cols.max() < silent_threshold

    if not is_silent:
        for ci in range(columns_n):
            x0 = int(round(ci * cell_w))
            x1 = int(round((ci + 1) * cell_w))
            for bi in range(bands_n):
                # Y 軸顛倒: 高頻在頂 (bi 大), 低頻在底 (bi 小) → 畫面 y 小=頂部
                y0 = int(round((bands_n - 1 - bi) * cell_h))
                y1 = int(round((bands_n - bi) * cell_h))
                v = float(cols[ci, bi])
                r, g, b = colormap(v)
                draw.rectangle([(x0, y0), (x1 - 1, y1 - 1)], fill=(r, g, b, 255))
    else:
        # 靜音 fallback: 深底色 + ♪ silent 字樣
        try:
            font = ImageFont.truetype("arial.ttf", max(10, H // 6))
        except Exception:
            font = ImageFont.load_default()
        draw.text((W // 2 - 28, H // 2 - 8), "♪ silent",
                  fill=(140, 140, 140, 220), font=font)

    # 邊框 (白色 + 加粗, 緊急燈光下對比清晰)
    draw.rectangle([(0, 0), (W - 1, H - 1)],
                   outline=(255, 255, 255, border_alpha), width=border_width)
    return img


def overlay_spectrogram(frame: Image.Image,
                        cols: np.ndarray,
                        position: str = "bottom-right",
                        size: tuple[int, int] | None = None,
                        padding: int = DEFAULT_PADDING) -> Image.Image:
    """疊 spectrogram strip 到 frame 角落, in-place 操作 (回傳同物件).

    Args:
        frame: PIL.Image (RGB or RGBA), 截圖本身
        cols: AudioCapture.snapshot() 結果
        position: 角落 — bottom-right / bottom-left / top-right / top-left
        size: 若 None 則按 frame 解析度等比 (1080p 基準 240x60)
        padding: 距邊距 px (1080p 基準, 等比縮放)

    Returns:
        同 frame (in-place RGBA composite). 若 frame 是 RGB 會先轉 RGBA.
    物理意義: daemon main_loop 一行接 — img = overlay_spectrogram(img, cap.snapshot())
    """
    W, H = frame.size
    # 解析度等比 — base 1920x1080
    scale = min(W / 1920.0, H / 1080.0)
    scale = max(0.4, scale)  # 480p 不要縮太小
    if size is None:
        sw = int(round(DEFAULT_SIZE_1080P[0] * scale))
        sh = int(round(DEFAULT_SIZE_1080P[1] * scale))
    else:
        sw, sh = size
    pad = int(round(padding * scale))

    strip = render_spectrogram_strip(cols, size=(sw, sh))

    # 算左上角座標
    if "bottom" in position:
        y = H - sh - pad
    else:
        y = pad
    if "right" in position:
        x = W - sw - pad
    else:
        x = pad

    if frame.mode != "RGBA":
        frame = frame.convert("RGBA")
    frame.alpha_composite(strip, (x, y))
    return frame


# ===========================================================
# Stereo EQ Bar viz (Tim 2026-06-08 提案, B=peak hold 衰減)
# Layout: 縱向 60 bands, 每 band 一根橫條
#   - 條長度 (橫向) = (L+R)/2 整體音量, 全長對應 W
#   - 條 RGB:
#       R = left channel 該頻段 dB (auto-AGC normalize)
#       G = right channel 該頻段 dB
#       B = peak hold (0.5s 衰減), 補回時間維度殘像
#   - L>R → 偏紅; R>L → 偏綠; L≈R → 黃; 剛響過 → 加藍
# ===========================================================
def render_stereo_eq_bar(L_mat: np.ndarray,
                          R_mat: np.ndarray,
                          peak_mat: np.ndarray,
                          size: tuple[int, int] = DEFAULT_STEREO_EQ_SIZE_1080P,
                          bg_alpha: int = DEFAULT_BG_ALPHA,
                          border_alpha: int = DEFAULT_BORDER_ALPHA,
                          border_width: int = DEFAULT_BORDER_WIDTH,
                          silent_threshold: float = 0.02) -> Image.Image:
    """Render stereo spectrogram (Tim 2026-06-08 設計) → RGBA PIL Image.

    Layout: 鋪底全寬 — X=time (左舊右新), Y=freq (上高下低), pixel RGB=(L,R,peak)
        L>R → 偏紅 ; R>L → 偏綠 ; balanced → 黃 ; 剛響過 → 加藍

    Args:
        L_mat, R_mat, peak_mat: shape=(columns, bands) normalize 0~1
        size: (W, H), W=time 寬度, H=freq 縱深 (壓低留字幕空間)
    """
    W, H = size
    img = Image.new("RGBA", (W, H), (0, 0, 0, bg_alpha))
    draw = ImageDraw.Draw(img)
    if L_mat.size == 0 or R_mat.size == 0:
        draw.rectangle([(0, 0), (W - 1, H - 1)], outline=(255, 255, 255, border_alpha), width=border_width)
        return img

    columns_n, bands_n = L_mat.shape
    cell_w = W / columns_n
    cell_h = H / bands_n
    envelope_max = max(float(L_mat.max()), float(R_mat.max()))
    is_silent = envelope_max < silent_threshold

    if not is_silent:
        for ci in range(columns_n):
            x0 = int(round(ci * cell_w))
            x1 = int(round((ci + 1) * cell_w))
            for bi in range(bands_n):
                # Y 軸顛倒: 高頻在頂 → bi 大 / 畫面 y 小
                y0 = int(round((bands_n - 1 - bi) * cell_h))
                y1 = int(round((bands_n - bi) * cell_h))
                r = int(round(L_mat[ci, bi] * 255))
                g = int(round(R_mat[ci, bi] * 255))
                b = int(round(peak_mat[ci, bi] * 255))
                if r + g + b == 0:
                    continue  # 純黑 cell 不繪 (省 op)
                draw.rectangle([(x0, y0), (max(x1 - 1, x0), max(y1 - 1, y0))], fill=(r, g, b, 255))
    else:
        try:
            font = ImageFont.truetype("arial.ttf", max(10, H // 3))
        except Exception:
            font = ImageFont.load_default()
        draw.text((W // 2 - 28, H // 2 - 8), "♪ silent",
                  fill=(140, 140, 140, 220), font=font)

    draw.rectangle([(0, 0), (W - 1, H - 1)],
                   outline=(255, 255, 255, border_alpha), width=border_width)
    return img


def overlay_stereo_eq_bar(frame: Image.Image,
                          L_mat: np.ndarray,
                          R_mat: np.ndarray,
                          peak_mat: np.ndarray,
                          position: str = "bottom-stretch",
                          size: tuple[int, int] | None = None,
                          padding: int = DEFAULT_PADDING) -> Image.Image:
    """疊 stereo spectrogram viz 到 frame 底部全寬 (預設 position=bottom-stretch)."""
    W, H = frame.size
    scale = min(W / 1920.0, H / 1080.0)
    scale = max(0.4, scale)
    if size is None:
        if "stretch" in position:
            # 鋪底全寬完全貼底, 0 padding
            sw = W
            sh = int(round(DEFAULT_STEREO_EQ_SIZE_1080P[1] * scale))
        else:
            sw = int(round(DEFAULT_STEREO_EQ_SIZE_1080P[0] * scale))
            sh = int(round(DEFAULT_STEREO_EQ_SIZE_1080P[1] * scale))
    else:
        sw, sh = size
    pad = int(round(padding * scale))

    bar = render_stereo_eq_bar(L_mat, R_mat, peak_mat, size=(sw, sh))

    if "stretch" in position:
        # 完全貼底 — 0 padding 各方向 (避免遮中文字幕)
        x = 0
        y = H - sh
    else:
        if "bottom" in position:
            y = H - sh - pad
        else:
            y = pad
        if "right" in position:
            x = W - sw - pad
        else:
            x = pad

    if frame.mode != "RGBA":
        frame = frame.convert("RGBA")
    frame.alpha_composite(bar, (x, y))
    return frame


# ===========================================================
# T-AudioLog — 長時段 audio strip 渲染 (給 screenstream_montage.py 接底)
# 物理意義: montage 已有 12 tile 縮圖牆, 底下接一條完整 cycle 時段的 audio spectrogram, 視覺解耦
# 設計依據: Tim 2026-06-08 提案「montage 下方接音頻 strip 不限版面高度」
# ===========================================================
def load_audio_log(path) -> dict | None:
    """讀 npz log 檔案. 失敗回 None (fail-soft, montage 走無 strip 路徑)."""
    from pathlib import Path as _Path
    p = _Path(path)
    if not p.exists():
        return None
    try:
        npz = np.load(p, allow_pickle=False)
        return {
            "timestamps": npz["timestamps"],
            "L_db": npz["L_db"],
            "R_db": npz["R_db"],
            "peak_db": npz["peak_db"],
            "window_sec": float(npz["window_sec"]),
            "bands": int(npz["bands"]),
            "freq_min": float(npz["freq_min"]),
            "freq_max": float(npz["freq_max"]),
        }
    except Exception:
        return None


def render_audio_log_strip(
        timestamps: np.ndarray,
        L_db: np.ndarray,
        R_db: np.ndarray,
        peak_db: np.ndarray,
        t_start: float,
        t_end: float,
        width: int,
        height: int = 280,
        tile_times: list[float] | None = None,
        # Auto-AGC (跟 AudioCapture 同預設, 一致 normalize 體驗)
        dynamic_range_db: float = 45.0,
        top_headroom_db: float = 3.0,
        silence_floor_db: float = -65.0,
        top_percentile: float = 95.0,
        # 視覺 (summit 2026-06-08 polish round)
        bg_alpha: int = 245,             # 純黑底, 高反差
        border_alpha: int = 230,
        border_width: int = 2,
        tile_marker_alpha: int = 165,    # 更明顯 (110 → 165)
        label_alpha: int = 235,
        silent_threshold: float = 0.02,
        show_tile_numbers: bool = True,  # 在頂部 marker 旁標 #1..#N
        show_freq_guide: bool = True,    # 中段加一條 mid-freq 水平淡虛線
) -> Image.Image:
    """Render audio log strip 給 montage 接底 (跨整個 cycle 時段, 含 tile 對應線 + 時間標籤).

    Args:
        timestamps, L_db, R_db, peak_db: load_audio_log() 返回的對應陣列
        t_start, t_end: 要渲染的時段 (epoch 浮點, 通常 = montage 首末 tile 的 mtime)
        width, height: 輸出尺寸 px (寬通常綁 montage canvas 寬)
        tile_times: 各 tile 的 mtime (epoch), 在 strip 上畫垂直白線對應位置;
                    None 則不畫

    Returns:
        RGBA PIL.Image, 可直接 paste / alpha_composite 到 canvas.

    物理意義: 跟 frame 嵌入式 stereo_eq_bar 同色彩語意 (R=L, G=R, B=peak), 但解耦版面跟時間軸.
    """
    img = Image.new("RGBA", (width, height), (0, 0, 0, bg_alpha))
    draw = ImageDraw.Draw(img)

    # 區塊職責: 框 + 「無資料」 fallback
    def _draw_border():
        draw.rectangle([(0, 0), (width - 1, height - 1)],
                       outline=(255, 255, 255, border_alpha), width=border_width)

    if timestamps is None or len(timestamps) == 0:
        try:
            font = ImageFont.truetype("arial.ttf", max(12, height // 5))
        except Exception:
            font = ImageFont.load_default()
        draw.text((width // 2 - 80, height // 2 - 10),
                  "♪ no audio log (daemon dump 還沒寫?)",
                  fill=(180, 180, 180, label_alpha), font=font)
        _draw_border()
        return img

    # 區塊職責: 切時段
    # 物理意義: log ring 寫到當下, 但 montage 區間可能落在某個更早 window; mask 出落在 [t_start, t_end]
    # 數值影響: 邊界 inclusive, 若一個 column 都不命中走 silent fallback
    if t_end <= t_start:
        t_end = t_start + 1e-3  # 避免 div by 0
    mask = (timestamps >= t_start) & (timestamps <= t_end)
    if not mask.any():
        try:
            font = ImageFont.truetype("arial.ttf", max(12, height // 5))
        except Exception:
            font = ImageFont.load_default()
        msg = f"♪ no audio cols in span [{int(t_start)} → {int(t_end)}]"
        draw.text((width // 2 - 150, height // 2 - 10),
                  msg, fill=(180, 180, 180, label_alpha), font=font)
        _draw_border()
        return img

    L_slice = L_db[mask]
    R_slice = R_db[mask]
    peak_slice = peak_db[mask]
    ts_slice = timestamps[mask]
    bands_n = L_slice.shape[1]

    # 區塊職責: Auto-AGC normalize (跟 AudioCapture.snapshot_stereo 一致)
    # 物理意義: 整個 log 段一起算 percentile, 跟 in-frame viz 是分開 normalize — 但動態範圍/floor 都一樣
    # 數值影響: 若整段都很安靜, top_percentile 會 fallback 到 silence_floor + dynamic_range 保底
    def _auto_agc(arr_flat: np.ndarray) -> tuple[float, float]:
        try:
            p_top = float(np.percentile(arr_flat, top_percentile))
        except Exception:
            p_top = float(arr_flat.max())
        top = max(p_top + top_headroom_db, silence_floor_db + dynamic_range_db)
        bot = top - dynamic_range_db
        return top, bot

    lr_top, lr_bot = _auto_agc(np.concatenate([L_slice.ravel(), R_slice.ravel()]))
    pk_top, pk_bot = _auto_agc(peak_slice.ravel())

    def _norm(arr, top, bot):
        return np.clip((arr - bot) / (top - bot), 0.0, 1.0).astype(np.float32)

    L_n = _norm(L_slice, lr_top, lr_bot)
    R_n = _norm(R_slice, lr_top, lr_bot)
    peak_n = _norm(peak_slice, pk_top, pk_bot)

    # 區塊職責: render — X 軸用「時間 → x 像素」直接映射 (而非 column index 等寬), 缺資料段自然顯示成黑
    # 物理意義: 時間軸要對齊 tile 對應線, 不能用 column index 等寬 (column 之間時間間隔可能不均)
    # 數值影響: 每 column 渲染一個小矩形, 寬度 = (t[i+1] - t[i]) / total_span × width
    span = float(t_end - t_start)
    # silent check 用 normalize 後的 (0~1), 不是 raw dB (raw dB 是負數 → 跟 0.02 比直接誤觸 silent)
    envelope_max = max(float(L_n.max()), float(R_n.max()))
    is_silent = envelope_max < silent_threshold

    def _x_of(ts: float) -> int:
        return int(round((ts - t_start) / span * width))

    cell_h = height / bands_n

    if not is_silent:
        # 每 column 矩形: x 範圍 = [x_of(ts[i]), x_of(ts[i+1])], y 範圍 = bands_n - 1 - bi
        for ci in range(len(ts_slice)):
            x0 = _x_of(float(ts_slice[ci]))
            # 下一個 column 的 x; 最後 column 用 window_sec 推
            if ci + 1 < len(ts_slice):
                x1 = _x_of(float(ts_slice[ci + 1]))
            else:
                # 估 window 寬: 用前一個 step
                if ci > 0:
                    step = float(ts_slice[ci]) - float(ts_slice[ci - 1])
                else:
                    step = span / max(1, len(ts_slice))
                x1 = _x_of(float(ts_slice[ci]) + step)
            x1 = max(x1, x0 + 1)
            x1 = min(x1, width)
            for bi in range(bands_n):
                y0 = int(round((bands_n - 1 - bi) * cell_h))
                y1 = int(round((bands_n - bi) * cell_h))
                r = int(round(L_n[ci, bi] * 255))
                g = int(round(R_n[ci, bi] * 255))
                b = int(round(peak_n[ci, bi] * 255))
                if r + g + b == 0:
                    continue
                draw.rectangle([(x0, y0), (max(x1 - 1, x0), max(y1 - 1, y0))],
                               fill=(r, g, b, 255))
    else:
        try:
            font = ImageFont.truetype("arial.ttf", max(14, height // 4))
        except Exception:
            font = ImageFont.load_default()
        draw.text((width // 2 - 50, height // 2 - 12),
                  "♪ silent throughout",
                  fill=(160, 160, 160, label_alpha), font=font)

    # 區塊職責: 中段水平 freq guide (淡虛線, 視覺 anchor 區分高頻/低頻)
    # 物理意義: 24 bands 對 log-scale freq, 中段 = 中頻 ~= 1.0 kHz, 給 agent 速判斷上下哪邊是 vocal/instrument
    # 數值影響: 整條淡灰虛線, 4px on/4px off, alpha 不高 (60) 避免遮 spectrogram
    if show_freq_guide and not is_silent:
        mid_y = height // 2
        # 虛線: 從 0 到 width, 每 8px on/off
        for x in range(0, width, 12):
            x_end = min(x + 6, width)
            draw.line([(x, mid_y), (x_end, mid_y)],
                      fill=(255, 255, 255, 60), width=1)

    # 區塊職責: tile boundary markers (頂部 + 底部刻度線 + 頂部標 #N tile 序號)
    # 物理意義: 讓 agent 一眼對應「這個 tile 那一刻的音頻長這樣」+ 直接認出第幾張 tile
    # 數值影響: tile 序號用小字標頂部, 不貫穿 spectrogram 避免遮 (跟之前一致)
    if tile_times:
        tick_h_top = max(14, height // 8)     # 拉長: 6 → 14, 視覺更顯眼
        tick_h_bot = max(10, height // 10)    # 底部稍短 (留給時間 label)
        try:
            tile_num_font = ImageFont.truetype("arial.ttf", max(10, height // 18))
        except Exception:
            tile_num_font = ImageFont.load_default()
        for ti, tt in enumerate(tile_times, start=1):
            if t_start <= tt <= t_end:
                xx = _x_of(float(tt))
                # 頂部刻度線 (拉長 + 加粗)
                draw.line([(xx, 0), (xx, tick_h_top)],
                          fill=(255, 255, 255, tile_marker_alpha), width=2)
                # 底部刻度線
                draw.line([(xx, height - tick_h_bot), (xx, height - 1)],
                          fill=(255, 255, 255, tile_marker_alpha), width=2)
                # tile 序號小字 #N — 略偏右避免重疊刻度線
                if show_tile_numbers:
                    label = f"#{ti}"
                    draw.text((xx + 3, 2), label,
                              fill=(255, 255, 255, label_alpha), font=tile_num_font)

    # 區塊職責: 時間軸 label (首末 + 中段, 字體加大)
    # 物理意義: 給 agent 速懂這條 strip 跨多少秒, 不必算 mtime; span 顯著放中心
    # 數值影響: 字體 height//16 (絕對值更大), label 位置底部避免重疊 spectrogram 主體
    try:
        font = ImageFont.truetype("arial.ttf", max(13, height // 16))
    except Exception:
        font = ImageFont.load_default()
    span_sec = t_end - t_start
    label_y = height - max(20, height // 14)

    def _hms(ts: float) -> str:
        return time.strftime("%H:%M:%S", time.localtime(ts))

    # 用底色矩形給 label 一個 contrast block (純黑 alpha 200), 防淺色音訊蓋到字
    def _draw_label(x: int, text: str, anchor_right: bool = False):
        # text 大小 (粗估字寬 = font_size * 0.6)
        font_size = max(13, height // 16)
        approx_w = int(len(text) * font_size * 0.6)
        approx_h = font_size + 4
        bx0 = (x - approx_w - 2) if anchor_right else (x - 2)
        bx1 = x if anchor_right else (x + approx_w + 2)
        by0 = label_y - 2
        by1 = label_y + approx_h
        draw.rectangle([(bx0, by0), (bx1, by1)], fill=(0, 0, 0, 200))
        tx = bx0 + 2
        draw.text((tx, label_y), text, fill=(230, 230, 230, label_alpha), font=font)

    _draw_label(4, _hms(t_start))
    span_text = f"Δ {span_sec:.1f}s"
    span_x = width // 2
    _draw_label(span_x, span_text)
    _draw_label(width - 6, _hms(t_end), anchor_right=True)

    _draw_border()
    return img


# ===========================================================
# CLI 自驗 (mock + live)
# ===========================================================
def make_mock_cols(columns: int = DEFAULT_COLUMNS, bands: int = DEFAULT_BANDS) -> np.ndarray:
    """Synthetic test pattern: 漸增亮度 + 兩條斜紋 (低頻 sweep + 高頻 pulse).

    讓本小姐視覺判讀: 對的時序 (左舊右新) + 對的 freq 軸 (上高下低) + colormap 對.
    """
    cols = np.zeros((columns, bands), dtype=np.float32)
    for ci in range(columns):
        # 時序漸增能量 (左暗右亮)
        base = ci / max(1, columns - 1) * 0.3
        # 低頻 sweep — bi 隨 ci 線性上升
        sweep_band = int(ci / columns * bands * 0.4)
        # 高頻脈衝 — 每 5 column 一次
        pulse_active = (ci % 5 == 0)
        for bi in range(bands):
            v = base
            if abs(bi - sweep_band) < 3:
                v += 0.6
            if pulse_active and bi > bands * 0.7:
                v += 0.7
            cols[ci, bi] = min(1.0, v)
    return cols


def cmd_mock() -> int:
    """Render fake matrix → 輸出 _viz_mock.png + composite 到假 frame 看右下角效果."""
    cols = make_mock_cols()
    strip = render_spectrogram_strip(cols)
    out_strip = Path("_viz_mock_strip.png").resolve()
    strip.save(out_strip)
    print(f"strip saved: {out_strip}")

    # 假 frame 1920x1080 漸層背景
    bg = Image.new("RGB", (1920, 1080), (40, 40, 50))
    draw = ImageDraw.Draw(bg)
    for y in range(0, 1080, 20):
        draw.line([(0, y), (1920, y)], fill=(60, 60, 70))
    bg = overlay_spectrogram(bg, cols)
    out_full = Path("_viz_mock_full.png").resolve()
    bg.convert("RGB").save(out_full, "PNG")
    print(f"full frame saved: {out_full}")
    return 0


def cmd_live(seconds: int = 10) -> int:
    """抓 N 秒真實音訊, 結束時 dump 最後 snapshot 進 PNG."""
    cap = AudioCapture()
    cap.start()
    print(f"AudioCapture started, recording {seconds} sec...")
    for i in range(seconds):
        time.sleep(1)
        if cap.error:
            print(f"  ❌ capture error: {cap.error}")
            cap.stop()
            return 1
        snap = cap.snapshot()
        print(f"  t={i+1}s  max={snap.max():.3f}  mean={snap.mean():.3f}")
    cols = cap.snapshot()
    cap.stop()

    bg = Image.new("RGB", (1920, 1080), (40, 40, 50))
    bg = overlay_spectrogram(bg, cols)
    out = Path("_viz_live.png").resolve()
    bg.convert("RGB").save(out, "PNG")
    print(f"live snapshot saved: {out}")
    return 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: screenstream_audio_viz.py mock|live [seconds]")
        sys.exit(2)
    cmd = sys.argv[1]
    if cmd == "mock":
        sys.exit(cmd_mock())
    elif cmd == "live":
        secs = int(sys.argv[2]) if len(sys.argv) > 2 else 10
        sys.exit(cmd_live(secs))
    else:
        print(f"unknown cmd: {cmd}")
        sys.exit(2)
