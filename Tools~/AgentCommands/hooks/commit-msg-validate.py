#!/usr/bin/env python3
"""
commit-msg-validate.py — commit-msg hook：只擋「已有 trailer 但格式或 domain 對不上」。

# 區塊職責：抓沒走 git_commit.py 的人。走工具的人在工具內已經被驗過，這裡是第二道。
# 物理意義：**不擋沒有 trailer 的 commit** —— Tim 自己手改的、機器產生的 bump，本來就不該掛
#          agent trailer。只擋「宣稱是某 persona 做的、但署名內容與 registry 對不上」那種：
#          那是會靜默寫進不可變 history 的失真（2026-08-03 三方共識）。
# 數值影響：解析不到 registry（工具鏈不在該 repo 內）→ **放行並印一行說明**，不擋。
#          防護本身不該變成「新 clone 的人 commit 不了」的路障。

安裝：python <UCL_Core>/Tools~/AgentCommands/install_hooks.py
手動測：python commit-msg-validate.py <commit-msg-file>

exit 0 = 通過（含無 trailer / 無法驗證）；exit 1 = 擋下
"""

from __future__ import annotations
import re
import sys
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

TRAILER_RE = re.compile(r"^Co-Authored-By:\s*(?P<agent>[^@]+)@(?P<persona>[^(]+)\((?P<model>[^)]*)\)\s*<(?P<email>[^>]+)>\s*$")


def load_resolvers():
    """把 agent_email / agent_model 掛進來；掛不上回 (None, None) —— 由 caller 放行。"""
    here = Path(__file__).resolve().parent.parent          # …/Tools~/AgentCommands
    if str(here) not in sys.path:
        sys.path.insert(0, str(here))
    try:
        from agent_email import resolve_email, load_persona    # type: ignore
        from agent_model import format_trailer_model           # type: ignore
        return (resolve_email, load_persona, format_trailer_model)
    except Exception:
        return (None, None, None)


def main() -> int:
    if len(sys.argv) < 2:
        print("commit-msg hook: 缺少訊息檔參數（放行）", file=sys.stderr)
        return 0
    try:
        message = Path(sys.argv[1]).read_text(encoding="utf-8")
    except Exception as e:
        print(f"commit-msg hook: 讀不到訊息檔（放行）：{e}", file=sys.stderr)
        return 0

    trailers = [ln for ln in message.splitlines() if ln.strip().startswith("Co-Authored-By:")]
    if not trailers:
        return 0        # 沒有 trailer 不是違規 —— 人手改的 commit 本來就不該掛

    resolve_email, load_persona, format_trailer_model = load_resolvers()
    if resolve_email is None:
        print("commit-msg hook: 找不到 agent_email / agent_model（無法驗證，放行）", file=sys.stderr)
        return 0

    problems = []
    for line in trailers:
        m = TRAILER_RE.match(line.strip())
        if not m:
            problems.append(f"格式不合：{line.strip()}\n"
                            f"    應為 Co-Authored-By: <agent>@<persona>(<model>) <email>")
            continue
        persona = m.group("persona").strip()
        if not load_persona(persona):
            problems.append(f"查無此 persona：{persona}（{line.strip()}）")
            continue
        expect_email = resolve_email(persona)["email"]
        expect_model = format_trailer_model(persona)["text"]
        if m.group("email").strip() != expect_email:
            problems.append(f"{persona} 的信箱對不上：寫了 {m.group('email').strip()}，"
                            f"registry 是 {expect_email}")
        if m.group("model").strip() != expect_model:
            problems.append(f"{persona} 的型號對不上：寫了 {m.group('model').strip()}，"
                            f"registry 是 {expect_model}")

    if not problems:
        return 0

    print("", file=sys.stderr)
    print("⛔ commit-msg hook 擋下：trailer 與 registry 對不上", file=sys.stderr)
    for p in problems:
        print(f"  • {p}", file=sys.stderr)
    print("", file=sys.stderr)
    print("  這道檢查只擋「已經有 trailer 但內容不符」—— 沒有 trailer 的 commit 一律放行。", file=sys.stderr)
    print("  修法：別手打 trailer，改用", file=sys.stderr)
    print("    python <UCL_Core>/Tools~/AgentCommands/git_commit.py --persona <你> --repo <repo> -m \"...\"", file=sys.stderr)
    print("  真的要硬過：git commit --no-verify（但那筆會永遠留在 history 裡）", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
