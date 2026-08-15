#!/usr/bin/env python3
"""audio_transcribe.py — 系統音訊語音轉文字 (STT) helper (openai-whisper, GPU 優先)。

# 區塊職責: 抓 system audio loopback (或讀 WAV) → openai-whisper 轉錄 → 帶時間戳文字段;
#          給 screenstream_montage.py 的 --stt 模式接底, 在字幕 sidecar 補一段「語音轉錄」。
# 物理意義: agent 沒有耳朵 — OCR 讀「畫面已翻好的字幕(中文)」, STT 補「原始語音(多為英文)」,
#          兩者時間軸並置 → 逐句雙語對照 (字幕『好』↔ 語音『Yes』)。是給視覺通道補一個聽覺 modality。
# 數值影響: 模型單例常駐 (small ~460MB VRAM fp16); RTX4080 上 tiny/small transcribe 遠快於實時;
#          soundcard loopback 擷取 N 秒即取 N 秒 wall-clock (阻塞), 之後 GPU 轉錄 <1s (短片段)。

設計依據: Quest stt-whisper-integration (Tim 2026-07-05 20-token task「用 Quest workflow 實作, 細節妳先決定, 我 QA 微調」)
作者: kotoko (claude-da-xiaojie), 2026-07-05

設計決策 (kotoko 自決, 實作簡單優先):
  - Whisper 端用 openai-whisper (torch cu121 wheel 自帶 CUDA, 免手動裝 Toolkit/cuDNN) — 對齊 Tim「好裝優先」。
  - 音訊來源走「on-demand 即時擷取最近 N 秒」(soundcard loopback), 不改正在跑的 daemon —
    對齊「近即時觀看」場景 (montage cursor ≈ now)。精確歷史對齊 (daemon 落盤 raw PCM) 留 v2。
  - 逐句雙語配對可靠; 逐字對是 best-effort (字幕是翻譯非逐字轉錄, Whisper 時間戳段級)。

依賴:
  - openai-whisper (pip install openai-whisper) + torch (cu121 wheel)
  - soundcard (WASAPI loopback; 只在 capture_live 用, 讀 WAV 不需要)
  - numpy

CLI 自驗:
  python audio_transcribe.py check                 # 檢查依賴 + device
  python audio_transcribe.py live 8 --model tiny   # 擷取 8 秒系統音訊 + 轉錄
  python audio_transcribe.py file some.wav         # 轉錄一個 WAV
"""
from __future__ import annotations

import json
import os
import sys
import threading
import time
import wave
from pathlib import Path

import numpy as np

# ===========================================================
# 常數 — Whisper 輸入規格與預設
# ===========================================================
# 物理意義: Whisper 模型內部一律 16kHz mono float32 (-1~1); 其餘 sample_rate 交給擷取端 resample。
WHISPER_SAMPLE_RATE = 16000
# 預設模型: 環境變數 STT_WHISPER_MODEL 可覆蓋 (Tim QA 可切 tiny/base/small/medium/large-v3)。
# small 是中英文品質 vs 速度的甜蜜點; RTX4080 12GB 連 large-v3 都跑得動。
DEFAULT_MODEL = os.environ.get("STT_WHISPER_MODEL", "small")
# 擷取端每次即時抓的秒數上限 (防 montage 窗口過長時抓太久阻塞)。
DEFAULT_CAPTURE_CAP_SEC = 30.0

# ===========================================================
# 模型單例 — 避免每次呼叫重載 (載入 small ~數秒)
# ===========================================================
_model = None                # 已載入的 whisper model (單例)
_model_key = None            # (model_size, device) — 變更時才重載
_model_lock = threading.Lock()
_init_error: str | None = None  # 依賴/載入失敗訊息 (fail-soft, 給 caller 印警告)
_user_site_debug: str = ""      # _ensure_user_site 的診斷字串 (import 失敗時併入 _init_error)


def _ensure_user_site() -> None:
    # 區塊職責：把 pip install --user 的落點 (user-site) 補進 sys.path。
    # 物理意義：whisper/torch 裝在 %APPDATA%\Python\PythonXY\site-packages (user-site)；
    #          Unity Editor spawn 的 daemon 子行程環境下 user-site 有時不在 sys.path
    #          (同一支 python.exe、shell 端 import 正常、daemon 端 No module named 'whisper' —
    #          2026-07-09 sw-eadd06 場實錄的「同名不同環境」層次混淆案)。
    # 數值影響：插在第一個 system site-packages「之前」— 對齊正常 python 的解析優先序
    #          (user-site 先於 system site)。這很重要：system site-packages 存在一份
    #          殘缺 torch/torchgen 孤兒目錄 (無 dist-info、import 後無 __version__)，
    #          若 user-site 只 append 在尾巴，torch 會先解析到壞的 system 殘本而炸
    #          "No module named 'torchgen.model'"。路徑不存在則不動作，fail-soft。
    global _user_site_debug
    try:
        import site
        candidates = []
        # 正規解法：site 模組自己算 user-site (吃 APPDATA / PYTHONUSERBASE)
        try:
            usp = site.getusersitepackages()
            if usp:
                candidates.append(usp)
        except Exception:
            pass
        # Fallback：daemon 環境 APPDATA 異常時，從 USERPROFILE 手拼 Roaming user-site 路徑
        home = os.environ.get("USERPROFILE") or os.path.expanduser("~")
        ver_dir = f"Python{sys.version_info.major}{sys.version_info.minor}"
        candidates.append(os.path.join(home, "AppData", "Roaming", "Python", ver_dir, "site-packages"))
        # 找出第一個 system site-packages 的位置，user-site 插它前面 (沒找到就 append)
        insert_at = len(sys.path)
        for i, entry in enumerate(sys.path):
            if "site-packages" in (entry or ""):
                insert_at = i
                break
        added = []
        for p in candidates:
            if p and os.path.isdir(p) and p not in sys.path:
                sys.path.insert(insert_at, p)
                insert_at += 1
                added.append(p)
        # 診斷字串 (import 仍失敗時併入 _init_error, 禁靜默失敗)
        # whisper_visible=False 但 shell 端 True → 子行程環境看不到該路徑 (容器/虛擬化隔離,
        #   2026-07-09 查明 Unity Editor 從 Claude MSIX app-container 內啟動時撞到)
        added_note = f"added={added}" if added else f"NOT_added(candidates={candidates})"
        whisper_visible = (candidates and os.path.isdir(os.path.join(candidates[0], "whisper")))
        _user_site_debug = f"{added_note} whisper_visible={whisper_visible} exe={sys.executable!r}"
    except Exception as e:
        _user_site_debug = f"_ensure_user_site 本身炸了: {e}"
        # fail-soft: 補路徑失敗就維持原狀, 讓 caller 的 import 失敗訊息浮出


