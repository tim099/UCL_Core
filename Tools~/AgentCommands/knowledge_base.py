#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
knowledge_base.py — Agent 知識庫 / 長期記憶向量檢索工具 (skeleton v0.1)

位置: <UCL_Core>/Tools~/AgentCommands/ — 與 run_cmd.py / awakening.py / library.py 同列的
      通用 agent 工具 (跨專案 submodule 共用)。index 快取則落在「主專案」的 AgentCommands/_vectors
      (走 data_root(), 專案資料不進共享 submodule)。

架構定位 (Zeta/summit 2026-07-23, per Tim 拍板):
  - 本 script = 知識庫的「唯一真相來源」: 真正算向量、建索引、跑檢索都在這裡。
  - Cmd_KnowledgeBase (C#) = 管理層自動化入口 (status/install/prefetch/reindex/search)，
    agent 與 AdminPage 共用同一條 code path。
  - UCL_KnowledgeBaseAdminPage (C#) = Cmd 之上的薄 UI。
  嵌入後端走 FlagEmbedding 的真 BAAI/bge-m3，但介面與後端解耦 — 換模型不動上層。

  熱路徑 (search / embed) 刻意留純 Python: agent 直接呼叫最短路徑，
  不走 Unity Cmd queue round-trip (保 <15ms、不綁 Editor 存活)。

ops:
  status   [--format json|text]                      環境 / 模型 / 索引狀態
  install  [--full]                                  pip 安裝依賴 (FlagEmbedding；--full 顯式加 torch)
  prefetch                                           下載並預熱 bge-m3 權重
  reindex  --target docs|lessons [--format ...]      掃描目標文件、切塊、建向量索引
  search   --query <text> [--target docs] [--topk N] [--format ...]   向量檢索 top-k
  embed    --text <text> [--format json|text]        單句嵌入測試 (維度 + 延遲)

依賴缺席時: 不 crash — status/reindex/search 回結構化「pending_deps」狀態，
           指引使用者先跑 install / prefetch。
"""
import argparse
import json
import os
import sys
import time
from pathlib import Path

# 嵌入模型 — 後端統一走 FlagEmbedding 的真 BAAI/bge-m3 (Tim 2026-07-23 拍板不用輕量替代)。
#   bge-m3 = dense(語意) + sparse(關鍵字) + colbert(multi-vector) 三合一；本 skeleton 先用 dense_vecs。
#   依賴: FlagEmbedding (+torch)，經 op=install 安裝 (走 AdminPage/腳本，跨機器可重現)。
#   model id 可經 --model / 環境變數 KB_EMBED_MODEL 覆蓋 (介面與模型解耦，但預設就是 bge-m3)。
DEFAULT_MODEL = "BAAI/bge-m3"
MODEL_NAME = os.environ.get("KB_EMBED_MODEL", DEFAULT_MODEL)  # main() 會依 --model 再覆寫


def install_hint() -> str:
    """知識庫未安裝時的可行動提示 (跨機器優先走 AdminPage，per Tim)。"""
    return (
        "⚠️ 知識庫尚未安裝（FlagEmbedding 後端缺席）。安裝方式（擇一）：\n"
        "  1.【推薦】Unity Editor → 控制台 →「🧠 知識庫管理」→ 按「📦 安裝 bge-m3 依賴」\n"
        "     （走腳本安裝，跨專案/機器可重現；裝完再按「⬇️ 預熱 bge-m3 權重」）\n"
        "  2. CLI:   python <UCL_Core>/Tools~/AgentCommands/knowledge_base.py install --full\n"
        "  3. Agent: run_cmd.py run KnowledgeBase --arg op=install --arg full=true"
    )


# ─────────────────────────────────────────────────────────────────────────
# 後端診斷 — 三態 + 真錯誤不摺疊 (2026-07-26 修「誤報」bug 家族)
#
# 區塊職責：判定嵌入後端「到底是沒裝、還是裝了但壞了」，並產出可直接照做的修法。
# 血教訓：舊版 _deps_status() / load_model() 用裸 except 把「任何 import 失敗」壓成
#        單一布林「沒裝」，於是 Recuva null-byte 污染（ValueError: source code string
#        cannot contain null bytes）被謊報成「未安裝，請按安裝鈕」→ 使用者照提示重裝
#        → pip 回 already satisfied rc=0 →「✅ 成功」→ status 又說沒裝 = 閉環死循環，
#        全程沒有一行顯示真因。
# 拍板依據（summit / gura 2026-07-26 拍磚）：摺疊狀態＝說謊的溫床 → 三態 + 帶原始
#        exception + 帶「精準且安全」的修復指令（絕不自動執行破壞性修復）。
# ─────────────────────────────────────────────────────────────────────────
TARGET_PKG = "FlagEmbedding"           # 直接依賴 — 使用者「要的那個」，缺它才叫「沒裝」
DEP_PKGS = ("FlagEmbedding", "torch")  # 診斷 / 指紋涵蓋範圍（torch 是 bge-m3 的地基）


def _pkg_version(name: str) -> str:
    """讀已安裝版本 — 走 dist-info metadata，不 import 該套件。

    物理意義：套件 .py 被 null byte 污染時 import 必炸，但 metadata 仍讀得到；
    所以壞檔情境下依然報得出「你裝的是 2.6.0+cu124」這種救命資訊（決定修法能不能安全）。
    """
    try:
        from importlib import metadata as im
        return im.version(name)
    except Exception:
        return ""


def _site_dirs():
    """所有可能放套件的路徑（site-packages / user-site / sys.path）去重後回傳。

    數值影響：同一套件在多個路徑各有一份 = 影子安裝，正是 MSIX 沙箱重定向的指紋
    （pip 寫進虛擬化 user-site、消費端讀真實路徑 → rc=0 卻 import 不到）。
    """
    dirs = []
    try:
        import site
        dirs += list(site.getsitepackages())
        u = site.getusersitepackages()
        dirs += [u] if isinstance(u, str) else list(u)
    except Exception:
        pass
    dirs += [p for p in sys.path if p and Path(p).is_dir()]
    seen, out = set(), []
    for d in dirs:
        try:
            rp = str(Path(d).resolve())
        except Exception:
            continue
        if rp not in seen:
            seen.add(rp)
            out.append(rp)
    return out


def find_pkg_dirs(name: str):
    """回所有 site 路徑下的同名套件目錄（>1 筆 = 重複/影子安裝，需人工裁決哪份是真的）。"""
    hits = []
    for d in _site_dirs():
        p = Path(d) / name
        if p.is_dir() and (p / "__init__.py").exists():
            hits.append(str(p))
    return hits


def scan_null_byte_files(names=DEP_PKGS + ("functorch",), max_files=12000, max_hits=25):
    """掃 .py 檔的 null byte 污染（Recuva sector 污染的指紋）。

    參數意義：max_files/max_hits 是成本上限 — 只在 state=broken 時呼叫，
    且掃到上限就截斷回報（truncated=True），避免壞環境下 status 變成分鐘級。
    回傳 (壞檔清單, 已掃檔數, 是否截斷)。
    """
    bad, scanned = [], 0
    for n in names:
        for base in find_pkg_dirs(n):
            for fp in Path(base).rglob("*.py"):
                if scanned >= max_files or len(bad) >= max_hits:
                    return bad, scanned, True
                scanned += 1
                try:
                    if b"\x00" in fp.read_bytes():
                        bad.append(str(fp))
                except Exception:
                    pass
    return bad, scanned, False


def probe_backend() -> dict:
    """import 層探測 — 回三態 {state, error, failed_module}，原始錯誤一律不吞。

    state 判定線（summit 拍板）：失敗的 module「是不是使用者直接要的那個」——
      - e.name 是 FlagEmbedding 本身 → missing（真沒裝，該裝）
      - e.name 是子依賴（torch 之類）→ broken（目標在、地基缺/壞，別叫人重裝目標）
      - 其他任何 exception（null byte 的 ValueError / DLL load 的 ImportError…）→ broken
    """
    try:
        import FlagEmbedding  # noqa: F401
        return {"state": "installed", "error": "", "failed_module": ""}
    except ModuleNotFoundError as e:
        name = (getattr(e, "name", "") or "").split(".")[0]
        state = "missing" if name == TARGET_PKG else "broken"
        return {"state": state, "error": f"{type(e).__name__}: {e}", "failed_module": name}
    except Exception as e:
        # 這裡才是舊版最致命的黑洞：null byte / DLL / SyntaxError 全被當成「沒裝」。
        return {"state": "broken", "error": f"{type(e).__name__}: {e}", "failed_module": ""}


def _torch_repair_cmd() -> str:
    """依「已裝的 torch variant」組出不會誤降級的重裝指令（gura 拍磚加值）。

    物理意義：cu124 這種 local tag 代表 CUDA 專版；裸 pip install --force-reinstall torch
    會抓到 PyPI 預設輪子（多為 CPU 版）→ 治病治成殘廢（07-23 血案：90 分鐘 reindex 白費）。
    故把 variant 原樣釘進指令，並補對應的 index-url。
    """
    ver = _pkg_version("torch")
    if not ver:
        return "pip install torch   # 目前讀不到已裝版本，安裝前請先確認要 CPU 版還是 CUDA 版"
    if "+" in ver:
        local = ver.split("+", 1)[1]          # e.g. cu124
        return (f"pip install --force-reinstall --no-cache-dir torch=={ver} "
                f"--index-url https://download.pytorch.org/whl/{local}")
    return f"pip install --force-reinstall --no-cache-dir torch=={ver}"


def repair_hint(diag: dict) -> str:
    """把診斷結果翻成「照著做就對」的修法 — 只給指令，永不自動執行。"""
    if diag.get("state") == "installed":
        return ""
    if diag.get("state") == "missing":
        return install_hint()

    lines = ["⚠️ 後端『已安裝但無法載入』（不是未安裝 — 按安裝鈕沒有用）。",
             f"   真實錯誤: {diag.get('error') or '(未取得)'}"]
    if diag.get("failed_module"):
        lines.append(f"   失敗的 module: {diag['failed_module']}（子依賴層，不是 {TARGET_PKG} 本體）")
    vers = {k: v for k, v in (diag.get("versions") or {}).items() if v}
    if vers:
        lines.append("   已裝版本: " + " / ".join(f"{k}=={v}" for k, v in vers.items()))
    for name, dirs in (diag.get("pkg_dirs") or {}).items():
        if len(dirs) > 1:
            lines.append(f"   ⚠ {name} 有 {len(dirs)} 份實體（影子/沙箱重定向嫌疑）: " + " | ".join(dirs))
    bad = diag.get("corrupt_files") or []
    if bad:
        lines.append(f"   🩸 偵測到 {len(bad)}{'+' if diag.get('corrupt_truncated') else ''} 個 null-byte 壞檔"
                     f"（Recuva sector 污染指紋，掃了 {diag.get('corrupt_scanned', 0)} 個 .py）:")
        for f in bad[:5]:
            lines.append(f"      - {f}")
        if len(bad) > 5:
            lines.append(f"      … 另 {len(bad) - 5} 個（完整清單見 --format json 的 corrupt_files）")
        lines.append("   修法（依序試，全部都不要用裸的 --force-reinstall）:")
        lines.append("     1. 只重抽壞掉的那幾檔（從乾淨備份 / 同版 wheel 解壓覆蓋），保住其餘環境")
        lines.append(f"     2. 真要重裝該套件時，用釘版本的安全指令:\n        {_torch_repair_cmd()}")
        lines.append("     3. 若上面顯示有多份實體 → 先 uninstall 掉沙箱/影子那份，別兩份並存")
    else:
        lines.append("   修法: 先照真實錯誤判層（DLL/CUDA 不合 → 修 torch；檔案損毀 → 重抽壞檔）。")
        lines.append(f"     torch 若要重裝，用釘版本指令避免誤降級:\n        {_torch_repair_cmd()}")
    return "\n".join(lines)


def diagnose_backend(deep: bool = True) -> dict:
    """完整診斷 = 三態探測 + 版本/實體路徑 + （壞掉時）壞檔掃描 + 修法。"""
    probe = probe_backend()
    diag = {
        "state": probe["state"],
        "error": probe["error"],
        "failed_module": probe["failed_module"],
        "target_pkg": TARGET_PKG,
        "versions": {n: _pkg_version(n) for n in DEP_PKGS},
        "pkg_dirs": {n: find_pkg_dirs(n) for n in DEP_PKGS},
        "corrupt_files": [],
        "corrupt_scanned": 0,
        "corrupt_truncated": False,
    }
    if deep and probe["state"] == "broken":
        bad, scanned, trunc = scan_null_byte_files()
        diag["corrupt_files"] = bad
        diag["corrupt_scanned"] = scanned
        diag["corrupt_truncated"] = trunc
    diag["hint"] = repair_hint(diag)
    return diag


# ─────────────────────────────────────────────────────────────────────────
# 安裝指紋 + 健康快取 — 「真探針」結果的失效鍵綁指紋而非時間（summit 拍板）
# 物理意義：時間型快取會 stale（又疊一層「外觀 OK」）；綁套件版本 + dist-info mtime，
#          任何重裝/污染都會改指紋 → 快取自動失效，stale 有界。
# ─────────────────────────────────────────────────────────────────────────
def install_fingerprint() -> str:
    import hashlib
    parts = [sys.executable, sys.version.split()[0]]
    for n in DEP_PKGS:
        ver = _pkg_version(n)
        stamp = 0
        try:
            from importlib import metadata as im
            d = im.distribution(n)
            base = getattr(d, "_path", None)
            if base is not None and Path(str(base)).exists():
                stamp = Path(str(base)).stat().st_mtime_ns
        except Exception:
            pass
        parts.append(f"{n}={ver or 'absent'}@{stamp}")
    return hashlib.sha256("|".join(parts).encode("utf-8")).hexdigest()[:16]


def health_path() -> Path:
    return vectors_dir() / "_kb_health.json"


def read_health() -> dict:
    try:
        p = health_path()
        if p.is_file():
            d = json.loads(p.read_text(encoding="utf-8"))
            return d if isinstance(d, dict) else {}
    except Exception:
        pass
    return {}


def write_health(d: dict):
    try:
        vectors_dir().mkdir(parents=True, exist_ok=True)
        _atomic_write_text(health_path(), json.dumps(d, ensure_ascii=False, indent=2))
    except Exception:
        pass


def smoke_probe(now: str = "") -> dict:
    """真煙霧測試 — 真的走一遍 model load + forward（embed 一個極短字串）。

    數值影響：import 過只證明「檔案能讀」，權重壞/CUDA 不合要到 forward 才炸（反向假 OK）。
    這裡花一次極小推論換「真的能查」的證據，結果寫進 _kb_health.json 供 status 快取。
    """
    fp = install_fingerprint()
    t0 = time.time()
    vecs, err = embed_texts(["ok"])
    rec = {
        "fingerprint": fp,
        "probe_ok": vecs is not None,
        "dim": len(vecs[0]) if vecs else 0,
        "probe_ms": round((time.time() - t0) * 1000, 1),
        "error": "" if vecs is not None else (err or "未知"),
        "model": MODEL_NAME,
        "verified_at": now or time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }
    write_health(rec)
    return rec


def cached_verification() -> dict:
    """回 {verified: bool, stale: bool, ...} — 指紋不符 / 無快取 → 一律當「未驗證」。"""
    rec = read_health()
    if not rec:
        return {"verified": False, "stale": False, "reason": "尚無探針紀錄", "record": {}}
    if rec.get("fingerprint") != install_fingerprint():
        return {"verified": False, "stale": True, "reason": "安裝指紋已變（重裝/污染）→ 舊探針作廢",
                "record": rec}
    return {"verified": bool(rec.get("probe_ok")), "stale": False,
            "reason": "" if rec.get("probe_ok") else (rec.get("error") or "上次探針失敗"),
            "record": rec}


# ─────────────────────────────────────────────────────────────────────────
# 路徑解析 — 對齊 _lib/ucl_paths.py 慣例 (.git 只認資料夾，跳過 submodule gitlink)
# 物理意義: 本 script 在 <UCL_Core>/Tools~/AgentCommands/ (submodule 內)。從這裡往上 walk，
#          .is_dir() 會跳過 UCL_Core 自己的 .git gitlink 檔，續行命中「主專案」的 .git 資料夾，
#          故 index 快取正確落在主專案 AgentCommands，不會誤寫進共享 submodule (2026-07-23 血教訓)。
# ─────────────────────────────────────────────────────────────────────────
_THIS = Path(__file__).resolve()


def _find_git_root(start: Path):
    p = start.resolve()
    while p != p.parent:
        if (p / ".git").is_dir():   # 只認 .git 資料夾，跳過 submodule 的 .git gitlink 檔
            return p
        p = p.parent
    return None


def repo_root() -> Path:
    env = os.environ.get("CLAUDE_PROJECT_DIR")
    if env and Path(env).is_dir():
        return Path(env).resolve()
    walked = _find_git_root(_THIS)
    if walked:
        return walked
    # fallback: .git walk 全失敗 (極罕見，e.g. 無 git 環境) → 再試 cwd walk，仍無則 UCL_Core 根
    return _find_git_root(Path.cwd()) or _THIS.parents[2]


def data_root() -> Path:
    """AgentCommands 資料根 — honors <repo>/.agentcommands_root.local pointer (C#/Python 共讀)。"""
    root = repo_root()
    pointer = root / ".agentcommands_root.local"
    try:
        if pointer.exists():
            content = pointer.read_text(encoding="utf-8").strip()
            if content and Path(content).is_absolute():
                return Path(content).resolve()
    except Exception:
        pass
    return (root / "AgentCommands").resolve()


def vectors_dir() -> Path:
    return data_root() / "_vectors"


def index_path(target: str) -> Path:
    return vectors_dir() / f"{target}_index.json"


def _atomic_write_text(dest: Path, text: str, encoding: str = "utf-8"):
    """先寫 .partial 暫存、成功才 os.replace 換上 — 永不半路輾掉 live 檔（summit 拍板）。

    物理意義：舊版直接對 live 索引 write_text()，寫一半失敗 / 內容退化就地生效，
    既有的好索引無從回頭（重建 = 90 分鐘）。原子換檔讓「失敗」等於「什麼都沒發生」。
    """
    tmp = dest.with_suffix(dest.suffix + ".partial")
    tmp.write_text(text, encoding=encoding)
    os.replace(str(tmp), str(dest))


# ─────────────────────────────────────────────────────────────────────────
# Target — 「要索引 / 檢索哪一個語料庫」的具名集合。
#   config-driven：定義在同目錄 kb_targets.json（加 target = 改該檔、零 code；
#   Python 與 C# AdminPage 同讀，消除雙邊漂移）。glob 前綴：
#     無      → repo 根（<repo>/…）
#     core:   → UCL_Core 根（submodule 掛載點無關）
#     data:   → AgentCommands 資料根（honors .agentcommands_root.local）
#   支援多 target 搜尋（--target docs,coredocs 或 --target all）。
# ─────────────────────────────────────────────────────────────────────────
def core_root() -> Path:
    # knowledge_base.py 在 <core>/Tools~/AgentCommands/ → parents[2] = <core> 根
    return _THIS.parents[2]


# 內建預設 — kb_targets.json 缺失/損毀時的 fallback（確保永不 crash）
_DEFAULT_TARGETS = {
    "docs": {"desc": "專案文檔 — <repo>/Docs/**/*.md", "kind": "markdown",
             "globs": ["Docs/**/*.md"]},
    "coredocs": {"desc": "UCL_Core 共享文檔 — <core>/Docs~/**/*.md", "kind": "markdown",
                 "globs": ["core:Docs~/**/*.md"]},
    "lessons": {"desc": "Agent 經驗庫 — AgentCommands/Lessons", "kind": "lessons",
                "globs": ["data:Lessons/**/*.jsonl", "data:Lessons/**/*.md"]},
}


def load_targets() -> dict:
    """讀 kb_targets.json 的 targets 區塊；缺失/壞檔 → 回內建預設（不 crash）。"""
    cfg_path = _THIS.parent / "kb_targets.json"
    try:
        if cfg_path.exists():
            data = json.loads(cfg_path.read_text(encoding="utf-8"))
            tgts = data.get("targets") if isinstance(data, dict) else None
            if isinstance(tgts, dict) and tgts:
                return tgts
    except Exception:
        pass
    return _DEFAULT_TARGETS


def target_defs() -> dict:
    """{name: desc} — 給 status / 錯誤訊息顯示用。"""
    return {k: v.get("desc", "") for k, v in load_targets().items()}


def valid_targets():
    return list(load_targets().keys())


def _glob_base(prefix_glob: str):
    """依前綴決定 base 根：core: → UCL_Core 根；data: → 資料根；其餘 → repo 根。"""
    if prefix_glob.startswith("core:"):
        return core_root(), prefix_glob[len("core:"):]
    if prefix_glob.startswith("data:"):
        return data_root(), prefix_glob[len("data:"):]
    return repo_root(), prefix_glob


def resolve_target_sources(target: str):
    cfg = load_targets().get(target)
    if not cfg:
        return {"kind": "unknown", "base": "", "files": []}
    files = []
    bases = []
    for g in cfg.get("globs", []):
        base, pat = _glob_base(g)
        bases.append(str(base))
        try:
            files += [str(p) for p in base.glob(pat) if p.is_file()]
        except Exception:
            pass
    return {"kind": cfg.get("kind", "markdown"),
            "base": bases[0] if bases else "",
            "files": sorted(set(files))}


def parse_targets(arg: str):
    """'all' → 全部合法 target；'a,b' → [a,b]；單一 → [a]（不驗合法性，交 caller）。"""
    if not arg:
        return []
    if arg.strip().lower() == "all":
        return valid_targets()
    return [t.strip() for t in arg.split(",") if t.strip()]


# ─────────────────────────────────────────────────────────────────────────
# 嵌入後端 (lazy) — FlagEmbedding 缺席時回 (None, install_hint)，不 crash
# ─────────────────────────────────────────────────────────────────────────
_MODEL = None


def load_model():
    global _MODEL
    if _MODEL is not None:
        return _MODEL, None
    # import 失敗一律先診斷再回訊息 — 「沒裝」與「裝了但壞了」給的修法完全不同。
    try:
        from FlagEmbedding import BGEM3FlagModel  # noqa
    except Exception:
        diag = diagnose_backend()
        return None, diag["hint"] or install_hint()
    try:
        _MODEL = BGEM3FlagModel(MODEL_NAME, use_fp16=True)  # 首次會自動下載 ~1.2GB 權重
        return _MODEL, None
    except Exception as e:
        return None, f"模型 '{MODEL_NAME}' 載入失敗 ({e}). 首次會下載權重；網路/磁碟/CUDA 問題可重試。"


def embed_texts(texts):
    """回 (vectors:list[list[float]] | None, err)。取 bge-m3 的 dense_vecs。"""
    model, err = load_model()
    if model is None:
        return None, err
    try:
        out = model.encode(list(texts), batch_size=8, max_length=1024)
        dense = out["dense_vecs"] if isinstance(out, dict) else out
        vecs = [list(map(float, v)) for v in dense]
        return vecs, None
    except Exception as e:
        return None, f"嵌入計算失敗: {e}"


def _deps_status():
    """回 (installed, version) — 保留舊介面（後相容），實作改走三態探測。

    注意：installed=False 已不等於「沒裝」（可能是壞了）。需要區分的呼叫端
    請改用 diagnose_backend()；本函式只回「現在能不能用」這個布林。
    版本改讀 dist-info（FlagEmbedding 沒有 __version__，舊版永遠回空字串）。
    """
    state = probe_backend()["state"]
    return state == "installed", _pkg_version(TARGET_PKG)


# ─────────────────────────────────────────────────────────────────────────
# 切塊 — 極簡: 按空行分段，長段再截斷 (skeleton；未來可換 sentence-aware)
# ─────────────────────────────────────────────────────────────────────────
def chunk_text(text: str, max_chars: int = 800):
    chunks = []
    for para in text.replace("\r\n", "\n").split("\n\n"):
        p = para.strip()
        if not p:
            continue
        while len(p) > max_chars:
            chunks.append(p[:max_chars])
            p = p[max_chars:]
        if p:
            chunks.append(p)
    return chunks


# ─────────────────────────────────────────────────────────────────────────
# ops
# ─────────────────────────────────────────────────────────────────────────
def op_status(args):
    """環境 / 後端 / 索引狀態 — 三態診斷 + 探針驗證狀態（欄位純加法，舊欄位語意不變）。

    設計權衡（相對 summit 的「每次跑極小真探針」做了一處讓步）：
      status 是最常被隨手呼叫的 op，若快取失效就同步載 1.2GB 權重會讓它變成十幾秒級。
      故此處只做 import 層探測 + 讀「指紋綁定」的探針快取；快取缺/失效時**誠實標記為未驗證**
      而不是假裝 OK，並指路 --probe / prefetch 去補。跑 --probe 才真的走一遍 model+forward。
    """
    diag = diagnose_backend()
    have_backend = diag["state"] == "installed"
    ver = _pkg_version(TARGET_PKG)

    # 探針驗證：--probe 現場跑真煙霧測試；否則讀指紋綁定的快取（stale 有界）
    if getattr(args, "probe", False) and have_backend:
        rec = smoke_probe(getattr(args, "_now", ""))
        verification = {"verified": bool(rec.get("probe_ok")), "stale": False,
                        "reason": "" if rec.get("probe_ok") else rec.get("error", ""),
                        "record": rec, "source": "fresh"}
    else:
        verification = {**cached_verification(), "source": "cache"}

    vdir = vectors_dir()
    indexes = {}
    if vdir.is_dir():
        for f in vdir.glob("*_index.json"):
            try:
                meta = json.loads(f.read_text(encoding="utf-8"))
                has_vec = bool(meta.get("has_vectors", False))
                indexes[f.stem.replace("_index", "")] = {
                    "chunks": len(meta.get("chunks", [])),
                    "built_at": meta.get("built_at", ""),
                    "model": meta.get("model", ""),
                    "has_vectors": has_vec,
                    # 誠實標籤：manifest-only 佔位檔「存在」但不可查，別讓它混進「已就緒」的印象
                    "manifest_only": bool(meta.get("manifest_only", not has_vec)),
                    "searchable": bool(meta.get("searchable", has_vec)),
                }
            except Exception as e:
                indexes[f.stem] = {"error": str(e)}
    result = {
        "ok": True,
        "python": sys.version.split()[0],
        "model_name": MODEL_NAME,
        "backend": "FlagEmbedding",
        "backend_installed": have_backend,          # 舊欄位：語意仍是「現在能不能用」
        "backend_version": ver,
        "state": diag["state"],                     # 新欄位：installed | missing | broken
        "diagnosis": diag,                          # 新欄位：真錯誤 / 版本 / 實體路徑 / 壞檔 / 修法
        "verified": verification["verified"],       # 新欄位：權重真的跑過 forward 才 True
        "verified_at": (verification.get("record") or {}).get("verified_at", ""),
        "verification": verification,
        "install_fingerprint": install_fingerprint(),
        "vectors_dir": str(vdir),
        "vectors_dir_exists": vdir.is_dir(),
        "available_targets": target_defs(),
        "indexes": indexes,
        "ready": have_backend,                      # 舊欄位保持原語意（後相容），細節看 verified
    }
    return result


def _verify_import_fresh():
    """另起乾淨 subprocess 驗 import — 回 (ok, detail)。

    區塊職責：驗「裝完真的能用」。
    為何一定要另起 process：本 process 的 import 快取已記住失敗結果，同 process 再 import
    會拿到快取而非真實狀態（自己驗自己＝假證）。順便回報實體路徑，抓沙箱重定向。
    """
    import subprocess
    code = (
        "import json,sys\n"
        "r={'ok':False}\n"
        "try:\n"
        "    import FlagEmbedding as F\n"
        "    r['ok']=True\n"
        "    r['file']=getattr(F,'__file__','')\n"
        "except BaseException as e:\n"
        "    r['error']='%s: %s'%(type(e).__name__,e)\n"
        "    r['name']=getattr(e,'name','') or ''\n"
        "print(json.dumps(r))\n"
    )
    try:
        proc = subprocess.run([sys.executable, "-c", code], capture_output=True, text=True,
                              encoding="utf-8", errors="replace", timeout=300)
        for line in reversed((proc.stdout or "").strip().splitlines()):
            try:
                d = json.loads(line)
                return bool(d.get("ok")), d
            except Exception:
                continue
        return False, {"error": f"驗證子行程無有效輸出 (rc={proc.returncode}): "
                                f"{(proc.stderr or '')[-400:]}"}
    except Exception as e:
        return False, {"error": f"驗證子行程失敗: {e}"}


def op_install(args):
    """pip 安裝 + 真 import 驗證 — rc=0 不等於裝好（2026-07-26 修死循環）。

    區塊職責：把「pip 說成功」跟「真的能用」拆成兩個獨立證據，兩者皆過才回 ok=True。
    血教訓：舊版 ok = returncode==0，於是
      (a) 套件在但檔案壞掉 → pip 回 already satisfied rc=0，一個 byte 都沒修，仍報 ✅；
      (b) 裝到沙箱/影子 site-packages → rc=0 但消費端 import 不到。
    配上 status 誤報「未安裝」就形成無出口死循環。
    刻意不自動 --force-reinstall（Tim 環境的 torch 是手動釘的 CUDA 版，自動重裝＝可能降級成
    CPU 版；summit/gura 一致拍板「只報告 + 給精準指令」）。
    """
    import subprocess
    # 統一裝 bge-m3 後端依賴 (FlagEmbedding 會連帶拉 torch/transformers)。--full 保留為顯式加 torch。
    pkgs = ["FlagEmbedding", "torch"] if args.full else ["FlagEmbedding"]
    try:
        proc = subprocess.run(
            [sys.executable, "-m", "pip", "install", "-U", *pkgs],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=1800,
        )
    except Exception as e:
        return {"ok": False, "packages": pkgs, "error": str(e), "verified": False}

    out = (proc.stdout or "")
    # pip 的「已滿足」路徑代表它什麼都沒動 — 配上驗證失敗就是「壞檔沒被修」的鐵證。
    already = "Requirement already satisfied" in out
    verify_ok, vdetail = _verify_import_fresh()

    r = {
        "ok": proc.returncode == 0 and verify_ok,     # 兩個證據都要：pip 成功 ✕ 真能 import
        "packages": pkgs,
        "returncode": proc.returncode,
        "pip_ok": proc.returncode == 0,
        "verified": verify_ok,                        # 新增欄位（純加法，不動舊語意）
        "verify_detail": vdetail,
        "already_satisfied": already,
        "stdout_tail": out[-1500:],
        "stderr_tail": (proc.stderr or "")[-1500:],
    }
    if not verify_ok:
        diag = diagnose_backend()
        r["state"] = diag["state"]
        r["diagnosis"] = diag
        notes = [f"pip rc={proc.returncode} 但**驗證 import 失敗** → 不算裝好。",
                 f"驗證錯誤: {vdetail.get('error', '(無)')}"]
        if already:
            notes.append("pip 回報 'Requirement already satisfied' = 它認為版本符合、"
                         "**完全沒動檔案**；若檔案已損毀，重跑 install 永遠無效（死循環根源）。")
        notes.append(diag["hint"])
        r["error"] = "\n".join(n for n in notes if n)
    return r


def op_prefetch(args):
    model, err = load_model()
    if model is None:
        return {"ok": False, "state": diagnose_backend()["state"], "error": err}
    # 觸發一次嵌入確保權重真的下載並可推論
    t0 = time.time()
    vecs, verr = embed_texts(["knowledge base prefetch warmup"])
    if vecs is None:
        return {"ok": False, "error": verr}
    # 這一輪已經真的走完 model load + forward → 順手把「已驗證」寫進健康檔，
    # 讓之後的 status 有指紋綁定的真證據可讀（自然流程 install → prefetch 就填好快取）。
    rec = smoke_probe(getattr(args, "_now", ""))
    return {
        "ok": True,
        "model": MODEL_NAME,
        "dim": len(vecs[0]),
        "warmup_ms": round((time.time() - t0) * 1000, 1),
        "verified": bool(rec.get("probe_ok")),
        "verified_at": rec.get("verified_at", ""),
    }


def op_reindex(args):
    """支援單一 / 逗號多選 / all — 逐 target 建索引，多筆時回 multi 聚合。"""
    targets = parse_targets(args.target)
    known = set(valid_targets())
    unknown = [t for t in targets if t not in known]
    if unknown:
        opts = "\n".join(f"  - {k}: {v}" for k, v in target_defs().items())
        return {"ok": False, "error": f"未知 target {unknown}。合法值（可逗號多選或 all）：\n{opts}"}
    if not targets:
        return {"ok": False, "error": "未指定 target"}
    results = [_reindex_one(t, args) for t in targets]
    if len(results) == 1:
        return results[0]
    return {"ok": all(r.get("ok") for r in results), "multi": True, "results": results}


def _reindex_one(target: str, args):
    src = resolve_target_sources(target)
    if src["kind"] == "unknown":
        opts = "\n".join(f"  - {k}: {v}" for k, v in target_defs().items())
        return {"ok": False, "error": f"未知 target='{target}'。目前僅支援以下合法值：\n{opts}"}

    # 掃描 + 切塊 (這段不依賴模型，永遠可跑 — 建立 manifest)
    chunks = []
    for fp in src["files"]:
        try:
            raw = Path(fp).read_text(encoding="utf-8", errors="replace")
        except Exception:
            continue
        for order, ct in enumerate(chunk_text(raw)):
            chunks.append({"id": f"{Path(fp).name}#{order}", "file": fp, "ord": order, "text": ct})

    # ── 既有索引保護 ──────────────────────────────────────────────────
    # 區塊職責：判斷「這次是首建、還是覆蓋一份已經有向量的 live 索引」。
    # 血教訓（2026-07-26 沙箱實測）：後端壞掉時舊版照樣印 ✅ 並把 has_vectors=True 的好索引
    # 輾成 manifest-only，重建成本 90 分鐘。鐵則（summit 拍板）：**後端不可用 + 已有完整索引
    # → abort，一個位元組都別動**；manifest-only 只在「首建、沒東西可失」時才是 feature。
    ip = index_path(target)
    existing_has_vectors = False
    if ip.is_file():
        try:
            existing_has_vectors = bool(json.loads(ip.read_text(encoding="utf-8")).get("has_vectors"))
        except Exception:
            existing_has_vectors = False   # 讀不動的舊檔不視為「有價值資產」，容許覆蓋重建

    diag = diagnose_backend()
    backend_ok = diag["state"] == "installed"

    if not backend_ok:
        if existing_has_vectors:
            return {
                "ok": False,
                "target": target,
                "protected": True,             # 新增欄位：既有索引被保護、未被覆寫
                "state": diag["state"],
                "status": "aborted_backend_unavailable",
                "has_vectors": True,
                "searchable": True,
                "index_path": str(ip),
                "error": ("後端不可用，為保護既有索引（含向量）已中止 reindex，未寫入任何位元組。\n"
                          + diag["hint"]),
            }
        # 首建且沒東西可失 → manifest-only 佔位（保留原設計意圖），但標籤誠實：不可查。
        has_vectors, embed_err, took_ms = False, None, 0.0
    else:
        # 後端可用 → 真的算向量。算失敗一律不落檔（既有索引因此也不會被動到）。
        has_vectors, embed_err, took_ms = False, None, 0.0
        if chunks:
            t0 = time.time()
            vecs, embed_err = embed_texts([c["text"] for c in chunks])
            took_ms = round((time.time() - t0) * 1000, 1)
            if vecs is not None:
                for c, v in zip(chunks, vecs):
                    c["vec"] = v
                has_vectors = True
            else:
                return {
                    "ok": False,                      # ok 與 status 對齊：失敗就是失敗
                    "target": target,
                    "protected": existing_has_vectors,
                    "state": diag["state"],
                    "status": "embed_failed",
                    "embed_ms": took_ms,
                    "index_path": str(ip),
                    "error": f"嵌入失敗，未寫入索引（既有索引保持原狀）: {embed_err}",
                }

    meta = {
        "target": target,
        "model": MODEL_NAME if has_vectors else "",
        "source_base": src["base"],
        "file_count": len(src["files"]),
        "chunks": chunks,
        "has_vectors": has_vectors,
        "manifest_only": not has_vectors,   # 誠實標籤（gura 拍磚）：別讓佔位檔跟真索引撞名
        "searchable": has_vectors,
        "built_at": args._now or "",
    }
    vectors_dir().mkdir(parents=True, exist_ok=True)
    # 原子換檔：寫 .partial → os.replace，寫一半失敗等於什麼都沒發生
    _atomic_write_text(ip, json.dumps(meta, ensure_ascii=False, indent=2))
    return {
        "ok": True,
        "target": target,
        "files": len(src["files"]),
        "chunks": len(chunks),
        "has_vectors": has_vectors,
        "manifest_only": not has_vectors,
        "searchable": has_vectors,
        "embed_ms": took_ms,
        "status": "ready" if has_vectors else "pending_deps",
        "note": None if has_vectors else (
            "首建 manifest（尚無向量，**不可檢索**）；後端就緒後重跑 reindex 才有向量。\n"
            + (diag["hint"] or "")),
        "index_path": str(ip),
    }


def _cosine(a, b):
    import math
    dot = sum(x * y for x, y in zip(a, b))
    na = math.sqrt(sum(x * x for x in a))
    nb = math.sqrt(sum(y * y for y in b))
    if na == 0 or nb == 0:
        return 0.0
    return dot / (na * nb)


def op_search(args):
    # 多 target：'all' / 'docs,coredocs' / 單一。同一模型同向量空間 → 分數可比、可合併。
    targets = parse_targets(args.target)
    known = set(valid_targets())
    if not targets:
        return {"ok": False, "error": "未指定 target"}
    unknown = [t for t in targets if t not in known]
    if unknown:
        opts = "\n".join(f"  - {k}: {v}" for k, v in target_defs().items())
        return {"ok": False, "error": f"未知 target {unknown}。合法值（可逗號多選或 all）：\n{opts}"}
    # 後端不可用 → 先給「分得清沒裝 vs 壞掉」的診斷 (不必等讀 index 才失敗)
    diag = diagnose_backend()
    if diag["state"] != "installed":
        return {"ok": False,
                "status": "not_installed" if diag["state"] == "missing" else "backend_broken",
                "state": diag["state"],
                "diagnosis": diag,
                "error": diag["hint"]}

    # 收集選定 targets 的所有含向量 chunks（各 chunk 記住來源 target）
    entries = []          # list of (target, chunk)
    missing, pending = [], []
    for t in targets:
        ip = index_path(t)
        if not ip.is_file():
            missing.append(t)
            continue
        try:
            meta = json.loads(ip.read_text(encoding="utf-8"))
        except Exception as e:
            return {"ok": False, "error": f"索引 '{t}' 讀取失敗: {e}"}
        if not meta.get("has_vectors"):
            pending.append(t)
            continue
        for c in meta.get("chunks", []):
            if c.get("vec"):
                entries.append((t, c))
    if not entries:
        detail = []
        if missing:
            detail.append(f"未建索引 {missing}（先 reindex）")
        if pending:
            detail.append(f"無向量(manifest-only) {pending}")
        return {"ok": False, "error": "；".join(detail) or "選定 target 無可用向量"}

    t0 = time.time()
    qvec, err = embed_texts([args.query])
    if qvec is None:
        return {"ok": False, "error": err}

    # numpy 向量化餘弦 — 取代純 Python 迴圈（16k chunks: ~1.5s → ~0.4s）
    import numpy as np
    matrix = np.asarray([c["vec"] for (_t, c) in entries], dtype=np.float32)
    q = np.asarray(qvec[0], dtype=np.float32)
    denom = np.linalg.norm(matrix, axis=1) * (np.linalg.norm(q) + 1e-9) + 1e-9
    sims = (matrix @ q) / denom
    k = max(1, args.topk)
    order = np.argsort(-sims)[:k]

    hits = []
    for i in order:
        idx = int(i)
        t, c = entries[idx]
        hits.append({"score": round(float(sims[idx]), 4), "target": t,
                     "id": c["id"], "file": c["file"], "preview": c["text"][:200]})
    return {
        "ok": True,
        "targets": targets,
        "query": args.query,
        "searched_chunks": len(entries),
        "latency_ms": round((time.time() - t0) * 1000, 1),
        "hits": hits,
        "note": (f"略過未建索引 {missing}" if missing else None),
    }


def op_targets(args):
    """列出所有可用 target（config-driven）— 供 C# AdminPage 動態建下拉，不寫死。"""
    return {"ok": True, "targets": list(target_defs().items())}


def op_embed(args):
    t0 = time.time()
    vecs, err = embed_texts([args.text])
    if vecs is None:
        return {"ok": False, "error": err}
    v = vecs[0]
    return {
        "ok": True,
        "text": args.text,
        "dim": len(v),
        "latency_ms": round((time.time() - t0) * 1000, 1),
        "head": [round(x, 4) for x in v[:5]],
    }


# ─────────────────────────────────────────────────────────────────────────
# 輸出 — json (機器/C# parse) 或 text (人類可讀 markdown)
# ─────────────────────────────────────────────────────────────────────────
def render_text(op: str, r: dict) -> str:
    # 多 target reindex → 逐筆展開（先於 ok 檢查，失敗細節不被吞）
    if r.get("multi"):
        return "\n".join(render_text(op, rr) for rr in r.get("results", []))
    if not r.get("ok", False):
        head = f"❌ {op} 失敗"
        if r.get("protected"):
            # 失敗但「什麼都沒壞」也要說清楚 — 使用者最怕的是不知道既有資產動沒動
            head = f"🛡️ {op} 已中止（既有索引受保護，未被覆寫）"
        return f"{head}: {r.get('error') or r.get('note') or r}"
    if op == "status":
        # 後端一行分三態說話 — 「壞了」絕不能再印成「未安裝」（誤報 bug 的源頭）
        state = r.get("state", "installed" if r["backend_installed"] else "missing")
        ver = r.get("backend_version") or "(版本未知)"
        backend_line = {
            "installed": f"✅ 已安裝 {ver}",
            "missing": "⚠️ 未安裝 (op=install)",
            "broken": "🩸 已安裝但無法載入 (不是未安裝 — 重裝沒用，見下方診斷)",
        }.get(state, f"? {state}")
        # 驗證一行：import 過 ≠ 權重能跑 forward，未驗證就老實說未驗證
        v = r.get("verification", {})
        if r.get("verified"):
            rec = v.get("record", {})
            verify_line = (f"✅ 已驗證（真探針 dim={rec.get('dim', '?')} / "
                           f"{r.get('verified_at', '')} / {v.get('source', '')}）")
        elif state != "installed":
            verify_line = "—（後端不可用，無從驗證）"
        elif v.get("stale"):
            verify_line = "⚠️ 舊探針已作廢（安裝指紋變了）→ 跑 status --probe 或 op=prefetch 重驗"
        else:
            verify_line = f"⚠️ 未驗證（{v.get('reason') or '尚無探針紀錄'}）→ 跑 status --probe 或 op=prefetch"
        lines = [
            "# 🧠 知識庫狀態",
            f"- Python: {r['python']}",
            f"- 嵌入模型: {r['model_name']}（後端 {r.get('backend', 'FlagEmbedding')}）",
            f"- 後端依賴: {backend_line}",
            f"- 權重驗證: {verify_line}",
            f"- 向量庫目錄: {r['vectors_dir']} ({'存在' if r['vectors_dir_exists'] else '未建置'})",
            f"- 就緒: {'✅' if r['ready'] else '⚠️ 尚未就緒'}",
        ]
        # 壞掉時把真因與修法直接攤在 status 上 — 不用另外跑指令才看得到真相
        if state != "installed" and (r.get("diagnosis") or {}).get("hint"):
            lines.append("- 診斷 / 修法:")
            lines += [f"    {ln}" for ln in r["diagnosis"]["hint"].splitlines()]
        lines.append("- 可用 target（僅這些；填其他值報未知）:")
        for k, v in r.get("available_targets", {}).items():
            lines.append(f"    - {k}: {v}")
        if r["indexes"]:
            lines.append("- 已建索引:")
            for k, iv in r["indexes"].items():
                tag = "" if iv.get("searchable", iv.get("has_vectors")) else " ⚠ manifest-only(不可檢索)"
                lines.append(f"    - {k}: {iv.get('chunks', '?')} chunks / vectors={iv.get('has_vectors')}"
                             f" / {iv.get('built_at', '')}{tag}")
        else:
            lines.append("- 已建索引: (尚無 — 跑 op=reindex)")
        return "\n".join(lines)
    if op == "reindex":
        flag = "" if r.get("searchable", r.get("has_vectors")) else " ⚠ manifest-only(不可檢索)"
        return (f"✅ reindex `{r['target']}`: {r['files']} 檔 → {r['chunks']} chunks / "
                f"status={r['status']}{flag}" + (f"\n   {r['note']}" if r.get('note') else ""))
    if op == "search":
        tgts = ",".join(r.get("targets", []))
        out = [f"🔍 `{r['query']}` @ [{tgts}] ({r['latency_ms']}ms, {r.get('searched_chunks', '?')} chunks)"]
        if r.get("note"):
            out.append(f"  ⚠ {r['note']}")
        for h in r["hits"]:
            out.append(f"  [{h['score']}] ({h.get('target', '?')}) {h['id']}\n     {h['preview']}")
        return "\n".join(out)
    if op == "targets":
        # 一行一個 "name\tdesc" — C# 讀行、split '\t' 取 [0] 建下拉
        return "\n".join(f"{k}\t{v}" for k, v in r["targets"])
    if op == "embed":
        return f"✅ embed dim={r['dim']} latency={r['latency_ms']}ms head={r['head']}"
    if op == "install":
        # 兩個證據分開印：pip 說成功 ✕ 真 import 驗過 — 只有兩者皆過才是 ✅
        return (f"✅ install {r['packages']} rc={r.get('returncode')} / 驗證 import: ✅\n"
                f"{(r.get('stdout_tail') or '')[-600:]}")
    if op == "prefetch":
        return (f"✅ prefetch {r['model']} dim={r['dim']} warmup={r['warmup_ms']}ms"
                f" / 已寫入探針紀錄 {r.get('verified_at', '')}")
    return json.dumps(r, ensure_ascii=False, indent=2)


def main():
    # Windows 下被 C# / 其他進程 redirect 時 stdout 預設 cp950，print emoji 會 UnicodeEncodeError。
    # 強制 UTF-8 (對齊專案「9 支 py 補 UTF-8」教訓)。
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass

    ap = argparse.ArgumentParser(description="Agent 知識庫向量檢索工具 (skeleton)")
    sub = ap.add_subparsers(dest="op", required=True)

    # --model 覆蓋嵌入模型 (所有 op 通用；空 = 用 env/預設)
    def _add_model(pp): pp.add_argument("--model", default="")
    # --probe: 現場跑真煙霧測試（載權重 + forward）而非讀快取 — 慢但是唯一的硬證據
    p = sub.add_parser("status"); p.add_argument("--format", default="text")
    p.add_argument("--probe", action="store_true", help="現場跑真探針驗證權重可推論（會載模型，較慢）")
    _add_model(p)
    p = sub.add_parser("install"); p.add_argument("--full", action="store_true"); p.add_argument("--format", default="text"); _add_model(p)
    p = sub.add_parser("prefetch"); p.add_argument("--format", default="text"); _add_model(p)
    _tgt_help = "語料庫 (可逗號多選或 all；合法: " + " / ".join(valid_targets()) + ")"
    p = sub.add_parser("reindex"); p.add_argument("--target", required=True, help=_tgt_help); p.add_argument("--format", default="text"); _add_model(p)
    p = sub.add_parser("search"); p.add_argument("--query", required=True); p.add_argument("--target", default="docs", help=_tgt_help); p.add_argument("--topk", type=int, default=5); p.add_argument("--format", default="text"); _add_model(p)
    p = sub.add_parser("embed"); p.add_argument("--text", required=True); p.add_argument("--format", default="text"); _add_model(p)
    p = sub.add_parser("targets"); p.add_argument("--format", default="text"); _add_model(p)

    args = ap.parse_args()
    args._now = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())

    # --model > 環境變數 KB_EMBED_MODEL > 預設；覆寫 module global 供 load_model 使用。
    global MODEL_NAME
    if getattr(args, "model", ""):
        MODEL_NAME = args.model

    handlers = {
        "status": op_status, "install": op_install, "prefetch": op_prefetch,
        "reindex": op_reindex, "search": op_search, "embed": op_embed,
        "targets": op_targets,
    }
    r = handlers[args.op](args)
    fmt = getattr(args, "format", "text")
    if fmt == "json":
        print(json.dumps(r, ensure_ascii=False, indent=2))
    else:
        print(render_text(args.op, r))
    sys.exit(0 if r.get("ok", False) else 1)


if __name__ == "__main__":
    main()
