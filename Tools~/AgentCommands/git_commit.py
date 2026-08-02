#!/usr/bin/env python3
"""
git_commit.py — commit 的最後一步：帶 persona 參數，自動組 Co-Authored-By 後提交。

# 區塊職責：只做「組 trailer + 提交」。**stage 什麼、切哪個分支、要不要 push，一律不碰** —— 那些維持原本手動流程。
# 物理意義：trailer 以前是手打的，於是它會漂：同一位 meadow 三筆 commit 出現過 (GPT)/(GPT-5)/(GPT-5.6)
#          與 anthropic/openai 兩種 domain。身分、型號、信箱三個欄位都推導自 persona 檔與信箱 registry，
#          手不碰就不會漂。
# 數值影響：信箱解析不到（哨兵 unset@invalid）預設**拒絕提交**；要硬幹得明示 --allow-unset。
#          寧可擋下也不要讓一個假位址進 git history —— history 改不掉。

用法（訊息走 stdin，跟現行 heredoc 習慣一致）:
  python git_commit.py --persona basecamp <<'EOF'
  標題行

  內文…
  EOF

  # 多位參與者 → 每人一行 trailer
  python git_commit.py --persona basecamp --persona meadow --repo Assets/Plugins/UCL_Core -m "標題"

  # 只看會組出什麼，不提交
  python git_commit.py --persona basecamp --dry-run -m "test"

exit code: 0 成功 / 2 參數或 persona 有問題 / 3 信箱未設定 / 4 沒有 staged 變更 / 5 git commit 失敗
"""

from __future__ import annotations
import argparse
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

from agent_email import resolve_email, load_persona, looks_like_email, UNSET_SENTINEL  # noqa: E402
from agent_model import resolve_model  # noqa: E402

EXIT_OK, EXIT_BAD_ARGS, EXIT_UNSET_EMAIL, EXIT_NOTHING_STAGED, EXIT_COMMIT_FAIL = 0, 2, 3, 4, 5
TRAILER_PREFIX = "Co-Authored-By:"


def git(repo: str, *args: str) -> subprocess.CompletedProcess:
    return subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True, encoding="utf-8")


def build_trailers(personas: list, allow_unset: bool) -> tuple:
    """persona 清單 → (trailer 行清單, 錯誤訊息清單)。順序保持輸入順序，重複的只留第一次。"""
    lines, problems, seen = [], [], set()
    for persona in personas:
        if persona in seen:
            continue
        seen.add(persona)
        p = load_persona(persona)
        if not p:
            problems.append(f"persona 檔不存在或讀不到：{persona}")
            continue
        agent = (p.get("agent") or "").strip()
        # 型號走解析器：有人把 agent 名填進 model 欄（實測），底層翻譯掉才不會印出 (Antigravity) 這種型號
        model_info = resolve_model(persona)
        model = model_info["model"]
        if not agent:
            problems.append(f"{persona} 的 agent 欄是空的（trailer 的身分會變成 ?）")
        info = resolve_email(persona)
        email = info["email"]
        if email == UNSET_SENTINEL or not looks_like_email(email):
            msg = f"{persona} 的信箱未設定或格式可疑（{email}）—— 到 Editor 的 Persona & Agent 管理頁設定"
            if allow_unset:
                print(f"WARN: {msg}", file=sys.stderr)
            else:
                problems.append(msg)
                continue
        lines.append(f"{TRAILER_PREFIX} {agent or '?'}@{persona}({model or '?'}) <{email}>")
    return lines, problems


def compose_message(body: str, trailers: list) -> str:
    """把 trailer 併到訊息尾端；已經有同一行就不重複加（重跑同一個指令不該長出兩份）。"""
    text = body.rstrip("\n")
    existing = {ln.strip() for ln in text.splitlines() if ln.strip().startswith(TRAILER_PREFIX)}
    fresh = [t for t in trailers if t not in existing]
    if not fresh:
        return text + "\n"
    # trailer 區塊與內文之間留一個空行；若內文尾端已經是 trailer 區塊就直接接上。
    sep = "\n" if existing and text.splitlines()[-1].strip().startswith(TRAILER_PREFIX) else "\n\n"
    return text + sep + "\n".join(fresh) + "\n"


