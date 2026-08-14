#!/usr/bin/env python3
"""
session_common.py — session 型 CLI 的共用底層 helpers

# 區塊職責：時間戳 / 原子寫檔 / 酒館發言 / 薪資結算 / persona 解析等跨 session 模式共用工具。
# 物理意義：這些 helper 原本住在 work_session.py，2026-07-29 上班模式退役刪檔時，
#          唯一還活著的消費者 stream_watch_session.py 會跟著壞 → 抽成獨立模組。
#          抽出的是「工具」不是「上班語意」— 派工 / 薪資費率 / phantom-payroll 那套沒有帶過來。
# 數值影響：純函式庫，import 無副作用（除了解析 repo root 與載入 bank resolver）。
"""
from __future__ import annotations
import datetime
import json
import os
import subprocess
import sys
import time
import uuid
from pathlib import Path

# _HERE = <UCL_Core>/Tools~/AgentCommands（本檔在其下的 _lib/）
_HERE = Path(__file__).resolve().parent.parent

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
    walked = _find_git_root_by_walk(Path.cwd())
    if walked:
        return walked
    # Skip walking from _HERE — that hits UCL_Core .git first (wrong layer).
    # Fall back to cwd as last resort.
    return Path.cwd().resolve()


_REPO_ROOT = _resolve_repo_root()

_SESSIONS_PATH = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "work_sessions.json"
_QUOTA_PATH = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "agent_bonus_quota.json"
_PERSONAS_DIR = _REPO_ROOT / "AgentCommands" / "AwakenInit" / "personas"
# run_cmd.py is sibling — UCL_Core/Tools~/AgentCommands/run_cmd.py.
_RUN_CMD = _HERE / "run_cmd.py"

# ─── agent → bank 解析 (單一 SOT) ───────────────────────────────────────
# 區塊職責: agent 字串 → Treasury bank account id，一律走共用 resolver 讀 _registry_meta.json。
# 物理意義: agent_banks 的唯一真相在 AwakenInit/_registry_meta.json；本檔「不」自帶硬編表。
#          (2026-07-21 calli 收斂: 舊硬編 AGENT_TO_BANK 缺 Luna → kaguya stream-watch 抱 AGENT_TO_BANK miss,
#           per kaguya 證物 A + summit「快取≠真相」家族; 對齊 awakening.py / canvas.py 同一 resolver)。
# 數值影響: 新增 agent 只改 _registry_meta.json，本檔零改動即同步；未知 agent 走命名慣例 {canonical}-da-xiaojie。
_REGISTRY_META_PATH = _REPO_ROOT / "AgentCommands" / "AwakenInit" / "_registry_meta.json"

import importlib.util as _ilu  # 顯式檔案路徑載入共用 resolver，避開 _lib package 名稱相撞 (同 awakening.py idiom)
_BANK_RESOLVER_PATH = _HERE / "_lib" / "bank_resolver.py"
_br_spec = _ilu.spec_from_file_location("_ucl_bank_resolver_ws", _BANK_RESOLVER_PATH)
_br_mod = _ilu.module_from_spec(_br_spec)
_br_spec.loader.exec_module(_br_mod)

_REPO_ROOT = _resolve_repo_root()

_QUOTA_PATH = _REPO_ROOT / "AgentCommands" / "ChatTavern" / "agent_bonus_quota.json"
_PERSONAS_DIR = _REPO_ROOT / "AgentCommands" / "AwakenInit" / "personas"
_RUN_CMD = _HERE / "run_cmd.py"
_REGISTRY_META_PATH = _REPO_ROOT / "AgentCommands" / "AwakenInit" / "_registry_meta.json"

import importlib.util as _ilu  # 顯式檔案路徑載入共用 resolver，避開 _lib package 名稱相撞 (同 awakening.py idiom)
_BANK_RESOLVER_PATH = _HERE / "_lib" / "bank_resolver.py"
_br_spec = _ilu.spec_from_file_location("_ucl_bank_resolver_session", _BANK_RESOLVER_PATH)
_br_mod = _ilu.module_from_spec(_br_spec)
_br_spec.loader.exec_module(_br_mod)

def _resolve_bank(agent: str) -> str:
    """agent → bank account id，讀 _registry_meta.json 單一 SOT (fallback 命名慣例 {canonical}-da-xiaojie)。"""
    reg = _br_mod.load_registry_meta(_REGISTRY_META_PATH)
    return _br_mod.resolve_bank_account(reg, agent)

SALARY_RATE_PER_MIN = 2          # tokens per minute
VOUCHER_INTERVAL_MIN = 5         # 1 voucher per N minutes


# ─── utilities ──────────────────────────────────────────────────────────

def utcnow_iso() -> str:
    n = datetime.datetime.utcnow()
    return n.strftime("%Y-%m-%dT%H:%M:%S.") + f"{n.microsecond // 1000:03d}Z"

def parse_iso(ts: str) -> datetime.datetime:
    """Parse ISO 8601 to datetime (UTC naive)."""
    if ts.endswith("Z"):
        ts = ts[:-1]
    return datetime.datetime.fromisoformat(ts)

def short_uuid(n: int = 4) -> str:
    return uuid.uuid4().hex[:n]


# ─── Persona resolution ─────────────────────────────────────────────────