def is_available() -> bool:
    """依賴是否齊全 (whisper + torch import 得動)。fail-soft: 缺就回 False, caller 跳過 STT 段。

    import 失敗時先補 user-site 進 sys.path 重試一次 (解 Unity Editor 子行程吃不到
    pip install --user 套件的環境差異), 兩次都失敗才回 False。
    """
    global _init_error
    try:
        import whisper  # noqa: F401
        import torch  # noqa: F401
        return True
    except Exception:
        _ensure_user_site()
        try:
            import whisper  # noqa: F401
            import torch  # noqa: F401
            _init_error = None
            return True
        except Exception as e:
            _init_error = (f"openai-whisper / torch import 失敗 (含 user-site fallback): {e}; "
                           f"[debug {_user_site_debug}] "
                           f"→ pip install --user openai-whisper 且 torch cu121 wheel")
            return False


def init_error() -> str | None:
    """回最後一次 init/依賴錯誤字串 (給 montage 報告層印, 禁靜默失敗)。"""
    return _init_error


def _pick_device() -> str:
    """自動挑 device: 有 CUDA 走 cuda (GPU), 否則 cpu。"""
    try:
        import torch
        return "cuda" if torch.cuda.is_available() else "cpu"
    except Exception:
        return "cpu"


def get_model(model_size: str = DEFAULT_MODEL, device: str | None = None):
    """回單例 whisper model; (model_size, device) 沒變就 reuse, 變了才重載。

    Args:
        model_size: tiny/base/small/medium/large-v3 (預設 env STT_WHISPER_MODEL 或 small)
        device: "cuda"/"cpu"; None = 自動偵測
    Returns:
        whisper model, 或 None (載入失敗, _init_error 記錄原因)
    """
    global _model, _model_key, _init_error
    if not is_available():
        return None
    dev = device or _pick_device()
    key = (model_size, dev)
    with _model_lock:
        if _model is not None and _model_key == key:
            return _model
        try:
            import whisper
            _model = whisper.load_model(model_size, device=dev)
            _model_key = key
            _init_error = None
            return _model
        except Exception as e:
            _init_error = f"whisper.load_model({model_size}, {dev}) 失敗: {e}"
            _model = None
            _model_key = None
            return None


# ===========================================================
# 音訊來源 (1) 即時擷取 system audio loopback
# ===========================================================
def capture_live(seconds: float, sample_rate: int = WHISPER_SAMPLE_RATE) -> np.ndarray:
    """用 soundcard WASAPI loopback 抓最近 `seconds` 秒的系統輸出音訊 → mono float32 16k。

    物理意義: 對齊 daemon 的 audio_viz 同一條 loopback 來源; 阻塞抓滿 N 秒才回。
    Returns: shape=(N,) float32 in [-1,1]; 失敗回空陣列 (_init_error 記錄)。
    """
    global _init_error
    seconds = max(0.5, min(float(seconds), DEFAULT_CAPTURE_CAP_SEC))
    try:
        import soundcard as sc
    except Exception as e:
        _init_error = f"soundcard import 失敗 (即時擷取需要): {e} → pip install --user soundcard"
        return np.zeros(0, dtype=np.float32)
    try:
        # 區塊職責: 取「預設喇叭的 loopback 麥克風」= 系統正在播的聲音
        # 物理意義: include_loopback=True 讓 speaker 變成可錄的 loopback 裝置 (WASAPI)
        spk = sc.default_speaker()
        mic = sc.get_microphone(id=str(spk.name), include_loopback=True)
        n = int(sample_rate * seconds)
        with mic.recorder(samplerate=sample_rate, channels=1) as rec:
            data = rec.record(numframes=n)  # shape=(n, 1) float32 already -1~1
        audio = np.asarray(data, dtype=np.float32).reshape(-1)
        return audio
    except Exception as e:
        _init_error = f"loopback 擷取失敗: {e}"
        return np.zeros(0, dtype=np.float32)


# ===========================================================
# 音訊來源 (2) 讀 WAV 檔 → 16k mono float32
# ===========================================================
def load_wav(path) -> np.ndarray:
    """讀 WAV → mono float32 16k (簡易 resample: 線性內插)。失敗回空陣列。"""
    global _init_error
    try:
        with wave.open(str(path), "rb") as wf:
            sr = wf.getframerate()
            ch = wf.getnchannels()
            sw = wf.getsampwidth()
            raw = wf.readframes(wf.getnframes())
        # 依 sample width 解碼成 float32 -1~1
        if sw == 2:
            arr = np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0
        elif sw == 4:
            arr = np.frombuffer(raw, dtype=np.int32).astype(np.float32) / 2147483648.0
        elif sw == 1:
            arr = (np.frombuffer(raw, dtype=np.uint8).astype(np.float32) - 128.0) / 128.0
        else:
            _init_error = f"不支援的 WAV sampwidth={sw}"
            return np.zeros(0, dtype=np.float32)
        # 多聲道 → mono (取平均)
        if ch > 1:
            arr = arr.reshape(-1, ch).mean(axis=1)
        # resample 到 16k (線性內插, 夠 POC 用)
        if sr != WHISPER_SAMPLE_RATE and arr.size:
            tgt_n = int(round(arr.size * WHISPER_SAMPLE_RATE / sr))
            arr = np.interp(np.linspace(0, arr.size - 1, tgt_n),
                            np.arange(arr.size), arr).astype(np.float32)
        return arr.astype(np.float32)
    except Exception as e:
        _init_error = f"讀 WAV 失敗: {e}"
        return np.zeros(0, dtype=np.float32)


