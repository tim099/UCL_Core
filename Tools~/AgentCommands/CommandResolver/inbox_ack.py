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
# AgentCommands/ChatTavern/rooms/<room>/inbox/<agent>.md
DEFAULT_TAVERN_ROOT = os.path.abspath(
    os.path.join(_HERE, "..", "..", "..", "..", "..", "..", "..", "AgentCommands", "ChatTavern")
)


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
    parser.add_argument("--agent", required=True, help="agent_id e.g. claude-da-xiaojie")
    parser.add_argument("--room", default="tavern",
                        help="single room to archive (default: tavern). use --all-rooms for sweep.")
    parser.add_argument("--all-rooms", action="store_true",
                        help="archive 該 agent 在 tavern + hideout 兩房 inbox（已讀全清）")
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
        result = archive_inbox(args.tavern_root, room, args.agent)
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
