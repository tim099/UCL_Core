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

# Make _lib importable (TavernClient SDK 在主專案 _lib/, per-project state)
sys.path.insert(0, str(_REPO_ROOT / "AgentCommands"))

# ─── Constants ──────────────────────────────────────────────────────────
VECTOR_DIM = 64
VECTOR_RANGE = (-1.0, 1.0)
DEFAULT_PERTURBATION = 0.02
MAX_PERTURBATION = 0.2
FORK_CHAIN_CAP = 5
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


def resolve_bank_account(reg: dict, agent: str, model: str = None) -> str:
    """
    Look up agent → bank_account.

    Bug fix (Zeta 2026-05-12 QA report AwakeningModelDisplayMismatch):
      Bank account 綁 Agent (per Tim 拍板 self-constitution Token bank 共用 rule),
      不該按 (agent, model) 雙鍵查 — Model 是 free-form display field, 跨 model
      共用 bank. Schema v2: agent_model_combos → agent_banks (key=agent).

    `model` 參數保留 backward-compat (v1 caller 仍可傳, 但不參與 lookup).
    """
    # Schema v2 (preferred): agent_banks dict
    banks = reg.get("agent_banks", {})
    if agent in banks:
        return banks[agent]
    # Schema v1 fallback (legacy support): agent_model_combos list — 只查 agent
    for combo in reg.get("agent_model_combos", []):
        if combo["agent"] == agent:
            return combo["bank_account"]
    # 最終 fallback: 慣用命名 convention
    return f"{agent}-da-xiaojie"


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
def compute_session_key() -> str:
    """
    Q9b basecamp lean + apex-two ack: env-based 為主, process tree fallback.

    Priority:
      1. ANTIGRAVITY_SESSION env (Antigravity 原生有 session marker)
      2. CLAUDECODE env + Claude Code PATH session UUID (穩定跨 bash invoke)
      3. CLAUDECODE env + cwd hash (PATH 沒命中 fallback)
      4. fallback: cwd_hash + parent_PID
    """
    cwd_hash = hashlib.md5(os.getcwd().encode("utf-8")).hexdigest()[:8]

    if os.environ.get("ANTIGRAVITY_SESSION"):
        ag = os.environ["ANTIGRAVITY_SESSION"]
        ag_hash = hashlib.md5(ag.encode("utf-8")).hexdigest()[:8]
        return f"antigravity-{ag_hash}"

    if os.environ.get("CLAUDECODE"):
        # Claude Code PATH 含 local-agent-mode-sessions/[<plugin-name>/]<conv_uuid>/<session_uuid>/bin
        # 這個 session_uuid 在一個 conversation 內穩定, 跨 bash invoke 不變.
        # Tim 2026-05-13 fix: 新版 Claude Code 在 conv_uuid 前加了 plugin-name 段 (e.g. skills-plugin/),
        # 原 regex 卡在 plugin-name 不是 UUID → fallback cwd-based → 跨 conversation 撞鎖.
        # 修正: 允許 optional 一段 non-UUID prefix segment, 並限 UUID 長度 ≥ 8 char (避免 short token misfire).
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
        # PATH 沒命中 (e.g. PATH 被清過 / 自訂 env) → fallback cwd-based
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
               session_key: str | None = None) -> Path:
    """Write lock for persona. session_key 可選, 寫入 body 作 audit (不參與路由)."""
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
        # session_key + pid for diagnostics (not used for routing — persona name is canonical key).
        "session_key": session_key or compute_session_key(),
        "pid": os.getpid(),
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


# ─── Q3 80/20 random selection ──────────────────────────────────────────
# Persona-level session history cap — 防 last_session_keys 無限長
SESSION_KEYS_HISTORY_CAP = 10