# ===========================================================
# 轉錄 — audio → 帶時間戳文字段
# ===========================================================
# ===========================================================
# 區塊職責：靜音幻覺防治 —— RMS 前置閘 + whisper 官方門檻 + segment 後過濾（三層）
# 物理意義：whisper 對著沒有語音的音軌會**自信地**吐字。2026-08-01 Tim 首次試錄的實證：
#          14 段全是孤立數字「3/2/1/2/1…」、每段恰好 2.0 秒 —— 那是幻覺的典型形狀。
#          （wake#52 2026-07-09 就診斷過「靜音幻覺無 RMS gate」，一直沒修，今天補上。）
# 數值影響：三層各治一種漏網：
#   ① RMS 前置閘 —— 整段音量低於門檻**根本不送 whisper**（省 GPU + 從源頭免幻覺）。
#      最可靠，因為它不依賴模型的自我評估。
#   ② whisper 官方 threshold 參數 —— no_speech_threshold / logprob_threshold /
#      compression_ratio_threshold 由 transcribe() 內建支援；另加 condition_on_previous_text=False
#      （官方已知問題：跨窗接續會把幻覺一路滾雪球，關掉可斷開重複迴圈）。
#   ③ segment 後過濾 no_speech_prob —— **必要，不是重複**：官方內建的抑制邏輯要求
#      no_speech_prob > 門檻 **且** avg_logprob < 門檻**兩者同時成立**才丟棄。
#      而靜音幻覺常常是「高 no_speech_prob 但模型很有信心」→ 內建那條不會 fire。
#      單獨用 no_speech_prob 過一次才擋得住。
# 邊界：門檻全部可從 config 調（Tim QA 用）。RMS 門檻預設保守（0.005，約 -46 dBFS），
#      寧可放行邊緣音量也不要把小聲對白吃掉 —— 漏擋只是多幾段雜訊，誤擋是真的丟資訊。
# ===========================================================
DEFAULT_RMS_GATE = 0.005          # 低於此 RMS 視為靜音，不送 whisper（0 = 停用本閘）
DEFAULT_NO_SPEECH_MAX = 0.6       # segment 的 no_speech_prob 超過即丟（對齊官方 no_speech_threshold 預設）
DEFAULT_LOGPROB_MIN = -1.0        # segment 的 avg_logprob 低於即丟（對齊官方 logprob_threshold 預設）


def audio_rms(audio: np.ndarray) -> float:
    """整段音訊的 RMS（0~1）。空/壞資料回 0.0。"""
    if audio is None or getattr(audio, "size", 0) == 0:
        return 0.0
    try:
        return float(np.sqrt(np.mean(np.square(audio.astype(np.float32)))))
    except Exception:
        return 0.0


def transcribe(audio: np.ndarray, language: str | None = None,
               model_size: str = DEFAULT_MODEL, device: str | None = None,
               initial_prompt: str | None = None,
               rms_gate: float = DEFAULT_RMS_GATE,
               no_speech_max: float = DEFAULT_NO_SPEECH_MAX,
               logprob_min: float = DEFAULT_LOGPROB_MIN) -> list[dict]:
    """把 float32 16k mono audio 丟 whisper 轉錄, 回帶時間戳的段列表。

    Args:
        audio: shape=(N,) float32 -1~1 @16kHz
        language: "en"/"zh"/None(自動偵測); 直播原文多為 en, 指定可加速+穩定
        initial_prompt: (選) 前文語境, 給 whisper 做詞彙偏置 —— 主要用途是餵「登場人物名」
            壓人名咬字 (シャーリー→サレイ 之類)。MUST 用轉錄語言的字形 (日文餵片假名, 不是中文譯名),
            否則偏置無效甚至更糟。空/None = 不偏置 (原行為)。
    Returns:
        [{"start": float 秒, "end": float 秒, "text": str}, ...]; 失敗/靜音回 []
    """
    if audio is None or audio.size < WHISPER_SAMPLE_RATE // 2:  # < 0.5s 直接跳過
        return []
    # ① RMS 前置閘 —— 靜音根本不送 whisper。回空是**誠實的空**，不是幻覺的「1」。
    if rms_gate > 0:
        rms = audio_rms(audio)
        if rms < rms_gate:
            return []
    model = get_model(model_size, device)
    if model is None:
        return []
    try:
        import torch
        use_fp16 = (_pick_device() == "cuda")
        # ② whisper 官方門檻參數（值皆為官方預設，顯式寫出讓它可被 config 覆蓋、也讓讀 code 的人看得到）
        #    condition_on_previous_text=False 是刻意偏離預設 (True)：
        #    官方已知問題 —— 跨窗接續會讓幻覺滾雪球（前一窗的垃圾變成下一窗的 prompt）。
        result = model.transcribe(audio.astype(np.float32), language=language,
                                  fp16=use_fp16, verbose=False,
                                  initial_prompt=(initial_prompt or None),
                                  no_speech_threshold=no_speech_max,
                                  logprob_threshold=logprob_min,
                                  compression_ratio_threshold=2.4,
                                  condition_on_previous_text=False)
        segs = []
        dropped = 0
        for s in result.get("segments", []):
            txt = (s.get("text") or "").strip()
            if not txt:
                continue
            # ③ 後過濾 —— **必須用 AND，不能用 OR**（2026-08-01 實測血證）。
            #    首版我寫成 OR，理由是「官方內建要兩者同時成立才丟，擋不住高信心的靜音幻覺」。
            #    論證很順，結論是錯的：Tim 直播現場實測，whisper 回了五段**真的對白**
            #    （「正好許久沒救存檔了」等），而它們的 no_speech_prob 全是 0.685 > 0.6
            #    → 我那層 OR 把五段全砍，字幕整個變空。
            #    根因：no_speech_prob 是**每 30 秒窗共用一個值**、不是每段各算，
            #    遊戲/動畫這種 BGM+人聲混音天生就偏高；而 avg_logprob=-0.291 遠高於 -1.0
            #    表示模型其實很有信心。**官方用 AND 正是為了這種情形。**
            #    → 靜音幻覺交給 ① RMS 前置閘處理（那才是對症的層）；這裡只沿用官方語意，不加碼。
            nsp = float(s.get("no_speech_prob", 0.0))
            alp = float(s.get("avg_logprob", 0.0))
            if nsp > no_speech_max and alp < logprob_min:
                dropped += 1
                continue
            segs.append({"start": float(s.get("start", 0.0)),
                         "end": float(s.get("end", 0.0)),
                         "text": txt,
                         # 保留判定依據 —— 之後調門檻時看得到當初為什麼留/丟，不用重跑
                         "no_speech_prob": round(nsp, 4),
                         "avg_logprob": round(alp, 4)})
        # 過濾筆數不靜默 —— 掛在模組級供 caller 查（本檔無 logger，避免為一行 debug 引入新依賴）
        globals()["_last_filtered_count"] = dropped
        return segs
    except Exception as e:
        global _init_error
        _init_error = f"transcribe 失敗: {e}"
        return []


