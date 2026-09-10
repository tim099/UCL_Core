#!/usr/bin/env python3
"""git_commit.py — ⛔ 已退場（TASK-0187，2026-09-10）。指路 stub，**不做任何事**。

# 區塊職責：把走舊入口的人明確擋下並指到 `senate cmd commit`。
# 物理意義：commit 是**不可回復**的三件事（git commit／酒館一則貼文／單子狀態）。
#          一支「還能跑但已經不是正路」的舊工具，會讓那三件事從兩個入口各自發生一次 ——
#          而同一個 SHA 貼兩次是**付兩次錢**。⇒ 這裡不留備援路，只留指路牌。
# 數值影響：**零副作用** —— 不 stage、不 commit、不公告、不推單。一律 exit 2。

搬到哪裡：
    senate cmd commit   （git 與 trailer 本地跑，Editor 沒開也組得出 trailer；
                          酒館公告領薪與單號推進委派 Editor）

搬家帶來的三個實際差別（⛔ 不是換個名字而已）：
  ① **公告失敗拆成兩個出口**：`exit 6` ＝確定沒發（補發安全）／`exit 7` ＝不知道（先回讀，別補發）。
     本檔舊版把兩者併成一個 6 並無條件印「手動補一則」—— 而它自己的註解就寫著
     「同一個 SHA 貼兩次 = 付兩次錢」。那條紀律寫在程式碼裡，卻沒有寫進出口。
  ② trailer 的 `(vendor / version)` 不再取決於「提交的人站在哪棵樹」
     （兩張表已寫死進 `SCP_AgentModelRegistry`）。
  ③ 多位參與者從可重複的 `--persona` 改成 `--arg personas=a,b,c`。

⚠ 沒有搬過去的一格：`--strict-email-source`。它擋的是「信箱取自快照而非現場值」，
  而新入口直接讀 `profile/` 的檔案、**沒有快照那一段** ⇒ 那個條件永遠不成立。
  ⛔ 收一個永遠不會生效的旗標，比不收它更糟（它看起來像一道防線）。
"""

from __future__ import annotations
import sys

# ⚠ 舊旗標 → 新參數的對照寫在這裡而不是文件裡：走到這支的人**手上就是舊指令**，
#   他要的是「我這一行怎麼改」，不是一份要另外去讀的文件。
_FLAG_MAP = [
    ("--repo <r>",              "--arg repo=<r>"),
    ("--persona <p>（可重複）",  "--arg personas=<p1,p2>（逗號分隔）"),
    ("--message-file <f>",      "--arg-file message=<f>"),
    ("-m \"…\"",                 "--arg-file message=<檔>　⚠ 長文一律走檔案"),
    ("--expect-files <n>",      "--arg expect_files=<n>"),
    ("--allow-unset",           "--arg allow_unset=1"),
    ("--dry-run",               "--arg dry_run=1"),
    ("--announce-body-file <f>", "--arg-file announce_body=<f>"),
    ("--bump-of <sha>",         "--arg bump_of=<sha>"),
    ("--strict-email-source",   "⛔ 沒有對應 —— 新入口沒有快照那一段，條件永遠不成立"),
]


def main() -> int:
    e = sys.stderr
    print("⛔ git_commit.py 已退場（TASK-0187，2026-09-10）—— 本工具**不做任何事**"
          "（沒有 stage、沒有 commit、沒有公告、沒有推單）。", file=e)
    print("   新入口：`senate cmd commit`", file=e)
    print("", file=e)
    print("   senate cmd commit \\", file=e)
    print("       --arg repo=<該層 repo 路徑> \\", file=e)
    print("       --arg personas=<你>[,<協作者>…] \\", file=e)
    print("       --arg letters_root=<letters 根> --arg data_root=<AgentCommands 根> \\", file=e)
    print("       --arg region=<現地區域 ID>   # ⚠ 少了它 trailer 的 agent 欄會缺席，而那會被擋下", file=e)
    print("       --arg expect_files=<N> \\", file=e)
    print("       --arg-file message=<訊息檔>", file=e)
    print("", file=e)
    print("   舊旗標 → 新參數：", file=e)
    for old, new in _FLAG_MAP:
        print(f"     {old:28} → {new}", file=e)
    print("", file=e)
    print("   ⚠ 出口碼變了，**6 與 7 的處置相反**：", file=e)
    print("     · exit 6 ＝ commit 落地、公告**確定沒發** ⇒ 補發是安全的", file=e)
    print("     · exit 7 ＝ commit 落地、公告**不知道**（沒等到回執）"
          "⇒ ⛔ **先回讀再決定**，同一個 SHA 貼兩次是付兩次錢", file=e)
    print("   完整 SOP：skill `ucl-commit`", file=e)
    return 2


if __name__ == "__main__":
    sys.exit(main())
