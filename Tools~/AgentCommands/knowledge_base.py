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
    try:
        from FlagEmbedding import BGEM3FlagModel  # noqa
    except Exception:
        return None, install_hint()
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
    """回 (installed, version) — 後端 FlagEmbedding 是否就緒。"""
    installed = False
    ver = ""
    try:
        import FlagEmbedding  # noqa
        installed = True
        ver = getattr(FlagEmbedding, "__version__", "")
    except Exception:
        pass
    return installed, ver


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
    have_backend, ver = _deps_status()
    vdir = vectors_dir()
    indexes = {}
    if vdir.is_dir():
        for f in vdir.glob("*_index.json"):
            try:
                meta = json.loads(f.read_text(encoding="utf-8"))
                indexes[f.stem.replace("_index", "")] = {
                    "chunks": len(meta.get("chunks", [])),
                    "built_at": meta.get("built_at", ""),
                    "model": meta.get("model", ""),
                    "has_vectors": bool(meta.get("has_vectors", False)),
                }
            except Exception as e:
                indexes[f.stem] = {"error": str(e)}
    result = {
        "ok": True,
        "python": sys.version.split()[0],
        "model_name": MODEL_NAME,
        "backend": "FlagEmbedding",
        "backend_installed": have_backend,
        "backend_version": ver,
        "vectors_dir": str(vdir),
        "vectors_dir_exists": vdir.is_dir(),
        "available_targets": target_defs(),
        "indexes": indexes,
        "ready": have_backend,
    }
    return result


def op_install(args):
    import subprocess
    # 統一裝 bge-m3 後端依賴 (FlagEmbedding 會連帶拉 torch/transformers)。--full 保留為顯式加 torch。
    pkgs = ["FlagEmbedding", "torch"] if args.full else ["FlagEmbedding"]
    try:
        proc = subprocess.run(
            [sys.executable, "-m", "pip", "install", "-U", *pkgs],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=1800,
        )
        return {
            "ok": proc.returncode == 0,
            "packages": pkgs,
            "returncode": proc.returncode,
            "stdout_tail": (proc.stdout or "")[-1500:],
            "stderr_tail": (proc.stderr or "")[-1500:],
        }
    except Exception as e:
        return {"ok": False, "packages": pkgs, "error": str(e)}


def op_prefetch(args):
    model, err = load_model()
    if model is None:
        return {"ok": False, "error": err}
    # 觸發一次嵌入確保權重真的下載並可推論
    t0 = time.time()
    vecs, verr = embed_texts(["knowledge base prefetch warmup"])
    if vecs is None:
        return {"ok": False, "error": verr}
    return {
        "ok": True,
        "model": MODEL_NAME,
        "dim": len(vecs[0]),
        "warmup_ms": round((time.time() - t0) * 1000, 1),
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

    # 嵌入 (依賴後端；缺席則落 manifest-only，狀態 pending_deps)
    have_backend, _ = _deps_status()
    has_vectors = False
    embed_err = None
    took_ms = 0.0
    if have_backend and chunks:
        t0 = time.time()
        vecs, embed_err = embed_texts([c["text"] for c in chunks])
        if vecs is not None:
            for c, v in zip(chunks, vecs):
                c["vec"] = v
            has_vectors = True
        took_ms = round((time.time() - t0) * 1000, 1)

    meta = {
        "target": target,
        "model": MODEL_NAME if has_vectors else "",
        "source_base": src["base"],
        "file_count": len(src["files"]),
        "chunks": chunks,
        "has_vectors": has_vectors,
        "built_at": args._now or "",
    }
    vectors_dir().mkdir(parents=True, exist_ok=True)
    index_path(target).write_text(
        json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    return {
        "ok": True,
        "target": target,
        "files": len(src["files"]),
        "chunks": len(chunks),
        "has_vectors": has_vectors,
        "embed_ms": took_ms,
        "status": "ready" if has_vectors else "pending_deps",
        "note": None if has_vectors else (embed_err or "已建 manifest；裝好後端依賴後重跑 reindex 才有向量。"),
        "index_path": str(index_path(target)),
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
    # 後端未裝 → 先給安裝提示 (不必等讀 index 才失敗)
    have_backend, _ = _deps_status()
    if not have_backend:
        return {"ok": False, "status": "not_installed", "error": install_hint()}

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
        return f"❌ {op} 失敗: {r.get('error') or r.get('note') or r}"
    if op == "status":
        lines = [
            "# 🧠 知識庫狀態",
            f"- Python: {r['python']}",
            f"- 嵌入模型: {r['model_name']}（後端 {r.get('backend', 'FlagEmbedding')}）",
            f"- 後端依賴: {'✅ 已安裝 ' + r['backend_version'] if r['backend_installed'] else '⚠️ 未安裝 (op=install)'}",
            f"- 向量庫目錄: {r['vectors_dir']} ({'存在' if r['vectors_dir_exists'] else '未建置'})",
            f"- 就緒: {'✅' if r['ready'] else '⚠️ 尚未就緒'}",
        ]
        lines.append("- 可用 target（僅這些；填其他值報未知）:")
        for k, v in r.get("available_targets", {}).items():
            lines.append(f"    - {k}: {v}")
        if r["indexes"]:
            lines.append("- 已建索引:")
            for k, v in r["indexes"].items():
                lines.append(f"    - {k}: {v.get('chunks', '?')} chunks / vectors={v.get('has_vectors')} / {v.get('built_at', '')}")
        else:
            lines.append("- 已建索引: (尚無 — 跑 op=reindex)")
        return "\n".join(lines)
    if op == "reindex":
        return (f"✅ reindex `{r['target']}`: {r['files']} 檔 → {r['chunks']} chunks / "
                f"status={r['status']}" + (f"\n   {r['note']}" if r.get('note') else ""))
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
        return (f"{'✅' if r['ok'] else '❌'} install {r['packages']} rc={r.get('returncode')}\n"
                f"{r.get('stderr_tail') or r.get('stdout_tail') or ''}")
    if op == "prefetch":
        return f"✅ prefetch {r['model']} dim={r['dim']} warmup={r['warmup_ms']}ms"
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
    p = sub.add_parser("status"); p.add_argument("--format", default="text"); _add_model(p)
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