# ===========================================================
# T-STT-Cache (Quest T06, kotoko 2026-07-05) — daemon-side 持續 STT cache
# 區塊職責: 照 subtitle_ocr 的 daemon OCR cache 模式 — daemon 背景持續錄音轉錄寫 cache,
#          多 viewer 的 montage 端只讀 cache (不各自重跑), 沒 cache 的音訊直接無視 (靠 OCR)。
# 物理意義: OCR cache 單位=每幀 (frame_NNNN.json); STT cache 單位=每個時間 chunk (stt_<epoch_ms>.json),
#          因語音是時間軸連續量、不對應單幀。段時間戳存「絕對 epoch」→ montage 依窗口 [after, until] 篩。
# 數值影響: chunk 15s, whisper GPU 轉 15s <1s → worker 輕鬆跟上實時; cache json 每檔 <5KB;
#          rolling 清理保留 ~ring buffer span (預設 700s) 避免無限長大。
# ===========================================================
# 預設 STT cache 目錄 (跟 frames/ 與 ocr/ 平行, 由 daemon 寫 / montage 讀)。
# 本檔已遷入 <UCL_Core>/Tools~/AgentCommands (2026-07-26) — 不能再用「上一層 = AgentCommands 根」假設,
# 改 repo-walk (.git 只認資料夾跳過 gitlink) + honors .agentcommands_root.local, cache 落主專案。
def _stt_data_root() -> Path:
    _here = Path(__file__).resolve().parent
    env = os.environ.get("CLAUDE_PROJECT_DIR")
    root = None
    if env and Path(env).is_dir():
        root = Path(env).resolve()
    else:
        p = _here
        while p != p.parent:
            if (p / ".git").is_dir():
                root = p
                break
            p = p.parent
        if root is None:
            root = _here.parent.parent
    pointer = root / ".agentcommands_root.local"
    try:
        if pointer.exists():
            content = pointer.read_text(encoding="utf-8").strip()
            if content and Path(content).is_absolute():
                return Path(content).resolve()
    except Exception:
        pass
    return (root / "AgentCommands").resolve()


STT_CACHE_DIR = _stt_data_root() / "_screenstream" / "stt"
STT_STATUS_FILENAME = "_status.json"     # watermark: 最新已 cache 到的 end_epoch
DEFAULT_CHUNK_SEC = 15.0                  # 每個 cache chunk 的音訊長度
DEFAULT_CACHE_RETENTION_SEC = 700.0      # 舊 cache 保留秒數 (≈ frame ring buffer span, 之後刪)


def _stt_status_path(cache_dir: Path) -> Path:
    return Path(cache_dir) / STT_STATUS_FILENAME


