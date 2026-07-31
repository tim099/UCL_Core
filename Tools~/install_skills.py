#!/usr/bin/env python3
"""install_skills.py — Install UCL_Core skills into the host project.

UCL_Core ships skills under `Skills~/<name>/SKILL.md`. Claude Code only loads
skills from `<project-root>/.claude/skills/`, and does not recurse into
submodules. This script copies the source-of-truth skills into the host
project's `.claude/skills/` directory.

Usage (run from anywhere):
    python <UCL_Core>/Tools~/install_skills.py [options]

Options:
    --target <claude|antigravity|codex>
                          Target agent format. Default: claude. Codex installs into .codex/skills.
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
       or ending with `~`), install into the target workspace skill directory.
    3. Each installed skill gets a `.ucl_source` file recording only the source
       path (a presence/provenance marker). No per-file hashes and no git commit
       are stored: installed copies are a disposable mirror of Skills~/, and
       staleness is judged by direct content comparison (source vs installed),
       not by stored hashes (which churn) — see UCL_AgentSkillManagerPage.
    4. Idempotent mirror: re-running overwrites only files whose content differs
       from source; identical files are skipped, and installed files with no
       corresponding source file (orphans) are removed. Installed copies are not
       hand-edited, so there is no local-edit protection. On full installs
       (no --include/--exclude), installed skill *directories* whose source was
       removed from Skills~ (retired skills, marked by .ucl_source) are also
       uninstalled — otherwise the Editor page would judge them Stale forever.
    5. Writes `<target-skill-dir>/.ucl_installed` as a global marker so agent-side
       self-checks can detect installation.

Exit codes:
    0  success (or nothing to do)
    1  unrecoverable error (no project root, etc.)
    2  partial — some skills skipped due to local edits or include/exclude
"""

from __future__ import annotations

import argparse
import json
import os
import re
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


# ---------------------------------------------------------------------------
# File ops
# ---------------------------------------------------------------------------

# 區塊職責: marker JSON 的 atomic 寫入 — 先寫 temp 檔再 os.replace 原子換入。
# 物理意義: marker (.ucl_source / .ucl_installed) 若在 write_text 中途被殺 / 磁碟滿會留半截 JSON
#          → 下輪 JSONDecodeError。atomic 寫避免半截檔。
# 數值影響: os.replace 在同一 filesystem 上為原子操作; temp 檔留同目錄確保同 fs。
def write_json_atomic(path: Path, data: dict, trailing_newline: bool = False) -> None:
    text = json.dumps(data, indent=2, ensure_ascii=False)
    if trailing_newline:
        text += "\n"
    tmp = path.parent / (path.name + ".tmp")
    tmp.write_text(text, encoding="utf-8")
    os.replace(tmp, path)


# ---------------------------------------------------------------------------
# Antigravity trigger 自動發現 (Claude 式) — Tim 2026-07-26 拍板 C 方案
#   痛點: 舊版 trigger 硬編碼在 install_skills.py + UCL_AgentSkillManagerPage.cs 兩處 per-skill
#         map, 新增 skill 要同改兩檔、漏一個就落 always_on。
#   解法: trigger 詞本來就寫在每個 SKILL.md description 的「觸發詞…:」行 (SOT co-located,
#         跟 Claude 一樣)。改成從 SKILL.md 自身取, 不再中央硬編碼:
#         優先序 (A) frontmatter 顯式 on_intent 欄 → (B) parse 描述「觸發詞」行 → (C) always_on(warn)。
#         .cs 端 (UCL_AgentSkillManagerPage.AntigravityTrigger) 改用同源泛型 parser, 斷雙寫同步負擔。
# ---------------------------------------------------------------------------

