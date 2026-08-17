#!/usr/bin/env python3
"""media_admin.py — 影音處理 (STT 語音轉文字 / OCR 字幕讀取) 的管理後端。

# 區塊職責: UCL_MediaAdminPage (Unity Editor 管理頁) 的 python 端唯一真相源 —
#          依賴檢查 / 安裝 (whisper / torch CUDA / rapidocr) / daemon config 讀寫 / STT 試錄。
# 物理意義: 真正做事的都是既有工具鏈 (audio_transcribe.py / screenstream daemon / montage --ocr)。
#          本 script 只是把「環境健檢 + pip 安裝 + _screenstream/_config.json 欄位調整」收攏成
#          一個 CLI, 讓 Editor 頁與 agent 走同一條路 (對齊 knowledge_base.py 的分層哲學)。
# 設計取捨: script 住 <UCL_Core>/Tools~/AgentCommands (跨專案共用); 但 STT/OCR 的 runtime 狀態
#          (daemon config / stt cache) 是 per-project 的, 一律經 data_root() 落在主專案
#          AgentCommands/_screenstream/, 不寫進 submodule (對齊 knowledge_base.py 2026-07-23 血教訓)。

位置: <UCL_Core>/Tools~/AgentCommands/media_admin.py
作者: kaguya (Luna), 2026-07-25 — Tim 指派「參考 UCL_KnowledgeBaseAdminPage 做影音管理頁」

CLI:
  python media_admin.py status                  # 依賴 + device + config 總覽 (人類可讀)
  python media_admin.py get-config              # 白名單 config 欄位 (純 JSON, 給 C# 頁面填表)
  python media_admin.py set-config k=v [k=v..]  # 寫回 daemon config (白名單 + 型別轉換)
  python media_admin.py list-plugins            # 插件清單 + 安裝狀態 + 可用動作 (純 JSON, 給頁面建下拉)
  python media_admin.py plugin --id stt --action install     # 安裝
  python media_admin.py plugin --id ocr --action uninstall   # 解除安裝
  python media_admin.py test-stt [--sec N]      # 委派專案端 audio_transcribe.py live N 試錄

插件與動作的唯一定義處是本檔的 PLUGINS 註冊表 (Tim 2026-08-11 拍板: 插件會越來越多,
頁面改成「下拉選插件 → 顯示該插件的動作」, 新增插件只改這張表, C# 端不必動)。
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

# ===========================================================
# 區塊：stdout 編碼保險
# 物理意義：Unity C# 端 redirect stdout 時 Windows 預設 cp950, print emoji/中文會 crash;
#          reconfigure 成 utf-8 與 C# 端 PYTHONIOENCODING=utf-8 雙保險 (同 knowledge_base.py)。
# ===========================================================
try:
    sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[union-attr]
    sys.stderr.reconfigure(encoding="utf-8")  # type: ignore[union-attr]
except Exception:
    pass

# ===========================================================
# 區塊：路徑解析 — 對齊 knowledge_base.py 慣例
# 物理意義: 本 script 在 <UCL_Core>/Tools~/AgentCommands/ (submodule 內)。往上 walk 時
#          .is_dir() 會跳過 UCL_Core 自己的 .git gitlink 檔、命中主專案 .git 資料夾,
#          故 config / cache 一律落在「主專案」AgentCommands, 不誤寫共享 submodule。
# ===========================================================
_THIS = Path(__file__).resolve()


def _find_git_root(start: Path):
    # 逐層向上找 .git「資料夾」(submodule 的 .git 是檔案 → 被 is_dir 跳過)
    p = start.resolve()
    while p != p.parent:
        if (p / ".git").is_dir():
            return p
        p = p.parent
    return None


def repo_root() -> Path:
    # 優先吃 CLAUDE_PROJECT_DIR (agent 環境); 其次從 script 位置 walk; 最後 cwd walk / UCL_Core 根
    env = os.environ.get("CLAUDE_PROJECT_DIR")
    if env and Path(env).is_dir():
        return Path(env).resolve()
    walked = _find_git_root(_THIS)
    if walked:
        return walked
    return _find_git_root(Path.cwd()) or _THIS.parents[2]



# ⚠ pointer 檔讀取已收斂到 _lib/ucl_paths.py（Tim 2026-08-17 拍板）。
#   原本有 10 份平行實作，每份自己 read_text().strip()。十份都對，
#   但十份就是十個會各自漂移的真相源；而漂移的症狀是「這支讀 A 目錄、那支讀 B 目錄」，
#   兩邊都不報錯。⇒ 之後改 pointer 檔格式只需改一處。
_UCL_PATHS_CACHE = None


def _ucl_paths_mod():
    global _UCL_PATHS_CACHE
    if _UCL_PATHS_CACHE is None:
        import importlib.util as _ilu
        from pathlib import Path as _P
        _spec = _ilu.spec_from_file_location(
            "_ucl_paths_shared", _P(__file__).resolve().parent / "_lib" / "ucl_paths.py")
        _m = _ilu.module_from_spec(_spec)
        _spec.loader.exec_module(_m)
        _UCL_PATHS_CACHE = _m
    return _UCL_PATHS_CACHE


def data_root() -> Path:    return _ucl_paths_mod().data_root()    # 委派唯一實作


def config_path() -> Path:
    """screenstream daemon 設定檔 — STT/OCR 欄位的落點 (per-project)。"""
    return data_root() / "_screenstream" / "_config.json"


def audio_transcribe_path() -> Path:
    """專案端 STT 工具 (EOV-local) — test-stt 委派對象; 不存在則該 op 誠實報缺。"""
    return data_root() / "Tools" / "audio_transcribe.py"


# ===========================================================
# 區塊：user-site 補path — 對齊 audio_transcribe.py 的 _ensure_user_site 教訓
# 物理意義: whisper/torch 裝在 pip --user 的 user-site; Unity spawn 的子行程有時 sys.path 沒帶到,
#          且 system site 存在殘缺 torch 孤兒 → 必須把 user-site 插在 system site「之前」。
# ===========================================================
def _ensure_user_site() -> None:
    # 2026-07-26 Tim QA 追坑: Unity spawn 的子行程裡 user-site「在 sys.path 但排在 system site 之後」→
    # system 的 CPU torch 先被解析、Roaming 的 cu126 版被遮蔽 (shell 正常、頁面異常的「同名不同環境」案)。
    # 故不只補「缺席」, 也要修「順序」: user-site 一律搬到第一個 system site-packages 之前。
    try:
        import site
        candidates = []
        try:
            usp = site.getusersitepackages()
            if usp:
                candidates.append(usp)
        except Exception:
            pass
        # APPDATA fallback (site 模組算不出時, e.g. 特殊 env) — 對齊 audio_transcribe 的多重候選
        appdata = os.environ.get("APPDATA")
        if appdata:
            candidates.append(os.path.join(appdata, "Python",
                                           f"Python{sys.version_info.major}{sys.version_info.minor}",
                                           "site-packages"))
        # 第一個 system site-packages 的 index (user-site 目標插入點)
        def _first_system_idx() -> int:
            for i, p in enumerate(sys.path):
                pl = (p or "").lower()
                if "site-packages" in pl and "roaming" not in pl:
                    return i
            return len(sys.path)
        for usp in candidates:
            if not (usp and os.path.isdir(usp)):
                continue
            sys_idx = _first_system_idx()
            if usp in sys.path:
                cur = sys.path.index(usp)
                if cur > sys_idx:
                    # 順序錯 (user-site 落在 system 後) → 搬到 system 前
                    sys.path.remove(usp)
                    sys.path.insert(_first_system_idx(), usp)
            else:
                sys.path.insert(sys_idx, usp)
            break  # 第一個有效候選處理完即止
    except Exception:
        pass  # fail-soft: 補不進去就用原 sys.path, 讓 import 檢查誠實反映

# ===========================================================
# 區塊：config 欄位白名單 — 「頁面可調」的 STT/OCR 欄位與型別
# 物理意義: _config.json 還有 fps/resolution 等錄影欄位, 那些歸 UCL_ScreenStreamPage 管;
#          本工具只動影音辨識相關欄位, 防止管理頁誤寫壞錄影設定。
# 數值影響: stt_enabled 是「實效值」(daemon worker lifecycle 綁它, 且與錄影開關耦合),
#          管理頁只讀不寫 — 可寫的是 stt_setting (Tim 意圖, 持久化) 等。
# ===========================================================
EDITABLE_KEYS = {
    # key: (type, 說明)
    "stt_setting":   (bool,  "STT 意圖開關 (錄影時同步啟動語音轉錄)"),
    "stt_model":     (str,   "whisper 模型 (tiny/base/small/medium/large-v3)"),
    "stt_lang":      (str,   "轉錄語言 hint (ja/zh/en/auto)"),
    "stt_chunk_sec": (int,   "daemon 連續轉錄的分段秒數"),
    "stt_prompt":    (str,   "whisper initial_prompt 詞彙偏置 (人名用原文字形)"),
    "ocr_enabled":   (bool,  "字幕 OCR 開關"),
    "ocr_workers":   (int,   "OCR worker 數"),
    # 字幕帶座標 — 底部原點語意 (Tim 2026-07-28 拍板: 0=畫面下方, 高度往上長)
    "ocr_y_bottom_pct": (float, "字幕帶底邊離畫面下緣距離比例 (0=貼底)"),
    "ocr_h_pct":     (float, "字幕帶高度比例 (從底邊往上長, 0~1)"),
    "ocr_extra_regions": (list, '額外字幕判定區域 JSON list, 例: [{"y_bottom_pct":0.85,"h_pct":0.1}] (可空 [])'),
    "ocr_min_conf":  (float, "OCR 最低信度過濾 (0~1)"),
}
# 唯讀展示欄位 (status/get-config 顯示, set-config 拒改)
READONLY_KEYS = ["enabled", "stt_enabled"]
# 已退役欄位 → 給明確遷移提示 (2026-07-28 字幕帶語意改底部原點, 舊頂部原點 key 拒寫防 split-brain)
RETIRED_KEYS = {
    "ocr_y_pct": "字幕帶已改底部原點語意 — 改用 ocr_y_bottom_pct (0=畫面下方; 換算: y_bottom = 1 - y_pct - h_pct)",
}


def _load_config() -> dict:
    # 讀 daemon config; 不存在回空 dict (status 會標「daemon 未初始化」)
    p = config_path()
    if not p.exists():
        return {}
    try:
        return json.loads(p.read_text(encoding="utf-8-sig"))
    except Exception as e:
        raise SystemExit(f"❌ 讀取 config 失敗 ({p}): {e}")


def _coerce(key: str, raw: str):
    # 依白名單型別把 CLI 字串轉成 JSON 值; bool 收 true/false/1/0 (大小寫不拘)
    typ = EDITABLE_KEYS[key][0]
    if typ is bool:
        v = raw.strip().lower()
        if v in ("true", "1", "yes", "on"):
            return True
        if v in ("false", "0", "no", "off"):
            return False
        raise SystemExit(f"❌ {key} 需要 bool (true/false), 收到: {raw}")
    if typ is int:
        return int(raw)
    if typ is float:
        return float(raw)
    if typ is list:
        # list 型別欄位 (ocr_extra_regions) 收 JSON 字串; 交 subtitle_ocr 同一套驗證正規化
        try:
            v = json.loads(raw)
        except (ValueError, TypeError) as e:
            raise SystemExit(f"❌ {key} 需要 JSON list (例: [{{\"y_bottom_pct\":0.85,\"h_pct\":0.1}}]), 解析失敗: {e}")
        if not isinstance(v, list):
            raise SystemExit(f"❌ {key} 需要 JSON list, 收到: {type(v).__name__}")
        return v
    return raw  # str 原樣


# ===========================================================
# 區塊：依賴檢查 — import 探測 (不觸發模型載入, 快速)
# ===========================================================
def _probe_import(mod: str):
    """import 一個模組, 回 (ok, version_or_error)。不載模型、不吃 VRAM。"""
    try:
        m = __import__(mod)
        ver = getattr(m, "__version__", "?")
        return True, str(ver)
    except Exception as e:
        return False, f"{type(e).__name__}: {e}"


# ─────────────────────────────────────────────────────────────────────────
# CUDA 不可用診斷 — 分辨「驅動太舊」vs「torch 是 CPU wheel」vs「無 GPU」
# 物理意義: torch.cuda.is_available()=False 有多種原因，最常見且最易誤判的是
#          「驅動支援的 CUDA 版 < torch 編譯的 CUDA 版」(驅動太舊)。此時重裝 torch 無用，
#          要更新 NVIDIA 驅動。用 nvidia-smi 抓驅動端 CUDA 版跟 torch.version.cuda 比對。
# (2026-07-27 Tim QA: RTX 2060 驅動 457.51=CUDA11.1 撞 torch cu126，誤導提示 install --torch-cuda)
# ─────────────────────────────────────────────────────────────────────────
def _ver_tuple(s):
    try:
        return tuple(int(x) for x in str(s).strip().split("."))
    except Exception:
        return ()


def _nvidia_smi_info():
    """回 (gpu_name, driver_ver, driver_cuda_ver) 或 None (無 nvidia-smi / 無 NVIDIA GPU)。"""
    import subprocess
    import re
    try:
        out = subprocess.run(
            ["nvidia-smi"], capture_output=True, text=True,
            encoding="utf-8", errors="replace", timeout=15,
        ).stdout or ""
    except Exception:
        return None
    if "Driver Version" not in out:
        return None
    drv = re.search(r"Driver Version:\s*([\d.]+)", out)
    cud = re.search(r"CUDA Version:\s*([\d.]+)", out)
    gpu = re.search(r"\|\s*\d+\s+(.+?)\s+(?:TCC|WDDM|On|Off)\b", out)
    return (
        gpu.group(1).strip() if gpu else "NVIDIA GPU",
        drv.group(1) if drv else "?",
        cud.group(1) if cud else None,
    )


def _cuda_unavailable_hint(torch) -> str:
    """CUDA 不可用時的可行動提示 — 判斷根因給對的解法 (不要一律叫人重裝 torch)。"""
    tcuda = getattr(getattr(torch, "version", None), "cuda", None)  # torch 編譯的 CUDA 版, e.g. "12.6"
    # torch 本身若是 CPU wheel (+cpu 或 version.cuda=None) → 重裝 GPU 版才對
    tver = str(getattr(torch, "__version__", ""))
    if tcuda is None or "+cpu" in tver:
        return "⚠ 不可用 (CPU only) — torch 是 CPU 版 (無 CUDA)，用 install --torch-cuda 換 GPU 版"
    info = _nvidia_smi_info()
    if info is None:
        return "⚠ 不可用 (CPU only) — 未偵測到 NVIDIA GPU / 驅動 (nvidia-smi 不可用)；獨顯請先安裝驅動"
    gpu, drv, drv_cuda = info
    if drv_cuda and _ver_tuple(drv_cuda) < _ver_tuple(tcuda):
        return (f"⚠ 不可用 (CPU only) — 驅動太舊：{gpu} 驅動 {drv} 僅支援 CUDA {drv_cuda}，"
                f"但 torch 是 CUDA {tcuda} 版。請【更新 NVIDIA 驅動】(不是重裝 torch — torch 已是 GPU 版)")
    return (f"⚠ 不可用 (CPU only) — {gpu} 驅動 {drv} (支援 CUDA {drv_cuda})、torch CUDA {tcuda} 版本相容卻不可用；"
            f"檢查 CUDA_VISIBLE_DEVICES 是否被清空、或 torch 安裝是否毀損")


def op_status() -> int:
    # 區塊職責：一頁式健檢 — 依賴 / GPU / config, 給管理頁「狀態」面板直接顯示
    _ensure_user_site()
    lines = ["🎬 影音處理 (STT/OCR) 環境狀態", ""]
    # --- python 環境 ---
    lines.append(f"Python: {sys.version.split()[0]}  ({sys.executable})")
    # --- STT 依賴 ---
    ok_w, v_w = _probe_import("whisper")
    ok_t, v_t = _probe_import("torch")
    ok_s, v_s = _probe_import("soundcard")
    ok_n, v_n = _probe_import("numpy")
    lines.append("")
    lines.append("── STT (語音轉文字) ──")
    lines.append(f"  openai-whisper : {'✅ ' + v_w if ok_w else '❌ 未安裝 (' + v_w + ')'}")
    lines.append(f"  torch          : {'✅ ' + v_t if ok_t else '❌ 未安裝 (' + v_t + ')'}")
    if ok_t:
        try:
            import torch  # noqa: F811 — 上面 probe 已確認可 import
            cuda = torch.cuda.is_available()
            if cuda:
                lines.append(f"  CUDA           : ✅ {torch.cuda.get_device_name(0)}")
            else:
                lines.append(f"  CUDA           : {_cuda_unavailable_hint(torch)}")
            # 安裝位置標示 — 分辨 system site vs user-site (雙包遮蔽診斷用, 2026-07-26 Tim QA 案例)
            tfile = str(getattr(torch, "__file__", ""))
            loc = "user-site" if "Roaming" in tfile else ("system site" if "site-packages" in tfile else "?")
            lines.append(f"  torch 位置     : {loc} ({tfile})")
        except Exception as e:
            lines.append(f"  CUDA           : ⚠ 檢查失敗 ({e})")
    lines.append(f"  soundcard      : {'✅ ' + v_s if ok_s else '❌ 未安裝 (' + v_s + ') — live 即時擷取需要'}")
    lines.append(f"  numpy          : {'✅ ' + v_n if ok_n else '❌ 未安裝 (' + v_n + ')'}")
    # --- OCR 依賴 ---
    ok_o, v_o = _probe_import("rapidocr_onnxruntime")
    ok_ort, v_ort = _probe_import("onnxruntime")
    lines.append("")
    lines.append("── OCR (字幕讀取) ──")
    lines.append(f"  rapidocr-onnxruntime : {'✅ ' + v_o if ok_o else '❌ 未安裝 (' + v_o + ')'}")
    lines.append(f"  onnxruntime          : {'✅ ' + v_ort if ok_ort else '❌ 未安裝 (' + v_ort + ')'}")
    if ok_ort:
        # 檢查 CUDA ExecutionProvider — onnxruntime-gpu 裝了且驅動可用才會列出
        try:
            import onnxruntime  # noqa: F811 — 上面 probe 已確認可 import
            providers = list(onnxruntime.get_available_providers())
            has_cuda = "CUDAExecutionProvider" in providers
            if has_cuda:
                # provider「已註冊」≠ 實際能在 GPU 跑 — 同一張老驅動也會拖累 OCR CUDA (同 torch 根因)。
                info = _nvidia_smi_info()
                caveat = ""
                if info and info[2] and _ver_tuple(info[2]) < (12, 0):
                    caveat = f"（⚠ 但驅動僅支援 CUDA {info[2]}，實際 inference 可能 fallback CPU；更新驅動後再驗）"
                lines.append(f"  OCR CUDA             : ✅ CUDAExecutionProvider 已註冊{caveat}")
            else:
                lines.append(f"  OCR CUDA             : ⚠ 僅 CPU ({', '.join(providers)}) — 可用 install --ocr-cuda 換 GPU 版")
        except Exception as e:
            lines.append(f"  OCR CUDA             : ⚠ 檢查失敗 ({e})")
    # --- 專案端工具 ---
    at = audio_transcribe_path()
    lines.append("")
    lines.append("── 專案端工具 ──")
    lines.append(f"  audio_transcribe.py : {'✅ ' + str(at) if at.exists() else '⚠ 不存在 (' + str(at) + ') — test-stt 不可用'}")
    # --- daemon config ---
    cfg = _load_config()
    cp = config_path()
    lines.append("")
    lines.append(f"── daemon config ({cp}) ──")
    if not cfg:
        lines.append("  ⚠ config 不存在 — screenstream daemon 尚未初始化 (Editor 開過錄影頁才會生成)")
    else:
        for k in READONLY_KEYS:
            if k in cfg:
                lines.append(f"  {k} (唯讀) = {json.dumps(cfg.get(k), ensure_ascii=False)}")
        for k in EDITABLE_KEYS:
            if k in cfg:
                lines.append(f"  {k} = {json.dumps(cfg.get(k), ensure_ascii=False)}")
    print("\n".join(lines))
    return 0


def op_get_config() -> int:
    # 區塊職責：給 C# 頁面填表用的純 JSON — 白名單欄位 + 唯讀欄位 + 檔案存在性
    cfg = _load_config()
    fields = {k: cfg.get(k) for k in EDITABLE_KEYS if k in cfg}
    # 舊 config 遷移展示 (2026-07-28 底部原點語意): 只有頂部原點 ocr_y_pct 的 config →
    # 換算出 ocr_y_bottom_pct 給頁面回填, 頁面下次「套用」就寫新 key 完成遷移
    if "ocr_y_bottom_pct" not in fields and "ocr_y_pct" in cfg:
        try:
            h = float(cfg.get("ocr_h_pct", 0.12))
            fields["ocr_y_bottom_pct"] = round(max(0.0, min(1.0, 1.0 - float(cfg["ocr_y_pct"]) - h)), 4)
        except (TypeError, ValueError):
            pass
    out = {
        "config_path": str(config_path()),
        "exists": bool(cfg),
        "readonly": {k: cfg.get(k) for k in READONLY_KEYS if k in cfg},
        "fields": fields,
    }
    print(json.dumps(out, ensure_ascii=False, indent=2))
    return 0


def op_set_config(pairs: list[str]) -> int:
    # 區塊職責：白名單欄位寫回 — k=v 逐對驗證/型別轉換, 一次讀改寫, 保留其餘欄位不動
    p = config_path()
    if not p.exists():
        print(f"❌ config 不存在 ({p}) — 請先在 Editor 開啟 ScreenStream 錄影頁讓 daemon 生成 config。")
        return 1
    cfg = _load_config()
    changed = []
    for pair in pairs:
        if "=" not in pair:
            print(f"❌ 參數格式須為 key=value, 收到: {pair}")
            return 1
        k, v = pair.split("=", 1)
        k = k.strip()
        if k in RETIRED_KEYS:
            print(f"❌ 欄位 `{k}` 已退役: {RETIRED_KEYS[k]}")
            return 1
        if k not in EDITABLE_KEYS:
            print(f"❌ 欄位 `{k}` 不在白名單 (可調: {', '.join(EDITABLE_KEYS)})")
            return 1
        newv = _coerce(k, v)
        if cfg.get(k) != newv:
            changed.append(f"{k}: {json.dumps(cfg.get(k), ensure_ascii=False)} → {json.dumps(newv, ensure_ascii=False)}")
        cfg[k] = newv
    # 寫回 — indent 2 與 daemon 既有格式一致; ensure_ascii=False 保中文
    p.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
    if changed:
        print("✅ config 已更新:\n  " + "\n  ".join(changed))
        print("ℹ daemon 每 loop reload config → 錄影中改動即時生效, 不需停/啟錄影。stt_model/lang/prompt 改動由 daemon T-STT-AutoRestart 自動重起 worker 套用 (~1 loop + whisper reload 數秒), 無需手動 toggle。")
    else:
        print("✅ config 無變化 (值相同)。")
    return 0


# ===========================================================
# 區塊：安裝 — pip --user (與 audio_transcribe/knowledge_base 同慣例, 落 user-site)
# 物理意義: --user 避免動 system site (那裡有殘缺 torch 孤兒的前科); 安裝輸出原樣透傳給頁面。
# ===========================================================
def _pip_install(args: list[str]) -> int:
    cmd = [sys.executable, "-m", "pip", "install", "--user"] + args
    print(f"$ {' '.join(cmd)}")
    sys.stdout.flush()
    r = subprocess.run(cmd)
    print("✅ 安裝完成" if r.returncode == 0 else f"❌ pip 失敗 (exit {r.returncode})")
    return r.returncode


def _pip_uninstall(pkgs: list[str]) -> int:
    # 區塊職責：反覆 uninstall 直到該套件在所有 site 都不存在。
    # 物理意義：pip 一次只卸「sys.path 順位最前」的那一份 — user-site 與 system site 可能各有一份
    #          (torch 孤兒的前科就是這樣來的)。只跑一次會留下被遮蔽的第二份, 而 status 仍會顯示 ✅,
    #          於是「解除安裝成功」是假的。迴圈到 pip 不再回報 Successfully 為止才算真的乾淨。
    # 數值影響：上限 4 輪 — 正常最多 2 份 (user/system), 留餘裕但不無限迴圈。
    rc = 0
    for _ in range(4):
        cmd = [sys.executable, "-m", "pip", "uninstall", "-y"] + pkgs
        print(f"$ {' '.join(cmd)}")
        sys.stdout.flush()
        r = subprocess.run(cmd, capture_output=True, text=True)
        out = ((r.stdout or "") + (r.stderr or "")).strip()
        if out:
            print(out[-1500:])
        if "Successfully uninstalled" not in out:
            break   # 這一輪沒卸掉任何東西 → 已經沒有殘留
    return rc


def _install_torch_cuda() -> int:
    # 區塊職責：torch 換 CUDA wheel — 體積大 (數 GB), 頁面按鈕已標注耐心等。
    # ⚠ 2026-07-26 血教訓 (Tim QA): 只帶 --upgrade 會踩「已滿足」陷阱 —
    #   system site 若已有較新的 CPU 版 (如 2.13.0+cpu > cu124 index 最新 2.6.0),
    #   pip 認定 requirement satisfied → 空跑 exit 0 → 誤報安裝完成。
    #   對策: ①index 換 cu126 (有與新版同號的 +cu126 build) ②--force-reinstall 強制重裝
    #        ③裝完 subprocess 實測 torch.cuda.is_available() 才算成功 (不信 pip exit 0)。
    #   ④--force-reinstall 會連依賴一起強制重裝, 但 --index-url 已把 PyPI 換成 pytorch index —
    #     那上面沒有 typing-extensions 等一般依賴 → ResolutionImpossible (2026-07-26 第二層坑)。
    #     故 torch 本體帶 --no-deps 強裝 (CUDA 版與同號 CPU 版依賴集相同, 既有依賴照用),
    #     驗收 subprocess 會抓到真缺依賴的情況, 不會靜默壞。
    rc = _pip_install(["torch", "--index-url", "https://download.pytorch.org/whl/cu126",
                       "--upgrade", "--force-reinstall", "--no-deps"])
    return rc if rc != 0 else _verify_torch_cuda()


def _reinstall_ort_gpu() -> int:
    # 區塊職責：onnxruntime GPU 乾淨重裝 — 卸雙 dist → 清殘骸 → 裝 gpu → 驗收 providers
    # 物理意義: rapidocr 底層走 onnxruntime — 換 onnxruntime-gpu 後 CUDAExecutionProvider
    #          才會列入 available providers (status 的「OCR CUDA」行驗收)。
    # ⚠ 2026-07-26 嵌合體坑 (Tim QA): onnxruntime 與 onnxruntime-gpu 兩個 dist 共用同一個
    #   onnxruntime/ package 目錄 — 疊裝會混出「GPU 的 provider DLL + CPU 的 pybind pyd」嵌合體,
    #   providers 只剩 [Azure, CPU]。正解: 先把兩個 dist 全部 uninstall 乾淨 (user+system 都掃),
    #   清掉殘留資料夾, 再乾淨裝 onnxruntime-gpu, 最後 subprocess 實測 providers 含 CUDA 才算成功。
    import shutil
    # (1) 反覆 uninstall 直到兩個 dist 都不見 (pip 一次只卸 sys.path 順位最前的那份; user/system 都可能有)
    for _ in range(4):
        r = subprocess.run([sys.executable, "-m", "pip", "uninstall", "-y", "onnxruntime", "onnxruntime-gpu"],
                           capture_output=True, text=True)
        out = (r.stdout or "") + (r.stderr or "")
        print(out.strip()[-300:] if out.strip() else "(pip uninstall 無輸出)")
        if "Successfully uninstalled" not in out:
            break  # 兩個 dist 都已不存在 → 卸載階段完成
    # (2) 清殘留 package 目錄 (嵌合體卸載後 RECORD 缺漏常留孤兒檔) — 只動 onnxruntime/ 本體
    try:
        import site
        roots = [p for p in ([site.getusersitepackages()] + list(site.getsitepackages())) if p and os.path.isdir(p)]
    except Exception:
        roots = []
    for root in roots:
        leftover = os.path.join(root, "onnxruntime")
        if os.path.isdir(leftover):
            print(f"清除殘留: {leftover}")
            shutil.rmtree(leftover, ignore_errors=True)
    # (3) 乾淨安裝 GPU 版 (user-site)
    rc = _pip_install(["onnxruntime-gpu"])
    if rc != 0:
        return rc
    # (4) 驗收 — 新 subprocess 實測 providers 含 CUDAExecutionProvider (pip exit 0 不算數)
    code = ("import sys; sys.path.insert(0, r'" + str(_THIS.parent) + "'); "
            "import media_admin; media_admin._ensure_user_site(); "
            "import onnxruntime as ort; pv = ort.get_available_providers(); "
            "print(f'verify: onnxruntime {ort.__version__} @ {ort.__file__}'); print('providers:', pv); "
            "ok = 'CUDAExecutionProvider' in pv; "
            "print('✅ OCR CUDA 可用' if ok else '❌ CUDAExecutionProvider 不在 providers'); "
            "sys.exit(0 if ok else 1)")
    r = subprocess.run([sys.executable, "-c", code])
    if r.returncode != 0:
        print("❌ onnxruntime GPU 驗收失敗 — 請看上方 verify 行。")
    return r.returncode


def _verify_torch_cuda() -> int:
    # 區塊職責：安裝後驗收 — 新開 subprocess import torch (避免本行程已載入舊版), 實測 CUDA 可用性
    # 物理意義：pip exit 0 ≠ 裝對 (見上方血教訓); 只有 torch.cuda.is_available()=True 才算 GPU 版就緒
    # ⚠ 驗收子行程也要先修 user-site 順序 (從 Unity 頁面環境 spawn 時同樣會被 system CPU 版遮蔽) —
    #   借道本 script import 自己的 _ensure_user_site, 保證與 status 同一套路徑邏輯。
    code = ("import sys; sys.path.insert(0, r'" + str(_THIS.parent) + "'); "
            "import media_admin; media_admin._ensure_user_site(); "
            "import torch; ok = torch.cuda.is_available(); "
            "print(f'verify: torch {torch.__version__} @ {torch.__file__}'); "
            "print(('✅ CUDA 可用: ' + torch.cuda.get_device_name(0)) if ok else '❌ CUDA 不可用 (仍是 CPU 版或驅動問題)'); "
            "sys.exit(0 if ok else 1)")
    r = subprocess.run([sys.executable, "-c", code])
    if r.returncode != 0:
        print("❌ torch CUDA 驗收失敗 — 安裝流程有跑但 GPU 不可用, 請看上方 verify 行判斷是版本還是驅動問題。")
    return r.returncode


def _install_faster_whisper() -> int:
    # 區塊職責：裝 faster-whisper，**而且不准弄壞 OCR 的 GPU onnxruntime**。
    # 物理意義：faster-whisper 宣告相依 `onnxruntime`（vad_filter 的 Silero 走 ONNX）。
    #          但本機 OCR 走的是 `onnxruntime-gpu` —— 兩者是**不同 dist、同一個 package 目錄**，
    #          pip 不會認為 gpu 版滿足 `onnxruntime` 這條需求，於是會把 CPU 版裝上去疊在一起。
    #          🩸 這正是本檔 `_reinstall_ort_gpu` 與 OCR 插件註解早就寫過的坑，後果是
    #          **OCR 安靜退回 CPU**（providers 少掉 CUDA，不報錯、不 crash，只是慢一個量級）。
    # 數值影響：偵測到 onnxruntime-gpu → 走 --no-deps 再補裝其餘相依（不含 onnxruntime）；
    #          沒有 gpu 版 → 照常安裝，讓 pip 自己把 CPU 版拉進來。
    # ⚠ 判斷依據是 **pip 的 dist 名**，不是 `import onnxruntime` 成不成功 ——
    #   後者兩種 dist 都會成功，用它判等於沒判。
    r = subprocess.run([sys.executable, "-m", "pip", "show", "onnxruntime-gpu"],
                       capture_output=True, text=True)
    has_ort_gpu = (r.returncode == 0)
    if not has_ort_gpu:
        return _pip_install(["faster-whisper"])

    print("ℹ 偵測到 onnxruntime-gpu（OCR 在用）—— 改走 --no-deps 安裝，避免疊上 CPU 版 onnxruntime。")
    rc = _pip_install(["--no-deps", "faster-whisper"])
    if rc != 0:
        return rc
    # faster-whisper 的其餘相依顯式補齊（刻意不含 onnxruntime）。
    # 已滿足的項目 pip 是 no-op，所以多列不會重裝；漏列才會變成 import 期才發現的缺件。
    return _pip_install(["av", "ctranslate2", "tokenizers", "huggingface-hub", "tqdm"])


def _verify_faster_whisper_cuda() -> int:
    # 區塊職責：安裝後驗收 —— 在 CUDA 上**實跑一次**，不是問「裝了沒」。
    # 物理意義：ctranslate2 自帶 runtime，缺 cuDNN / cuBLAS 時的行為是**安靜退回 CPU**（不拋例外、
    #          不印警告）—— 於是「跑得完」與「跑在 GPU 上」在 stdout 上長得一模一樣。
    #          所以驗收條件是 device 建得起來 ＋ 真的轉一段音訊出來，兩者都過才算。
    # 數值影響：下載 tiny 的 CTranslate2 權重（約 75MB，HuggingFace）；VRAM 佔用極小。
    #          失敗回非 0，頁面會顯示紅字 —— 與 _verify_torch_cuda / onnxruntime 驗收同一套路。
    # ⚠ 子行程 import 而非本行程：本行程可能已載入舊版模組，那會讓驗收驗到不是等下要跑的那份。
    # ⚠ 退出碼要能分辨「沒裝」與「裝了但 GPU 沒通」——**兩個成因的處方相反**
    #   （前者去按安裝，後者去查 cuDNN）。印成同一句話的代價是叫人去修一個沒壞的東西。
    #   3 = import 失敗（沒裝）／1 = 裝了但 CUDA 這條路不通／0 = 實跑成功。
    code = (
        "import sys\n"
        "sys.path.insert(0, r'" + str(_THIS.parent) + "')\n"
        "import media_admin\n"
        "media_admin._ensure_user_site()\n"
        "try:\n"
        "    import numpy as np, ctranslate2\n"
        "    from faster_whisper import WhisperModel\n"
        "except ImportError as e:\n"
        "    print(f'verify: 尚未安裝 ({e})'); sys.exit(3)\n"
        "n = ctranslate2.get_cuda_device_count()\n"
        "print(f'verify: ctranslate2 {ctranslate2.__version__}, cuda_device_count={n}')\n"
        "if n < 1: sys.exit(1)\n"
        "m = WhisperModel('tiny', device='cuda', compute_type='float16')\n"
        "segs, info = m.transcribe(np.zeros(16000, dtype=np.float32), vad_filter=True)\n"
        "list(segs)\n"
        "print('✅ CUDA 實跑成功 — faster-whisper tiny @ float16, vad_filter 可用')\n"
        "sys.exit(0)\n"
    )
    r = subprocess.run([sys.executable, "-c", code])
    if r.returncode == 3:
        print("❌ 尚未安裝 faster-whisper —— 請先按「📦 安裝 faster-whisper」。")
        print("   （這不是 GPU 問題，別去查 cuDNN。）")
    elif r.returncode != 0:
        print("❌ faster-whisper GPU 驗收失敗 —— 套件裝好了，但 CUDA 這條路沒通。")
        print("   最常見成因：ctranslate2 找不到 cuDNN 9 / cuBLAS（CUDA 12 需要）。")
        print("   ⚠ 未通過時**不要當它能用** —— 它會安靜退回 CPU，速度差一個量級而且不報錯。")
    return r.returncode


# ===========================================================
# 區塊：插件註冊表 — 「一個插件 = 一組動作」的唯一定義處
# 物理意義：頁面的下拉選單、動作按鈕、確認文案全部由這張表生成 —— 新增一個插件只改這裡，
#          C# 端一行都不用動 (Tim 2026-08-11 拍板：插件會越來越多, 不要繼續加按鈕)。
# 數值影響：packages 是 pip 名稱, probe 是 import 名稱 (兩者常不同, 例 rapidocr-onnxruntime →
#          rapidocr_onnxruntime); danger=True 的動作在頁面上會先跳確認框。
# ⚠ 共用套件不入任何插件的 uninstall 清單 —— numpy / torch 被 daemon、montage、audio-viz 共用,
#   卸掉會靜默弄壞整條陪看鏈。torch 另立獨立動作, 由人明確選擇, 不夾帶在「解除安裝 STT」裡。
# ===========================================================
PLUGINS = {
    "stt": {
        "name": "STT 語音轉文字 (openai-whisper)",
        "desc": "whisper 轉錄 + soundcard 系統音訊擷取。torch 是它的推論後端，另立動作管理。",
        "probe": ["whisper", "soundcard"],
        "actions": [
            {"id": "install", "label": "📦 安裝 STT 依賴",
             "hint": "openai-whisper + soundcard + numpy（預設拉 CPU 版 torch）", "danger": False},
            {"id": "uninstall", "label": "🗑 解除安裝 STT 依賴",
             "hint": "只卸 openai-whisper + soundcard。numpy 與 torch 保留 —— 它們被錄影/縮圖/音訊視覺化共用。",
             "danger": True},
        ],
    },
    "torch": {
        "name": "torch 推論後端 (STT 用)",
        "desc": "whisper 的推論後端。CUDA 版可 GPU 加速；體積大，切換請耐心等。",
        "probe": ["torch"],
        "actions": [
            {"id": "cuda", "label": "⚡ 換 CUDA 版 (cu126 wheel)",
             "hint": "GPU 加速轉錄。裝完會實測 torch.cuda.is_available() 才算成功。", "danger": False},
            {"id": "uninstall", "label": "🗑 解除安裝 torch",
             "hint": "⚠ 卸掉後 whisper 無法推論，STT 整條鏈停擺。重裝需重新下載數 GB。", "danger": True},
        ],
    },
    "faster-whisper": {
        "name": "STT 後端 · faster-whisper (CTranslate2)",
        # ⚠ 這些字串會被 IMGUI 的 richText Label 直接印出來 —— 支援的是 <b>/<color>，
        #   **markdown** 與 `反引號` 都會原樣顯示（2026-08-16 現場截圖抓到）。要強調用 <b>。
        "desc": ("openai-whisper 的替代後端。① 有 vad_filter（Silero VAD）＝「自動切在靜音處」的前提，"
                 "openai-whisper 沒有這個參數；② 支援量化，同模型 VRAM 大降"
                 "（本機實測 openai medium 佔 ~4.6GB）。不依賴 torch，與現行後端可並存、可回退。"),
        "probe": ["faster_whisper", "ctranslate2"],
        "actions": [
            {"id": "install", "label": "📦 安裝 faster-whisper",
             "hint": ("純加法：不動 torch、不動 numpy。偵測到 OCR 的 onnxruntime-gpu 會自動改走 "
                      "--no-deps，否則 CPU 版會疊上去讓 OCR <b>安靜退回 CPU</b>。裝完自動驗 GPU。"),
             "danger": False},
            {"id": "verify", "label": "🔍 只驗 GPU（不安裝）",
             "hint": ("下載 tiny（約 75MB）在 CUDA 實跑一次。必要 —— ctranslate2 找不到 cuDNN 時會"
                      "<b>安靜退回 CPU</b>，不報錯。"),
             "danger": False},
            {"id": "uninstall", "label": "🗑 解除安裝 faster-whisper",
             "hint": ("卸 faster-whisper + ctranslate2 + av。onnxruntime <b>保留</b> —— OCR 共用，"
                      "一起卸會靜默弄壞字幕辨識。"),
             "danger": True},
        ],
    },
    "ocr": {
        "name": "OCR 字幕讀取 (rapidocr)",
        "desc": "縮圖牆字幕辨識。底層走 onnxruntime，CPU/GPU 兩個 dist 不可疊裝。",
        "probe": ["rapidocr_onnxruntime", "onnxruntime"],
        "actions": [
            {"id": "install", "label": "📦 安裝 OCR 依賴",
             "hint": "rapidocr-onnxruntime（含 CPU 版 onnxruntime）", "danger": False},
            {"id": "cuda", "label": "⚡ 換 CUDA 版 (onnxruntime-gpu)",
             "hint": "先卸乾淨兩個 dist 再裝 GPU 版，最後實測 providers 含 CUDA 才算成功。", "danger": False},
            {"id": "cpu", "label": "↩ 還原 CPU 版 onnxruntime",
             "hint": "不是移除，是降級：卸 onnxruntime-gpu → 裝回 CPU 版，OCR 仍可用。", "danger": True},
            {"id": "uninstall", "label": "🗑 解除安裝 OCR 依賴",
             "hint": "⚠ 卸 rapidocr-onnxruntime + onnxruntime(+gpu)。卸掉後 montage --ocr 無字幕輸出。",
             "danger": True},
        ],
    },
}


# ===========================================================
# 區塊：模型權重管理 — 「插件是 pip 套件、模型是快取目錄」，兩件事分開管
# 物理意義：兩個後端的權重落在**完全不同的快取**：
#          openai-whisper → ~/.cache/whisper/<size>.pt（單檔）
#          faster-whisper → HuggingFace hub/models--Systran--faster-whisper-<size>/（目錄）
# 數值影響：medium 級距 openai 約 1.4GB、faster-whisper 約 1.5GB —— 這是「刪掉能拿回多少空間」的量級。
# ⛔ **絕不提供「清空 HF 快取」這種動作**：同一個 hub 目錄裡躺著別的系統在用的模型
#    （2026-08-16 實測本機有 BAAI--bge-m3 8.5GB）。一律**逐個具名**刪。
# ===========================================================
MODEL_SIZES = ("tiny", "base", "small", "medium", "large-v3")
_FW_REPO_TPL = "Systran/faster-whisper-{size}"


def _whisper_cache_root() -> Path:
    return Path(os.environ.get("XDG_CACHE_HOME", Path.home() / ".cache")) / "whisper"


def _hf_hub_root() -> Path:
    hf = os.environ.get("HF_HOME")
    base = Path(hf) if hf else (Path.home() / ".cache" / "huggingface")
    return base / "hub"


def _dir_size_bytes(path: Path) -> tuple[int, int]:
    """→ (完整檔位元組, 未完成檔位元組)。

    # ⚠ **`.incomplete` 不算數** —— HuggingFace 下載中斷會留下部分 blob，
    #   把它們算進大小的話，面板會顯示「✅ 已安裝 1385MB」而模型其實載不起來。
    #   🩸 2026-08-16 現場：worker 被 supervisor 連砍 3 次，留下 8 個 .incomplete，
    #   而我第一版就是這樣把殘骸算成了「已安裝」。**外觀 OK ≠ 真的 OK，我自己犯的那次。**
    """
    if path.is_file():
        return path.stat().st_size, 0
    if not path.is_dir():
        return 0, 0
    done = part = 0
    for f in path.rglob("*"):
        if not f.is_file():
            continue
        if f.name.endswith(".incomplete"):
            part += f.stat().st_size
        else:
            done += f.stat().st_size
    return done, part


def _model_entry(backend: str, size: str) -> dict:
    if backend == "openai-whisper":
        path = _whisper_cache_root() / f"{size}.pt"
    else:
        path = _hf_hub_root() / ("models--" + _FW_REPO_TPL.format(size=size).replace("/", "--"))
    n, partial = _dir_size_bytes(path)
    # installed 的判準刻意帶下限：殘骸也有位元組，>0 會把「下載中斷」讀成「已安裝」。
    # 32MB 低於任何一個 whisper 級距（最小的 tiny 就 72MB），所以不會誤判真的小模型。
    return {"id": f"{backend}:{size}", "backend": backend, "size_name": size,
            "path": str(path), "bytes": n, "mb": round(n / 1048576, 1),
            "partial_mb": round(partial / 1048576, 1),
            "installed": n > 32 * 1048576}


def op_list_models() -> int:
    # 區塊職責：把兩個快取的現況吐成 JSON 給頁面建下拉（同 list-plugins 的分工：清單不在 C# 維護）
    out = {"models": []}
    for backend in ("openai-whisper", "faster-whisper"):
        for size in MODEL_SIZES:
            out["models"].append(_model_entry(backend, size))
    print(json.dumps(out, ensure_ascii=False, indent=1))
    return 0


def _stt_worker_busy() -> str:
    """回非空字串＝現在不該動權重檔（字串本身就是理由）。空字串＝可以動。"""
    # 物理意義：worker 跑起來時權重檔是**開著的**，Windows 上刪不掉（而錯誤訊息會長得像檔案損毀）。
    #          與其讓人看一個 IOException 去猜，不如在動手前就說清楚「先停錄影」。
    try:
        cfg_path = data_root() / "_screenstream" / "_config.json"
        cfg = json.loads(cfg_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return ""      # 讀不到設定 → 不阻擋（讀不到 ≠ 正在跑）
    if cfg.get("stt_enabled") or cfg.get("enabled"):
        return "STT worker 可能正在執行（config: enabled/stt_enabled 為 true）—— 先在錄影頁停錄影再刪"
    return ""


def op_model(model_id: str, action: str) -> int:
    # ⚠ 未知 id/action 一律 fail-fast 並列出合法值（同 op_plugin —— 刪權重不可逆，猜錯代價是重下 1.5GB）
    valid_actions = ("download", "delete", "clean-partial")
    if action not in valid_actions:
        print(f"❌ 未知動作 `{action}` (可用: {', '.join(valid_actions)})")
        return 1
    try:
        backend, size = model_id.split(":", 1)
    except ValueError:
        print(f"❌ id 格式應為 <backend>:<size>，收到 `{model_id}`")
        return 1
    if backend not in ("openai-whisper", "faster-whisper") or size not in MODEL_SIZES:
        print(f"❌ 未知模型 `{model_id}`（backend 需為 openai-whisper/faster-whisper，"
              f"size 需為 {', '.join(MODEL_SIZES)}）")
        return 1

    e = _model_entry(backend, size)
    if action == "clean-partial":
        # 區塊職責：清掉 HuggingFace 下載中斷留下的 .incomplete blob。
        # 物理意義：它們不是模型的一部分，只是沒下完的碎片；留著純浪費磁碟，
        #          而且會讓「這個模型佔多大」這個問題答錯（見 _dir_size_bytes 的血證）。
        # ⚠ 只刪 .incomplete，**不碰任何完整檔** —— 清殘骸不該有把模型清掉的風險。
        root = Path(e["path"])
        if not root.is_dir():
            print(f"ℹ {model_id} 沒有目錄可清（{root}）")
            return 0
        n_del = freed = 0
        for f in root.rglob("*.incomplete"):
            try:
                freed += f.stat().st_size
                f.unlink()
                n_del += 1
            except OSError as ex:
                print(f"⚠ 刪不掉 {f.name}: {ex}")
        print(f"🧹 清掉 {n_del} 個未完成碎片，釋出 {round(freed / 1048576, 1)} MB")
        return 0

    if action == "delete":
        if not e["installed"]:
            print(f"ℹ {model_id} 本來就不存在（{e['path']}）—— 不當成失敗。")
            return 0
        busy = _stt_worker_busy()
        if busy:
            print(f"❌ 拒絕刪除：{busy}")
            return 1
        import shutil
        target = Path(e["path"])
        print(f"🗑 刪除 {model_id} — {e['mb']} MB")
        print(f"   {target}")
        try:
            if target.is_dir():
                shutil.rmtree(target)
            else:
                target.unlink()
        except OSError as ex:
            print(f"❌ 刪除失敗（檔案可能仍被開著）：{ex}")
            return 1
        after = _model_entry(backend, size)
        print("✅ 已刪除" if not after["installed"] else f"❌ 刪不乾淨，仍有 {after['mb']} MB")
        return 0 if not after["installed"] else 1

    # download —— 刻意**在管理頁下載**而不是讓即時 worker 首跑時邊跑邊下載：
    # 🩸 2026-08-16 實測：worker 首跑下載 medium(1.5GB) 期間沒有任何產物，
    #    被 supervisor 的 90s 停滯偵測連砍 3 次 → 下載永遠完不成（livelock）。
    print(f"📥 下載 {model_id} … 這步會花數分鐘且不可省（權重不在本機就沒得跑）")
    sys.stdout.flush()
    if backend == "faster-whisper":
        code = ("from huggingface_hub import snapshot_download; "
                f"p = snapshot_download(repo_id='{_FW_REPO_TPL.format(size=size)}'); "
                "print('✅ 下載完成:', p)")
    else:
        code = ("import whisper, os; "
                f"p = whisper._download(whisper._MODELS['{size}'], "
                "os.path.join(os.path.expanduser('~'), '.cache', 'whisper'), False); "
                "print('✅ 下載完成:', p if isinstance(p, str) else '(已快取)')")
    r = subprocess.run([sys.executable, "-c", code])
    if r.returncode != 0:
        print("❌ 下載失敗 —— 上方是原始錯誤；網路/HF 限流是最常見成因。")
        return r.returncode
    after = _model_entry(backend, size)
    print(f"📦 落地檢查：{after['mb']} MB @ {after['path']}")
    # ⚠ 驗收看落地檔不看 exit code —— 「跑完了」與「東西在磁碟上」是兩件事
    return 0 if after["installed"] else 1


def op_list_plugins() -> int:
    # 區塊職責：把註冊表 + 當前安裝狀態吐成 JSON 給 C# 頁面建下拉選單
    # 物理意義：頁面不自己維護清單 → 表與 UI 永遠同步 (不會出現「表加了、按鈕沒加」的漂移)
    _ensure_user_site()
    out = {"plugins": []}
    for pid, meta in PLUGINS.items():
        probes = []
        installed = True
        for mod in meta["probe"]:
            ok, ver = _probe_import(mod)
            probes.append({"module": mod, "ok": ok, "info": ver})
            installed = installed and ok
        out["plugins"].append({
            "id": pid,
            "name": meta["name"],
            "desc": meta["desc"],
            "installed": installed,
            "probes": probes,
            "actions": meta["actions"],
        })
    print(json.dumps(out, ensure_ascii=False, indent=2))
    return 0


def op_plugin(plugin_id: str, action: str) -> int:
    # 區塊職責：執行某插件的某個動作 — 頁面唯一的安裝/解除安裝入口
    # ⚠ 未知 id/action 一律 fail-fast 並列出合法值, 不做模糊比對 (裝/卸是不可逆動作, 猜錯代價太大)
    meta = PLUGINS.get(plugin_id)
    if meta is None:
        print(f"❌ 未知插件 `{plugin_id}` (可用: {', '.join(PLUGINS)})")
        return 1
    valid = [a["id"] for a in meta["actions"]]
    if action not in valid:
        print(f"❌ 插件 `{plugin_id}` 沒有動作 `{action}` (可用: {', '.join(valid)})")
        return 1
    print(f"▶ {meta['name']} — {action}")
    sys.stdout.flush()

    if plugin_id == "stt":
        if action == "install":
            return _pip_install(["openai-whisper", "soundcard", "numpy"])
        if action == "uninstall":
            # numpy / torch 刻意不卸 —— 見 PLUGINS 註解的共用套件警告
            rc = _pip_uninstall(["openai-whisper", "soundcard"])
            print("ℹ numpy 與 torch 保留（錄影/縮圖/音訊視覺化共用）。要卸 torch 請選 torch 插件。")
            return rc
    if plugin_id == "torch":
        if action == "cuda":
            return _install_torch_cuda()
        if action == "uninstall":
            rc = _pip_uninstall(["torch"])
            print("ℹ torch 已卸除 — whisper 將無法推論。要恢復請先跑 STT 插件的『安裝』(會拉回 CPU 版 torch)。")
            return rc
    if plugin_id == "faster-whisper":
        if action == "install":
            rc = _install_faster_whisper()
            if rc != 0:
                return rc
            return _verify_faster_whisper_cuda()
        if action == "verify":
            return _verify_faster_whisper_cuda()
        if action == "uninstall":
            # ⚠ onnxruntime 不卸 —— OCR 插件共用（同 numpy/torch 的共用套件規則）
            rc = _pip_uninstall(["faster-whisper", "ctranslate2", "av"])
            print("ℹ onnxruntime 保留（OCR 插件共用）。openai-whisper 未受影響，STT 仍可用舊後端。")
            return rc
    if plugin_id == "ocr":
        if action == "install":
            return _pip_install(["rapidocr-onnxruntime"])
        if action == "cuda":
            return _reinstall_ort_gpu()
        if action == "cpu":
            # 降級而非移除：兩個 dist 共用同一個 package 目錄, 必須先卸乾淨再裝 CPU 版 (同 _reinstall_ort_gpu 教訓)
            _pip_uninstall(["onnxruntime", "onnxruntime-gpu"])
            rc = _pip_install(["onnxruntime"])
            print("ℹ 已還原 CPU 版 onnxruntime — OCR 仍可用, 只是不吃 GPU。" if rc == 0 else "")
            return rc
        if action == "uninstall":
            return _pip_uninstall(["rapidocr-onnxruntime", "onnxruntime", "onnxruntime-gpu"])
    print(f"❌ 動作 `{action}` 尚未實作 (插件 {plugin_id})")
    return 1


def op_test_stt(sec: int, model: str, lang: str) -> int:
    # 區塊職責：試錄 — 委派專案端 audio_transcribe.py live N (真正的擷取/轉錄邏輯不重造)
    at = audio_transcribe_path()
    if not at.exists():
        print(f"❌ 專案端 audio_transcribe.py 不存在 ({at}) — 本專案未啟用 STT 工具, 無法試錄。")
        return 1
    cmd = [sys.executable, str(at), "live", str(sec), "--model", model, "--lang", lang]
    print(f"🎙 試錄 {sec}s (model={model}, lang={lang}) — 委派 audio_transcribe.py live…")
    sys.stdout.flush()
    r = subprocess.run(cmd)
    return r.returncode


# ===========================================================
# 區塊：CLI 入口
# ===========================================================
def main(argv: list[str]) -> int:
    import argparse
    ap = argparse.ArgumentParser(description="media_admin.py — 影音處理 (STT/OCR) 管理後端")
    sub = ap.add_subparsers(dest="op", required=True)
    sub.add_parser("status")
    sub.add_parser("get-config")
    p_set = sub.add_parser("set-config")
    p_set.add_argument("pairs", nargs="+", help="key=value (白名單欄位)")
    sub.add_parser("list-plugins")
    sub.add_parser("list-models")
    p_md = sub.add_parser("model")
    p_md.add_argument("--id", required=True, help="<backend>:<size>，見 list-models")
    p_md.add_argument("--action", required=True, help="download / delete")
    p_pl = sub.add_parser("plugin")
    p_pl.add_argument("--id", required=True, help="插件 id (見 list-plugins)")
    p_pl.add_argument("--action", required=True, help="動作 id (見 list-plugins)")
    p_ts = sub.add_parser("test-stt")
    p_ts.add_argument("--sec", type=int, default=8)
    p_ts.add_argument("--model", default="small")
    p_ts.add_argument("--lang", default="ja")
    a = ap.parse_args(argv)
    if a.op == "status":
        return op_status()
    if a.op == "get-config":
        return op_get_config()
    if a.op == "set-config":
        return op_set_config(a.pairs)
    if a.op == "list-plugins":
        return op_list_plugins()
    if a.op == "list-models":
        return op_list_models()
    if a.op == "model":
        return op_model(a.id, a.action)
    if a.op == "plugin":
        return op_plugin(a.id, a.action)
    if a.op == "test-stt":
        return op_test_stt(a.sec, a.model, a.lang)
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
