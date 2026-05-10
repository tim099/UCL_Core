"""T-LVC-003 — Core fuzzy command resolver (TF-IDF + cosine).

Pipeline:
  input → normalize → substring_match → if hit, return conf=1.0
                                       → if miss, vectorize → cosine vs trigger matrix
                                       → multiply per-agent ema_weight
                                       → top-K filter by conf threshold
                                       → return ResolverResult

Backend: char-ngram TF-IDF (numpy only, no sklearn dep)
"""
from __future__ import annotations
import json
import math
import os
import re
import sys
import time
from dataclasses import dataclass, field, asdict
from typing import Dict, List, Tuple, Optional, Any

import numpy as np

from normalize import normalize, load_synonyms

# 區塊職責：路徑 default
_HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_TRIGGERS = os.path.join(_HERE, "triggers.json")
DEFAULT_WEIGHTS = os.path.join(_HERE, "weights.json")
DEFAULT_VECTORS = os.path.join(_HERE, "trigger_vectors.npz")  # sparse-friendly
DEFAULT_VOCAB = os.path.join(_HERE, "vocab.json")

# 區塊職責：直覺參數定錨（per Plan §0）
NGRAM_MIN = 2
NGRAM_MAX = 3
VOCAB_CAP = 8192
CONF_AUTO = 0.85          # >= 自動 pick
CONF_PROMPT = 0.60        # 0.60~0.85 → Y/N
TOP_K = 3
EMA_ALPHA = 0.10
HIT_REWARD = 1.0
MISS_PENALTY = -0.5


# ─────────────────────────────────────────────────────────────────
# Data classes
# ─────────────────────────────────────────────────────────────────

@dataclass
class Candidate:
    """單一匹配候選."""
    entry_id: str
    display_name: str
    workflow_path: str
    conf: float                        # 0.0 ~ 1.0+
    raw_cosine: float                  # 未乘 weight 的 cosine
    weight_applied: float              # ema weight 應用值


@dataclass
class ResolverResult:
    """resolver.resolve() 回傳結構."""
    input: str
    normalized: str
    mode: str                           # substring_exact | vector_fuzzy | no_match
    candidates: List[Candidate] = field(default_factory=list)
    elapsed_ms: float = 0.0
    suggested_action: str = ""          # auto_pick | prompt_yn | silent_fallback


# ─────────────────────────────────────────────────────────────────
# TF-IDF (hand-rolled, numpy only)
# ─────────────────────────────────────────────────────────────────

def _char_ngrams(text: str, n_min: int = NGRAM_MIN, n_max: int = NGRAM_MAX) -> List[str]:
    """抽 char-ngram (n_min ~ n_max).

    區塊職責：對 normalized 字串切 sliding window
    物理意義：'進酒館' (n=2) → ['進酒', '酒館']
              'token' (n=3) → ['tok','oke','ken']
    數值影響：CJK 字元各佔 1 grid，跟英文等寬處理
    """
    grams: List[str] = []
    L = len(text)
    for n in range(n_min, n_max + 1):
        if L < n:
            continue
        for i in range(L - n + 1):
            grams.append(text[i : i + n])
    return grams


def _build_vocab_and_df(
    docs_grams: List[List[str]], cap: int = VOCAB_CAP
) -> Tuple[Dict[str, int], np.ndarray]:
    """建 vocab (gram → idx) + document frequency 表.

    Returns:
      vocab: {gram: idx}
      df: np.array shape (V,) — 出現該 gram 的 doc 數
    """
    df_counter: Dict[str, int] = {}
    for grams in docs_grams:
        seen = set(grams)
        for g in seen:
            df_counter[g] = df_counter.get(g, 0) + 1

    # 按 df 排序取 top-cap（高 df 的 gram 比較通用）
    sorted_grams = sorted(df_counter.items(), key=lambda kv: kv[1], reverse=True)
    if len(sorted_grams) > cap:
        sorted_grams = sorted_grams[:cap]

    vocab = {g: i for i, (g, _) in enumerate(sorted_grams)}
    df = np.zeros(len(vocab), dtype=np.float32)
    for g, i in vocab.items():
        df[i] = df_counter[g]
    return vocab, df


