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
# ==== T-SSREC-01 錄播模式 (Tim 2026-08-01 拍板; 規格見 Docs~/zh-Hant/Plan/Plan_ScreenStream_Recording_Mode.md) ====
# 物理意義: 直播是 ring (繞回去覆寫), 錄播是流 (不繞、不覆寫) —— 兩條各自單純, 不互相遷就。
#          資料夾**開錄即命名** (recordings/<名稱>/)，所以沒有 rename 那一步 ——
#          名稱在開錄時就已經知道 (後台欄位填的)，繞一圈反而不直覺 (Tim 2026-08-01 實測回饋)。
#          rename 是同磁碟 O(1) metadata 操作 → 沒有複製、就沒有「匯出時與 writer 競爭」的問題。
# 邊界: 錄播必須寫獨立資料夾 —— 它的 index 從 1 遞增, 會直接撞上 ring 的槽位 1,2,3… 互相覆蓋。
# 2026-08-01 修正 (Tim 實測回饋「我以為會根據這個名稱錄製到資料夾」):
#   原設計是「寫固定路徑 recording/、停錄才 rename 成名稱」。但名稱在**開錄時就已經知道**
#   (Tim 在後台欄位填了)，所以直接用它建資料夾更直覺 —— 而且省掉 rename 那一步，
#   連帶消除 Windows「目錄內有開啟 handle 時 rename 被拒」的風險。名稱留空才退回時間戳。
RECORDINGS_DIR = STREAM_DIR / "recordings"          # 每段錄製一個資料夾: recordings/<名稱>/
REC_LEGACY_DIR = STREAM_DIR / "recording"           # 舊版固定路徑 — 開機自癒會把殘留搬進 recordings/
REC_MANIFEST_NAME = "manifest.json"
# 檔名寬度: 錄播不 wrap, @1fps 約 2.8 小時就衝破 4 位數 (且 :04d 是最小寬度不截斷 →
# 跨位數時字典序會崩: frame_10000 排在 frame_9999 前面)。6 位 @1fps 撐 11.6 天。
FRAME_NAME_WIDTH = 6
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
    # ==== T-SSREC-01 錄播模式開關 (Tim 2026-08-01) ====
    # 物理意義: true → 每張 frame 除了寫 ring buffer, 另外寫一份進 recording/ (不 wrap、不覆寫)。
    #          直播 loop 完全不受影響 —— 陪看的 montage 照樣讀 ring buffer (雙寫, 非二選一)。
    # 數值影響: 多一次寫檔 (68 KB/幀實測) ≈ 245 MB/小時; 由 recording_stop_free_mb 兜底防塞爆磁碟。
    # 邊界: 由 false→true 開新錄製段; true→false 停錄 (rename 成品 + 重建空資料夾)。
    # ==== 靜音幻覺防治門檻 (T-STT-Silence, 2026-08-01) ====
    # 物理意義: whisper 對無語音音軌會自信地吐字 (Tim 首試錄實證: 14 段全是「3/2/1」)。
    #          三層防治的門檻放這裡讓 Tim QA 時能調, 不必改 code。
    # 數值影響: rms_gate 0 = 停用前置閘; no_speech_max / logprob_min 對齊 whisper 官方預設。
    "stt_rms_gate": 0.005,        # 低於此 RMS 的 chunk 根本不送 whisper (約 -46 dBFS)
    "stt_no_speech_max": 0.6,     # segment 的 no_speech_prob 超過即丟
    "stt_logprob_min": -1.0,      # segment 的 avg_logprob 低於即丟
    "recording_enabled": False,
    "recording_name": "",          # 停錄時的資料夾名稱; 空 → 用起始時間戳 (之後手動改名不影響任何機制)
    "recording_stop_free_mb": 1024,  # 剩餘磁碟低於此值 → 自動停錄 (視同一次正常結束, 不是崩)
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
    # 實際跑轉錄的工具（UCL_ScreenStreamPage 的「後端」下拉寫入）。
    # openai-whisper=現行(torch) / faster-whisper=CTranslate2(有 vad_filter、可量化)。
    "stt_backend": "openai-whisper",
    # Silero VAD 前置切靜音。⚠ 僅 faster-whisper 支援；其他後端下 supervisor 不會帶這個旗標。
    "stt_vad_filter": False,
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


