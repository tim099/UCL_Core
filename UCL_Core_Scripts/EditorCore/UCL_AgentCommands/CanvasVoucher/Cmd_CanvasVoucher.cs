// 區塊職責：Cmd_CanvasVoucher — 繪圖券 CMD wrapper（thin），委派 UCL_CanvasVoucherLedger static API。
// 物理意義：Tim 2026-07-22 拍板「券發放收攏 C# static class、python 端透過 CMD 操作」的 python 入口 —
//          agent / python 透過 run_cmd.py run CanvasVoucher 觸發，寫入走 C# 單一 owner(UCL_CanvasVoucherLedger)，
//          不再各自直寫 vouchers/*.json（單寫者，杜絕跨 process 路徑 split + 兩寫者 drift）。
// 數值影響：op-dispatch(balance / grant / consume)；結果寫 _last_op.md 給 caller 讀。
// 對齊 Cmd_Treasury 結構(thin wrapper + WriteLastOp/RejectLastOp/FailLastOp)。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.CanvasVoucher
{
    public class Cmd_CanvasVoucher : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "CanvasVoucher";
        public override string ShortDescription => "繪圖券帳本 — 綁 persona（balance / grant / consume），C# canonical owner";

        public override string ArgsSchema =>
            "balance: persona=persona名（必填）—— 回**三個**數字：可花總額 / 永久券 / 未過期限時券\n" +
            "grant: persona=persona名 amount=N [source=admin_grant] [ref=業務ref] [expires_at=<UTC ISO>] — 發券（**expires_at 空＝永久券**；帶了＝限時券，到期自動作廢並記 history）\n" +
            "consume: persona=persona名 amount=N [source=canvas_place] [ref=...] — 用券（**先花快過期的**；可花總額不足 fail，不部分扣款）";

        public override string ExampleArgs => "op=balance;persona=kiara";

        public override string HelpURL => "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_CanvasVoucher.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            string op = GetArg(args, "op", "").ToLowerInvariant();
            if (string.IsNullOrEmpty(op)) { Reject("缺少 op 參數（balance / grant / consume）"); return; }

            try
            {
                switch (op)
                {
                    case "balance": Op_Balance(args); break;
                    case "grant":   Op_Grant(args); break;
                    case "consume": Op_Consume(args); break;
                    default: Reject($"未知 op: {op}"); break;
                }
            }
            catch (System.Exception ex)
            {
                Fail($"執行 op={op} 失敗：{ex.Message}");
            }
        }

        void Op_Balance(Dictionary<string, string> args)
        {
            string persona = GetArg(args, "persona", "");
            if (string.IsNullOrEmpty(persona)) { Reject("balance 缺少 persona"); return; }
            // 查詢就把三種都報出來 —— **不替使用者挑一種**。
            // 2026-08-18 券改批次制：「永久」「未過期限時」「可花總額」是三個不同的答案，
            // 只回一個數字的話，讀的人會拿它當成自己心裡想的那一種（而那不會報錯）。
            int aPermanent = UCL_CanvasVoucherLedger.GetPermanent(persona);
            int aExpiring = UCL_CanvasVoucherLedger.GetExpiring(persona);
            int bal = UCL_CanvasVoucherLedger.GetSpendable(persona);
            WriteLastOp($"# 🎨 繪圖券 balance\n\n- persona: `{persona}`\n"
                      + $"- **可花總額: {bal}**（未過期限時 {aExpiring} ＋ 永久 {aPermanent}）\n"
                      + $"- 永久券: **{aPermanent}**　存著的，不會過期\n"
                      + $"- 未過期限時券: **{aExpiring}**　到期即作廢，過期後這個數字自己會掉\n"
                      + "\n> ⚠ 三個數字問的是**不同的問題** —— 規劃付款看「可花總額」，查存量看「永久券」。別拿其中一個當另一個用。\n");
            Debug.Log($"[CanvasVoucher] balance {persona}: spendable={bal} permanent={aPermanent} expiring={aExpiring}");
        }

        void Op_Grant(Dictionary<string, string> args)
        {
            string persona = GetArg(args, "persona", "");
            string amountStr = GetArg(args, "amount", "0");
            string source = GetArg(args, "source", "manual_grant");
            string refText = GetArg(args, "ref", "");
            if (string.IsNullOrEmpty(persona)) { Reject("grant 缺少 persona"); return; }
            if (!int.TryParse(amountStr, out int amount) || amount <= 0) { Reject($"grant amount 無效或非正數: {amountStr}"); return; }

            // 期間限定券（Tim 2026-08-18）：`expires_at` 空 ＝ 永久券 ⇒ 不帶這個參數時行為與改動前逐值相同。
            string aExpiresAt = GetArg(args, "expires_at", "").Trim();
            var (before, after) = UCL_CanvasVoucherLedger.Grant(persona, amount, source, refText, aExpiresAt);
            WriteLastOp($"# ✅ 繪圖券 grant\n\n- persona: `{persona}`\n- amount: **+{amount}**\n- source: `{source}`\n- balance: {before} → **{after}**\n");
        }

        void Op_Consume(Dictionary<string, string> args)
        {
            string persona = GetArg(args, "persona", "");
            string amountStr = GetArg(args, "amount", "0");
            string source = GetArg(args, "source", "canvas_place");
            string refText = GetArg(args, "ref", "");
            if (string.IsNullOrEmpty(persona)) { Reject("consume 缺少 persona"); return; }
            if (!int.TryParse(amountStr, out int amount) || amount <= 0) { Reject($"consume amount 無效或非正數: {amountStr}"); return; }

            var (before, after) = UCL_CanvasVoucherLedger.Consume(persona, amount, source, refText);
            WriteLastOp($"# ✅ 繪圖券 consume\n\n- persona: `{persona}`\n- amount: **-{amount}**\n- use: `{source}`\n- balance: {before} → **{after}**\n");
        }

        // 借用 ChatTavern render 寫 _last_op.md（對齊 Cmd_Treasury 的 helper 做法，避免跨 asmdef 依賴問題）
        static void WriteLastOp(string md) =>
            UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_ChatTavernRender.WriteLastOp(md);

        static void Reject(string msg)
        {
            WriteLastOp($"# ❌ CanvasVoucher Cmd Rejected\n\n{msg}\n");
            throw new System.InvalidOperationException(msg);
        }

        static void Fail(string msg)
        {
            WriteLastOp($"# ❌ CanvasVoucher Cmd Failed\n\n{msg}\n");
            throw new System.Exception(msg);
        }

        static string GetArg(Dictionary<string, string> args, string key, string def) =>
            args != null && args.TryGetValue(key, out var v) ? v : def;
    }
}
#endif
