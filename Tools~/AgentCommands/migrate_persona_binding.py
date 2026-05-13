#!/usr/bin/env python3
"""kyouko-persona-binding T04 — migrate existing letters / baton 進 Agent@Persona layout.

Before (agent-keyed):
    baton/letters/<actor>/<ts>.md
    baton/letters/<actor>/_latest.md
    baton/<actor>_<ts>.md
    baton/_latest_<actor>.md

After (Agent@Persona-keyed):
    baton/letters/<actor>/<persona>/<ts>.md
    baton/letters/<actor>/<persona>/_latest.md
    baton/<actor>/<persona>/<ts>.md
    baton/<actor>/<persona>/_latest.md

Strategy:
- Letters: 讀 frontmatter `written_by_persona`，沒有的進 `_unassigned/`
- Baton timestamped: frontmatter 沒 persona 欄，全進 `_unassigned/`
- `_latest_*.md`: 不搬（per-persona 重生 by next goodnight）
- `dialogues/`: 整 dir 搬 `_unassigned/dialogues/`（一律保守）
- `cross-agent/`: 不動

Usage:
    python migrate_persona_binding.py --dry-run     # 看搬遷計畫
    python migrate_persona_binding.py --apply       # 實際搬
"""
from __future__ import annotations
import argparse
import re
import shutil
import sys
from pathlib import Path

# Resolve REPO_ROOT by walking up until AgentCommands/ChatTavern/baton/ exists
def _find_repo_root() -> Path:
    p = Path(__file__).resolve()
    for ancestor in p.parents:
        if (ancestor / "AgentCommands" / "ChatTavern" / "baton").is_dir():
            return ancestor
    raise SystemExit(f"❌ 找不到 repo root (AgentCommands/ChatTavern/baton) — script at {p}")


REPO_ROOT = _find_repo_root()
BATON_DIR = REPO_ROOT / "AgentCommands" / "ChatTavern" / "baton"
LETTERS_DIR = BATON_DIR / "letters"

FRONTMATTER_RE = re.compile(r"^---\s*\n(.*?)\n---\s*\n", re.DOTALL)
PERSONA_KEY_RE = re.compile(r"^written_by_persona:\s*(.+)$", re.MULTILINE)


def read_persona_from_frontmatter(p: Path) -> str | None:
    """Return persona name or None if not found in frontmatter."""
    try:
        text = p.read_text(encoding="utf-8", errors="replace")
    except Exception:
        return None
    m = FRONTMATTER_RE.match(text)
    if not m:
        return None
    pm = PERSONA_KEY_RE.search(m.group(1))
    if not pm:
        return None
    val = pm.group(1).strip().strip('"').strip("'")
    # 拒絕含空白 / 路徑符 / .. 的怪 persona 名稱（落 _unassigned 保險）
    if not val or any(c in val for c in ("/", "\\", " ", "\t")) or ".." in val:
        return None
    return val


def plan_letters_migration() -> list[tuple[Path, Path, str]]:
    """Return [(src, dst, reason)] list. Skip already-migrated files."""
    plan: list[tuple[Path, Path, str]] = []
    if not LETTERS_DIR.exists():
        return plan
    for actor_dir in LETTERS_DIR.iterdir():
        if not actor_dir.is_dir():
            continue
        if actor_dir.name == "cross-agent":
            continue
        for entry in actor_dir.iterdir():
            # 子目錄 = 已搬遷過的 persona dir, 或 dialogues — 跳過 (避免重搬)
            if entry.is_dir():
                if entry.name == "dialogues":
                    dst = actor_dir / "_unassigned" / "dialogues"
                    plan.append((entry, dst, "dialogues/ → _unassigned/dialogues/"))
                continue
            if not entry.name.endswith(".md"):
                continue
            # _latest.md 不搬, 等下次 goodnight 自然重生
            if entry.name == "_latest.md":
                continue
            persona = read_persona_from_frontmatter(entry)
            if persona is None:
                persona = "_unassigned"
                reason = "no written_by_persona in frontmatter"
            else:
                reason = f"frontmatter written_by_persona = {persona}"
            dst = actor_dir / persona / entry.name
            plan.append((entry, dst, reason))
    return plan


def plan_baton_migration() -> list[tuple[Path, Path, str]]:
    """Return [(src, dst, reason)] for baton timestamped + _latest_*.md."""
    plan: list[tuple[Path, Path, str]] = []
    if not BATON_DIR.exists():
        return plan
    for entry in BATON_DIR.iterdir():
        if not entry.is_file():
            continue
        if not entry.name.endswith(".md"):
            continue
        # _latest_<actor>.md → 不搬 (next baton call 自然在新 path 重生)
        if entry.name.startswith("_latest_"):
            continue
        # _last_op.md 是 caller confirm, 留原處
        if entry.name == "_last_op.md":
            continue
        # <actor>_<ts>.md 形式
        m = re.match(r"^(?P<actor>[^/\\]+?)_(\d{8}T\d{6}Z)\.md$", entry.name)
        if not m:
            continue
        actor = m.group("actor")
        dst = BATON_DIR / actor / "_unassigned" / entry.name.split("_", 1)[1]
        plan.append((entry, dst, "baton timestamped — persona 不可推, 全進 _unassigned/"))
    return plan


def apply_plan(plan: list[tuple[Path, Path, str]]) -> None:
    for src, dst, _ in plan:
        dst.parent.mkdir(parents=True, exist_ok=True)
        if dst.exists():
            print(f"⚠ skip (dst exists): {dst.relative_to(REPO_ROOT)}", file=sys.stderr)
            continue
        if src.is_dir():
            shutil.move(str(src), str(dst))
        else:
            shutil.move(str(src), str(dst))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true", help="實際搬 (預設 dry-run)")
    ap.add_argument("--dry-run", action="store_true", help="只印計畫")
    args = ap.parse_args()

    apply = args.apply and not args.dry_run

    letters_plan = plan_letters_migration()
    baton_plan = plan_baton_migration()

    print(f"# kyouko-persona-binding T04 migration plan\n")
    print(f"Mode: {'APPLY' if apply else 'DRY-RUN'}\n")
    print(f"## Letters ({len(letters_plan)} entries)\n")
    for src, dst, reason in letters_plan:
        print(f"- `{src.relative_to(REPO_ROOT)}`")
        print(f"  → `{dst.relative_to(REPO_ROOT)}`")
        print(f"  - {reason}")
    print(f"\n## Baton ({len(baton_plan)} entries)\n")
    for src, dst, reason in baton_plan:
        print(f"- `{src.relative_to(REPO_ROOT)}`")
        print(f"  → `{dst.relative_to(REPO_ROOT)}`")
        print(f"  - {reason}")

    if apply:
        print(f"\n## Applying...\n")
        apply_plan(letters_plan)
        apply_plan(baton_plan)
        print(f"[OK] migration complete: {len(letters_plan)} letters + {len(baton_plan)} batons")
    else:
        print(f"\n(dry-run — 加 --apply 實際搬)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