# ===========================================================
# 區塊職責：T-SSREC-01 錄播模式 — 狀態機、manifest、開機自癒、停錄三步
# 物理意義：錄播是一條「不輪替的流」。它與 ring buffer 的差別只有一個：不取 mod。
#          但因此衍生三件必須處理的事 —— 獨立資料夾（否則撞 ring 槽位）、
#          6 位數檔名（不 wrap 會衝破 4 位）、以及「中斷也要留下痕跡」。
# 數值影響：index 嚴格不跳號（寫檔成功才配發下一號）。錄播的 index 是**播放軸**不是牆鐘測量 ——
#          Tim 拍板：觀看錄播沒有時間壓力、可逐張看，所以連續性 > 牆鐘精度。
#          牆鐘軸零成本另外有：檔案 mtime。兩軸各司其職，manifest 記 started_at 讓漂移可觀測。
# 邊界：中斷（daemon 被砍 / Editor 崩 / 斷電）當下沒有任何 code 跑得到收尾 →
#      不能依賴「結束時寫 stopped_at」。改用狀態機 + 開機自癒（見 recording_selfheal）。
# ===========================================================
_rec_state = {"active": False, "next_index": 1, "started_at": None, "started_epoch": 0.0,
              "dir": None, "ocr_pool": None, "offsets": []}


def _rec_dir() -> Path:
    d = _rec_state.get("dir")
    return Path(d) if d else (RECORDINGS_DIR / "_unnamed")


def _rec_manifest_path() -> Path:
    return _rec_dir() / REC_MANIFEST_NAME


def _sanitize_name(name: str) -> str:
    """檔名安全化 — Windows 禁字元 + 去頭尾空白; 空 → 時間戳由 caller 決定。"""
    bad = '\/:*?"<>|'
    return "".join(c for c in (name or "") if c not in bad).strip()


def _rec_write_manifest(cfg: dict, status: str, **extra) -> None:
    """寫 manifest。status: recording / complete / interrupted。fail-soft 不擋錄影。"""
    try:
        _rec_dir().mkdir(parents=True, exist_ok=True)
        data = {
            "status": status,
            "started_at": _rec_state.get("started_at"),
            # nominal = config 設定值; actual = 實測值。
            # 血證 (Tim 2026-08-01 首次試錄): config 寫 fps=60，實際只有 2.29 fps —— 差 26 倍。
            # 擷取跟不上設定值是常態 (grab+resize+JPEG 一輪 300ms+)，而 t(i)=i/fps 這條換算
            # 完全依賴分母是真的。播放端一律用 actual_fps，nominal 只留作參考。
            "fps": cfg.get("fps", 1),
            "nominal_fps": cfg.get("fps", 1),
            "actual_fps": _rec_actual_fps(),
            "frame_count": _rec_state.get("next_index", 1) - 1,
            "last_index": _rec_state.get("next_index", 1) - 1,
            # 錄製**當下**的值 —— 事後沒字幕要分得出「當時沒開」還是「開了但辨識失敗」
            "ocr_enabled": bool(cfg.get("ocr_enabled", False)),
            "stt_enabled": bool(cfg.get("stt_enabled", False)),
            "monitor": cfg.get("monitor"),
            "resolution": cfg.get("resolution"),
            "frame_name_width": FRAME_NAME_WIDTH,
            "title": cfg.get("recording_name", "") or None,
        }
        data.update(extra)
        tmp = _rec_manifest_path().with_suffix(".json.tmp")
        tmp.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        os.replace(tmp, _rec_manifest_path())
    except Exception as e:
        log(f"recording manifest write fail: {e}", "WARN")


