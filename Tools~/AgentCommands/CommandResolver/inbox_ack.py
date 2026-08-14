"""T-INBOX-ACK — 把 inbox 內容 archive，讓「叮」之後只看新 mention.

使用情境：
  Tim 用「叮」看完 inbox 但不想逐條回 → 打「已讀」→ 把當前 inbox 整個 append 到 archive
  → 下次「叮」只會看到「真新」mention，不被舊 stale 干擾

Flow:
  inbox/<agent>.md  ──append──>  inbox/<agent>_archive.md
                    ──truncate──> 清空 + 寫一行 header（保留 file 存在性）

Atomicity:
  先 read inbox → write archive → 再清 inbox。任一步失敗 → 不動 inbox（避免漏存）
"""
from __future__ import annotations
import argparse
import os
import sys
from datetime import datetime, timezone
from typing import Optional


_HERE = os.path.dirname(os.path.abspath(__file__))


# 區塊職責：repo 根 / 資料根解析 — 取代原本硬編 7 層 ".." 的相對鏈
# 物理意義：本檔住 <UCL_Core>/Tools~/AgentCommands/CommandResolver/，而 UCL_Core 是 submodule，
#          **各專案掛載深度不同**（Assets/Plugins/UCL_Core、Assets/UCL/UCL_Core、CardGame/Assets/UCL/…）
#          → 任何寫死的 ".." 層數都會跨專案漂移。原值多爬一層（7 層 → D:\Unity 而非 D:\Unity\LY），
#          導致 inbox 找不到（2026-07-28 實測）。改走「往上找 .git 資料夾」與其他工具同慣例。
# 數值影響：.git 只認資料夾（submodule 的 .git 是檔案 → 自動跳過，命中主專案根）；
#          另 honors CLAUDE_PROJECT_DIR（agent 環境注入）與 .agentcommands_root.local（資料根 override）。
def _find_git_root(start: str):
    p = os.path.abspath(start)
    while True:
        if os.path.isdir(os.path.join(p, ".git")):
            return p
        parent = os.path.dirname(p)
        if parent == p:
            return None
        p = parent


def _repo_root() -> str:
    env = os.environ.get("CLAUDE_PROJECT_DIR")
    if env and os.path.isdir(env):
        return os.path.abspath(env)
    walked = _find_git_root(_HERE)
    if walked:
        return walked
    return _find_git_root(os.getcwd()) or os.path.abspath(os.path.join(_HERE, "..", "..", "..", "..", "..", ".."))


def _data_root(root: str) -> str:
    """AgentCommands 資料根 — honors <repo>/.agentcommands_root.local pointer（C#/Python 共讀）。"""
    pointer = os.path.join(root, ".agentcommands_root.local")
    try:
        if os.path.isfile(pointer):
            with open(pointer, "r", encoding="utf-8") as f:
                content = f.read().strip()
            if content and os.path.isabs(content):
                return os.path.abspath(content)
    except OSError:
        pass
    return os.path.join(root, "AgentCommands")


# AgentCommands/ChatTavern/rooms/<room>/inbox/<agent>.md
DEFAULT_TAVERN_ROOT = os.path.join(_data_root(_repo_root()), "ChatTavern")


def inbox_path(tavern_root: str, room: str, agent: str) -> str:
    return os.path.join(tavern_root, "rooms", room, "inbox", f"{agent}.md")


def archive_path(tavern_root: str, room: str, agent: str) -> str:
    return os.path.join(tavern_root, "rooms", room, "inbox", f"{agent}_archive.md")


def count_mentions(text: str) -> int:
    """粗略估 mention 數 — 看『## [seq=』出現次數."""
    return text.count("## [seq=")