def read_body(args) -> str:
    if args.message_file:
        return Path(args.message_file).read_text(encoding="utf-8")
    if args.message:
        return args.message
    if sys.stdin.isatty():
        return ""
    return sys.stdin.read()


def main() -> int:
    ap = argparse.ArgumentParser(description="組 Co-Authored-By 並提交（只做最後一步，不 stage 不 push）")
    ap.add_argument("--persona", action="append", default=[],
                    help="參與者 persona，可重複給；每位一行 trailer")
    ap.add_argument("--repo", default=".", help="git 工作目錄（submodule 就指到該 submodule）")
    ap.add_argument("-m", "--message", help="commit 訊息（不給就讀 stdin）")
    ap.add_argument("--message-file", help="從檔案讀 commit 訊息")
    ap.add_argument("--allow-unset", action="store_true",
                    help="信箱未設定仍提交（預設拒絕 —— 假位址進了 history 就改不掉）")
    ap.add_argument("--dry-run", action="store_true", help="只印組出來的訊息，不提交")
    args = ap.parse_args()

    if not args.persona:
        print("ERROR: 至少要一個 --persona", file=sys.stderr)
        return EXIT_BAD_ARGS

    body = read_body(args)
    if not body.strip():
        print("ERROR: commit 訊息是空的（用 -m / --message-file / stdin）", file=sys.stderr)
        return EXIT_BAD_ARGS

    trailers, problems = build_trailers(args.persona, args.allow_unset)
    if problems:
        for msg in problems:
            print(f"ERROR: {msg}", file=sys.stderr)
        return EXIT_UNSET_EMAIL
    if not trailers:
        print("ERROR: 沒有可用的 trailer", file=sys.stderr)
        return EXIT_BAD_ARGS

    message = compose_message(body, trailers)

    if args.dry_run:
        print("─── 將提交的訊息 ───")
        print(message, end="")
        print("─── （--dry-run，未提交）───")
        return EXIT_OK

    # 空提交是沉默的失敗來源：git 會回非零但訊息很像其他錯誤，先自己驗一次講清楚。
    staged = git(args.repo, "diff", "--cached", "--name-only")
    if staged.returncode != 0:
        print(f"ERROR: 讀 staged 清單失敗：{staged.stderr.strip()}", file=sys.stderr)
        return EXIT_COMMIT_FAIL
    if not staged.stdout.strip():
        print(f"ERROR: {args.repo} 沒有 staged 變更 —— 本工具只做提交，stage 請自己來", file=sys.stderr)
        return EXIT_NOTHING_STAGED

    result = subprocess.run(["git", "-C", args.repo, "commit", "-F", "-"],
                            input=message, capture_output=True, text=True, encoding="utf-8")
    if result.returncode != 0:
        print(result.stdout, end="")
        print(f"ERROR: git commit 失敗：{result.stderr.strip()}", file=sys.stderr)
        return EXIT_COMMIT_FAIL

    print(result.stdout.strip())
    sha = git(args.repo, "rev-parse", "--short", "HEAD").stdout.strip()
    print()
    for t in trailers:
        print(f"  {t}")
    print()
    # 領薪是漏最兇的一步（血證：新制上線後 source_kind=commit 曾 82 天零領取），
    # 所以把 SHA 與該貼的 meta 直接吐出來，不要求任何人記得格式。
    print(f"💰 領薪提醒 — 這筆 SHA `{sha}` 要發一則酒館公告才算數（一則訊息一個 SHA）：")
    print(f"   meta: {{\"tag\":\"commit\",\"sha\":\"{sha}\",\"category\":\"meta\"}}   --wait-reply 0")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