def _rec_actual_fps() -> float:
    """從實際 offsets 算真實擷取速率（不足 2 幀 → 0.0 表示未知，不假裝）。"""
    offs = _rec_state.get("offsets") or []
    if len(offs) < 2:
        return 0.0
    span = (offs[-1] - offs[0]) / 1000.0
    return round((len(offs) - 1) / span, 3) if span > 0 else 0.0


def _rec_flush_offsets() -> None:
    """每幀的實際相對時間落 sidecar（frames.jsonl）。

    為什麼不只靠 fps 換算：fps 是名目值且實測差到 26 倍；而且幀距本身會抖
    （實測中位數 378ms、最大 5710ms 的卡頓）。offsets 是唯一能還原真實時間軸的東西，
    且它跟著資料夾走 —— 複製 / 搬移不會像檔案 mtime 那樣被弄丟。
    """
    try:
        offs = _rec_state.get("offsets") or []
        if not offs:
            return
        lines = "".join(json.dumps({"i": i + 1, "offset_ms": ms}) + "\n" for i, ms in enumerate(offs))
        (_rec_dir() / "frames.jsonl").write_text(lines, encoding="utf-8")
    except Exception as e:
        log(f"recording offsets flush fail: {e}", "WARN")


def recording_start(cfg: dict) -> None:
    """開錄：資料夾**開錄即命名**（Tim 2026-08-01 實測回饋）+ 立刻寫 status=recording 的 manifest。

    名稱取自 config.recording_name；留空 → 起始時間戳。撞名自動加 _2/_3。
    同時建 ocr/ 與 stt/ 子目錄 —— 錄播的字幕快取必須跟直播分開存：
    兩邊的 frame 都叫 frame_000001.jpg，而 OCR cache 檔名是 `<frame stem>.json`，
    共用目錄會直接互蓋（2026-08-01 首版實作踩到的真 bug）。
    """
    try:
        RECORDINGS_DIR.mkdir(parents=True, exist_ok=True)
        started_epoch = time.time()
        name = _sanitize_name(cfg.get("recording_name", ""))
        if not name:
            name = time.strftime("%Y%m%d_%H%M%S", time.localtime(started_epoch))
        d = RECORDINGS_DIR / name
        n = 2
        while d.exists():
            d = RECORDINGS_DIR / f"{name}_{n}"
            n += 1
        d.mkdir(parents=True, exist_ok=True)
        (d / "ocr").mkdir(exist_ok=True)
        (d / "stt").mkdir(exist_ok=True)

        _rec_state["dir"] = str(d)
        _rec_state["active"] = True
        _rec_state["next_index"] = 1
        _rec_state["offsets"] = []
        _rec_state["started_epoch"] = started_epoch
        _rec_state["started_at"] = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
        _rec_state["ocr_pool"] = _rec_start_ocr_pool(cfg, d)
        _rec_write_manifest(cfg, "recording")
        log(f"recording START → recordings/{d.name}")
    except Exception as e:
        _rec_state["active"] = False
        log(f"recording start fail: {e}", "ERROR")


def _rec_start_ocr_pool(cfg: dict, rec_dir: Path):
    """錄播專用 OCR pool（cache 落 <rec>/ocr/）。

    刻意獨立一個 pool 而不是共用直播那顆：OcrWorkerPool 的 cache 路徑是建構時綁死的
    `cache_dir / (frame.stem + '.json')`，而兩邊 frame 同名 → 共用必互蓋。
    workers 固定 1（錄播是背景工作，不跟直播搶 CPU）。
    """
    if not cfg.get("ocr_enabled", False):
        return None
    try:
        from subtitle_ocr import OcrWorkerPool, regions_from_config, is_available
        if not is_available():
            return None
        pool = OcrWorkerPool(
            rec_dir / "ocr",
            regions=regions_from_config(cfg),
            min_confidence=float(cfg.get("ocr_min_conf", 0.5)),
            workers=1,
            adaptive=bool(cfg.get("ocr_adaptive", True)),
        )
        pool.start()
        log("recording ocr pool started (workers=1, cache→<rec>/ocr/)")
        return pool
    except Exception as e:
        log(f"recording ocr pool start fail: {e}", "WARN")
        return None


