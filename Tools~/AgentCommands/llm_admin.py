#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""llm_admin.py — 本地 LLM 模型管理（ollama 之上的薄層；C# 端 UCL_LLMModelAdminPage 與 agent 共用）。

區塊職責：本地大語言模型的**環境狀態 / 目錄 / 安裝 / 解除安裝 / 試跑**唯一真相源。

物理意義：
  真正持有模型的是 **ollama**（下載、量化格式、磁碟位置、載入卸載都是它的事），
  本檔不重造那一層 —— 它做的是 ollama 沒有的兩件事：
    ① **目錄（catalog）** —— 「哪些模型適合這個專案、要多少顯存」是策展知識，ollama 不知道；
    ② **結構化輸出** —— C# 端要的是 JSON，不是給人看的表格。
  ⇒ 換掉後端（llama.cpp server / LM Studio）時只改本檔，Editor 頁一行不動。

數值影響：
  `install` / `uninstall` 會真的動磁碟（模型動輒 1–5 GB）；其餘 op 唯讀。
  本檔**不啟動也不停止 ollama 服務** —— 服務生命週期歸 OS / 使用者，
  由 Editor 去 spawn 一顆常駐服務等於製造孤兒行程（domain reload 殺不掉它）。

⚠ 對側契約：C# 端是 `UCL_LLMAdminRunner` + `UCL_LLMModelAdminPage`。
  兩端要一起改 —— 只改一端的後果是 JSON 欄位對不上，而 C# 讀不到欄位時**只會顯示空值**，
  看起來跟「沒有模型」一模一樣。

用法：
    python llm_admin.py status   [--format json|text]
    python llm_admin.py list     [--format json|text]     # 目錄 × 已安裝狀態
    python llm_admin.py install   --model qwen3:4b
    python llm_admin.py uninstall --model qwen3:4b
    python llm_admin.py test      --model qwen3:4b [--prompt "..."]
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request

OLLAMA = "ollama"

# 區塊職責：找出 ollama 執行檔（PATH 優先，找不到再查已知安裝位置）。
# 物理意義：Windows 安裝完 ollama 之後，**已經在跑的 process（含 Unity Editor）拿到的 PATH 是舊的** ——
#          於是 `which("ollama")` 回 None，而磁碟上它明明就在。那個症狀跟「根本沒安裝」一模一樣，
#          使用者會照著提示再裝一次，然後再看到同一句「未安裝」（閉環）。
# 數值影響：只影響「找不找得到」；找到後所有指令都用絕對路徑跑，不依賴 PATH。
_KNOWN_WIN_PATHS = [
    r"%LOCALAPPDATA%\Programs\Ollama\ollama.exe",
    r"%PROGRAMFILES%\Ollama\ollama.exe",
]


def ollama_exe() -> tuple[str, bool]:
    """回 (執行檔路徑, 是否在 PATH 上)。找不到回 ("", False)。"""
    w = shutil.which(OLLAMA)
    if w:
        return w, True
    for raw in _KNOWN_WIN_PATHS:
        cand = os.path.expandvars(raw)
        if os.path.isfile(cand):
            return cand, False          # 找得到但不在 PATH ⇒ 重開終端機/Editor 才會生效
    return "", False
DEFAULT_TIMEOUT = 60
INSTALL_TIMEOUT = 60 * 60          # 模型可能好幾 GB；下載慢不是異常
TEST_PROMPT = "用繁體中文說一句吧檯招呼，20 字以內。"
API_BASE = "http://127.0.0.1:11434"      # ollama 的本機 HTTP API（服務預設埠）
TEST_TIMEOUT = 60                        # 試跑逾時（秒）—— 超過就斷線回報，不無限等
TEST_NUM_PREDICT = 120                   # 生成上限（token）—— 酒保只要短句，不讓它寫論文

