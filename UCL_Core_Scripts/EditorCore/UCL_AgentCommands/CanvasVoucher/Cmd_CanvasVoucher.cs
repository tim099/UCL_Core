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
            "balance: persona=persona名（必填）\n" +
            "grant: persona=persona名 amount=N [source=admin_grant] [ref=業務ref] — 發券（balance += amount）\n" +
            "consume: persona=persona名 amount=N [source=canvas_place] [ref=...] — 用券（不足 fail）";

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
            int bal = UCL_CanvasVoucherLedger.GetBalance(persona);
            WriteLastOp($"# 🎨 繪圖券 balance\n\n- persona: `{persona}`\n- **balance: {bal}**\n");
            Debug.Log($"[CanvasVoucher] balance {persona} = {bal}");
        }

        void Op_Grant(Dictionary<string, string> args)
        {
            string persona = GetArg(args, "persona", "");
            string amountStr = GetArg(args, "amount", "0");
            string source = GetArg(args, "source", "manual_grant");
            string refText = GetArg(args, "ref", "");
            if (string.IsNullOrEmpty(persona)) { Reject("grant 缺少 persona"); return; }
            if (!int.TryParse(amountStr, out int amount) || amount <= 0) { Reject($"grant amount 無效或非正數: {amountStr}"); return; }

            var (before, after) = UCL_CanvasVoucherLedger.Grant(persona, amount, source, refText);
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