def _rec_copy_stt() -> int:
    """把錄製時間窗內的 STT chunk 複製一份進 <rec>/stt/。

    STT 是**牆鐘戳**的（stt/stt_<epoch>.json），跟 frame index 不同軸 —— 所以用起訖 epoch 篩。
    複製而非移動：直播端仍需要它們。窗界各放寬 30 秒，避免邊界 chunk 被切掉。
    只複製、不重算 —— 事後重算的時間戳對不上原始錄製時刻，混進去就再也分不出哪些是當場的。
    """
    n = 0
    try:
        if not STT_CACHE_DIR.is_dir():
            return 0
        lo = (_rec_state.get("started_epoch") or 0) - 30
        hi = time.time() + 30
        dest = _rec_dir() / "stt"
        dest.mkdir(parents=True, exist_ok=True)
        for f in STT_CACHE_DIR.glob("stt_*.json"):
            try:
                ep = float(f.stem.split("_")[-1])
            except ValueError:
                continue
            # ⚠ 檔名的 epoch 是**毫秒**（13 位），不是秒 —— 2026-08-01 首版寫成秒直接比對，
            # 結果窗永遠命不中、靜默複製 0 筆。而我的沙箱 fixture 又是自己用秒造的，
            # 於是測試「驗證了我的假設」而不是真實格式，一路綠燈到 Tim 實錄才發現。
            # 這裡做寬容判斷而非寫死單位：>1e12 視為毫秒（秒要到西元 33658 年才會有 13 位）。
            if ep > 1e12:
                ep /= 1000.0
            if lo <= ep <= hi:
                shutil.copy2(f, dest / f.name)
                n += 1
    except Exception as e:
        log(f"recording stt copy fail: {e}", "WARN")
    return n


def recording_stop(cfg: dict, status: str = "complete") -> None:
    """停錄：關 OCR pool → 複製 STT → 落 offsets → 收尾 manifest。

    資料夾在開錄時就已命名，所以**沒有 rename 這一步**（連帶沒有 Windows 目錄改名被拒的風險）。
    """
    if not _rec_state.get("active") and not _rec_state.get("dir"):
        return
    _rec_state["active"] = False
    frames = _rec_state.get("next_index", 1) - 1
    pool = _rec_state.get("ocr_pool")
    if pool is not None:
        try: pool.stop()
        except Exception: pass
        _rec_state["ocr_pool"] = None
    stt_n = _rec_copy_stt()
    _rec_flush_offsets()
    stopped_at = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
    _rec_write_manifest(cfg, status, stopped_at=stopped_at, stt_chunks=stt_n)
    log(f"recording STOP({status}) → recordings/{_rec_dir().name}（{frames} 幀 / STT {stt_n} chunk / "
        f"實測 {_rec_actual_fps()} fps）")
    _rec_state["dir"] = None


def recording_selfheal(cfg: dict) -> None:
    """開機自癒：上次沒收乾淨的錄製段標成 interrupted。

    「中斷視同錄製結束」預設了「結束時有人寫得下 stopped_at」——
    而中斷最常見的形態（daemon 被砍 / Editor 崩 / 斷電）根本跑不到那一行。
    所以痕跡靠**下次啟動**補：掃 recordings/*/manifest.json，status 還是 recording 的就收掉。
    last_index 由檔名推導（檔名就是序號，一行 max）。
    """
    # 舊版固定路徑殘留（2026-08-01 改版前的 recording/）→ 搬進 recordings/ 一併收尾
    try:
        if REC_LEGACY_DIR.is_dir() and any(REC_LEGACY_DIR.glob("frame_*.jpg")):
            RECORDINGS_DIR.mkdir(parents=True, exist_ok=True)
            dest = RECORDINGS_DIR / ("legacy_" + time.strftime("%Y%m%d_%H%M%S"))
            os.rename(REC_LEGACY_DIR, dest)
            log(f"錄播舊版殘留 recording/ → 搬進 recordings/{dest.name}", "WARN")
    except Exception as e:
        log(f"legacy recording dir migrate fail: {e}", "WARN")

    if not RECORDINGS_DIR.is_dir():
        return
    for d in RECORDINGS_DIR.iterdir():
        mp = d / REC_MANIFEST_NAME
        if not (d.is_dir() and mp.exists()):
            continue
        try:
            data = json.loads(mp.read_text(encoding="utf-8"))
            if data.get("status") != "recording":
                continue
            idxs = [int(f.stem.split("_")[-1]) for f in d.glob("frame_*.jpg")]
            last = max(idxs) if idxs else 0
            data["status"] = "interrupted"
            data["frame_count"] = last
            data["last_index"] = last
            data.setdefault("note", "daemon 重啟時偵測到未正常收尾 → 標記 interrupted（stopped_at 未知，以 last_index 為準）")
            mp.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
            log(f"recording self-heal: recordings/{d.name} 未正常收尾（{last} 幀）→ interrupted", "WARN")
        except Exception as e:
            log(f"recording self-heal fail ({d.name}): {e}", "WARN")


