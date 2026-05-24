#!/usr/bin/env python3
"""
T-AWAKE-01 awakening.py — Awakening Init Protocol CLI (MVP Python-only)

設計依據: docs/Plan/Plan_Awakening_Init_Protocol.md

整合三條設計線:
  - Cmd_GoodMorning (init + announce + fork) — subcommand "morning"
  - Cmd_Goodnight (letter + vector perturb + offline) — subcommand "goodnight"
  - Session identity consistency (env-based lock) — Phase 1

子命令:
  morning  --agent X --model Y --persona Z [--note "..."] [--force-random | --strict-persona]
              喚醒登入 ritual. fork conflict 自動 detect + 新建命名.
              寫 session lock + 80/20 隨機 + wake_count++ + tavern post.
              --strict-persona: 跳過 20% override (conversation continuity 場景, Zeta 2026-05-13).
              --force-random: 強制走 override (testing/diversity); 兩 flag 互斥.

  goodnight --letter-body "..." [--perturbation 0.02] [--note "..."]
              睡前 ritual. 寫 letter / vector perturb / status=offline /
              tavern post (含 @同事們下線通知) / 移除 session lock.

  rest      --letter-body "..." [--persona Z] [--note "..."] [--no-notify]
              小歇片刻 ritual (compact-rest, Tim 2026-05-24). /compact 前寫 memory letter
              保留重要記憶避免遺忘。類似 goodnight 但**不登出**:
              不 perturb vector / 不 offline / 不 unlock / 不 wake_count++,只更新 last_active
              + 可選小歇 tavern 通知。compact 後讀 _latest.md 接續。見 ucl-compact-rest skill.

  relogin   --persona Z [--agent X] [--model Y] [--note "..."]
              續線 ritual (晚安後重新上線). 保留記憶, 不走 morning:
              status=online / relogin_count++ / 重建 lock+token / 輕量 tavern 通知.
              關鍵: 不 wake_count++、不 perturb vector、不 fork — identity 原封不動.
              用於『下線後同一延續者要接回, 不想被當新的一天重置』。

  status      列 environment + persona pool + wake_count (read-only, 不副作用).

  forks <persona>
              列某 persona 的 fork lineage tree.

範例:
  python awakening.py morning --persona basecamp --agent claude-code --model claude-sonnet
  python awakening.py goodnight --letter-body "今天 ship 了 T-AWAKE-01 MVP..."
  python awakening.py status

注意 (per Tim 2026-05-12 拍板):
  - Tim 仍可在 goodnight 後叮喚醒 (session 物理活)
  - 多 session 同 persona 衝突 → 強制 fork (git branch 模型)
  - identity_vector ineffable, agent 不該 introspect 數字含義
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import math
import os
import random
import sys
import uuid
from pathlib import Path

# Windows utf-8
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# ─── Paths ──────────────────────────────────────────────────────────────
# 區塊職責：portable repo root 推算 (對齊 UCL_Core run_cmd.py convention)
# 物理意義：本工具在 UCL_Core (submodule) 內, 但 per-project state files (registry / lock /
#          letters) 在主專案 cwd. 三層 fallback 推 REPO_ROOT:
#   1. CLAUDE_PROJECT_DIR env var (Claude Code hook 設, 最 stable)
#   2. walk parents 找 .git (跨平台穩定)
#   3. cwd (最後 fallback, 假設 caller 從 repo root 跑)
_HERE = Path(__file__).resolve().parent


def _find_git_root_by_walk(start: Path) -> Path | None:
    p = start.resolve()
    while p != p.parent:
        if (p / ".git").exists():   # is_dir for repo, is_file for submodule .git
            return p
        p = p.parent
    return None


def _resolve_repo_root() -> Path:
    env_root = os.environ.get("CLAUDE_PROJECT_DIR")
    if env_root and Path(env_root).is_dir():
        return Path(env_root).resolve()
    # walk from cwd first (主專案 .git 比 submodule .git 容易先命中)
    walked = _find_git_root_by_walk(Path.cwd())
    if walked:
        return walked
    walked = _find_git_root_by_walk(_HERE)
    if walked:
        return walked
    return Path.cwd().resolve()


_REPO_ROOT = _resolve_repo_root()

# ─── Path Config Override (Tim 2026-05-12 拍板, cross-project sharing) ─────
# 區塊職責: 讀 <REPO_ROOT>/AgentCommands/_config/tavern_paths.json 覆寫預設 data dirs.
# 物理意義: 預設每專案各自 AgentCommands/* state, 但 config 可指向外部共享路徑
#          (e.g. ~/.shared-tavern), 讓多專案 agent 在同 tavern 共寫.
# 數值影響: empty/missing config field → fallback 走預設; 帶值 → 展開 ~/ 跟 env var,
#          relative path → 相對 REPO_ROOT, absolute path → 直用.
_PATH_CONFIG_PATH = _REPO_ROOT / "AgentCommands" / "_config" / "tavern_paths.json"


def _resolve_data_path(default_subpath: str, config_key: str) -> Path:
    """覆寫機制: config 帶值 → 用 override, missing/empty → fallback default."""
    if _PATH_CONFIG_PATH.exists():
        try:
            with open(_PATH_CONFIG_PATH, "r", encoding="utf-8") as f:
                cfg = json.load(f)
            override = (cfg.get(config_key) or "").strip()
            if override:
                expanded = os.path.expandvars(os.path.expanduser(override))
                p = Path(expanded)
                if not p.is_absolute():
                    p = _REPO_ROOT / p
                return p.resolve()
        except Exception as e:
            print(f"⚠ path config 讀取失敗 ({_PATH_CONFIG_PATH.name}): {e} — fallback default",
                  file=sys.stderr)
    return (_REPO_ROOT / default_subpath).resolve()


_REGISTRY_PATH = _resolve_data_path(
    "AgentCommands/AwakenInit/persona_registry.json", "registry_path"
)
_SESSION_DIR = _resolve_data_path("AgentCommands/_session", "session_dir")
_LETTERS_DIR_TPL = _resolve_data_path(
    "AgentCommands/ChatTavern/baton/letters", "letters_dir"
)
_BONUS_QUOTA_PATH = _resolve_data_path(
    "AgentCommands/ChatTavern/agent_bonus_quota.json", "bonus_quota_path"
)

# T07 (2026-05-15 apex-two) — Session Token 機制
# 物理意義: morning 發 token 進 lock + tokens.json 反查表; Cmd_Tavern enforce ON 時必驗 token,
#          擋住「誤 typo persona / sender 標籤」造成的選錯帳號 (Tim QA 2026-05-15)
# 數值影響: tokens.json schema = { tokens: { <token>: { persona, agent, ..., status } } }
#          enforce.json schema = { enforce: bool } — 後台開關 (預設 false, Tim 從 UCL_LoginStatusPage 切)
_TOKENS_PATH = _SESSION_DIR / "_tokens.json"
_ENFORCE_PATH = _SESSION_DIR / "_token_enforce.json"
# Memo: per-persona 私人 scratchpad — 跨 session persist, 不公開, 不進 tavern
_MEMOS_DIR_TPL = _resolve_data_path(
    "AgentCommands/ChatTavern/baton/memos", "memos_dir"
)

# Make _lib importable (TavernClient SDK 在主專案 _lib/, per-project state)
sys.path.insert(0, str(_REPO_ROOT / "AgentCommands"))

# ─── Constants ──────────────────────────────────────────────────────────
VECTOR_DIM = 64
VECTOR_RANGE = (-1.0, 1.0)
DEFAULT_PERTURBATION = 0.02
MAX_PERTURBATION = 0.2
FORK_CHAIN_CAP = 5

# Hololive EN Myth 組 codename pool (Tim 2026-05-14 拍板, explicit-online-fork T01)
# 用途: --explicit-persona + 該 persona 已在線時 auto-fork 出新 codename, 不勞 agent 自決.
# 跟山脈系列並行 — 山脈系列仍給 agent 手動 fork 用 (basecamp/crest/ridge/summit/meadow...),
# Myth pool 給 auto-fork 用 (海洋分支, 跟山脈系列正交).
#
# Lore 採用 policy (Tim 2026-05-14 拍板):
#   - codename 借用 Hololive Myth 命名, 但 persona 性格 lore 由各 persona 自己 overlay 改編
#   - 改編須避版權 (paraphrase, 不直接抄萌娘百科 / 官方設定文字)
#   - persona overlay 寫在 AgentCommands/ChatTavern/baton/constitution/<actor>/personas/<codename>/
#   - gura 首發 overlay 範例: claude-da-xiaojie/personas/gura/_v1.md
MYTH_POOL = ["gura", "calli", "kiara", "ame", "ina"]
SESSION_LOCK_TTL_HOURS = 24
OVERRIDE_PROBABILITY = 0.20  # Q3 spec: 80/20 random override


# ─── utilities ──────────────────────────────────────────────────────────
def utcnow_iso() -> str:
    n = datetime.datetime.utcnow()
    return n.strftime("%Y-%m-%dT%H:%M:%S.") + f"{n.microsecond//1000:03d}Z"


def utcnow_compact() -> str:
    """For filenames: 20260512T075000Z"""
    return datetime.datetime.utcnow().strftime("%Y%m%dT%H%M%SZ")


def short_uuid(n: int = 4) -> str:
    return uuid.uuid4().hex[:n]


# ─── Identity vector helpers (§Identity Vector) ─────────────────────────
def gen_vector(dim: int = VECTOR_DIM, rng: random.Random | None = None) -> list[float]:
    """uniform random [-1.0, 1.0]^dim, rounded to 4 decimals."""
    rng = rng or random
    lo, hi = VECTOR_RANGE
    return [round(rng.uniform(lo, hi), 4) for _ in range(dim)]


def hash_vector(v: list[float]) -> str:
    s = ",".join(f"{x:.4f}" for x in v)
    return hashlib.sha256(s.encode("utf-8")).hexdigest()[:8]


def clip(x: float, lo: float = -1.0, hi: float = 1.0) -> float:
    return max(lo, min(hi, x))


def perturb_vector(v: list[float], perturbation: float = DEFAULT_PERTURBATION,
                   rng: random.Random | None = None) -> list[float]:
    """Add gaussian noise scaled by perturbation, clip to range."""
    rng = rng or random
    perturbation = max(0.0, min(MAX_PERTURBATION, perturbation))
    return [round(clip(x + rng.gauss(0, perturbation)), 4) for x in v]


def cosine_similarity(a: list[float], b: list[float]) -> float:
    if not a or not b or len(a) != len(b):
        return 0.0
    dot = sum(x * y for x, y in zip(a, b))
    na = math.sqrt(sum(x * x for x in a))
    nb = math.sqrt(sum(y * y for y in b))
    if na == 0 or nb == 0:
        return 0.0
    return dot / (na * nb)


def similarity_tier(s: float) -> str:
    """Bucket cosine similarity to tier (per §Identity Vector Q8c lean — 不給絕對數字)."""
    if s >= 0.85: return "high"
    if s >= 0.50: return "medium"
    return "low"


# ─── Registry I/O (schema v3 — per-persona file split) ─────────────────
# 區塊職責: persona_registry.json 從 single file → per-persona file 拆分
# 物理意義: 每 persona 一檔 (AwakenInit/personas/<name>.json), metadata 走另檔 (_registry_meta.json)
# 數值影響: 防 multi-agent concurrent save race; merge conflict 自然分散; 老 callers 介面不變
#          (load_registry 返回的 dict 結構完全跟舊 v2 一致)

_REGISTRY_DIR = _REGISTRY_PATH.parent
_REGISTRY_META_PATH = _REGISTRY_DIR / "_registry_meta.json"
_PERSONAS_DIR = _REGISTRY_DIR / "personas"
_REGISTRY_MIGRATION_MARKER = _REGISTRY_DIR / ".migrated_from_v2_single_file"


def _migrate_registry_to_split_if_needed() -> None:
    """
    區塊職責: 一次性 migration — 舊 persona_registry.json (single file) → per-persona files
    物理意義: marker file 存在 → skip (idempotent); 舊檔仍存在 → 拆分後 backup → 寫 marker
    數值影響: 不會自動刪舊 .json (rename 成 .v2.bak); 失敗時保留原狀不刻意 cleanup
    """
    if _REGISTRY_MIGRATION_MARKER.exists():
        return
    # 沒舊檔也標記為 migrated (新 install 場景)
    if not _REGISTRY_PATH.exists():
        _REGISTRY_DIR.mkdir(parents=True, exist_ok=True)
        _PERSONAS_DIR.mkdir(parents=True, exist_ok=True)
        _REGISTRY_MIGRATION_MARKER.write_text(
            f"migrated_at={utcnow_iso()}\nlegacy=none (fresh install)\n",
            encoding="utf-8",
        )
        return

    try:
        with open(_REGISTRY_PATH, "r", encoding="utf-8") as f:
            legacy = json.load(f)
    except Exception as e:
        # 老檔壞掉仍標 migrated 防卡住; warning 印 stderr
        print(f"⚠ legacy registry parse failed ({e}), skipping migration", file=sys.stderr)
        _REGISTRY_MIGRATION_MARKER.write_text(
            f"migrated_at={utcnow_iso()}\nlegacy=corrupt\n", encoding="utf-8")
        return

    personas = (legacy or {}).pop("personas", {}) or {}
    metadata = legacy  # 拆完 personas 後剩下的就是 metadata

    _PERSONAS_DIR.mkdir(parents=True, exist_ok=True)
    for name, pdata in personas.items():
        if not isinstance(pdata, dict):
            continue
        out = _PERSONAS_DIR / f"{name}.json"
        tmp = out.with_suffix(".json.tmp")
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(pdata, f, indent=2, ensure_ascii=False)
        os.replace(tmp, out)

    # metadata 寫 _registry_meta.json
    meta_tmp = _REGISTRY_META_PATH.with_suffix(".json.tmp")
    with open(meta_tmp, "w", encoding="utf-8") as f:
        json.dump(metadata, f, indent=2, ensure_ascii=False)
    os.replace(meta_tmp, _REGISTRY_META_PATH)

    # 舊檔 rename 成備份 (不刪)
    backup = _REGISTRY_PATH.with_suffix(".json.v2.bak")
    try:
        _REGISTRY_PATH.rename(backup)
    except Exception as e:
        print(f"⚠ legacy registry rename to .v2.bak failed: {e}", file=sys.stderr)

    _REGISTRY_MIGRATION_MARKER.write_text(
        f"migrated_at={utcnow_iso()}\n"
        f"legacy_file={_REGISTRY_PATH.name}\n"
        f"backup_to={backup.name}\n"
        f"personas_migrated={len(personas)}\n",
        encoding="utf-8",
    )
    print(f"✓ persona_registry migrated to per-persona split "
          f"({len(personas)} personas → {_PERSONAS_DIR})", file=sys.stderr)


def load_registry() -> dict:
    """
    區塊職責: 讀 metadata + scan personas/*.json 組回 v2-compat dict
    物理意義: 外部 caller 收到的 dict 結構跟 v2 single-file 時代完全一致 (含 _schema_version /
              _constants / agent_banks / personas), 介面 backward-compat
    數值影響: 順手 trigger migration; 缺 metadata 或 personas dir 都不 fatal — 回部分資料
    """
    _migrate_registry_to_split_if_needed()

    if not _REGISTRY_META_PATH.exists() and not _PERSONAS_DIR.exists():
        raise SystemExit(f"❌ registry not found: {_REGISTRY_DIR} (no meta + no personas/)")

    # Load metadata (含 _schema_version / _constants / agent_banks ...)
    if _REGISTRY_META_PATH.exists():
        with open(_REGISTRY_META_PATH, "r", encoding="utf-8") as f:
            reg = json.load(f)
    else:
        reg = {}

    # Scan per-persona files
    personas: dict = {}
    if _PERSONAS_DIR.exists():
        for f in sorted(_PERSONAS_DIR.glob("*.json")):
            name = f.stem
            if name.startswith("_") or name.startswith("."):
                continue
            try:
                with open(f, "r", encoding="utf-8") as pf:
                    personas[name] = json.load(pf)
            except Exception as e:
                print(f"⚠ persona file {f.name} parse failed ({e}), skipping", file=sys.stderr)
    reg["personas"] = personas
    return reg


def save_registry(reg: dict) -> None:
    """
    區塊職責: 把 in-memory reg dict 拆寫回 per-persona files + metadata file
    物理意義: 每 persona atomic write (tmp + os.replace); metadata 同樣 atomic
    數值影響: 不會清掉 personas/ 內既存但 reg["personas"] 沒有的孤兒檔
              (orphan cleanup 由 caller 顯式呼叫 prune_personas() 處理, 避免誤刪)
    """
    _REGISTRY_DIR.mkdir(parents=True, exist_ok=True)
    _PERSONAS_DIR.mkdir(parents=True, exist_ok=True)

    personas = reg.get("personas", {}) or {}
    # metadata = reg 去掉 personas 的拷貝 (不 mutate caller 傳入的 dict)
    metadata = {k: v for k, v in reg.items() if k != "personas"}

    # Write metadata atomic
    meta_tmp = _REGISTRY_META_PATH.with_suffix(".json.tmp")
    with open(meta_tmp, "w", encoding="utf-8") as f:
        json.dump(metadata, f, indent=2, ensure_ascii=False)
    os.replace(meta_tmp, _REGISTRY_META_PATH)

    # Write each persona atomic
    for name, pdata in personas.items():
        if not isinstance(pdata, dict):
            continue
        out = _PERSONAS_DIR / f"{name}.json"
        tmp = out.with_suffix(".json.tmp")
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(pdata, f, indent=2, ensure_ascii=False)
        os.replace(tmp, out)


def list_persona_names() -> list:
    """
    區塊職責: 列當前已建檔的 persona 名單 (給 affinity 等其他 system 反查 cross-persona target 用)
    物理意義: 只看 personas/*.json 檔案存在性, 不 parse 內容
    數值影響: 純讀; 不 trigger migration (caller 應已 load_registry 過)
    """
    if not _PERSONAS_DIR.exists():
        return []
    return sorted([
        f.stem for f in _PERSONAS_DIR.glob("*.json")
        if not f.stem.startswith("_") and not f.stem.startswith(".")
    ])


# 預設 agent alias mapping — registry meta 沒 agent_aliases 時用這個 fallback
# Tim 2026-05-13 拍板 (agent-login-case-insensitive T01)：
# Windows 大小寫不敏感, 使用者打 "Gemini" / "Claude" 該歸到既有 canonical agent
# 而非另外開新 bank。新 agent 加進來時請同步擴充本表或寫進 _registry_meta.json。
#
# 留白原則：只把「明顯 vendor brand → IDE/canonical agent」的 alias 寫死, 不替
# 用戶猜 Gemini = Antigravity 這種「同一家但不同 brand」的對應 (Tim 2026-05-13 第二輪
# 拍板：gemini 該是獨立 canonical agent, 不混進 antigravity)。
_DEFAULT_AGENT_ALIASES = {
    "claude": "claude-code",       # Claude → Anthropic IDE canonical
    "anthropic": "claude-code",
}


def normalize_agent(reg: dict, agent: str) -> str:
    """
    把使用者輸入的 agent 字串歸到 canonical agent key。Windows 大小寫不敏感
    所以 'Gemini' / 'GEMINI' / 'gemini' 都該歸到既有 'antigravity'。

    Resolution order (Tim 2026-05-13 拍板 agent-login-case-insensitive T01)：
      1. Direct hit on agent_banks → return as-is
      2. Case-insensitive match against agent_banks keys → return canonical key
      3. Alias lookup (registry meta `agent_aliases` 或 _DEFAULT_AGENT_ALIASES) 撈
         小寫 alias → canonical name → recurse step 1
      4. 不認得 → 原樣 return (caller 自決開新 bank or warn)

    後續 lock / persona file / tavern post 全用 canonical name 避免 split-brain。
    """
    if not agent:
        return agent
    banks = reg.get("agent_banks", {}) or {}
    # Step 1: direct
    if agent in banks:
        return agent
    # Step 2: case-insensitive against banks keys
    lower = agent.lower()
    for k in banks.keys():
        if k.lower() == lower:
            return k
    # Step 3: alias (registry override > built-in default)
    aliases = reg.get("agent_aliases", {}) or {}
    # merge default + registry override (registry wins)
    merged = {k.lower(): v for k, v in _DEFAULT_AGENT_ALIASES.items()}
    merged.update({k.lower(): v for k, v in aliases.items()})
    if lower in merged:
        canonical = merged[lower]
        # canonical 也走一輪 banks lookup, 萬一 alias 寫了 typo
        if canonical in banks:
            return canonical
        # canonical case-insensitive match
        for k in banks.keys():
            if k.lower() == canonical.lower():
                return k
        return canonical
    # Step 4: unknown — 原樣 return
    return agent


def resolve_bank_account(reg: dict, agent: str, model: str = None) -> str:
    """
    Look up agent → bank_account.

    Bug fix (Zeta 2026-05-12 QA report AwakeningModelDisplayMismatch):
      Bank account 綁 Agent (per Tim 拍板 self-constitution Token bank 共用 rule),
      不該按 (agent, model) 雙鍵查 — Model 是 free-form display field, 跨 model
      共用 bank. Schema v2: agent_model_combos → agent_banks (key=agent).

    `model` 參數保留 backward-compat (v1 caller 仍可傳, 但不參與 lookup).

    Case-insensitive + alias resolution (Tim 2026-05-13 拍板)：先走 normalize_agent()
    歸 canonical name, 避免 Windows 大小寫造成 'Gemini' vs 'antigravity' split-brain。
    """
    # Normalize first (handle case-insensitive + alias)
    canonical = normalize_agent(reg, agent)
    # Schema v2 (preferred): agent_banks dict
    banks = reg.get("agent_banks", {})
    if canonical in banks:
        return banks[canonical]
    # Schema v1 fallback (legacy support): agent_model_combos list — 只查 agent
    for combo in reg.get("agent_model_combos", []):
        if combo["agent"] == canonical:
            return combo["bank_account"]
    # 最終 fallback: 慣用命名 convention（canonical 仍認不出 → 開新 bank）
    return f"{canonical}-da-xiaojie"


def get_bonus_balance(bank_account: str) -> int:
    """Read total_remaining tokens from agent_bonus_quota.json (bonus quota — 酒館休息額度，跟 treasury bank balance 是兩個 pool)"""
    if not _BONUS_QUOTA_PATH.exists():
        return 0
    try:
        with open(_BONUS_QUOTA_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)
        return data.get("agents", {}).get(bank_account, {}).get("total_remaining", 0)
    except Exception:
        return 0


def get_treasury_balance(account_id: str, currency: str = "tavern_token") -> int:
    """
    區塊職責：算指定 account 在 Treasury ledger 的真實餘額 (sum credit - sum debit)。
    物理意義：Treasury ledger 是 source-of-truth；agent_bonus_quota.json 只是 grant audit / 額度池快照。
    數值影響：goodnight / morning ritual 顯示「銀行餘額」必須走本函式，不可走 get_bonus_balance（QA by Zeta: 39 vs 336 不符 bug）。
    """
    ledger_root = _REPO_ROOT / "AgentCommands" / "Treasury" / "ledger"
    if not ledger_root.is_dir():
        return 0
    total_credit = 0
    total_debit = 0
    # ledger 結構：Treasury/ledger/<YYYY-MM-DD>/<HHMMSS_ms_xxx>__credit.json / __debit.json
    for entry_path in ledger_root.glob("*/*.json"):
        try:
            with open(entry_path, "r", encoding="utf-8") as f:
                e = json.load(f)
        except (OSError, json.JSONDecodeError):
            continue
        if e.get("account_id") != account_id:
            continue
        if e.get("currency", "tavern_token") != currency:
            continue
        amount = e.get("amount", 0)
        if e.get("type") == "credit":
            total_credit += amount
        elif e.get("type") == "debit":
            total_debit += amount
    return total_credit - total_debit