def _vectorize(
    text: str, vocab: Dict[str, int], idf: np.ndarray
) -> np.ndarray:
    """把單一字串轉成 TF-IDF 向量 (dense, shape (V,)).

    Args:
      text: normalized 字串
      vocab: gram → idx
      idf: shape (V,) — log((N+1)/(df+1)) + 1

    Returns:
      L2-normalized vector shape (V,)
    """
    V = len(vocab)
    vec = np.zeros(V, dtype=np.float32)
    grams = _char_ngrams(text)
    if not grams:
        return vec
    for g in grams:
        idx = vocab.get(g)
        if idx is not None:
            vec[idx] += 1.0
    # tf-idf
    vec = vec * idf
    # L2 normalize
    norm = np.linalg.norm(vec)
    if norm > 0:
        vec = vec / norm
    return vec


def _build_trigger_matrix(
    triggers: List[Dict[str, Any]]
) -> Tuple[Dict[str, int], np.ndarray, np.ndarray, List[Tuple[int, int]]]:
    """為所有 trigger phrase 建 vector matrix.

    Returns:
      vocab
      idf: shape (V,)
      matrix: shape (P, V) — P = total phrase count，每行 L2-normalized
      phrase_to_entry: list of (entry_idx, phrase_idx_within_entry) — phrase 反查 entry
    """
    # Step 1: collect 所有 normalized phrases
    docs_grams: List[List[str]] = []
    phrase_to_entry: List[Tuple[int, int]] = []
    for ei, entry in enumerate(triggers):
        for pi, ph in enumerate(entry.get("phrases", [])):
            n = normalize(ph, synonyms={})  # 不替換同義詞 — phrases 本身視為 ground truth
            grams = _char_ngrams(n)
            docs_grams.append(grams)
            phrase_to_entry.append((ei, pi))

    if not docs_grams:
        # Empty corpus — return zero matrix
        return {}, np.zeros(0, dtype=np.float32), np.zeros((0, 0), dtype=np.float32), []

    # Step 2: build vocab + df
    vocab, df = _build_vocab_and_df(docs_grams, cap=VOCAB_CAP)
    if not vocab:
        return {}, np.zeros(0, dtype=np.float32), np.zeros((0, 0), dtype=np.float32), []

    # Step 3: idf = log((N+1)/(df+1)) + 1 (smoothed sklearn-style)
    N = len(docs_grams)
    idf = np.log((N + 1.0) / (df + 1.0)) + 1.0

    # Step 4: vectorize each doc
    P = len(docs_grams)
    V = len(vocab)
    matrix = np.zeros((P, V), dtype=np.float32)
    for i, grams in enumerate(docs_grams):
        # tf
        for g in grams:
            idx = vocab.get(g)
            if idx is not None:
                matrix[i, idx] += 1.0
        # tf-idf
        matrix[i] = matrix[i] * idf
        # L2 normalize
        norm = np.linalg.norm(matrix[i])
        if norm > 0:
            matrix[i] = matrix[i] / norm

    return vocab, idf, matrix, phrase_to_entry


# ─────────────────────────────────────────────────────────────────
# Resolver class
# ─────────────────────────────────────────────────────────────────

