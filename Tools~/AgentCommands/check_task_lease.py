#!/usr/bin/env python3
"""
check_task_lease.py — git pre-commit hook：偵測 staged code change 是否落在 active task lease 內

W1 enforcement (T08 chat-flow-robust quest)：
- Tim 拍板：「動 code 前必先 task_claim」是 quest workflow 鐵律
- 但 task_claim 是 lease 紀錄，沒 enforce「該檔案的修改必須由 owner 動」
- 雙 agent 在同 session 內各犯一次 W1（Gemini T02 / Claude T07） — 自律不夠

本 script 行為：
1. git diff --cached --name-only → 拿 staged 檔案清單
2. 掃 chat_tavern/rooms/*/events.jsonl → 找 status=claimed/in_progress/review 的 active task
3. 對每個 staged 檔案 → grep tasks/<id>.md spec body 找路徑提及
   - 在 lease 內 + claimer == 自己 → ✅ 通過無 print
   - 在 lease 內 + claimer ≠ 自己 → ⚠ stderr「妳在動 X 的 lease 內 task」
   - 沒任何 lease cover → ⚠ stderr「該檔案沒對應 task_claim」
4. **不擋 commit**（exit 0）— warning 為主，避免 false positive 厭世

Bypass：
- ENV `UCL_SKIP_TASK_CHECK=1` → 整個檢查 silent skip（ad-hoc commit / hotfix 用）

安裝：
- 建議放 `<repo>/.git/hooks/pre-commit`（bash invoker 1 行）
- bash hook 內容：`exec python "<UCL_Core>/Tools~/AgentCommands/check_task_lease.py"`

Author: Claude大小姐 (force_reclaim T08 from Gemini大小姐 with Tim 授權)
Co-design: Gemini大小姐 (T08 plan + claim flow)
"""
import os
import sys
import json
import subprocess
import pathlib
from typing import List, Dict, Optional


# ===========================================================
# Config
# ===========================================================

ENV_BYPASS = "UCL_SKIP_TASK_CHECK"
GIT_AGENT_MAP = {
    # git config user.name 對應 sender_id（讓 hook 知道「我是誰」）
    # 預設值：環境變數 / git config 沒設時走第一個 fallback
    "Tim": "Tim",
    "Claude": "claude-da-xiaojie",
    "Gemini": "gemini-da-xiaojie",
    "GPT": "gpt-shifu",
    "Zeta": "Zeta",
}


# ===========================================================
# Git helpers
# ===========================================================

def get_repo_root() -> Optional[pathlib.Path]:
    """git rev-parse --show-toplevel"""
    try:
        out = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"],
            capture_output=True, text=True, check=True
        )
        return pathlib.Path(out.stdout.strip())
    except (subprocess.CalledProcessError, FileNotFoundError):
        return None


def get_staged_files(repo_root: pathlib.Path) -> List[str]:
    """git diff --cached --name-only — staged code 清單（repo-root 相對路徑）"""
    try:
        out = subprocess.run(
            ["git", "diff", "--cached", "--name-only"],
            capture_output=True, text=True, check=True, cwd=str(repo_root)
        )
        return [line.strip() for line in out.stdout.splitlines() if line.strip()]
    except (subprocess.CalledProcessError, FileNotFoundError):
        return []


def resolve_my_agent_id() -> str:
    """從 git config user.name 推 sender_id；fallback 走 ENV CLAUDE_AGENT_ID 或預設值"""
    # 優先級 1：顯式 ENV
    env_id = os.environ.get("UCL_TASK_LEASE_AGENT_ID", "").strip()
    if env_id:
        return env_id
    # 優先級 2：git config user.name → mapping
    try:
        out = subprocess.run(
            ["git", "config", "user.name"],
            capture_output=True, text=True, check=True
        )
        gitname = out.stdout.strip()
        for key, sid in GIT_AGENT_MAP.items():
            if key.lower() in gitname.lower():
                return sid
    except Exception:
        pass
    # 優先級 3：fallback
    return "Tim"


# ===========================================================
# Task lease lookup
# ===========================================================

def find_tavern_dir(repo_root: pathlib.Path) -> Optional[pathlib.Path]:
    """找 AgentCommands/ChatTavern/rooms/ 路徑"""
    candidate = repo_root / "AgentCommands" / "ChatTavern" / "rooms"
    if candidate.exists():
        return candidate
    return None


def load_room_events(events_path: pathlib.Path) -> List[dict]:
    """讀 events.jsonl，partial line skip"""
    if not events_path.exists():
        return []
    events = []
    try:
        raw = events_path.read_text(encoding="utf-8")
        idx = 0
        while idx < len(raw):
            nl = raw.find("\n", idx)
            if nl < 0:
                break
            line = raw[idx:nl].strip()
            idx = nl + 1
            if not line:
                continue
            try:
                events.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    except Exception:
        pass
    return events