# ─────────────────────────────────────────────────────────────────────────
# 區塊職責：模型目錄（策展清單）
# 物理意義：這裡不是「ollama 有哪些模型」（那有幾千個），是**這個專案挑過的那幾個**：
#          純聊天（酒保自動發言）用得上、6GB 顯存以下跑得動、中文可用。
#          每筆有**兩個不同的數字**，別混用：
#            · `size_gb`  = **下載量／磁碟佔用**（Q4_K_M 權重檔本身）
#            · `vram_gb`  = **實際顯存需求估值** ＝ 權重 ＋ KV cache(約 4k context) ＋ 執行期開銷
#          兩者差約 0.5–1.5GB，而使用者最常把前者讀成後者 ⇒ 以為 2.4GB 的模型 4GB 卡穩跑。
#          ⚠ 這台機器同時開著 Unity Editor（自己吃 1–3GB）—— 判準是**可用顯存**不是總顯存。
#          ⚠ 顯存不夠時 ollama **不會報錯**，只會把層數丟給 CPU ⇒ 速度掉一個數量級。
# 數值影響：只影響顯示與預設排序；安裝與否一律以 `ollama list` 的實際回報為準（見 list()）。
# ⚠ 版本號會過期。這份清單是候選，不是保證存在 —— `ollama pull` 失敗時錯誤訊息會直說。
# ─────────────────────────────────────────────────────────────────────────
CATALOG = [
    # id(ollama tag)  參數  size_gb=下載量(Q4_K_M)  vram_gb=實際顯存估值  中文0-5  recommend=純聊天推薦
    # ⚠ 兩個數字是**不同的東西**，見上方區塊註解。vram_gb 已含 KV cache 與執行期額外開銷。
    {"id": "qwen3:4b",      "params": "4B",   "size_gb": 2.4,  "vram_gb": 3.2,
     "zh": 5, "family": "Qwen", "recommend": True,
     "note": "純聊天首選 —— 中文語感最好、指令跟得住，6GB 卡還留得下餘裕給 Unity。"},
    {"id": "qwen3:1.7b",    "params": "1.7B", "size_gb": 1.1,  "vram_gb": 1.7,
     "zh": 4, "family": "Qwen", "recommend": True,
     "note": "極省。酒保那種短句綽綽有餘，載入快、留給 Unity 的顯存最多。"},
    {"id": "qwen3:0.6b",    "params": "0.6B", "size_gb": 0.5,  "vram_gb": 0.9,
     "zh": 3, "family": "Qwen", "recommend": False,
     "note": "最小。適合純罐頭句與極低配機器；語感會明顯變鈍。"},
    {"id": "qwen2.5:3b",    "params": "3B",   "size_gb": 1.9,  "vram_gb": 2.7,
     "zh": 4, "family": "Qwen", "recommend": False,
     "note": "上一代，穩定成熟；Qwen3 拉不到時的退路。"},
    {"id": "gemma3:4b",     "params": "4B",   "size_gb": 2.6,  "vram_gb": 3.4,
     "zh": 4, "family": "Gemma", "recommend": False,
     "note": "多語系穩、語氣自然；授權走 Gemma 條款（非 OSI），商用前先看。"},
    {"id": "phi4-mini",     "params": "3.8B", "size_gb": 2.3,  "vram_gb": 3.1,
     "zh": 2, "family": "Phi", "recommend": False,
     "note": "英文強、中文偏弱 —— 酒保講中文的話不推。MIT 授權。"},
    {"id": "llama3.2:3b",   "params": "3B",   "size_gb": 2.0,  "vram_gb": 2.8,
     "zh": 2, "family": "Llama", "recommend": False,
     "note": "中文是弱項；列在這裡是為了對照，不是為了用。"},

    # ── 6GB 放不下的（顯存夠再選；放不下時 ollama 會把層數丟給 CPU —— 不報錯，只是很慢）──
    {"id": "qwen3:8b",      "params": "8B",   "size_gb": 4.7,  "vram_gb": 6.2,
     "zh": 5, "family": "Qwen", "recommend": False,
     "note": "品質明顯高一階。6GB 卡剛好卡在邊界（Unity 也在吃）—— 8GB 起跳才穩。"},
    {"id": "qwen3:14b",     "params": "14B",  "size_gb": 9.0,  "vram_gb": 11.0,
     "zh": 5, "family": "Qwen", "recommend": False,
     "note": "12GB 卡的主力。聊天已經有明顯「懂梗」的差距。"},
    {"id": "qwen3:30b-a3b", "params": "30B-MoE", "size_gb": 18.0, "vram_gb": 20.0,
     "zh": 5, "family": "Qwen", "recommend": False,
     "note": "MoE：總參數大但每次只活化約 3B ⇒ **算得快、可是顯存照整包吃**。24GB 卡適用。"},
    {"id": "qwen3:32b",     "params": "32B",  "size_gb": 20.0, "vram_gb": 23.0,
     "zh": 5, "family": "Qwen", "recommend": False,
     "note": "24GB 卡的上限附近。純聊天用它是殺雞用牛刀，但中文最好。"},
    {"id": "gemma3:12b",    "params": "12B",  "size_gb": 8.1,  "vram_gb": 10.0,
     "zh": 4, "family": "Gemma", "recommend": False,
     "note": "Gemma 家的中量級；多語系穩。"},
    {"id": "gemma3:27b",    "params": "27B",  "size_gb": 17.0, "vram_gb": 19.5,
     "zh": 4, "family": "Gemma", "recommend": False,
     "note": "Gemma 家上限；24GB 卡適用。"},
    {"id": "llama3.1:8b",   "params": "8B",   "size_gb": 4.9,  "vram_gb": 6.4,
     "zh": 3, "family": "Llama", "recommend": False,
     "note": "英文生態最廣、工具鏈最多；中文普通。"},
    {"id": "mistral-small", "params": "24B",  "size_gb": 14.0, "vram_gb": 16.5,
     "zh": 3, "family": "Mistral", "recommend": False,
     "note": "Apache-2.0 的中大型；歐語系強，中文一般。"},
]

