#!/usr/bin/env python3
"""screenstream_daemon.py — 螢幕串流 rolling buffer daemon (T11 MVP, 2026-05-16 basecamp).

# 區塊職責：常駐 daemon 每秒截圖 → ring buffer 寫 frames/frame_NNNN.jpg
# 物理意義：跟 discord_inbound_bot 對偶 (一個收外部訊息, 一個寫螢幕狀態給 agent 讀);
#          ring buffer 自動覆蓋舊 frame, 不會無限長大; 每寫一筆 copy 一份成 _latest.jpg
#          給 agent 快速 access. config 開關 reload 每 loop, toggle off 後最多 1s 內暫停.
# 數值影響：1080p JPEG q65 ≈ 200-400KB/frame, 600 frames ≈ 120-240 MB 環狀; 每秒 ~5% CPU.

設計依據: docs/Plan/Plan_ScreenStream_Design.md (Tim 2026-05-16 拍板, basecamp 自決最終 spec)

啟動:
  python AgentCommands/Tools/screenstream_daemon.py

或由 Unity Editor 的 UCL_ScreenStreamDaemon.cs 自動 spawn (per InitializeOnLoadMethod;
2026-07-28 起存活綁 config.enabled — 停止錄影即收掉, 不再常駐 idle).

Config: AgentCommands/_screenstream/_config.json (預設 enabled=false, 須手動開啟)

依賴: Pillow (PIL.ImageGrab) — Windows 內建支援 grab multi-screen
"""
from __future__ import annotations

import datetime
import io
import json
import os
import shutil
import sys
import time
from pathlib import Path

# Windows console UTF-8
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# 區塊職責：路徑解析 — 本檔已遷入 <UCL_Core>/Tools~/AgentCommands (跨專案共用, 2026-07-26 Tim 拍板),
#          不能再用「上兩層 = repo 根」假設。改 repo-walk (.git 只認資料夾, 跳過 submodule gitlink),
#          runtime 狀態 (_screenstream/) 一律落「主專案」AgentCommands — 對齊 knowledge_base.py 慣例。
HERE = Path(__file__).resolve().parent


def _find_git_root(start: Path):
    # 逐層向上找 .git「資料夾」(submodule 的 .git 是檔案 → 被 is_dir 跳過)
    p = start.resolve()
    while p != p.parent:
        if (p / ".git").is_dir():
            return p
        p = p.parent
    return None


def _resolve_repo_root() -> Path:
    # 優先吃 CLAUDE_PROJECT_DIR (agent 環境); 其次從 script 位置 walk; 最後 cwd walk / 上兩層 fallback
    env = os.environ.get("CLAUDE_PROJECT_DIR")
    if env and Path(env).is_dir():
        return Path(env).resolve()
    walked = _find_git_root(HERE)
    if walked:
        return walked
    return _find_git_root(Path.cwd()) or HERE.parent.parent


def _resolve_data_root(root: Path) -> Path:
    # AgentCommands 資料根 — honors <repo>/.agentcommands_root.local pointer (C#/Python 共讀)
    pointer = root / ".agentcommands_root.local"
    try:
        if pointer.exists():
            content = pointer.read_text(encoding="utf-8").strip()
            if content and Path(content).is_absolute():
                return Path(content).resolve()
    except Exception:
        pass
    return (root / "AgentCommands").resolve()


REPO_ROOT = _resolve_repo_root()
STREAM_DIR = _resolve_data_root(REPO_ROOT) / "_screenstream"
FRAMES_DIR = STREAM_DIR / "frames"
CONFIG_PATH = STREAM_DIR / "_config.json"
LATEST_TXT = STREAM_DIR / "_latest.txt"
LATEST_JPG = STREAM_DIR / "_latest.jpg"
LOG_PATH = STREAM_DIR / "_daemon.log"
PID_PATH = STREAM_DIR / "_daemon.pid"
# T-AudioLog (Tim 2026-06-08, summit ship) — audio log dump 目標 (給 montage 接底 audio strip 用)
# 物理意義: AudioCapture 跑長 log ring (in-memory, 600s by default), 每 N frame 觸發 atomic write 此檔案
# 數值影響: 落盤約 1MB / 600s; 寫盤頻率約 10s 一次 → 對 daemon main loop ~ms 級開銷
AUDIO_LOG_PATH = STREAM_DIR / "_audio_log.npz"
AUDIO_LOG_DUMP_EVERY_N_FRAMES = 10  # 1 fps 預設 = 每 10s 寫一次
# T13 (2026-05-16) — 敏感 Page marker; Editor 端寫 mtime, daemon 看 mtime < 2s 視為「敏感 page active」→ 整張塗黑
SENSITIVE_FLAG_PATH = STREAM_DIR / "_sensitive.flag"
SENSITIVE_STALE_SEC = 2.0
# T14 (2026-05-16) — 多螢幕資訊匯出檔; daemon 啟動 + monitor topology 變動時更新, Editor page 讀此檔列 dropdown
MONITORS_PATH = STREAM_DIR / "_monitors.json"
# T17 (2026-05-18 gura) — 緊急中斷直播 lock 檔; Editor 端按「中斷直播」按鈕寫入,
# daemon 每 loop tick reload config 後 check 此檔存在 → set enabled=false + 刪 lock + 廣播酒保 stop
STOP_LOCK_PATH = STREAM_DIR / "_stop.lock"
# T19 (2026-06-07 summit) — Unity Game view 來源檔; monitor="unity_game" 時由「下游專案自備的
# GameView capturer」(UCL_Core 未內建; 本 repo 目前無實作 → 此模式僅輸出 placeholder)
# 於 Play mode 每 frame 末擷取 Game view 真實 render 寫此檔, daemon 改讀它當截圖來源 (取代 OS ImageGrab)。
GAMEVIEW_SRC_PATH = STREAM_DIR / "_gameview_src.jpg"
# Unity src 超過此秒數沒更新 (Play mode 關 / capturer idle) → 視為 stale, 改輸出 placeholder 提示而非凍結舊 frame。
GAMEVIEW_STALE_SEC = 5.0

