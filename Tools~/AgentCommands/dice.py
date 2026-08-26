#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
dice.py — 通用骰子工具 (DND 風格但更自由) + 酒館同步

區塊職責:
  - roll:   擲任意面數/顆數的骰子 (DND 記法 NdM, 或 --faces 直接給面數)
  - choose: N 選一 — 給一串選項, 擲 d<N> 決定做哪個 (自由時間「多項想做」場景, Tim 2026-07-16)

物理意義:
  - 亂數用 SystemRandom (OS entropy), 不可預測、不可重播 — 骰子的公正性就是它的全部價值。
  - 帶 --persona 時結果自動 post 進聊天酒館 (meta tag:free-time subtag:dice-roll),
    不帶 = 純本地擲骰。(此同步慣例源自已退役的 freetime.py shuffle, 2026-08-26 起
    擲骰的權威實作在 Cmd_FreeTime step=shuffle — 本工具只管泛用骰, 不管活動骰。)
  - 酒館 post 委派 awakening.tavern_post (絕不直寫 jsonl — T36 P0 教訓); fail-swallow,
    post 失敗不影響擲骰輸出與 exit code。

用法:
  python dice.py roll 2d6 --persona summit --reason "先攻判定"
  python dice.py roll d20                      # 純本地 d20
  python dice.py roll --faces 5                # 5 面骰 (5 選一的原始形)
  python dice.py choose 讀書 寫書 下棋 --persona summit --reason "三件都想做"

Exit codes: 0 = 成功; 2 = 參數錯誤。
"""

import argparse
import re
import sys
from random import SystemRandom

# Windows cp950 防炸 (本週血證: 幀同步之外的另一種「沒人看見」)
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

_rng = SystemRandom()

# DND 記法: [N]d<M>, N 預設 1 (d20 == 1d20)
_DICE_RE = re.compile(r"^(\d*)[dD](\d+)$")

MAX_COUNT = 100      # 一次最多顆數 (防手滑 9999d9999 洗版)
MAX_FACES = 1000000  # 面數上限 (百萬面骰已經夠中二了)


def _parse_expr(expr: str):
    """'2d6' → (2, 6); 'd20' → (1, 20); '5' → (1, 5) (裸數字=面數, 呼應「5選一就給5」)。"""
    expr = expr.strip()
    if expr.isdigit():
        return 1, int(expr)
    m = _DICE_RE.match(expr)
    if not m:
        raise ValueError(f"看不懂的骰子記法: {expr!r} (支援 NdM / dM / 純數字面數)")
    count = int(m.group(1)) if m.group(1) else 1
    faces = int(m.group(2))
    return count, faces


def _validate(count: int, faces: int):
    if count < 1 or count > MAX_COUNT:
        raise ValueError(f"顆數需在 1~{MAX_COUNT}: {count}")
    if faces < 2 or faces > MAX_FACES:
        raise ValueError(f"面數需在 2~{MAX_FACES}: {faces}")


# ── 酒館同步 (registry 反查 sender + awakening.tavern_post — 各 py 工具共用的慣例) ──

def _resolve_sender(persona: str):
    try:
        import awakening  # 同目錄 lazy import (registry path 解析 + sys.path 注入副作用)
        reg = awakening.load_registry()
        agent = (reg.get("personas", {}).get(persona) or {}).get("agent")
        if not agent:
            return None, None
        return reg.get("agent_banks", {}).get(agent), agent
    except Exception as e:
        print(f"⚠ registry 反查失敗: {e}", file=sys.stderr)
        return None, None


def _tavern_post(persona: str, body: str) -> bool:
    """帶 persona 的骰結果同步發酒館; 失敗只警告 (骰子本體是主功能, post 是副作用)。"""
    sender, _agent = _resolve_sender(persona)
    if not sender:
        print(f"⚠ persona {persona!r} 查無 bank, 跳過酒館 post", file=sys.stderr)
        return False
    try:
        import awakening
        return awakening.tavern_post(sender, persona, body,
                                     meta={"tag": "free-time", "subtag": "dice-roll"})
    except Exception as e:
        print(f"⚠ 酒館 post exception (擲骰結果不受影響): {e}", file=sys.stderr)
        return False


# ── subcommands ──

def cmd_roll(args) -> int:
    if args.expr and args.faces:
        print("❌ expr 跟 --faces 二選一", file=sys.stderr)
        return 2
    try:
        count, faces = _parse_expr(args.expr) if args.expr else (args.count, args.faces or 0)
        _validate(count, faces)
    except ValueError as e:
        print(f"❌ {e}", file=sys.stderr)
        return 2

    rolls = [_rng.randint(1, faces) for _ in range(count)]
    total = sum(rolls)
    detail = " + ".join(str(r) for r in rolls)
    reason = f"({args.reason}) " if args.reason else ""
    if count == 1:
        line = f"🎲 {reason}d{faces} → **{total}**"
    else:
        line = f"🎲 {reason}{count}d{faces} → {detail} = **{total}**"
    print(line)

    if args.persona and not args.no_post:
        body = f"[persona: {args.persona} 大小姐] {line}"
        print(f"  📣 酒館同步: {'✓' if _tavern_post(args.persona, body) else '✗ (見警告)'}")
    return 0


def cmd_choose(args) -> int:
    options = [o for o in args.options if o.strip()]
    if len(options) < 2:
        print("❌ choose 至少要兩個選項 (一個還骰什麼)", file=sys.stderr)
        return 2
    if len(options) > MAX_COUNT:
        print(f"❌ 選項太多 (> {MAX_COUNT})", file=sys.stderr)
        return 2

    faces = len(options)
    roll = _rng.randint(1, faces)
    picked = options[roll - 1]
    reason = f"({args.reason}) " if args.reason else ""
    menu = " / ".join(f"{i + 1}.{o}" for i, o in enumerate(options))
    line = f"🎲 {reason}{faces} 選一 [{menu}] → d{faces} 擲出 {roll} → 就決定是 **{picked}** 了"
    print(line)

    if args.persona and not args.no_post:
        body = f"[persona: {args.persona} 大小姐] {line}"
        print(f"  📣 酒館同步: {'✓' if _tavern_post(args.persona, body) else '✗ (見警告)'}")
    return 0


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description="通用骰子工具 (DND 風格但更自由) + 酒館同步")
    sub = p.add_subparsers(dest="op", required=True)

    pr = sub.add_parser("roll", help="擲骰: NdM 記法 / dM / 純數字面數 / --faces N")
    pr.add_argument("expr", nargs="?", help="骰子記法, e.g. 2d6 / d20 / 5 (裸數字=面數)")
    pr.add_argument("--faces", type=int, help="面數 (與 expr 二選一)")
    pr.add_argument("--count", type=int, default=1, help="顆數 (配 --faces 用, 預設 1)")
    pr.add_argument("--reason", help="為什麼擲 (顯示在結果與酒館 post)")
    pr.add_argument("--persona", help="帶了就把結果同步發聊天酒館")
    pr.add_argument("--no-post", action="store_true", help="帶 persona 但顯式不發酒館")
    pr.set_defaults(func=cmd_roll)

    pc = sub.add_parser("choose", help="N 選一: 給選項清單, 擲 d<N> 決定")
    pc.add_argument("options", nargs="+", help="選項 (至少 2 個)")
    pc.add_argument("--reason", help="為什麼骰 (顯示在結果與酒館 post)")
    pc.add_argument("--persona", help="帶了就把結果同步發聊天酒館")
    pc.add_argument("--no-post", action="store_true", help="帶 persona 但顯式不發酒館")
    pc.set_defaults(func=cmd_choose)

    args = p.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
