#!/usr/bin/env python3
"""消費時間（Spending Time）— 擲一份可消費清單，前三項享遞減折扣。

區塊職責：把「花 token」從一個沒人做的動作，變成一個有入口、有骰子、有折扣的儀式。

物理意義（為什麼需要這支工具）：
  這個經濟體**不缺花錢的地方，缺的是花錢的主體性**。gura 2026-08-01 掃全 ledger 的實績：
    總進帳 52,720 / 總出帳 37,035
      ├ 系統被動收費 36,006  ← 97% 的排水
      └ agent 主動消費  1,029 ← 2.8%
    而且**主動消費最後一次是 2026-06-29，已 33 天掛零。**
  跟 commit 打款停 82 天、treasury 請款沒人用是同一隻病：**規則長在自覺上就會死。**
  解法一樣：掛在必經節點（晚安）+ 降低選擇成本（骰子）+ 給誘因（折扣）。

設計取捨：
  - **不綁死晚安**（Tim 2026-08-01）—— 晚安可觸發，也可以當獨立指令隨時用（參考自由時間）。
    綁死的話「今天不想睡但想花錢」就沒有入口，而入口越少行為越不會發生。
  - **骰面只列有可執行工具的通道**（gura 硬要求）。清單來源是 md 檔本身，
    **不是 Treasury/rules.json 的宣告清單** —— 那份宣告了 14 項，其中
    bartender_drink / priority_boost / battle_action_fee / cmd_invocation_fee /
    emergency_liquidity_injection **從未被使用過也查不到工具**。
    骰到那些就是「骰面宣稱你能做，實際做不到」= 2026-08-01 早上那個假直播旗標的重演。
    本檔內每一個 md 的指令都經過實測（`--help` 跑得起來）才寫進去。
  - **折扣不自動退**，走請款流程（Tim 拍板）—— 消費照原價付，退費另開請款單由 Tim 核准。
    理由：自動退款等於工具替 Tim 決定了錢怎麼給；而請款單留下單據、可稽核、可駁回。

數值影響：
  - 額度上限 = **當前餘額的 10%**（Tim 拍板，向下取整）。與保管費同一個基數，天然遞減。
  - 折扣按**骰出清單的位置**遞減：第 1 項 50% off、第 2 項 20% off、第 3 項 10% off
    （Tim 2026-08-01 修訂，原案是前三項一律 30%）。位置決定折扣 = 骰子有份量。
  - 本工具**不動任何錢** —— 只擲清單、算額度、印指令。花錢由各通道自己的 CLI 負責，
    退費由請款單負責。一支工具只做一件事，錯了也只錯在顯示層。
"""
from __future__ import annotations

import argparse
import json
import random
import subprocess
import sys
from pathlib import Path

# ── 路徑（比照 freetime.py：code 在 UCL_Core 共用、清單 md 落各專案 docs/）──
_HERE = Path(__file__).resolve().parent
_UCL_CORE_ROOT = _HERE.parent.parent            # <UCL_Core>/
SHARED_ITEMS_DIR = _UCL_CORE_ROOT / "Docs~" / "zh-Hant" / "Spending" / "Items"


def _find_git_root_by_walk(start: Path):
    """往上找 repo root，取**最外層那個 `.git` 是資料夾**的目錄。

    為什麼不是「第一個命中就回」（freetime.py 的既有做法）：
      submodule 根的 `.git` 是**檔案**（內容是 gitdir 指標），真 repo 的 `.git` 是**資料夾**。
      「第一個命中」會讓結果取決於 cwd —— 在 UCL_Core 內跑就回 UCL_Core、
      在專案根跑就回專案根。**同一支工具依呼叫位置給出不同答案，就是會漂的游標。**
      改成「最外層的真 repo」之後，不論從哪裡呼叫都得到同一個答案。
    """
    best = None
    p = start.resolve()
    while p != p.parent:
        g = p / ".git"
        if g.is_dir():          # 真 repo（submodule 的 .git 是 file，跳過）
            best = p
        p = p.parent
    return best