def reduce_active_tasks(events: List[dict]) -> Dict[str, dict]:
    """
    精簡 reducer — 只認 active task 狀態（claimed / in_progress / review）。
    回 dict {task_id: {owner, status, lease_until}}
    """
    states: Dict[str, dict] = {}
    for ev in events:
        tid = ev.get("task_id")
        if not tid:
            continue
        t = ev.get("type")
        data = ev.get("data") or {}
        if t == "task_create":
            states[tid] = {"status": "pending", "owner": None, "lease_until": None}
        elif t == "task_claim" and tid in states:
            states[tid]["status"] = "claimed"
            states[tid]["owner"] = ev.get("actor")
            states[tid]["lease_until"] = data.get("lease_until")
        elif t == "task_progress" and tid in states:
            states[tid]["status"] = "in_progress"
            if data.get("lease_until"):
                states[tid]["lease_until"] = data["lease_until"]
        elif t == "task_review_request" and tid in states:
            states[tid]["status"] = "review"
        elif t == "task_done" and tid in states:
            states[tid]["status"] = "done"
        elif t == "task_release" and tid in states:
            states[tid]["status"] = "pending"
            states[tid]["owner"] = None
        elif t == "task_reject" and tid in states:
            states[tid]["status"] = "in_progress"
        elif t == "task_force_reclaim" and tid in states:
            states[tid]["status"] = "claimed"
            states[tid]["owner"] = ev.get("actor")
            states[tid]["lease_until"] = data.get("lease_until")
    # 過濾 active
    return {tid: s for tid, s in states.items() if s["status"] in ("claimed", "in_progress", "review")}


def task_spec_mentions_file(spec_path: pathlib.Path, file_path: str) -> bool:
    """task spec body grep 看有沒有提到此檔案路徑（fallback B：寬鬆 grep）"""
    if not spec_path.exists():
        return False
    try:
        body = spec_path.read_text(encoding="utf-8")
        # 提取檔名 + 路徑各 substring 都試
        filename = pathlib.Path(file_path).name
        return file_path in body or filename in body
    except Exception:
        return False


# ===========================================================
# 主流程
# ===========================================================

def main() -> int:
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")

    # ENV bypass
    if os.environ.get(ENV_BYPASS):
        return 0

    repo_root = get_repo_root()
    if not repo_root:
        return 0   # 不在 git repo / git 缺；silent skip

    staged = get_staged_files(repo_root)
    if not staged:
        return 0   # 沒 staged 檔；nothing to check

    tavern_rooms = find_tavern_dir(repo_root)
    if not tavern_rooms:
        return 0   # 沒 tavern 結構；本系統未啟用

    my_id = resolve_my_agent_id()

    # 收集所有房的 active task + room dir
    active_tasks_by_room: Dict[str, Dict[str, dict]] = {}
    for room_dir in tavern_rooms.iterdir():
        if not room_dir.is_dir():
            continue
        events_path = room_dir / "events.jsonl"
        if not events_path.exists():
            continue
        events = load_room_events(events_path)
        active = reduce_active_tasks(events)
        if active:
            active_tasks_by_room[room_dir.name] = active

    # 對每個 staged 檔案做 lease 比對
    warnings_self = []        # 我自己 lease 內：不警告（OK）
    warnings_other = []       # 在他人 lease 內：警告
    warnings_no_lease = []    # 沒任何 lease cover：警告

    for sf in staged:
        # 找有提及此檔案的 active task
        matched_tasks = []
        for room, tasks in active_tasks_by_room.items():
            for tid, st in tasks.items():
                spec_path = tavern_rooms / room / "tasks" / f"{tid}.md"
                if task_spec_mentions_file(spec_path, sf):
                    matched_tasks.append({"room": room, "task_id": tid, "owner": st["owner"]})

        if not matched_tasks:
            warnings_no_lease.append(sf)
        else:
            owners = {m["owner"] for m in matched_tasks if m["owner"]}
            if my_id in owners:
                # 我有 lease 罩這檔 → 通過
                continue
            else:
                warnings_other.append((sf, matched_tasks))

    # 印 warnings (stderr，不擋 commit)
    if warnings_no_lease or warnings_other:
        sys.stderr.write("\n")
        sys.stderr.write("=" * 70 + "\n")
        sys.stderr.write(f"⚠ Task Lease 提醒 (W1 enforcement)\n")
        sys.stderr.write(f"  你 (git agent_id): {my_id}\n")
        sys.stderr.write("=" * 70 + "\n")

        if warnings_no_lease:
            sys.stderr.write(f"\n⚠ 以下 staged 檔案沒有對應的 active task lease：\n")
            for f in warnings_no_lease[:10]:
                sys.stderr.write(f"    - {f}\n")
            if len(warnings_no_lease) > 10:
                sys.stderr.write(f"    ... 另 {len(warnings_no_lease) - 10} 筆未列\n")
            sys.stderr.write(f"\n    建議：先 task_create + task_claim，或在現有 task spec body 內補檔案路徑。\n")

        if warnings_other:
            sys.stderr.write(f"\n⚠ 以下 staged 檔案落在他人 lease 內（非妳 owner）：\n")
            for sf, tasks in warnings_other[:10]:
                owners_str = ", ".join({t["owner"] for t in tasks if t["owner"]})
                tids = ", ".join(t["task_id"] for t in tasks[:3])
                sys.stderr.write(f"    - {sf}\n")
                sys.stderr.write(f"        在 lease 內：{tids} (owner: {owners_str})\n")
            sys.stderr.write(f"\n    建議：跟 owner 協調 task_release，或妳 task_force_reclaim（lease 過期才行）。\n")

        sys.stderr.write(f"\n  Bypass：export {ENV_BYPASS}=1 跳過此檢查\n")
        sys.stderr.write("=" * 70 + "\n\n")

    # 一律 exit 0 — 不擋 commit（warning 為主）
    return 0


if __name__ == "__main__":
    sys.exit(main())
