"""
Migrate _identity_<session_key>.json → _persona_<persona>.json (Tim 2026-05-13 v2 lock refactor).

One-shot, idempotent. Per Plan_Awakening_Init_Protocol §Persona-keyed Lock.

Usage:
    python migrate_session_to_persona_locks.py [--dry-run] [--prune-stale]

Behavior:
    - Reads <repo>/AgentCommands/_session/_identity_*.json
    - For each: extract `persona` from body → write to _persona_<persona>.json
      (atomic via tmp + os.replace)
    - If multiple _identity_* files target same persona (collision case from
      cwd-based session_key bugs), keep the most-recent locked_at.
    - --prune-stale: also delete old _identity_*.json after migrate. Without
      this flag they're kept as audit (move-only-not-delete).
    - Idempotent: marker file `_session/.migration_persona_keyed_at` written
      after first run; subsequent runs no-op unless --force.
"""

from __future__ import annotations

import argparse
import datetime
import json
import os
import pathlib
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass


def _resolve_repo_root() -> pathlib.Path:
    env_root = os.environ.get("CLAUDE_PROJECT_DIR")
    if env_root and pathlib.Path(env_root).is_dir():
        return pathlib.Path(env_root).resolve()
    cwd = pathlib.Path.cwd().resolve()
    while cwd != cwd.parent and not (cwd / ".git").exists():
        cwd = cwd.parent
    return cwd if (cwd / ".git").exists() else pathlib.Path.cwd().resolve()


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dry-run", action="store_true", help="Show plan, don't write.")
    ap.add_argument("--prune-stale", action="store_true",
                    help="Delete _identity_*.json after successful migrate (default keeps audit).")
    ap.add_argument("--force", action="store_true", help="Re-run even if marker exists.")
    args = ap.parse_args()

    repo_root = _resolve_repo_root()
    session_dir = repo_root / "AgentCommands" / "_session"
    if not session_dir.exists():
        print(f"❌ {session_dir} not found")
        return 1

    marker = session_dir / ".migration_persona_keyed_at"
    if marker.exists() and not args.force:
        print(f"♻ already migrated at {marker.read_text(encoding='utf-8').strip()} — no-op (use --force to re-run)")
        return 0

    identity_files = sorted(session_dir.glob("_identity_*.json"))
    if not identity_files:
        print("✓ no _identity_*.json files to migrate")
        if not args.dry_run:
            marker.write_text(datetime.datetime.utcnow().isoformat() + "Z", encoding="utf-8")
        return 0

    # Group by persona, keep most-recent locked_at on collision.
    by_persona: dict[str, dict] = {}
    sources: dict[str, list[pathlib.Path]] = {}  # persona → src files
    for f in identity_files:
        try:
            d = json.loads(f.read_text(encoding="utf-8"))
        except Exception as e:
            print(f"  ⚠ skip {f.name}: parse fail ({e})")
            continue
        persona = d.get("persona")
        if not persona:
            print(f"  ⚠ skip {f.name}: no persona field")
            continue
        sources.setdefault(persona, []).append(f)
        existing = by_persona.get(persona)
        if existing is None or d.get("locked_at", "") > existing.get("locked_at", ""):
            by_persona[persona] = d

    print(f"Plan: migrate {len(identity_files)} _identity files → {len(by_persona)} _persona files")
    for persona, d in sorted(by_persona.items()):
        srcs = sources[persona]
        target = session_dir / f"_persona_{persona}.json"
        marker_str = "OVERWRITE" if target.exists() else "create"
        from_str = ", ".join(s.name for s in srcs[:3]) + (f" + {len(srcs)-3} more" if len(srcs) > 3 else "")
        print(f"  {marker_str} {target.name}: persona={persona}, agent={d.get('agent','?')}, from=[{from_str}]")

    if args.dry_run:
        print("--- DRY RUN: no writes ---")
        return 0

    # Write per-persona files atomically.
    for persona, d in by_persona.items():
        target = session_dir / f"_persona_{persona}.json"
        # Add pid field if missing (new schema requires it; legacy lacks it).
        d.setdefault("pid", os.getpid())
        tmp = target.with_suffix(".json.tmp")
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(d, f, ensure_ascii=False, indent=2)
        os.replace(tmp, target)
        print(f"  ✓ wrote {target.name}")

    # Optional prune of old _identity files.
    if args.prune_stale:
        pruned = 0
        for f in identity_files:
            try:
                f.unlink()
                pruned += 1
            except OSError as e:
                print(f"  ⚠ failed to delete {f.name}: {e}")
        print(f"  🧹 pruned {pruned} _identity_*.json files")
    else:
        print(f"  📜 kept {len(identity_files)} _identity_*.json (audit; use --prune-stale to delete)")

    marker.write_text(datetime.datetime.utcnow().isoformat() + "Z", encoding="utf-8")
    print(f"✓ migration complete; marker {marker.name} written")
    return 0


if __name__ == "__main__":
    sys.exit(main())