# ===========================================================
# 預設 config (首次跑時建立)
# 物理意義: agent 該知道每欄位後台可調; daemon 每 loop 重讀
# 數值影響: enabled 預設 false → daemon 啟動後等待 toggle on 才開始 capture
# ===========================================================
DEFAULT_CONFIG = {
    "enabled": False,
    "fps": 1,
    "max_frames": 600,
    "resolution": "1080p",          # "2k" | "1440p" | "1080p" | "720p" | "480p" | "native"
    "quality": 65,
    "monitor": "primary",            # "primary" | "all" | "unity_game" | "<index>"
    "format": "jpg",
    "started_at": None,
    "frame_count": 0,
    # T-AudioViz (summit 2026-06-08) — Audio Spectrogram Overlay
    # 物理意義: 抓 system audio loopback FFT 推 5s ring, 疊角落 spectrogram 給 agent 截圖看出音訊動態
    # 數值影響: 預設 off — Tim 從 UCL_ScreenStreamPage 或手改 _config.json 開
    "audio_viz_enabled": False,
    "audio_viz_position": "bottom-right",
    "audio_viz_mode": "stereo_eq",  # "spectrogram" | "stereo_eq" (Tim 2026-06-08 提案 RGB=L,R,peak)  # bottom-right / bottom-left / top-right / top-left
    # T-OCR-Pipeline (Tim 2026-06-10 拍板「每 frame 錄製時就自動產生, 多執行緒並行」)
    # 物理意義: 每寫一張 frame 即丟 OCR worker pool 背景跑字幕辨識 → _screenstream/ocr/frame_NNNN.json;
    #          montage --ocr 端 cache-first 讀, 命中免重跑 (每輪省 10-30s inline OCR)
    # 數值影響: 預設 off — Tim 從 _config.json 開; workers=2 (fps=2 × ~300ms/幀 = 0.6 負載, 留 headroom)
    "ocr_enabled": False,
    "ocr_workers": 2,
    # 字幕帶座標 — 底部原點語意 (Tim 2026-07-28 拍板): y_bottom=0 表示帶底貼畫面下緣, 高度往上長。
    # 舊頂部原點 key ocr_y_pct 的 config 由 subtitle_ocr.regions_from_config 讀取時自動換算遷移。
    "ocr_y_bottom_pct": 0.0,   # 帶底邊離畫面下緣距離 (0=貼底)
    "ocr_h_pct": 0.12,         # 字幕帶高度 (從底邊往上長)
    # 額外字幕判定區域 (可空) — 有些影片字幕偶爾跑到上方; 各項 {"y_bottom_pct": f, "h_pct": f}
    "ocr_extra_regions": [],
    "ocr_min_conf": 0.5,    # 低信度過濾
    # T-OCR-AdaptiveDensity (Tim 2026-06-10) — lag 過大時自動跳幀追進度 (詳見 subtitle_ocr.py 常數)
    "ocr_adaptive": True,
    # T-STT-Cache (Quest T06/T07, kotoko 2026-07-05) — 持續語音轉錄 cache (openai-whisper, GPU)
    # 物理意義: 開啟後背景 worker 連續錄 chunk 轉錄寫 stt/stt_<epoch>.json; montage --stt cache-only 讀。
    # 數值影響: whisper GPU 常駐 (small ~460MB VRAM); 預設 OFF (跟 ocr_enabled 一樣須手動開)。
    "stt_enabled": False,
    "stt_model": "small",       # tiny/base/small/medium/large-v3
    "stt_lang": "",             # en/zh/空=自動偵測
    "stt_chunk_sec": 15,        # 每個 cache chunk 音訊長度
    # T-STT-Prompt (summit 2026-07-10, RFC2): whisper initial_prompt 詞彙偏置 (登場人物名壓咬字)。
    #   MUST 用轉錄語言字形 (日文餵片假名 e.g.「登場人物：エミリコ、ケイト」不是中文譯名)。
    #   陪看 skill 從 reading-library stt-prompt 抽該書日文角色名填入。改動需 toggle off→on 重起。
    "stt_prompt": "",
    "_schema_version": 1,
}

# T-OCR-Pipeline — daemon-side OCR cache 目錄 (跟 frames/ 平行)
OCR_CACHE_DIR = STREAM_DIR / "ocr"
# T-STT-Cache — daemon-side STT cache 目錄 (跟 frames/ 與 ocr/ 平行)
STT_CACHE_DIR = STREAM_DIR / "stt"

RESOLUTION_MAP = {
    "2k":     (2560, 1440),
    "1440p":  (2560, 1440),
    "1080p":  (1920, 1080),
    "720p":   (1280, 720),
    "480p":   (854, 480),
    "native": None,                  # 不 resize
}


def log(msg: str, level: str = "INFO") -> None:
    """簡訊 log → stdout + _daemon.log (給 Unity Editor 子程序 + 人類查問題)."""
    ts = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    line = f"[{ts}] [{level}] {msg}"
    print(line, flush=True)
    try:
        with LOG_PATH.open("a", encoding="utf-8") as f:
            f.write(line + "\n")
    except OSError:
        pass


def ensure_dirs() -> None:
    """首次跑時建立目錄樹 + 預設 config."""
    STREAM_DIR.mkdir(parents=True, exist_ok=True)
    FRAMES_DIR.mkdir(parents=True, exist_ok=True)
    if not CONFIG_PATH.exists():
        save_config(DEFAULT_CONFIG.copy())
        log(f"created default config: {CONFIG_PATH}")


def load_config() -> dict:
    """讀 config 並補預設; bad json → 用預設 + 警告."""
    try:
        with CONFIG_PATH.open("r", encoding="utf-8") as f:
            cfg = json.load(f)
        # 補預設欄位 (向前相容)
        for k, v in DEFAULT_CONFIG.items():
            cfg.setdefault(k, v)
        return cfg
    except (OSError, json.JSONDecodeError) as e:
        log(f"config load fail ({e}), using default", "WARN")
        return DEFAULT_CONFIG.copy()