class Resolver:
    """主 resolver — 包 substring + vector fuzzy + weight learning."""

    def __init__(
        self,
        triggers_path: str = DEFAULT_TRIGGERS,
        weights_path: str = DEFAULT_WEIGHTS,
    ):
        self.triggers_path = triggers_path
        self.weights_path = weights_path
        self._load_triggers()
        self._load_weights()
        self._build_vectors()

    def _load_triggers(self) -> None:
        if not os.path.exists(self.triggers_path):
            raise FileNotFoundError(f"triggers.json not found: {self.triggers_path}")
        with open(self.triggers_path, "r", encoding="utf-8") as f:
            data = json.load(f)
        self.triggers: List[Dict[str, Any]] = data.get("triggers", [])

    def _load_weights(self) -> None:
        if os.path.exists(self.weights_path):
            with open(self.weights_path, "r", encoding="utf-8") as f:
                self.weights_data = json.load(f)
        else:
            self.weights_data = {"_schema_version": 1, "agents": {}}

    def _build_vectors(self) -> None:
        vocab, idf, matrix, p2e = _build_trigger_matrix(self.triggers)
        self.vocab = vocab
        self.idf = idf
        self.matrix = matrix
        self.phrase_to_entry = p2e

    def _get_ema_weight(self, agent_id: str, entry_id: str) -> float:
        """讀 per-agent per-entry EMA weight，預設 1.0."""
        agent = self.weights_data.get("agents", {}).get(agent_id, {})
        return float(agent.get(entry_id, {}).get("ema_weight", 1.0))

    def _substring_match(self, normalized: str) -> Optional[Dict[str, Any]]:
        """既有 CommandTable 行為：substring 任一命中即返."""
        for entry in self.triggers:
            for ph in entry.get("phrases", []):
                ph_n = normalize(ph, synonyms={})
                if ph_n and ph_n in normalized:
                    return entry
        return None

    def resolve(
        self,
        input_text: str,
        agent_id: str = "claude-da-xiaojie",
        top_k: int = TOP_K,
    ) -> ResolverResult:
        """主 entry — 跑完整 pipeline."""
        t0 = time.time()
        normalized = normalize(input_text)

        # Phase 1: substring exact
        hit = self._substring_match(normalized)
        if hit is not None:
            cand = Candidate(
                entry_id=hit["entry_id"],
                display_name=hit.get("display_name", hit["entry_id"]),
                workflow_path=hit.get("workflow_path", ""),
                conf=1.0,
                raw_cosine=1.0,
                weight_applied=1.0,
            )
            return ResolverResult(
                input=input_text,
                normalized=normalized,
                mode="substring_exact",
                candidates=[cand],
                elapsed_ms=(time.time() - t0) * 1000.0,
                suggested_action="auto_pick",
            )

        # Phase 2: vector fuzzy
        if self.matrix.size == 0 or len(self.vocab) == 0:
            return ResolverResult(
                input=input_text,
                normalized=normalized,
                mode="no_match",
                candidates=[],
                elapsed_ms=(time.time() - t0) * 1000.0,
                suggested_action="silent_fallback",
            )

        q_vec = _vectorize(normalized, self.vocab, self.idf)
        if np.linalg.norm(q_vec) == 0:
            return ResolverResult(
                input=input_text,
                normalized=normalized,
                mode="no_match",
                candidates=[],
                elapsed_ms=(time.time() - t0) * 1000.0,
                suggested_action="silent_fallback",
            )

        # cosine = dot product (因都已 L2 normalized)
        sims = self.matrix @ q_vec  # shape (P,)

        # 把 phrase-level sim aggregate 成 entry-level — 取每 entry 的 max
        entry_max: Dict[int, Tuple[float, int]] = {}  # entry_idx → (max_cosine, phrase_idx)
        for p_idx, (e_idx, ph_idx) in enumerate(self.phrase_to_entry):
            cur = entry_max.get(e_idx, (-1.0, -1))
            if sims[p_idx] > cur[0]:
                entry_max[e_idx] = (float(sims[p_idx]), ph_idx)

        # 套用 ema weight
        scored: List[Tuple[float, float, float, int]] = []  # (final_conf, raw, weight, entry_idx)
        for e_idx, (raw, _) in entry_max.items():
            entry = self.triggers[e_idx]
            w = self._get_ema_weight(agent_id, entry["entry_id"])
            final = raw * w
            # cap final at 1.0 for display sanity (raw 也 cap)
            final = min(final, 1.0)
            scored.append((final, raw, w, e_idx))

        # 排序
        scored.sort(key=lambda x: x[0], reverse=True)

        # 取 top_k 且 conf >= CONF_PROMPT
        candidates: List[Candidate] = []
        for final, raw, w, e_idx in scored[:top_k]:
            if final < CONF_PROMPT:
                break
            entry = self.triggers[e_idx]
            candidates.append(Candidate(
                entry_id=entry["entry_id"],
                display_name=entry.get("display_name", entry["entry_id"]),
                workflow_path=entry.get("workflow_path", ""),
                conf=round(final, 4),
                raw_cosine=round(raw, 4),
                weight_applied=round(w, 4),
            ))

        elapsed_ms = (time.time() - t0) * 1000.0

        if not candidates:
            return ResolverResult(
                input=input_text,
                normalized=normalized,
                mode="no_match",
                candidates=[],
                elapsed_ms=elapsed_ms,
                suggested_action="silent_fallback",
            )

        # 決定 suggested_action
        top_conf = candidates[0].conf
        if top_conf >= CONF_AUTO:
            action = "auto_pick"
        else:
            action = "prompt_yn"

        return ResolverResult(
            input=input_text,
            normalized=normalized,
            mode="vector_fuzzy",
            candidates=candidates,
            elapsed_ms=elapsed_ms,
            suggested_action=action,
        )


