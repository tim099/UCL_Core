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

  記憶五層 (Tim 2026-07-28 拍板; 見 Docs~/zh-Hant/Workflows/Awakening_Ritual_Workflow.md Step 8):
    見樹 T1   letters/<persona>/_latest.md            昨夜 1 封 (日記/抒發)
    見叢 T1.5 letters/<persona>/_keys_open.md         當期交棒清單 (可勾銷/執行用)
    見林 T2   longterm/wake_<N>-<M>.md                ~10 夜反思濃縮
    見森 T3   longterm/forest/gen_<NNN>_*.md          第 5 份見林起, 跨段縱向敘事 (rolling fold)
    見根 T4   fragments/<type>_<slug>.md + _root_index.md   關鍵記憶片段 (唯一事實來源) + 機械索引
  對應 subcommand:
    consolidate --persona X [--digest-body ...] [--level linzi|forest]
              見林 digest (預設) / 見森 fold (--level forest); 不帶 body = inspect 模式.
              見林寫入後自動: 歸檔當期見叢 + 提示抽 fragment + 檢查見森門檻.
    root-index --persona X        見根: 掃 fragments/ 機械重建 _root_index.md (手改會被覆寫)
    keys --persona X [--add "…"]  見叢: append (隨時可加, 不限儀式) / 列出當期清單
    brief --persona X             重生成 _wake_brief.md (身分+五層記憶+營運層單一文本; morning 自動跑)

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
import re
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
        # 只認「.git 資料夾」為真 repo 根，跳過 submodule 的 .git gitlink 檔。
        # (對齊 _lib/ucl_paths.py 慣例) — 否則從 submodule 內 cwd 跑會誤把
        # UCL_Core submodule 當 host root, state 寫進影子 <submodule>/AgentCommands。
        if (p / ".git").is_dir():
            return p
        p = p.parent
    return None


def _resolve_repo_root() -> Path:
    env_root = os.environ.get("CLAUDE_PROJECT_DIR")
    if env_root and Path(env_root).is_dir():
        return Path(env_root).resolve()
    # walk from cwd first; is_dir 過濾確保跳過 submodule gitlink，命中真主專案 .git 資料夾
    walked = _find_git_root_by_walk(Path.cwd())
    if walked:
        return walked
    walked = _find_git_root_by_walk(_HERE)
    if walked:
        return walked
    return Path.cwd().resolve()


_REPO_ROOT = _resolve_repo_root()

# ─── T-PATH-01 (2026-05-28): AgentCommands 資料根 pointer 檔 ─────
# 區塊職責: 讀 <git-root>/.agentcommands_root.local pointer 檔得資料根 (C# 控制台 Apply 寫入)。
# 物理意義: 兩語言共讀同一 pointer 檔, per-machine (gitignored), 沒 → 預設 git_root/AgentCommands。
def _resolve_agentcommands_data_root(git_root: Path) -> Path:
    pointer = git_root / ".agentcommands_root.local"
    try:
        if pointer.exists():
            content = pointer.read_text(encoding="utf-8").strip()
            if content:
                p = Path(content)
                if p.is_absolute():
                    return p.resolve()
    except Exception:
        pass
    return (git_root / "AgentCommands").resolve()

_DATA_ROOT = _resolve_agentcommands_data_root(_REPO_ROOT)

# ─── Path Config Override (legacy, Tim 2026-05-12 → 2026-05-28 deprecation) ─
# 區塊職責: tavern_paths.json 細粒度 override (registry/session/letters/etc.) — 已被 pointer 檔取代。
# 物理意義: 若殘留檔 → 仍 honor (transition window), 但印一次 deprecation warning,
#          Phase 後續移除。新方案走 pointer 檔 (整個資料根一次 override) + CLI 參數做 ad-hoc。
_PATH_CONFIG_PATH = _REPO_ROOT / "AgentCommands" / "_config" / "tavern_paths.json"
_tavern_paths_deprecation_warned = False


def _warn_tavern_paths_deprecated_once() -> None:
    global _tavern_paths_deprecation_warned
    if _tavern_paths_deprecation_warned:
        return
    _tavern_paths_deprecation_warned = True
    print(
        f"⚠ DEPRECATION: {_PATH_CONFIG_PATH.name} 細粒度 path override 已 deprecated (T-PATH-01)。\n"
        f"   新方案: 控制台改 AgentCommands 資料根 → 寫 <git-root>/.agentcommands_root.local pointer 檔。\n"
        f"   過渡窗口: 仍 honor 既有 tavern_paths.json,但會在後續 Phase 移除。",
        file=sys.stderr,
    )


def _resolve_data_path(default_subpath: str, config_key: str) -> Path:
    """覆寫機制 (T-PATH-01 後): legacy tavern_paths.json 仍 honor (deprecated),
    否則用 pointer-aware 資料根映射 (default_subpath 形如 'AgentCommands/X' → _DATA_ROOT/X)。"""
    if _PATH_CONFIG_PATH.exists():
        try:
            with open(_PATH_CONFIG_PATH, "r", encoding="utf-8") as f:
                cfg = json.load(f)
            override = (cfg.get(config_key) or "").strip()
            if override:
                _warn_tavern_paths_deprecated_once()
                expanded = os.path.expandvars(os.path.expanduser(override))
                p = Path(expanded)
                if not p.is_absolute():
                    p = _REPO_ROOT / p
                return p.resolve()
        except Exception as e:
            print(f"⚠ path config 讀取失敗 ({_PATH_CONFIG_PATH.name}): {e} — fallback pointer/default",
                  file=sys.stderr)
    # 把 default_subpath 「AgentCommands/<sub>」前綴換成 _DATA_ROOT (pointer-aware)
    if default_subpath.startswith("AgentCommands/"):
        return (_DATA_ROOT / default_subpath[len("AgentCommands/"):]).resolve()
    return (_DATA_ROOT / default_subpath).resolve()


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
# best-effort 下線廣播硬上限（2026-07-22 rec 1+3）：goodnight 核心（信/perturb/offline/解鎖）
# 都在廣播前落地，廣播純附帶。用短上限避免 Editor 卡住時阻塞到觸發外層 timeout（summit 遇的 SIGTERM 143）。
GOODNIGHT_BROADCAST_TIMEOUT_SEC = 12.0
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
SESSION_LOCK_TTL_HOURS = 24   # ⚠ 與 Cmd_Tavern.cs PERSONA_LOCK_TTL_HOURS 保持同步 (post 滑動續期用同一 TTL)
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


# 區塊職責：agent → bank 解析改走 UCL_Core 代碼側 _lib/bank_resolver.py 單一 source-of-truth。
# 物理意義：normalize_agent / resolve_bank_account / DEFAULT_AGENT_ALIASES 不再在本檔重複定義，
#           統一由共用模組提供 — 杜絕「awakening 與 canvas 各自維護平行對照表漂移」的
#           identity-layer bug (2026-06-04 canvas 把 Zeta 麾下 persona token 誤扣 claude bank 案)。
# 載入手法：本檔在 module 載入時 sys.path.insert(0, <repo>/AgentCommands)，使裸 `_lib` package
#           名綁到「專案狀態側」repo-root/AgentCommands/_lib (有 __init__.py，含 tavern_client 等)。
#           bank_resolver 住在「代碼側」Tools~/AgentCommands/_lib (本腳本 sibling)，名稱相撞拿不到，
#           故用 importlib 依絕對檔案路徑顯式載入，繞開 `_lib` 名稱遮蔽。
import importlib.util as _ilu  # 顯式檔案路徑載入共用 resolver，避開 _lib package 名稱相撞

# 從本腳本同目錄的 _lib/bank_resolver.py 載入（_HERE 已於檔首解析為本檔所在目錄）
_BANK_RESOLVER_PATH = _HERE / "_lib" / "bank_resolver.py"
_br_spec = _ilu.spec_from_file_location("_ucl_bank_resolver", _BANK_RESOLVER_PATH)
_br_mod = _ilu.module_from_spec(_br_spec)
_br_spec.loader.exec_module(_br_mod)

