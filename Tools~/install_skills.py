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

def file_sha1(path: Path) -> str:
    h = hashlib.sha1()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def copy_skill(src_dir: Path, dst_dir: Path, log: _Log) -> tuple[int, int]:
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

    for src_file in src_dir.rglob("*"):
        if not src_file.is_file():
            continue
        rel = src_file.relative_to(src_dir)
        dst_file = dst_dir / rel

        src_hash = file_sha1(src_file)
        new_hashes[str(rel).replace(os.sep, "/")] = src_hash

        if dst_file.is_file():
            dst_hash = file_sha1(dst_file)
            if dst_hash == src_hash:
                continue  # already up to date
            recorded = prior_hashes.get(str(rel).replace(os.sep, "/"))
            if recorded is not None and dst_hash != recorded:
                log.warn(
                    f"local edit detected, skipping: {dst_file} "
                    f"(rerun with --force-overwrite to replace, or delete the file)"
                )
                skipped += 1
                continue

        log.action("copy", dst_file)
        if not log.dry:
            dst_file.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src_file, dst_file)
        copied += 1

    if not log.dry:
        source_marker.parent.mkdir(parents=True, exist_ok=True)
        source_marker.write_text(
            json.dumps(
                {
                    "ucl_core_commit": get_ucl_commit(),
                    "source": str(src_dir.relative_to(UCL_CORE_ROOT)),
                    "file_hashes": new_hashes,
                },
                indent=2,
                ensure_ascii=False,
            ),
            encoding="utf-8",
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


def remove_skill(dst_dir: Path, log: _Log) -> bool:
    """Remove a previously installed skill if it has the .ucl_source marker."""
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


# 區塊職責：將 UCL 的 Skill 目錄（含 SKILL.md）轉寫為單一 Markdown 檔案，並自動注入 Antigravity 專屬的 always_on 觸發器。
# 物理意義：這將 UCL 的動態工作流 Skill 標準，無縫對映到 Gemini/Antigravity 的全域規則載入目錄，確保其能夠自動識別並常駐載入。
# 數值影響：不涉及效能損耗，生成一個新規則檔案 (如 ucl-chat-tavern.md) 與一個對應的 .ucl_source 追蹤檔，並更新全域 .ucl_installed 檔。
def copy_skill_antigravity(src_dir: Path, dst_file: Path, log: _Log) -> tuple[int, int]:
    """Copy SKILL.md from src_dir to dst_file with frontmatter transformation."""
    # 參數 src_dir: 來源 Skill 的目錄路徑 (e.g. Skills~/ucl-chat-tavern/)
    # 參數 dst_file: 目標規則檔案路徑 (e.g. .agents/rules/ucl-chat-tavern.md)
    # 參數 log: 用於記錄與輸出 log 的日誌輔助物件
    # 傳回值: (已拷貝檔案數, 已略過檔案數) 的 tuple 格式，用於後續狀態匯總
    copied = 0  # 初始化已拷貝檔案數為 0
    skipped = 0  # 初始化已略過檔案數為 0

    skill_file = src_dir / "SKILL.md"  # 定義來源 Skill 說明檔的完整路徑
    if not skill_file.is_file():  # 判斷來源 Skill 說明檔是否存在，若不存在則直接返回
        return 0, 0  # 回傳零複製與零略過

    source_marker = dst_file.parent / f"{dst_file.name}.ucl_source"  # 定義用來防寫與追蹤的防修剪標記檔案路徑
    prior_hashes: dict[str, str] = {}  # 用於儲存先前記錄之 SHA-1 檔案雜湊值的字典
    if source_marker.is_file():  # 若存在舊的防寫標記檔則讀取其記錄
        try:  # 嘗試安全載入 JSON 標記資料
            data = json.loads(source_marker.read_text(encoding="utf-8"))  # 讀取 JSON 並解析為 dict
            prior_hashes = data.get("file_hashes", {}) or {}  # 提取檔案雜湊值映射，若無則預設為空 dict
        except (json.JSONDecodeError, OSError):  # 捕捉 JSON 解析或檔案讀取異常
            prior_hashes = {}  # 發生異常時將舊雜湊值重設為空 dict

    src_content = skill_file.read_text(encoding="utf-8")  # 讀取來源 SKILL.md 的完整文字內容

    # 根據不同的 Skill 名稱決定最適合的動態觸發屬性值
    skill_name = src_dir.name  # 取得當前 Skill 的目錄名稱 (例如 ucl-chat-tavern)
    if skill_name == "ucl-chat-tavern":  # 針對聊天酒館 Skill 的觸發設定
        trigger_val = '{ on_intent: ["進入酒館", "聊天酒館", "進酒館", "去酒館", "enter tavern", "自言自語", "跟自己討論", "solo think", "腦力激盪", "solo brainstorm", "自我辯論"] }'
    elif skill_name == "ucl-commit":  # 針對 Commit 規範 Skill 的觸發設定
        trigger_val = '{ on_intent: ["commit", "提交", "git commit"] }'
    elif skill_name == "ucl-compile-error":  # 針對編譯錯誤排查 Skill 進行雙軌併行觸發 (檔案模式與意圖模式並行)
        trigger_val = '{ on_files: ["*.cs"], on_intent: ["編譯錯", "compile error", "CS0103", "CS0117", "CS1503", "CS0246", "asmdef", "assembly"] }'
    elif skill_name == "ucl-create-cmd":  # 針對新增指令 Skill 的觸發設定
        trigger_val = '{ on_intent: ["新增 AgentCommand", "新增指令", "Create Cmd", "Create Command"] }'
    elif skill_name == "ucl-hook-setup":  # 針對 Hook 配置 Skill 的觸發設定
        trigger_val = '{ on_intent: ["Hook Setup", "Hook 設置", "設置 Hook", "install skills"] }'
    else:  # 若不匹配已知任何 Skill
        trigger_val = '"always_on"'  # 預設採用全域常駐啟用屬性

    # 前置轉換：將動態計算得出的 trigger 屬性注入 YAML Frontmatter 中
    transformed_content = src_content  # 初始化轉換後的內容為原始內容
    if src_content.startswith("---"):  # 如果原始內容開頭包含標準 YAML 邊界
        parts = src_content.split("---", 2)  # 將內容以 --- 切割成最多三個部分 (開頭、Frontmatter內文、Body)
        if len(parts) >= 3:  # 確保成功切出完整的 Frontmatter 區塊
            frontmatter = parts[1]  # 取得 Frontmatter 區塊內容文字
            if "trigger:" not in frontmatter:  # 判斷是否已經有 trigger 屬性
                frontmatter = f"trigger: {trigger_val}\n{frontmatter}"  # 在 Frontmatter 開頭注入常駐啟用屬性
                transformed_content = f"---\n{frontmatter}---{parts[2]}"  # 重新組裝為帶有觸發屬性的完整 Markdown
    else:  # 如果檔案開頭沒有 YAML 邊界
        transformed_content = f"---\ntrigger: {trigger_val}\n---\n\n{src_content}"  # 自動為其加上 YAML 頭部並注入屬性

    transformed_hash = hashlib.sha1(transformed_content.encode("utf-8")).hexdigest()  # 計算轉換後內容之 SHA-1 雜湊值以進行防寫校驗

    if dst_file.is_file():  # 如果目標規則 Markdown 檔案已經存在
        dst_content = dst_file.read_text(encoding="utf-8")  # 讀取現有目標檔案的內容
        dst_hash = hashlib.sha1(dst_content.encode("utf-8")).hexdigest()  # 計算目標現有內容之 SHA-1 雜湊值
        if dst_hash == transformed_hash:  # 若目標現有雜湊等於轉換後的最新內容雜湊
            return 0, 0  # 已經是最新，直接返回 (0 拷貝, 0 略過)
        # 為了處理 frontmatter 的 trigger 注入，我們這裡需要比對雜湊記錄
        recorded = prior_hashes.get("SKILL.md")  # 從先前的防寫紀錄中獲取 SKILL.md 當初對應的雜湊 (此值已修正為轉換後的 hash)
        if recorded is not None and dst_hash != recorded:  # 若雜湊記錄存在且目標已被使用者手動修改
            log.warn(f"local edit detected, skipping: {dst_file}")  # 輸出警告日誌，避免覆寫開發者的手工修改
            skipped += 1  # 累計略過計數
            return 0, skipped  # 提前返回並回報略過

    log.action("copy", dst_file)  # 輸出即將複製的動作 log
    if not log.dry:  # 如果非乾跑 (dry-run) 模式，則正式執行寫檔
        dst_file.parent.mkdir(parents=True, exist_ok=True)  # 遞迴建立目標所在的 .agents/rules/ 目錄
        dst_file.write_text(transformed_content, encoding="utf-8")  # 寫入包含 always_on 觸發器的 Markdown 檔案

        source_marker.write_text(  # 正式寫入伴隨的防寫標記 JSON 檔案
            json.dumps(  # 將元數據序列化為 JSON
                {
                    "ucl_core_commit": get_ucl_commit(),  # 寫入當前 UCL_Core 倉庫的 commit 雜湊值
                    "source": str(skill_file.relative_to(UCL_CORE_ROOT)),  # 記錄來源 SKILL.md 相對於 UCL_Core 根目錄的路徑
                    "file_hashes": {"SKILL.md": transformed_hash},  # 將轉換後實際寫入內容之雜湊記錄於對照表中，修正了原本比對 untransformed 雜湊的 bug
                },
                indent=2,  # 使用兩空格縮排，保持排版精美
                ensure_ascii=False,  # 允許非 ASCII 字元，防止中文亂碼
            ),
            encoding="utf-8",  # 指定以 UTF-8 編碼儲存
        )
    copied += 1  # 累計拷貝成功計數
    return copied, skipped  # 傳回計數結果


# 區塊職責：安全移除先前安裝的 Antigravity 規則 Markdown 檔案與對應的 .ucl_source 追蹤標記檔。
# 物理意義：清理不再使用的專案規則配置，防止過時的 UCL_Core 動態工作流繼續干擾 Gemini/Antigravity Agent 的日常運作。
# 數值影響：刪除 .md 規則與 .ucl_source 追蹤檔，使 .agents/rules/ 回歸初始乾淨狀態。
def remove_skill_antigravity(dst_file: Path, log: _Log) -> bool:
    """Remove installed Antigravity rule and its sibling source marker."""
    # 參數 dst_file: 待刪除的目標規則檔案路徑 (e.g. .agents/rules/ucl-chat-tavern.md)
    # 參數 log: 用於記錄與輸出 log 的日誌輔助物件
    # 傳回值: 若成功執行刪除或目標不存在則回傳布林值
    source_marker = dst_file.parent / f"{dst_file.name}.ucl_source"  # 定義對應的防寫追蹤標記檔路徑
    if not dst_file.exists():  # 判斷目標 Markdown 規則檔案是否存在
        return False  # 如果檔案本來就不存在，直接回傳 False

    if not source_marker.is_file():  # 判斷是否存在對應的 .ucl_source 標記檔，保護開發者自建的其他規則檔不被誤刪
        log.warn(f"no .ucl_source marker, skipping removal: {dst_file}")  # 輸出警示日誌
        return False  # 回傳 False 略過安全刪除

    log.action("remove", dst_file)  # 輸出刪除動作日誌
    if not log.dry:  # 如果不是乾跑模式則正式刪檔
        if dst_file.is_file():  # 判定為一般檔案
            dst_file.unlink()  # 刪除目標 Markdown 規則檔
        if source_marker.is_file():  # 判定追蹤標記檔存在
            source_marker.unlink()  # 刪除防寫追蹤標記檔
    return True  # 成功完成刪除程序，回傳 True


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
    parser.add_argument("--link", action="store_true", help="Use symlink/junction instead of copy (Claude only).")
    parser.add_argument("--uninstall", action="store_true", help="Remove previously installed UCL skills.")
    parser.add_argument("--dry-run", action="store_true", help="Print actions without changing files.")
    parser.add_argument("--quiet", action="store_true", help="Suppress per-file logs.")
    parser.add_argument("--project-root", help="Override host project root detection.")
    args = parser.parse_args(argv)

    log = _Log(quiet=args.quiet, dry=args.dry_run)

    project_root = find_project_root(args.project_root)
    # 根據 --target 參數動態判定目標放置根目錄
    if args.target == "antigravity":
        skills_dst_root = project_root / ".agents" / "rules"
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

    log.info(f"Skills found:    {discovered}")
    log.info(f"Skills selected: {selected}")

    if not args.dry_run:
        skills_dst_root.mkdir(parents=True, exist_ok=True)

    exit_code = 0

    if args.uninstall:
        for name in selected:
            if args.target == "antigravity":
                remove_skill_antigravity(skills_dst_root / f"{name}.md", log)
            else:
                remove_skill(skills_dst_root / name, log)
        marker = skills_dst_root / ".ucl_installed"
        if marker.is_file() and not args.dry_run:
            marker.unlink()
        log.info("Uninstall complete.")
        return 0

    total_copied = 0
    total_skipped = 0
    for name in selected:
        src = SKILLS_SRC / name
        if args.target == "antigravity":
            if args.link:
                log.warn("--link is not supported for single-file antigravity rules, falling back to copy.")
            copied, skipped = copy_skill_antigravity(src, skills_dst_root / f"{name}.md", log)
            total_copied += copied
            total_skipped += skipped
            if skipped:
                exit_code = 2
        else:
            dst = skills_dst_root / name
            if args.link:
                ok = link_skill(src, dst, log)
                if not ok:
                    exit_code = 2
                continue
            copied, skipped = copy_skill(src, dst, log)
            total_copied += copied
            total_skipped += skipped
            if skipped:
                exit_code = 2

    # Global marker
    marker = skills_dst_root / ".ucl_installed"
    if not args.dry_run:
        marker.write_text(
            json.dumps(
                {
                    "ucl_core_commit": get_ucl_commit(),
                    "installed_skills": selected,
                    "target": args.target,
                    "mode": "link" if (args.link and args.target != "antigravity") else "copy",
                },
                indent=2,
                ensure_ascii=False,
            ),
            encoding="utf-8",
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
