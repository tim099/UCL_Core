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
# Target — 「要索引 / 檢索哪一個語料庫」的具名集合。單一真相源在此。
#   ⚠ 目前**只有** docs / lessons 兩個合法值；填其他值 → "unknown target" 錯誤。
#   要新增 target（例如 letters / booknotes）= 開發者在下方 resolve_target_sources()
#   加一個分支 + 在 TARGET_DEFS 補說明，非使用者可自由填的自由欄位。
# ─────────────────────────────────────────────────────────────────────────
TARGET_DEFS = {
    "docs": "專案文檔 — 掃 <repo>/Docs/**/*.md（UCL / 專案說明文件）",
    "lessons": "Agent 經驗庫 — 掃 AgentCommands/Lessons/*.jsonl + *.md（跨 agent 累積教訓）",
}


def valid_targets():
    return list(TARGET_DEFS.keys())


def resolve_target_sources(target: str):
    root = repo_root()
    dr = data_root()
    if target == "docs":
        base = root / "Docs"
        files = sorted(str(p) for p in base.rglob("*.md")) if base.is_dir() else []
        return {"kind": "markdown", "base": str(base), "files": files}
    if target == "lessons":
        # agent-lessons-log: jsonl 每行一筆 lesson
        candidates = [dr / "Lessons", dr / "lessons"]
        files = []
        for c in candidates:
            if c.is_dir():
                files += [str(p) for p in c.rglob("*.jsonl")]
                files += [str(p) for p in c.rglob("*.md")]
        return {"kind": "lessons", "base": str(candidates[0]), "files": sorted(files)}
    return {"kind": "unknown", "base": "", "files": []}


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
        "available_targets": TARGET_DEFS,
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
    target = args.target
    src = resolve_target_sources(target)
    if src["kind"] == "unknown":
        opts = "\n".join(f"  - {k}: {v}" for k, v in TARGET_DEFS.items())
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
    target = args.target
    # target 合法性 → 未知直接列合法值 (不要落到「尚無索引」誤導)
    if target not in TARGET_DEFS:
        opts = "\n".join(f"  - {k}: {v}" for k, v in TARGET_DEFS.items())
        return {"ok": False, "error": f"未知 target='{target}'。目前僅支援以下合法值：\n{opts}"}
    # 後端未裝 → 先給安裝提示 (不必等讀 index 才失敗)
    have_backend, _ = _deps_status()
    if not have_backend:
        return {"ok": False, "status": "not_installed", "error": install_hint()}
    ip = index_path(target)
    if not ip.is_file():
        return {"ok": False, "error": f"target='{target}' 尚無索引。先跑 op=reindex --target {target}。"}
    try:
        meta = json.loads(ip.read_text(encoding="utf-8"))
    except Exception as e:
        return {"ok": False, "error": f"索引讀取失敗: {e}"}
    if not meta.get("has_vectors"):
        return {"ok": False, "status": "pending_deps",
                "error": f"target='{target}' 索引無向量 (manifest-only)。裝好後端依賴後重跑 reindex。"}

    t0 = time.time()
    qvec, err = embed_texts([args.query])
    if qvec is None:
        return {"ok": False, "error": err}
    q = qvec[0]
    scored = []
    for c in meta.get("chunks", []):
        v = c.get("vec")
        if not v:
            continue
        scored.append((_cosine(q, v), c))
    scored.sort(key=lambda x: x[0], reverse=True)
    top = scored[: max(1, args.topk)]
    return {
        "ok": True,
        "target": target,
        "query": args.query,
        "latency_ms": round((time.time() - t0) * 1000, 1),
        "hits": [
            {"score": round(s, 4), "id": c["id"], "file": c["file"],
             "preview": c["text"][:200]}
            for s, c in top
        ],
    }


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
        out = [f"🔍 `{r['query']}` @ {r['target']} ({r['latency_ms']}ms)"]
        for h in r["hits"]:
            out.append(f"  [{h['score']}] {h['id']}\n     {h['preview']}")
        return "\n".join(out)
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
    _tgt_help = "語料庫 (僅 " + " / ".join(valid_targets()) + "；填其他值會報未知 target)"
    p = sub.add_parser("reindex"); p.add_argument("--target", required=True, choices=valid_targets(), help=_tgt_help); p.add_argument("--format", default="text"); _add_model(p)
    p = sub.add_parser("search"); p.add_argument("--query", required=True); p.add_argument("--target", default="docs", help=_tgt_help); p.add_argument("--topk", type=int, default=5); p.add_argument("--format", default="text"); _add_model(p)
    p = sub.add_parser("embed"); p.add_argument("--text", required=True); p.add_argument("--format", default="text"); _add_model(p)

    args = ap.parse_args()
    args._now = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())

    # --model > 環境變數 KB_EMBED_MODEL > 預設；覆寫 module global 供 load_model 使用。
    global MODEL_NAME
    if getattr(args, "model", ""):
        MODEL_NAME = args.model

    handlers = {
        "status": op_status, "install": op_install, "prefetch": op_prefetch,
        "reindex": op_reindex, "search": op_search, "embed": op_embed,
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