def _extract_trigger_words(frontmatter: str) -> list[str]:
    """從 frontmatter(含 description block)的「觸發詞…:」行抽觸發關鍵字清單。
    容忍格式變體(觸發詞 / 觸發詞包含 / 觸發詞 (case-insensitive…):)、分隔 / ／ 、、跨行續接。抓不到回 []。"""
    m = re.search(r"觸發詞[^\n:：]*[:：]\s*(.*)", frontmatter)
    if not m:
        return []
    tail = frontmatter[m.start(1):]
    collected: list[str] = []
    for i, raw in enumerate(tail.split("\n")):
        s = raw.strip()
        if i == 0:
            collected.append(s)
            continue
        if not s:
            break
        # 續行判定: 含分隔符 / ／ 、 或 '-' 起頭 = 觸發詞續接; 否則視為描述散文, 停
        # 額外終止: 觸發詞後常接「跨 agent 通用 …」「對應 …」散文, 這些行也含 / → 明確擋掉
        if any(k in s for k in ("跨 agent", "對應", "本 skill", "完整")):
            break
        if any(sep in s for sep in ("/", "／", "、")) or s.startswith("-"):
            collected.append(s)
        else:
            break
    # 每行去掉開頭的 '- ' 子類 bullet 標記 (否則相鄰行會黏成「X - Y」融合 token)
    blob = " ".join(re.sub(r"^\s*-\s+", " ", ln) for ln in collected)
    # 觸發詞清單不含句號「。」— 句號後一律是散文, 截斷 (解 canvas/ding 同行尾巴衝進 prose)
    blob = blob.split("。")[0]
    words: list[str] = []
    # 分隔: / ／ 、 以及 ' - '/' – ' (多行 block 的子類 bullet 邊界)
    for part in re.split(r"[/／、]|\s[-–—]\s", blob):
        p = re.sub(r"（[^）]*）", "", part)   # 去全形括號註解
        p = re.sub(r"\([^)]*\)", "", p)       # 去半形括號註解
        # 富 block 雜訊「**粗體分類**: 詞」「Tim → agent 正向: 詞」— 僅在含粗體/箭頭時取 label 後半
        # (不動一般冒號, 免把 HH:mm / work session status 這類含冒號的正常觸發詞砍壞)
        if ("**" in p or "→" in p) and ("：" in p or ":" in p):
            p = re.split(r"[:：]", p)[-1]
        p = p.replace("**", "").replace("→", " ")
        p = p.strip().strip("`").strip("。，,、 ").strip().lstrip("-*").strip()
        # 丟殘留標頭/散文碎片: 仍含粗體殘骸、過長、或明顯非觸發詞
        if p and len(p) <= 40 and "**" not in p:
            words.append(p)
    seen: set[str] = set()
    out: list[str] = []
    for w in words:
        if w not in seen:
            seen.add(w)
            out.append(w)
    return out[:40]

def _json_str_list(words: list[str]) -> str:
    return "[" + ", ".join(json.dumps(w, ensure_ascii=False) for w in words) + "]"

def derive_antigravity_trigger(content: str, skill_name: str) -> str:
    """Claude 式自動發現: trigger 從 SKILL.md 自身取, 不用中央硬編碼 map。
    優先序: (A) frontmatter 顯式 on_intent → (B) 描述「觸發詞」行 parse → (C) always_on(fallback+warn)。
    新增 skill 只要在自己 SKILL.md 寫觸發詞(本來就有), 零 install_skills.py 編輯。"""
    frontmatter = ""
    if content.startswith("---"):
        parts = content.split("---", 2)
        if len(parts) >= 3:
            frontmatter = parts[1]
    # 顯式 on_files 欄 (e.g. compile-error: on_files: ["*.cs"] — 檔案類型自動觸發), 有則併入結果
    mf = re.search(r"^\s*on_files\s*:\s*(\[.*\])\s*$", frontmatter, re.MULTILINE)
    on_files = mf.group(1).strip() if mf else None

    def _wrap(intent_body: str) -> str:
        if on_files:
            return "{ on_files: %s, on_intent: %s }" % (on_files, intent_body)
        return "{ on_intent: %s }" % intent_body

    # (A) 顯式 on_intent 欄 override (YAML flow list, e.g. on_intent: ["看直播","陪看"])
    m = re.search(r"^\s*on_intent\s*:\s*(\[.*\])\s*$", frontmatter, re.MULTILINE)
    if m:
        return _wrap(m.group(1).strip())
    # (B) parse 描述「觸發詞」行
    words = _extract_trigger_words(frontmatter)
    if words:
        return _wrap(_json_str_list(words))
    # 只有 on_files 沒 on_intent (純檔案觸發 skill)
    if on_files:
        return "{ on_files: %s }" % on_files
    # (C) fallback — 誠實 warn, 不靜默
    sys.stderr.write(
        f"[install_skills] ⚠ {skill_name}: SKILL.md 無 on_intent/on_files 欄且抓不到「觸發詞」行 "
        f"→ trigger=always_on (建議描述加『觸發詞:』行或 frontmatter 加 on_intent)\n"
    )
    return '"always_on"'

