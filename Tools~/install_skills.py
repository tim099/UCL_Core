#!/usr/bin/env python3
"""install_skills.py — Install UCL_Core skills into the host project.

UCL_Core ships skills under `Skills~/<name>/SKILL.md`. Claude Code only loads
skills from `<project-root>/.claude/skills/`, and does not recurse into
submodules. This script copies the source-of-truth skills into the host
project's `.claude/skills/` directory.

Usage (run from anywhere):
    python <UCL_Core>/Tools~/install_skills.py [options]

Options:
    --target <claude>     Target agent format. Default: claude. (cursor / agents-md / gemini planned.)
    --include <a,b,c>     Only install these skills (comma list).
    --exclude <a,b,c>     Skip these skills.
    --link                Use directory symlink/junction instead of copy. Lets edits in UCL_Core sync immediately. May fail without admin on Windows.
    --uninstall           Remove previously installed UCL skills (anything under .claude/skills/ marked with .ucl_source).
    --dry-run             Print actions without changing files.
    --quiet               Suppress per-file logs.
    --project-root <p>    Override host project root detection.

Behaviour:
    1. Locate host project root by walking up from this script's directory until
       a `.git` directory or `.claude/` directory is found (cap 8 levels).
    2. For each skill directory in `Skills~/` (skipping names starting with `_`
       or ending with `~`), install into `<root>/.claude/skills/<name>/`.
    3. Each installed skill gets a `.ucl_source` file recording the UCL_Core
       commit hash and source path — used to detect later edits.
    4. Idempotent: re-running updates only changed files. If a destination file
       has been edited locally and no longer matches the recorded source hash,
       the script warns and skips that file (does NOT overwrite).
    5. Writes `.claude/skills/.ucl_installed` as a global marker so agent-side
       self-checks can detect installation.

Exit codes:
    0  success (or nothing to do)
    1  unrecoverable error (no project root, etc.)
    2  partial — some skills skipped due to local edits or include/exclude
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Iterable


# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------

SCRIPT = Path(__file__).resolve()
UCL_CORE_ROOT = SCRIPT.parent.parent  # Tools~/install_skills.py -> UCL_Core/
SKILLS_SRC = UCL_CORE_ROOT / "Skills~"
MANIFEST = SKILLS_SRC / "_manifest.json"


# ---------------------------------------------------------------------------
# Logging helpers
# ---------------------------------------------------------------------------

class _Log:
    def __init__(self, quiet: bool, dry: bool):
        self.quiet = quiet
        self.dry = dry

    def info(self, msg: str) -> None:
        if not self.quiet:
            print(msg)

    def warn(self, msg: str) -> None:
        print(f"[warn] {msg}", file=sys.stderr)

    def err(self, msg: str) -> None:
        print(f"[error] {msg}", file=sys.stderr)

    def action(self, verb: str, target: Path) -> None:
        prefix = "[dry-run] " if self.dry else ""
        if not self.quiet:
            print(f"{prefix}{verb}: {target}")


# ---------------------------------------------------------------------------
# Project root detection
# ---------------------------------------------------------------------------

def find_project_root(override: str | None) -> Path:
    """Walk up from script location to find the *outermost* project root.

    UCL_Core is typically a submodule, so its own `.git` is a file (gitdir
    pointer) rather than a directory. We must skip past submodule boundaries
    and find the host project. Strategy:

    1. Walk up to 12 levels collecting candidates.
    2. A candidate has `.claude/` directory OR a real `.git/` *directory*
       (not a file — files indicate submodule).
    3. Prefer the outermost candidate (highest in tree) — that's the host
       project, not an intermediate submodule.

    Raises SystemExit if no candidate found.
    """
    if override:
        p = Path(override).resolve()
        if not p.is_dir():
            raise SystemExit(f"--project-root {p} is not a directory")
        return p

    candidates: list[Path] = []
    current = SCRIPT.parent
    for _ in range(12):
        has_claude = (current / ".claude").is_dir()
        has_git_dir = (current / ".git").is_dir()  # real repo, not submodule
        if has_claude or has_git_dir:
            candidates.append(current)
        if current.parent == current:
            break
        current = current.parent

    if not candidates:
        raise SystemExit(
            "Could not locate project root (no .claude/ or .git/ directory found "
            "within 12 levels of this script). Pass --project-root <path> explicitly."
        )

    # Prefer outermost candidate that has .claude/ (it's the agent-facing root).
    # Fall back to outermost .git directory.
    with_claude = [c for c in candidates if (c / ".claude").is_dir()]
    if with_claude:
        return with_claude[-1]
    return candidates[-1]


# ---------------------------------------------------------------------------
# Source metadata
# ---------------------------------------------------------------------------

def get_ucl_commit() -> str:
    """Return the current UCL_Core git commit, or 'unknown' if not in a git repo."""
    try:
        result = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=UCL_CORE_ROOT,
            capture_output=True,
            text=True,
            check=False,
        )
        if result.returncode == 0:
            return result.stdout.strip()
    except (FileNotFoundError, OSError):
        pass
    return "unknown"


def load_manifest() -> dict:
    if not MANIFEST.is_file():
        raise SystemExit(f"Manifest not found: {MANIFEST}")
    return json.loads(MANIFEST.read_text(encoding="utf-8"))


# 區塊職責：定位 UCL_SkillConfigAsset 的「真實 source of truth」目錄。
# 物理意義：Templates~ 只是初始模板 — Core module 安裝時會把它複製到專案的
#          <UnityAssets>/.BuiltinModules/.../Core/UCL_Assets/UCL_SkillConfigAsset/，
#          之後使用者在 Editor 改開關也是同步到 .BuiltinModules(不回寫 Templates~)。
#          所以判定 disabled 必須讀 runtime .BuiltinModules; Templates~ 只在「專案還沒
#          materialize BuiltinModules(全新專案)」時當 fallback 預設。
# 數值影響：從 UCL_CORE_ROOT 往上走找含 .BuiltinModules 的祖先(UCL_Core 必在 Unity Assets 內,
#          .BuiltinModules 在 Assets 根) → 命中即回該 UCL_SkillConfigAsset dir;
#          BuiltinModules 不存在 → 回 Templates~ 預設 dir。跨專案通用(不寫死 CardGame/Assets)。
_SKILLCFG_REL = ("ModulesRoot", "Modules", "Core", "UCL_Assets", "UCL_SkillConfigAsset")


def resolve_skill_config_dir() -> Path:
    cur = UCL_CORE_ROOT
    for _ in range(10):
        builtin = cur / ".BuiltinModules"
        if builtin.is_dir():
            return builtin.joinpath(*_SKILLCFG_REL)
        if cur.parent == cur:
            break
        cur = cur.parent
    # fallback：全新專案還沒 materialize BuiltinModules → 用 Templates~ 模板預設
    return UCL_CORE_ROOT.joinpath("Templates~", "Assets", ".BuiltinModules", *_SKILLCFG_REL)


# 區塊職責：讀 UCL_SkillConfigAsset 判定哪些 skill 設為不裝（Enabled=false）
# 物理意義：Plan_SkillManager_PerSkill_Toggle — 「透過 UCL_SkillConfigAsset 開關 skill」。
#          asset JSON 欄位 "Enabled"(UCL_Asset 序列化 strip m_; 相容舊 "m_Enabled")，ID=檔名(skill 名)。
# 數值影響：回傳 Enabled=false 的 skill 名 set；目錄/欄位缺失 → 視為未停用(空集)。
def load_skill_config_disabled() -> set[str]:
    cfg_dir = resolve_skill_config_dir()
    disabled: set[str] = set()
    if not cfg_dir.is_dir():
        return disabled
    for f in cfg_dir.glob("*.json"):
        try:
            data = json.loads(f.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            continue
        enabled = data.get("Enabled", data.get("m_Enabled", True))
        if enabled is False:
            disabled.add(f.stem)
    return disabled


def discover_skills() -> list[str]:
    """List skill directories from Skills~/ (filesystem truth)."""
    if not SKILLS_SRC.is_dir():
        raise SystemExit(f"Skills~ source not found: {SKILLS_SRC}")
    skills = []
    for child in sorted(SKILLS_SRC.iterdir()):
        if not child.is_dir():
            continue
        name = child.name
        if name.startswith("_") or name.endswith("~"):
            continue
        if not (child / "SKILL.md").is_file():
            continue
        skills.append(name)
    return skills


# 區塊職責：對「目前選定的一組 skill」算出穩定 aggregate SHA1。
# 物理意義：取代過往以 git commit 比對 stale 的作法 — commit bump 但 skill 內容沒動時不該被視為 stale。
# 數值影響：以 (skill name, posix relative path, file SHA1) 三元組依字典序串入單一 hasher，
#          Editor 端 (UCL_AgentSkillManagerPage) 用同樣演算法重算後比對 .ucl_installed.source_hash。
#          只算 source 側（Skills~/<name>/），不關心安裝端目錄；antigravity 走 SKILL.md only 的子集。
def compute_source_hash(skill_names: list[str], target: str) -> str:
    h = hashlib.sha1()
    for name in sorted(skill_names):
        src = SKILLS_SRC / name
        if not src.is_dir():
            continue
        files = sorted(p for p in src.rglob("*") if p.is_file())
        # 排序 by posix-style relative path 以確保跨 OS 一致
        entries: list[tuple[str, Path]] = []
        for f in files:
            rel = f.relative_to(SKILLS_SRC).as_posix()
            entries.append((rel, f))
        entries.sort(key=lambda e: e[0])
        for rel, f in entries:
            h.update(rel.encode("utf-8"))
            h.update(b"\0")
            h.update(file_sha1(f).encode("ascii"))
            h.update(b"\0")
    return h.hexdigest()


# ---------------------------------------------------------------------------
# File ops
# ---------------------------------------------------------------------------

def file_sha1(path: Path) -> str:
    h = hashlib.sha1()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


# 區塊職責: marker JSON 的 atomic 寫入 — 先寫 temp 檔再 os.replace 原子換入。
# 物理意義: marker (.ucl_source / .ucl_installed) 若在 write_text 中途被殺 / 磁碟滿會留半截 JSON
#          → 下輪 JSONDecodeError → prior_hashes={} → 所有 recorded 變 None
#          → local-edit 保護「靜默全關」(basecamp R2 review 點名, 跟毒化同級的靜默失效)。
# 數值影響: os.replace 在同一 filesystem 上為原子操作; temp 檔留同目錄確保同 fs。
def write_json_atomic(path: Path, data: dict, trailing_newline: bool = False) -> None:
    text = json.dumps(data, indent=2, ensure_ascii=False)
    if trailing_newline:
        text += "\n"
    tmp = path.parent / (path.name + ".tmp")
    tmp.write_text(text, encoding="utf-8")
    os.replace(tmp, path)


def get_antigravity_trigger_frontmatter(skill_name: str) -> str:
    if skill_name == "ucl-chat-tavern":
        return '{ on_intent: ["進入酒館", "聊天酒館", "進酒館", "去酒館", "enter tavern", "自言自語", "跟自己討論", "solo think", "腦力激盪", "solo brainstorm", "自我辯論"] }'
    elif skill_name == "ucl-commit":
        return '{ on_intent: ["commit", "提交", "git commit"] }'
    elif skill_name == "ucl-compile-error":
        return '{ on_files: ["*.cs"], on_intent: ["編譯錯", "compile error", "CS0103", "CS0117", "CS1503", "CS0246", "asmdef", "assembly"] }'
    elif skill_name == "ucl-create-cmd":
        return '{ on_intent: ["新增 AgentCommand", "新增指令", "Create Cmd", "Create Command"] }'
    elif skill_name == "ucl-hook-setup":
        return '{ on_intent: ["Hook Setup", "Hook 設置", "設置 Hook", "install skills"] }'
    elif skill_name == "ucl-watch-video":
        return '{ on_intent: ["watch video", "看影片", "觀看影片", "YouTube", "影片心得", "影片轉錄"] }'
    else:
        return '"always_on"'

def transform_antigravity_frontmatter(content: str, skill_name: str) -> str:
    trigger_val = get_antigravity_trigger_frontmatter(skill_name)
    if content.startswith("---"):
        parts = content.split("---", 2)
        if len(parts) >= 3:
            frontmatter = parts[1]
            if "trigger:" not in frontmatter:
                frontmatter = f"trigger: {trigger_val}\n{frontmatter}"
                return f"---\n{frontmatter}---{parts[2]}"
    return f"---\ntrigger: {trigger_val}\n---\n\n{content}"

def copy_skill(src_dir: Path, dst_dir: Path, log: _Log, force: bool = False, target: str = "claude") -> tuple[int, int]:
    """Copy contents of src_dir to dst_dir. Returns (copied, skipped_due_to_edit)."""
    copied = 0
    skipped = 0

    source_marker = dst_dir / ".ucl_source"
    prior_hashes: dict[str, str] = {}
    if source_marker.is_file():
        try:
            data = json.loads(source_marker.read_text(encoding="utf-8"))
            prior_hashes = data.get("file_hashes", {}) or {}
        except (json.JSONDecodeError, OSError):
            prior_hashes = {}

    new_hashes: dict[str, str] = {}

    # 區塊職責: 逐檔三分支顯式記錄 (basecamp R2 review 定案, 防 marker 毒化的核心語意)。
    # 物理意義: marker file_hashes 必須恆等於「最後一次成功寫入 dst 的內容 hash」:
    #          copied      → 記 src_hash (剛寫入的就是 source 內容)
    #          up-to-date  → 記 src_hash (dst == source, 順帶治癒舊毒 marker)
    #          skipped     → 保留舊 recorded (dst 沒動, 記錄就不能動 — 舊版在此無條件記 src_hash,
    #                        導致「跳過一次 = marker 與磁碟永久脫鉤 = 之後每輪都誤判 local edit」自我毒化)
    # 數值影響: 任一分支都會在 new_hashes 留下記錄 — 檔案不會從 marker 消失
    #          (消失 → recorded=None → 下輪 silent overwrite, local-edit 保護靜默失效)。
    for src_file in src_dir.rglob("*"):
        if not src_file.is_file():
            continue
        rel = src_file.relative_to(src_dir)
        rel_key = str(rel).replace(os.sep, "/")
        dst_file = dst_dir / rel

        is_antigravity_skill_md = (target == "antigravity" and src_file.name == "SKILL.md")
        if is_antigravity_skill_md:
            src_content = src_file.read_text(encoding="utf-8")
            transformed_content = transform_antigravity_frontmatter(src_content, src_dir.name)
            src_hash = hashlib.sha1(transformed_content.encode("utf-8")).hexdigest()
        else:
            src_hash = file_sha1(src_file)

        if dst_file.is_file():
            if is_antigravity_skill_md:
                dst_content = dst_file.read_text(encoding="utf-8")
                dst_hash = hashlib.sha1(dst_content.encode("utf-8")).hexdigest()
            else:
                dst_hash = file_sha1(dst_file)
            if dst_hash == src_hash:
                new_hashes[rel_key] = src_hash  # up-to-date → 記 src_hash (= dst 實際內容)
                continue  # already up to date
            recorded = prior_hashes.get(rel_key)
            if not force and recorded is not None and dst_hash != recorded:
                log.warn(
                    f"local edit detected, skipping: {dst_file} "
                    f"(rerun with --force-overwrite to replace, or delete the file)"
                )
                skipped += 1
                new_hashes[rel_key] = recorded  # skipped → 保留舊 recorded, 不可記 src_hash
                continue

        log.action("copy", dst_file)
        if not log.dry:
            dst_file.parent.mkdir(parents=True, exist_ok=True)
            if is_antigravity_skill_md:
                dst_file.write_text(transformed_content, encoding="utf-8")
            else:
                shutil.copy2(src_file, dst_file)
        copied += 1
        new_hashes[rel_key] = src_hash  # copied → 記 src_hash (剛寫入的內容)

    # 區塊職責: orphan 清理 (Fix4) — source 端已刪除/改名、但安裝端還殘留的舊檔。
    # 物理意義: copy 只增不刪 → source 刪檔後安裝端殘留 → Editor 端 per-skill drift 比對永遠亮
    #          「⚠改動」, 連 force 重裝都修不掉 (只蓋不刪)。安全邊界 = prior marker 記錄:
    #          只刪「自己裝過的」(marker file_hashes 有記錄), 使用者自建檔不在記錄內絕不誤刪。
    # 數值影響: orphan 被使用者改過 (hash ≠ recorded) 且非 force → 不刪 + warning + 保留記錄
    #          (破壞看得見, 同 copy / uninstall 路徑的 local-edit 保護語意, 留給 --force-overwrite);
    #          刪除成功 → 從 new_hashes 移除記錄, marker 自然收斂。
    for rel_key, rec_hash in prior_hashes.items():
        if rel_key in new_hashes:
            continue  # source 還在, 非 orphan
        orphan = dst_dir / rel_key
        if not orphan.is_file():
            continue  # 安裝端也已不存在, 記錄自然消失
        if not force and file_sha1(orphan) != rec_hash:
            log.warn(
                f"orphan with local edit, keeping: {orphan} "
                f"(source removed this file; rerun with --force-overwrite to delete)"
            )
            skipped += 1
            new_hashes[rel_key] = rec_hash  # 保留記錄 — 下輪仍視為 orphan 可見可刪
            continue
        log.action("remove orphan", orphan)
        if not log.dry:
            orphan.unlink()

    if not log.dry:
        source_marker.parent.mkdir(parents=True, exist_ok=True)
        write_json_atomic(
            source_marker,
            {
                "ucl_core_commit": get_ucl_commit(),
                "source": str(src_dir.relative_to(UCL_CORE_ROOT)),
                "file_hashes": new_hashes,
            },
        )

    return copied, skipped


def link_skill(src_dir: Path, dst_dir: Path, log: _Log) -> bool:
    """Create a directory symlink/junction. Returns True on success."""
    if dst_dir.exists() or dst_dir.is_symlink():
        if dst_dir.is_symlink() and Path(os.readlink(dst_dir)) == src_dir:
            return True  # already linked correctly
        log.warn(f"target exists, removing before link: {dst_dir}")
        if not log.dry:
            if dst_dir.is_symlink() or dst_dir.is_file():
                dst_dir.unlink()
            else:
                shutil.rmtree(dst_dir)

    log.action("link", dst_dir)
    if log.dry:
        return True

    try:
        if os.name == "nt":
            # Windows: try directory junction first (no admin needed)
            subprocess.check_call(
                ["cmd", "/c", "mklink", "/J", str(dst_dir), str(src_dir)],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
        else:
            os.symlink(src_dir, dst_dir, target_is_directory=True)
        return True
    except (OSError, subprocess.CalledProcessError) as exc:
        log.err(f"link failed for {dst_dir}: {exc}. Fall back to --link removed; rerun without --link.")
        return False


def detect_local_drift(dst_dir: Path) -> list[str]:
    """回傳 dst_dir 內相對 .ucl_source.file_hashes 有 local 改動的檔名清單。
    區塊職責: toggle-off / uninstall 前偵測使用者手動改過的檔, 讓『破壞看得見』。
    物理意義: 重算每檔 sha1 vs .ucl_source 記錄的 hash; 不符 = local edit。symlink / 無 marker → 視為無 drift。"""
    marker = dst_dir / ".ucl_source"
    if dst_dir.is_symlink() or not marker.is_file():
        return []
    try:
        recorded = (json.loads(marker.read_text(encoding="utf-8")).get("file_hashes", {}) or {})
    except (json.JSONDecodeError, OSError):
        return []
    drifted: list[str] = []
    for rel, rec_hash in recorded.items():
        f = dst_dir / rel
        if f.is_file() and file_sha1(f) != rec_hash:
            drifted.append(rel)
    return drifted


def remove_skill(dst_dir: Path, log: _Log, force: bool = False) -> bool:
    """Remove a previously installed skill if it has the .ucl_source marker.
    force=False 時偵測到 local drift(使用者手動改過) → 警告並跳過(不靜默刪);
    force=True → 照刪。(basecamp R2 review: toggle-off 破壞要看得見)"""
    if not dst_dir.exists():
        return False
    if not (dst_dir / ".ucl_source").is_file() and not dst_dir.is_symlink():
        log.warn(f"no .ucl_source marker, skipping: {dst_dir}")
        return False
    if not force:
        drifted = detect_local_drift(dst_dir)
        if drifted:
            log.warn(
                f"local edit detected in {dst_dir.name} ({', '.join(drifted)}); "
                f"skipping uninstall to avoid silent data loss "
                f"(rerun with --force-overwrite to remove anyway, or back up your edits first)"
            )
            return False
    log.action("remove", dst_dir)
    if not log.dry:
        if dst_dir.is_symlink():
            dst_dir.unlink()
        else:
            shutil.rmtree(dst_dir)
    return True





# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def parse_csv(value: str | None) -> set[str]:
    if not value:
        return set()
    return {s.strip() for s in value.split(",") if s.strip()}


def filter_skills(all_skills: Iterable[str], include: set[str], exclude: set[str]) -> list[str]:
    selected = list(all_skills)
    if include:
        selected = [s for s in selected if s in include]
    if exclude:
        selected = [s for s in selected if s not in exclude]
    return selected


# 區塊職責：CLI 主要進入點，分析執行參數並分發拷貝任務至指定 Agent 端。
# 物理意義：這協調了整個 Skill 機制的安裝與解除安裝生命週期，動態將對應的目錄或單一檔案副本部署到專案主體中。
# 數值影響：這修改了專案 `.claude/skills/` 或 `.agents/rules/` 目錄下的靜態檔案結構。
def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Install UCL_Core skills into the host project.")
    parser.add_argument("--target", default="claude", choices=["claude", "antigravity"], help="Agent target format.")
    parser.add_argument("--include", help="Comma-separated list of skill names to include (others skipped).")
    parser.add_argument("--exclude", help="Comma-separated list of skill names to skip.")
    parser.add_argument("--include-optional", dest="include_optional", action="store_true",
                        help="Also install manifest optional=true skills (default-OFF) in a full install.")
    parser.add_argument("--link", action="store_true", help="Use symlink/junction instead of copy (Claude only).")
    parser.add_argument("--uninstall", action="store_true", help="Remove previously installed UCL skills.")
    parser.add_argument("--dry-run", action="store_true", help="Print actions without changing files.")
    parser.add_argument("--quiet", action="store_true", help="Suppress per-file logs.")
    parser.add_argument("--project-root", help="Override host project root detection.")
    parser.add_argument("--force-overwrite", "--force", action="store_true", help="Force overwrite skills that have local edits.")
    args = parser.parse_args(argv)

    log = _Log(quiet=args.quiet, dry=args.dry_run)

    project_root = find_project_root(args.project_root)
    # 根據 --target 參數動態判定目標放置根目錄
    if args.target == "antigravity":
        skills_dst_root = project_root / ".agents" / "skills"
        
        # Cleanup legacy antigravity rules (.md files)
        legacy_rules_dir = project_root / ".agents" / "rules"
        if legacy_rules_dir.is_dir():
            for md_file in legacy_rules_dir.glob("*.md"):
                marker = legacy_rules_dir / f"{md_file.name}.ucl_source"
                if marker.is_file():
                    log.info(f"Removing legacy antigravity rule: {md_file}")
                    if not args.dry_run:
                        md_file.unlink()
                        marker.unlink()
            try:
                if not any(legacy_rules_dir.iterdir()) and not args.dry_run:
                    legacy_rules_dir.rmdir()
            except OSError:
                pass
    else:
        skills_dst_root = project_root / ".claude" / "skills"

    log.info(f"UCL_Core: {UCL_CORE_ROOT}")
    log.info(f"Project root: {project_root}")
    log.info(f"Target dir:  {skills_dst_root}")

    discovered = discover_skills()
    if not discovered:
        log.err("No skills found in Skills~/ — did you run from the right UCL_Core?")
        return 1

    include = parse_csv(args.include)
    exclude = parse_csv(args.exclude)
    selected = filter_skills(discovered, include, exclude)

    # 區塊職責: 預設關閉(default-OFF) — manifest optional=true 的 skill 不在預設安裝集
    # 物理意義: 淘汰候選 / 低頻 skill 標 optional=true → 預設不裝(非破壞, 留在 Skills~/, 隨時可裝);
    #          只在『無 --include(全量裝) 且 沒 --include-optional』時排除; 顯式 --include <name> 仍可裝。
    # 數值影響: 全量安裝集縮小; per-skill 開關(Page)或 --include 可單獨裝回。
    if not include and not getattr(args, "include_optional", False):
        try:
            optional_names = {s["name"] for s in load_manifest().get("skills", []) if s.get("optional")}
        except Exception:
            optional_names = set()
        # 預設不裝集 = manifest optional ∪ UCL_SkillConfigAsset Enabled=false (asset 為主, optional 為 fallback)
        off_names = optional_names | load_skill_config_disabled()
        if off_names:
            skipped_off = [s for s in selected if s in off_names]
            selected = [s for s in selected if s not in off_names]
            if skipped_off:
                log.info(f"Default-OFF skipped: {skipped_off}  (use --include <name> or --include-optional to install)")

            # 區塊職責: reconcile — 對「設為不裝(off) 但目前實體還裝著」的 skill 主動解除安裝。
            # 物理意義: Tim 要求「disable 後同步要解除安裝該 skill」。先前只做『跳過安裝』
            #          (off 不進 selected), 但已裝的目錄不會自己消失 → 同步 = 裝該裝的 + 刪該停的。
            #          帶 drift 保護(remove_skill force=False): 被本地改過的會警告跳過, 不靜默刪。
            # 數值影響: 移除 off 且實體存在的 skill dir + 從 .ucl_installed.installed_skills 剔除。
            reconcile_removed: list[str] = []
            for name in skipped_off:
                dst = skills_dst_root / name
                if dst.exists() and remove_skill(dst, log, force=args.force_overwrite):
                    reconcile_removed.append(name)
            # 同步 marker：把被 reconcile 移除的 skill 從 installed_skills 剔除
            marker = skills_dst_root / ".ucl_installed"
            if reconcile_removed and marker.is_file() and not args.dry_run:
                try:
                    mdata = json.loads(marker.read_text(encoding="utf-8"))
                    prev = mdata.get("installed_skills", []) or []
                    remaining = [s for s in prev if s not in reconcile_removed]
                    if remaining != prev:
                        mdata["installed_skills"] = remaining
                        mdata["source_hash"] = compute_source_hash(remaining, args.target)
                        write_json_atomic(marker, mdata, trailing_newline=True)
                except (json.JSONDecodeError, OSError):
                    pass
            if reconcile_removed:
                log.info(f"Reconcile-uninstalled (disabled but were installed): {reconcile_removed}")

    log.info(f"Skills found:    {discovered}")
    log.info(f"Skills selected: {selected}")

    if not args.dry_run:
        skills_dst_root.mkdir(parents=True, exist_ok=True)

    exit_code = 0

    if args.uninstall:
        removed: list[str] = []
        for name in selected:
            if remove_skill(skills_dst_root / name, log, force=args.force_overwrite):
                removed.append(name)
        # 區塊職責: per-skill uninstall 要更新 marker 而非整個刪 (basecamp R2 review)
        # 物理意義: 只 uninstall 子集(--include 單 skill)時, 從 installed_skills 移除被刪的、保留其餘 +
        #          重算 aggregate source_hash; 若清空才刪 marker。--include 沒給(全 uninstall) → 刪 marker。
        marker = skills_dst_root / ".ucl_installed"
        if marker.is_file() and not args.dry_run:
            partial = bool(include) or bool(exclude)   # 有 include/exclude = 子集操作
            try:
                mdata = json.loads(marker.read_text(encoding="utf-8"))
                prev = mdata.get("installed_skills", []) or []
            except (json.JSONDecodeError, OSError):
                prev, partial = [], False
            remaining = [s for s in prev if s not in removed]
            if partial and remaining:
                mdata["installed_skills"] = remaining
                mdata["source_hash"] = compute_source_hash(remaining, args.target)
                write_json_atomic(marker, mdata, trailing_newline=True)
                log.info(f"Marker updated: {len(remaining)} skill(s) still installed.")
            else:
                marker.unlink()
        log.info(f"Uninstall complete. removed={removed}")
        return 0

    total_copied = 0
    total_skipped = 0
    for name in selected:
        src = SKILLS_SRC / name
        dst = skills_dst_root / name
        if args.link:
            if args.target == "antigravity":
                log.warn("--link is not supported for antigravity target (needs trigger injection), falling back to copy.")
            else:
                ok = link_skill(src, dst, log)
                if not ok:
                    exit_code = 2
                continue
        copied, skipped = copy_skill(src, dst, log, force=args.force_overwrite, target=args.target)
        total_copied += copied
        total_skipped += skipped
        if skipped:
            exit_code = 2

    # Global marker
    # source_hash：對 selected 這組 skill 的 source 內容算 aggregate SHA1，
    # 用於 Editor 端 (UCL_AgentSkillManagerPage) 判斷是否 stale；取代以往的 commit-only 比對。
    marker = skills_dst_root / ".ucl_installed"
    if not args.dry_run:
        # 區塊職責: per-skill install 要 merge 進 installed_skills 而非覆蓋 (basecamp R2 review)
        # 物理意義: --include/--exclude 子集安裝時, 取『既有 installed_skills ∪ selected』, 保留沒動到的;
        #          全量安裝(無 include/exclude) → 直接用 selected。source_hash 對最終集合重算。
        final_skills = list(selected)
        if (include or exclude) and marker.is_file():
            try:
                prev = json.loads(marker.read_text(encoding="utf-8")).get("installed_skills", []) or []
            except (json.JSONDecodeError, OSError):
                prev = []
            merged = list(prev)
            for s in selected:
                if s not in merged:
                    merged.append(s)
            final_skills = merged
        write_json_atomic(
            marker,
            {
                "ucl_core_commit": get_ucl_commit(),
                "source_hash": compute_source_hash(final_skills, args.target),
                "installed_skills": final_skills,
                "target": args.target,
                "mode": "link" if (args.link and args.target != "antigravity") else "copy",
            },
        )

    log.info(
        f"\nDone. copied={total_copied} skipped={total_skipped} "
        f"selected={len(selected)} mode={'link' if (args.link and args.target != 'antigravity') else 'copy'}"
    )
    if total_skipped:
        log.warn(
            "Some files were skipped due to local edits. Resolve manually or remove the file and rerun."
        )

    return exit_code


if __name__ == "__main__":
    sys.exit(main())
