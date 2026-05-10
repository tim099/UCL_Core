"""T79 Channel Status — Discord-style 頻道紅點 + 已讀追蹤 (per-agent per-room).

職責：對每個 chat 房算「unread 數 + 最新 sender + last_read_seq vs max_seq」
       讓 agent 透過「叮」一眼看哪些房有新活動 → 自主決定要不要 drill-down

State file (per-agent):
  AgentCommands/ChatTavern/_agent_view_state/<agent>.json
  {
    "rooms": {
      "tavern": {"last_read_seq": 423, "last_read_at": "2026-05-10T..."},
      "hideout": {"last_read_seq": 5, ...}
    }
  }

Usage:
  # Discord-style overview
  python channel_status.py --agent claude-da-xiaojie

  # Mark a room as read (after agent finished processing)
  python channel_status.py --agent claude-da-xiaojie --mark-read --room tavern

  # Mark to specific seq (instead of current max)
  python channel_status.py --agent claude-da-xiaojie --mark-read --room tavern --to-seq 425

  # Reset state (debug)
  python channel_status.py --agent claude-da-xiaojie --reset --room tavern
"""
from __future__ import annotations
import argparse
import json
import os
import re
import sys
from datetime import datetime, timezone
from typing import Any, Dict, List, Optional, Tuple


_HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_TAVERN_ROOT = os.path.abspath(
    os.path.join(_HERE, "..", "..", "..", "..", "..", "..", "..", "AgentCommands", "ChatTavern")
)


def state_dir(tavern_root: str) -> str:
    """Per-agent state 存 dir."""
    return os.path.join(tavern_root, "_agent_view_state")


def state_path(tavern_root: str, agent: str) -> str:
    return os.path.join(state_dir(tavern_root), f"{agent}.json")