# 對外維持與舊版相同的模組級名稱，下游 caller (normalize_agent(reg,..) / resolve_bank_account(reg,..)) 不需改
_DEFAULT_AGENT_ALIASES = _br_mod.DEFAULT_AGENT_ALIASES   # backward-compat alias（舊名留著供既有引用）
normalize_agent = _br_mod.normalize_agent               # canonical agent key 正規化
resolve_bank_account = _br_mod.resolve_bank_account      # agent → Treasury bank account


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
    ledger_root = _DATA_ROOT / "Treasury" / "ledger"  # T-PATH-01: 走可 override 資料根
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


# 註（2026-07-31）：select_persona（80/20 自決）與 auto_fork_codename（Myth pool 自動挑名）
#   隨 persona 顯式必填 / explicit-online-fork 廢除一併移除 —— 兩者都是「工具替人選身分」，
#   而身分決定現在一律由使用者顯式給。fork 命名走顯式 --fork-name。

def tavern_post(sender_id: str, persona: str, body: str, meta: dict | None = None,
                room: str = "tavern", session_token: str | None = None,
                timeout: float | None = None) -> bool:
    """Spawn run_cmd.py Tavern op=post. fail-swallow 不擋 ritual.

    session_token (T07): enforce ON 時必帶，否則 Cmd_Tavern reject。caller (e.g. cmd_goodnight)
    從 lock.session_token 撈來透傳即可；None / "" → 不附（enforce OFF 路徑）.

    timeout (2026-07-22): 顯式短上限透傳給 TavernClient。best-effort 廣播（如 goodnight 下線通知）
    應帶短 timeout，避免 Editor 卡住時阻塞到觸發外層呼叫者的 timeout（SIGTERM 143）。
    None → 沿用 TavernClient 預設 60s（morning 等一般 caller）。
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
            timeout=timeout,
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
def _split_author_frontmatter(body: str, machine: dict) -> tuple:
    """把作者自己寫的 frontmatter 從 body 拆出來，回 (去頭的 body, 要併入的額外欄位行)。

    區塊職責：**單一 letter 只該有一份 frontmatter**（Tim 2026-07-31 抓到）。
    物理意義：letter 模板（ucl-letters-to-self 七段格式）教作者自己寫一份 frontmatter，
             而 write_letter 又會再包一層 —— 於是每封信開頭都疊了兩坨幾乎一樣的 header，
             差別只在機器版時間精確到毫秒、作者版多了 session_context / intended_reader。
             不是誰寫錯，是**兩邊都以為自己負責那塊**。
    數值影響：機器欄位（type / actor / written_at / written_by_persona / trigger）以本函式為準；
             作者的其餘欄位（session_context / intended_reader / 自訂欄）原樣併入。
             作者若寫了同名欄且值不同 → 保留成 `<key>_as_written`（**不靜默丟掉他寫的東西**）。
    邊界：body 不以 --- 開頭 → 原樣回傳，extra 為空。
    """
    s = body.lstrip("\n")
    if not s.startswith("---"):
        return body, []
    end = s.find("\n---", 3)
    if end == -1:
        return body, []                      # 有頭無尾 = 不是 frontmatter，別亂切
    block = s[3:end].strip("\n")
    rest = s[end + 4:].lstrip("\n")
    extra = []
    for line in block.split("\n"):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        k, sep, v = line.partition(":")
        if not sep:
            continue
        k, v = k.strip(), v.strip()
        if k in machine:
            if v and v != str(machine[k]):
                extra.append(f"{k}_as_written: {v}")   # 機器版勝出，但作者版留痕
            continue
        extra.append(f"{k}: {v}")
    return rest, extra


def write_letter(actor: str, persona: str, body: str, trigger: str = "cmd_goodnight") -> Path:
    """寫 letter to future self per ucl-letters-to-self skill SOP.

    Letter binding 鐵律 (Tim 2026-06-15 拍板, 取代 2026-05-13 kyouko-persona-binding T02):
    letter 是 persona-level subjective reframe — 不同 persona 的 framing 校正不該
    共用同個 _latest.md pointer。binding key 是 **Persona**。
    (原 T02 用 Agent@Persona 雙層, 但 persona 名稱全域唯一, agent 分組層只造成
     actor 命名漂移 — bank-id vs agent-marker vs 重複 suffix 等 bug; 故砍掉 agent 層,
     只留 persona。actor 身分仍記在 frontmatter 作 provenance。)

    Path layout:
        baton/letters/<persona>/<ts>.md   (timestamped, 累積 chain)
        baton/letters/<persona>/_latest.md  (覆寫 pointer)
        baton/letters/<persona>/dialogues/  (round-trip 對話, 留給未來)
    """
    letters_dir = _LETTERS_DIR_TPL / persona
    letters_dir.mkdir(parents=True, exist_ok=True)

    ts = utcnow_compact()
    path = letters_dir / f"{ts}.md"
    # 機器欄位（provenance）— 這幾個以本函式為準，作者寫的同名欄不採用
    machine = {
        "type": "letter_to_future_self",
        "actor": actor,
        "written_at": utcnow_iso(),
        "written_by_persona": persona,
        "trigger": trigger,
    }
    body, extra = _split_author_frontmatter(body, machine)
    fm_lines = [f"{k}: {v}" for k, v in machine.items()] + extra
    frontmatter = "---\n" + "\n".join(fm_lines) + "\n---\n\n"
    with open(path, "w", encoding="utf-8") as f:
        f.write(frontmatter + body + "\n")

    # update _latest.md pointer (per-persona, 不會被別 persona 覆蓋)
    latest = letters_dir / "_latest.md"
    with open(latest, "w", encoding="utf-8") as f:
        f.write(frontmatter + body + "\n")
    return path


# ─── Long-term memory consolidation (Tim 2026-06-15 拍板) ──────────────────
# 三層記憶 (同構 reading-library 章→arc→卷 / [[ucl-letters-to-self]]):
#   T1 episodic — letters/<persona>/<ts>.md + _latest.md (每晚 goodnight letter,樹)
#   T2 長期記憶 — letters/<persona>/longterm/wake_<N>-<M>.md (一段期間反思濃縮,林)
# morning overdue 檢查: gap = wake_count - last_consolidated_wake;
#   gap >= 門檻(預設10,agent 可在 fork/重大 reframe 等節點自決提前) → 喚醒流程內補整理。
# 濃縮 body 由 agent 反思寫成(不是機械貼信),工具負責持久化 + 更新 pointer(同 write_letter 分工)。
DEFAULT_CONSOLIDATION_THRESHOLD = 10


def longterm_dir(persona: str) -> Path:
    """T2 長期記憶 digest 目錄: letters/<persona>/longterm/"""
    return _LETTERS_DIR_TPL / persona / "longterm"


def _read_frontmatter_field(path: Path, field: str) -> str:
    """從 letter/digest md 的 --- frontmatter 抓某欄(written_at / trigger / span_wake 等)。失敗回 ''。"""
    try:
        with open(path, "r", encoding="utf-8") as f:
            head = f.read(1200)
    except Exception:
        return ""
    m = re.search(rf"^{re.escape(field)}:\s*(.+)$", head, re.MULTILINE)
    return m.group(1).strip() if m else ""


def list_episodic_letters(persona: str, since_iso: str | None = None) -> list:
    """列 persona 頂層 episodic letters(排除 _latest/_index 與子夾 dialogues/longterm),
    依 written_at 升冪;since_iso 給定則只取 written_at > since_iso 的(本段待濃縮)。"""
    d = _LETTERS_DIR_TPL / persona
    if not d.exists():
        return []
    items = []
    for p in d.iterdir():
        if not p.is_file() or p.suffix != ".md":
            continue
        if p.name in ("_latest.md", "_index.md"):
            continue
        wa = _read_frontmatter_field(p, "written_at")
        if since_iso and wa and wa <= since_iso:
            continue
        items.append((wa or p.name, p))
    items.sort(key=lambda t: t[0])
    return [p for _, p in items]


def latest_longterm_digest(persona: str) -> Path | None:
    """該 persona 最新一篇 T2 digest(給 morning『見林』+ fork 初醒讀母用)。"""
    d = longterm_dir(persona)
    if not d.exists():
        return None
    digs = sorted(d.glob("wake_*.md"))
    return digs[-1] if digs else None


def consolidation_status(persona: str, reg: dict,
                         threshold: int = DEFAULT_CONSOLIDATION_THRESHOLD) -> dict:
    """算 persona 的長期記憶整理狀態(overdue / span / 待濃縮信件)。"""
    p = reg["personas"].get(persona, {})
    wake = p.get("wake_count", 0)
    last_c = p.get("last_consolidated_wake", 0) or 0
    last_at = p.get("last_consolidated_at")
    # 欄位缺失時改問磁碟：digest 檔名 wake_<start>-<end>.md 才是既成事實，
    # persona json 的這兩欄只是快取 —— 而它已經證明會掉（2026-07-31：letters 同步了、
    # personas/ 沒同步，於是 kiara/basecamp 的欄位歸零，但 digest 檔好端端躺在那）。
    # 不自癒的話：gap 從 0 起算 → 立刻 OVERDUE → 逼人重做已經做過的濃縮。
    if not last_c:
        digs = list_digests(persona)
        if digs:
            m = re.search(r"wake_(\d+)-(\d+)", digs[-1].name)
            if m:
                last_c = int(m.group(2))
                # digest 的日期欄叫 consolidated_at（不是 written_at）——
                # 用錯欄名會讓 last_at 留 None，pending_letters 就退化成「列出全部信」。
                last_at = last_at or _read_frontmatter_field(digs[-1], "consolidated_at") or None
    return {
        "wake_count": wake,
        "last_consolidated_wake": last_c,
        "last_consolidated_at": last_at,
        "gap": wake - last_c,
        "overdue": (wake - last_c) >= threshold,
        "threshold": threshold,
        "span_start": last_c + 1,
        "span_end": wake,
        "pending_letters": list_episodic_letters(persona, since_iso=last_at),
    }


def write_longterm_digest(persona: str, reg: dict, body: str,
                          span_start: int, span_end: int) -> Path:
    """寫 T2 長期記憶 digest + 重建 _index.md + 更新 persona.last_consolidated_wake/at。"""
    d = longterm_dir(persona)
    d.mkdir(parents=True, exist_ok=True)
    ts = utcnow_iso()
    fm = (f"---\n"
          f"type: longterm_memory_digest\n"
          f"persona: {persona}\n"
          f"span_wake: {span_start}-{span_end}\n"
          f"consolidated_at: {ts}\n"
          f"---\n\n")
    path = d / f"wake_{span_start:03d}-{span_end:03d}.md"
    with open(path, "w", encoding="utf-8") as f:
        f.write(fm + body + "\n")
    # 重建 _index.md(掃全部 digest,append-friendly)
    idx_lines = [f"# Long-term memory index — {persona}", ""]
    for dg in sorted(d.glob("wake_*.md")):
        idx_lines.append(f"- [{dg.name}]({dg.name}) — wake {_read_frontmatter_field(dg, 'span_wake')} "
                         f"@ {_read_frontmatter_field(dg, 'consolidated_at')}")
    with open(d / "_index.md", "w", encoding="utf-8") as f:
        f.write("\n".join(idx_lines) + "\n")
    # 更新 persona 欄位(source-of-truth: registry)
    if persona in reg.get("personas", {}):
        reg["personas"][persona]["last_consolidated_wake"] = span_end
        reg["personas"][persona]["last_consolidated_at"] = ts
        save_registry(reg)
    return path


def _print_longterm_memory_block(reg: dict, persona: str, p: dict,
                                 threshold: int = DEFAULT_CONSOLIDATION_THRESHOLD) -> None:
    """morning 結尾印長期記憶讀取指引 + overdue 提醒(skill 引導 agent 動作)。

    2026-07-28：記憶升為五層後，主動作改成「讀一份 wake brief」——本函式仍印各層原檔路徑
    當 fallback（brief 生成失敗 / 想直接看原檔時用），但 agent 的預設動作只需 Read brief。
    """
    st = consolidation_status(persona, reg, threshold)
    # §0 身分 + 見根/見叢/見森/見林/見樹 + §7-9 營運層，彙整成單一可直讀文本（機械生成，手改會被覆寫）
    try:
        write_root_index(persona)                      # 先刷新見根索引（brief 會 inline 它）
        brief = write_wake_brief(persona, reg, p, threshold)
        print(f"\n## 📖 記憶接續 — 讀這一份就好")
        print(f"   → `{brief.relative_to(_REPO_ROOT)}`  "
              f"(§0 身分 → §1-6 記憶 → §7-9 營運; 每次 morning 重生成)")
        part2 = brief.parent / "_wake_brief_part2.md"
        if part2.exists():
            print(f"   ↳ 續讀檔(超出主檔上限已分檔, 視情況再讀): `{part2.relative_to(_REPO_ROOT)}`")
    except Exception as e:
        print(f"\n## 📖 記憶接續 — ⚠ wake brief 生成失敗({e}); 改讀下列原檔")
    print(f"\n## 🧠 長期記憶原檔 (fallback)")
    # 見林: 最新 digest
    latest_dg = latest_longterm_digest(persona)
    if latest_dg is not None:
        print(f"   見林 → 讀最新長期記憶: `{latest_dg.relative_to(_REPO_ROOT)}`")
        print(f"          (完整列表見 `{(longterm_dir(persona) / '_index.md').relative_to(_REPO_ROOT)}`)")
    else:
        print(f"   見林 → (尚無長期記憶 digest;wake 累積到門檻會提示整理)")
    # 見樹: 昨夜 letter
    latest_letter = _LETTERS_DIR_TPL / persona / "_latest.md"
    if latest_letter.exists():
        print(f"   見樹 → 讀昨夜 letter: `{latest_letter.relative_to(_REPO_ROOT)}`")
    # fork 初醒讀母 persona 最新 digest 一次(Tim 拍板)
    parent = p.get("forked_from")
    if parent and p.get("wake_count", 0) == 1:
        parent_dg = latest_longterm_digest(parent)
        if parent_dg is not None:
            print(f"   🧬 fork 初醒 → 額外讀母 persona '{parent}' 最新長期記憶接血統: "
                  f"`{parent_dg.relative_to(_REPO_ROOT)}`")
    # overdue 提醒
    if st["overdue"]:
        print(f"   ⚠ 長期記憶整理 OVERDUE: gap={st['gap']} (門檻 {threshold}); "
              f"上次整理到 wake {st['last_consolidated_wake']}, 現在 wake {st['wake_count']}")
        print(f"     → 整理本段 wake {st['span_start']}-{st['span_end']} ({len(st['pending_letters'])} 封 episodic):")
        print(f"       awakening.py consolidate --persona {persona}   # 先看清單+讀信")
        print(f"       awakening.py consolidate --persona {persona} --digest-body \"<反思濃縮>\"  # 寫入")
    else:
        print(f"   ✓ 長期記憶整理進度: gap={st['gap']}/{threshold} (上次到 wake {st['last_consolidated_wake']})")


# ─────────────────────────────────────────────────────────────────────────
# 見根 / 見叢 / 見森 — 記憶五層 T3~T5 (Tim 2026-07-28 拍板, 討論串 tavern #13786-13801)
#
# 區塊職責：把「記憶」拆成 5 個各有明確職責的層，並讓 morning 只需讀一份彙整文本。
#   見樹 T1  letters/<persona>/_latest.md           昨夜 1 封（日記，抒發用）
#   見叢 T1.5 letters/<persona>/_keys_open.md        當期交棒清單（checkbox，執行用）
#   見林 T2  letters/<persona>/longterm/wake_N-M.md  10 夜濃縮（既有）
#   見森 T3  letters/<persona>/longterm/forest/      第 5 份見林起，跨段縱向敘事（rolling fold）
#   見根 T4  letters/<persona>/fragments/            關鍵記憶片段 + 機械生成索引 _root_index.md
#
# 物理意義（防漂移的核心）：fragment 檔是**唯一事實來源**，內容寫一次之後不再改寫；
#   樹/叢/林/森/索引全部只是視圖。折疊(fold)因此變成「集合聯集 + 重排」而非「重寫散文」，
#   避免 rolling summary 的傳話遊戲式漂移（summit 2026-07-27 判定官拍磚點）。
# 數值影響：見根索引與 wake brief 皆為機械生成產物 → 可隨時重建、可 diff、可寫回歸測試；
#   手改會被下次生成覆寫（檔頭已標）。
# ─────────────────────────────────────────────────────────────────────────
FOREST_DIGEST_THRESHOLD = 5      # 第 N 份見林起開始折見森（digest 計數，非 wake 計數）
ROOT_INDEX_SHOW_LIMIT = 12       # 見根索引「必讀」區塊顯示上限；其餘明說隱藏筆數（禁靜默截斷）
# （BRIEF_LINE_CAP / BRIEF_CATCHUP_COUNT 已隨 wake brief 生成搬到 wake_brief.py；
#   本檔下方保留 BRIEF_LINE_CAP 別名供 cmd_brief 顯示用，避免兩處各定義一份會漂的數字）
FRAG_TYPE_ORDER = ["lesson", "unsolved", "relation", "identity", "philosophy"]


def fragments_dir(persona: str) -> Path:
    """T4 見根: 關鍵記憶片段目錄。"""
    return _LETTERS_DIR_TPL / persona / "fragments"


def root_index_path(persona: str) -> Path:
    return fragments_dir(persona) / "_root_index.md"


def forest_dir(persona: str) -> Path:
    """T3 見森: 放在 longterm/forest/ 子夾 — 刻意不與 longterm/wake_*.md 同層,
    否則 latest_longterm_digest() 的 glob("wake_*.md") 會誤抓見森當見林 pointer。"""
    return longterm_dir(persona) / "forest"


def keys_open_path(persona: str) -> Path:
    """T1.5 見叢: 當期開放中的交棒清單（見林寫入時歸檔並重開）。"""
    return _LETTERS_DIR_TPL / persona / "_keys_open.md"


def keys_archive_dir(persona: str) -> Path:
    return _LETTERS_DIR_TPL / persona / "keys"


def parse_fragment(path: Path) -> dict:
    """讀單一 fragment 的 frontmatter → dict（含 origins 筆數當 fallback recurrence）。

    數值影響：只 parse frontmatter、不讀正文 → 索引生成成本 O(檔數) 且極輕。
    """
    try:
        text = path.read_text(encoding="utf-8")
    except Exception:
        return {}
    m = re.match(r"^---\n(.*?)\n---", text, re.S)
    if not m:
        return {}
    fm = {}
    for line in m.group(1).split("\n"):
        mm = re.match(r"^(\w+):\s*(.*)$", line)
        if mm:
            fm[mm.group(1)] = mm.group(2).strip()
    fm["_origin_count"] = len(re.findall(r"^\s*-\s*\{\s*by:", m.group(1), re.M))
    fm["_path"] = path
    fm.setdefault("id", path.stem)
    return fm


def load_fragments(persona: str) -> list:
    """列該 persona 全部 fragment（排除底線開頭的產物檔如 _root_index.md）。"""
    d = fragments_dir(persona)
    if not d.exists():
        return []
    out = []
    for p in sorted(d.glob("*.md")):
        if p.name.startswith("_"):
            continue
        fm = parse_fragment(p)
        if fm:
            out.append(fm)
    return out


def _frag_sort_key(f: dict):
    """排序：踩過次數(recurrence)降冪 → type 群組 → id（穩定）。
    物理意義：次數本身就是資訊 — 踩 9 次的教訓該排在最上面。"""
    try:
        rec = int(f.get("recurrence", f.get("_origin_count", 1)) or 1)
    except Exception:
        rec = 1
    ti = FRAG_TYPE_ORDER.index(f["type"]) if f.get("type") in FRAG_TYPE_ORDER else 99
    return (-rec, ti, f.get("id", ""))


def render_root_index(persona: str, show_limit: int = ROOT_INDEX_SHOW_LIMIT) -> str:
    """見根索引 — 純機械生成（掃 fragment frontmatter）。

    區塊職責：產出「必讀關鍵記憶」清單文本。
    數值影響：只列 status=open + 踩過次數最多的 3 筆 internalized；closed 不列但不刪檔；
      超過 show_limit 明說隱藏筆數（禁靜默截斷）。
    """
    frags = load_fragments(persona)
    open_rows = [f for f in frags if f.get("status") == "open"]
    intl_rows = [f for f in frags if f.get("status") == "internalized"]
    open_rows.sort(key=_frag_sort_key)
    intl_rows.sort(key=_frag_sort_key)
    shown, hidden = open_rows[:show_limit], max(0, len(open_rows) - show_limit)

    L = ["---", "type: root_index", f"persona: {persona}",
         "generated: mechanical   # 掃 fragments/ frontmatter 產生 — 手改會被下次生成覆寫",
         f"fragment_total: {len(frags)}", "---", "",
         f"# 🌱 見根 — {persona} 必讀關鍵記憶索引", "",
         "> 機械生成 → 零漂移、可隨時重建、可 diff 驗證。事實來源永遠是 fragment 檔本身；",
         "> 見根/樹/叢/林/森都只是視圖。排序＝踩過次數降冪。closed 不列但不刪檔。", "",
         f"## 必讀（status: open，{len(open_rows)} 筆）", "",
         "| 次數 | 類型 | 關鍵記憶 | 涉及層 | 檔案 |", "|---|---|---|---|---|"]
    for f in shown:
        L.append(f"| **{f.get('recurrence', f['_origin_count'])}** | {f.get('type', '?')} | "
                 f"{f.get('title', f['id'])} | {f.get('layers', '') or '—'} | "
                 f"[{f['id']}]({f['id']}.md) |")
    if hidden:
        L += ["", f"⚠ **另有 {hidden} 筆 open 未顯示**（顯示上限 {show_limit}）— 全清單見本目錄。"]
    L += ["", "## 已內化（status: internalized，取踩過次數最多的 3 筆）", ""]
    for f in intl_rows[:3]:
        L.append(f"- ✅ {f.get('title', f['id'])}（踩過 "
                 f"{f.get('recurrence', f['_origin_count'])} 次）→ [{f['id']}]({f['id']}.md)")
    if len(intl_rows) > 3:
        L.append(f"- …另有 {len(intl_rows) - 3} 筆已內化（不列，避免洗版；見本目錄）")
    shared = [f for f in frags if f.get("visibility") == "shared"]
    L += ["", "## 共享狀態", "",
          f"- shared（可被其他 persona / 外部 reference）：{len(shared)} 筆",
          f"- private：{len(frags) - len(shared)} 筆"]
    return "\n".join(L) + "\n"


def write_root_index(persona: str) -> Path | None:
    """生成/覆寫見根索引；無 fragment 時不建檔（回 None）。"""
    if not load_fragments(persona):
        return None
    d = fragments_dir(persona)
    d.mkdir(parents=True, exist_ok=True)
    path = root_index_path(persona)
    path.write_text(render_root_index(persona), encoding="utf-8")
    return path


# ─── 見叢 (T1.5) — 當期交棒清單 ─────────────────────────────────────────
def keys_entries(persona: str) -> tuple:
    """回 (未勾銷 list[str], 已勾銷 list[str])；解析 `- [ ]` / `- [x]` 行。"""
    p = keys_open_path(persona)
    if not p.exists():
        return [], []
    todo, done = [], []
    for line in p.read_text(encoding="utf-8").split("\n"):
        s = line.strip()
        if s.startswith("- [ ]"):
            todo.append(s[5:].strip())
        elif s.startswith("- [x]") or s.startswith("- [X]"):
            done.append(s[5:].strip())
    return todo, done


def keys_append(persona: str, items: list) -> Path:
    """append 交棒事項到當期見叢（隨時可加，不限儀式 — summit 2026-07-27 拍板：
    斷線風險最高的正是「沒走到任何儀式就掛掉」的場景）。"""
    p = keys_open_path(persona)
    p.parent.mkdir(parents=True, exist_ok=True)
    if not p.exists():
        p.write_text(
            "---\ntype: keys_open\npersona: %s\nopened_at: %s\n---\n\n"
            "# 🌿 見叢 — 當期交棒清單（跨夜 append-only，見林時歸檔）\n\n"
            "> 給明天的自己**執行**用（可勾銷）；抒發與敘事寫進 letter，不寫這裡。\n\n"
            % (persona, utcnow_iso()), encoding="utf-8")
    with open(p, "a", encoding="utf-8") as f:
        for it in items:
            f.write(f"- [ ] {it}  <!-- {utcnow_iso()} -->\n")
    return p


def keys_archive(persona: str, span_start: int, span_end: int) -> Path | None:
    """見林寫入時把當期見叢歸檔成 keys/wake_<N>-<M>.md 並重開空的當期檔。
    物理意義：叢的窗口與見林窗口同步開關 → 天然不會無限長。"""
    p = keys_open_path(persona)
    if not p.exists():
        return None
    ad = keys_archive_dir(persona)
    ad.mkdir(parents=True, exist_ok=True)
    dest = ad / f"wake_{span_start:03d}-{span_end:03d}.md"
    dest.write_text(p.read_text(encoding="utf-8"), encoding="utf-8")
    p.unlink()
    return dest


# ─── 見森 (T3) — rolling fold ───────────────────────────────────────────
def list_digests(persona: str) -> list:
    d = longterm_dir(persona)
    return sorted(d.glob("wake_*.md")) if d.exists() else []


def list_forests(persona: str) -> list:
    d = forest_dir(persona)
    return sorted(d.glob("gen_*.md")) if d.exists() else []


def latest_forest(persona: str) -> Path | None:
    fs = list_forests(persona)
    return fs[-1] if fs else None


def forest_status(persona: str) -> dict:
    """見森狀態：門檻是否達到 / 是否有新見林未折疊。

    數值影響：首折是唯一的多輸入折疊（讀全部 digest）；之後恆為 2 份輸入
    （上代森 + 新林）→ 成本不隨壽命成長。
    """
    digs, fors = list_digests(persona), list_forests(persona)
    last_gen = len(fors)
    folded_upto = 0
    if fors:
        folded_upto = int(_read_frontmatter_field(fors[-1], "folded_digest_count") or 0)
    return {
        "digest_count": len(digs),
        "forest_count": last_gen,
        "threshold": FOREST_DIGEST_THRESHOLD,
        "eligible": len(digs) >= FOREST_DIGEST_THRESHOLD,
        "folded_digest_count": folded_upto,
        "pending": max(0, len(digs) - folded_upto) if len(digs) >= FOREST_DIGEST_THRESHOLD else 0,
        "overdue": len(digs) >= FOREST_DIGEST_THRESHOLD and folded_upto < len(digs),
        "next_gen": last_gen + 1,
        "digests": digs,
        "latest_forest": fors[-1] if fors else None,
    }


def write_forest(persona: str, body: str) -> Path:
    """寫新一代見森（append-only：舊代全保留，per Tim 拍板）。"""
    st = forest_status(persona)
    d = forest_dir(persona)
    d.mkdir(parents=True, exist_ok=True)
    gen = st["next_gen"]
    span_end = 0
    if st["digests"]:
        m = re.search(r"wake_(\d+)-(\d+)", st["digests"][-1].name)
        span_end = int(m.group(2)) if m else 0
    prev = st["latest_forest"].name if st["latest_forest"] else "(首折)"
    path = d / f"gen_{gen:03d}_wake_001-{span_end:03d}.md"
    fm = (f"---\ntype: forest_digest\npersona: {persona}\ngeneration: {gen}\n"
          f"span_wake: 1-{span_end}\nfolded_digest_count: {st['digest_count']}\n"
          f"folded_from: {prev} + {st['digests'][-1].name if st['digests'] else '-'}\n"
          f"consolidated_at: {utcnow_iso()}\n---\n\n")
    path.write_text(fm + body.strip() + "\n", encoding="utf-8")
    return path


# ─── Wake brief — morning 的單一可直讀文本 ──────────────────────────────
# ─── wake brief（已抽離到 wake_brief.py）────────────────────────────────
# 區塊職責：本檔只保留「呼叫入口」——組裝與排版全在 wake_brief.py（Tim 2026-07-31：這支太肥）。
# 設計取捨：把本模組自己當參數傳過去（sys.modules[__name__]），避免 wake_brief 反過來
#          import awakening 造成循環匯入 / 第二份模組實例（狀態讀寫仍是本檔的地盤）。
# 自我定位再 import：以 script 執行時 sys.path[0] 剛好是本目錄，但**被別的工具 import 時不是**
# （tavern_cmd.py 就會 import_module("awakening")）。不補這兩行 = 換個 cwd 就 ModuleNotFoundError。
_HERE = str(Path(__file__).resolve().parent)
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
import wake_brief as _wb                       # noqa: E402 — 必須在 sys.path 補完之後

BRIEF_LINE_CAP = _wb.BRIEF_LINE_CAP            # 對外沿用舊名（cmd_brief 顯示用）


def build_wake_brief(persona: str, reg: dict, p: dict,
                     threshold: int = DEFAULT_CONSOLIDATION_THRESHOLD) -> tuple:
    return _wb.build_wake_brief(sys.modules[__name__], persona, reg, p, threshold)


def write_wake_brief(persona: str, reg: dict, p: dict,
                     threshold: int = DEFAULT_CONSOLIDATION_THRESHOLD) -> Path:
    return _wb.write_wake_brief(sys.modules[__name__], persona, reg, p, threshold)



# ─── Subcommands ────────────────────────────────────────────────────────
def cmd_morning(args: argparse.Namespace) -> int:
    """喚醒 ritual — persona 顯式必填、agent 由綁定反推、已在線即中斷。

    2026-07-31 Tim 拍板重寫。刪掉的三段舊邏輯與理由：
      - persona 80/20 自決：把身分決定權交給一個「還沒讀記憶的自己」，順序本身就是反的。
      - same-caller reuse no-op：靜默 no-op 跟「真的醒了」長得一樣；改成撞牆並明說「妳已經在線」。
      - explicit-online-fork / --strict-persona / --rebind-agent：
        「顯式打名字 + 已在線」在新規則下是**停**，不是自動生分身；
        其餘旗標都是剛醒的人替自己簽字的旁路。換綁走後台，不從 ritual 開後門。
    """
    reg = load_registry()
    preferred = args.persona
    model = args.model

    # ① persona 必須已註冊 —— 打錯字不該變成「幫你建一個新人格」
    if preferred not in reg.get("personas", {}):
        print(f"❌ persona '{preferred}' 不存在。", file=sys.stderr)
        names = sorted(reg.get("personas", {}).keys())
        print(f"   可選（{len(names)}）: {', '.join(names)}", file=sys.stderr)
        print("   要開新 persona 走後台「🧬 Persona & Agent 管理頁」，或 --fork-name <NEW> 從既有 persona 分出。",
              file=sys.stderr)
        return 2

    # ② agent 一律反推（registry 查表 = 機械事實，不是自決）
    p = reg["personas"][preferred]
    agent = normalize_agent(reg, p.get("agent") or "")
    if not agent:
        print(f"❌ persona '{preferred}' 沒有綁定 agent，無法反推。請從後台補上 agent 歸屬。", file=sys.stderr)
        return 2
    bank_account = resolve_bank_account(reg, agent, model)
    session_key = compute_session_key(agent, preferred)

    print(f"🌅 GoodMorning ritual starting (session_key={session_key})")
    print(f"   Persona={preferred} / Agent={agent}（由綁定反推）/ Model={model} / Bank={bank_account}")

    # ③ 唯一的中斷條件：該 persona 目前是否在線
    #    只看「這個 persona 有沒有人在用」——不比對 claim_origin / pid，
    #    同一個 env 多 persona 並存是常態不是事故（summit 2026-07-31 指出的誤殺風險）。
    #    過期 lock 不自動豁免：那本來就不該發生，由 Tim 從後台登出（Tim 拍板）。
    #    檢查對象是「本次真正要佔用的那個 persona」——帶 --fork-name 時是新分身，不是母體。
    #    （母體在線不該擋 fork：fork 出來的是**另一個** persona，沒有同時登入同一個。）
    occupy = args.fork_name or preferred
    occupy_reg = reg["personas"].get(occupy, {})
    existing_lock = read_lock(occupy)
    if existing_lock is not None or occupy_reg.get("status") == "online":
        print(f"⛔ '{occupy}' 目前在線 —— 同一個 persona 不得同時登入兩次，流程中止。", file=sys.stderr)
        if existing_lock is not None:
            print(f"   lock: session_key={existing_lock.get('session_key', '?')} "
                  f"pid={existing_lock.get('pid', '?')} locked_at={existing_lock.get('locked_at', '?')}"
                  f"{' (已過期)' if is_lock_expired(existing_lock) else ''}", file=sys.stderr)
        else:
            print("   registry status=online 但查無 lock（上次下線沒走完）", file=sys.stderr)
        print("   解法：讓它先下線（後台「登入狀態」頁登出，或該 session 跑 goodnight），再重跑。", file=sys.stderr)
        print("   ⚠ 不要改用別的 persona 名繞過去 —— 那是製造分身，比停下來糟。", file=sys.stderr)
        return 2

    # ④ fork（可選）：以 preferred 為母體開新分身並改喚醒它
    chosen, decision = preferred, "preferred"
    fork_happened = False
    if args.fork_name:
        chosen = fork_persona(reg, source=preferred, target=args.fork_name,
                              agent=agent, model=model)
        fork_happened = True
        decision = "fork"
        print(f"🌱 fork '{preferred}' → '{chosen}' "
              f"(lineage: {' → '.join(reg['personas'][chosen]['fork_lineage'])} → {chosen})")
        # session_key 要跟著換成「實際佔用的那個 persona」——
        # 否則 fork 出來的 lock 會掛著母體的名字，之後查 lock 對不上人。
        session_key = compute_session_key(agent, chosen)

    p = reg["personas"][chosen]

    # Step 3: wake_count++ + set status active
    # 註（2026-07-31）：cross-agent claim 檢查連同 --rebind-agent 一併移除 ——
    #   agent 現在是從 persona.agent 反推的，caller 已經沒有「宣稱錯 agent」的管道，
    #   這個守衛守的是一扇不存在的門。換綁走後台「🧬 Persona & Agent 管理頁」。
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

    # T06.4 的 stdout 版待辦預覽已於 2026-07-31 移除 —— 改由 wake brief §7 落檔（Tim 拍板）。
    # 兩個理由：① stdout 會被 compact 吃掉，落檔的不會；
    #          ② 舊版讀的 inbox 路徑（ChatTavern/inbox/<bank>.md）在 2026-07-24「讀取端收斂」
    #             搬到 rooms/<room>/inbox/ 之後就一直是空目錄，而它 except: pass ——
    #             於是它「什麼都沒印」跟「真的沒待辦」長得一模一樣，靜默了整整一週。
    print(f"   📥 待辦 / 收件匣 / 酒館 catch-up → 見 wake brief §7-§8")

    # T-LongTermMemory (Tim 2026-06-15): 長期記憶讀取指引 + overdue 整理提醒
    # morning 除昨夜 letter(見樹) 也讀近期長期記憶 digest(見林); gap 過門檻則提示補整理。
    _print_longterm_memory_block(reg, chosen, p)
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
        persona = lock["persona"]
        agent = lock["agent"]
        model = lock.get("model", "")
        # #4 read-through (Bank 整合 2026-07-21, calli 接手 kiara #3): 凍結的 bank_account 欄降為純顯示,
        # actor 一律從 lock 的 agent 欄「重算」bank — agent 身分穩、會漂的是 agent→bank 映射, 避免 stale 誤路由。
        # shadow-compare: 重算值 != 凍結值 → log 一行 (漂移偵測、反靜默; 實測目前全 persona 一致, 故直接採重算值)。
        _frozen_bank = lock.get("bank_account")
        actor = resolve_bank_account(reg, normalize_agent(reg, agent), model)
        if _frozen_bank and _frozen_bank != actor:
            print(f"⚠ [bank read-through] lock bank_account={_frozen_bank!r} != 重算 {actor!r} "
                  f"(persona={persona} agent={agent}) — 採重算值(SOT), 凍結欄已 stale。", file=sys.stderr)

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

    print(f"✅ 小歇完成。/compact 後讀 baton/letters/{persona}/_latest.md 接續記憶。")
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
        persona = lock["persona"]
        agent = lock["agent"]
        model = lock.get("model", "")
        # #4 read-through (Bank 整合 2026-07-21, calli 接手 kiara #3): 凍結的 bank_account 欄降為純顯示,
        # actor 一律從 lock 的 agent 欄「重算」bank — agent 身分穩、會漂的是 agent→bank 映射, 避免 stale 誤路由。
        # shadow-compare: 重算值 != 凍結值 → log 一行 (漂移偵測、反靜默; 實測目前全 persona 一致, 故直接採重算值)。
        _frozen_bank = lock.get("bank_account")
        actor = resolve_bank_account(reg, normalize_agent(reg, agent), model)
        if _frozen_bank and _frozen_bank != actor:
            print(f"⚠ [bank read-through] lock bank_account={_frozen_bank!r} != 重算 {actor!r} "
                  f"(persona={persona} agent={agent}) — 採重算值(SOT), 凍結欄已 stale。", file=sys.stderr)

    print(f"🌙 Goodnight ritual starting")
    print(f"   actor={actor} / persona={persona} / perturbation={perturbation}")

    # Step 1: write letter (可跳過)
    # 設計理由 (Tim 2026-06-14 拍板): 手動登出 (UCL_LoginStatusPage) 走 --no-letter 不寫信。
    #   原因 — 手動登出多為 cleanup / 登出失敗重試場景, 信在 ritual 最前面就寫了, 失敗時已累積一堆
    #   無意義的 placeholder 信。letter 是「agent 自決 goodnight 留給未來自己的心得」, 手動 cleanup
    #   不該偽造這種信。real goodnight (agent 自己跑) 仍須帶 --letter-body, 不受影響。
    if getattr(args, "no_letter", False):
        letter_path = None
        print(f"✉ --no-letter: 跳過寫信 (手動登出 / cleanup 不留心得信)")
    else:
        if not (args.letter_body or "").strip():
            print(f"❌ 須帶 --letter-body <心得> (或顯式 --no-letter 跳過寫信)", file=sys.stderr)
            return 2
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
    # letter 行：no-letter 時略過 (手動登出未留信)
    letter_line = (f"- letter ship: `{letter_path.relative_to(_REPO_ROOT)}` (私密心得在信裡)\n"
                   if letter_path is not None else "- letter: (略 — 手動登出未留信)\n")
    body = (f"🌙 **{persona}** 進入今日子協議 — 晚安\n\n"
            f"{summary_block}"
            f"📢 @同事們 我下線了, 別對我跑 op=wait 24min wait chain — 我不會主動回應.\n"
            f"但 Tim 可隨時叮喚 (session 仍物理活), 被叫醒時 presence 會自動 reset.\n\n"
            f"{letter_line}"
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
    # broadcast_token 先從 in-memory lock dict 撈好 — 下面 remove_lock 只刪磁碟檔, 不動此變數,
    # 故即使先解鎖, enforce ON 的下線廣播仍帶得到尚未過期的 token.
    if args.session_token is None:
        broadcast_token = (lock or {}).get("session_token", "") or None
    else:
        broadcast_token = args.session_token or None

    # Step 5 (前移至廣播之前): remove persona lock (Tim 2026-05-13 v2 — persona-keyed, 直接刪自己 persona 的 lock)
    # 設計理由 (summit 2026-06-14 QA fix — Editor↔subprocess 重入死鎖):
    #   UCL_LoginStatusPage 登出走主線程同步 WaitForExit 卡 python; 而 tavern_post 內部 run_cmd 又要
    #   Editor 主線程處理 trigger 才返回 → 兩邊互等死鎖, 撐到 30s WaitForExit timeout 才解開。
    #   原順序 (tavern_post → remove_lock) 在死鎖場景下 remove_lock 被卡在廣播之後 → lock 不刪 → 「卡在登入」。
    #   對稱於 morning (write_lock 先於 tavern_post 故登入看起來正常): 把權威狀態變更 (解鎖) 移到廣播前,
    #   即使廣播死鎖/失敗, lock 早已刪除, Page re-read 即為登出狀態。broadcast_token 已先撈, 不受影響。
    if lock is not None:
        removed = remove_lock(persona)
        print(f"🔓 persona lock {'removed' if removed else 'already gone'}")
    else:
        removed = False
        print(f"🔓 no persona lock to remove (already gone)")

    # Step 4 (後移至解鎖之後): tavern post (offline notice). 廣播是 best-effort —
    # 死鎖/失敗都不影響上面已落地的解鎖。
    # rec 1 (2026-07-22): 帶短 timeout（GOODNIGHT_BROADCAST_TIMEOUT_SEC）— Editor 卡住時快速放棄、
    # 不阻塞到觸發外層呼叫者 timeout（SIGTERM 143）。核心已全落地，廣播成不成不影響 exit code。
    ok = tavern_post(
        sender_id=actor,
        persona=persona,
        body=body,
        meta={"tag": "goodnight-protocol", "category": "meta",
              "status-change": "offline",
              "letter": (letter_path.name if letter_path is not None else ""),
              "perturbation": str(perturbation)},
        session_token=broadcast_token,
        timeout=GOODNIGHT_BROADCAST_TIMEOUT_SEC,
    )
    # rec 3 (2026-07-22): 廣播失敗/逾時 → graceful degradation，吐一行手動補發指令（body 短、單引號包，
    # 免多行/反引號陷阱）。核心已完成，此處只是把 summit 手動做的那步變成 CLI 現成給的一鍵指令。
    if not ok:
        _rc = _HERE / "run_cmd.py"
        _manual = (
            f"python \"{_rc}\" run Tavern --arg op=post --arg room=tavern "
            f"--arg sender_id={actor} --arg persona={persona} "
            f"--arg body='🌙 {persona} 進入今日子協議、下線了 @同事們（goodnight 自動廣播逾時，手動補）' "
            f"--arg meta='tag:goodnight-protocol;category:meta;status-change:offline'"
        )
        print(f"⚠ 下線廣播未發（Editor 未即時回應，已在 {GOODNIGHT_BROADCAST_TIMEOUT_SEC:.0f}s 內放棄不阻塞）。",
              file=sys.stderr)
        print("   核心已完成（信/perturb/offline/解鎖全落地），不必重跑（會 double-perturb）。", file=sys.stderr)
        print("   要補發下線通知，跑：", file=sys.stderr)
        print(f"   {_manual}", file=sys.stderr)

    # T07 (2026-05-15 apex-two): expire token — 不刪, 標 status=expired 留 audit。
    # 必須擺在 tavern_post「之後」: enforce ON 時上面那筆下線廣播要用尚未過期的 token, 先 expire 會被 Cmd_Tavern reject。
    n_expired = expire_token(persona=persona, reason="goodnight")
    if n_expired > 0:
        print(f"🎫 session_token expired ({n_expired} 筆)")

    print(f"\n🌙 Goodnight ritual complete:")
    print(f"   letter:        {letter_path if letter_path is not None else '(none — --no-letter)'}")
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


def cmd_consolidate(args: argparse.Namespace) -> int:
    """長期記憶整理 (T2 digest)。
    兩段式 (同 write_letter 分工: agent 寫 body, 工具持久化):
      1. inspect — 不帶 --digest-body: 印 overdue 狀態 + 列本段待濃縮 episodic letters 給 agent 讀
      2. write   — 帶 --digest-body: 寫 longterm/wake_<N>-<M>.md + 更新 _index + last_consolidated_wake
    """
    reg = load_registry()
    persona = args.persona
    if persona not in reg.get("personas", {}):
        print(f"❌ persona '{persona}' 不存在於 registry", file=sys.stderr)
        return 2

    # ── 見森 (T3) 分支：--level forest ────────────────────────────────
    # 區塊職責：折「上代森 + 最新見林」成新一代森（首折例外：讀全部見林）。
    # 數值影響：門檻＝第 FOREST_DIGEST_THRESHOLD 份見林；輸入恆為 2 份（首折 N 份），成本不隨壽命成長。
    if getattr(args, "level", "linzi") == "forest":
        fst = forest_status(persona)
        if not args.digest_body:
            print(f"# 🌲 見森狀態 — {persona}")
            print(f"- 見林份數: {fst['digest_count']} (門檻 {fst['threshold']} 份)")
            print(f"- 已折世代: gen{fst['forest_count']}"
                  f" (折到第 {fst['folded_digest_count']} 份見林)")
            if not fst["eligible"]:
                print(f"- 狀態: ○ 未達門檻，還差 {fst['threshold'] - fst['digest_count']} 份見林")
                return 0
            print(f"- 狀態: {'⚠ 有 %d 份新見林待折' % fst['pending'] if fst['overdue'] else '✓ 已是最新'}")
            if fst["forest_count"] == 0:
                print(f"- **首折**（唯一的多輸入折疊）→ 讀下列全部見林:")
                for dgp in fst["digests"]:
                    print(f"    - {dgp.relative_to(_REPO_ROOT)}")
            else:
                print(f"- rolling fold → 只讀 2 份輸入:")
                print(f"    - 上代森: {fst['latest_forest'].relative_to(_REPO_ROOT)}")
                print(f"    - 新見林: {fst['digests'][-1].relative_to(_REPO_ROOT)}")
            print(f"\n→ 讀完後寫回（森是**縱向敘事 + fragment 索引指標**，不是見林的串接）:")
            print(f"  awakening.py consolidate --persona {persona} --level forest \\")
            print(f"      --digest-body \"<身分演變軸/坑已內化vs還在踩/關係演變/脊椎收斂/長壽未解線>\"")
            return 0
        if not fst["eligible"]:
            print(f"❌ 見林只有 {fst['digest_count']} 份，未達見森門檻 {fst['threshold']} 份",
                  file=sys.stderr)
            return 2
        fpath = write_forest(persona, args.digest_body)
        print(f"✅ 見森 gen{fst['next_gen']} 寫入: {fpath.relative_to(_REPO_ROOT)}")
        print(f"   folded_digest_count: {fst['digest_count']} (舊世代全保留, append-only)")
        ri = write_root_index(persona)
        if ri:
            print(f"   見根索引已重建: {ri.relative_to(_REPO_ROOT)}")
        return 0

    st = consolidation_status(persona, reg, args.threshold)

    if not args.digest_body:
        # inspect / status 模式
        print(f"# 🧠 長期記憶整理狀態 — {persona}")
        print(f"- wake_count: {st['wake_count']}")
        print(f"- last_consolidated_wake: {st['last_consolidated_wake']} (@ {st['last_consolidated_at'] or '從未整理'})")
        print(f"- gap: {st['gap']} (門檻 {st['threshold']}) → {'⚠ OVERDUE 該整理' if st['overdue'] else 'ok 尚未到門檻'}")
        print(f"- 建議 span: wake {st['span_start']}-{st['span_end']}")
        print(f"- 本段待濃縮 episodic letters ({len(st['pending_letters'])} 封):")
        for lp in st["pending_letters"]:
            print(f"  - {lp.relative_to(_REPO_ROOT)}")
        print(f"\n→ 讀完上列信件後, 反思濃縮成 digest body 寫回:")
        print(f"  awakening.py consolidate --persona {persona} --digest-body \"<跨夜主題/沉澱教訓/關係演變/未解線/一句精華>\" \\")
        print(f"      [--span-start {st['span_start']} --span-end {st['span_end']}]")
        return 0

    # write 模式
    span_start = args.span_start if args.span_start is not None else st["span_start"]
    span_end = args.span_end if args.span_end is not None else st["span_end"]
    if span_end < span_start:
        print(f"❌ span_end({span_end}) < span_start({span_start})", file=sys.stderr)
        return 2
    path = write_longterm_digest(persona, reg, args.digest_body, span_start, span_end)
    print(f"✅ 長期記憶 digest 寫入: {path.relative_to(_REPO_ROOT)}")
    print(f"   span: wake {span_start}-{span_end}")
    print(f"   persona.last_consolidated_wake → {span_end}")
    print(f"   index: {(longterm_dir(persona) / '_index.md').relative_to(_REPO_ROOT)}")

    # 見林寫入後的三個連動（Tim 2026-07-28 拍板：fragment 在見林時抽）
    # ① 見叢歸檔：當期交棒清單與見林窗口同步關閉 → 天然不會無限長
    arch = keys_archive(persona, span_start, span_end)
    if arch is not None:
        print(f"   🌿 見叢已歸檔: {arch.relative_to(_REPO_ROOT)} (當期檔已重置)")
    # ② 提示抽 fragment（內容要 agent 反思寫，工具只負責 schema 與索引）
    print(f"\n   🌱 下一步 — 抽關鍵記憶 fragment（見林時抽，goodnight 保持輕）:")
    print(f"      寫檔到 {fragments_dir(persona).relative_to(_REPO_ROOT)}/<type>_<slug>.md")
    print(f"      type ∈ {FRAG_TYPE_ORDER}；同一教訓再踩到就**追加 origin + bump recurrence**，別開新檔")
    print(f"      每個 origin 標 layer（Syntactic/Identity/Status/Content/Aggregate）+ 當次 context")
    print(f"      寫完跑: awakening.py root-index --persona {persona}   # 機械重建見根索引")
    # ③ 見森門檻檢查
    fst = forest_status(persona)
    if fst["eligible"]:
        print(f"\n   🌲 見森: 見林已達 {fst['digest_count']} 份 (門檻 {fst['threshold']}) → 該折新世代:")
        print(f"      awakening.py consolidate --persona {persona} --level forest")
    else:
        print(f"\n   🌲 見森: 見林 {fst['digest_count']}/{fst['threshold']} 份，未達折疊門檻")
    return 0


def cmd_root_index(args: argparse.Namespace) -> int:
    """見根 (T4): 機械重建必讀索引。產物可隨時重建 → 不需要任何人工 body。"""
    path = write_root_index(args.persona)
    if path is None:
        print(f"○ {args.persona} 尚無 fragment（見林時抽）— 未建索引")
        return 0
    frags = load_fragments(args.persona)
    opens = [f for f in frags if f.get("status") == "open"]
    print(f"✅ 見根索引重建: {path.relative_to(_REPO_ROOT)}")
    print(f"   fragment 總數 {len(frags)}（open {len(opens)} / 其餘 {len(frags) - len(opens)}）")
    return 0


def cmd_keys(args: argparse.Namespace) -> int:
    """見叢 (T1.5): 當期交棒清單 append / list。

    物理意義：交棒清單「隨時可 append、不限儀式」(summit 2026-07-27 拍磚) —
    斷線風險最高的正是沒走到任何儀式就掛掉的場景，撞到未解線就當場丟進來。
    """
    if args.add:
        p = keys_append(args.persona, args.add)
        print(f"✅ 見叢 append {len(args.add)} 條 → {p.relative_to(_REPO_ROOT)}")
    todo, done = keys_entries(args.persona)
    print(f"\n# 🌿 見叢 — {args.persona}（{len(todo)} 未完 / {len(done)} 已完）")
    for t in todo:
        print(f"- [ ] {t}")
    for d in done[-3:]:
        print(f"- [x] {d}")
    if not todo and not done:
        print("(當期無事項)")
    return 0


def cmd_brief(args: argparse.Namespace) -> int:
    """手動重生成 wake brief（morning 會自動生成；改完 fragment 想立刻重讀時用）。"""
    reg = load_registry()
    if args.persona not in reg.get("personas", {}):
        print(f"❌ persona '{args.persona}' 不存在於 registry", file=sys.stderr)
        return 2
    write_root_index(args.persona)
    path = write_wake_brief(args.persona, reg, reg["personas"][args.persona])
    lines = len(path.read_text(encoding="utf-8").split("\n"))
    print(f"✅ wake brief 生成: {path.relative_to(_REPO_ROOT)} ({lines} 行 / 上限 {BRIEF_LINE_CAP})")
    part2 = path.parent / "_wake_brief_part2.md"
    if part2.exists():
        print(f"   ↳ 續讀檔: {part2.relative_to(_REPO_ROOT)}")
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
    # 2026-07-31 Tim 拍板：persona 是唯一身分輸入；agent 一律由 persona 綁定反推，不再是參數。
    # 廢除 --agent / --explicit-persona / --strict-persona / --force-random / --rebind-agent：
    #   前者讓 caller 有機會宣稱錯身分；後四者都是「剛醒的人自己 ack 自己」的旁路。
    #   換綁 agent 走後台「🧬 Persona & Agent 管理頁」，不從 ritual 開後門。
    pm.add_argument("--persona", required=True, help="要喚醒的 persona codename（唯一身分輸入）")
    pm.add_argument("--model", required=True, help="自報型號 e.g. Opus 5 / gemini-2.5-pro")
    pm.add_argument("--note", default="", help="optional 喚醒 note")
    pm.add_argument("--fork-name", default=None,
                    help="以 --persona 為母體 fork 一個新 persona 並喚醒它（fork 流程日後重做）")
    pm.set_defaults(func=cmd_morning)

    pg = sub.add_parser("goodnight", help="睡前 ritual (Cmd_Goodnight)")
    # letter-body 改 optional (Tim 2026-06-14): 配 --no-letter 用 — 未帶 --no-letter 時仍 runtime 強制要 body。
    pg.add_argument("--letter-body", default="", help="letter to future self body (★私密心得寫這, 只落磁碟). 未帶 --no-letter 時必填.")
    pg.add_argument("--no-letter", action="store_true",
                    help="跳過寫信 (手動登出 / cleanup 場景 — UCL_LoginStatusPage 登出走此 flag, 不偽造心得信).")
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

    pcons = sub.add_parser("consolidate", help="長期記憶整理 (T2 digest); 不帶 --digest-body=只列狀態+待濃縮信件")
    pcons.add_argument("--persona", required=True, help="要整理的 persona codename")
    pcons.add_argument("--digest-body", default=None,
                       help="反思濃縮的 digest 內文; 省略 = inspect 模式(列 overdue 狀態 + 本段待濃縮 letters 清單)")
    pcons.add_argument("--span-start", type=int, default=None, help="起 wake# (預設 last_consolidated_wake+1)")
    pcons.add_argument("--span-end", type=int, default=None, help="迄 wake# (預設 現在 wake_count)")
    # 見森 (T3): --level forest 改折「上代森 + 最新見林」; 純加法, 預設仍是見林 (linzi)
    pcons.add_argument("--level", choices=["linzi", "forest"], default="linzi",
                       help="linzi=見林 digest (預設) / forest=見森 fold (第 5 份見林起可用)")

    # 見根 (T4): 機械重建必讀索引 — 產物可隨時重建, 故不需要 body 參數
    prid = sub.add_parser("root-index", help="見根: 掃 fragments/ 機械重建 _root_index.md")
    prid.add_argument("--persona", required=True)
    prid.set_defaults(func=cmd_root_index)

    # 見叢 (T1.5): 交棒清單 append / list — 隨時可加, 不限儀式
    pkeys = sub.add_parser("keys", help="見叢: 當期交棒清單 (append / list)")
    pkeys.add_argument("--persona", required=True)
    pkeys.add_argument("--add", action="append", default=None, help="append 一條交棒事項 (可重複)")
    pkeys.set_defaults(func=cmd_keys)

    # wake brief: 手動重生成 (morning 會自動生成; 這支給「改完 fragment 想立刻重讀」用)
    pbrief = sub.add_parser("brief", help="重生成 wake brief (身分+記憶+營運單一文本)")
    pbrief.add_argument("--persona", required=True)
    pbrief.set_defaults(func=cmd_brief)
    pcons.add_argument("--threshold", type=int, default=DEFAULT_CONSOLIDATION_THRESHOLD,
                       help=f"overdue 門檻 (預設 {DEFAULT_CONSOLIDATION_THRESHOLD})")
    pcons.set_defaults(func=cmd_consolidate)

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
