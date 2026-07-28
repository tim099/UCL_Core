#!/usr/bin/env python3
"""treasury_ledger.py — UCL_Core canonical treasury 餘額/廣播 helper (2026-06-06 summit).

# 區塊職責：treasury ledger 的「餘額計算 + null balance 回填 + Discord 廣播觸發」單一真相來源。
# 物理意義：ledger 是 append-only、每筆一獨立 JSON 檔、金額不可變 → 真實餘額永遠 = 按 ts 重放所有
#          entry 的 amount 累加。balance_before/after 欄位只是 denormalized 顯示快取，不是真相。
#          本 lib 提供：(1) 重放算餘額 (2) 把 null 快取補對 (3) fire-and-forget 廣播。
# 數值影響：純讀檔 + 累加；不上鎖（cache 偶爾 racy 不影響真實金額，下次重放自愈）。

設計依據:
  - root cause: fire_salary_credit (work_session.py) 原本裸 spawn notify_treasury.py 繞過 backfill,
    導致 work_session/waiter/remote_work/stream_watch 四個 session 的薪資 entry balance 永遠 null
    → Discord 進帳卡顯示「餘額 None → None」(Tim 2026-06-06 QA 抓到)。
  - 此前主專案 _lib/treasury_broadcast.py (T64/T67) 已有等價邏輯, 但 UCL_Core 寫入者跨界用不到;
    Tim 拍板把「相關功能移進 UCL_Core」當跨專案 canonical home (跟 bank_resolver.py 同層)。
  - reframe by basecamp: 顯示端「重放算」是權威路徑, 寫入端填 cache 是錦上添花。

跨專案用法 (UCL_Core 內的 writer):
    sys.path.insert(0, str(_HERE))          # _HERE = Tools~/AgentCommands
    from _lib.treasury_ledger import fire_broadcast
    finalize_entry(entry_abs_path)          # 自動 backfill 餘額 (Discord 由 C# mirror pull)

ledger 路徑慣例 (self-locating 依據):
    <repo>/AgentCommands/Treasury/ledger/<YYYY-MM-DD>/<HHMMSS>_<ms>_<uuid>__{credit|debit}.json
"""
from __future__ import annotations

import glob
import json
import os
import sys
from pathlib import Path


# ===========================================================
# 路徑 self-locating
# 物理意義: entry 落在 <repo>/AgentCommands/Treasury/ledger/<date>/<file>.json,
#          故 ledger_root = parents[1], repo_root = parents[4] (穩定相對結構)。
# ===========================================================
def _ledger_root_of(entry_path: Path) -> Path:
    """從 entry 檔路徑反推 ledger 根目錄 (<...>/Treasury/ledger/)."""
    return entry_path.resolve().parents[1]


def _repo_root_of(entry_path: Path) -> Path:
    """從 entry 檔路徑反推專案 repo root (<repo>/)."""
    return entry_path.resolve().parents[4]


# ===========================================================
# 餘額重放 (event-source — 唯一真相)
# ===========================================================
def _iter_signed(ledger_root: Path, account_id: str, currency: str):
    """yield (ts, uuid, signed_amount) for matching entries。credit 正、debit 負。"""
    ledger_root = Path(ledger_root)
    if not ledger_root.exists():
        return
    for fpath in glob.glob(str(ledger_root / "*" / "*.json")):
        try:
            with open(fpath, "r", encoding="utf-8") as fp:
                e = json.load(fp)
        except (OSError, json.JSONDecodeError):
            continue
        if e.get("account_id") != account_id:
            continue
        if e.get("currency", "tavern_token") != currency:
            continue
        amt = e.get("amount", 0)
        signed = amt if e.get("type") == "credit" else -amt
        yield (e.get("ts", ""), e.get("uuid", ""), signed)


def compute_balance(ledger_root: Path, account_id: str, currency: str = "tavern_token",
                    exclude_uuid: str | None = None) -> int:
    """掃全 ledger 累加指定 account 的「當前總餘額」。

    物理意義: credit 加、debit 扣 (純總和, 跟順序無關); exclude_uuid 可排除某筆。
    用途: 「現在帳上多少」的查詢 (balance_query 風格)。**算單筆 running balance 請用 running_balance_before**。
    """
    total = 0
    for ts, uid, signed in _iter_signed(ledger_root, account_id, currency):
        if exclude_uuid and uid == exclude_uuid:
            continue
        total += signed
    return total


