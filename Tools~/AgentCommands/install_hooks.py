#!/usr/bin/env python3
"""
install_hooks.py — 把 commit-msg 驗證 hook 裝進本專案的每一層 repo（含所有 submodule）。

# 區塊職責：解決「hook 不入版控、每個 repo 各一份、新 clone 的人一個都沒有」這個結構性問題。
# 物理意義：**這支存在本身就是那個問題的證據** —— 需要有人記得跑安裝器的防護，
#          等於「防護存在 ≠ 防護生效」。所以它 (1) 一次裝好所有層，(2) --check 能查誰沒裝。
# 數值影響：只寫 .git/hooks/commit-msg；已存在且不是本 hook 時**不覆蓋**，印出來讓人自己決定 ——
#          蓋掉別人的 hook 是那種沒有錯誤訊息的破壞。

用法:
  python install_hooks.py             # 裝到所有層
  python install_hooks.py --check     # 只查哪些層裝了 / 沒裝
  python install_hooks.py --uninstall # 移除（只移除本 hook，不動別人的）
"""

from __future__ import annotations
import argparse
import os
import subprocess
import sys
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

MARKER = "# UCL commit-msg validate hook"
VALIDATOR = Path(__file__).resolve().parent / "hooks" / "commit-msg-validate.py"


def repo_root() -> Path:
    p = Path(__file__).resolve()
    for parent in p.parents:
        if (parent / ".git").is_dir():
            return parent
    return Path.cwd()


def list_repos(root: Path) -> list:
    """主 repo + 所有（含巢狀）submodule 的工作目錄。"""
    repos = [root]
    try:
        out = subprocess.run(["git", "-C", str(root), "submodule", "--quiet", "foreach", "--recursive",
                              "echo $displaypath"], capture_output=True, text=True, encoding="utf-8")
        for line in (out.stdout or "").splitlines():
            line = line.strip()
            if line:
                repos.append(root / line)
    except Exception as e:
        print(f"⚠ 列舉 submodule 失敗（只裝主 repo）：{e}", file=sys.stderr)
    return repos


def hooks_dir(repo: Path):
    """.git 可能是目錄（主 repo）或檔案（submodule 的 gitdir redirect）。"""
    dot = repo / ".git"
    if dot.is_dir():
        return dot / "hooks"
    if dot.is_file():
        try:
            text = dot.read_text(encoding="utf-8").strip()
            if text.startswith("gitdir:"):
                target = text.split("gitdir:", 1)[1].strip()
                return (repo / target).resolve() / "hooks"
        except Exception:
            return None
    return None


def hook_body() -> str:
    # 用相對於 hook 檔的絕對路徑呼叫 validator —— hook 執行時的 cwd 是 repo 根，不能靠相對路徑。
    return (f"#!/bin/sh\n{MARKER}\n"
            f'exec python "{VALIDATOR.as_posix()}" "$1"\n')


def main() -> int:
    ap = argparse.ArgumentParser(description="安裝 commit-msg 驗證 hook 到所有層 repo")
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--uninstall", action="store_true")
    args = ap.parse_args()

    if not VALIDATOR.exists():
        print(f"ERROR: 找不到 validator：{VALIDATOR}", file=sys.stderr)
        return 2

    root = repo_root()
    installed = missing = foreign = 0
    for repo in list_repos(root):
        hd = hooks_dir(repo)
        rel = repo.relative_to(root) if repo != root else Path(".")
        if hd is None:
            print(f"  ?  {rel}（找不到 .git/hooks）")
            continue
        target = hd / "commit-msg"
        current = target.read_text(encoding="utf-8") if target.exists() else ""

        if args.check:
            state = "✅ 已裝" if MARKER in current else ("⚠ 別人的 hook" if current else "○ 未裝")
            if MARKER in current: installed += 1
            elif current: foreign += 1
            else: missing += 1
            print(f"  {state}  {rel}")
            continue

        if args.uninstall:
            if MARKER in current:
                target.unlink()
                installed += 1
                print(f"  🗑 移除  {rel}")
            continue

        if current and MARKER not in current:
            # 別人的 hook 一律不蓋 —— 覆蓋是沒有錯誤訊息的破壞
            foreign += 1
            print(f"  ⚠ 跳過  {rel}（已有其他 commit-msg hook，未覆蓋）")
            continue
        hd.mkdir(parents=True, exist_ok=True)
        target.write_text(hook_body(), encoding="utf-8", newline="\n")
        try:
            os.chmod(target, 0o755)
        except Exception:
            pass
        installed += 1
        print(f"  ✅ 安裝  {rel}")

    print()
    if args.check:
        print(f"已裝 {installed} / 未裝 {missing} / 別人的 {foreign}")
        return 1 if missing else 0
    print(f"完成：{installed} 層{'移除' if args.uninstall else '安裝'}"
          + (f"，{foreign} 層因已有其他 hook 而跳過" if foreign else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