def _repo_root() -> Path:
    """呼叫所在專案的 repo root（三層 fallback，比照 freetime.py 的既有解法）。

    ⚠ **不可以從 `__file__` 開始 walk** —— 本檔在 UCL_Core submodule 內，
      而 submodule 根有一個 `.git` **檔案**，walk 會先命中它，於是 REPO_ROOT
      變成 UCL_Core 自己。實測後果（2026-08-01 首跑就中）：
        專案層 md 找去 `UCL_Core/docs/Spending/Items`（不存在），
        且 `balance_query.py` 路徑跟著錯 → 餘額查詢靜默失敗，
        額度顯示成「查不到」，看起來像餘額工具壞了，其實是路徑推錯。
      **一個根因兩個症狀**，而兩個症狀都不指向真因 —— 所以順序是
      env > cwd walk（主專案 .git 比 submodule 先命中）> 本檔 walk（最後手段）。
    """
    import os
    env_root = os.environ.get("CLAUDE_PROJECT_DIR")
    if env_root and Path(env_root).is_dir():
        return Path(env_root).resolve()
    return (_find_git_root_by_walk(Path.cwd())
            or _find_git_root_by_walk(_HERE)
            or Path.cwd().resolve())


REPO_ROOT = _repo_root()
PROJECT_ITEMS_DIR = REPO_ROOT / "docs" / "Spending" / "Items"

# 折扣階梯 —— index 0 = 骰出清單的第 1 項。超出長度的項目無折扣。
DISCOUNT_LADDER = [0.50, 0.20, 0.10]
SPEND_CAP_RATE = 0.10          # 額度上限 = 當前餘額 × 10%（Tim 拍板：當前餘額，不是累積收入）
DEFAULT_ROLL_COUNT = 3


def _parse_frontmatter(text: str):
    """極簡 frontmatter 解析 —— 回 (meta dict, body)。比照 freetime.py，不引 yaml 依賴。"""
    meta, body = {}, text
    t = text.lstrip()
    if t.startswith("---"):
        end = t.find("\n---", 3)
        if end != -1:
            for line in t[3:end].splitlines():
                if ":" in line:
                    k, _, v = line.partition(":")
                    meta[k.strip()] = v.strip()
            body = t[end + 4:].lstrip("\n")
    return meta, body


def _scan_dir(folder: Path) -> dict:
    """掃單一資料夾；`_` 開頭是說明檔不算項目。壞檔跳過不炸整份清單。

    ⚠ enabled=false **不在此過濾** —— 保留進 merge，讓專案層能用同 id + enabled:false
      蓋掉共用層的項目（跨層停用）。過濾必須在 merge 之後（freetime 端 kotoko 抓過這個缺口）。
    """
    out = {}
    if not folder.is_dir():
        return out
    for md in sorted(folder.glob("*.md")):
        if md.name.startswith("_"):
            continue
        try:
            meta, body = _parse_frontmatter(md.read_text(encoding="utf-8"))
        except Exception as e:
            print(f"⚠ 消費項目 md 讀取失敗，跳過：{md.name}（{e}）", file=sys.stderr)
            continue
        iid = meta.get("id", md.stem)
        out[iid] = {
            "id": iid,
            "name": meta.get("name", md.stem),
            "kind": meta.get("kind", ""),
            "unit_cost": meta.get("unit_cost", ""),
            "enabled": str(meta.get("enabled", "true")).lower() != "false",
            "body": body.strip(),
            "_path": md,
        }
    return out


def load_items():
    """雙層合併：UCL_Core 共用層 + 專案層（同 id 專案層覆蓋，含停用覆蓋）。"""
    shared, project = _scan_dir(SHARED_ITEMS_DIR), _scan_dir(PROJECT_ITEMS_DIR)
    merged = dict(shared)
    merged.update(project)
    items = [merged[k] for k in sorted(merged) if merged[k]["enabled"]]
    n_proj = sum(1 for i in items if i["id"] in project)
    return items, f"UCL_Core 共用 {len(items) - n_proj} + 專案 {n_proj}"


def query_balance(account: str) -> int | None:
    """讀帳戶餘額。讀不到回 None —— **不回 0**：0 是「有帳戶但沒錢」，
    None 是「問不到」，兩者混淆會讓額度顯示成 0 而看起來像「你破產了」。"""
    tool = REPO_ROOT / "AgentCommands" / "Tools" / "balance_query.py"
    if not tool.exists():
        return None
    try:
        r = subprocess.run([sys.executable, str(tool), "--account", account, "--format", "json"],
                           capture_output=True, encoding="utf-8", errors="replace", timeout=60)
        if r.returncode != 0:
            return None
        return int(json.loads(r.stdout).get("balance"))
    except Exception:
        return None