def migrate_frame_name_width() -> None:
    """4 位數 → 6 位數一次性遷移：偵測到舊格式就清空 frames/。

    ring buffer 本來就是 ephemeral（只留最近 N 秒），清掉零損失；
    不清的話新舊檔名會混在同一資料夾直到輪替完，期間任何按檔名排序的讀取都會漂。
    """
    try:
        old = [f for f in FRAMES_DIR.glob("frame_*.jpg") if len(f.stem.split("_")[-1]) < FRAME_NAME_WIDTH]
        if not old:
            return
        log(f"frame 檔名寬度遷移（{len(old)} 個舊格式檔）→ 清空 frames/（ring buffer 為 ephemeral，零損失）")
        for f in FRAMES_DIR.glob("frame_*.jpg"):
            try: f.unlink()
            except Exception: pass
    except Exception as e:
        log(f"frame name migration fail: {e}", "WARN")

def migrate_frame_name_width() -> None:
    """4 位數 → 6 位數一次性遷移：偵測到舊格式就清空 frames/。

    ring buffer 本來就是 ephemeral（只留最近 N 秒），清掉零損失；
    不清的話新舊檔名會混在同一資料夾直到輪替完，期間任何按檔名排序的讀取都會漂。
    """
    try:
        old = [f for f in FRAMES_DIR.glob("frame_*.jpg") if len(f.stem.split("_")[-1]) < FRAME_NAME_WIDTH]
        if not old:
            return
        log(f"frame 檔名寬度遷移（{len(old)} 個舊格式檔）→ 清空 frames/（ring buffer 為 ephemeral，零損失）")
        for f in FRAMES_DIR.glob("frame_*.jpg"):
            try: f.unlink()
            except Exception: pass
    except Exception as e:
        log(f"frame name migration fail: {e}", "WARN")


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


