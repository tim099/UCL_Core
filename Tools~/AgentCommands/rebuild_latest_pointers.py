#!/usr/bin/env python3
"""kyouko-persona-binding T04 follow-up — rebuild _latest.md pointers per persona subdir.

T04 migration 把 letters/baton 搬進 <actor>/<persona>/ 但沒重 build _latest.md
pointer (per-persona)。Morning ritual SOP 第二步 cat <persona>/_latest.md 會 file
not found。本 script 對每個 persona subdir 找最新 timestamped letter 複製成 _latest.md。

Letters: baton/letters/<persona>/_latest.md ← 該 dir 最新 *.md (Tim 2026-06-15 砍 agent 層)
Baton:   baton/<actor>/<persona>/_latest.md ← 該 dir 最新 *.md (baton 仍 2 層)
"""
from __future__ import annotations
import re
import shutil
import sys
from pathlib import Path


def _find_repo_root() -> Path:
    p = Path(__file__).resolve()
    for ancestor in p.parents:
        if (ancestor / "AgentCommands" / "ChatTavern" / "baton").is_dir():
            return ancestor
    raise SystemExit(f"[ERR] repo root not found from {p}")


REPO_ROOT = _find_repo_root()
BATON_DIR = REPO_ROOT / "AgentCommands" / "ChatTavern" / "baton"
LETTERS_DIR = BATON_DIR / "letters"

TS_RE = re.compile(r"(\d{8}T\d{6}Z|\d{4}-\d{2}-\d{2}T\d{6}Z)")


def latest_ts(name: str) -> tuple[int, str]:
    """Sort key — files w/ timestamp prioritized over special names.
    Returns (1, ts) for timestamped, (0, name) for others (sorts last when max())."""
    m = TS_RE.search(name)
    if m:
        return (1, m.group(1))
    return (0, name)


def rebuild_persona_flat(root: Path, label: str) -> int:
    """For each immediate <persona>/ subdir, copy newest *.md to _latest.md.

    Tim 2026-06-15 拍板: letters 只分 persona (砍 agent 層), 故 letters 走 1 層
    <persona>/ 結構 (baton 仍走 2 層 <actor>/<persona>/, 見 main)。
    """
    count = 0
    if not root.exists():
        return 0
    for persona_dir in root.iterdir():
        if not persona_dir.is_dir() or persona_dir.name == "cross-agent":
            continue
        mds = [
            p for p in persona_dir.iterdir()
            if p.is_file() and p.suffix == ".md" and p.name != "_latest.md"
            and not p.name.startswith(".")
        ]
        if not mds:
            continue
        newest = max(mds, key=lambda p: latest_ts(p.name))
        target = persona_dir / "_latest.md"
        shutil.copyfile(str(newest), str(target))
        count += 1
        rel = target.relative_to(REPO_ROOT)
        print(f"  [{label}] {rel}  <-  {newest.name}")
    return count


def main() -> int:
    print(f"# rebuild_latest_pointers (kyouko-persona-binding T04 follow-up)\n")
    print(f"## Letters _latest.md rebuild")
    n_letters = rebuild_persona_flat(LETTERS_DIR, "letter")
    print(f"\n## Baton _latest.md rebuild")
    # Baton structure: BATON_DIR/<actor>/<persona>/<ts>.md
    # 排除 constitution / letters 子樹
    fake_actor_root = BATON_DIR
    skip = {"letters", "constitution"}
    actor_dirs = [d for d in fake_actor_root.iterdir()
                  if d.is_dir() and d.name not in skip]
    count_baton = 0
    for actor_dir in actor_dirs:
        for persona_dir in actor_dir.iterdir():
            if not persona_dir.is_dir():
                continue
            mds = [
                p for p in persona_dir.iterdir()
                if p.is_file() and p.suffix == ".md" and p.name != "_latest.md"
            ]
            if not mds:
                continue
            newest = max(mds, key=lambda p: latest_ts(p.name))
            target = persona_dir / "_latest.md"
            shutil.copyfile(str(newest), str(target))
            count_baton += 1
            print(f"  [baton]  {target.relative_to(REPO_ROOT)}  <-  {newest.name}")
    print(f"\n[OK] {n_letters} letter _latest.md + {count_baton} baton _latest.md rebuilt")
    return 0


if __name__ == "__main__":
    sys.exit(main())