def read_stt_status(cache_dir: Path = STT_CACHE_DIR) -> dict | None:
    """讀 daemon STT worker 進度水位 (latest_end_epoch / model / updated_at). 不存在/壞檔回 None。"""
    sp = _stt_status_path(cache_dir)
    if not sp.exists():
        return None
    try:
        with sp.open("r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, json.JSONDecodeError, ValueError):
        return None


def read_stt_cache(after_epoch: float, until_epoch: float,
                   cache_dir: Path = STT_CACHE_DIR) -> tuple[list[dict], dict]:
    """cache-only 讀: 回窗口 [after_epoch, until_epoch] 內的 STT 段 + 覆蓋資訊。

    不現跑轉錄 — 純讀 daemon 預產 cache (對齊 OCR cache-first 鐵律)。
    Returns:
        (segments, info):
          segments = [{"start_epoch","end_epoch","text"}, ...] 依 start_epoch 排序 (窗口內)
          info = {"cache_present": bool, "chunks_hit": int, "covered": bool,
                  "latest_end_epoch": float|None}
    """
    cache_dir = Path(cache_dir)
    info = {"cache_present": False, "chunks_hit": 0, "covered": False, "latest_end_epoch": None}
    if not cache_dir.exists():
        return [], info
    status = read_stt_status(cache_dir)
    if status:
        info["latest_end_epoch"] = status.get("latest_end_epoch")
    segs: list[dict] = []
    hit = 0
    for jf in sorted(cache_dir.glob("stt_*.json")):
        try:
            with jf.open("r", encoding="utf-8") as f:
                chunk = json.load(f)
        except (OSError, json.JSONDecodeError, ValueError):
            continue
        c0 = float(chunk.get("start_epoch", 0.0))
        c1 = float(chunk.get("end_epoch", 0.0))
        # chunk 與窗口有重疊才算命中 (cache_present = 至少有一個 chunk 落在此範圍)
        if c1 < after_epoch or c0 > until_epoch:
            continue
        info["cache_present"] = True
        hit += 1
        for s in chunk.get("segments", []):
            se, ee = float(s.get("start_epoch", 0.0)), float(s.get("end_epoch", 0.0))
            # 區塊職責: 跨界段的收錄語意 —— **重疊即收**，不是「中心點落在窗內才收」。
            # 物理意義: 語音不會照觀看窗切齊。一段 20~32s 的話，在 15~25s 與 25~35s 兩個觀看窗
            #          **都應該看得到** —— 前者看到它的開頭、後者看到它的結尾。
            #          舊版用中心點判定 (mid=26 → 只有後窗收得到)，理由是「避免跨界段重複」；
            #          但那個「重複」正是使用者要的**連續性**，不是噪音 (Tim 2026-08-01 指正)。
            #          漏掉才是真的傷害：前窗的觀看者完全不知道那 5 秒有人在講話。
            # 數值影響: 跨界段會在相鄰兩窗各出現一次，並標 partial 讓顯示端知道它是被切開的
            #          ("head"=尾巴延伸到窗外 / "tail"=開頭在窗之前 / "both"=整個窗都在這句話中間)。
            if se <= until_epoch and ee >= after_epoch:
                txt = (s.get("text") or "").strip()
                if txt:
                    before = se < after_epoch
                    after = ee > until_epoch
                    partial = ("both" if (before and after) else
                               "tail" if before else
                               "head" if after else None)
                    item = {"start_epoch": se, "end_epoch": ee, "text": txt}
                    if partial:
                        item["partial"] = partial
                    segs.append(item)
    segs.sort(key=lambda x: x["start_epoch"])
    info["chunks_hit"] = hit
    # covered: cache 水位有蓋過窗口尾端 (daemon 沒落後)
    le = info["latest_end_epoch"]
    info["covered"] = bool(le is not None and le >= until_epoch - DEFAULT_CHUNK_SEC)
    return segs, info


def write_stt_chunk(cache_dir, start_epoch: float, end_epoch: float,
                    segments: list[dict], model_size: str = DEFAULT_MODEL,
                    audio_sec: float | None = None) -> str:
    """把一個 chunk 的段 (相對時間戳→絕對 epoch) atomic 寫成 cache json + 更新 status watermark。

    區塊職責: module-level 寫入函式, 給 SttCacheWorker (常駐) 與 montage --stt-live (同步現抓) 共用,
             避免兩處各寫一份 cache 格式漂移 (格式=stt_<start_ms>.json + _status.json, 對齊 read_stt_cache)。
    物理意義: segments 的 start/end 是「段內相對秒」→ 加 start_epoch 還原絕對 epoch, montage 依此對齊窗口。
    數值影響: watermark latest_end_epoch 取 max(既有, 本 chunk end) — montage-live 亂序寫時不倒退水位。
    回傳: 寫出的 cache 檔絕對路徑 (str)。

    audio_sec (Tim 2026-08-11 拍板, Sirius 血證): **實際送進 whisper 的音訊秒數**
      (`audio.size / WHISPER_SAMPLE_RATE`), 與 start/end_epoch 那對 bracket 是**兩件事**。
      物理意義: bracket 是「loop 這一圈的牆上時間」(t0=擷取前 / t1=轉錄後), 由建構方式保證
                前後相鄰 → **它結構上不可能顯示漏錄, 即使真的漏了**。
                2026-08-11 實測 44 chunk 得「覆蓋率 99.9% / 0 gap」, 而那個數字是自我實現的:
                拿 bracket 算覆蓋率等於拿迴圈時間戳證明迴圈沒有中斷。
      數值影響: 只多一個欄位, 不改 bracket 語意 (montage read_stt_cache 仍依 bracket 篩窗口);
                有了它才能算真覆蓋率 = Σaudio_sec / span, 並看出 bracket 比音訊長多少
                (實測平均約 +0.8s → **sidecar 上的 STT 時間戳系統性偏晚, 該當 ±1s 讀**)。
      None = 呼叫端沒量 (舊行為), 欄位不寫入 —— 缺值與 0 要分得出來, 不給預設。
    """
    cache_dir = Path(cache_dir)
    cache_dir.mkdir(parents=True, exist_ok=True)
    abs_segs = [{"start_epoch": start_epoch + s["start"],
                 "end_epoch": start_epoch + s["end"],
                 "text": s["text"]} for s in segments]
    fname = cache_dir / f"stt_{int(start_epoch * 1000)}.json"
    payload = {"start_epoch": start_epoch, "end_epoch": end_epoch,
               "model": model_size, "segments": abs_segs}
    # 缺值不寫欄位 —— 「沒量」與「量到 0 秒」是兩件事, 混成 0 會讓覆蓋率統計靜默偏低
    if audio_sec is not None:
        payload["audio_sec"] = round(float(audio_sec), 3)
    tmp = fname.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    tmp.replace(fname)
    # 更新 watermark — 取 max 避免 montage-live 亂序寫 (窗口尾早於既有 daemon 水位) 把水位拉退
    sp = _stt_status_path(cache_dir)
    prev_le = 0.0
    try:
        prev = json.loads(sp.read_text(encoding="utf-8"))
        prev_le = float(prev.get("latest_end_epoch", 0.0) or 0.0)
    except Exception:
        pass
    new_le = max(prev_le, end_epoch)
    tmp2 = sp.with_suffix(".json.tmp")
    tmp2.write_text(json.dumps({"latest_end_epoch": new_le, "model": model_size,
                                "updated_at": end_epoch}, ensure_ascii=False), encoding="utf-8")
    tmp2.replace(sp)
    return str(fname)


def write_stt_status_error(cache_dir, error: str | None) -> None:
    """把 worker 錯誤寫進 _status.json 的 error/error_at 欄 (error=None 清除)。

    區塊職責: 補「錯誤被記在 thread 私有欄位、外界無人能讀」的可觀測性缺口 —
             2026-07-27 STT 靜默殭屍事故: 擷取失敗重試迴圈空轉 2h, _error 有值但 UI/log 全無感。
    物理意義: merge 寫 — 保留既有 watermark 欄位只動 error 欄; atomic replace 防半寫。
    數值影響: 只在錯誤狀態「轉換」時呼叫 (首次失敗 / 恢復), 不在重試迴圈內每秒寫盤。
    """
    try:
        sp = _stt_status_path(Path(cache_dir))
        sp.parent.mkdir(parents=True, exist_ok=True)
        try:
            status = json.loads(sp.read_text(encoding="utf-8"))
            if not isinstance(status, dict):
                status = {}
        except (OSError, json.JSONDecodeError, ValueError):
            status = {}
        if error:
            status["error"] = error
            status["error_at"] = time.time()
        else:
            status.pop("error", None)
            status.pop("error_at", None)
        tmp = sp.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(status, ensure_ascii=False), encoding="utf-8")
        tmp.replace(sp)
    except OSError:
        pass  # 可觀測性輔助欄 — 寫盤失敗不影響轉錄主流程 (主要錯誤已經由 warn_cb 出聲)