# 開播/停播的酒館廣播已搬到 Editor 端 (UCL_ScreenStreamPage.PostStreamAnnounce, 2026-08-04)。
# 原本這裡有一支 post_bartender_announce()，掛在「cfg.enabled false→true」的 transition 上。
# 2026-07-28 daemon 生命週期改成「存活綁 enabled」之後那個 transition 再也不會發生
# (daemon 啟動時 enabled 已是 true; 停播時 daemon 被 kill)，於是兩個廣播一起靜默消失 ——
# 實證: 酒館最後一筆 2026-07-27、daemon log 內 announce 出現 0 次 (該函式成功與失敗都會 log)。
# 整支刪除而不是留著: 它內含一份訊息文案, 留下來就是同一段文字兩處各存一份 (必漂),
# 而且哪天生命週期改回常駐 idle 就會雙發。事件的所有者是那顆按鈕, 不是 daemon 的存活狀態。


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
    # T-SSREC-01: 檔名寬度一次性遷移 + 上次沒收乾淨的錄製段自癒歸檔（見各自 docstring）
    migrate_frame_name_width()
    recording_selfheal(cfg)
    # 錄播開關的 transition 偵測（與 enabled 同機制）
    last_recording = bool(cfg.get("recording_enabled", False))
    if last_recording:
        recording_start(cfg)
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
    # T-STT-AutoRestart (Tim 2026-07-20): worker 運行中的 (model, lang, prompt) 快照 —
    #   config 任一項被改 → 自動 stop + 重起套用, 取代舊版「只 log WARN 等人工 toggle」的靜默失效設計
    #   (血證: 換片後 stt_lang/stt_prompt 殘留上一場, whisper 幻聽出舊片人名)
    # T-STT-Watchdog (2026-07-27 靜默殭屍事故) — worker 產出停滯偵測 state
    # 物理意義: 擷取失敗重試迴圈 (audio_transcribe._loop 空音訊分支) thread 不死,
    #          下方「dead 偵測」(要求 thread 已死) 永遠不觸發 → STT 靜默停擺 2h 無任何警訊。
    #          改追 chunk_count 水位: 停滯超過門檻 = 殭屍, 不管 thread 死活。
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
                # Ring buffer: frame_idx 從 1 開始, mod max_frames（會繞回去覆寫）
                frame_idx = (cfg["frame_count"] % cfg["max_frames"]) + 1
                target = FRAMES_DIR / f"frame_{frame_idx:0{FRAME_NAME_WIDTH}d}.jpg"
                atomic_write_jpeg(img, target, cfg["quality"])
                cfg["frame_count"] += 1

                # ==== T-SSREC-01 錄播雙寫（Tim 2026-08-01）====
                # 物理意義: 同一張截圖再寫一份進 recording/，**不取 mod** → 不繞、不覆寫。
                #          刻意雙寫而非二選一：ring 照常滾 → 陪看的 montage 一行都不用改。
                # 數值影響: index 嚴格不跳號 —— **寫檔成功後才配發下一號**，失敗就用同一號重試。
                #          （「先 ++ 再寫」是更順手的寫法，但掉一張就永久跳號，ffmpeg 之類的
                #            image-sequence 消費端會直接斷在那裡。這行不要被善意地改掉。）
                if _rec_state.get("active"):
                    try:
                        rec_idx = _rec_state["next_index"]
                        rec_target = _rec_dir() / f"frame_{rec_idx:0{FRAME_NAME_WIDTH}d}.jpg"
                        atomic_write_jpeg(img, rec_target, cfg["quality"])
                        _rec_state["next_index"] = rec_idx + 1      # ← 只在寫成功後前進
                        # 每幀實際相對時間（ms）—— 不靠 fps 換算，因為 fps 是名目值
                        # （Tim 首試錄實測 config 寫 60、實際 2.29，差 26 倍）
                        _rec_state["offsets"].append(int((time.time() - _rec_state["started_epoch"]) * 1000))
                        # 錄播的 OCR 走**自己的** pool（cache 落 <rec>/ocr/）——
                        # 與直播共用會互蓋：兩邊 frame 同名，而 cache 檔名是 <frame stem>.json
                        rec_pool = _rec_state.get("ocr_pool")
                        if rec_pool is not None and not sensitive_now:
                            try: rec_pool.submit(rec_target)
                            except Exception: pass
                        # 每 60 幀更新一次 manifest（讓外部看得到進度；崩了也有近期水位）
                        if rec_idx % 60 == 0:
                            _rec_write_manifest(cfg, "recording")
                            _rec_flush_offsets()
                            free_mb = shutil.disk_usage(str(STREAM_DIR)).free / (1024 * 1024)
                            if free_mb < float(cfg.get("recording_stop_free_mb", 1024)):
                                log(f"磁碟剩餘 {free_mb:.0f} MB 低於門檻 → 自動停錄（視同正常結束）", "WARN")
                                recording_stop(cfg, status="complete")
                                cfg["recording_enabled"] = False
                                save_config(cfg)
                    except Exception as e:
                        log(f"recording write fail（本幀跳過，index 不前進）: {e}", "WARN")
                try:
                    update_latest(frame_idx, target)
                except Exception as e:
                    log(f"update_latest fail: {e}", "WARN")

                # (OCR 已移交 C# UCL_OcrWorkerSupervisor — 獨立行程掃 frames 目錄, 本 loop 不再 submit。
                #  敏感畫面本來就寫成黑圖, 掃目錄版讀不到敏感內容; 代價僅多 OCR 幾張黑圖。)
                
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
                    # 顯式戳停止時刻 — 下游結算要的是「什麼時候停的」, 不是「什麼時候被發現的」。
                    # (對偶: UCL_ScreenStreamPage 在 toggle 翻轉時寫同一個欄位。)
                    cfg["enabled_changed_at"] = (
                        datetime.datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z")
                    save_config(cfg)
                    STOP_LOCK_PATH.unlink()
                    log("stop.lock consumed (file removed)")
                except Exception as e:
                    log(f"stop.lock process fail: {e}", "ERROR")

            # T15 — 偵測 enabled transition 觸發酒保廣播
            curr_enabled = bool(cfg.get("enabled", False))
            # ==== T-SSREC-01 錄播 transition（與 enabled 同機制：false→true 開錄 / true→false 停錄）====
            curr_recording = bool(cfg.get("recording_enabled", False))
            if curr_recording != last_recording:
                if curr_recording:
                    recording_start(cfg)
                else:
                    recording_stop(cfg, status="complete")
                last_recording = curr_recording

            if curr_enabled != last_enabled:
                # 開播/停播的酒館廣播已搬到 Editor 按鈕端 (UCL_ScreenStreamPage.PostStreamAnnounce)。
                # 為什麼不留在這裡: 2026-07-28 起 daemon 存活綁 cfg.enabled —— daemon 啟動時
                # enabled 已是 true, 這個 transition 再也不會發生 (實證: 酒館最後一筆廣播
                # 2026-07-27, 本 log 內 announce 出現 0 次); 停播時 daemon 直接被 kill,
                # 也沒機會發。**留一份「幾乎不會執行」的廣播只會變成第二個寫入者**,
                # 哪天生命週期又改回常駐 idle 就會雙發。本段只保留 _live_info 對齊。
                if curr_enabled:
                    write_live_info(cfg)      # 落檔暫存本場直播資訊 (存活期 = 直播期間)
                else:
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

            # ── OCR 已移交 C# 端管理 (2026-08-15 遷移階段 2, Tim 拍板) ──
            # 物理意義: OCR 現在是獨立行程 (`subtitle_ocr.py --serve`), 由 C# 的
            #   UCL_OcrWorkerSupervisor 起停 / 依設定重起 / 依**產物水位**判定停滯重起。
            # ⚠ 原本長在這裡的 T-OCR-Pipeline / T-OCR-AutoRestart / all-workers-dead 三段一併移除,
            #   **不留停用版** — 留著就是第二個決策點, 而兩顆 pool 併寫同一份 cache 不會報錯。

            # ── STT 已移交 C# 端管理 (2026-08-15, Tim 拍板「python 儘量減少耦合, 都透過 C# 統一管理」) ──
            # 物理意義: STT 現在是**獨立行程** (`audio_transcribe.py serve`), 由 C# 的
            #   UCL_SttWorkerSupervisor 起停/依設定重起/依**產物水位**判定停滯重起。
            #   本 daemon 不再碰 STT — 兩邊都起 worker 會併寫同一份 stt cache, 而那不會報錯。
            # ⚠ 原本長在這裡的三段 (T-STT-Cache 生命週期 / T-STT-AutoRestart / T-STT-Watchdog)
            #   一併移除, **不保留停用版**: 留著就是第二個決策點, 而「誰重起的」會永遠查不清楚。
            #   歷史與血證 (2026-07-27 靜默殭屍 2h) 見 git history 與 Plan_StreamWatch_Cmd.md。

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
        # (OCR/STT 均已移交 C# supervisor 管理 — 本 daemon 沒有 pool/worker 可收;
        #  錄播專用的 _rec_state["ocr_pool"] 仍在本 daemon 內, 由 _rec_stop 收。)
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