# 顯存門檻：低於它才算「這張 6GB 卡放得下」。
# ⚠ 這是**卡的容量**不是**可用量** —— Unity Editor 自己就吃 1–3GB，判準永遠是 nvidia-smi 的 free 欄。
VRAM_BUDGET_GB = 6.0


def _run(args, timeout=DEFAULT_TIMEOUT):
    """跑一次 ollama。回 (exit, stdout, stderr)；找不到執行檔回 (-1, '', 訊息)。"""
    exe, _ = ollama_exe()
    if not exe:
        return -1, "", ("找不到 ollama —— 請先安裝（管理頁有一鍵安裝鈕，或 https://ollama.com/download），"
                        "裝完重開 Unity Editor 讓 PATH 生效。")
    try:
        p = subprocess.run([exe] + args, capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=timeout)
        return p.returncode, p.stdout or "", p.stderr or ""
    except subprocess.TimeoutExpired:
        return -1, "", f"逾時（{timeout}s）—— 指令：ollama {' '.join(args)}"
    except Exception as e:
        return -1, "", f"執行失敗：{e}"


def installed_models() -> tuple[list, str]:
    """已安裝模型清單（`ollama list` 的解析結果）。回 (models, error)。

    ⚠ 這是「有沒有安裝」的**唯一**判準 —— 不去掃 ollama 的 blobs 目錄。
      磁碟上有檔 ≠ 它註冊得到，而兩者不一致時**兩邊都不會報錯**。
    """
    code, out, err = _run(["list"])
    if code != 0:
        return [], (err or out).strip()
    models = []
    for line in out.splitlines()[1:]:            # 第一行是表頭
        parts = line.split()
        if len(parts) >= 3:
            models.append({"id": parts[0], "size": " ".join(parts[2:4])})
    return models, ""