def transform_antigravity_frontmatter(content: str, skill_name: str) -> str:
    if content.startswith("---"):
        parts = content.split("---", 2)
        if len(parts) >= 3:
            frontmatter = parts[1]
            # 作者已在 SKILL.md 顯式宣告 trigger: → 原樣保留 (最高優先, 修掉舊版 double-wrap bug)
            if "trigger:" in frontmatter:
                return content
            trigger_val = derive_antigravity_trigger(content, skill_name)
            # frontmatter(=parts[1]) 已以 '\n' 起首; 直接前置 trigger 值, 不再多加 '\n'
            # (舊版多一個 '\n' 會在 trigger 行後留一空行 → 剝行比對時殘留、誤判 drift)
            frontmatter = f"trigger: {trigger_val}{frontmatter}"
            return f"---\n{frontmatter}---{parts[2]}"
    # 無 frontmatter 的退化情況: 包一層並注入衍生 trigger
    trigger_val = derive_antigravity_trigger(content, skill_name)
    return f"---\ntrigger: {trigger_val}\n---\n\n{content}"

def copy_skill(src_dir: Path, dst_dir: Path, log: _Log, force: bool = False, target: str = "claude") -> tuple[int, int]:
    """Sync src_dir → dst_dir as a pure mirror. Returns (copied, skipped).

    Installed copies are a disposable mirror of the source-of-truth Skills~/;
    they are NOT hand-edited (per Tim 2026-07-14). So there is no local-edit
    protection and no stored per-file hashes: each file is overwritten unless
    its content is already identical (direct content compare), and installed
    files with no corresponding source file (orphans) are removed. `force` is
    accepted for signature compatibility but no longer changes behaviour here.
    `skipped` is always 0 (kept for the (copied, skipped) return contract).
    """
    copied = 0

    source_marker = dst_dir / ".ucl_source"
    src_rel_keys: set[str] = set()

    # 逐檔：內文相同 → 跳過；不同 / 不存在 → 覆蓋。（不算 hash，直接比對內文）
    # Antigravity 的 SKILL.md 安裝時注入 trigger frontmatter，故比對/寫入用轉換後內文。
    for src_file in src_dir.rglob("*"):
        if not src_file.is_file():
            continue
        rel = src_file.relative_to(src_dir)
        rel_key = str(rel).replace(os.sep, "/")
        src_rel_keys.add(rel_key)
        dst_file = dst_dir / rel

        transformed = (target == "antigravity" and src_file.name == "SKILL.md")
        expected_text = transform_antigravity_frontmatter(src_file.read_text(encoding="utf-8"), src_dir.name) if transformed else None

        if dst_file.is_file():
            if transformed:
                if dst_file.read_text(encoding="utf-8") == expected_text:
                    continue  # already up to date
            elif dst_file.read_bytes() == src_file.read_bytes():
                continue      # already up to date

        log.action("copy", dst_file)
        if not log.dry:
            dst_file.parent.mkdir(parents=True, exist_ok=True)
            if transformed:
                dst_file.write_text(expected_text, encoding="utf-8")
            else:
                shutil.copy2(src_file, dst_file)
        copied += 1

    # 區塊職責: orphan 清理 — 已裝端有、但 source 端已無的檔（source 刪除/改名後的殘留）。
    # 物理意義: 已裝 = 純鏡像，source 沒有的就不該留。除 .ucl_source 本身（源端沒有的安裝標記）。
    # 數值影響: 直接刪，不做 local-edit 判斷（不考慮手改已裝副本）。
    if dst_dir.is_dir():
        for dst_file in list(dst_dir.rglob("*")):
            if not dst_file.is_file():
                continue
            rel_key = str(dst_file.relative_to(dst_dir)).replace(os.sep, "/")
            if rel_key == ".ucl_source":
                continue
            if rel_key not in src_rel_keys:
                log.action("remove orphan", dst_file)
                if not log.dry:
                    dst_file.unlink()

    if not log.dry:
        source_marker.parent.mkdir(parents=True, exist_ok=True)
        write_json_atomic(
            source_marker,
            {
                # 純存在/來源標記。不記 file_hashes（已裝視為 source 純鏡像，狀態改由 direct content
                # compare 判定）也不記 ucl_core_commit（會隨 commit churn）。
                "source": str(src_dir.relative_to(UCL_CORE_ROOT)),
            },
        )

    return copied, 0


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