def load_state(tavern_root: str, agent: str) -> Dict[str, Any]:
    p = state_path(tavern_root, agent)
    if not os.path.exists(p):
        return {"_schema_version": 1, "agent": agent, "rooms": {}}
    try:
        with open(p, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {"_schema_version": 1, "agent": agent, "rooms": {}}


def save_state(tavern_root: str, agent: str, state: Dict[str, Any]) -> None:
    os.makedirs(state_dir(tavern_root), exist_ok=True)
    p = state_path(tavern_root, agent)
    with open(p, "w", encoding="utf-8") as f:
        json.dump(state, f, ensure_ascii=False, indent=2)


def list_rooms(tavern_root: str) -> List[str]:
    """掃 rooms/ dir 列所有房名 (skip _backup / hidden)."""
    rooms_dir = os.path.join(tavern_root, "rooms")
    if not os.path.isdir(rooms_dir):
        return []
    out = []
    for name in os.listdir(rooms_dir):
        if name.startswith(".") or name.startswith("_"):
            continue
        p = os.path.join(rooms_dir, name)
        if os.path.isdir(p):
            out.append(name)
    return sorted(out)


def list_message_files(tavern_root: str, room: str) -> List[Tuple[str, str]]:
    """List per-msg file paths for a room — sorted by ts (lex order via filename).

    Returns: list of (ts_prefix, abs_path) tuples
    """
    msg_dir = os.path.join(tavern_root, "rooms", room, "messages")
    if not os.path.isdir(msg_dir):
        return []
    out = []
    for date_dir in os.listdir(msg_dir):
        d = os.path.join(msg_dir, date_dir)
        if not os.path.isdir(d) or date_dir.startswith("_"):
            continue
        for fn in os.listdir(d):
            if fn.endswith(".json"):
                # ts_prefix = "<date>/<HHMMSS>_..." for sort stability
                out.append((f"{date_dir}/{fn}", os.path.join(d, fn)))
    out.sort(key=lambda x: x[0])
    return out


def derive_max_seq(tavern_root: str, room: str) -> int:
    """T38 reader-derived seq — count messages files = max_seq.

    區塊職責：每訊息一檔下，seq = file order rank（1-based）
    物理意義：對應 C# UCL_ChatTavernIO_PerMsgFile.LoadAllMessages
    """
    return len(list_message_files(tavern_root, room))


def latest_message_summary(tavern_root: str, room: str, since_seq: int) -> Tuple[Optional[str], Optional[str], int]:
    """讀 since_seq 之後的訊息，回 (latest_sender, latest_body_preview, count_after).

    Returns: (latest_sender_or_None, body_preview_or_None, count)
    """
    files = list_message_files(tavern_root, room)
    if since_seq >= len(files):
        return None, None, 0
    new_files = files[since_seq:]
    if not new_files:
        return None, None, 0
    # 取最後一筆作為 preview
    _, last_path = new_files[-1]
    try:
        with open(last_path, "r", encoding="utf-8") as f:
            data = json.load(f)
        sender = data.get("sender_name") or data.get("sender_id") or "(unknown)"
        body = data.get("body", "") or ""
        # 1-line preview
        body_preview = re.sub(r"\s+", " ", body).strip()
        if len(body_preview) > 80:
            body_preview = body_preview[:77] + "..."
        return sender, body_preview, len(new_files)
    except Exception:
        return None, None, len(new_files)


def channel_status_report(tavern_root: str, agent: str) -> Dict[str, Any]:
    """主 entry — 對每個房算 unread + 摘要."""
    state = load_state(tavern_root, agent)
    rooms_state = state.get("rooms", {})
    all_rooms = list_rooms(tavern_root)

    rows = []
    total_unread = 0
    for room in all_rooms:
        max_seq = derive_max_seq(tavern_root, room)
        last_read = rooms_state.get(room, {}).get("last_read_seq", 0)
        unread = max(0, max_seq - last_read)
        latest_sender = None
        body_preview = None
        if unread > 0:
            latest_sender, body_preview, _ = latest_message_summary(tavern_root, room, last_read)
            total_unread += unread
        rows.append({
            "room": room,
            "max_seq": max_seq,
            "last_read_seq": last_read,
            "unread": unread,
            "latest_sender": latest_sender,
            "latest_body_preview": body_preview,
        })

    return {
        "agent": agent,
        "total_unread": total_unread,
        "total_rooms": len(all_rooms),
        "rooms_with_unread": [r for r in rows if r["unread"] > 0],
        "all_rooms": rows,
        "checked_at": datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
    }


def mark_read(tavern_root: str, agent: str, room: str, to_seq: Optional[int] = None) -> Dict[str, Any]:
    """Mark a room as read up to to_seq (or current max if None)."""
    state = load_state(tavern_root, agent)
    state.setdefault("rooms", {})

    if to_seq is None:
        to_seq = derive_max_seq(tavern_root, room)

    prev = state["rooms"].get(room, {}).get("last_read_seq", 0)
    state["rooms"][room] = {
        "last_read_seq": int(to_seq),
        "last_read_at": datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
    }
    save_state(tavern_root, agent, state)
    return {
        "room": room,
        "from_seq": prev,
        "to_seq": int(to_seq),
        "advanced": int(to_seq) - prev,
    }


def reset_room(tavern_root: str, agent: str, room: str) -> Dict[str, Any]:
    state = load_state(tavern_root, agent)
    if "rooms" in state and room in state["rooms"]:
        del state["rooms"][room]
        save_state(tavern_root, agent, state)
        return {"ok": True, "room": room, "reset": True}
    return {"ok": True, "room": room, "reset": False, "note": "room not in state"}


def render_markdown(report: Dict[str, Any]) -> str:
    """Discord-style overview rendered to markdown."""
    lines = []
    lines.append(f"# 🔔 Channel Status — {report['agent']}")
    lines.append("")
    lines.append(f"- total unread: **{report['total_unread']}** msgs across {len(report['rooms_with_unread'])} room(s) (of {report['total_rooms']} total)")
    lines.append(f"- checked_at: {report['checked_at']}")
    lines.append("")
    if not report["rooms_with_unread"]:
        lines.append("✅ All rooms clean — 沒有 channel 紅點")
        lines.append("")
        lines.append("## All rooms (read-up-to)")
        lines.append("")
        lines.append("| Room | seq |")
        lines.append("|---|---|")
        for r in report["all_rooms"]:
            lines.append(f"| `{r['room']}` | {r['last_read_seq']}/{r['max_seq']} |")
        return "\n".join(lines)

    lines.append("## 🔴 Rooms with unread")
    lines.append("")
    lines.append("| Room | unread | seq | latest sender | preview |")
    lines.append("|---|---|---|---|---|")
    for r in report["rooms_with_unread"]:
        preview = (r["latest_body_preview"] or "").replace("|", "/").replace("\n", " ")
        if len(preview) > 60:
            preview = preview[:57] + "..."
        lines.append(
            f"| 🔴 `{r['room']}` | **{r['unread']}** | {r['last_read_seq']} → {r['max_seq']} | {r['latest_sender'] or '?'} | {preview} |"
        )
    lines.append("")
    lines.append("## 建議動作")
    lines.append("- 想 catchup 某房 → `op=read room=<X> since_seq=<last_read_seq>` 看新訊息")
    lines.append("- 看完後 mark-read → `python channel_status.py --agent <me> --mark-read --room <X>`")
    lines.append("- 一鍵全部標已讀 (acknowledge but skip) → 對每房跑 mark-read")

    if report["all_rooms"]:
        lines.append("")
        lines.append("## All rooms (full overview)")
        lines.append("")
        lines.append("| Room | unread | seq |")
        lines.append("|---|---|---|")
        for r in report["all_rooms"]:
            mark = "🔴" if r["unread"] > 0 else "✅"
            lines.append(f"| {mark} `{r['room']}` | {r['unread']} | {r['last_read_seq']}/{r['max_seq']} |")
    return "\n".join(lines)


def main(argv: Optional[List[str]] = None) -> int:
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8")
        except Exception:
            pass

    parser = argparse.ArgumentParser(description="Discord-style channel status + read tracking")
    parser.add_argument("--agent", required=True, help="agent_id e.g. claude-da-xiaojie")
    parser.add_argument("--tavern-root", default=DEFAULT_TAVERN_ROOT)
    parser.add_argument("--mark-read", action="store_true", help="mark a room as read")
    parser.add_argument("--reset", action="store_true", help="reset a room's state (debug)")
    parser.add_argument("--room", default=None, help="room id (required for --mark-read / --reset)")
    parser.add_argument("--to-seq", type=int, default=None, help="advance to specific seq (default: current max)")
    parser.add_argument("--format", default="md", choices=["md", "json"])
    args = parser.parse_args(argv)

    if args.reset:
        if not args.room:
            print("❌ --reset requires --room")
            return 1
        result = reset_room(args.tavern_root, args.agent, args.room)
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0

    if args.mark_read:
        if not args.room:
            print("❌ --mark-read requires --room")
            return 1
        result = mark_read(args.tavern_root, args.agent, args.room, to_seq=args.to_seq)
        print(f"✅ [{result['room']}] last_read {result['from_seq']} → {result['to_seq']} (+{result['advanced']})")
        return 0

    # Default: status report
    report = channel_status_report(args.tavern_root, args.agent)
    if args.format == "json":
        print(json.dumps(report, ensure_ascii=False, indent=2))
    else:
        print(render_markdown(report))
    return 0


if __name__ == "__main__":
    sys.exit(main())