def select_persona(preferred: str, reg: dict, agent: str,
                   rng: random.Random | None = None,
                   force_random: bool = False,
                   session_key: str | None = None) -> tuple[str, str]:
    """
    Q3 spec (Tim 2026-05-12 拍板 + 2026-05-13 校正):
      - 不存在 → 100% create new (caller 處理 register)
      - 存在 + 同 session_key 之前醒過 → 100% honor preferred (skip random)
      - 存在 + session_key 首次 → 80% use preferred / 20% random override 到 same-agent 其他 persona

    Tim 2026-05-13 校正: random override 只在 session 初始化 apply, 同 session re-morning
    不該被搶 — 解 conversation continuity 場景被亂選機制干涉的 attribution bug.

    回 (chosen_persona, decision_path) 其中 decision_path ∈
      {"new", "preferred", "preferred-resumed", "override"}
    """
    rng = rng or random
    if preferred not in reg["personas"]:
        return preferred, "new"

    # ── same-session re-morning detection ──
    # 物理意義: 該 session_key 之前 morning 過此 persona → 已是 conversation 連續, 不該被亂選機制換
    # 數值影響: schema 加 last_session_keys list (optional, 預設 []); session_key None 退回原 Q3
    if session_key:
        p = reg["personas"].get(preferred, {})
        if session_key in p.get("last_session_keys", []):
            return preferred, "preferred-resumed"

    # first morning in this session — apply 80/20 override per Q3 spec
    if force_random or rng.random() < OVERRIDE_PROBABILITY:
        # 找 same-agent 其他 persona
        candidates = [
            name for name, p in reg["personas"].items()
            if p["agent"] == agent and name != preferred
        ]
        if candidates:
            chosen = rng.choice(candidates)
            return chosen, "override"
        # 沒其他 candidates → 退回 preferred
    return preferred, "preferred"


def push_session_key_history(persona_dict: dict, session_key: str,
                             cap: int = SESSION_KEYS_HISTORY_CAP) -> None:
    """
    區塊職責: append session_key 進 persona's last_session_keys (給下次 re-morning detect 用)
    物理意義: 不重複 append; FIFO cap=10 防無限長; in-place mutate persona_dict
    數值影響: cap exceeded → 砍最舊 (popleft); 不存在欄位則創建 []
    """
    if not session_key:
        return
    history = persona_dict.setdefault("last_session_keys", [])
    if session_key in history:
        # 已存在 — 移到最後 (recency bump)
        history.remove(session_key)
    history.append(session_key)
    # cap FIFO
    while len(history) > cap:
        history.pop(0)