def running_balance_before(ledger_root: Path, account_id: str, currency: str,
                           target_ts: str, target_uuid: str) -> int:
    """算「目標 entry 之前」的 running balance (= 該筆 balance_before)。

    物理意義: ledger 是事件流, 單筆的 running balance 須按時間序累加, 不能用「全帳-自己」
              (那會把目標之後發生的交易也算進去 → 追溯回填舊筆時算錯)。
    數值影響: 按 (ts, uuid) 穩定排序; uuid 當同秒 tiebreaker (對齊 montage mtime/idx 教訓)。
              只累加排在 target 嚴格之前者。
    """
    rows = sorted(_iter_signed(ledger_root, account_id, currency), key=lambda r: (r[0], r[1]))
    key = (target_ts, target_uuid)
    before = 0
    for ts, uid, signed in rows:
        if (ts, uid) < key:
            before += signed
    return before


# ===========================================================
# null balance 回填 (修正 denormalized cache)
# ===========================================================
def backfill_balance_fields(entry_path: str | Path) -> bool:
    """若 entry 的 balance_before/after 任一為 None, 重放算出後寫回 JSON。

    物理意義: filesystem 直寫的薪資 entry 沒填 balance; 廣播前補完, Discord 卡才顯示對的數字。
    回傳: True=有修改; False=已有值/讀寫失敗/非 credit·debit。
    """
    entry_path = Path(entry_path)
    try:
        with open(entry_path, "r", encoding="utf-8") as f:
            e = json.load(f)
    except (OSError, json.JSONDecodeError):
        return False

    # 兩欄都已有值 → 視為已算過 (C# 單寫者路徑), 不動
    if e.get("balance_before") is not None and e.get("balance_after") is not None:
        return False

    account = e.get("account_id")
    entry_type = e.get("type")
    if not account or entry_type not in ("credit", "debit"):
        return False
    currency = e.get("currency", "tavern_token")
    amount = e.get("amount", 0)
    target_uuid = e.get("uuid")

    ledger_root = _ledger_root_of(entry_path)
    balance_before = running_balance_before(ledger_root, account, currency,
                                            e.get("ts", ""), target_uuid)
    balance_after = balance_before + amount if entry_type == "credit" else balance_before - amount

    e["balance_before"] = balance_before
    e["balance_after"] = balance_after

    # atomic-ish write back (tmp → replace), 避免廣播 reader 讀到半寫檔
    try:
        tmp = entry_path.with_suffix(".json.tmp")
        tmp.write_text(json.dumps(e, ensure_ascii=False, indent=2), encoding="utf-8")
        os.replace(tmp, entry_path)
        return True
    except OSError:
        return False


# ===========================================================
# Entry 收尾 (balance backfill) — 2026-07-28: 廣播 spawn 已移除
# ===========================================================
def finalize_entry(entry_path: str | Path, *, quiet: bool = True) -> None:
    """補齊 entry 的 balance_before/after 欄位。

    物理意義: 只做「重放算餘額回填」— 修掉「餘額 None → None」顯示問題的那一步。
    Discord 廣播 (2026-07-28 起): 不在此 spawn 任何 python。ledger 是 append-only 事件流,
              C# UCL_DiscordTreasuryMirror 依 cursor pull 撿走新 entry (冪等、漏送可補)。
              舊版在此 fire-and-forget spawn notify_discord.py, 是 2026-07-28 python
              併發失控事故 (單一殭屍鎖 holder → 後續全部無鎖並發) 的 spawn 來源之一。
    安全: 任何失敗都 swallow, 絕不擋 caller 主流程 (entry 已落盤)。
    """
    entry_path = Path(entry_path)
    if not entry_path.exists():
        return
    try:
        backfill_balance_fields(entry_path)
    except Exception as ex:
        if not quiet:
            print(f"[treasury_ledger] backfill fail (continuing): {ex}", file=sys.stderr)


# 舊名保留為 alias — 呼叫端逐步改名期間不斷線 (語意已變: 不再廣播, 只 backfill)
fire_broadcast = finalize_entry


if __name__ == "__main__":
    # CLI debug: backfill + broadcast 一筆 entry
    import argparse
    p = argparse.ArgumentParser(description="UCL_Core treasury ledger entry finalizer (balance backfill)")
    p.add_argument("entry_path", help="ledger entry json path (absolute)")
    p.add_argument("--verbose", action="store_true")
    args = p.parse_args()
    finalize_entry(args.entry_path, quiet=not args.verbose)
    print(f"Finalized (balance backfilled): {args.entry_path}")