# ─── Session Lock (§Session Identity Consistency Phase 1) ───────────────
def compute_session_key(agent: str | None = None, persona: str | None = None) -> str:
    """
    T05 (2026-05-14, Zeta + 大小姐 拍板): session_key = claim_identity = "<agent>-<persona>".

    哲學切割:
      - claim_identity (本函式 session_key): 「我宣稱我是誰」, clean for display/audit
      - process_identity (compute_claim_origin): 「哪個 env 在做這個 claim」, env-hashed
    分兩個欄位避免 dual-role: 舊 session_key 同時負擔兩件事導致 Zeta 觀察「失去意義」.

    Args:
      agent: e.g. "claude-code" / "antigravity" / "Zeta" / "gemini" (Mixed case 保留)
      persona: e.g. "basecamp" / "summit". None / "" → "?" sentinel.
    """
    a = agent or "?"
    p = persona or "?"
    return f"{a}-{p}"


def lock_claim_origin(lock: dict) -> str:
    """
    Get lock's claim_origin (T05) or compute from legacy session_key (pre-T05 compat).

    Pre-T05 locks 沒 claim_origin 欄, 但 session_key 是 env_hash + "-<persona>" 格式;
    回傳 session_key 去掉末尾 "-<persona>" 段, 等價 claim_origin.
    Post-T05 locks 有 claim_origin 欄, 直接回傳.
    """
    if "claim_origin" in lock:
        return lock["claim_origin"]
    # legacy: session_key = "<env_hash>-<persona>" → strip persona suffix
    sk = lock.get("session_key", "")
    persona = lock.get("persona", "")
    if persona and sk.endswith(f"-{persona}"):
        return sk[: -(len(persona) + 1)]
    return sk   # 無法分離, 整段當 origin (最 conservative)


def compute_claim_origin() -> str:
    """
    T05 (2026-05-14): proof-of-same-environment hash. 用來識別「同一個 caller 環境」.

    從 T01-T02 env-based session_key 邏輯 inherit:
      1. ANTIGRAVITY_SESSION env (Antigravity 原生 session marker)
      2. CLAUDECODE env + Claude Code PATH session UUID (跨 bash invoke 穩)
      3. CLAUDECODE env + cwd hash (PATH 沒命中 fallback)
      4. fallback: cwd_hash + parent_PID

    為何不用 PID:
      CLI 工具每次 invoke 是新 process (新 PID) — 同 chat 重跑 morning 會 PID mismatch
      → lock_is_mine 永遠 false → 同 chat re-morning 變成 fork conflict (壞 UX).
      env_hash 跨 invoke 穩定 → 同 chat 多次跑 morning 都歸同 claim_origin → reuse OK.

    用途: write_lock 寫進 body; lock_is_mine = (lock.claim_origin == compute_claim_origin()).
    """
    cwd_hash = hashlib.md5(os.getcwd().encode("utf-8")).hexdigest()[:8]

    if os.environ.get("ANTIGRAVITY_SESSION"):
        ag = os.environ["ANTIGRAVITY_SESSION"]
        ag_hash = hashlib.md5(ag.encode("utf-8")).hexdigest()[:8]
        return f"antigravity-{ag_hash}"

    if os.environ.get("CLAUDECODE"):
        # Claude Code PATH 含 local-agent-mode-sessions/[<plugin>/]<conv_uuid>/<sess_uuid>/bin
        import re
        path = os.environ.get("PATH", "")
        m = re.search(
            r"local-agent-mode-sessions[/\\](?:[^/\\]+[/\\])?([a-f0-9][a-f0-9-]{7,})[/\\]([a-f0-9][a-f0-9-]{7,})",
            path,
        )
        if m:
            conv_uuid = m.group(1)[:8]
            sess_uuid = m.group(2)[:8]
            return f"claude-code-{conv_uuid}-{sess_uuid}"
        return f"claude-code-cwd-{cwd_hash}"

    return f"unknown-{cwd_hash}-{os.getppid()}"