def remove_skill(dst_dir: Path, log: _Log, force: bool = False) -> bool:
    """Remove a previously installed skill if it has the .ucl_source marker.

    Installed copies are a disposable mirror of source (not hand-edited), so
    removal is unconditional. `force` accepted for signature compatibility."""
    if not dst_dir.exists():
        return False
    if not (dst_dir / ".ucl_source").is_file() and not dst_dir.is_symlink():
        log.warn(f"no .ucl_source marker, skipping: {dst_dir}")
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
# 數值影響：這修改了專案 `.claude/skills/` 或 `.agents/skills/` 目錄下的靜態檔案結構。
def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Install UCL_Core skills into the host project.")
    parser.add_argument("--target", default="claude", choices=["claude", "antigravity", "codex"], help="Agent target format.")
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
        
        # 區塊職責：清理舊版 Antigravity rules 目錄，讓 Antigravity skill 根只保留目前鏡像。
        # 物理意義：Antigravity 讀 .agents/skills；遺留的 .agents/rules 不是目前載入來源。
        # 數值影響：只刪除帶 .ucl_source marker 的 UCL 舊規則，不會碰使用者自行建立的規則。
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
    elif args.target == "codex":
        skills_dst_root = project_root / ".codex" / "skills"
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
            #          已裝 = 純鏡像 → 無條件移除(remove_skill 不做 local-edit 判斷)。
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
                        mdata.pop("source_hash", None)  # 舊 marker 遺留欄位, 順手清掉
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
        # 物理意義: 只 uninstall 子集(--include 單 skill)時, 從 installed_skills 移除被刪的、保留其餘;
        #          若清空才刪 marker。--include 沒給(全 uninstall) → 刪 marker。
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
                mdata.pop("source_hash", None)  # 舊 marker 遺留欄位, 順手清掉
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

    # 區塊職責: orphan skill 目錄清理 — 已裝端有 .ucl_source、但 Skills~ 源已整個刪除的 skill。
    # 物理意義: 源 skill 被刪/改名後(例: ucl-waiter 於 SLIM 整併移除), 已裝殘留沒有 source 可同步,
    #          Editor 端 direct content compare 永遠判 Stale → 「同步到當前版本」按了也不會轉綠。
    #          已裝 = 純鏡像 → source 不存在的整個 skill 目錄一併移除, 同步後狀態才會收斂到 Synced。
    # 數值影響: 只在全量安裝(無 --include/--exclude)時執行, 子集操作不動未選中的目錄;
    #          比對基準用 discovered(Skills~ 現存全集)而非 selected — default-OFF 的 skill 源還在,
    #          不算 orphan(它們由上方 reconcile 分支處理)。
    if not include and not exclude:
        orphan_removed: list[str] = []
        discovered_set = set(discovered)
        if skills_dst_root.is_dir():
            for child in sorted(skills_dst_root.iterdir()):
                if not child.is_dir() or child.name in discovered_set:
                    continue
                if not (child / ".ucl_source").is_file():
                    continue  # 非本工具裝的目錄(使用者自己放的 skill)不動
                if remove_skill(child, log, force=args.force_overwrite):
                    orphan_removed.append(child.name)
        if orphan_removed:
            log.info(f"Orphan-uninstalled (source removed from Skills~): {orphan_removed}")

    # Global marker（installed_skills 清單 + target/mode；不存 hash/commit，
    # Editor 端狀態改由 direct content compare 判定，見 UCL_AgentSkillManagerPage）
    marker = skills_dst_root / ".ucl_installed"
    if not args.dry_run:
        # 區塊職責: per-skill install 要 merge 進 installed_skills 而非覆蓋 (basecamp R2 review)
        # 物理意義: --include/--exclude 子集安裝時, 取『既有 installed_skills ∪ selected』, 保留沒動到的;
        #          全量安裝(無 include/exclude) → 直接用 selected。
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
