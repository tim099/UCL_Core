"""
P7 Migration: add `tz` field to existing TimeRule entries (default Asia/Taipei).

One-shot, idempotent. Per Plan_Bartender_System.md §3 Timezone 規範.

Usage:
    python migrate_time_rules_add_tz.py [--dry-run] [--default-tz Asia/Taipei]

Behavior:
    - Reads <repo>/AgentCommands/ChatTavern/bartender/time_rules.json
    - For each rule without `tz` field → add tz = <default-tz>
    - Marks file with `_migration_p7_tz_at: <ISO ts>` to prevent re-runs
    - --dry-run prints diff without writing
"""

import argparse
import datetime
import json
import pathlib
import sys

# Windows console cp950 → emoji blow up; force UTF-8 stdout (no-op on POSIX).
try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dry-run", action="store_true", help="print diff without writing")
    ap.add_argument("--default-tz", default="Asia/Taipei", help="TZ for legacy entries (default: Asia/Taipei)")
    ap.add_argument(
        "--path",
        default="AgentCommands/ChatTavern/bartender/time_rules.json",
        help="time_rules.json path relative to repo root",
    )
    args = ap.parse_args()

    # Script lives in UCL_Core/Tools~/AgentCommands/ (cross-project submodule).
    # Resolve consumer-project repo root via cwd .git walk (same pattern as awakening.py).
    import os
    env_root = os.environ.get("CLAUDE_PROJECT_DIR")
    if env_root and pathlib.Path(env_root).is_dir():
        repo_root = pathlib.Path(env_root).resolve()
    else:
        cwd = pathlib.Path.cwd().resolve()
        while cwd != cwd.parent and not (cwd / ".git").exists():
            cwd = cwd.parent
        repo_root = cwd if (cwd / ".git").exists() else pathlib.Path.cwd().resolve()
    p = repo_root / args.path
    if not p.exists():
        print(f"❌ {p} not found")
        return 1

    data = json.loads(p.read_text(encoding="utf-8"))

    # idempotent guard
    if data.get("_migration_p7_tz_at"):
        print(f"♻ already migrated at {data['_migration_p7_tz_at']} — no-op")
        return 0

    rules = data.get("rules", []) if isinstance(data, dict) else data
    added = 0
    for r in rules:
        if isinstance(r, dict) and "tz" not in r:
            r["tz"] = args.default_tz
            added += 1
            print(f"  + {r.get('id', '?')}: tz = {args.default_tz}")

    if added == 0:
        print("✓ no entries needed migration (all have tz already)")
        # still mark migrated to prevent re-scan
        data["_migration_p7_tz_at"] = datetime.datetime.utcnow().isoformat() + "Z"
    else:
        data["_migration_p7_tz_at"] = datetime.datetime.utcnow().isoformat() + "Z"
        print(f"✓ migrated {added} entries → tz={args.default_tz}")

    if args.dry_run:
        print("--- DRY RUN: no write ---")
        return 0

    p.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"✓ wrote {p}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