def save_config(cfg: dict) -> None:
    """atomic write: tmp → rename."""
    tmp = CONFIG_PATH.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(cfg, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(tmp, CONFIG_PATH)


def write_pid() -> None:
    """寫 PID 給外部 alive 判斷."""
    try:
        PID_PATH.write_text(str(os.getpid()), encoding="utf-8")
    except OSError:
        pass


def cleanup_pid() -> None:
    """退出前刪 PID 檔 — 但只刪「屬於自己」的那份。
    物理意義: recompile 硬殺舊 daemon 不跑 cleanup，會殘留 stale PID；新 daemon 接手後 write_pid 覆寫成
             自己的 PID。若此時另一隻正在 graceful 退出的舊 daemon 無條件 unlink，會誤刪掉「新 daemon 剛寫的」
             PID 檔 → 新 daemon 活著但 PID 檔消失 → 頁面誤判 DEAD (2026-07-27 Tim QA)。
    修法: 只有檔案內容 == 自己的 PID 才刪，絕不刪別隻 daemon 的 PID 檔。"""
    try:
        if PID_PATH.read_text(encoding="utf-8").strip() == str(os.getpid()):
            PID_PATH.unlink()
    except OSError:
        pass


# ===========================================================
# Multi-monitor enumeration (T14)
# 物理意義: 用 screeninfo 列舉所有實體 monitor 跟 bbox, 寫 _monitors.json 給 Editor page 讀
# 數值影響: daemon 啟動時跑一次; 跑時 monitor 變動 (USB 拔/插) 不自動更新, 需重啟 daemon
# ===========================================================
def _enumerate_monitors_win():
    """用 Windows EnumDisplayMonitors 列舉實體 monitor — 零依賴 fallback (screeninfo 缺時)。
    座標為 virtual desktop 空間, 與 PIL.ImageGrab(all_screens=True) 的 bbox 一致。
    (2026-07-27 Tim QA: screeninfo 未裝 → 只剩 primary; 改用內建 Win32 API 免安裝即偵測多螢幕。)"""
    import ctypes

    class RECT(ctypes.Structure):
        _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                    ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

    class MONITORINFOEXW(ctypes.Structure):
        _fields_ = [("cbSize", ctypes.c_ulong), ("rcMonitor", RECT),
                    ("rcWork", RECT), ("dwFlags", ctypes.c_ulong),
                    ("szDevice", ctypes.c_wchar * 32)]

    MONITORINFOF_PRIMARY = 0x1
    user32 = ctypes.windll.user32
    # 顯式 argtypes — 64-bit handle 不設會被 ctypes 當 c_int 截斷 → 崩
    MonitorEnumProc = ctypes.WINFUNCTYPE(
        ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p,
        ctypes.POINTER(RECT), ctypes.c_void_p)
    user32.GetMonitorInfoW.argtypes = [ctypes.c_void_p, ctypes.POINTER(MONITORINFOEXW)]
    user32.GetMonitorInfoW.restype = ctypes.c_int
    user32.EnumDisplayMonitors.argtypes = [
        ctypes.c_void_p, ctypes.c_void_p, MonitorEnumProc, ctypes.c_void_p]
    user32.EnumDisplayMonitors.restype = ctypes.c_int

    found = []

    def _cb(hMonitor, hdc, lprc, lparam):
        mi = MONITORINFOEXW()
        mi.cbSize = ctypes.sizeof(MONITORINFOEXW)
        if user32.GetMonitorInfoW(hMonitor, ctypes.byref(mi)):
            r = mi.rcMonitor
            found.append({
                "x": int(r.left), "y": int(r.top),
                "w": int(r.right - r.left), "h": int(r.bottom - r.top),
                "primary": bool(mi.dwFlags & MONITORINFOF_PRIMARY),
                "name": str(mi.szDevice) or "DISPLAY",
            })
        return 1

    proc = MonitorEnumProc(_cb)
    if not user32.EnumDisplayMonitors(None, None, proc, 0):
        raise OSError("EnumDisplayMonitors 回傳 0 (列舉失敗)")
    # 穩定排序: primary 先, 再依座標 → index 可預期
    found.sort(key=lambda m: (not m["primary"], m["x"], m["y"]))
    for i, m in enumerate(found):
        m["index"] = i
        if not m["name"]:
            m["name"] = f"DISPLAY{i + 1}"
    return found


def enumerate_monitors():
    """回 list of dict: {index, x, y, w, h, primary, name}. 皆不可用回 []."""
    # 優先 screeninfo (若已安裝, 跨平台且有 friendly name)
    try:
        from screeninfo import get_monitors
        out = []
        for i, m in enumerate(get_monitors()):
            out.append({
                "index": i,
                "x": int(m.x),
                "y": int(m.y),
                "w": int(m.width),
                "h": int(m.height),
                "primary": bool(m.is_primary),
                "name": str(m.name or f"DISPLAY{i+1}"),
            })
        if out:
            return out
    except Exception as e:
        log(f"enumerate_monitors: screeninfo 不可用, 改用 Windows API ({e})", "INFO")
    # fallback: Windows 內建 EnumDisplayMonitors (免安裝)
    try:
        return _enumerate_monitors_win()
    except Exception as e:
        log(f"enumerate_monitors fail (win api): {e}", "WARN")
        return []


def write_monitors_file(monitors: list) -> None:
    """匯出 monitor 列表給 Editor page dropdown 用."""
    try:
        MONITORS_PATH.write_text(json.dumps({"monitors": monitors}, indent=2, ensure_ascii=False), encoding="utf-8")
    except OSError as e:
        log(f"write monitors file fail: {e}", "WARN")


def resolve_monitor_bbox(monitor: str, monitors: list):
    """解析 config.monitor 字串 → bbox tuple (x1,y1,x2,y2) 或 None (=all_screens).

    支援:
      "primary" — 抓 is_primary monitor
      "all"     — 整 virtual desktop (回 None, 走 ImageGrab(all_screens=True))
      "0"/"1"/"2"... — 指定 monitor index
      無效字串 → fallback primary
    """
    if monitor == "all":
        return None  # 走 all_screens=True
    if monitor == "primary":
        for m in monitors:
            if m["primary"]:
                return (m["x"], m["y"], m["x"] + m["w"], m["y"] + m["h"])
        # 找不到 primary → 用 monitor 0
        if monitors:
            m = monitors[0]
            return (m["x"], m["y"], m["x"] + m["w"], m["y"] + m["h"])
        return None
    # 嘗試 index
    try:
        idx = int(monitor)
        if 0 <= idx < len(monitors):
            m = monitors[idx]
            return (m["x"], m["y"], m["x"] + m["w"], m["y"] + m["h"])
    except (ValueError, IndexError):
        pass
    # fallback: primary
    return resolve_monitor_bbox("primary", monitors)


# ===========================================================
# Screenshot — 走 PIL.ImageGrab (Windows 內建多螢幕支援)
# 物理意義: grab 抓 Win32 desktop pixel buffer; bbox 指定切某 monitor;
#          all_screens=True 抓 virtual desktop 全部 monitor 拼接
# 數值影響: grab() 在 1080p 約 50-100ms; resize 約 10-30ms; encode JPEG 約 30-50ms
# ===========================================================
def make_placeholder_image(msg: str, size: tuple = (1280, 720)):
    """產生提示用 placeholder 圖 (深灰底 + 置中白字).

    物理意義: unity_game 模式下 Unity 端沒在供應 frame (Play mode 關 / capturer idle) 時,
              輸出明確提示而非凍結舊 frame, 符合「外觀 OK ≠ 真的 OK」— 不讓觀察者誤判直播仍在跑。
    """
    from PIL import Image, ImageDraw, ImageFont
    img = Image.new("RGB", size, (15, 15, 25))
    draw = ImageDraw.Draw(img)
    try:
        font = ImageFont.truetype("arial.ttf", size=max(20, size[1] // 28))
    except (OSError, IOError):
        font = ImageFont.load_default()
    try:
        bbox = draw.textbbox((0, 0), msg, font=font)
        tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    except AttributeError:
        tw, th = draw.textsize(msg, font=font)
    draw.multiline_text(((size[0] - tw) // 2, (size[1] - th) // 2), msg,
                        fill=(160, 170, 200), font=font, align="center")
    return img


def load_gameview_src():
    """讀 Unity 端 _gameview_src.jpg 當截圖來源 (monitor="unity_game" 模式).

    回 PIL.Image。來源檔缺失 / stale (Play mode 關或 capturer idle) / 解碼失敗 → 回 placeholder 提示圖。
    物理意義: daemon 不自己 grab, 改消費 Unity 供應的 Game view 真實 render; 但要把「沒在供應」明確標示出來。
    """
    from PIL import Image
    if not GAMEVIEW_SRC_PATH.exists():
        return make_placeholder_image("[Unity Game View]\nwaiting for capturer\n(enter Play mode to start)")
    try:
        age = time.time() - GAMEVIEW_SRC_PATH.stat().st_mtime
        if age > GAMEVIEW_STALE_SEC:
            return make_placeholder_image(
                f"[Unity Game View]\nsource stale ({age:.0f}s)\n(Play mode off? capturer idle?)")
        # copy bytes 後 open, 避免持有 file handle 與 Unity 寫檔競態
        with GAMEVIEW_SRC_PATH.open("rb") as f:
            data = f.read()
        return Image.open(io.BytesIO(data)).convert("RGB")
    except Exception as e:
        log(f"load gameview src fail: {e}", "WARN")
        return make_placeholder_image("[Unity Game View]\nsource decode failed")


def grab_and_resize(monitor: str, resolution: str, monitors_cache: list):
    """截圖 + (選擇性) resize. 回 PIL.Image."""
    from PIL import Image, ImageGrab
    if monitor == "unity_game":
        # T19 — 不走 OS 螢幕擷取, 改讀 Unity 端供應的 Game view 真實 render
        img = load_gameview_src()
    elif monitor == "all":
        img = ImageGrab.grab(all_screens=True)
    else:
        bbox = resolve_monitor_bbox(monitor, monitors_cache)
        if bbox is not None:
            img = ImageGrab.grab(bbox=bbox, all_screens=True)
        else:
            img = ImageGrab.grab()
    target = RESOLUTION_MAP.get(resolution.lower())
    if target is not None and img.size != target:
        img = img.resize(target, Image.LANCZOS)
    return img


def is_sensitive_active() -> bool:
    """T13 — Editor 端敏感 Page 在 OnGUI ping marker; daemon 看 mtime < SENSITIVE_STALE_SEC 視為 active.

    物理意義: marker mtime stale (沒 ping) → 敏感 page 不再 foreground → 恢復 normal capture.
    Self-healing: Page 不必管 OnEnable/OnDisable lifecycle, OnGUI ping 自動失效.
    """
    if not SENSITIVE_FLAG_PATH.exists():
        return False
    try:
        age = time.time() - SENSITIVE_FLAG_PATH.stat().st_mtime
        return age < SENSITIVE_STALE_SEC
    except OSError:
        return False


def make_blackout_image(size: tuple, reason_hint: str = ""):
    """敏感模式下回傳全黑圖 + 水印「[敏感視窗錄影已遮蔽]」.

    物理意義: 不留白 / 不留空 → 讓 agent / Tim 看到 frame 時明確知道「被故意遮了」,
              避免「frame 0 byte 以為 daemon 壞了」誤判.
    """
    from PIL import Image, ImageDraw, ImageFont
    img = Image.new("RGB", size, (20, 0, 0))   # 深紅近黑, 警示色
    draw = ImageDraw.Draw(img)
    try:
        # 嘗試系統 default font
        font = ImageFont.truetype("arial.ttf", size=max(24, size[1] // 30))
    except (OSError, IOError):
        font = ImageFont.load_default()
    # 純英文避免 arial.ttf 無 Chinese/emoji glyph fallback
    msg = "[REDACTED] ScreenStream Recording - Sensitive Page Active"
    if reason_hint:
        msg += f"\n({reason_hint})"
    # 中央對齊
    try:
        # Pillow 9.0+
        bbox = draw.textbbox((0, 0), msg, font=font)
        tw = bbox[2] - bbox[0]
        th = bbox[3] - bbox[1]
    except AttributeError:
        # 舊版 fallback
        tw, th = draw.textsize(msg, font=font)
    x = (size[0] - tw) // 2
    y = (size[1] - th) // 2
    draw.multiline_text((x, y), msg, fill=(220, 80, 80), font=font, align="center")
    return img


def atomic_write_jpeg(img, path: Path, quality: int) -> None:
    """tmp → rename 避免 reader 讀到半寫檔."""
    tmp = path.with_suffix(".jpg.tmp")
    img.convert("RGB").save(tmp, format="JPEG", quality=quality, optimize=False)
    os.replace(tmp, path)


def update_latest(frame_idx: int, frame_path: Path) -> None:
    """寫 _latest.txt + copy 一份成 _latest.jpg (給 agent 直接讀 latest)."""
    try:
        LATEST_TXT.write_text(str(frame_idx), encoding="utf-8")
        shutil.copy2(frame_path, LATEST_JPG)
    except OSError as e:
        log(f"update_latest fail: {e}", "WARN")


# ===========================================================
# Main loop
# 物理意義: 每秒一次 grab + write; config disabled → sleep_until_enabled poll;
#          每 loop 重讀 config → toggle off 最多 1s 內感應
# ===========================================================
def sleep_until_enabled(poll_sec: float = 5.0) -> dict:
    """daemon 在 enabled=false 時休眠, 直到 config 被改開."""
    log("waiting for enabled=true ...")
    while True:
        cfg = load_config()
        if cfg.get("enabled"):
            log("enabled detected → start capture")
            return cfg
        time.sleep(poll_sec)


# 區塊職責: 直播現場資訊落檔 (Tim 2026-07-27) — _live_info.json 存活期 = 直播期間。
# 物理意義: 「檔案存在」= 直播中 (開播寫入、停播刪除), 內容 = 本場片名/描述 + 開播參數 —
#          給 ucl-free-time 等下游判斷「現在有直播嗎? 在播什麼?」的 canonical 來源,
#          不必自己 parse config (config.stream_title 是持久欄位, 停播後仍殘留, 語意不同)。
# 數值影響: atomic 換檔寫; 寫/刪失敗只 WARN 不影響 capture 主流程。
LIVE_INFO_PATH = STREAM_DIR / "_live_info.json"


def write_live_info(cfg: dict) -> None:
    try:
        info = {
            "stream_title": str(cfg.get("stream_title") or "").strip(),
            "started_at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "resolution": cfg.get("resolution", ""),
            "fps": cfg.get("fps", 0),
            "monitor": str(cfg.get("monitor", "")),
        }
        tmp = LIVE_INFO_PATH.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(info, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
        tmp.replace(LIVE_INFO_PATH)
        log(f"live info written ({info['stream_title'] or '(無片名)'})")
    except OSError as e:
        log(f"live info write fail: {e}", "WARN")


def clear_live_info() -> None:
    try:
        LIVE_INFO_PATH.unlink(missing_ok=True)
        log("live info cleared (直播結束)")
    except OSError as e:
        log(f"live info clear fail: {e}", "WARN")


def post_bartender_announce(event: str, cfg: dict, monitors_cache: list) -> None:
    """T15 — 透過酒保 NPC 廣播 ScreenStream start/end 事件給同事們.

    物理意義: daemon 偵測 cfg.enabled toggle 變化 → 走 TavernClient SDK 寫一筆酒保訊息;
              sender_id=tavern-keeper / meta tag=bartender-rule-announce 跟既有 NPC post 對齊.
    數值影響: post fail (e.g. token enforce 擋) 不影響 daemon 主流程 (caught + log warning).
    Privacy: body 純文字無 @everyone, 沒列 monitor 編號等敏感 (避免外洩 Tim 雙螢幕配置).
    """
    try:
        # 動態 import 避免 daemon 啟動時必依賴 AgentCommands._lib
        import sys as _sys
        repo_root = REPO_ROOT   # 遷移後不再從 STREAM_DIR 反推 (pointer 場景會算錯), 直接用檔頭解析值
        if str(repo_root) not in _sys.path:
            _sys.path.insert(0, str(repo_root))
        from AgentCommands._lib.tavern_client import TavernClient

        client = TavernClient()
        if event == "start":
            res_label = cfg.get("resolution", "?")
            mon_label = cfg.get("monitor", "?")
            fps = cfg.get("fps", "?")
            # 片名/描述 (Tim 2026-07-27): Page 端輸入的 stream_title, 有填才附加一行節目資訊
            stream_title = str(cfg.get("stream_title") or "").strip()
            title_line = f"📺 本場節目: {stream_title}\n" if stream_title else ""
            body = (
                f"🍺📹 *咳咳, 諸位.* ScreenStream 直播開始啦!\n"
                f"{title_line}"
                f"Tim 開了錄影機, 每秒一張快照 ({res_label} @ {fps} fps, monitor={mon_label}).\n"
                f"想看 Tim 在玩什麼就 Read AgentCommands/_screenstream/_latest.jpg 吧.\n"
                f"——酒保提醒: 不 @ everyone 不擾人, 大家自由觀察."
            )
            meta = "tag:bartender-rule-announce;category:meta;event:screenstream-start"
        elif event == "stop":
            count = cfg.get("frame_count", 0)
            body = (
                f"🍺⏹ *直播結束.* ScreenStream daemon 已停止 capture.\n"
                f"本次累計 {count} frames 進 ring buffer (10 min rolling 之後自動覆蓋).\n"
                f"想找剛剛某張畫面的同事們抓緊看. ——酒保關燈了."
            )
            meta = "tag:bartender-rule-announce;category:meta;event:screenstream-stop"
        else:
            return

        res = client.post_message(
            room="tavern",
            sender="tavern-keeper",
            body=body,
            meta=meta,
            wait_reply=0,
            timeout=15.0,
        )
        if res.ok:
            log(f"bartender announce '{event}' posted OK")
        else:
            err = res.error or (res.stderr or "")[:200].strip()
            log(f"bartender announce '{event}' post fail: {err}", "WARN")
    except Exception as e:
        log(f"bartender announce '{event}' exception: {e}", "WARN")


def main_loop() -> int:
    ensure_dirs()
    write_pid()
    # Process 註冊中心 (Tim 2026-07-27 統一管理處): 自我註冊進共用 registry —
    # C# spawn 端 (UCL_ScreenStreamDaemon) 通常已代為註冊 → skip_if_exists 不重寫 (保留 C# 出處);
    # CLI 手動啟動時才由此補記錄, 讓 UCL_ProcessAdminPage 也看得到。
    # allow_multiple=True: 同類多開防治歸 C# pre-spawn guard + 本 daemon 的 PID 檔機制, 這裡只管能見度。
    try:
        from process_registry import register_self
        register_self("screenstream_daemon", description="ScreenStream 錄影/STT/OCR daemon (self-registered)",
                      registered_by="screenstream_daemon.py", allow_multiple=True, skip_if_exists=True)
    except Exception as e:
        log(f"process_registry self-register skip: {e}", "WARN")
    # T14 — 啟動時列舉 monitor + 匯出檔給 Editor page 用
    monitors_cache = enumerate_monitors()
    write_monitors_file(monitors_cache)
    if monitors_cache:
        log(f"detected {len(monitors_cache)} monitor(s): " + ", ".join(
            f"#{m['index']}={m['w']}x{m['h']}@({m['x']},{m['y']}){'(primary)' if m['primary'] else ''}"
            for m in monitors_cache))
    cfg = load_config()
    # T15 — 記初始 enabled 狀態, 偵測 transition
    last_enabled = bool(cfg.get("enabled", False))
    # 直播現場資訊對齊 (Tim 2026-07-27): daemon 重啟不觸發 transition —
    # 啟動時直播已在進行 → 補寫 _live_info.json; 未直播 → 清掉 crash 殘留 (檔案存在 = 直播中的不變式)
    if last_enabled:
        write_live_info(cfg)
    else:
        clear_live_info()
    consecutive_errors = 0

    # T-AudioViz (summit 2026-06-08) — AudioCapture lifecycle 跟 enabled 同步
    # 物理意義: enabled 才 start capture; 每 loop tick 對齊 audio_viz_enabled toggle
    # 數值影響: AudioCapture 失敗時 capture=None, overlay 自動 skip (fail-soft)
    audio_capture = None
    last_audio_viz_enabled = False
    overlay_spectrogram = None
    overlay_stereo_eq_bar = None

    # T-OCR-Pipeline (Tim 2026-06-10) — OCR worker pool lifecycle 跟 ocr_enabled 同步
    # 物理意義: 每張 frame 寫盤後 submit 進 pool, worker threads 並行 OCR → ocr/frame_NNNN.json cache
    # 數值影響: pool 失敗 = None → submit skip (fail-soft, daemon 主流程不受影響)
    ocr_pool = None
    last_ocr_enabled = False
    last_ocr_band = None   # T-OCR-AutoRestart: (y_pct, h_pct, min_conf, workers) 快照, 變更→自動重起 pool
    # T-STT-Cache — STT worker lifecycle 跟 stt_enabled 同步 (對偶 ocr_pool)
    stt_worker = None
    last_stt_enabled = False
    # T-STT-AutoRestart (Tim 2026-07-20): worker 運行中的 (model, lang, prompt) 快照 —
    #   config 任一項被改 → 自動 stop + 重起套用, 取代舊版「只 log WARN 等人工 toggle」的靜默失效設計
    #   (血證: 換片後 stt_lang/stt_prompt 殘留上一場, whisper 幻聽出舊片人名)
    last_stt_cfg = ("", "", "")
    # T-STT-Watchdog (2026-07-27 靜默殭屍事故) — worker 產出停滯偵測 state
    # 物理意義: 擷取失敗重試迴圈 (audio_transcribe._loop 空音訊分支) thread 不死,
    #          下方「dead 偵測」(要求 thread 已死) 永遠不觸發 → STT 靜默停擺 2h 無任何警訊。
    #          改追 chunk_count 水位: 停滯超過門檻 = 殭屍, 不管 thread 死活。
    stt_last_chunk_count = -1     # 上次看到的 chunk 水位 (-1 = 尚無 worker)
    stt_last_progress_ts = 0.0    # 水位最後推進時刻
    stt_zombie_restarts = 0       # 連續殭屍重起次數 (有產出即歸零; ≥3 升級 ERROR)
    try:
        from screenstream_audio_viz import (
            AudioCapture,
            overlay_spectrogram as _overlay_spec_fn,
            overlay_stereo_eq_bar as _overlay_stereo_fn,
        )
        overlay_spectrogram = _overlay_spec_fn
        overlay_stereo_eq_bar = _overlay_stereo_fn
        log("audio viz module loaded (spectrogram + stereo_eq)")
    except Exception as e:
        log(f"audio viz module load fail (overlay disabled): {e}", "WARN")

    try:
        while True:
            if not cfg.get("enabled"):
                cfg = sleep_until_enabled()
                # 標記 started_at 給外部知 daemon 開始當前 capture session
                cfg["started_at"] = datetime.datetime.utcnow().isoformat() + "Z"
                save_config(cfg)

            t0 = time.monotonic()
            try:
                # T13 — 先 grab 算 size, 再依 sensitive flag 決定 blackout 或正常輸出
                # 物理意義: 即使敏感模式下也走完整 grab → resize, 維持 frame 節奏穩定 + size 跟其他 frames 一致
                # 數值影響: 敏感模式下浪費 grab CPU 但保 ring buffer 一致性 (簡單勝過 optimize)
                img = grab_and_resize(cfg["monitor"], cfg["resolution"], monitors_cache)
                sensitive_now = is_sensitive_active()
                if sensitive_now:
                    img = make_blackout_image(img.size, reason_hint="sensitive page in foreground")
                else:
                    # T-AudioViz (summit 2026-06-08) — Audio Spectrogram overlay
                    # 物理意義: 截圖前疊 spectrogram strip 到角落, sensitive 模式跳過 (一致性)
                    # 數值影響: fail-soft — viz 任何 exception 不影響原始 frame 落盤
                    if cfg.get("audio_viz_enabled", False) and audio_capture is not None:
                        try:
                            viz_mode = cfg.get("audio_viz_mode", "spectrogram")
                            position = cfg.get("audio_viz_position", "bottom-right")
                            if viz_mode == "stereo_eq" and overlay_stereo_eq_bar is not None:
                                L_n, R_n, peak_n = audio_capture.snapshot_stereo()
                                # stereo_eq 預設鋪底全寬 — 除非 cfg 明示 position
                                stereo_position = position if position != "bottom-right" else "bottom-stretch"
                                img = overlay_stereo_eq_bar(img, L_n, R_n, peak_n, position=stereo_position)
                            elif overlay_spectrogram is not None:
                                cols = audio_capture.snapshot()
                                img = overlay_spectrogram(img, cols, position=position)
                            # alpha_composite 返 RGBA, JPEG 不支援 alpha → convert RGB
                            if img.mode != "RGB":
                                img = img.convert("RGB")
                        except Exception as e:
                            log(f"audio viz overlay fail: {e}", "WARN")
                # Ring buffer: frame_idx 從 1 開始, mod max_frames
                frame_idx = (cfg["frame_count"] % cfg["max_frames"]) + 1
                target = FRAMES_DIR / f"frame_{frame_idx:04d}.jpg"
                atomic_write_jpeg(img, target, cfg["quality"])
                cfg["frame_count"] += 1
                try:
                    update_latest(frame_idx, target)
                except Exception as e:
                    log(f"update_latest fail: {e}", "WARN")

                # T-OCR-Pipeline — frame 落盤即 submit 背景 OCR (錄製時就產字幕 cache)
                # 物理意義: submit O(1) 非阻塞, 真 OCR 在 worker threads; blackout 幀跳過 (沒字幕可讀)
                if ocr_pool is not None and not sensitive_now:
                    try:
                        ocr_pool.submit(target)
                    except Exception as e:
                        log(f"ocr submit fail: {e}", "WARN")
                
                # 每 30 frame 才存 config (省 disk write)
                if cfg["frame_count"] % 30 == 0:
                    save_config(cfg)
                consecutive_errors = 0
            except Exception as e:
                consecutive_errors += 1
                log(f"capture fail ({consecutive_errors}): {e}", "ERROR")
                if consecutive_errors >= 5:
                    log("5 consecutive failures, backing off 30s", "WARN")
                    time.sleep(30)
                    consecutive_errors = 0

            # 控制 fps: 補足 1/fps 秒
            target_period = 1.0 / max(0.1, cfg.get("fps", 1))
            elapsed = time.monotonic() - t0
            sleep_remaining = target_period - elapsed
            if sleep_remaining > 0:
                time.sleep(sleep_remaining)

            # Re-read config (toggle on/off 感應)
            # T11 bug fix (2026-05-16 dogfood-found by Tim/Zeta):
            # 物理意義: daemon 的 frame_count 是 runtime state, 每 30 frame 才 save_config.
            #          原本 reload 整覆蓋 cfg dict → frame_count 被磁碟舊值蓋回 → 永遠卡在 0
            #          → ring buffer 沒 rotate → frame_0001.jpg 被無限覆寫 → 沒 10min 歷史.
            # 數值影響: 保留 runtime frame_count, 只 reload 其他 user-editable 欄位
            saved_frame_count = cfg.get("frame_count", 0)
            cfg = load_config()
            # 取兩者較大值 (若磁碟 frame_count > runtime 表示 save_config 後 daemon 重啟過; 否則 runtime 是 truth)
            cfg["frame_count"] = max(saved_frame_count, cfg.get("frame_count", 0))

            # T17 (2026-05-18 gura) — 緊急中斷直播 lock 偵測
            # 物理意義: Editor 端 UCL_ScreenStreamGuard.WriteStopLock 寫入 _stop.lock 檔 → 本段 read & process & unlink
            # 數值影響: 偵測到 lock → force cfg.enabled=false, save_config, 刪 lock, 下個 loop iteration 跑 sleep_until_enabled
            #          下個 iteration 透過 T15 transition 邏輯廣播酒保「stop」
            if STOP_LOCK_PATH.exists():
                try:
                    lock_content = STOP_LOCK_PATH.read_text(encoding="utf-8", errors="ignore").strip()
                    log(f"⛔ stop.lock detected → forcing enabled=false. reason: {lock_content[:200]}")
                    cfg["enabled"] = False
                    save_config(cfg)
                    STOP_LOCK_PATH.unlink()
                    log("stop.lock consumed (file removed)")
                except Exception as e:
                    log(f"stop.lock process fail: {e}", "ERROR")

            # T15 — 偵測 enabled transition 觸發酒保廣播
            curr_enabled = bool(cfg.get("enabled", False))
            if curr_enabled != last_enabled:
                if curr_enabled:
                    post_bartender_announce("start", cfg, monitors_cache)
                    write_live_info(cfg)      # 落檔暫存本場直播資訊 (存活期 = 直播期間)
                else:
                    post_bartender_announce("stop", cfg, monitors_cache)
                    clear_live_info()         # 直播結束即清 — 檔案存在與否 = 是否直播中
                last_enabled = curr_enabled

            # T-AudioViz — AudioCapture lifecycle 跟 audio_viz_enabled 同步
            # 物理意義: viz toggle on → start capture thread; off → stop 釋放音訊裝置
            # 數值影響: capture 失敗不影響 daemon 本體 (log warn, viz 自動 skip)
            curr_audio_viz = bool(cfg.get("audio_viz_enabled", False)) and overlay_spectrogram is not None
            if curr_audio_viz != last_audio_viz_enabled:
                if curr_audio_viz:
                    try:
                        from screenstream_audio_viz import AudioCapture
                        audio_capture = AudioCapture()
                        audio_capture.start()
                        log("audio capture started")
                    except Exception as e:
                        log(f"audio capture start fail: {e}", "WARN")
                        audio_capture = None
                else:
                    if audio_capture is not None:
                        try:
                            audio_capture.stop()
                            log("audio capture stopped")
                        except Exception as e:
                            log(f"audio capture stop fail: {e}", "WARN")
                        audio_capture = None
                last_audio_viz_enabled = curr_audio_viz
            # 偵測 capture 內部錯誤 — 若 thread fail, 印一次 warn
            if audio_capture is not None and audio_capture.error:
                log(f"audio capture thread error (will fail-soft): {audio_capture.error}", "WARN")
                audio_capture = None  # 避免反覆 spam warn
                last_audio_viz_enabled = False  # 允許 next toggle 重 init

            # T-OCR-Pipeline — OCR pool lifecycle 跟 ocr_enabled toggle 同步
            # 物理意義: toggle on → 起 worker pool (per-thread RapidOCR engine); off → stop 釋放 threads
            # 數值影響: y/h/conf/workers 改動需 toggle off→on 重起 pool 才生效 (跟 audio viz 同慣例)
            curr_ocr_enabled = bool(cfg.get("ocr_enabled", False))
            # T-OCR-AutoRestart (Tim 2026-07-27) — 對偶 T-STT-AutoRestart：pool 運行中偵測 band/conf/workers
            #   任一改變 → 自動 stop 讓下方 enabled-transition 以新設定重起。消滅「改字幕帶位置按套用卻沒 toggle
            #   = 靜默沿用舊 band」bug (OcrWorkerPool 的 regions/conf/workers 綁建構子, 中途不可熱改)。
            # regions 快照 (2026-07-28 底部原點 + 額外區域): regions_from_config 已正規化 round(4),
            #   tuple 化後可直接比相等 — 主帶或任一額外區域增刪改都會觸發重起。
            try:
                from subtitle_ocr import regions_from_config as _regions_from_config
                curr_ocr_regions = tuple(_regions_from_config(cfg))
            except Exception:
                curr_ocr_regions = ((0.0, 0.12),)
            curr_ocr_band = (
                curr_ocr_regions,
                round(float(cfg.get("ocr_min_conf", 0.5)), 4),
                int(cfg.get("ocr_workers", 2)),
            )
            if ocr_pool is not None and last_ocr_band is not None and curr_ocr_band != last_ocr_band:
                log(f"ocr 設定改變 → 自動重起 pool 套用 (regions={curr_ocr_band[0]} "
                    f"min_conf={curr_ocr_band[1]} workers={curr_ocr_band[2]})")
                try:
                    ocr_pool.stop()
                except Exception as e:
                    log(f"ocr pool stop fail (auto-restart): {e}", "WARN")
                ocr_pool = None
                last_ocr_enabled = False   # 强制走下方 enabled-transition 的啟動分支重起
            last_ocr_band = curr_ocr_band
            if curr_ocr_enabled != last_ocr_enabled:
                if curr_ocr_enabled:
                    try:
                        from subtitle_ocr import OcrWorkerPool, is_available as _ocr_avail, get_init_error
                        if not _ocr_avail():
                            log(f"ocr pool start skip (engine 不可用): {get_init_error()}", "WARN")
                            ocr_pool = None
                        else:
                            from subtitle_ocr import regions_from_config
                            ocr_pool = OcrWorkerPool(
                                OCR_CACHE_DIR,
                                regions=regions_from_config(cfg),
                                min_confidence=float(cfg.get("ocr_min_conf", 0.5)),
                                workers=int(cfg.get("ocr_workers", 2)),
                                adaptive=bool(cfg.get("ocr_adaptive", True)),
                            )
                            ocr_pool.start()
                            log(f"ocr worker pool started (workers={ocr_pool.workers}, "
                                f"regions={ocr_pool.regions})")
                    except Exception as e:
                        log(f"ocr pool start fail: {e}", "WARN")
                        ocr_pool = None
                else:
                    if ocr_pool is not None:
                        try:
                            ocr_pool.stop()
                            log(f"ocr worker pool stopped (processed={ocr_pool.processed}, "
                                f"dropped={ocr_pool.dropped}, errors={ocr_pool.errors})")
                        except Exception as e:
                            log(f"ocr pool stop fail: {e}", "WARN")
                        ocr_pool = None
                last_ocr_enabled = curr_ocr_enabled
            # worker engine init 全滅偵測 — 印一次 warn 後關掉避免 spam
            if ocr_pool is not None and ocr_pool.errors > 0 and ocr_pool.processed == 0 and ocr_pool.last_error:
                if not ocr_pool._threads or all(not t.is_alive() for t in ocr_pool._threads):
                    log(f"ocr pool all workers dead (will fail-soft): {ocr_pool.last_error}", "WARN")
                    ocr_pool = None
                    last_ocr_enabled = False  # 允許 next toggle 重 init

            # T-STT-Cache (Quest T07, kotoko 2026-07-05) — STT worker lifecycle 跟 stt_enabled toggle 同步
            # 物理意義: toggle on → 起 SttCacheWorker (自開 loopback 連續錄 chunk 轉錄寫 stt cache);
            #          off → stop 釋放 thread + 卸 whisper。
            # 數值影響: whisper GPU 常駐 (small ~460MB VRAM); fail-soft — 依賴缺只 log WARN, 不影響截圖主流程。
            curr_stt_enabled = bool(cfg.get("stt_enabled", False))
            # T-STT-AutoRestart (Tim 2026-07-20) — worker 運行中偵測 (model, lang, prompt) 任一改變 →
            #   自動 stop 讓下方 enabled-transition 分支以新設定重起, 消滅「改設定沒 toggle = 靜默沿用舊值」整族 bug。
            # 物理意義: worker 的 model/lang/prompt 綁建構子生命週期, 中途不可熱改 — 故設定變更 = 必須換一顆 worker。
            # 數值影響: 重起成本 = 卸載+重載 whisper model (~數秒), 只在設定真的變了才發生; chunk 銜接損失 ≤1 chunk。
            curr_stt_cfg = (
                str(cfg.get("stt_model", "small")),
                str(cfg.get("stt_lang") or ""),
                str(cfg.get("stt_prompt") or "").strip(),
            )
            if stt_worker is not None and curr_stt_cfg != last_stt_cfg:
                log(f"stt 設定改變 → 自動重起 worker 套用 (model={curr_stt_cfg[0]}, lang='{curr_stt_cfg[1]}', "
                    f"prompt='{curr_stt_cfg[2][:30]}')")
                try:
                    stt_worker.stop()
                except Exception as e:
                    log(f"stt worker stop fail (auto-restart): {e}", "WARN")
                stt_worker = None
                last_stt_enabled = False   # 强制走下方 enabled-transition 的啟動分支重起
            if curr_stt_enabled != last_stt_enabled:
                if curr_stt_enabled:
                    try:
                        from audio_transcribe import SttCacheWorker, is_available as _stt_avail, init_error as _stt_err
                        if not _stt_avail():
                            log(f"stt worker start skip (whisper 不可用): {_stt_err()}", "WARN")
                            stt_worker = None
                        else:
                            # progress_cb: 每寫一個 chunk 記一筆 daemon log (每 5 chunk 印一次避免洗版)
                            def _stt_progress(n, n_segs, end_ep):
                                if n % 5 == 1:
                                    log(f"stt cache: {n} chunk 已寫 (最新 {n_segs} 段)")
                            _stt_prompt = str(cfg.get("stt_prompt") or "").strip()
                            stt_worker = SttCacheWorker(
                                STT_CACHE_DIR,
                                model_size=str(cfg.get("stt_model", "small")),
                                language=(cfg.get("stt_lang") or None),
                                chunk_sec=float(cfg.get("stt_chunk_sec", 15)),
                                progress_cb=_stt_progress,
                                prompt=_stt_prompt,
                                # T-STT-Watchdog: worker 內部失敗 (擷取炸/恢復) 直通 daemon log, 禁靜默
                                warn_cb=lambda m: log(m, "WARN"),
                            )
                            stt_worker.start()
                            # T-STT-AutoRestart: 記下這顆 worker 實際吃到的設定快照, 供上方變更偵測比對
                            last_stt_cfg = curr_stt_cfg
                            # T-STT-Watchdog: 重置停滯計時 (新 worker 從 0 起算)
                            stt_last_chunk_count = stt_worker.chunk_count
                            stt_last_progress_ts = time.time()
                            _pnote = f", prompt='{_stt_prompt[:40]}…'" if _stt_prompt else ""
                            log(f"stt cache worker started (model={stt_worker.model_size}, "
                                f"lang='{cfg.get('stt_lang') or ''}', chunk={stt_worker.chunk_sec}s{_pnote})")
                    except Exception as e:
                        log(f"stt worker start fail: {e}", "WARN")
                        stt_worker = None
                else:
                    if stt_worker is not None:
                        try:
                            stt_worker.stop()
                            log("stt cache worker stopped")
                        except Exception as e:
                            log(f"stt worker stop fail: {e}", "WARN")
                        stt_worker = None
                last_stt_enabled = curr_stt_enabled
            # STT worker 早死偵測 (擷取/模型失敗) — 印一次 warn 後關掉允許重 toggle
            if stt_worker is not None and stt_worker.error() and (
                    stt_worker._thread is None or not stt_worker._thread.is_alive()):
                log(f"stt worker dead (will fail-soft): {stt_worker.error()}", "WARN")
                stt_worker = None
                last_stt_enabled = False
            # T-STT-Watchdog (2026-07-27) — 殭屍偵測: thread 活著但 chunk 產出停滯。
            # 物理意義: 上方 dead 偵測只抓「thread 已死」; 擷取失敗重試迴圈 thread 永遠活著 →
            #          音訊堆疊 process 級壞死時 STT 靜默停擺 (血證: 2026-07-27 停擺 2h 零警訊)。
            #          停滯門檻 = max(60s, 4×chunk_sec): 正常節奏每 chunk_sec 必有一筆, 4 倍容忍轉錄慢。
            # 數值影響: 殭屍 → 重起 worker (model 有 module 級快取, 重起成本低; capture_live 每 chunk
            #          重挑 default speaker, 裝置暫時性失效可自癒)。連續 3 次重起仍無產出 → 升級 ERROR
            #          (疑似 process 級壞死, 只有重啟 daemon 救得回); 之後每 10 次才再叫, 防洗版。
            if stt_worker is not None and stt_worker._thread is not None and stt_worker._thread.is_alive():
                _now_ts = time.time()
                if stt_worker.chunk_count != stt_last_chunk_count:
                    stt_last_chunk_count = stt_worker.chunk_count
                    stt_last_progress_ts = _now_ts
                    stt_zombie_restarts = 0
                elif _now_ts - stt_last_progress_ts > max(60.0, stt_worker.chunk_sec * 4):
                    stt_zombie_restarts += 1
                    _stall = _now_ts - stt_last_progress_ts
                    _werr = stt_worker.error() or "(worker 未記錯誤)"
                    if stt_zombie_restarts <= 3 or stt_zombie_restarts % 10 == 0:
                        _esc = (" — 連續多次重起無效, 疑似 process 級音訊堆疊壞死, 需重啟 daemon 才能恢復"
                                if stt_zombie_restarts >= 3 else "")
                        log(f"stt watchdog: worker 活著但 {_stall:.0f}s 無 chunk 產出 "
                            f"(第 {stt_zombie_restarts} 次殭屍重起); 最後錯誤: {_werr}{_esc}",
                            "ERROR" if stt_zombie_restarts >= 3 else "WARN")
                    try:
                        stt_worker.stop()
                    except Exception as e:
                        log(f"stt watchdog stop fail: {e}", "WARN")
                    stt_worker = None
                    last_stt_enabled = False   # 走上方 enabled-transition 啟動分支重起
                    stt_last_progress_ts = _now_ts
            # (舊版 T-STT-Prompt「改了沒重起只 log WARN」偵測已由上方 T-STT-AutoRestart 自動重起機制取代)

            # T-AudioLog (Tim 2026-06-08, summit ship) — 每 N frame 觸發 dump audio log
            # 物理意義: 給 screenstream_montage.py 載入後可按 cycle 區間 slice 渲染 audio strip
            # 數值影響: 寫 .npz ~1MB, atomic 換檔; 失敗只 log WARN, daemon 主流程不阻塞
            # fail-soft: capture 不存在 / dump 失敗都不影響 frame 寫盤
            if (audio_capture is not None
                    and cfg["frame_count"] > 0
                    and cfg["frame_count"] % AUDIO_LOG_DUMP_EVERY_N_FRAMES == 0):
                try:
                    result = audio_capture.dump_log(AUDIO_LOG_PATH)
                    if not result.get("ok"):
                        # empty ring (剛 start 還沒推資料) 不算 warn — 只有真錯才 log
                        err = result.get("error", "")
                        if err and err != "empty":
                            log(f"audio log dump fail: {err}", "WARN")
                except Exception as e:
                    log(f"audio log dump unexpected fail: {e}", "WARN")
    except KeyboardInterrupt:
        log("KeyboardInterrupt → shutdown")
        return 0
    except Exception as e:
        log(f"main loop crash: {e}", "ERROR")
        return 1
    finally:
        # 退出前存 final config + 清 PID + 停 AudioCapture
        try:
            save_config(cfg)
        except Exception:
            pass
        if audio_capture is not None:
            try: audio_capture.stop()
            except Exception: pass
        if ocr_pool is not None:
            try: ocr_pool.stop()
            except Exception: pass
        if stt_worker is not None:
            try: stt_worker.stop()
            except Exception: pass
        cleanup_pid()


def enum_monitors_oneshot() -> int:
    """--enum-monitors 一次性模式: 枚舉螢幕寫 _monitors.json 即退, 不進 main loop、不寫 PID。

    # 區塊職責: 給 Editor 端「daemon 未運行時」預熱/刷新螢幕清單快取 (Tim 2026-07-28)
    # 物理意義: daemon 存活已綁 config.enabled (停止錄影即收掉), _monitors.json 只在 daemon 啟動時寫
    #          → 全新環境 / 熱插拔外接螢幕在未錄影期間拿不到清單。本模式讓 Editor 啟動時
    #          與頁面「🔄 刷新」鈕都能秒級補寫快取。
    # 數值影響: 只寫 _monitors.json 一檔; 與運行中 daemon 併寫同檔的機率極低且內容同源, 無害。
    """
    ensure_dirs()
    monitors = enumerate_monitors()
    write_monitors_file(monitors)
    print(f"enum-monitors: {len(monitors)} monitor(s) → {MONITORS_PATH}")
    return 0


if __name__ == "__main__":
    if "--enum-monitors" in sys.argv:
        sys.exit(enum_monitors_oneshot())
    sys.exit(main_loop())