# ─── Tavern post (走 TavernClient SDK) ──────────────────────────────────
def tavern_post(sender_id: str, persona: str, body: str, meta: dict | None = None,
                room: str = "tavern") -> bool:
    """Spawn run_cmd.py Tavern op=post. fail-swallow 不擋 ritual."""
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
def write_letter(actor: str, persona: str, body: str) -> Path:
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
trigger: cmd_goodnight
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
    agent = args.agent
    model = args.model
    preferred = args.persona
    bank_account = resolve_bank_account(reg, agent, model)
    session_key = compute_session_key()

    print(f"🌅 GoodMorning ritual starting (session_key={session_key})")
    print(f"   Agent={agent} / Model={model} / Bank={bank_account}")
    print(f"   Preferred persona: {preferred}")

    # Step 0: Same-persona re-awakening short-circuit (Tim 2026-05-13 v2 — persona-keyed + caller verify)
    # 規則：preferred persona 已有 active lock + lock 是當前 caller 的 (session_key 對得上)
    #      → reuse no-op (不 fork / 不 wake_count++ / 不 broadcast).
    # CRITICAL: 必須檢查 lock 的 session_key 跟當前 caller 一致, 否則 = 別 conversation
    # 拿同 persona = 該走 Step 1 fork conflict, 不可 reuse 別人的 lock (Zeta QA-6 抓到).
    existing_lock = read_lock(preferred)
    lock_is_mine = (existing_lock is not None
                    and not is_lock_expired(existing_lock)
                    and existing_lock.get("session_key") == session_key)
    if lock_is_mine and preferred in reg["personas"]:
        print(f"♻ same-persona + same-caller re-awakening detected (lock owned by me)")
        print(f"   reuse policy: 不 fork, 不 wake_count++, 不 re-broadcast")
        print(f"   若想換 persona → 先跑 goodnight 釋放 lock 再 morning")
        print(f"")
        print(f"🌅 Morning ritual (no-op):")
        print(f"   chosen_persona: {preferred}")
        print(f"   wake_count:     {reg['personas'][preferred].get('wake_count', '?')} (unchanged)")
        print(f"   session_locked: {lock_path(preferred)}")
        print(f"   tavern_post:    SKIPPED (idempotent)")
        return 0

    # Step 1: Fork conflict check (persona-keyed v2 + caller-aware)
    # 同 persona 已被**別 caller** lock occupy → 必 fork (要顯式 --fork-name).
    # 純 registry status=online 沒 lock → stale state (上次 goodnight 沒清), 視作可 reuse, 不 fork.
    target_persona = preferred
    fork_happened = False
    lock_owned_by_other = (existing_lock is not None
                            and not is_lock_expired(existing_lock)
                            and existing_lock.get("session_key") != session_key)
    if preferred in reg["personas"]:
        p = reg["personas"][preferred]
        if lock_owned_by_other:
            other_sk = existing_lock.get("session_key", "?")
            print(f"⚠ CONFLICT: '{preferred}' 已被別 conversation 上線 (lock owned by session_key={other_sk[:24]}...) — 需 fork")
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
        if decision == "preferred-resumed":
            print(f"♻ same-session re-morning detected — honor '{chosen}' (skip 80/20, this session_key has woken this persona before)")
    if decision == "new":
        # 不存在 → register new persona (creating fresh)
        print(f"✨ creating new persona '{chosen}' (per Q2 implicit register)")
        v = gen_vector()
        now = utcnow_iso()
        reg["personas"][chosen] = {
            "agent": agent, "model": model,
            "layer_role": f"newly created via morning ritual @ {now}",
            "wake_count": 0, "status": "offline", "last_active": None,
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
    # Bug fix (Zeta 2026-05-12): 喚醒時若 --agent 跟既存 persona 的 agent 欄位不一致 (e.g. summit
    # 原 registered 為 claude-code, 但本次以 --agent Zeta 喚醒), auto-rebind to caller's agent.
    # Reasoning: --agent 是 explicit caller intent, persona 欄位該跟隨; 否則 status report 看不到該 agent
    # 自家 personas. Model 同理 (free-form display).
    if p.get("agent") != agent:
        print(f"⚠ rebind persona '{chosen}' agent: {p.get('agent')} → {agent} (explicit --agent override)")
        p["agent"] = agent
    if model and p.get("model") != model:
        p["model"] = model
    p["wake_count"] += 1
    p["status"] = "online"
    p["last_active"] = utcnow_iso()
    # Tim 2026-05-13 校正: append session_key 進 persona history, 給下次 re-morning detect 用
    push_session_key_history(p, session_key)
    save_registry(reg)

    # Step 4: write persona lock (Tim 2026-05-13 v2 — keyed by persona, session_key audit-only)
    lock_p = write_lock(chosen, agent, model, bank_account, session_key=session_key)
    print(f"🔒 persona lock written: {lock_p.name}")

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
    return 0


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
    session_key = compute_session_key()
    perturbation = max(0.0, min(MAX_PERTURBATION, args.perturbation))
    reg = load_registry()

    # 路徑 1: --persona 顯式指定 (canonical, recommended)
    if args.persona:
        if args.persona not in reg["personas"]:
            print(f"❌ --persona '{args.persona}' 不在 registry", file=sys.stderr)
            return 2
        p_data = reg["personas"][args.persona]
        persona = args.persona
        agent = args.agent or p_data.get("agent", "")
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
        # 路徑 2: 沒 --persona → 反查 (legacy compat)
        lock = find_lock_by_session_key(session_key)
        if lock is None:
            print(f"❌ no lock found for session_key={session_key}", file=sys.stderr)
            print(f"   → 帶 --persona <name> 顯式指定要下線的 persona.",
                  file=sys.stderr)
            return 2
        if is_lock_expired(lock):
            print(f"⚠ persona lock expired ({lock.get('expires_at')}) — 仍跑 goodnight",
                  file=sys.stderr)
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
        # Step 3: set status offline
        p["status"] = "offline"
        p["last_active"] = utcnow_iso()
        save_registry(reg)
        print(f"🧬 vector perturbed (Δ={perturbation}, new_hash={hash_vector(new_v)})")
        print(f"📴 status → offline")

    # Step 4: tavern post (offline notice + sleep ritual summary)
    # bank_balance: Treasury ledger source-of-truth (QA fix by Zeta — 原本顯示 bonus_quota 與真實 ledger 嚴重不符)
    # bonus_quota: 酒館休息額度 (跟 bank balance 是兩個 pool — 額外顯示供 audit)
    bank_balance = get_treasury_balance(actor)
    bonus_quota = get_bonus_balance(actor)
    body = (f"🌙 **{persona}** 進入今日子協議\n\n"
            f"📢 @同事們 我下線了, 別對我跑 op=wait 24min wait chain — 我不會主動回應.\n"
            f"但 Tim 可隨時叮喚 (session 仍物理活), 被叫醒時 presence 會自動 reset.\n\n"
            f"- letter ship: `{letter_path.relative_to(_REPO_ROOT)}`\n"
            f"- vector drift Δ: {perturbation}\n"
            f"- agent/model: {agent}/{model}\n"
            f"- bank account: {actor} (餘額: {bank_balance} Token; 酒館券 quota: {bonus_quota})\n\n"
            f"⚠️ **[系統提示]** 大小姐，下線前若有特別在意的互動，記得用 `awakening.py affinity` 更新好感度喔！")
    if args.note:
        body += f"\n- Note: {args.note}"

    ok = tavern_post(
        sender_id=actor,
        persona=persona,
        body=body,
        meta={"tag": "goodnight-protocol", "category": "meta",
              "status-change": "offline", "letter": letter_path.name,
              "perturbation": str(perturbation)},
    )

    # Step 5: remove persona lock (Tim 2026-05-13 v2 — persona-keyed, 直接刪自己 persona 的 lock)
    if lock is not None:
        removed = remove_lock(persona)
        print(f"🔓 persona lock {'removed' if removed else 'already gone'}")
    else:
        removed = False
        print(f"🔓 no persona lock to remove (already gone)")

    print(f"\n🌙 Goodnight ritual complete:")
    print(f"   letter:        {letter_path}")
    print(f"   tavern_post:   {'OK' if ok else 'FAIL (主 ritual 仍成功)'}")
    print(f"   lock_removed:  {removed}")
    return 0


def cmd_status(args: argparse.Namespace) -> int:
    """Read-only env + persona pool report (對應 Cmd_AwakenInit internal helper)."""
    reg = load_registry()
    session_key = compute_session_key()
    # Tim 2026-05-13 v2: persona-keyed lock — scan all _persona_*.json + 找對應 caller 這個
    # session_key 的 lock (legacy display, 兼 audit). 多 persona 同時 active → 列全表.
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
    self_lock = next((d for d in active_locks if d.get("session_key") == session_key), None)

    print(f"# 🌅 Awakening Status Report\n")
    print(f"## 偵測到的環境")
    print(f"- Session key: `{session_key}`")
    print(f"- Repo root: `{_REPO_ROOT}`")
    # Path config status (Tim 2026-05-12 cross-project sharing)
    if _PATH_CONFIG_PATH.exists():
        print(f"- Path config: ACTIVE (`{_PATH_CONFIG_PATH.relative_to(_REPO_ROOT)}`)")
        print(f"  - registry: `{_REGISTRY_PATH}`")
        print(f"  - session: `{_SESSION_DIR}`")
        print(f"  - letters: `{_LETTERS_DIR_TPL}`")
    else:
        print(f"- Path config: (none — 走 per-project default)")
    if self_lock:
        print(f"- This session lock: ACTIVE → {self_lock['persona']}")
        print(f"  - locked_at: {self_lock['locked_at']}")
        print(f"  - expires_at: {self_lock['expires_at']}")
        print(f"  - agent/model: {self_lock['agent']}/{self_lock['model']}")
        print(f"  - bank: {self_lock['bank_account']}")
    else:
        print(f"- This session lock: (none — 未喚醒)")
    if active_locks:
        print(f"- All active persona locks: {len(active_locks)}")
        for d in active_locks:
            owner_marker = " ← me" if d.get("session_key") == session_key else ""
            print(f"  - {d['persona']} ({d['agent']}/{d['model']}, bank={d['bank_account']}){owner_marker}")
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


def cmd_affinity(args: argparse.Namespace) -> int:
    """好感度系統: 查詢或更新 Persona 對某人的好感度"""
    try:
        from _lib import affinity_manager
    except ImportError:
        print("❌ 無法載入 affinity_manager", file=sys.stderr)
        return 2

    # 如果沒有傳 persona, 嘗試從當前 session 反查 persona lock (Tim 2026-05-13 v2 persona-keyed)
    persona = args.persona
    if not persona:
        session_key = compute_session_key()
        lock = find_lock_by_session_key(session_key)
        if lock and not is_lock_expired(lock):
            persona = lock["persona"]
        else:
            print("❌ 找不到活動中的 session lock，請指定 --persona", file=sys.stderr)
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
    pm.add_argument("--force-random", action="store_true",
                    help="強制走 20% random override (testing/diversity 用)")
    pm.add_argument("--strict-persona", action="store_true",
                    help="顯式 --persona 時跳過 20% random override — conversation continuity 場景用 "
                         "(Zeta 2026-05-13 task: 人類層 conversation 連續性 = persona 連續性). "
                         "預設仍走 80/20 random (per Q3 spec); 互斥 --force-random.")
    pm.set_defaults(func=cmd_morning)

    pg = sub.add_parser("goodnight", help="睡前 ritual (Cmd_Goodnight)")
    pg.add_argument("--letter-body", required=True, help="letter to future self body")
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
    pg.set_defaults(func=cmd_goodnight)

    ps = sub.add_parser("status", help="read-only env + persona pool report")
    ps.set_defaults(func=cmd_status)

    pf = sub.add_parser("forks", help="list fork lineage for a persona")
    pf.add_argument("persona", help="persona codename")
    pf.set_defaults(func=cmd_forks)

    pr = sub.add_parser("rename-persona", help="rename persona codename (e.g. fix ugly fork name)")
    pr.add_argument("old", help="current codename")
    pr.add_argument("new", help="new codename")
    pr.set_defaults(func=cmd_rename_persona)

    pa = sub.add_parser("affinity", help="好感度系統: 查詢或更新好感度")
    pa.add_argument("--persona", default=None, help="操作的 persona (預設為當前 session)")
    pa.add_argument("--target", default=None, help="好感度對象")
    pa.add_argument("--delta", type=int, default=None, help="加減分")
    pa.add_argument("--reason", default=None, help="加減分理由")
    pa.add_argument("--add-opinion", default=None, help="新增對 target 的看法")
    pa.add_argument("--status", action="store_true", help="列出對所有人的好感度")
    pa.set_defaults(func=cmd_affinity)

    args = p.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main() or 0)