def atomic_write_json(path: Path, data: dict) -> None:
    """
    Atomic JSON write — write to tmp file, fsync, rename over target.
    os.replace 在 POSIX + Windows 都是 atomic (Python 3.3+).
    防 partial-write 撞 Editor recompile / 系統中斷導致 state corruption.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.flush()
        try:
            os.fsync(f.fileno())
        except OSError:
            pass  # fsync 不支援某些 fs 時 silent skip
    os.replace(tmp, path)

def tavern_post(sender_id: str, body: str, meta: dict, persona: str = "") -> bool:
    """Post a broadcast announcement to tavern via Cmd_Tavern. Returns True if the cmd completed.

    區塊職責：session 類工具（陪看 / 上班）的開播・收播・加入廣播出口。
    物理意義：這些是**廣播**——沒有人會回，所以顯式帶 ``--wait-reply 0``。
    數值影響：不帶那個旗標時 run_cmd 會用預設等回覆窗口，於是**每次都等到呼叫端 120s timeout
              才回 False**，並在 stderr 印「⚠ tavern_post fail」——而訊息其實早就落地了。
              那不是偶發，是結構上必定發生（2026-08-06 summit 一晚實測四次：兩場陪看的
              開播與收播公告全部回報 timeout，四次訊息都在酒館裡）。
              它也是 basecamp 長期教訓「送出成功 ≠ 對方收到」的鏡像：「等待失敗 ≠ 對方沒收到」。
    ⚠ 原本這行 docstring 寫「we don't wait」而程式其實在等 —— 註解比事實大，
      兩者不符時壞的一邊是註解沒被執行、於是沒有人發現。旗標與這段說明是同一次修的。
    """
    import subprocess
    cmd = [
        sys.executable, str(_RUN_CMD), "run", "Tavern",
        "--arg", "op=post",
        "--arg", "room=tavern",
        "--arg", f"sender_id={sender_id}",
        "--arg", f"body={body}",
        "--arg", f"meta={json.dumps(meta, ensure_ascii=False)}",
        "--wait-reply", "0",
    ]
    if persona:
        cmd.extend(["--arg", f"persona={persona}"])
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", timeout=120)
        return r.returncode == 0
    except Exception as e:
        print(f"⚠ tavern_post fail: {e}", file=sys.stderr)
        return False


# ─── Treasury 薪資入帳（走 Cmd_Treasury，不再直寫 ledger）──────────────

def fire_salary_credit(bank: str, persona: str, amount: int, session_id: str, checkpoint: str) -> str:
    """上班 session 薪資入帳。成功回傳可追溯的 source_ref，失敗回空字串。

    區塊職責：把薪資結算交給唯一的金流寫入端（C# UCL_TreasuryLedger），本函式只負責組參數。
    物理意義：本函式原本**直接寫 ledger json 檔**（原註解自承 "direct write, Phase 1 bypass
             Cmd_Treasury"）。那條旁路繞過了 C# 端的全部保護：
               · 帳號解析與歸一（agent 名大小寫 / persona 名 / 別名）
               · 已銷戶帳號拒收
               · balance_before/after 計算（原本靠事後 backfill 補，補不到就是兩個 null）
               · 餘額快取即時累進
             2026-08-14 實查：孤兒帳戶裡 `gemini-da-xiaojie` / `zeta-da-xiaojie` /
             `ClaudeCode-da-xiaojie` / `antigravity-da-xiaojie-da-xiaojie`（**雙後綴**）
             都是這條路配合 bank_resolver 的 derive 產生的。
    數值影響：金額與 source_kind 不變（`work_session_salary`，歷史查詢連續性優先）；
             改變的是**誰來寫**。balance 欄位由 C# 自填，事後 backfill 不再需要。
    邊界：Cmd 需要 Editor 在線。薪資結算本來就發生在有 Editor 的場次（直播陪看），
         而「寧可失敗也不要繞過收銀台」是刻意取捨 —— 靜默直寫的帳比沒入帳的帳難查。
    """
    if amount <= 0:
        return ""
    ref = f"ws:{session_id}:{checkpoint}:{persona}"
    try:
        sys.path.insert(0, str(_HERE))
        from _lib.treasury_cmd import treasury_credit
        ok, out = treasury_credit(
            account=bank, amount=amount,
            source_kind="work_session_salary", source_ref=ref,
            description=f"上班 session 薪資 — {persona} {checkpoint}",
            caller="system",          # 內部自動入帳，非使用者發起
        )
    except Exception as e:
        print(f"❌ 薪資入帳失敗（{type(e).__name__}: {e}）—— 未入帳，請人工補", file=sys.stderr)
        return ""
    if not ok:
        print(f"❌ 薪資入帳被拒：{out}", file=sys.stderr)
        return ""
    return ref

    return str(path.relative_to(_REPO_ROOT))


# ─── Voucher accrual (per-persona, post v2 migration) ───────────────────

def resolve_persona(name: str) -> dict | None:
    """Find persona record by name, return dict with agent / bank / status. None if not found."""
    f = _PERSONAS_DIR / f"{name}.json"
    if not f.exists():
        return None
    try:
        p = json.loads(f.read_text(encoding="utf-8"))
    except Exception:
        return None
    agent = p.get("agent", "")
    bank = _resolve_bank(agent) if agent else None
    return {
        "persona": name,
        "agent": agent,
        "bank": bank,
        "status": p.get("status", "?"),
    }