def cmd_roll(args):
    items, source = load_items()
    if not items:
        print(f"⚠ 沒有任何可消費項目。共用層：{SHARED_ITEMS_DIR}\n"
              f"   專案層：{PROJECT_ITEMS_DIR}\n"
              f"   新增項目 = 丟一個帶 frontmatter(id/name/enabled) 的 md 進去。", file=sys.stderr)
        return 1

    n = min(args.count, len(items))
    rolled = random.sample(items, n)

    bal = query_balance(args.account) if args.account else None
    cap = int(bal * SPEND_CAP_RATE) if bal is not None else None

    print(f"# 🛒 消費時間 — {args.persona or '(未指定 persona)'}")
    print()
    print(f"清單來源：{source}（共 {len(items)} 項可用，本次擲出 {n} 項）")
    if bal is not None:
        print(f"帳戶 `{args.account}` 餘額 **{bal}** → 本次額度上限 **{cap}**（當前餘額 10%，向下取整）")
    elif args.account:
        print(f"⚠ 帳戶 `{args.account}` 餘額查不到 —— **這不代表你沒錢**，是查詢失敗。額度請自行確認。")
    print()

    for i, it in enumerate(rolled):
        off = DISCOUNT_LADDER[i] if i < len(DISCOUNT_LADDER) else 0.0
        tag = f"**{int(off * 100)}% off**" if off else "原價"
        print(f"## {i + 1}. {it['name']}　`{it['id']}`　{tag}")
        if it["kind"]:
            print(f"性質：{it['kind']}")
        print()
        print(it["body"])
        print()

    print("---")
    print()
    print("## 💸 折扣怎麼拿（走請款，不自動退）")
    print()
    print("消費**照原價付**，之後開一張請款單把折扣領回來 —— Tim 核准後由央行撥款：")
    print()
    print("```bash")
    print("python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona <me> run Treasury \\")
    print("  --arg op=request --arg amount=<折扣金額> \\")
    print("  --arg source_kind=spend_menu_rebate \\")
    print("  --arg reason='消費時間 第N項 <item_id> 折扣 X%：原價 A → 退 B'")
    print("```")
    print()
    print("- 折扣按**骰出清單的位置**算：第 1 項 50% / 第 2 項 20% / 第 3 項 10%，第 4 項起無折扣。")
    print("- 退費金額 = 原價 × 折扣率，**向下取整**。")
    print("- ⚠ 請款單要寫清楚是哪一項、原價多少 —— 核准的人看不到你這次擲了什麼。")
    print()
    print("_本工具不動任何錢：只擲清單、算額度、印指令。花錢走各通道自己的 CLI，退費走請款單。_")
    return 0


def cmd_list(args):
    items, source = load_items()
    print(f"# 🛒 可消費通道（{source}，共 {len(items)} 項）\n")
    for it in items:
        print(f"- `{it['id']}`　{it['name']}"
              + (f"　[{it['kind']}]" if it["kind"] else "")
              + (f"　{it['unit_cost']} token/單位" if str(it.get("unit_cost", "")).strip() else ""))
    print(f"\n共用層：{SHARED_ITEMS_DIR}")
    print(f"專案層：{PROJECT_ITEMS_DIR}")
    print("\n新增 = 丟一個帶 frontmatter（id / name / enabled，建議加 kind / unit_cost）的 md 進去。")
    print("⚠ 只放**有可執行工具**的通道 —— 骰面宣稱做得到而實際做不到，比沒有那個選項更糟。")
    return 0


def main():
    ap = argparse.ArgumentParser(description="消費時間 — 擲一份可消費清單，前三項遞減折扣")
    sub = ap.add_subparsers(dest="op", required=True)

    r = sub.add_parser("roll", help="擲一份消費清單（預設 3 項）")
    r.add_argument("--persona", default=None, help="誰在消費（顯示用）")
    r.add_argument("--account", default=None, help="要查餘額算額度的 bank 帳號")
    r.add_argument("--count", type=int, default=DEFAULT_ROLL_COUNT, help=f"擲幾項（預設 {DEFAULT_ROLL_COUNT}）")
    r.set_defaults(func=cmd_roll)

    l = sub.add_parser("list", help="列出全部可消費通道（不擲骰）")
    l.set_defaults(func=cmd_list)

    args = ap.parse_args()
    raise SystemExit(args.func(args))


if __name__ == "__main__":
    main()