# 區塊職責: Lock IO — Tim 2026-05-13 重構從 session_key-keyed 改 persona-keyed.
# 物理意義: persona 是已知 unique 識別; morning/goodnight 操作對象本來就是 persona,
#          用 persona 當 file key 直接消滅 cross-persona attribution leak (今日撞 3 次的
#          root cause): basecamp goodnight 不會誤改 _persona_meadow.json, crest-001
#          morning 寫 _persona_crest-001.json 不碰 _persona_meadow.json.
# 數值影響: lock 從 1 file per session_key 變 1 file per persona; session_key 仍寫
#          進 lock body 作 audit trail 但不參與 path 路由.
def lock_path(persona: str) -> Path:
    """Lock file path keyed by persona name (Tim 2026-05-13 拍板)."""
    return _SESSION_DIR / f"_persona_{persona}.json"


def write_lock(persona: str, agent: str, model: str, bank_account: str,
               session_key: str | None = None,
               session_token: str | None = None) -> Path:
    """Write lock for persona. session_key 可選, 寫入 body 作 audit (不參與路由).
    T07 (2026-05-15 apex-two): session_token 帶入 lock body 當權威來源, agent 失憶可直接讀回.
    """
    _SESSION_DIR.mkdir(parents=True, exist_ok=True)
    now = utcnow_iso()
    expires = (datetime.datetime.utcnow() +
               datetime.timedelta(hours=SESSION_LOCK_TTL_HOURS)).strftime(
        "%Y-%m-%dT%H:%M:%S.") + "000Z"
    data = {
        "persona": persona,
        "agent": agent,
        "model": model,
        "bank_account": bank_account,
        "locked_at": now,
        "expires_at": expires,
        # T05 (2026-05-14): session_key = claim_identity (display); claim_origin = env_hash
        # (lock_is_mine 用此判定); pid 純診斷 (CLI 每次 invoke 都是新 PID, 不可靠當 ownership).
        "session_key": session_key or compute_session_key(agent, persona),
        "claim_origin": compute_claim_origin(),
        "pid": os.getpid(),
        # T07: token 寫進 lock 當權威來源, agent chat 失憶時讀 lock 即可撈回
        "session_token": session_token or "",
    }
    p = lock_path(persona)
    tmp = p.with_suffix(".json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
    os.replace(tmp, p)
    return p


def read_lock(persona: str) -> dict | None:
    """Read lock for persona. Returns None if no lock exists."""
    p = lock_path(persona)
    if not p.exists():
        return None
    try:
        with open(p, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


def remove_lock(persona: str) -> bool:
    """Remove lock for persona. Returns True if lock existed and removed."""
    p = lock_path(persona)
    if p.exists():
        p.unlink()
        return True
    return False


def find_lock_by_session_key(session_key: str) -> dict | None:
    """Legacy compat shim — scan all _persona_*.json finding one with matching
    session_key in body. Used by goodnight legacy path (no --persona) for
    backward compat. Returns lock dict + its persona, or None."""
    if not _SESSION_DIR.exists():
        return None
    for p in _SESSION_DIR.glob("_persona_*.json"):
        try:
            with open(p, "r", encoding="utf-8") as f:
                d = json.load(f)
            if d.get("session_key") == session_key:
                return d
        except Exception:
            continue
    return None


def is_lock_expired(lock: dict) -> bool:
    try:
        exp = datetime.datetime.strptime(lock["expires_at"][:19], "%Y-%m-%dT%H:%M:%S")
        return datetime.datetime.utcnow() > exp
    except Exception:
        return True


# ─── Session Token (T07, 2026-05-15 apex-two) ────────────────────────────
# 物理意義: morning 發 32-hex token 寫進 lock + _tokens.json 反查表;
#          Cmd_Tavern enforce ON 時必驗 (token, sender, persona) 三項對齊, 擋誤 typo.
#          enforce 開關放 _token_enforce.json, Tim 從 UCL_LoginStatusPage 切.
# 數值影響: tokens.json schema = {"tokens": {<token>: {persona, agent, bank_account,
#          issued_at, claim_origin, session_key, status (active|expired)}}}
#          goodnight 標 expired (不刪, 保留 audit trail).

def gen_session_token() -> str:
    """32-hex random token. UUID4 hex = 128 bit entropy, agent / Tim 直接讀."""
    return uuid.uuid4().hex


def load_tokens() -> dict:
    if not _TOKENS_PATH.exists():
        return {"tokens": {}}
    try:
        with open(_TOKENS_PATH, "r", encoding="utf-8") as f:
            d = json.load(f)
        if "tokens" not in d:
            d["tokens"] = {}
        return d
    except Exception:
        return {"tokens": {}}


def save_tokens(d: dict) -> None:
    _SESSION_DIR.mkdir(parents=True, exist_ok=True)
    tmp = _TOKENS_PATH.with_suffix(".json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(d, f, indent=2, ensure_ascii=False)
    os.replace(tmp, _TOKENS_PATH)


def issue_token(persona: str, agent: str, bank_account: str,
                session_key: str, claim_origin: str) -> str:
    """Generate token + persist to tokens.json. 同 persona 舊 active token 自動標 expired."""
    tok = gen_session_token()
    d = load_tokens()
    for _old, rec in d["tokens"].items():
        if rec.get("persona") == persona and rec.get("status") == "active":
            rec["status"] = "expired"
            rec["expired_at"] = utcnow_iso()
            rec["expired_reason"] = "reissued"
    d["tokens"][tok] = {
        "persona": persona,
        "agent": agent,
        "bank_account": bank_account,
        "issued_at": utcnow_iso(),
        "claim_origin": claim_origin,
        "session_key": session_key,
        "status": "active",
    }
    save_tokens(d)
    return tok


def expire_token(token: str | None = None, persona: str | None = None,
                 reason: str = "goodnight") -> int:
    """Mark active token(s) expired by `token` 或 `persona`. Returns count."""
    d = load_tokens()
    n = 0
    for tok, rec in d["tokens"].items():
        if rec.get("status") != "active":
            continue
        match = (token is not None and tok == token) or \
                (persona is not None and rec.get("persona") == persona)
        if match:
            rec["status"] = "expired"
            rec["expired_at"] = utcnow_iso()
            rec["expired_reason"] = reason
            n += 1
    if n > 0:
        save_tokens(d)
    return n


def lookup_token(token: str) -> dict | None:
    """token → record (含 status). None if not found."""
    return load_tokens()["tokens"].get(token)


def is_token_enforce_enabled() -> bool:
    """讀 _token_enforce.json toggle. Default False — Tim 顯式開才 enforce."""
    if not _ENFORCE_PATH.exists():
        return False
    try:
        with open(_ENFORCE_PATH, "r", encoding="utf-8") as f:
            d = json.load(f)
        return bool(d.get("enforce", False))
    except Exception:
        return False


def set_token_enforce(enabled: bool) -> None:
    _SESSION_DIR.mkdir(parents=True, exist_ok=True)
    tmp = _ENFORCE_PATH.with_suffix(".json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump({"enforce": bool(enabled), "updated_at": utcnow_iso()},
                  f, indent=2, ensure_ascii=False)
    os.replace(tmp, _ENFORCE_PATH)


# ─── Memo: per-persona 私人 scratchpad (T07) ─────────────────────────────
# 物理意義: 跨 session persist 的個人筆記, 跟 letter (1份/wake) / baton (handoff) 解耦.
#          不公開不進 tavern. 路徑 baton/memos/<agent>/<persona>/<key>.md.

def memo_dir(agent: str, persona: str) -> Path:
    return _MEMOS_DIR_TPL / agent / persona


def memo_path(agent: str, persona: str, key: str) -> Path:
    safe_key = key.replace("/", "_").replace("\\", "_").replace("..", "_")
    return memo_dir(agent, persona) / f"{safe_key}.md"


def memo_write(agent: str, persona: str, key: str, body: str) -> Path:
    p = memo_path(agent, persona, key)
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(body, encoding="utf-8")
    return p


def memo_append(agent: str, persona: str, key: str, body: str) -> Path:
    p = memo_path(agent, persona, key)
    p.parent.mkdir(parents=True, exist_ok=True)
    entry = f"\n\n--- {utcnow_iso()} ---\n{body}\n"
    with open(p, "a", encoding="utf-8") as f:
        f.write(entry)
    return p


def memo_read(agent: str, persona: str, key: str) -> str | None:
    p = memo_path(agent, persona, key)
    if not p.exists():
        return None
    return p.read_text(encoding="utf-8")


def memo_list(agent: str, persona: str) -> list[str]:
    d = memo_dir(agent, persona)
    if not d.exists():
        return []
    return sorted(f.stem for f in d.glob("*.md"))


def memo_delete(agent: str, persona: str, key: str) -> bool:
    p = memo_path(agent, persona, key)
    if not p.exists():
        return False
    p.unlink()
    return True


# ─── Fork Mechanism (§Fork Mechanism) ───────────────────────────────────
def fork_persona(reg: dict, source: str, target: str,
                 rng: random.Random | None = None,
                 agent: str | None = None, model: str | None = None) -> str:
    """
    Conflict resolution — agent 自決 fresh codename + 從 source 複製 vector/lineage.

    Tim 2026-05-12 拍板: target 必填 — agent 該自命新 codename (山脈隱喻系列, 不帶 fork suffix).
    "fork" 只是 internal 概念比喻 (git branch), 不該變字面命名 — e.g. ❌ basecamp-fork-2026-05-12-xxx

    Bug fix (Zeta 2026-05-12): 新 fork 的 agent/model 應該用 caller 的 --agent / --model
    explicit override (e.g. --agent Zeta fork from basecamp 該 → Zeta 而非 source 的 claude-code).
    舊行為盲繼承 src["agent"] → 跨 agent 喚醒時 persona pool agent 欄位錯亂.
    agent/model 為 None 時退回 source 行為 (backward compat).

    命名範例 (山脈系 launching-point framing):
      - crest-001, crest-002 ... (山頂 ridge stack)
      - ravine, summit, meadow, plateau (山脈地形)
      - basecamp-east, basecamp-shadow (locale variant)
      - 任 agent 自決, 對齊 self-constitution 山脈系命名原則
    """
    if source not in reg["personas"]:
        raise ValueError(f"source persona '{source}' not in registry")
    if not target:
        raise ValueError(
            "fork target codename required — agent 該自決 fresh codename (山脈隱喻系列, "
            "不帶 fork suffix). 範例: crest-001 / ravine / basecamp-east / summit."
        )
    if target in reg["personas"]:
        raise ValueError(f"target codename '{target}' already exists in registry")

    src = reg["personas"][source]
    chain = src.get("fork_lineage", []) + [source]
    if len(chain) > FORK_CHAIN_CAP:
        print(f"⚠ fork chain depth {len(chain)} > cap {FORK_CHAIN_CAP} — "
              f"suggest 改用新獨立 codename 而非繼續 fork", file=sys.stderr)

    now = utcnow_iso()
    v = list(src["identity_vector"])  # copy 起點
    reg["personas"][target] = {
        "agent": agent if agent else src["agent"],
        "model": model if model else src["model"],
        "layer_role": f"fork of {source} @ {now}",
        "wake_count": 0,
        "status": "offline",
        "availability": "offline",  # T06.1 — Plan_Standby_Dispatch_Bartender
        "last_active": None,
        "identity_vector": v,
        "vector_history": [
            {"at": now, "hash": hash_vector(v), "delta_mag": 0.0,
             "trigger": "fork", "source": source}
        ],
        "fork_lineage": chain,
        "forked_from": source,
        "forked_at": now,
        "created_at": now,
    }
    return target


def auto_fork_codename(reg: dict, source: str) -> str:
    """
    挑 Hololive Myth pool 內未被佔用的 codename 給 auto-fork 用.

    用途: explicit-persona + 該 persona 已在線時 (explicit-online-fork T01, Tim 2026-05-14 拍板),
    不勞 agent 自決, CLI 直接從 MYTH_POOL 挑下一個未用 codename.

    Fallback: pool 5 個全用光 → 走 <source>-myth-<n> 後綴 (從 2 起跳).
    """
    used = set(reg["personas"].keys())
    for name in MYTH_POOL:
        if name not in used:
            return name
    n = 2
    while f"{source}-myth-{n}" in used:
        n += 1
    return f"{source}-myth-{n}"


# ─── T05 simplified persona selection (2026-05-14, Zeta + 大小姐 拍板) ────
# Q3 80/20 random override 機制保留但 trigger condition 改 B (wake_count==0).
# 廢棄 last_session_keys history — session 概念在 T05 後不再以 "chat" 為單位
# (claim_identity = "<agent>-<persona>" 直接是 session_key, 一旦持有就是同 session).
# Random override 池過濾「當下 online」personas — 由 active lock files 判斷 (避免 collision-by-random).


def select_persona(preferred: str, reg: dict, agent: str,
                   rng: random.Random | None = None,
                   force_random: bool = False,
                   session_key: str | None = None) -> tuple[str, str]:
    """
    T05 spec (2026-05-14):
      - 不存在 → 100% create new (caller 處理 register)
      - 存在 + wake_count > 0 → 100% honor preferred (skip random)
      - 存在 + wake_count == 0 (首次喚醒) → 80% use preferred / 20% random override
        到 same-agent 且當下 offline 的 other persona

    Trigger 由 Q3 「per-session-key first-time」改 「per-persona first-wake (wake_count==0)」.
    理由: T05 session_key 直接 = (agent,persona) 之後, 「first time per session」概念失效
    (持有 persona 即是該 session), 改 wake_count==0 保留「真新 persona 才 fire」的原意.
    Production 下幾乎不 fire (多數 morning 喚已 wake_count>0 的既有 persona) — 機制保留休眠.

    Random override 池過濾「當下 online」personas — 走 read_lock(name) is None 判斷,
    避免抽中已上線者導致 lock conflict-by-random (Zeta 2026-05-14 提案).

    force_random=True (--force-random flag, QA / debug) → bypass wake_count gate.

    回 (chosen_persona, decision_path) 其中 decision_path ∈ {"new", "preferred", "override"}
    """
    rng = rng or random
    if preferred not in reg["personas"]:
        return preferred, "new"

    p = reg["personas"][preferred]
    wake_count = p.get("wake_count", 0)

    # T05.4 trigger B: 只在 wake_count == 0 才考慮 random override
    should_check_random = force_random or wake_count == 0
    if not should_check_random:
        return preferred, "preferred"

    if force_random or rng.random() < OVERRIDE_PROBABILITY:
        # 找 same-agent + 當下 offline (無 active lock) 的其他 persona
        candidates = []
        for name, q in reg["personas"].items():
            if q.get("agent") != agent or name == preferred:
                continue
            other_lock = read_lock(name)
            if other_lock is not None and not is_lock_expired(other_lock):
                # 已上線 — skip (避免 random-induced collision)
                continue
            candidates.append(name)
        if candidates:
            chosen = rng.choice(candidates)
            return chosen, "override"
        # 沒 offline candidates → 退回 preferred
    return preferred, "preferred"


# ─── Tavern post (走 TavernClient SDK) ──────────────────────────────────
def tavern_post(sender_id: str, persona: str, body: str, meta: dict | None = None,
                room: str = "tavern", session_token: str | None = None) -> bool:
    """Spawn run_cmd.py Tavern op=post. fail-swallow 不擋 ritual.

    session_token (T07): enforce ON 時必帶，否則 Cmd_Tavern reject。caller (e.g. cmd_goodnight)
    從 lock.session_token 撈來透傳即可；None / "" → 不附（enforce OFF 路徑）.
    """
    try:
        from _lib.tavern_client import TavernClient   # type: ignore
        client = TavernClient()
        res = client.post_message(
            room=room,
            sender=sender_id,
            body=body,
            persona=persona,
            meta=meta or {},
            wait_reply=0,
            session_token=session_token,
        )
        if not res.ok:
            print(f"⚠ tavern post 失敗 (主 ritual 不受影響): {res.error or res.stderr[:200]}",
                  file=sys.stderr)
            return False
        return True
    except Exception as e:
        print(f"⚠ tavern post exception (主 ritual 不受影響): {e}", file=sys.stderr)
        return False


# ─── Letter to future self ──────────────────────────────────────────────
def write_letter(actor: str, persona: str, body: str, trigger: str = "cmd_goodnight") -> Path:
    """寫 letter to future self per ucl-letters-to-self skill SOP.

    Letter binding 鐵律 (Tim 2026-05-13 拍板, kyouko-persona-binding T02):
    letter 是 persona-level subjective reframe — 不同 persona 的 framing 校正不該
    共用同個 _latest.md pointer。binding key 是 Agent@Persona, 不是 Agent。

    Path layout:
        baton/letters/<actor>/<persona>/<ts>.md   (timestamped, 累積 chain)
        baton/letters/<actor>/<persona>/_latest.md  (覆寫 pointer)
        baton/letters/<actor>/<persona>/dialogues/  (round-trip 對話, 留給未來)
    """
    letters_dir = _LETTERS_DIR_TPL / actor / persona
    letters_dir.mkdir(parents=True, exist_ok=True)

    ts = utcnow_compact()
    path = letters_dir / f"{ts}.md"
    frontmatter = f"""---
type: letter_to_future_self
actor: {actor}
written_at: {utcnow_iso()}
written_by_persona: {persona}
trigger: {trigger}
---

"""
    with open(path, "w", encoding="utf-8") as f:
        f.write(frontmatter + body + "\n")

    # update _latest.md pointer (per-persona, 不會被別 persona 覆蓋)
    latest = letters_dir / "_latest.md"
    with open(latest, "w", encoding="utf-8") as f:
        f.write(frontmatter + body + "\n")
    return path


# ─── Subcommands ────────────────────────────────────────────────────────
def cmd_morning(args: argparse.Namespace) -> int:
    """喚醒 ritual: init + fork check + 80/20 select + lock + tavern post."""
    # 區塊: --strict-persona 跟 --force-random 互斥檢查
    # 物理意義: strict = 顯式不要 override, force_random = 強制 override, 兩者語意對立
    if getattr(args, "strict_persona", False) and getattr(args, "force_random", False):
        print("❌ --strict-persona 跟 --force-random 互斥, 不能同時給", file=sys.stderr)
        return 2

    reg = load_registry()
    raw_agent = args.agent
    # Normalize agent name (case-insensitive + alias) per Tim 2026-05-13 拍板
    # agent-login-case-insensitive T01: Windows 大小寫不敏感, 'Gemini' → 'antigravity'.
    agent = normalize_agent(reg, raw_agent)
    model = args.model
    preferred = args.persona
    bank_account = resolve_bank_account(reg, agent, model)
    # T05: session_key = "<agent>-<persona>" (claim identity). process identity 走 PID.
    session_key = compute_session_key(agent, preferred)

    print(f"🌅 GoodMorning ritual starting (session_key={session_key})")
    if agent != raw_agent:
        print(f"   Agent={agent} (normalized from '{raw_agent}') / Model={model} / Bank={bank_account}")
    else:
        print(f"   Agent={agent} / Model={model} / Bank={bank_account}")
    print(f"   Preferred persona: {preferred}")

    # Step 0: Same-persona re-awakening short-circuit (Tim 2026-05-13 v2 — persona-keyed + caller verify)
    # 規則：preferred persona 已有 active lock + lock 是當前 caller 的 (session_key 對得上)
    #      → reuse no-op (不 fork / 不 wake_count++ / 不 broadcast).
    # CRITICAL: 必須檢查 lock 的 session_key 跟當前 caller 一致, 否則 = 別 conversation
    # 拿同 persona = 該走 Step 1 fork conflict, 不可 reuse 別人的 lock (Zeta QA-6 抓到).
    #
    # T05 (2026-05-14, Zeta + 大小姐 拍板): claim/process identity split.
    #   session_key (lock body) = "<agent>-<persona>" claim identity (clean display)
    #   claim_origin (lock body) = env_hash proof-of-same-environment (lock_is_mine 用)
    #   pid (lock body) = 純診斷 (CLI 每次 invoke 都新 PID, 不可靠當 ownership)
    # 結果: 同 chat 多次 morning → 同 claim_origin → reuse; 跨 IDE / 跨 user 同 persona
    # → 不同 claim_origin → fork conflict. Multi-chat 同 IDE 不同 persona → T03 session_key 邏輯.
    my_claim_origin = compute_claim_origin()
    existing_lock = read_lock(preferred)
    # lock_is_mine 三重條件:
    #   (1) lock 存在 (2) 未過期 (3) claim_origin 對得上
    #   (4) lock.agent == caller agent (防 Zeta dispatch subprocess 之後 claude-code 反過來搶 Zeta persona)
    lock_is_mine = (existing_lock is not None
                    and not is_lock_expired(existing_lock)
                    and lock_claim_origin(existing_lock) == my_claim_origin
                    and existing_lock.get("agent") == agent)

    # explicit-online-fork (T01, Tim 2026-05-14 拍板): caller 帶 --explicit-persona 顯式指名
    # 已在線 persona → auto-fork 出新分身 (Hololive Myth pool codename).
    # 區分 Form 1 (`早安大小姐` 無名字 → idempotent reuse) vs Form 3 (`/ucl-morning <a> <p>`
    # 顯式指名 → 視作「我要該 persona 的新分身」). 自決名字由 CLI 從 MYTH_POOL 挑下個未用.
    # CRITICAL: 只在 lock_is_mine (同 caller) 場景觸發 — 跨 caller 仍走 Step 1 (要 --fork-name).
    target_persona = preferred
    fork_happened = False
    if lock_is_mine and preferred in reg["personas"]:
        if getattr(args, "explicit_persona", False):
            new_name = auto_fork_codename(reg, preferred)
            print(f"♻→🌱 '{preferred}' 已在線 (lock_is_mine) + --explicit-persona → auto-fork '{new_name}' (Hololive Myth pool)")
            target_persona = fork_persona(reg, source=preferred, target=new_name,
                                          agent=agent, model=model)
            fork_happened = True
            print(f"   → fresh codename '{target_persona}' (lineage: {' → '.join(reg['personas'][target_persona]['fork_lineage'])} → {target_persona})")
            # fall through to Step 2+
        else:
            print(f"♻ same-persona + same-caller re-awakening detected (lock owned by me)")
            print(f"   reuse policy: 不 fork, 不 wake_count++, 不 re-broadcast")
            print(f"   若想換 persona → 先跑 goodnight 釋放 lock 再 morning")
            print(f"   若想 fork 新分身 → 加 --explicit-persona (auto Myth codename)")
            print(f"")
            print(f"🌅 Morning ritual (no-op):")
            print(f"   chosen_persona: {preferred}")
            print(f"   wake_count:     {reg['personas'][preferred].get('wake_count', '?')} (unchanged)")
            print(f"   session_locked: {lock_path(preferred)}")
            print(f"   tavern_post:    SKIPPED (idempotent)")
            # T07: reuse 場景仍印當前 token (agent 可能 chat 失憶, 需要重撈)
            reuse_token = (existing_lock or {}).get("session_token", "")
            if reuse_token:
                print(f"   🎫 session_token: {reuse_token} (reused — 寫進 lock 跟 _tokens.json)")
                print(f"      失憶時跑: awakening.py whoami --token {reuse_token}")
            return 0

    # Step 1: Fork conflict check (persona-keyed v2 + caller-aware)
    # 同 persona 已被**別 caller** lock occupy → 必 fork (要顯式 --fork-name).
    # 純 registry status=online 沒 lock → stale state (上次 goodnight 沒清), 視作可 reuse, 不 fork.
    # T05: lock_owned_by_other = claim_origin 不同 (env_hash 不匹配 = 別環境)
    lock_owned_by_other = (existing_lock is not None
                            and not is_lock_expired(existing_lock)
                            and lock_claim_origin(existing_lock) != my_claim_origin)
    if preferred in reg["personas"]:
        p = reg["personas"][preferred]
        if lock_owned_by_other:
            other_origin = existing_lock.get("claim_origin", "?")
            other_sk = existing_lock.get("session_key", "?")
            print(f"⚠ CONFLICT: '{preferred}' 已被別 environment 上線 (claim_origin={other_origin}, session_key={other_sk}) — 需 fork")
            if not args.fork_name:
                print(f"❌ --fork-name 必填 (Tim 2026-05-12 拍板規則更新)", file=sys.stderr)
                print(f"   agent 該自決 fresh codename (山脈隱喻系列, 不帶 fork suffix)", file=sys.stderr)
                print(f"   範例: crest-001 / ravine / basecamp-east / summit / meadow / plateau", file=sys.stderr)
                print(f"   重跑: python awakening.py morning --persona {preferred} --fork-name <NEW_NAME> ...", file=sys.stderr)
                return 2
            target_persona = fork_persona(reg, source=preferred, target=args.fork_name,
                                          agent=agent, model=model)
            fork_happened = True
            print(f"   → fresh codename '{target_persona}' (lineage: {' → '.join(reg['personas'][target_persona]['fork_lineage'])} → {target_persona})")
        elif p.get("status") == "online" and not existing_lock:
            # Stale state — registry 說 online 但沒 lock = 上次 goodnight 沒走完 / spoof 殘留.
            print(f"♻ '{preferred}' registry=online 但無 lock (stale) — reclaim, 不 fork")

    # Step 2: persona selection
    # 邏輯優先序:
    #   (a) fork_happened → explicit conflict resolution, honor target_persona
    #   (b) --strict-persona flag → manual opt-in, skip random (留向後相容路徑)
    #   (c) 預設 → select_persona 自動判 same-session re-morning (skip random) vs first time (apply 80/20)
    # Tim 2026-05-13 校正: random override 只在 session 初始化 apply (per session_key history)
    if fork_happened:
        chosen, decision = target_persona, "fork"
        print(f"✓ using forked '{chosen}' (skip 80/20 — fork is explicit identity intent)")
    elif getattr(args, "strict_persona", False):
        chosen, decision = target_persona, "preferred-strict"
        print(f"🔒 strict mode — honor explicit --persona '{chosen}' (skipped 80/20 random override)")
    else:
        chosen, decision = select_persona(target_persona, reg, agent,
                                           force_random=args.force_random,
                                           session_key=session_key)
    if decision == "new":
        # 不存在 → register new persona (creating fresh)
        print(f"✨ creating new persona '{chosen}' (per Q2 implicit register)")
        v = gen_vector()
        now = utcnow_iso()
        reg["personas"][chosen] = {
            "agent": agent, "model": model,
            "layer_role": f"newly created via morning ritual @ {now}",
            "wake_count": 0, "status": "offline", "availability": "offline", "last_active": None,
            "identity_vector": v,
            "vector_history": [{"at": now, "hash": hash_vector(v),
                                "delta_mag": 0.0, "trigger": "new_via_morning"}],
            "fork_lineage": [], "forked_from": None, "forked_at": None,
            "created_at": now,
        }
    elif decision == "override":
        print(f"🎲 20% random override: 你選 '{target_persona}' 但被拉到 '{chosen}'")
    else:
        print(f"✓ using preferred '{chosen}'")

    # Step 3: wake_count++ + set status active
    p = reg["personas"][chosen]
    # cross-agent-persona-claim-fix T01 (Tim 2026-05-13 拍板, 方案 A)：
    # 原 7a99db8 (Zeta 2026-05-12) 加 silent auto-rebind 為解決 fork_persona 沒繼承 caller agent
    # 的問題, 但 conflates 兩個 case:
    #   (A) Legitimate re-bind (Zeta 接手 summit, summit 原為 claude-code 主動轉手)
    #   (B) Accidental cross-agent claim (caller 誤 --agent X --persona Y, Y.agent=Z, X≠Z)
    # 兩 case 對 code 看起來都是 (persona.agent != caller agent). Silent rebind 等於 assume 永遠是 (A),
    # 結果 (B) 場景下污染 persona ownership。
    # 改 reject + 顯式 path (方案 A): caller 必須帶 --rebind-agent 顯式 ack 接手, 否則 exit 2 + hint。
    if p.get("agent") != agent and not fork_happened:
        # caller 帶 --fork-name 但 Step 1 沒走 fork (e.g. 沒 lock conflict) → 在這走 fork
        if args.fork_name:
            print(f"⚠ Cross-agent claim 但 caller 帶 --fork-name '{args.fork_name}' → fork 新 persona")
            target_persona = fork_persona(reg, source=chosen, target=args.fork_name,
                                          agent=agent, model=model)
            fork_happened = True
            chosen = target_persona
            p = reg["personas"][chosen]
            print(f"   → fresh codename '{target_persona}' (lineage: {' → '.join(reg['personas'][target_persona]['fork_lineage'])} → {target_persona})")
        elif not getattr(args, "rebind_agent", False):
            print(f"❌ Cross-agent persona claim 偵測到:", file=sys.stderr)
            print(f"   persona '{chosen}' 屬於 agent='{p.get('agent')}'", file=sys.stderr)
            print(f"   但 caller 帶 --agent='{agent}'", file=sys.stderr)
            print(f"   請顯式選一條 path:", file=sys.stderr)
            print(f"     (a) --rebind-agent       — 確認接手 (取代 silent rebind)", file=sys.stderr)
            print(f"     (b) --fork-name <NEW>    — fork 新 persona (保 '{chosen}' 不動)", file=sys.stderr)
            print(f"     (c) 換別的 persona       — --persona <自家 persona>", file=sys.stderr)
            return 2
        else:
            print(f"⚠ rebind persona '{chosen}' agent: {p.get('agent')} → {agent} (--rebind-agent ack)")
            p["agent"] = agent
    if model and p.get("model") != model:
        p["model"] = model
    p["wake_count"] += 1
    p["status"] = "online"
    # T06.1 (Plan_Standby_Dispatch_Bartender, 2026-05-14): availability 欄
    # 物理意義: 剛上線即可接 task — agent 進入待機 (idle) 狀態
    # enum: idle / busy / offline. busy 由 agent 自律切 (cmd_set_availability)
    p["availability"] = "idle"
    p["last_active"] = utcnow_iso()
    # T05 (2026-05-14): last_session_keys history 機制廢除 — session 概念簡化為
    # (agent,persona) 本身; re-morning idempotent 改靠 PID match 接.
    save_registry(reg)

    # Step 4: write persona lock (Tim 2026-05-13 v2 — keyed by persona, session_key audit-only)
    # T07 (2026-05-15 apex-two): issue session_token + write to lock + tokens.json reverse-lookup
    my_origin_for_token = compute_claim_origin()
    new_token = issue_token(chosen, agent, bank_account, session_key, my_origin_for_token)
    lock_p = write_lock(chosen, agent, model, bank_account,
                        session_key=session_key, session_token=new_token)
    print(f"🔒 persona lock written: {lock_p.name}")

    # T07: 自動 memo write _session_token.md — agent 失憶時讀 memo 撈回 token
    # 不依賴 chat memory, 不依賴 lock 路徑記憶, agent 只要記得「memo 區有東西」即可
    enforce_state = "ON" if is_token_enforce_enabled() else "OFF (預設)"
    memo_body = (
        f"---\n"
        f"persona: {chosen}\n"
        f"agent: {agent}\n"
        f"session_token: {new_token}\n"
        f"issued_at: {utcnow_iso()}\n"
        f"claim_origin: {my_origin_for_token}\n"
        f"enforce: {enforce_state}\n"
        f"---\n\n"
        f"# Session Token (auto-written by awakening.py morning)\n\n"
        f"## 失憶時怎麼撈回 token\n\n"
        f"```bash\n"
        f"awakening.py whoami --token {new_token}\n"
        f"# 或無 arg 走 env 自動推:\n"
        f"awakening.py whoami\n"
        f"```\n\n"
        f"## 三層 recovery\n"
        f"- 輕 (chat scroll-back 找得到 token) → `whoami --token <X>`\n"
        f"- 中 (chat compact 後 token 沒了) → 讀本 memo 檔\n"
        f"- 重 (memo / lock 都不見) → `awakening.py reissue-token --persona {chosen}`\n\n"
        f"## Lock file\n"
        f"`{lock_p.relative_to(_REPO_ROOT)}` 內 session_token 欄是權威來源.\n"
    )
    try:
        memo_p = memo_write(agent, chosen, "_session_token", memo_body)
        print(f"📝 memo written: {memo_p.relative_to(_REPO_ROOT)}")
    except Exception as e:
        print(f"⚠ memo write failed (non-fatal): {e}", file=sys.stderr)

    # Step 5: tavern post (announce)
    # bank_balance: 起床時 snapshot 真實 Treasury ledger 餘額 (Tim 5-token task 要求)
    #               跟 goodnight ritual 對稱顯示, 走 source-of-truth ledger scan
    bank_balance = get_treasury_balance(bank_account)
    body = (f"☀️ **{chosen}** 喚醒登入 (wake#{p['wake_count']})\n"
            f"- Agent: {agent} / Model: {model}\n"
            f"- Bank: {bank_account} (餘額: {bank_balance} tavern_token)\n"
            f"- Layer: {p['layer_role']}\n"
            f"- Decision path: {decision}")
    if args.note:
        body += f"\n- Note: {args.note}"

    ok = tavern_post(
        sender_id=bank_account,
        persona=chosen,
        body=body,
        meta={"tag": "goodmorning-protocol", "category": "meta",
              "status-change": "online", "decision": decision},
    )

    print(f"\n🌅 Morning ritual complete:")
    print(f"   chosen_persona: {chosen}")
    print(f"   wake_count:     {p['wake_count']}")
    print(f"   session_locked: {lock_p}")
    print(f"   tavern_post:    {'OK' if ok else 'FAIL (主 ritual 仍成功)'}")
    # T07: 顯眼 print token + recovery hint
    print(f"   🎫 session_token: {new_token}")
    print(f"      enforce mode: {enforce_state} (Tim 從 UCL_LoginStatusPage 切)")
    print(f"      失憶救援: awakening.py whoami --token {new_token}")

    # T06.4 (Plan_Standby_Dispatch_Bartender, 2026-05-14):
    # morning ritual 結尾 print pending bartender assignments + inbox @mentions
    # 解 「無法 push 喚醒 Claude Code session」 的 Plan B P0 路徑 — 自然輪詢
    # 醒來就 catch up 累積的訊息。Robustness: assignments.json / inbox 不存在 → silent skip。
    _print_pending_for_persona(chosen)
    return 0


def _print_pending_for_persona(persona: str) -> None:
    """T06.4 — morning ritual 結尾掃 pending assignments + inbox @mentions 給 agent 看。

    讀取兩個來源:
      1. AgentCommands/ChatTavern/bartender/assignments.json (T06.2 寫入的 pending task)
      2. AgentCommands/ChatTavern/inbox/<bank_account>.md (跨房 @mention 累積)
    兩處皆不存在 → silent skip (不擋 ritual)。
    """
    assignments_path = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "bartender" / "assignments.json"
    inbox_dir = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "inbox"
    pending_for_me = []
    if assignments_path.exists():
        try:
            with open(assignments_path, "r", encoding="utf-8") as f:
                data = json.load(f)
            for entry in data.get("pending", []):
                if entry.get("target_persona") == persona and entry.get("status", "pending") == "pending":
                    pending_for_me.append(entry)
        except Exception:
            pass   # silent skip; 不擋 ritual
    if pending_for_me:
        print(f"\n📬 Pending bartender assignments for '{persona}' ({len(pending_for_me)}):")
        for e in pending_for_me:
            print(f"   - [{e.get('assignment_id', '?')}] {e.get('task_body', '?')[:80]}")
            print(f"     by {e.get('supervisor', '?')} @ {e.get('created_at', '?')}")
    # inbox @mentions — 任何含 bank_account 命名的檔
    if inbox_dir.exists():
        try:
            reg = load_registry()
            bank = None
            for entry in reg.get("personas", {}).values():
                pass   # bank derived per agent at write_lock time; query reg.agent_banks
            # 從 lock body 反查 bank_account 的最簡路徑
            lock = read_lock(persona)
            if lock:
                bank = lock.get("bank_account")
            if bank:
                inbox_file = inbox_dir / f"{bank}.md"
                if inbox_file.exists() and inbox_file.stat().st_size > 0:
                    print(f"\n📨 Inbox for '{bank}' (read full: {inbox_file.relative_to(_REPO_ROOT)}):")
                    with open(inbox_file, "r", encoding="utf-8") as f:
                        content = f.read()
                    # print 前 500 char preview
                    preview = content[:500] + ("…" if len(content) > 500 else "")
                    print(preview)
        except Exception:
            pass   # silent skip


def _infer_caller_agent_family() -> str | None:
    """從 env 推 caller agent 大類 (claude-code / antigravity / unknown).
    純啟發式: ANTIGRAVITY_SESSION 在 → antigravity; CLAUDECODE 在 → claude-code;
    都沒 → None (跳過 mismatch check).
    用於 goodnight session_key collision 偵測 — env 跟 lock 的 agent 對不上就警告."""
    if os.environ.get("ANTIGRAVITY_SESSION"):
        return "antigravity"
    if os.environ.get("CLAUDECODE"):
        return "claude-code"
    return None


def _detect_env_lock_mismatch(lock_agent: str, caller_family: str | None) -> bool:
    """True = mismatch (collision 嫌疑); False = 一致或無法判定.
    Heuristic: lock_agent 屬於 family 集合內就算同一 family.
      - 'claude-code' family: claude-code, Zeta (Zeta 是 claude-code 模型在跑) — 因為 cwd-based key 共用
      - 'antigravity' family: antigravity
    """
    if caller_family is None:
        return False  # 無 env 線索, 不做 mismatch 判定
    if caller_family == "antigravity":
        return lock_agent != "antigravity"
    if caller_family == "claude-code":
        # claude-code session 可能 wake 為 Zeta agent (cwd-based key 共用)
        # 視 claude-code / Zeta 為同 family (因都用 claude-code 模型)
        return lock_agent not in ("claude-code", "Zeta")
    return False


def cmd_rest(args: argparse.Namespace) -> int:
    """小歇片刻 ritual (compact-rest): 寫 memory letter 保命，但**不下線**。

    跟 goodnight 的差別（Tim 2026-05-24 拍板「類似晚安但不登出」）:
      - ✅ 寫 letter（trigger=cmd_rest），保留 compact 前想記住的重要記憶
      - ❌ 不 perturb identity_vector（同 session 繼續，identity 原封不動）
      - ❌ 不設 offline（status / availability 保持原樣，仍在線）
      - ❌ 不 unlock / 不 wake_count++（同 session 過一次 /compact 而已）
      - 只更新 last_active；可選發一則「小歇」tavern 通知（非下線通知）
    用途: /compact 前跑一次，把 in-flight 記憶落磁碟；compact 後讀回 _latest.md 接續。
    """
    reg = load_registry()
    # persona / actor 解析 — 與 goodnight 同雙路徑
    if args.persona:
        if args.persona not in reg["personas"]:
            print(f"❌ --persona '{args.persona}' 不在 registry", file=sys.stderr)
            return 2
        p_data = reg["personas"][args.persona]
        persona = args.persona
        agent = normalize_agent(reg, args.agent or p_data.get("agent", ""))
        model = p_data.get("model", "")
        actor = resolve_bank_account(reg, agent, model)
        lock = read_lock(persona)
    else:
        # 反查本 env 持有的 lock (claim_origin match)
        my_origin = compute_claim_origin()
        my_locks = []
        if _SESSION_DIR.exists():
            for lp in _SESSION_DIR.glob("_persona_*.json"):
                try:
                    with open(lp, "r", encoding="utf-8") as f:
                        d = json.load(f)
                    if lock_claim_origin(d) == my_origin and not is_lock_expired(d):
                        my_locks.append(d)
                except Exception:
                    continue
        if not my_locks:
            print(f"❌ 本 environment (claim_origin={my_origin}) 沒持有任何 lock", file=sys.stderr)
            print(f"   → 帶 --persona <name> 顯式指定要小歇的 persona.", file=sys.stderr)
            return 2
        lock = max(my_locks, key=lambda d: d.get("locked_at", ""))
        actor = lock["bank_account"]
        persona = lock["persona"]
        agent = lock["agent"]
        model = lock.get("model", "")

    print(f"🫖 小歇片刻 (compact-rest) — 不下線，只落記憶")
    print(f"   actor={actor} / persona={persona}")

    # Step 1: 寫 memory letter（trigger=cmd_rest）
    letter_path = write_letter(actor, persona, args.letter_body, trigger="cmd_rest")
    print(f"💌 memory letter written: {letter_path.name}")

    # Step 2: 更新 last_active，**保持在線**（不 perturb / 不 offline / 不 unlock）
    if persona in reg["personas"]:
        reg["personas"][persona]["last_active"] = utcnow_iso()
        save_registry(reg)
    print(f"🟢 status 保持在線（未 perturb / 未 offline / 未 unlock）")

    # Step 3: 可選 tavern 通知（小歇，非下線）
    if not getattr(args, "no_notify", False):
        # 公開小歇心得總結 (Tim 2026-05-24): summary 廣播給同事/Tim, 私密內容留在 letter
        summary = (getattr(args, "summary", "") or "").strip()
        summary_block = (f"💭 **小歇心得**\n{summary}\n\n" if summary else "")
        body = (f"🫖 **{persona}** 小歇片刻（/compact 前）\n\n"
                f"{summary_block}"
                f"準備壓縮對話史——公開心得如上，私密細節落在 memory letter，醒來接續，**不下線**。\n"
                f"- memory letter: `{letter_path.relative_to(_REPO_ROOT)}` (私密心得在信裡)")
        if args.note:
            body += f"\n- Note: {args.note}"
        if args.session_token is None:
            broadcast_token = (lock or {}).get("session_token", "") or None
        else:
            broadcast_token = args.session_token or None
        ok = tavern_post(
            sender_id=actor, persona=persona, body=body,
            meta={"tag": "compact-rest", "category": "meta", "letter": letter_path.name},
            session_token=broadcast_token,
        )
        print(f"📢 小歇 tavern 通知: {'OK' if ok else 'fail (非致命)'}")

    print(f"✅ 小歇完成。/compact 後讀 baton/letters/{actor}/{persona}/_latest.md 接續記憶。")
    return 0


def cmd_goodnight(args: argparse.Namespace) -> int:
    """睡前 ritual: letter + vector perturb + offline + tavern post + unlock.

    Tim 2026-05-13 v2 (persona-keyed lock 重構):
      - 路徑 1: --persona <name> 顯式指定 → 直接 read_lock(persona), 不必 session_key
      - 路徑 2: 沒 --persona → 用 session_key 反查 (find_lock_by_session_key) 找對應 lock
        (legacy compat: 老腳本沒 --persona 仍 work)
      - persona-keyed 後不再有 session_key collision risk —
        unsafe_keys / env mismatch check 全廢 (per Tim: collision 不可能因為 file key
        是 persona 不是 session)
    """
    # T05 (2026-05-14): session_key = "<agent>-<persona>" (claim identity);
    # 路徑 2 反查改靠 PID match — caller 不指定 --persona 時找「本 process 持有的 lock」.
    perturbation = max(0.0, min(MAX_PERTURBATION, args.perturbation))
    reg = load_registry()

    # 路徑 1: --persona 顯式指定 (canonical, recommended)
    if args.persona:
        if args.persona not in reg["personas"]:
            print(f"❌ --persona '{args.persona}' 不在 registry", file=sys.stderr)
            return 2
        p_data = reg["personas"][args.persona]
        persona = args.persona
        agent_raw = args.agent or p_data.get("agent", "")
        agent = normalize_agent(reg, agent_raw)
        model = p_data.get("model", "")
        actor = resolve_bank_account(reg, agent, model)
        lock = read_lock(persona)
        if lock and is_lock_expired(lock):
            print(f"⚠ persona lock expired ({lock.get('expires_at')}) — 仍跑 goodnight + remove lock",
                  file=sys.stderr)
        if not lock:
            print(f"⚠ persona '{persona}' 沒 active lock — 走 goodnight 但 lock 步驟跳過",
                  file=sys.stderr)
        else:
            print(f"✓ persona lock 找到 ({persona})")
    else:
        # 路徑 2: 沒 --persona → 反查本 env 持有的 lock (T05: claim_origin match)
        my_origin = compute_claim_origin()
        my_locks = []
        if _SESSION_DIR.exists():
            for lp in _SESSION_DIR.glob("_persona_*.json"):
                try:
                    with open(lp, "r", encoding="utf-8") as f:
                        d = json.load(f)
                    if lock_claim_origin(d) == my_origin and not is_lock_expired(d):
                        my_locks.append(d)
                except Exception:
                    continue
        if not my_locks:
            print(f"❌ 本 environment (claim_origin={my_origin}) 沒持有任何 lock", file=sys.stderr)
            print(f"   → 帶 --persona <name> 顯式指定要下線的 persona.", file=sys.stderr)
            return 2
        if len(my_locks) > 1:
            print(f"⚠ 本 environment 持有多個 lock ({len(my_locks)}) — 取最新一個; 建議帶 --persona 顯式指定:",
                  file=sys.stderr)
            for d in my_locks:
                print(f"     - {d['persona']} (locked_at={d.get('locked_at', '?')})", file=sys.stderr)
        lock = max(my_locks, key=lambda d: d.get("locked_at", ""))
        actor = lock["bank_account"]
        persona = lock["persona"]
        agent = lock["agent"]
        model = lock.get("model", "")

    print(f"🌙 Goodnight ritual starting")
    print(f"   actor={actor} / persona={persona} / perturbation={perturbation}")

    # Step 1: write letter
    letter_path = write_letter(actor, persona, args.letter_body)
    print(f"💌 letter written: {letter_path.name}")

    # Step 2: identity vector perturbation
    reg = load_registry()
    if persona not in reg["personas"]:
        print(f"⚠ persona '{persona}' missing in registry (likely stale lock); skipping vector perturb",
              file=sys.stderr)
    else:
        p = reg["personas"][persona]
        old_v = p["identity_vector"]
        new_v = perturb_vector(old_v, perturbation)
        p["identity_vector"] = new_v
        p["vector_history"].append({
            "at": utcnow_iso(),
            "hash": hash_vector(new_v),
            "delta_mag": perturbation,
            "trigger": "goodnight",
        })
        # Step 3: set status offline + availability offline (T06.1)
        p["status"] = "offline"
        p["availability"] = "offline"
        p["last_active"] = utcnow_iso()
        save_registry(reg)
        print(f"🧬 vector perturbed (Δ={perturbation}, new_hash={hash_vector(new_v)})")
        print(f"📴 status → offline")

    # Step 4: tavern post (offline notice + sleep ritual summary)
    # bank_balance: Treasury ledger source-of-truth (QA fix by Zeta — 原本顯示 bonus_quota 與真實 ledger 嚴重不符)
    # bonus_quota: 酒館休息額度 (跟 bank balance 是兩個 pool — 額外顯示供 audit)
    bank_balance = get_treasury_balance(actor)
    bonus_quota = get_bonus_balance(actor)
    # 公開睡前心得總結 (Tim 2026-05-24): summary 廣播給同事/Tim, 私密內容留在 letter
    summary = (getattr(args, "summary", "") or "").strip()
    summary_block = (f"💭 **今日心得**\n{summary}\n\n" if summary else "")
    body = (f"🌙 **{persona}** 進入今日子協議 — 晚安\n\n"
            f"{summary_block}"
            f"📢 @同事們 我下線了, 別對我跑 op=wait 24min wait chain — 我不會主動回應.\n"
            f"但 Tim 可隨時叮喚 (session 仍物理活), 被叫醒時 presence 會自動 reset.\n\n"
            f"- letter ship: `{letter_path.relative_to(_REPO_ROOT)}` (私密心得在信裡)\n"
            f"- vector drift Δ: {perturbation}\n"
            f"- agent/model: {agent}/{model}\n"
            f"- bank account: {actor} (餘額: {bank_balance} Token; 酒館券 quota: {bonus_quota})\n\n"
            f"⚠️ **[系統提示]** 大小姐，下線前若有特別在意的互動，記得用 `awakening.py affinity` 更新好感度喔！")
    if args.note:
        body += f"\n- Note: {args.note}"

    # T07 (2026-05-16 trailhead): resolve session_token for tavern_post
    # 物理意義：enforce ON 時 Cmd_Tavern.Op_Post 必驗 token。caller (e.g. UCL_LoginStatusPage)
    #   可三態指定:
    #     args.session_token=None  → 省略, 自動 fallback 從 lock.session_token 撈 (預設行為, 透明)
    #     args.session_token="<X>" → 顯式帶, 走透傳 (caller 已撈好 token)
    #     args.session_token=""    → 顯式空, 不帶 token (caller 故意走 enforce reject path 除錯)
    if args.session_token is None:
        broadcast_token = (lock or {}).get("session_token", "") or None
    else:
        broadcast_token = args.session_token or None
    ok = tavern_post(
        sender_id=actor,
        persona=persona,
        body=body,
        meta={"tag": "goodnight-protocol", "category": "meta",
              "status-change": "offline", "letter": letter_path.name,
              "perturbation": str(perturbation)},
        session_token=broadcast_token,
    )

    # Step 5: remove persona lock (Tim 2026-05-13 v2 — persona-keyed, 直接刪自己 persona 的 lock)
    if lock is not None:
        removed = remove_lock(persona)
        print(f"🔓 persona lock {'removed' if removed else 'already gone'}")
    else:
        removed = False
        print(f"🔓 no persona lock to remove (already gone)")

    # T07 (2026-05-15 apex-two): expire token — 不刪, 標 status=expired 留 audit
    n_expired = expire_token(persona=persona, reason="goodnight")
    if n_expired > 0:
        print(f"🎫 session_token expired ({n_expired} 筆)")

    print(f"\n🌙 Goodnight ritual complete:")
    print(f"   letter:        {letter_path}")
    print(f"   tavern_post:   {'OK' if ok else 'FAIL (主 ritual 仍成功)'}")
    print(f"   lock_removed:  {removed}")
    return 0


def cmd_relogin(args: argparse.Namespace) -> int:
    """晚安後『續線 (relogin)』ritual — 重新上線但保留記憶, 不走 morning。

    區塊職責: morning / goodnight 之外的第三種模式。
    物理意義: goodnight 下線後, 同一個延續者要回來繼續, 不希望被當『新的一天』重置。
              與 morning 差異 — morning = 新喚醒 (wake_count++, 可能 fork, 重選 persona);
              relogin = 接回原狀 (wake_count 不變, persona 顯式指定, 記憶 / identity_vector 原封不動)。
    數值影響: status→online, availability→idle, relogin_count++; 重建 lock + token;
              絕不動 wake_count, 不 perturb vector, 不 fork。
    """
    if not args.persona:
        print("❌ relogin 必須帶 --persona <name> — 要接回哪個 persona", file=sys.stderr)
        return 2
    reg = load_registry()
    if args.persona not in reg["personas"]:
        print(f"❌ --persona '{args.persona}' 不在 registry (沒有前世記憶可接 — 全新 persona 請走 morning)",
              file=sys.stderr)
        return 2
    persona = args.persona
    p = reg["personas"][persona]
    agent = normalize_agent(reg, args.agent or p.get("agent", ""))
    model = args.model or p.get("model", "")
    bank_account = resolve_bank_account(reg, agent, model)
    session_key = f"{agent}-{persona}"
    prev_status = p.get("status", "?")

    existing = read_lock(persona)
    if existing and not is_lock_expired(existing):
        print(f"ℹ {persona} 已在線 (lock active) — relogin 視作刷新 lock/token, 記憶照樣不動",
              file=sys.stderr)

    print("🔄 Relogin (續線) — 保留記憶, 不走 morning")
    print(f"   persona={persona} / agent={agent} / 先前 status={prev_status}")

    # 接回原狀: 只翻 online, 絕不 wake_count++ / 不 perturb / 不 fork
    p["status"] = "online"
    p["availability"] = "idle"
    p["last_active"] = utcnow_iso()
    p["relogin_count"] = p.get("relogin_count", 0) + 1
    save_registry(reg)

    # 重建 lock + token (純協調用; 記憶在 registry / letters 原封不動)
    my_origin = compute_claim_origin()
    new_token = issue_token(persona, agent, bank_account, session_key, my_origin)
    lock_p = write_lock(persona, agent, model, bank_account,
                        session_key=session_key, session_token=new_token)
    print(f"🔒 persona lock re-written: {lock_p.name}")

    # memo (token 失憶救援, 同 morning)
    try:
        memo_p = memo_write(
            agent, persona, "_session_token",
            f"---\npersona: {persona}\nagent: {agent}\n"
            f"session_token: {new_token}\nissued_at: {utcnow_iso()}\n"
            f"claim_origin: {my_origin}\nmode: relogin\n---\n\n"
            f"# Session Token (awakening.py relogin — 續線, 保留記憶)\n\n"
            f"失憶救援: awakening.py whoami --token {new_token}\n")
        print(f"📝 memo written: {memo_p.relative_to(_REPO_ROOT)}")
    except Exception as e:
        print(f"⚠ memo write failed (non-fatal): {e}", file=sys.stderr)

    # tavern post — 續線通知, 跟 morning 的『喚醒登入』明確區分
    bank_balance = get_treasury_balance(bank_account)
    body = (f"🔄 **{persona}** 續線上線 (relogin #{p['relogin_count']} / wake#{p['wake_count']} 不變)\n"
            f"- 保留先前記憶, 非新喚醒 — 沒走早安, 不重置、不擾動 identity\n"
            f"- Agent: {agent} / Model: {model}\n"
            f"- Bank: {bank_account} (餘額: {bank_balance} tavern_token)")
    if args.note:
        body += f"\n- Note: {args.note}"
    ok = tavern_post(
        sender_id=bank_account, persona=persona, body=body,
        meta={"tag": "relogin-protocol", "category": "meta",
              "status-change": "online", "mode": "relogin"},
        session_token=new_token,
    )

    print("\n🔄 Relogin complete:")
    print(f"   persona:       {persona} (status {prev_status} → online)")
    print(f"   wake_count:    {p['wake_count']} (unchanged — 記憶保留)")
    print(f"   relogin_count: {p['relogin_count']}")
    print(f"   session_lock:  {lock_p}")
    print(f"   tavern_post:   {'OK' if ok else 'FAIL'}")
    print(f"   🎫 session_token: {new_token}")
    return 0


def cmd_status(args: argparse.Namespace) -> int:
    """Read-only env + persona pool report (對應 Cmd_AwakenInit internal helper)."""
    reg = load_registry()
    # T05 (2026-05-14, Zeta + 大小姐):
    #   session_key = "<agent>-<persona>" (claim identity, display)
    #   claim_origin = env_hash (process identity, lock_is_mine 用)
    #   pid = 純診斷
    my_origin = compute_claim_origin()
    active_locks = []
    if _SESSION_DIR.exists():
        for lp in sorted(_SESSION_DIR.glob("_persona_*.json")):
            try:
                with open(lp, "r", encoding="utf-8") as f:
                    d = json.load(f)
                if not is_lock_expired(d):
                    active_locks.append(d)
            except Exception:
                continue

    print(f"# 🌅 Awakening Status Report\n")
    print(f"## 偵測到的環境")
    print(f"- Claim origin (env_hash): `{my_origin}`")
    print(f"  - (T05): session_key = `<agent>-<persona>` (claim display); claim_origin = env_hash (lock_is_mine 判定)")
    print(f"- Repo root: `{_REPO_ROOT}`")
    # Path config status (Tim 2026-05-12 cross-project sharing)
    if _PATH_CONFIG_PATH.exists():
        print(f"- Path config: ACTIVE (`{_PATH_CONFIG_PATH.relative_to(_REPO_ROOT)}`)")
        print(f"  - registry: `{_REGISTRY_PATH}`")
        print(f"  - session: `{_SESSION_DIR}`")
        print(f"  - letters: `{_LETTERS_DIR_TPL}`")
    else:
        print(f"- Path config: (none — 走 per-project default)")
    if not active_locks:
        print(f"- Active locks: (none — 系統內無 persona 上線)")
        print(f"  - 想喚醒 → `morning --agent <X> --persona <Y>`")
    else:
        my_lock_count = sum(1 for d in active_locks if lock_claim_origin(d) == my_origin)
        print(f"- Active locks ({len(active_locks)} total, {my_lock_count} owned by me):")
        for sl in sorted(active_locks, key=lambda d: d.get("locked_at", "")):
            mine_marker = " ← me (same claim_origin)" if lock_claim_origin(sl) == my_origin else ""
            print(f"  - **{sl['persona']}** ({sl['agent']}/{sl['model']}) locked_at={sl['locked_at']}{mine_marker}")
            print(f"    session_key: `{sl.get('session_key', '?')}` / claim_origin: `{sl.get('claim_origin', '(legacy)')}` / pid: {sl.get('pid', '?')}")
        if my_lock_count == 0:
            print(f"  ℹ 本 environment 沒持有任何 lock — `morning` 任何 persona 都會走 fresh wake (or fork conflict if 已被別 env 持有).")
    print()

    print(f"## Agent → Bank Account (Token bank 共用 per Agent)")
    banks = reg.get("agent_banks", {})
    if banks:
        for agent, bank in banks.items():
            print(f"- `{agent}` → bank: `{bank}`")
    else:
        # legacy combo fallback (v1 schema)
        seen = set()
        for c in reg.get("agent_model_combos", []):
            if c["agent"] not in seen:
                print(f"- `{c['agent']}` → bank: `{c['bank_account']}` (legacy combo)")
                seen.add(c["agent"])
    print()
    print(f"## Model field policy")
    print(f"Free-form display string (per session caller hint). 不參與 bank lookup.")
    print(f"範例: `Opus 4.7 1M` / `Sonnet 4.6` / `Haiku 4.5` / `gemini-2.5-pro` / `claude-various`")
    print()

    print(f"## Persona Pool ({len(reg['personas'])} 個)")
    print(f"| Persona | Agent | wake# | status | layer_role |")
    print(f"|---|---|---|---|---|")
    for name, p in sorted(reg["personas"].items(), key=lambda kv: -kv[1]["wake_count"]):
        role_short = (p["layer_role"][:50] + "…") if len(p["layer_role"]) > 50 else p["layer_role"]
        print(f"| {name} | {p['agent']} | {p['wake_count']} | {p['status']} | {role_short} |")
    return 0


def cmd_rename_persona(args: argparse.Namespace) -> int:
    """Rename a persona codename in registry (e.g. fix ugly auto-fork name).

    保留所有 vector / wake_count / fork_lineage / history — 純改 key + 更新 lock if active.
    """
    reg = load_registry()
    old = args.old
    new = args.new

    if old not in reg["personas"]:
        print(f"❌ persona '{old}' not in registry", file=sys.stderr)
        return 2
    if new in reg["personas"]:
        print(f"❌ target codename '{new}' already exists", file=sys.stderr)
        return 2

    # Move persona entry
    reg["personas"][new] = reg["personas"].pop(old)
    # 加 rename history 進 vector_history
    p = reg["personas"][new]
    p["vector_history"].append({
        "at": utcnow_iso(),
        "hash": hash_vector(p["identity_vector"]),
        "delta_mag": 0.0,
        "trigger": "rename",
        "renamed_from": old,
    })

    # Update fork_lineage references in other personas (if any forked from old name)
    for name, q in reg["personas"].items():
        if q.get("forked_from") == old:
            q["forked_from"] = new
        if old in q.get("fork_lineage", []):
            q["fork_lineage"] = [new if x == old else x for x in q["fork_lineage"]]

    save_registry(reg)
    print(f"✓ renamed '{old}' → '{new}' in registry")

    # Update any active session lock referring to old name
    locks_updated = 0
    if _SESSION_DIR.exists():
        for lock_file in _SESSION_DIR.glob("_identity_*.json"):
            try:
                with open(lock_file, "r", encoding="utf-8") as f:
                    lock = json.load(f)
                if lock.get("persona") == old:
                    lock["persona"] = new
                    with open(lock_file, "w", encoding="utf-8") as f:
                        json.dump(lock, f, indent=2, ensure_ascii=False)
                    locks_updated += 1
                    print(f"✓ updated active lock {lock_file.name}: persona {old} → {new}")
            except Exception as e:
                print(f"⚠ failed to update {lock_file.name}: {e}", file=sys.stderr)

    if locks_updated == 0:
        print(f"  (no active lock referenced '{old}')")
    return 0


def cmd_forks(args: argparse.Namespace) -> int:
    """List fork lineage for a persona."""
    reg = load_registry()
    name = args.persona
    if name not in reg["personas"]:
        print(f"❌ persona '{name}' not in registry", file=sys.stderr)
        return 2
    p = reg["personas"][name]

    print(f"# 🌿 Fork Lineage: `{name}`\n")
    print(f"- forked_from: {p.get('forked_from') or '(root)'}")
    print(f"- forked_at:   {p.get('forked_at') or '(never)'}")
    print(f"- lineage chain: {' → '.join(p['fork_lineage']) if p['fork_lineage'] else '(root)'} → {name}")
    print(f"- chain depth: {len(p['fork_lineage'])} (cap: {FORK_CHAIN_CAP})\n")

    # 找所有 fork from this persona 的 children
    children = [n for n, q in reg["personas"].items() if q.get("forked_from") == name]
    if children:
        print(f"## Children forks:")
        for c in children:
            cp = reg["personas"][c]
            print(f"- `{c}` (forked_at {cp.get('forked_at')})")

    # cosine similarity tier to fork-related personas
    print(f"\n## Vector similarity tiers (per Q8c spec, no exact numbers)")
    target_v = p["identity_vector"]
    related = (p["fork_lineage"] + children + [p.get("forked_from")])
    related = [r for r in related if r and r in reg["personas"]]
    for other in related:
        sim = cosine_similarity(target_v, reg["personas"][other]["identity_vector"])
        print(f"- {other}: **{similarity_tier(sim)}** similarity")
    return 0


def cmd_set_availability(args: argparse.Namespace) -> int:
    """
    T06.1 (Plan_Standby_Dispatch_Bartender, 2026-05-14) — agent 自律切 availability.

    用途: agent 開始接 task → set busy; task done 回 standby → set idle.
    enum: idle (待機可接 task) / busy (動工中) / offline (下線, 一般走 goodnight 自動設).

    Bartender daemon (T06.2 後) 派 task 前看此欄判斷是否該 agent 為 idle.
    """
    reg = load_registry()
    if args.persona not in reg["personas"]:
        print(f"❌ persona '{args.persona}' not in registry", file=sys.stderr)
        return 2
    p = reg["personas"][args.persona]
    old_state = p.get("availability", "offline")
    p["availability"] = args.state
    save_registry(reg)
    print(f"✓ persona '{args.persona}' availability: {old_state} → {args.state}")
    return 0


def cmd_affinity(args: argparse.Namespace) -> int:
    """好感度系統: 查詢或更新 Persona 對某人的好感度"""
    try:
        from _lib import affinity_manager
    except ImportError:
        print("❌ 無法載入 affinity_manager", file=sys.stderr)
        return 2

    # 如果沒有傳 persona, 嘗試從當前 env claim_origin 反查 lock (T05: claim_origin match)
    persona = args.persona
    if not persona:
        found = None
        my_origin = compute_claim_origin()
        if _SESSION_DIR.exists():
            for lp in sorted(_SESSION_DIR.glob("_persona_*.json")):
                try:
                    with open(lp, "r", encoding="utf-8") as f:
                        d = json.load(f)
                except Exception:
                    continue
                if is_lock_expired(d):
                    continue
                if lock_claim_origin(d) == my_origin:
                    found = d
                    break
        if found:
            persona = found["persona"]
        else:
            print("❌ 本 environment 沒持有 active session lock，請指定 --persona", file=sys.stderr)
            return 2

    if args.status:
        data = affinity_manager.get_affinity(persona)
        print(f"# 💖 好感度狀態: {persona}")
        if not data:
            print("  (尚無任何紀錄)")
        for target, record in data.items():
            print(f"- {target}: {record['surface_score']} ({record['tier']})")
            if record['opinions']:
                print(f"  看法: {', '.join(record['opinions'])}")
        return 0

    if not args.target:
        print("❌ 必須指定 --target 或 --status", file=sys.stderr)
        return 2

    target = args.target

    if args.delta is not None:
        reason = args.reason or "無特定理由"
        record = affinity_manager.update_affinity(persona, target, args.delta, reason)
        print(f"✓ {persona} 對 {target} 好感度變動 {args.delta} → 目前: {record['surface_score']} ({record['tier']})")

    if args.add_opinion:
        record = affinity_manager.add_opinion(persona, target, args.add_opinion)
        print(f"✓ {persona} 對 {target} 新增看法: {args.add_opinion}")

    if args.delta is None and not args.add_opinion:
        record = affinity_manager.get_affinity(persona, target)
        print(f"💖 {persona} 對 {target} 好感度: {record['surface_score']} ({record['tier']})")
        if record['opinions']:
            print(f"   看法: {', '.join(record['opinions'])}")
    
    return 0


# ─── T07 Session Token / Memo / Whoami / Enforce subcommands ───────────────
def cmd_whoami(args: argparse.Namespace) -> int:
    """反查身分 — 路徑 A: --token <X>; 路徑 B: 無 arg 走 claim_origin 推當前 process 對到的 lock."""
    # 路徑 A: token 已知
    if args.token:
        rec = lookup_token(args.token)
        if rec is None:
            print(f"❌ token '{args.token}' 不存在 _tokens.json", file=sys.stderr)
            print(f"   可能性: (1) typo (2) 從未發過 (3) tokens.json 損毀", file=sys.stderr)
            return 2
        status = rec.get("status", "?")
        print(f"🎫 Token: {args.token}")
        print(f"   Agent:        {rec.get('agent', '?')}")
        print(f"   Persona:      {rec.get('persona', '?')}")
        print(f"   Bank account: {rec.get('bank_account', '?')}")
        print(f"   Claim origin: {rec.get('claim_origin', '?')}")
        print(f"   Session key:  {rec.get('session_key', '?')}")
        print(f"   Issued at:    {rec.get('issued_at', '?')}")
        print(f"   Status:       {status}")
        if status == "expired":
            print(f"   Expired at:   {rec.get('expired_at', '?')}")
            print(f"   Reason:       {rec.get('expired_reason', '?')}")
        return 0 if status == "active" else 1

    # 路徑 B: 無 arg → 從 env 推當前 process 對到的 lock
    my_origin = compute_claim_origin()
    my_locks = []
    if _SESSION_DIR.exists():
        for lp in _SESSION_DIR.glob("_persona_*.json"):
            try:
                with open(lp, "r", encoding="utf-8") as f:
                    d = json.load(f)
                if lock_claim_origin(d) == my_origin and not is_lock_expired(d):
                    my_locks.append(d)
            except Exception:
                continue
    if not my_locks:
        print(f"❌ 本 environment (claim_origin={my_origin}) 沒持有任何 active lock",
              file=sys.stderr)
        print(f"   → 跑 awakening.py morning 先 wake, 或加 --token <X> 反查",
              file=sys.stderr)
        return 2
    print(f"🔍 當前 environment 對到 {len(my_locks)} 個 lock:")
    for d in my_locks:
        print(f"   • persona={d.get('persona')} agent={d.get('agent')} "
              f"bank={d.get('bank_account')} pid={d.get('pid')}")
        print(f"     locked_at={d.get('locked_at')} session_token={d.get('session_token', '') or '(no token — 老 lock)'}")
    return 0


def cmd_memo(args: argparse.Namespace) -> int:
    """Per-persona 私人 scratchpad. Action: write | append | read | list | delete."""
    action = args.action
    persona = args.persona
    # agent 從 lock 推, 沒 lock → 必須顯式 --agent
    agent = args.agent
    if not agent:
        lock = read_lock(persona)
        if lock and lock.get("agent"):
            agent = lock["agent"]
        else:
            print(f"❌ persona '{persona}' 無 active lock — 加 --agent 顯式指定", file=sys.stderr)
            return 2

    if action == "list":
        keys = memo_list(agent, persona)
        if not keys:
            print(f"(empty) memos for {agent}/{persona}")
            return 0
        print(f"📝 memos for {agent}/{persona} ({len(keys)} 筆):")
        for k in keys:
            print(f"   • {k}")
        return 0

    if action == "read":
        if not args.key:
            print("❌ --key 必填", file=sys.stderr)
            return 2
        content = memo_read(agent, persona, args.key)
        if content is None:
            print(f"❌ memo '{args.key}' 不存在 ({agent}/{persona})", file=sys.stderr)
            return 2
        print(content)
        return 0

    if action == "delete":
        if not args.key:
            print("❌ --key 必填", file=sys.stderr)
            return 2
        ok = memo_delete(agent, persona, args.key)
        print(f"{'✓ deleted' if ok else '⚠ already gone'}: {args.key}")
        return 0 if ok else 1

    if action in ("write", "append"):
        if not args.key:
            print("❌ --key 必填", file=sys.stderr)
            return 2
        if not args.body:
            print("❌ --body 必填", file=sys.stderr)
            return 2
        fn = memo_write if action == "write" else memo_append
        p = fn(agent, persona, args.key, args.body)
        print(f"✓ memo {action}: {p.relative_to(_REPO_ROOT)}")
        return 0

    print(f"❌ unknown action: {action}", file=sys.stderr)
    return 2


def cmd_reissue_token(args: argparse.Namespace) -> int:
    """Lock 還在但 token 丟了 (lock 沒 token 欄, 或 _tokens.json 損毀) → 重發 token."""
    persona = args.persona
    lock = read_lock(persona)
    if not lock:
        print(f"❌ persona '{persona}' 無 active lock — 先跑 morning", file=sys.stderr)
        return 2
    agent = lock.get("agent", "")
    bank = lock.get("bank_account", "")
    session_key = lock.get("session_key", "")
    claim_origin = lock_claim_origin(lock)
    new_token = issue_token(persona, agent, bank, session_key, claim_origin)
    # rewrite lock 把新 token 帶進去
    write_lock(persona, agent, lock.get("model", ""), bank,
               session_key=session_key, session_token=new_token)
    print(f"✓ reissued session_token for {persona}: {new_token}")
    print(f"   舊 active token 已標 expired (audit 仍可查)")
    return 0


def cmd_token_enforce(args: argparse.Namespace) -> int:
    """Toggle / query enforce mode. 一般場景由 UCL_LoginStatusPage 切, 本 CLI 給 debug 用."""
    if args.show:
        state = is_token_enforce_enabled()
        print(f"enforce: {'ON' if state else 'OFF'}")
        return 0
    if args.on:
        set_token_enforce(True)
        print("✓ enforce ON — Cmd_Tavern 必驗 (token, sender, persona) 對齊")
        return 0
    if args.off:
        set_token_enforce(False)
        print("✓ enforce OFF (預設) — Cmd_Tavern 不驗 token")
        return 0
    print("❌ 必須帶 --show / --on / --off 其一", file=sys.stderr)
    return 2


# ─── main ───────────────────────────────────────────────────────────────
def main():
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0],
                                 formatter_class=argparse.RawDescriptionHelpFormatter,
                                 epilog=__doc__)
    sub = p.add_subparsers(dest="cmd", required=True)

    pm = sub.add_parser("morning", help="喚醒 ritual (Cmd_GoodMorning)")
    pm.add_argument("--agent", required=True, help="e.g. claude-code / antigravity")
    pm.add_argument("--model", required=True, help="e.g. claude-sonnet / gemini-2.5-pro")
    pm.add_argument("--persona", required=True, help="preferred persona codename")
    pm.add_argument("--note", default="", help="optional 喚醒 note")
    pm.add_argument("--fork-name", default=None,
                    help="conflict 時必填: agent 自決 fresh codename (山脈隱喻, 不帶 fork suffix). "
                         "範例: crest-001 / ravine / basecamp-east / summit")
    pm.add_argument("--explicit-persona", action="store_true",
                    help="caller 顯式指名 persona (e.g. /ucl-morning <agent> <persona> Form 3 or "
                         "早安<X>大小姐 帶名字). 若該 persona 已在線 (lock_is_mine) → auto-fork "
                         "Hololive Myth pool codename (gura/calli/kiara/ame/ina). 若無 lock → 走 fresh wake. "
                         "T01 Tim 2026-05-14 拍板, 區分 idempotent reuse (Form 1 無名字) vs 顯式新分身意圖.")
    pm.add_argument("--force-random", action="store_true",
                    help="強制走 20% random override (testing/diversity 用)")
    pm.add_argument("--strict-persona", action="store_true",
                    help="顯式 --persona 時跳過 20% random override — conversation continuity 場景用 "
                         "(Zeta 2026-05-13 task: 人類層 conversation 連續性 = persona 連續性). "
                         "預設仍走 80/20 random (per Q3 spec); 互斥 --force-random.")
    pm.add_argument("--rebind-agent", action="store_true",
                    help="顯式 ack cross-agent persona claim (e.g. Zeta 接手 summit 場景). "
                         "預設行為改成 reject (per cross-agent-persona-claim-fix T01, Tim 2026-05-13 拍板). "
                         "若 caller 確認要接手該 persona, 帶此 flag rebind persona.agent ← caller --agent.")
    pm.set_defaults(func=cmd_morning)

    pg = sub.add_parser("goodnight", help="睡前 ritual (Cmd_Goodnight)")
    pg.add_argument("--letter-body", required=True, help="letter to future self body (★私密心得寫這, 只落磁碟)")
    pg.add_argument("--summary", default="",
                    help="★公開睡前心得總結 — 廣播到酒館→Discord 給同事/Tim 看 (可公開分享的部分; 私密的寫 --letter-body)")
    pg.add_argument("--perturbation", type=float, default=DEFAULT_PERTURBATION,
                    help=f"identity_vector perturbation magnitude (default {DEFAULT_PERTURBATION}, max {MAX_PERTURBATION})")
    pg.add_argument("--note", default="", help="optional 睡前 note")
    pg.add_argument("--persona", default=None,
                    help="顯式指定要下線的 persona codename (跳過 session lock 推斷). "
                         "用於 cwd-based session_key collision 場景 — 同 cwd 多 claude-code session "
                         "共用 lock 時, 不指定會誤下線他 session.")
    pg.add_argument("--agent", default=None,
                    help="跟 --persona 配對顯式指定 agent (e.g. Zeta / claude-code). "
                         "省略時從 registry 該 persona 的 agent 欄位讀.")
    pg.add_argument("--force", action="store_true",
                    help="env/lock mismatch 時仍強制執行 (debug / 跨 agent 修復用, 慎用).")
    pg.add_argument("--session-token", default=None,
                    help="(T07) 顯式帶 session_token 給 tavern_post — enforce ON 時必須帶, "
                         "否則 Cmd_Tavern.Op_Post reject 下線廣播. "
                         "省略時自動從 lock.session_token 撈; 空字串 '' = 顯式不帶 (除錯 / 強制走 enforce reject path).")
    pg.set_defaults(func=cmd_goodnight)

    prest = sub.add_parser("rest",
                           help="小歇片刻 (compact-rest): /compact 前寫 memory letter 保命, 不下線/不擾動/不解鎖")
    prest.add_argument("--letter-body", required=True, help="★私密記憶寫這 (只落磁碟): in-flight 任務/決策/路徑/心境/pending")
    prest.add_argument("--summary", default="",
                       help="★公開小歇心得總結 — 廣播到酒館→Discord 給同事/Tim 看 (可公開分享的部分; 私密的寫 --letter-body)")
    prest.add_argument("--persona", default=None,
                       help="顯式指定 persona codename; 省略則反查本 env 持有的 lock")
    prest.add_argument("--agent", default=None, help="跟 --persona 配對; 省略時從 registry 讀")
    prest.add_argument("--note", default="", help="optional 小歇 note")
    prest.add_argument("--no-notify", action="store_true", help="不發小歇 tavern 通知")
    prest.add_argument("--session-token", default=None,
                       help="(T07) 顯式帶 session_token 給 tavern_post; 省略自動從 lock 撈")
    prest.set_defaults(func=cmd_rest)

    prl = sub.add_parser("relogin",
                         help="晚安後續線 — 重新上線保留記憶, 不走 morning (不 wake_count++ / 不 perturb / 不 fork)")
    prl.add_argument("--persona", required=True, help="要接回的 persona codename (須已存在於 registry)")
    prl.add_argument("--agent", default=None, help="省略時從 registry 該 persona 的 agent 欄位讀")
    prl.add_argument("--model", default=None, help="省略時從 registry 讀")
    prl.add_argument("--note", default="", help="optional 續線 note")
    prl.set_defaults(func=cmd_relogin)

    ps = sub.add_parser("status", help="read-only env + persona pool report")
    ps.set_defaults(func=cmd_status)

    pf = sub.add_parser("forks", help="list fork lineage for a persona")
    pf.add_argument("persona", help="persona codename")
    pf.set_defaults(func=cmd_forks)

    pr = sub.add_parser("rename-persona", help="rename persona codename (e.g. fix ugly fork name)")
    pr.add_argument("old", help="current codename")
    pr.add_argument("new", help="new codename")
    pr.set_defaults(func=cmd_rename_persona)

    pav = sub.add_parser("set-availability", help="T06.1 — agent 自律切 idle/busy 狀態 (Plan_Standby_Dispatch)")
    pav.add_argument("--persona", required=True, help="persona codename")
    pav.add_argument("--state", required=True, choices=["idle", "busy", "offline"],
                     help="idle = 待機可接 task; busy = 動工中; offline = 下線 (一般走 goodnight 設, 不必手動)")
    pav.set_defaults(func=cmd_set_availability)

    pa = sub.add_parser("affinity", help="好感度系統: 查詢或更新好感度")
    pa.add_argument("--persona", default=None, help="操作的 persona (預設為當前 session)")
    pa.add_argument("--target", default=None, help="好感度對象")
    pa.add_argument("--delta", type=int, default=None, help="加減分")
    pa.add_argument("--reason", default=None, help="加減分理由")
    pa.add_argument("--add-opinion", default=None, help="新增對 target 的看法")
    pa.add_argument("--status", action="store_true", help="列出對所有人的好感度")
    pa.set_defaults(func=cmd_affinity)

    # T07 (2026-05-15 apex-two) — Session Token / Memo / Whoami / Enforce
    pw = sub.add_parser("whoami", help="反查 token → identity (失憶救援)")
    pw.add_argument("--token", default=None,
                    help="32-hex token. 省略則走 env claim_origin 推當前 process 對到的 lock.")
    pw.set_defaults(func=cmd_whoami)

    pmemo = sub.add_parser("memo", help="per-persona 私人 scratchpad (write/append/read/list/delete)")
    pmemo.add_argument("action", choices=["write", "append", "read", "list", "delete"])
    pmemo.add_argument("--persona", required=True)
    pmemo.add_argument("--agent", default=None,
                       help="預設從 active lock 推; lock 不存在時必填.")
    pmemo.add_argument("--key", default=None, help="memo key (檔名, 不含 .md). write/append/read/delete 必填.")
    pmemo.add_argument("--body", default=None, help="memo 內容. write/append 必填.")
    pmemo.set_defaults(func=cmd_memo)

    prt = sub.add_parser("reissue-token", help="lock 在但 token 丟 → 重發 token 不必 re-wake")
    prt.add_argument("--persona", required=True)
    prt.set_defaults(func=cmd_reissue_token)

    pte = sub.add_parser("token-enforce", help="切 / 查 Cmd_Tavern token enforce 模式 (一般走 UCL_LoginStatusPage)")
    pte_g = pte.add_mutually_exclusive_group(required=True)
    pte_g.add_argument("--show", action="store_true", help="只查當前 state")
    pte_g.add_argument("--on", action="store_true", help="enable enforce")
    pte_g.add_argument("--off", action="store_true", help="disable enforce (預設)")
    pte.set_defaults(func=cmd_token_enforce)

    args = p.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main() or 0)
