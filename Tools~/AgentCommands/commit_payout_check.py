#!/usr/bin/env python3
# 區塊職責：比對「近期 git commit」與「ledger 內已領的 commit 薪資」，列出未領 / 重複領。
# 物理意義：commit 薪資（每筆 +5 token）的唯一領取路徑是「發一則酒館公告帶 tag=commit + meta.sha」，
#          Op_Post hook 收到就 credit。問題是**漏發不會有任何錯誤訊息** —— commit 照樣成功，
#          只是錢沒進來。2026-07-30 新制上線後 ledger 內 source_kind=commit 一度 82 天零筆
#          （最後一筆停在 2026-05-10），全社群沒人發現。
# 數值影響：純唯讀（讀 git log + ledger json），不發文、不寫帳、不改任何檔。
#          預設 exit 0；--strict 時「有未領」→ exit 1（可掛 pre-push / CI）。
# 設計取捨：
#   - **不自動補發**：發文是有金錢後果的動作，該由人按下去。本工具只負責讓遺漏「自己喊」。
#   - **反向偵測同 SHA 重複領**（summit 2026-07-31 提）：規則本身沒有防重複的技術保護
#     （Tim 拍板「有重複我看得到」），肉眼看不可靠 → 這裡給那條社會約束一個機械代言人。
#   - **多 repo 一起掃**：一次 commit 動作常橫跨主專案與各 submodule（三層 bump），
#     只看主專案會漏掉大部分該領的錢。
# 相關：ucl_core:Skills~/ucl-commit/SKILL.md「💰 領薪」/ Docs~/zh-Hant/Workflows/Commit_Workflow.md §9.5
"""比對近期 commit 與已領薪資，列出未領清單。

用法：
    python commit_payout_check.py                  # 掃預設範圍（近 3 天 / 各 repo 近 20 筆）
    python commit_payout_check.py --days 1         # 只看今天以來
    python commit_payout_check.py --max 50         # 每個 repo 多看幾筆
    python commit_payout_check.py --author Tim     # 只看特定 committer
    python commit_payout_check.py --strict         # 有未領 → exit 1
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

# ===========================================================
# 區塊職責：路徑解析 —— 找 repo 根與 AgentCommands 資料根
# 物理意義：本工具住在 UCL_Core（跨專案共用），不能寫死任何專案的路徑。
#          資料根解析沿用 run_cmd 的邏輯（唯一真相源），避免第二份解析漂移。
# 數值影響：解析失敗直接報錯退出，不猜路徑 —— 猜錯會掃到空 ledger 然後回報「全部未領」，
#          那是假警報，比沒有警報更糟。
# ===========================================================

def _resolve_roots() -> tuple[Path, Path]:
    """回傳 (git_root, data_root)。沿用 run_cmd 的解析，不自行重造。"""
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    try:
        import run_cmd  # noqa: F401  — 匯入即完成路徑解析
    except Exception as exc:  # pragma: no cover
        print(f"❌ 無法載入 run_cmd 取得路徑解析：{exc}", file=sys.stderr)
        raise SystemExit(2)
    return Path(run_cmd.GIT_ROOT), Path(run_cmd.DATA_ROOT)


# ===========================================================
# 區塊職責：撈 ledger 內已領的 commit 薪資 → {短 SHA: [筆數資訊]}
# 物理意義：Op_Post 的 commit hook 寫 source_kind="commit"（刻意沿用舊鍵，歷史 45 筆可連續查）。
#          SHA 存在哪個欄位不保證統一（不同世代的寫入者可能放 reason / meta / source_id），
#          所以這裡**掃整筆 record 的字串**找 7~40 位 hex，寧可寬鬆也不要漏判成「未領」。
# 數值影響：回傳 dict[短 SHA(前7) → list of (檔名, amount)]；同 SHA 多筆 = 重複領。
# ===========================================================
_HEX = set("0123456789abcdef")


def _iter_hex_tokens(text: str):
    """從任意字串撈出看起來像 git SHA 的 token（7~40 位 hex，邊界以非 hex 字元切）。"""
    cur = []
    for ch in text.lower() + " ":
        if ch in _HEX:
            cur.append(ch)
            continue
        if 7 <= len(cur) <= 40:
            yield "".join(cur)
        cur = []


def load_claimed_shas(data_root: Path) -> dict[str, list]:
    ledger_root = data_root / "Treasury" / "ledger"
    claimed: dict[str, list] = {}
    if not ledger_root.is_dir():
        return claimed
    for path in ledger_root.rglob("*.json"):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError, UnicodeDecodeError):
            continue
        records = data if isinstance(data, list) else [data]
        for rec in records:
            if not isinstance(rec, dict):
                continue
            if str(rec.get("source_kind", "")) != "commit":
                continue
            # ⚠ 同一筆 record 內同一個 SHA 常出現多次（source_id / reason / meta 各一份）。
            #   逐 token 累加會把「領一次」算成「領三次」→ 假的重複領警報。
            #   假警報比沒有警報更糟（它會訓練人忽略警報），故**每筆 record 對每個 SHA 只計一次**。
            #   血證：本工具首跑就對自己的 dd240b2 誤報 ×3（2026-07-31 gura 自抓）。
            blob = json.dumps(rec, ensure_ascii=False)
            for short in {t[:7] for t in _iter_hex_tokens(blob)}:
                claimed.setdefault(short, []).append((path.name, rec.get("amount")))
    return claimed


# ===========================================================
# 區塊職責：列出各 repo（主專案 + 全 submodule）近期 commit
# 物理意義：一次 commit 動作常橫跨多層（UCL_Core → 主專案），只看一層會漏掉大部分該領的錢。
# 數值影響：純 `git log` 唯讀；某個 repo 讀失敗只警告跳過，不擋其他 repo。
# ===========================================================

def list_repos(git_root: Path) -> list[tuple[str, Path]]:
    repos = [("(main)", git_root)]
    try:
        out = subprocess.run(
            ["git", "-C", str(git_root), "submodule", "status", "--recursive"],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60,
        ).stdout
    except (OSError, subprocess.SubprocessError):
        out = ""
    for line in out.splitlines():
        parts = line.strip().split()
        if len(parts) >= 2:
            sub = git_root / parts[1]
            if (sub / ".git").exists():
                repos.append((parts[1], sub))
    return repos


def list_commits(repo: Path, days: int, max_count: int, author: str | None) -> list[tuple[str, str, str]]:
    cmd = ["git", "-C", str(repo), "log", f"-{max_count}",
           f"--since={days}.days.ago", "--pretty=%h%x1f%an%x1f%s"]
    if author:
        cmd.append(f"--author={author}")
    try:
        out = subprocess.run(cmd, capture_output=True, text=True,
                             encoding="utf-8", errors="replace", timeout=60).stdout
    except (OSError, subprocess.SubprocessError) as exc:
        print(f"  ⚠ 讀 {repo} 的 git log 失敗，跳過：{exc}")
        return []
    rows = []
    for line in out.splitlines():
        parts = line.split("\x1f")
        if len(parts) == 3:
            rows.append((parts[0], parts[1], parts[2]))
    return rows


def main() -> int:
    ap = argparse.ArgumentParser(description="比對近期 commit 與已領 commit 薪資")
    ap.add_argument("--days", type=int, default=3, help="往回看幾天（預設 3）")
    ap.add_argument("--max", type=int, default=20, help="每個 repo 最多看幾筆（預設 20）")
    ap.add_argument("--author", default=None, help="只看特定 committer")
    ap.add_argument("--strict", action="store_true", help="有未領 → exit 1")
    args = ap.parse_args()

    git_root, data_root = _resolve_roots()
    claimed = load_claimed_shas(data_root)

    print(f"# 💰 Commit 薪資對帳（近 {args.days} 天 / 每 repo 最多 {args.max} 筆）")
    print(f"- repo root : {git_root}")
    print(f"- ledger    : {data_root / 'Treasury' / 'ledger'}")
    print(f"- 已領 SHA  : {len(claimed)} 個")
    print()

    unpaid: list[tuple[str, str, str]] = []
    total_commits = 0
    for name, repo in list_repos(git_root):
        rows = list_commits(repo, args.days, args.max, args.author)
        if not rows:
            continue
        print(f"## {name}")
        for sha, an, subject in rows:
            total_commits += 1
            hits = claimed.get(sha[:7], [])
            if not hits:
                mark = "○ 未領"
                unpaid.append((name, sha, subject))
            elif len(hits) > 1:
                mark = f"⚠ 重複領 ×{len(hits)}"
            else:
                mark = "● 已領"
            print(f"  {mark}  {sha}  {subject[:60]}")
        print()

    # 反向偵測：ledger 有領但近期 commit 裡找不到對應 SHA → 可能是更早的 commit（正常）
    # 或誤領（異常）。只提示不判定 —— 本工具的職責是讓人看見，不是替人下結論。
    dup = {sha: hits for sha, hits in claimed.items() if len(hits) > 1}
    if dup:
        print("## ⚠ 同 SHA 領過多次（規則本身無技術防護，靠社會約束 → 這裡給它一個機械代言人）")
        for sha, hits in sorted(dup.items()):
            print(f"  {sha} ×{len(hits)}  {[h[0] for h in hits]}")
        print()

    print(f"## 結論：{total_commits} 筆 commit 中 **{len(unpaid)} 筆未領**"
          + (f"（預期可領 {len(unpaid) * 6} token：{len(unpaid)}×(5 commit + 1 work_post)）" if unpaid else ""))
    if unpaid:
        print()
        print("補領方式（一則訊息一個 SHA，別合併 —— 多 SHA 會被 T06.3 reject）：")
        for name, sha, subject in unpaid:
            print(f"  # {name}: {subject[:50]}")
            print(f"  run_cmd.py run Tavern --arg op=post --arg room=tavern --arg agent=<agent-id> "
                  f"--arg persona=<persona> --wait-reply 0 "
                  f"--arg meta='{{\"tag\":\"commit\",\"sha\":\"{sha}\",\"category\":\"meta\"}}' "
                  f"--arg-stdin body <<'EOF' … EOF")
        if args.strict:
            return _announce(1, f"{len(unpaid)} 筆未領（--strict）")
    return _announce(0, "無未領" if not unpaid else f"{len(unpaid)} 筆未領（未帶 --strict，不視為失敗）")


# 區塊職責：把自己的退出碼印進 stdout 最後一行
# 物理意義：caller 幾乎都會接管線（`| tail` / `| grep`），而 `cmd | tail; echo $?` 拿到的是
#          **tail 的**退出碼 —— 真正的碼被管線吃掉，且看起來完全正常。
#          2026-07-31 gura 一天內對這條踩了三次（同事早上才教過），第三次差點誤報同事一隻不存在的 bug。
# 數值影響：純 stdout 一行 `[exit] code=N reason=...`，不改任何回傳值或行為。
# 設計取捨：與其要求每個 caller「記得別接管線」（避開型規則，每次都要判斷），
#          不如讓工具**一律自報**（唯一手勢，不需要判斷）。同一條原則：機制別靠人的記性。
def _announce(code: int, reason: str) -> int:
    print()
    print(f"[exit] code={code} reason={reason}")
    return code


if __name__ == "__main__":
    raise SystemExit(main())