def op_status() -> dict:
    exe, on_path = ollama_exe()
    have = bool(exe)
    ver_code, ver_out, ver_err = _run(["--version"]) if have else (-1, "", "")
    models, list_err = installed_models() if have else ([], "")
    ps = op_ps() if have else {"loaded": []}
    # 服務活著 ≠ 執行檔存在：`ollama list` 會去打本機服務，打不到就是沒跑
    serving = have and not list_err
    return {
        "ollama_installed": have,
        "ollama_path": exe,
        "on_path": on_path,          # 找得到但不在 PATH ⇒ 這個 process 的環境是舊的
        "version": ver_out.strip() or ver_err.strip(),
        "service_reachable": serving,
        "installed_count": len(models),
        "installed": models,
        "loaded": ps.get("loaded", []),     # 現在佔著顯存的（`ollama ps`）—— 跟「已安裝」是兩件事
        "loaded_count": len(ps.get("loaded", [])),
        "error": list_err,
        "hint": ("" if serving else
                 ("ollama 未安裝 —— 用管理頁的「一鍵安裝」，或到 https://ollama.com/download。"
                  if not have else
                  "ollama 找得到但不在本行程的 PATH 上（剛裝完？）—— **重開 Unity Editor** 後再試。"
                  if not on_path else
                  "ollama 有裝但服務打不到 —— 開一個終端機跑 `ollama serve`（或確認背景服務在跑）。")),
    }


def op_list() -> dict:
    models, err = installed_models()
    have = {m["id"] for m in models}
    # tag 對帳刻意寬鬆：`qwen3:4b` 與 `qwen3:4b-instruct-q4_K_M` 視為同一顆的變體，
    # 否則使用者手動 pull 過變體時，這頁會說「未安裝」而磁碟上明明有。
    def is_installed(mid: str) -> bool:
        return any(h == mid or h.startswith(mid.split(":")[0] + ":") and mid.split(":")[-1] in h
                   for h in have)

    catalog = []
    for m in CATALOG:
        c = dict(m)
        c["installed"] = mid_installed = (m["id"] in have) or is_installed(m["id"])
        c["exact"] = m["id"] in have          # 精確命中 vs 變體命中，UI 要分得出來
        c["fits_budget"] = m["vram_gb"] <= VRAM_BUDGET_GB   # 預設清單只列這些
        catalog.append(c)
    extra = [m for m in models if not any(c["id"] == m["id"] for c in catalog)]
    return {"catalog": catalog, "installed": models, "not_in_catalog": extra,
            "vram_budget_gb": VRAM_BUDGET_GB, "error": err}


def op_install(model: str) -> dict:
    t0 = time.time()
    code, out, err = _run(["pull", model], timeout=INSTALL_TIMEOUT)
    return {"ok": code == 0, "model": model, "seconds": round(time.time() - t0, 1),
            "stdout": out.strip()[-2000:], "error": ("" if code == 0 else (err or out).strip())}


# 區塊職責：安裝 ollama 本體（Windows：官方 PowerShell 安裝腳本）。
# 物理意義：`irm https://ollama.com/install.ps1 | iex` —— 這是**下載並執行遠端腳本**，
#          等於把安裝過程完全託付給那個網址當下的內容。
#          ⇒ 所以：① 只用官方網域、② 指令原文攤在 UI 上給人看過才按、③ 不做靜默背景安裝。
#          （實測 2026-08-19：該網址 307 轉向 github.com/ollama/ollama/releases/latest/download/install.ps1，
#            落點是官方 repo 的 release asset，22,627 bytes。）
# 數值影響：會安裝軟體、動 PATH。裝完**本行程的 PATH 仍是舊的** —— 要重開 Editor。
# ⚠ 非 Windows 平台不走這條（官方是 install.sh）—— 直接擋下並指路，不假裝支援。
def op_install_runtime(visible: bool = True) -> dict:
    if os.name != "nt":
        return {"ok": False, "error": "本 op 只支援 Windows；其他平台請走 https://ollama.com/download"}
    cmd = "irm https://ollama.com/install.ps1 | iex"
    args = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", cmd]
    try:
        if visible:
            # 開**看得見**的視窗：安裝過程可能要 UAC 或問話，藏起來會變成「按了沒反應」
            subprocess.Popen(args, creationflags=getattr(subprocess, "CREATE_NEW_CONSOLE", 0))
            return {"ok": True, "launched": True, "command": cmd,
                    "note": "已開啟 PowerShell 視窗執行安裝 —— 裝完**重開 Unity Editor**（PATH 才會更新），再按重新整理。"}
        p = subprocess.run(args, capture_output=True, text=True, encoding="utf-8",
                           errors="replace", timeout=INSTALL_TIMEOUT)
        return {"ok": p.returncode == 0, "command": cmd,
                "stdout": (p.stdout or "")[-2000:], "error": "" if p.returncode == 0 else (p.stderr or "")[-2000:]}
    except Exception as e:
        return {"ok": False, "command": cmd, "error": f"啟動失敗：{e}"}


