#!/usr/bin/env python3
"""run_cmd.py — 指路 stub。**本檔不執行任何派遣**，只印出等價的 Senate CLI 指令。

派 AgentCommand 給 Unity Editor 的入口是 `senate ucmd`；不需要 Unity 的指令走 `senate cmd`。
本檔存在的唯一理由：打慣舊指令的人會在這裡撞到一句話，而不是撞到「找不到檔案」。

退出碼一律 **2**（＝「這條路不通，照下面走」）—— 不是 0，因為什麼都沒發生。
"""
from __future__ import annotations

import sys

# 子指令 → 等價指令。**逐支列**：只寫一句「改用 senate」會讓人自己猜對照，而猜錯的那次
# 不會報錯（senate 對未宣告的旗標有預檢，但對「換了一支 Cmd」沒有意見）。
SUBCOMMANDS = [
    ("run <Type> --arg k=v",
     "senate ucmd run <Type> --persona <me> --arg k=v"),
    ("submit <Type> --arg k=v",
     "senate ucmd run <Type> --persona <me> --arg k=v --no-wait   # 印 Submitted: <cmd_id>"),
    ("wait <cmd_id>",
     "senate ucmd wait <cmd_id> --persona <me>   # --persona/--lane 要跟送出那次一致"),
    ("recompile",
     "senate cmd unity-recompile --arg persona=<me>   # 觸發＋等到那一趟結束才印"),
    ("list",
     "senate ucmd status   # 各分道的 trigger 狀態與殘量"),
    ("catalog",
     "senate cmd            # 列出所有指令\n"
     "                        senate cmd help <name>   # 單支的參數說明"),
]

# 旗標對照。左邊沒列到的（--mode / --description）在 senate 這側沒有對應物：
# 前者由 Cmd 自己決定，後者本來就只寫進 queue 供人看。
FLAGS = [
    ("--persona <p>", "--persona <p>   # 同名同義：決定 queue 路由並戳進 args"),
    ("--lane <id>", "--lane <id>     # 同名同義：queues/<persona>/queue-<id>.json"),
    ("--system", "--persona system"),
    ("--arg k=v", "--arg k=v"),
    ("--arg-file k=<路徑>", "--arg-file k=<路徑>"),
    ("--arg-stdin <KEY>", "--arg-file <KEY>=<檔>   # senate 沒有 stdin 那條，長內文一律走檔案"),
    ("--timeout <秒>", "--timeout <秒>"),
    ("--ack-timeout <秒>", "--ack-timeout <秒>   # 等前一批被取走，不是等自己跑完"),
    ("--poll-interval <秒>", "--poll-interval <秒>"),
    ("--output-file <路徑>", "--output-file <路徑>   # 跑完順便確認產物在不在（不改 exit code）"),
    ("--wait-reply <秒>",
     "senate ucmd run Tavern --arg op=wait …   然後 --arg op=wait_check --arg wait_id=<id>"),
    ("--wait-reply-from <persona>", "同上：等待引擎在 Cmd_Tavern 的 op=wait / op=wait_check"),
]


def main() -> int:
    out = sys.stderr
    print("⛔ run_cmd.py 不再執行派遣 —— 這是一支指路 stub。", file=out)
    print("", file=out)
    print("入口：", file=out)
    print("  senate ucmd run <CmdType>   派給 Unity Editor（需要 Editor 開著）", file=out)
    print("  senate cmd  <name>          不需要 Unity 的指令（本地跑完）", file=out)
    print("", file=out)
    print("子指令對照：", file=out)
    for old, new in SUBCOMMANDS:
        print(f"  run_cmd.py {old}", file=out)
        print(f"      → {new}", file=out)
    print("", file=out)
    print("旗標對照：", file=out)
    for old, new in FLAGS:
        print(f"  {old:28s} → {new}", file=out)
    print("", file=out)
    if len(sys.argv) > 1:
        # 把原本那行原樣印回去 —— 人要對照的是自己剛打的東西，不是一份泛用表。
        print("你剛打的是：run_cmd.py " + " ".join(sys.argv[1:]), file=out)
        print("", file=out)
    print("完整說明：senate --help ／ senate cmd ／ senate ucmd run", file=out)
    return 2


if __name__ == "__main__":
    sys.exit(main())
