"""財務操作的唯一 python 通道 —— 一律經 Cmd（C# server 端），python 不直寫 ledger。

規則（Tim 2026-08-04 定調）：
    **銀行 / token 相關操作都必須透過 C# server 端。python 只當 API 層。**

為什麼不能直寫（每一條都是實際會發生的後果，不是原則潔癖）：
  1. **餘額快取會靜默失準** —— C# 端自 2026-08-01 起初掃後不再列舉磁碟
     （`UCL_TreasuryLedger.s_InitialScanDone`），寫入端自行維護增量。
     python 直寫的 entry 在下次 InvalidateBalanceCache / domain reload 之前
     **C# 看不到**：後台顯示的餘額與磁碟不一致，而且沒有任何錯誤訊息。
  2. **繞過冪等判重** —— C# `WriteEntry` 會用 idempotency_key 擋重複入帳；直寫沒有這層。
  3. **簽章不可信** —— `sig_*` 欄位由寫入端自己填，python 直寫時會填成
     `manual_filesystem_write_*`。2026-08-04 盤查時，「有沒有 sig_* 欄位」一度被
     當成作者判準，結論剛好顛倒（實際 1,144 筆是 python 寫的）。
     **偽造成本為零的欄位不能當身分證明。**
  4. **balance_before/after 要另外回填** —— 直寫留 null，於是需要一支 python 去改寫
     既有 entry（append-only 帳本被就地修改）。走 Cmd 則 C# 當場就填好。

用法：
    from _lib.treasury_cmd import treasury_debit, treasury_credit
    ok, msg = treasury_debit(account="zeta", amount=3, source_kind="canvas_pixel",
                             source_ref=event_uuid, description="...", caller="canvas.py")
"""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

_HERE = Path(__file__).resolve().parent          # .../Tools~/AgentCommands/_lib
_RUN_CMD = _HERE.parent / "run_cmd.py"           # .../Tools~/AgentCommands/run_cmd.py


def _run(cmd_type: str, args: dict, *, timeout: float = 180.0) -> tuple[bool, str]:
    """送一個 Cmd 進 Editor 佇列並等它跑完。回 (ok, 訊息尾段)。"""
    argv = [sys.executable, str(_RUN_CMD), "run", cmd_type, "--wait-reply", "0"]
    for k, v in args.items():
        argv += ["--arg", f"{k}={v}"]
    try:
        r = subprocess.run(argv, capture_output=True, encoding="utf-8",
                           errors="replace", timeout=timeout)
    except Exception as e:
        return False, f"Cmd 執行失敗：{e}"
    out = (r.stdout or "") + (r.stderr or "")
    ok = r.returncode == 0 and "Success" in out
    return ok, out[-500:]


def treasury_balance(account: str, currency: str = "tavern_token", *,
                     timeout: float = 60.0) -> "int | None":
    """
    區塊職責：查餘額 —— 走 Cmd（C# 的 UCL_TreasuryLedger 增量快取），**python 不自己算**。
    物理意義：這是把 2026-08-04「銀行/token 一律走 C#」那條定調的**讀取端**補完 ——
             當時寫入端搬了（treasury_debit / credit），查詢端沒搬。
    數值影響：回傳 int；查不到（Editor 未開 / Cmd 失敗 / 沒回值）→ **None**，
             不回 0。⚠ 呼叫端要把 None 當「不知道」處理，不可拿它當「沒錢」去做付款判斷。
    🩸 為什麼一定要搬（2026-08-16 basecamp 量測）：python 端各自全掃帳本的複製品有四份，
       每份 14,985 檔逐檔 json.load —— 暖快取 0.6s，冷快取近兩分鐘。
       morning 的 brief 被它拖到 112s，08-13 那次直接撞 120s timeout 被 kill。
    """
    ok, out = _run("Treasury", {"op": "balance", "account": account, "currency": currency},
                   timeout=timeout)
    if not ok:
        return None
    # run_cmd 把 handler 回報的值印成 `  🔢 balance = 6208`（見 run_cmd.print_cmd_outputs）
    for line in reversed(out.splitlines()):
        if "🔢" in line and "balance" in line and "=" in line:
            try:
                return int(line.split("=", 1)[1].strip())
            except ValueError:
                return None
    return None


def treasury_debit(*, account: str, amount: int, source_kind: str, source_ref: str,
                   description: str, caller: str = "", currency: str = "tavern_token"):
    """扣款。amount <= 0 直接視為成功（沒有要扣的東西，不必打擾 Editor）。"""
    if amount <= 0:
        return True, "amount<=0，無需扣款"
    return _run("Treasury", {
        "op": "debit", "account": account, "amount": amount, "currency": currency,
        "use_kind": source_kind, "use_ref": source_ref,
        "description": description,
        # ⚠ caller 必須是 **帳戶本人**（或 "system"）—— UCL_TreasuryLedger 有帳戶隔離鐵律：
        #   caller 非 "system" 且 != accountId 就拋例外「不可動用對方帳戶」。
        #   傳工具名（"canvas.py"）會被自己的防盜用規則擋死，而錯誤訊息長得像帳本壞了。
        #   語意上這裡就是「該帳戶花自己的錢」，所以 caller = account 是正確的宣告，
        #   不是為了繞過檢查。真正的代操作（後台代所有帳戶）才用 "system"。
        "caller": caller or account,
    })


def treasury_credit(*, account: str, amount: int, source_kind: str, source_ref: str,
                    description: str, caller: str = "", currency: str = "tavern_token"):
    """入帳。amount <= 0 直接視為成功。"""
    if amount <= 0:
        return True, "amount<=0，無需入帳"
    return _run("Treasury", {
        "op": "credit", "account": account, "amount": amount, "currency": currency,
        "source_kind": source_kind, "source_ref": source_ref,
        "description": description,
        "caller": caller or account,   # 同 debit：帳戶隔離鐵律，見上方註解
    })


def canvas_voucher_consume(*, persona: str, amount: int, source_ref: str, description: str = ""):
    """消繪圖券 —— 走 Cmd_CanvasVoucher（C# 是券的 canonical owner）。"""
    if amount <= 0:
        return True, "amount<=0，無需消券"
    return _run("CanvasVoucher", {
        "op": "consume", "persona": persona, "amount": amount,
        "ref": source_ref, "description": description or f"canvas consume x{amount}",
    })


def canvas_voucher_grant(*, persona: str, amount: int, source: str = "manual_grant",
                         source_ref: str = "", description: str = ""):
    """發繪圖券 —— 走 Cmd_CanvasVoucher（C# 是券的 canonical owner）。

    🩸 2026-08-17 補這支的理由：**券是最後一種還能被 python 直寫的錢。**
      consume 早就走 Cmd 了，grant 卻留著 `canvas.py voucher grant` 與
      `chess.py grant_voucher()` 兩處直寫 —— 於是券成了本檔開頭那四條後果
      （快取靜默失準 / 繞過冪等 / 簽章不可信 / balance 欄自行維護）唯一的缺口。
      而缺口真的漏了：chess.py 的路徑推導 bug 把券寫到 repo 外，兩份帳本各自累積
      真實交易後分歧（summit 643 vs 231、basecamp 75 vs 254，四個 persona 只存在於錯的那邊）。
      **路徑 bug 是導火線，但能燒起來是因為那裡本來就允許直寫。**
    """
    if amount <= 0:
        return True, "amount<=0，無需發券"
    return _run("CanvasVoucher", {
        "op": "grant", "persona": persona, "amount": amount,
        "source": source, "ref": source_ref,
        "description": description or f"grant x{amount}",
    })