def op_uninstall(model: str) -> dict:
    code, out, err = _run(["rm", model])
    return {"ok": code == 0, "model": model,
            "stdout": out.strip(), "error": ("" if code == 0 else (err or out).strip())}


# 區塊職責：查「現在有什麼模型被載入顯存、佔多少」（`ollama ps`）。
# 物理意義：`ollama list` 是**磁碟上有什麼**，`ollama ps` 是**顯存裡有什麼** —— 兩件不同的事。
#          卡住／變慢的現場需要的是後者：看得到「佔了 5.5GB、而且是 CPU/GPU 混合」才知道發生什麼事。
def op_ps() -> dict:
    code, out, err = _run(["ps"])
    if code != 0:
        return {"ok": False, "loaded": [], "error": (err or out).strip()}
    loaded = []
    for line in out.splitlines()[1:]:
        parts = line.split()
        if len(parts) >= 4:
            # NAME ID SIZE PROCESSOR UNTIL —— PROCESSOR 是「100% GPU」還是「x% CPU」的關鍵欄
            loaded.append({"id": parts[0], "size": " ".join(parts[2:4]),
                           "processor": " ".join(parts[4:6]) if len(parts) >= 6 else ""})
    return {"ok": True, "loaded": loaded, "raw": out.strip(), "error": ""}


# 區塊職責：把模型從顯存卸下（`ollama stop`）。
# 物理意義：這是「中斷」真正該做的事 —— 殺掉發問的那個 process **不會**讓模型離開顯存，
#          它是 ollama 服務持有的。⇒ 卡住時要停的是**模型**，不是我們這支 python。
# ⚠ 舊版 ollama 沒有 `stop` 子命令；失敗時原樣回報它的錯誤，不假裝成功。
def op_stop(model: str) -> dict:
    code, out, err = _run(["stop", model])
    return {"ok": code == 0, "model": model, "stdout": out.strip(),
            "error": ("" if code == 0 else (err or out).strip())}