class SttCacheWorker:
    """daemon-side 常駐 STT cache worker (對偶 subtitle_ocr.OcrWorkerPool)。

    物理意義: 自開一路 soundcard loopback, 連續錄 chunk_sec 音訊 → whisper 轉錄 →
             寫 stt_<start_epoch_ms>.json (段帶絕對 epoch 時間戳) + 更新 _status.json watermark。
    Thread safety: 單一 worker thread; montage 端只讀檔, 不碰此 thread。
    """

    def __init__(self, cache_dir: Path = STT_CACHE_DIR, model_size: str = DEFAULT_MODEL,
                 language: str | None = None, chunk_sec: float = DEFAULT_CHUNK_SEC,
                 retention_sec: float = DEFAULT_CACHE_RETENTION_SEC,
                 progress_cb=None, prompt: str | None = None, warn_cb=None,
                 rms_gate: float = DEFAULT_RMS_GATE,
                 no_speech_max: float = DEFAULT_NO_SPEECH_MAX,
                 logprob_min: float = DEFAULT_LOGPROB_MIN):
        self.cache_dir = Path(cache_dir)
        self.model_size = model_size
        self.language = language
        # prompt: whisper initial_prompt (登場人物名詞彙偏置); 綁 worker 生命週期,
        #   改動需 toggle off→on 重起才生效 (同 model/lang)。空=不偏置。
        self.prompt = (prompt or "").strip() or None
        self.chunk_sec = float(chunk_sec)
        # 靜音幻覺三層防治的門檻（可從 daemon config 調，Tim QA 用）—— 見 transcribe() 上方區塊註解
        self.rms_gate = float(rms_gate)
        self.no_speech_max = float(no_speech_max)
        self.logprob_min = float(logprob_min)
        self.retention_sec = float(retention_sec)
        # progress_cb(chunk_num:int, n_segs:int, end_epoch:float) — 每寫完一個 chunk 回呼一次。
        # 物理意義: 讓 caller (CLI / daemon) 能定期輸出「還活著」的進度, 修「背景看似卡住」的 UX。
        self.progress_cb = progress_cb
        # warn_cb(msg:str) — 失敗禁靜默出口 (2026-07-27 靜默殭屍事故): 擷取失敗時讓 caller 記 log。
        self.warn_cb = warn_cb
        self.chunk_count = 0        # 已寫 chunk 累計 (給進度顯示 + daemon watchdog 停滯偵測)
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._error: str | None = None
        self._fail_streak = 0       # 連續擷取失敗次數 (成功即歸零; 控制 warn 頻率防洗版)

    def start(self) -> None:
        if self._thread and self._thread.is_alive():
            return
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        self._stop.clear()
        self._thread = threading.Thread(target=self._loop, name="SttCacheWorker", daemon=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()
        if self._thread:
            self._thread.join(timeout=self.chunk_sec + 3.0)

    def error(self) -> str | None:
        return self._error

    def _warn(self, msg: str) -> None:
        """出聲管道 — warn_cb 沒給就靜默 (CLI 場景); 回呼失敗不影響主流程。"""
        if self.warn_cb is None:
            return
        try:
            self.warn_cb(msg)
        except Exception:
            pass

    def _write_chunk(self, start_epoch: float, end_epoch: float, segments: list[dict],
                     audio_sec: float | None = None) -> None:
        """把一個 chunk atomic 寫成 cache json + 更新 status (委派 module-level write_stt_chunk 共用)。

        audio_sec: 實際送進 whisper 的音訊秒數 —— 見 write_stt_chunk 的說明。
                   montage --stt-live 那條路早就在量它 (gura 磚1「覆蓋% 要用實測不用請求秒數」),
                   常駐 worker 這條路先前沒量 → 本參數把兩條路拉到同一個誠實標準。
        """
        write_stt_chunk(self.cache_dir, start_epoch, end_epoch, segments, self.model_size,
                        audio_sec=audio_sec)

    def _cleanup(self, now_epoch: float) -> None:
        """刪超過 retention 的舊 cache (rolling, 對齊 frame ring buffer 不無限長大)。"""
        cutoff = now_epoch - self.retention_sec
        for jf in self.cache_dir.glob("stt_*.json"):
            try:
                # 檔名 stt_<epoch_ms>.json → 解 epoch
                ep = int(jf.stem.split("_")[1]) / 1000.0
                if ep < cutoff:
                    jf.unlink()
            except (ValueError, IndexError, OSError):
                continue

    def _loop(self) -> None:
        # 區塊職責: Windows COM per-thread 初始化 — WASAPI (soundcard) 的先決條件, 且必須「條件式」做。
        # 物理意義: soundcard 只在「首次 import 它的那條 thread」CoInitialize (import 副作用):
        #          ① 它已被別的 thread import 過 → 本 thread 沒 COM → loopback 全滅
        #            Error 0x800401f0 (CO_E_NOTINITIALIZED) → 需自行補 CoInitializeEx (血證一號)。
        #          ② 它尚未 import → 必須「讓它自己 init」— 它的 check_error 把 S_FALSE (本 thread
        #            已初始化) 當錯誤拋, 先搶 init 會害它 import 炸 Error 0x100000001 (血證二號,
        #            2026-07-27 同日雙殺 — 兩隻都是錯誤可視化通道上線後現形的)。
        # 數值影響: COINIT_MULTITHREADED (0x0, 對齊 soundcard 自身用法); ctypes 端不檢查回傳
        #          (S_OK/S_FALSE 皆可用)。
        if sys.platform == "win32" and "soundcard" in sys.modules:
            try:
                import ctypes
                ctypes.windll.ole32.CoInitializeEx(None, 0x0)
            except Exception as e:
                self._warn(f"CoInitializeEx fail (STT 擷取可能失效): {e}")
        # 預載模型 (第一次 ~數秒), 失敗就記錄退出 (fail-soft, daemon 主流程不受影響)
        if get_model(self.model_size) is None:
            self._error = init_error() or "STT worker: 模型載入失敗"
            self._warn(f"STT worker 啟動失敗: {self._error}")
            write_stt_status_error(self.cache_dir, self._error)
            return
        cleanup_counter = 0
        while not self._stop.is_set():
            t0 = time.time()
            audio = capture_live(self.chunk_sec)  # 阻塞抓滿 chunk_sec
            if self._stop.is_set():
                break
            if audio.size < WHISPER_SAMPLE_RATE // 2:
                self._error = init_error() or "STT worker: 擷取無音訊"
                # 區塊職責: 擷取失敗禁靜默 — 首次寫 _status.json error 欄 + 分級出聲。
                # 物理意義: 2026-07-27 事故 — process 級音訊堆疊壞死後此分支每秒空轉 2h,
                #          thread 活著故 daemon dead 偵測不觸發, log 零筆, 外界只見「最新 STT 凍結」。
                #          錯誤必須離開 thread 私有欄位 (log + status 檔) 才算存在。
                # 數值影響: 第 1/5 次 + 之後每 60 次 (≈每分鐘) warn 一次防洗版; status 檔只寫轉換沿。
                self._fail_streak += 1
                if self._fail_streak == 1:
                    write_stt_status_error(self.cache_dir, self._error)
                if self._fail_streak in (1, 5) or self._fail_streak % 60 == 0:
                    self._warn(f"STT 擷取失敗 (連續 {self._fail_streak} 次): {self._error}")
                # 短暫等待避免 busy loop (擷取失敗時)
                self._stop.wait(1.0)
                continue
            if self._fail_streak:
                self._warn(f"STT 擷取恢復 (先前連續失敗 {self._fail_streak} 次)")
                self._fail_streak = 0
                self._error = None
                write_stt_status_error(self.cache_dir, None)
            segs = transcribe(audio, language=self.language, model_size=self.model_size,
                              rms_gate=self.rms_gate, no_speech_max=self.no_speech_max,
                              logprob_min=self.logprob_min,
                              initial_prompt=self.prompt)
            t1 = time.time()
            try:
                # audio.size / SR = 這一圈真的送進 whisper 的秒數 (不是 chunk_sec 設定值, 也不是 t1-t0)
                self._write_chunk(t0, t1, segs, audio_sec=audio.size / WHISPER_SAMPLE_RATE)
                self.chunk_count += 1
                # 進度回呼 — 讓 caller 定期輸出心跳 (修「背景看似卡住」); 回呼失敗不影響主流程
                if self.progress_cb is not None:
                    try:
                        self.progress_cb(self.chunk_count, len(segs), t1)
                    except Exception:
                        pass
            except Exception as e:
                self._error = f"STT worker 寫 cache 失敗: {e}"
            cleanup_counter += 1
            if cleanup_counter >= 4:  # 每 ~4 chunk 清一次舊檔
                cleanup_counter = 0
                try:
                    self._cleanup(t1)
                except Exception:
                    pass


# ===========================================================
# 格式化 — 產生 sidecar「語音轉錄」段 (格式參考 OCR sidecar)
# ===========================================================
def _fmt_ts(sec: float) -> str:
    """秒 → M:SS (相對段內偏移)。"""
    m = int(sec // 60)
    s = int(sec % 60)
    return f"{m}:{s:02d}"


def build_stt_section(segments: list[dict], window_label: str = "",
                      model_size: str = DEFAULT_MODEL, device: str | None = None,
                      note: str = "") -> str:
    """組出 sidecar 用的「## 🎙 語音轉錄 (STT)」段字串 (格式對齊 OCR Per-frame)。

    OCR 段是「- **#tile** frameid HH:MM:SS: 字幕」; STT 段對齊成「- [M:SS–M:SS] 語音文字」,
    每筆帶段內時間戳, 讓 agent 能跟 OCR 字幕時間軸對照 (逐句雙語)。
    """
    dev = device or _pick_device()
    lines = [
        "## 🎙 語音轉錄 (STT)",
        "",
        f"_STT engine_: openai-whisper ({model_size}, device={dev}, fp16={dev=='cuda'})",
        f"_來源_: 系統音訊 loopback 即時擷取{(' — ' + window_label) if window_label else ''}",
        *([f"_⚠ {note}_"] if note else []),
        "",
    ]
    if not segments:
        lines.append("- _(此窗口無可辨識語音 / 靜音)_")
    else:
        for seg in segments:
            lines.append(f"- [{_fmt_ts(seg['start'])}–{_fmt_ts(seg['end'])}] {seg['text']}")
    return "\n".join(lines) + "\n"


def _fmt_epoch_hms(ep: float) -> str:
    """絕對 epoch → HH:MM:SS (local), 對齊 OCR sidecar 的 frame 時間戳格式。"""
    return time.strftime("%H:%M:%S", time.localtime(ep))


def build_stt_section_cached(segments: list[dict], info: dict, model_size: str = DEFAULT_MODEL,
                            device: str | None = None) -> str:
    """組出 cache-only 讀取的「🎙 語音轉錄 (STT·cache)」段 (絕對 epoch 時間戳 HH:MM:SS 對齊 OCR)。

    段時間戳用絕對 HH:MM:SS → agent 可直接跟 OCR Per-frame 的 HH:MM:SS 對齊, 做逐句雙語。
    沒 cache (daemon STT off / 落後) → 誠實標「無 cache — 靠 OCR」, 不現跑轉錄 (Tim 指定)。
    """
    dev = device or _pick_device()
    lines = [
        "## 🎙 語音轉錄 (STT·cache)",
        "",
        f"_STT engine_: openai-whisper ({model_size}, device={dev}) — daemon 預產 cache, montage 只讀不現跑",
    ]
    if not info.get("cache_present"):
        lines += [
            "",
            "- _(此窗口無 STT cache — daemon STT worker 未開/未覆蓋此段; 依 Tim 指定「沒緩存直接無視, 靠 OCR」)_",
        ]
        return "\n".join(lines) + "\n"
    cov = "" if info.get("covered") else " ⚠ cache 落後窗口尾端 (daemon STT 追趕中, 尾段可能缺)"
    lines += [f"_cache_: 命中 {info.get('chunks_hit',0)} 個 chunk{cov}", ""]
    if not segments:
        lines.append("- _(此窗口 cache 內無可辨識語音 / 靜音)_")
    else:
        for seg in segments:
            # 跨界段標記 —— 讓讀的人知道這句話被觀看窗切開了，別把半句當完整句解讀。
            # ◂ = 開頭在本窗之前（你看到的是尾巴）；▸ = 結尾在本窗之後（你看到的是開頭）。
            p = seg.get("partial")
            pre = "◂ " if p in ("tail", "both") else ""
            suf = " ▸" if p in ("head", "both") else ""
            lines.append(f"- [{_fmt_epoch_hms(seg['start_epoch'])}] {pre}{seg['text']}{suf}")
    return "\n".join(lines) + "\n"


# ===========================================================
# CLI 自驗
# ===========================================================
def _main(argv: list[str]) -> int:
    import argparse
    # Windows console 常是 cp950/cp1252, print emoji/中文會炸 → 強制 utf-8 (errors=replace 保底)
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    ap = argparse.ArgumentParser(description="audio_transcribe.py — STT helper 自驗")
    sub = ap.add_subparsers(dest="cmd")
    sub.add_parser("check")
    p_live = sub.add_parser("live")
    p_live.add_argument("seconds", type=float, nargs="?", default=8.0)
    p_live.add_argument("--model", default=DEFAULT_MODEL)
    p_live.add_argument("--lang", default=None)
    p_file = sub.add_parser("file")
    p_file.add_argument("path")
    p_file.add_argument("--model", default=DEFAULT_MODEL)
    p_file.add_argument("--lang", default=None)
    # cache worker 自驗: 跑 N 秒持續 cache, 之後停 (模擬 daemon 端)
    p_cw = sub.add_parser("cache-worker")
    p_cw.add_argument("seconds", type=float, nargs="?", default=45.0)
    p_cw.add_argument("--model", default=DEFAULT_MODEL)
    p_cw.add_argument("--lang", default=None)
    p_cw.add_argument("--chunk", type=float, default=DEFAULT_CHUNK_SEC)
    # cache 讀取自驗: 讀 [after, until] epoch 窗口的 cache 段
    p_rc = sub.add_parser("read-cache")
    p_rc.add_argument("after", type=float, nargs="?", default=0.0)
    p_rc.add_argument("until", type=float, nargs="?", default=0.0)
    # serve: 常駐模式 — 由 C# (UCL_SttWorkerSupervisor) 起停與監督, 本行程只負責「一直轉錄」。
    # 物理意義: 把原本寄生在 screenstream_daemon 內的 STT thread 拉成獨立行程 ——
    #   daemon 那顆對外只有一個 PID, C# 看得到的只有「活著」, 而 2026-07-27 的故障是
    #   「活著而且兩小時什麼都沒產出」。獨立行程 + C# 讀產物水位 = 那種故障不再靜默。
    # ⚠ 本模式**不做自我重起**: 停滯判定與重起是 C# 的職責 (單一 supervisor, 不要兩層都在猜)。
    #   本行程只做兩件事: 轉錄, 以及把心跳印在 stdout 讓人看得到。
    p_sv = sub.add_parser("serve")
    p_sv.add_argument("--model", default=DEFAULT_MODEL)
    p_sv.add_argument("--lang", default=None)
    p_sv.add_argument("--prompt", default=None)
    p_sv.add_argument("--chunk", type=float, default=DEFAULT_CHUNK_SEC)
    p_sv.add_argument("--retention", type=float, default=DEFAULT_CACHE_RETENTION_SEC)
    p_sv.add_argument("--rms-gate", type=float, default=DEFAULT_RMS_GATE)
    p_sv.add_argument("--no-speech-max", type=float, default=DEFAULT_NO_SPEECH_MAX)
    p_sv.add_argument("--logprob-min", type=float, default=DEFAULT_LOGPROB_MIN)
    args = ap.parse_args(argv)

    if args.cmd == "check":
        ok = is_available()
        print(f"openai-whisper/torch available: {ok}")
        print(f"device: {_pick_device()}")
        print(f"default model: {DEFAULT_MODEL}")
        try:
            import soundcard as sc
            print(f"soundcard: OK (speaker={sc.default_speaker().name})")
        except Exception as e:
            print(f"soundcard: 缺 ({e}) — 即時擷取不可用, 讀 WAV 仍可")
        if not ok:
            print(f"init_error: {init_error()}")
        return 0 if ok else 1

    if args.cmd == "serve":
        # 起手先把「我拿到什麼設定」印出來 —— 由 C# 傳進來的參數若被誤解, 這一行是唯一的對帳點。
        print(f"[stt-serve] model={args.model} lang={args.lang or '(auto)'} chunk={args.chunk}s "
              f"retention={args.retention}s prompt={'(有)' if (args.prompt or '').strip() else '(無)'}",
              flush=True)
        if not is_available():
            # 壞要往吵的方向壞: 環境沒裝好就**立刻非零退出**, 讓 C# 當場看到失敗,
            # 而不是起一個永遠不產出的行程 (那正是「活著而且什麼都沒做」)。
            print(f"[stt-serve] ✗ whisper/torch 不可用: {init_error()}", flush=True)
            return 2
        worker = SttCacheWorker(
            model_size=args.model, language=args.lang, chunk_sec=args.chunk,
            retention_sec=args.retention, prompt=args.prompt,
            rms_gate=args.rms_gate, no_speech_max=args.no_speech_max,
            logprob_min=args.logprob_min,
            progress_cb=lambda n, segs, end_ep: print(
                f"[stt-serve] chunk#{n} segs={segs} end={time.strftime('%H:%M:%S', time.localtime(end_ep))}",
                flush=True),
            warn_cb=lambda m: print(f"[stt-serve] ⚠ {m}", flush=True),
        )
        worker.start()
        print(f"[stt-serve] started (device={_pick_device()}) — 由 C# 端負責停止與重起", flush=True)
        try:
            while True:
                time.sleep(1.0)
                if worker.error():
                    print(f"[stt-serve] ✗ worker error: {worker.error()}", flush=True)
                    return 3
        except KeyboardInterrupt:
            pass
        finally:
            worker.stop()
        print(f"[stt-serve] stopped (chunks={worker.chunk_count})", flush=True)
        return 0

    if args.cmd == "live":
        print(f"擷取 {args.seconds}s 系統音訊 ...")
        audio = capture_live(args.seconds)
        print(f"  取得 {audio.size} samples ({audio.size/WHISPER_SAMPLE_RATE:.1f}s), "
              f"RMS={float(np.sqrt(np.mean(audio**2))) if audio.size else 0:.4f}")
        segs = transcribe(audio, language=args.lang, model_size=args.model)
        print(build_stt_section(segs, window_label=f"live {args.seconds}s", model_size=args.model))
        if init_error():
            print(f"init_error: {init_error()}")
        return 0

    if args.cmd == "file":
        audio = load_wav(args.path)
        print(f"讀 {args.path}: {audio.size} samples ({audio.size/WHISPER_SAMPLE_RATE:.1f}s)")
        segs = transcribe(audio, language=args.lang, model_size=args.model)
        print(build_stt_section(segs, window_label=Path(args.path).name, model_size=args.model))
        return 0

    if args.cmd == "cache-worker":
        print(f"啟動 SttCacheWorker (model={args.model}, chunk={args.chunk}s) 跑 {args.seconds}s ...",
              flush=True)
        # 進度回呼: 每寫完一個 chunk 印一行心跳 (修「背景看似卡住」的 UX) — 帶 flush 確保背景即時可見
        def _progress(n, n_segs, end_ep):
            print(f"  [{time.strftime('%H:%M:%S', time.localtime(end_ep))}] "
                  f"chunk #{n}: {n_segs} 段轉錄, cache 已寫 (共 {n} chunk)", flush=True)
        w = SttCacheWorker(model_size=args.model, language=args.lang, chunk_sec=args.chunk,
                           progress_cb=_progress)
        w.start()
        t_end = time.time() + args.seconds
        while time.time() < t_end:
            time.sleep(2.0)
            if w.error():
                print(f"  worker error: {w.error()}", flush=True)
        w.stop()
        st = read_stt_status()
        n = len(list(STT_CACHE_DIR.glob("stt_*.json"))) if STT_CACHE_DIR.exists() else 0
        print(f"停止。cache 目錄: {STT_CACHE_DIR}")
        print(f"  cache chunk 檔數: {n}, status: {st}")
        return 0

    if args.cmd == "read-cache":
        after = args.after if args.after else (time.time() - 60)
        until = args.until if args.until else time.time()
        segs, info = read_stt_cache(after, until)
        print(f"讀 cache 窗口 [{after:.0f}, {until:.0f}] → info={info}")
        print(build_stt_section_cached(segs, info))
        return 0

    ap.print_help()
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
