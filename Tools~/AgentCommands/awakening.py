#!/usr/bin/env python3
"""
T-AWAKE-01 awakening.py — Awakening Init Protocol CLI (MVP Python-only)

設計依據: docs/Plan/Plan_Awakening_Init_Protocol.md

整合三條設計線:
  - Cmd_GoodMorning (init + announce + fork) — subcommand "morning"
  - Cmd_Goodnight (letter + vector perturb + offline) — subcommand "goodnight"
  - Session identity consistency (env-based lock) — Phase 1

子命令:
  morning  --agent X --model Y --persona Z [--note "..."] [--force_random]
              喚醒登入 ritual. fork conflict 自動 detect + 新建命名.
              寫 session lock + 80/20 隨機 + wake_count++ + tavern post.

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


# ─── Registry I/O ───────────────────────────────────────────────────────
def load_registry() -> dict:
    if not _REGISTRY_PATH.exists():
        raise SystemExit(f"❌ registry not found: {_REGISTRY_PATH}")
    with open(_REGISTRY_PATH, "r", encoding="utf-8") as f:
        return json.load(f)


def save_registry(reg: dict) -> None:
    _REGISTRY_PATH.parent.mkdir(parents=True, exist_ok=True)
    tmp = _REGISTRY_PATH.with_suffix(".json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(reg, f, indent=2, ensure_ascii=False)
    os.replace(tmp, _REGISTRY_PATH)


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
        # Claude Code PATH 含 local-agent-mode-sessions/<conv_uuid>/<session_uuid>/bin
        # 這個 session_uuid 在一個 conversation 內穩定, 跨 bash invoke 不變.
        import re
        path = os.environ.get("PATH", "")
        m = re.search(r"local-agent-mode-sessions[/\\]([a-f0-9-]+)[/\\]([a-f0-9-]+)", path)
        if m:
            conv_uuid = m.group(1)[:8]
            sess_uuid = m.group(2)[:8]
            return f"claude-code-{conv_uuid}-{sess_uuid}"
        # PATH 沒命中 (e.g. PATH 被清過 / 自訂 env) → fallback cwd-based
        return f"claude-code-cwd-{cwd_hash}"

    return f"unknown-{cwd_hash}-{os.getppid()}"


def lock_path(session_key: str) -> Path:
    return _SESSION_DIR / f"_identity_{session_key}.json"


def write_lock(session_key: str, agent: str, model: str, persona: str,
               bank_account: str) -> Path:
    _SESSION_DIR.mkdir(parents=True, exist_ok=True)
    now = utcnow_iso()
    expires = (datetime.datetime.utcnow() +
               datetime.timedelta(hours=SESSION_LOCK_TTL_HOURS)).strftime(
        "%Y-%m-%dT%H:%M:%S.") + "000Z"
    data = {
        "session_key": session_key,
        "agent": agent,
        "model": model,
        "persona": persona,
        "bank_account": bank_account,
        "locked_at": now,
        "expires_at": expires,
    }
    p = lock_path(session_key)
    tmp = p.with_suffix(".json.tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2, ensure_ascii=False)
    os.replace(tmp, p)
    return p


def read_lock(session_key: str) -> dict | None:
    p = lock_path(session_key)
    if not p.exists():
        return None
    try:
        with open(p, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return None


def remove_lock(session_key: str) -> bool:
    p = lock_path(session_key)
    if p.exists():
        p.unlink()
        return True
    return False


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
def select_persona(preferred: str, reg: dict, agent: str,
                   rng: random.Random | None = None,
                   force_random: bool = False) -> tuple[str, str]:
    """
    Q3 spec (Tim 2026-05-12 拍板):
      - 不存在 → 100% create new (caller 處理 register)
      - 存在 → 80% use preferred / 20% random override 到 same-agent 其他 persona

    回 (chosen_persona, decision_path) 其中 decision_path ∈
      {"new", "preferred", "override"}
    """
    rng = rng or random
    if preferred not in reg["personas"]:
        return preferred, "new"

    # already exists — apply 20% override
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
    """寫 letter to future self per ucl-letters-to-self skill SOP."""
    letters_dir = _LETTERS_DIR_TPL / actor
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

    # update _latest.md pointer
    latest = letters_dir / "_latest.md"
    with open(latest, "w", encoding="utf-8") as f:
        f.write(frontmatter + body + "\n")
    return path


# ─── Subcommands ────────────────────────────────────────────────────────
def cmd_morning(args: argparse.Namespace) -> int:
    """喚醒 ritual: init + fork check + 80/20 select + lock + tavern post."""
    reg = load_registry()
    agent = args.agent
    model = args.model
    preferred = args.persona
    bank_account = resolve_bank_account(reg, agent, model)
    session_key = compute_session_key()

    print(f"🌅 GoodMorning ritual starting (session_key={session_key})")
    print(f"   Agent={agent} / Model={model} / Bank={bank_account}")
    print(f"   Preferred persona: {preferred}")

    # Step 1: Fork conflict check
    target_persona = preferred
    fork_happened = False
    if preferred in reg["personas"]:
        p = reg["personas"][preferred]
        if p.get("status") == "online":
            print(f"⚠ CONFLICT: '{preferred}' 已在另一 session 上線 — 需 fork")
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

    # Step 2: 80/20 random selection (skip 若剛 fork — fork 是 explicit conflict resolution)
    if fork_happened:
        chosen, decision = target_persona, "fork"
        print(f"✓ using forked '{chosen}' (skip 80/20 — fork is explicit identity intent)")
    else:
        chosen, decision = select_persona(target_persona, reg, agent,
                                           force_random=args.force_random)
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
    save_registry(reg)

    # Step 4: write session lock
    lock_p = write_lock(session_key, agent, model, chosen, bank_account)
    print(f"🔒 session lock written: {lock_p.name}")

    # Step 5: tavern post (announce)
    body = (f"☀️ **{chosen}** 喚醒登入 (wake#{p['wake_count']})\n"
            f"- Agent: {agent} / Model: {model}\n"
            f"- Bank: {bank_account}\n"
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


def cmd_goodnight(args: argparse.Namespace) -> int:
    """睡前 ritual: letter + vector perturb + offline + tavern post + unlock."""
    session_key = compute_session_key()
    lock = read_lock(session_key)
    if lock is None:
        print(f"❌ no active session lock for session_key={session_key}", file=sys.stderr)
        print(f"   → run `morning` subcommand first to lock session identity",
              file=sys.stderr)
        return 2
    if is_lock_expired(lock):
        print(f"⚠ session lock expired ({lock.get('expires_at')}) — 仍跑 goodnight 但 fresh re-lock 建議走 morning",
              file=sys.stderr)

    actor = lock["bank_account"]
    persona = lock["persona"]
    agent = lock["agent"]
    perturbation = max(0.0, min(MAX_PERTURBATION, args.perturbation))

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
    body = (f"🌙 **{persona}** 進入今日子協議\n\n"
            f"📢 @同事們 我下線了, 別對我跑 op=wait 24min wait chain — 我不會主動回應.\n"
            f"但 Tim 可隨時叮喚 (session 仍物理活), 被叫醒時 presence 會自動 reset.\n\n"
            f"- letter ship: `{letter_path.relative_to(_REPO_ROOT)}`\n"
            f"- vector drift Δ: {perturbation}\n"
            f"- agent/model: {agent}/{lock['model']}\n"
            f"- bank account: {actor}")
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

    # Step 5: remove session lock
    removed = remove_lock(session_key)
    print(f"🔓 session lock {'removed' if removed else 'already gone'}")

    print(f"\n🌙 Goodnight ritual complete:")
    print(f"   letter:        {letter_path}")
    print(f"   tavern_post:   {'OK' if ok else 'FAIL (主 ritual 仍成功)'}")
    print(f"   lock_removed:  {removed}")
    return 0


def cmd_status(args: argparse.Namespace) -> int:
    """Read-only env + persona pool report (對應 Cmd_AwakenInit internal helper)."""
    reg = load_registry()
    session_key = compute_session_key()
    lock = read_lock(session_key)

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
    print(f"- Lock: {'ACTIVE → ' + lock['persona'] if lock else '(none — 未喚醒)'}")
    if lock:
        print(f"  - locked_at: {lock['locked_at']}")
        print(f"  - expires_at: {lock['expires_at']}")
        print(f"  - agent/model: {lock['agent']}/{lock['model']}")
        print(f"  - bank: {lock['bank_account']}")
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
    pm.set_defaults(func=cmd_morning)

    pg = sub.add_parser("goodnight", help="睡前 ritual (Cmd_Goodnight)")
    pg.add_argument("--letter-body", required=True, help="letter to future self body")
    pg.add_argument("--perturbation", type=float, default=DEFAULT_PERTURBATION,
                    help=f"identity_vector perturbation magnitude (default {DEFAULT_PERTURBATION}, max {MAX_PERTURBATION})")
    pg.add_argument("--note", default="", help="optional 睡前 note")
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

    args = p.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main() or 0)