# 區塊職責：試跑一句（走 HTTP API，不走 `ollama run`）。
# 物理意義：改走 API 是為了拿回三件 CLI 給不了的控制權：
#            ① **逾時**：連線層 timeout ⇒ 卡住有上限，不會無限等（CLI 版只能靠外層 kill，
#               而 kill 掉 CLI 也不會把模型從顯存放掉）
#            ② **生成上限** num_predict ⇒ 酒保只要短句，不讓它一路寫下去
#            ③ **關掉 thinking**：Qwen3 預設會先吐一大段思考再回答 ——
#               那正是「按了沒反應、其實還在跑」最常見的原因（think:false 舊版會忽略，無害）
# 數值影響：不改變模型內容；只影響這一次呼叫的等待上限與長度。
def op_test(model: str, prompt: str, timeout: int = TEST_TIMEOUT, think: bool = False,
            keep_alive: int = -1, num_predict: int = TEST_NUM_PREDICT, system: str = "") -> dict:
    """跑一句。回 {output, thinking, seconds, tokens_per_sec}。

    · `think=True` 時**把思考段一起要回來**（放在 `thinking` 欄）——
      🩸 2026-08-19 實測：qwen3:4b 就算 think=False，仍會把推理寫進 `response`
      （「首先，問題是…關鍵點：…」），於是在 CLI 下看起來像卡住。
      ⇒ 診斷時要看得到那一段，才知道「它在想」而不是「它死了」。
    · `keep_alive` 秒數隨請求送：用完多久把模型從顯存卸掉（-1＝不指定，用 ollama 預設 5 分鐘）。
    """
    body = {
        "model": model, "stream": False, "think": think,
        "options": {"num_predict": num_predict},
        "messages": ([{"role": "system", "content": system}] if system else [])
                    + [{"role": "user", "content": prompt}],
    }
    if keep_alive >= 0:
        body["keep_alive"] = f"{keep_alive}s"
    req = urllib.request.Request(f"{API_BASE}/api/chat", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            d = json.loads(r.read().decode("utf-8", "replace"))
        m = d.get("message") or {}
        sec = round(time.time() - t0, 1)
        content = (m.get("content") or "").strip()
        thinking = (m.get("thinking") or "").strip()
        n = d.get("eval_count") or 0
        # 🩸 2026-08-19 實測（qwen3:4b）：思考段吃掉 3680 token 才收尾。
        #   上限不夠時 `content` 是空的、`thinking` 卻很長 —— 那**不是失敗也不是卡死，是被截斷**，
        #   而 ok=True + 空回答看起來就跟「模型壞了」一樣。⇒ 這裡把原因直接講出來。
        note = ""
        if not content and thinking and n >= num_predict:
            note = (f"⚠ 生成上限 {num_predict} token 用完，思考還沒結束 ⇒ 回答是空的（被截斷，不是失敗）。"
                    "提高上限，或換一顆不 thinking 的小模型（酒保短句實測 qwen3:0.6b 20 token 就收尾）。")
        return {"ok": True, "model": model, "seconds": sec, "prompt": prompt, "note": note,
                "output": content,
                "thinking": thinking,
                "eval_count": n,
                "tokens_per_sec": round((d.get("eval_count") or 0) /
                                        max((d.get("eval_duration") or 1) / 1e9, 1e-6), 1),
                "keep_alive": keep_alive, "error": ""}
    except urllib.error.URLError as e:
        return {"ok": False, "model": model, "seconds": round(time.time() - t0, 1),
                "output": "", "thinking": "",
                "error": f"連線失敗／逾時（{timeout}s）：{e.reason}　"
                         "⇒ 逾時不代表它死了，thinking 模型可能還在想；"
                         "用 op=ps 看它載在哪、用 op=stop 把它從顯存放掉。"}
    except Exception as e:
        return {"ok": False, "model": model, "seconds": round(time.time() - t0, 1),
                "output": "", "thinking": "", "error": f"試跑失敗：{e}"}


def to_text(op: str, d: dict) -> str:
    if op == "status":
        lines = ["# 🤖 本地 LLM 狀態",
                 f"- ollama: {'✅ ' + d['version'] if d['ollama_installed'] else '❌ 未安裝'}",
                 f"- 執行檔: {d['ollama_path'] or '(找不到)'}",
                 f"- 服務: {'✅ 可連線' if d['service_reachable'] else '❌ 打不到'}",
                 f"- 已安裝模型: {d['installed_count']} 個"]
        for m in d["installed"]:
            lines.append(f"    · {m['id']}　{m['size']}")
        lines.append(f"- 載入顯存中: {d.get('loaded_count', 0)} 個")
        for m in d.get("loaded", []):
            lines.append(f"    · {m['id']}　{m['size']}　{m.get('processor', '')}")
        if d["hint"]:
            lines.append(f"- ⚠ {d['hint']}")
        return "\n".join(lines)
    if op == "list":
        lines = ["# 📚 模型目錄（★＝推薦）"]
        for c in d["catalog"]:
            mark = "✅" if c["installed"] else "　"
            star = "★" if c["recommend"] else " "
            fit = "  " if c["fits_budget"] else " ⚠6GB放不下"
            lines.append(f"{mark}{star} {c['id']:<16} {c['params']:<8} "
                         f"下載{c['size_gb']}GB 顯存~{c['vram_gb']}GB 中文{c['zh']}/5{fit}")
            lines.append(f"      {c['note']}")
        if d["not_in_catalog"]:
            lines.append("— 目錄外（你自己 pull 的）—")
            for m in d["not_in_catalog"]:
                lines.append(f"    · {m['id']}　{m['size']}")
        if d["error"]:
            lines.append(f"⚠ {d['error']}")
        return "\n".join(lines)
    if op == "test":
        lines = [f"# ▶ 試跑 {d.get('model', '')}",
                 f"- 結果: {'✅ 成功' if d.get('ok') else '❌ 失敗'}　耗時 {d.get('seconds')}s"
                 f"　{d.get('tokens_per_sec', 0)} tok/s"]
        if d.get("thinking"):
            lines += ["", "## 🧠 思考過程（thinking）", d["thinking"]]
        if d.get("output"):
            lines += ["", "## 💬 回答", d["output"]]
        if d.get("note"):
            lines += ["", d["note"]]
        if d.get("error"):
            lines += ["", f"⚠ {d['error']}"]
        return "\n".join(lines)
    return json.dumps(d, ensure_ascii=False, indent=1)


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(description="本地 LLM 模型管理（ollama 薄層）")
    ap.add_argument("op", choices=["status", "list", "install", "uninstall", "test",
                                   "install-runtime", "ps", "stop"])
    ap.add_argument("--timeout", type=int, default=TEST_TIMEOUT, help="test：等待上限（秒）")
    ap.add_argument("--think", action="store_true", help="test：把思考段一起要回來（診斷 thinking 模型用）")
    ap.add_argument("--keep-alive", type=int, default=-1, dest="keep_alive",
                    help="test：用完幾秒後把模型從顯存卸載（-1＝用 ollama 預設）")
    ap.add_argument("--num-predict", type=int, default=TEST_NUM_PREDICT, dest="num_predict",
                    help="test：生成上限（token）")
    ap.add_argument("--system", default="", help="test：system prompt（酒保人設）")
    ap.add_argument("--hidden", action="store_true",
                    help="install-runtime：不開視窗、等它跑完（預設開視窗，因為可能要 UAC）")
    ap.add_argument("--model", default="")
    ap.add_argument("--prompt", default=TEST_PROMPT)
    ap.add_argument("--format", choices=["json", "text"], default="text")
    a = ap.parse_args()

    if a.op in ("install", "uninstall", "test", "stop") and not a.model:
        print(json.dumps({"ok": False, "error": f"{a.op} 需要 --model"}, ensure_ascii=False))
        return 2

    if a.op == "install-runtime": d = op_install_runtime(visible=not a.hidden)
    elif a.op == "status":    d = op_status()
    elif a.op == "list":      d = op_list()
    elif a.op == "install":   d = op_install(a.model)
    elif a.op == "uninstall": d = op_uninstall(a.model)
    elif a.op == "ps":        d = op_ps()
    elif a.op == "stop":      d = op_stop(a.model)
    else:                     d = op_test(a.model, a.prompt, a.timeout, a.think,
                                          a.keep_alive, a.num_predict, a.system)

    print(json.dumps(d, ensure_ascii=False, indent=1) if a.format == "json" else to_text(a.op, d))
    # 失敗要用 exit code 說 —— 只把錯誤印在 stdout，呼叫端會把它當成正常輸出
    return 0 if d.get("ok", True) else 1


if __name__ == "__main__":
    raise SystemExit(main())