def archive_inbox(tavern_root: str, room: str, agent: str) -> dict:
    """主要動作：append inbox to archive + clear inbox.

    Returns:
      {ok, archived_count, archive_size_after, error?}
    """
    inbox_p = inbox_path(tavern_root, room, agent)
    archive_p = archive_path(tavern_root, room, agent)

    if not os.path.exists(inbox_p):
        return {"ok": False, "error": f"inbox not found: {inbox_p}"}

    # Step 1: Read inbox
    try:
        with open(inbox_p, "r", encoding="utf-8") as f:
            content = f.read()
    except Exception as ex:
        return {"ok": False, "error": f"read inbox fail: {ex}"}

    if not content.strip():
        return {"ok": True, "archived_count": 0, "note": "inbox already empty"}

    mention_count = count_mentions(content)

    # Step 2: Append to archive (create if not exists)
    ts = datetime.now(timezone.utc).isoformat(timespec="seconds")
    separator = f"\n\n---\n## 📦 Archived at {ts} ({mention_count} mentions)\n\n"
    try:
        # 確保資料夾存在
        os.makedirs(os.path.dirname(archive_p), exist_ok=True)
        archive_existed = os.path.exists(archive_p)
        with open(archive_p, "a", encoding="utf-8") as f:
            if not archive_existed:
                f.write(f"# 📦 Inbox Archive — {agent}\n\n")
                f.write("> 由「已讀」trigger fire `inbox_ack.py` 自動歸檔\n")
            f.write(separator)
            f.write(content)
    except Exception as ex:
        return {"ok": False, "error": f"write archive fail: {ex}"}

    # Step 3: Clear inbox (寫個 header 保留 file，但內容空)
    try:
        with open(inbox_p, "w", encoding="utf-8") as f:
            f.write(f"<!-- inbox cleared at {ts} via inbox_ack.py -->\n")
    except Exception as ex:
        return {"ok": False, "error": f"clear inbox fail (archive 已寫): {ex}"}

    archive_size = os.path.getsize(archive_p)
    return {
        "ok": True,
        "archived_count": mention_count,
        "archive_size_after": archive_size,
        "archived_at": ts,
        "inbox_path": inbox_p,
        "archive_path": archive_p,
    }


def main(argv: Optional[list] = None) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8")
        except Exception:
            pass

    parser = argparse.ArgumentParser(description="Archive inbox content (mark as read)")
    # 區塊職責：收件匣的**擁有者 id** —— 它可以是 persona 也可以是 agent
    # 物理意義：inbox 檔是 `rooms/<room>/inbox/<擁有者>.md`，而兩層都有檔
    #          （persona 層 `inbox/summit.md`、agent 層 `inbox/Zeta.md`）。
    # ⚠ 2026-08-14 正名（apex-one 指出）：這個參數原本只叫 `--agent`，help 也寫 "agent_id"，
    #   但實務上最常餵的是 **persona**。名字比事實小 → 讀的人會以為只能填 agent，
    #   而填錯的方向剛好是「歸檔到一個不存在的收件匣」——那會靜默成功（沒有檔就當沒訊息）。
    #   canonical 改為 `--owner`；`--persona` / `--agent` 保留為等價別名，既有呼叫端不受影響。
    parser.add_argument("--owner", "--persona", "--agent", dest="owner", required=True,
                        help="收件匣擁有者 id — persona（如 summit）或 agent（如 Zeta）皆可，"
                             "對應 rooms/<room>/inbox/<owner>.md")
    parser.add_argument("--room", default="tavern",
                        help="single room to archive (default: tavern). use --all-rooms for sweep.")
    parser.add_argument("--all-rooms", action="store_true",
                        help="archive 該 owner 在 tavern + hideout 兩房 inbox（已讀全清）")
    parser.add_argument("--tavern-root", default=DEFAULT_TAVERN_ROOT)
    args = parser.parse_args(argv)

    # 區塊職責：rooms 清單決策
    # 物理意義：--all-rooms → ['tavern', 'hideout']（私訊也一起 archive）
    #          否則 → 單 [args.room]
    # 數值影響：每房獨立 archive；任一失敗不影響其他房
    rooms = ["tavern", "hideout"] if args.all_rooms else [args.room]

    total_archived = 0
    fails = []
    for room in rooms:
        result = archive_inbox(args.tavern_root, room, args.owner)
        if result["ok"]:
            cnt = result.get("archived_count", 0)
            total_archived += cnt
            if cnt > 0:
                print(f"✅ [{room}] {cnt} 筆 mention archived → {result['archive_path']}")
            else:
                # 多房 sweep 時靜默 skip 空房
                if not args.all_rooms:
                    print(f"✅ [{room}] inbox 已是空的（無動作）")
        else:
            fails.append((room, result["error"]))
            print(f"⚠ [{room}] {result['error']}")

    if args.all_rooms:
        print(f"📦 總計 archive {total_archived} 筆 across {len(rooms)} room(s)")

    return 0 if not fails else 1


if __name__ == "__main__":
    sys.exit(main())