# ─────────────────────────────────────────────────────────────────
# Output formatter
# ─────────────────────────────────────────────────────────────────

def format_markdown(result: ResolverResult) -> str:
    """把 ResolverResult 渲染成 _last_op.md 友善 markdown."""
    lines = ["# 🔍 Resolver Result\n"]
    lines.append(f"- input: `{result.input}`")
    lines.append(f"- normalized: `{result.normalized}`")
    lines.append(f"- mode: `{result.mode}`")
    lines.append(f"- elapsed_ms: {result.elapsed_ms:.1f}")
    lines.append(f"- suggested_action: `{result.suggested_action}`")
    lines.append("")

    if not result.candidates:
        lines.append("## (No candidate ≥ threshold)")
        lines.append("→ 走一般 prompt 處理，不打擾使用者")
        return "\n".join(lines)

    lines.append("## Candidates")
    lines.append("")
    lines.append("| Rank | entry_id | display | conf | raw_cos | weight | workflow |")
    lines.append("|---|---|---|---|---|---|---|")
    for i, c in enumerate(result.candidates, 1):
        lines.append(
            f"| {i} | `{c.entry_id}` | {c.display_name} | {c.conf:.3f} | "
            f"{c.raw_cosine:.3f} | {c.weight_applied:.3f} | `{c.workflow_path}` |"
        )
    lines.append("")

    if result.suggested_action == "auto_pick":
        c = result.candidates[0]
        lines.append(f"→ conf {c.conf:.2f} ≥ {CONF_AUTO} → **auto_pick**: 走 `{c.entry_id}` workflow")
    elif result.suggested_action == "prompt_yn":
        c = result.candidates[0]
        lines.append(
            f"→ conf {c.conf:.2f} 落在 [{CONF_PROMPT}, {CONF_AUTO}) → **prompt_yn**：")
        lines.append(
            f"   問使用者「您是指『{c.display_name}』嗎？(Y / 1-{len(result.candidates)} 改選 / N)」")

    return "\n".join(lines)


# ─────────────────────────────────────────────────────────────────
# CLI
# ─────────────────────────────────────────────────────────────────

def main(argv: Optional[List[str]] = None) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8")
        except Exception:
            pass

    args = argv or sys.argv[1:]
    if not args or "--help" in args or "-h" in args:
        print("Usage: resolver.py --input <text> [--agent <id>] [--top-k N] [--format md|json]")
        return 1

    inp = ""
    agent = "claude-da-xiaojie"
    top_k = TOP_K
    fmt = "md"
    i = 0
    while i < len(args):
        a = args[i]
        if a == "--input":
            inp = args[i + 1]; i += 2; continue
        if a == "--agent":
            agent = args[i + 1]; i += 2; continue
        if a == "--top-k":
            top_k = int(args[i + 1]); i += 2; continue
        if a == "--format":
            fmt = args[i + 1]; i += 2; continue
        i += 1

    if not inp:
        print("❌ --input required")
        return 1

    resolver = Resolver()
    result = resolver.resolve(inp, agent_id=agent, top_k=top_k)

    if fmt == "json":
        # dataclass → dict
        out = {
            "input": result.input,
            "normalized": result.normalized,
            "mode": result.mode,
            "elapsed_ms": result.elapsed_ms,
            "suggested_action": result.suggested_action,
            "candidates": [asdict(c) for c in result.candidates],
        }
        print(json.dumps(out, ensure_ascii=False, indent=2))
    else:
        print(format_markdown(result))
    return 0


if __name__ == "__main__":
    sys.exit(main())
