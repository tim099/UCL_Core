// 區塊職責：Cmd_CanvasVoucher — 繪圖券 CMD wrapper（thin），委派 UCL_CanvasVoucherLedger static API。
// 物理意義：Tim 2026-07-22 拍板「券發放收攏 C# static class、python 端透過 CMD 操作」的 python 入口 —
//          agent / python 透過 senate ucmd run CanvasVoucher 觸發，寫入走 C# 單一 owner(UCL_CanvasVoucherLedger)，
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
            "consume: persona=persona名 amount=N [source=canvas_place] [ref=...] — 用券（**先花快過期的**；可花總額不足 fail，不部分扣款）\n" +
            "usage: persona=persona名 ref=批次ref（必填）—— 唯讀，回**那一批**的 granted/forfeited/used，並明寫讀數源（`batches` 或 `history`）";

        public override string ExampleArgs => "op=balance;persona=kiara";

        public override string HelpURL => "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_CanvasVoucher.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            string op = GetArg(args, "op", "").ToLowerInvariant();
            if (string.IsNullOrEmpty(op)) { Reject(args, "缺少 op 參數（balance / grant / consume）"); return; }

            try
            {
                switch (op)
                {
                    case "balance": Op_Balance(args); break;
                    case "grant":   Op_Grant(args); break;
                    case "consume": Op_Consume(args); break;
                    case "usage":   Op_Usage(args); break;
                    default: Reject(args, $"未知 op: {op}"); break;
                }
            }
            catch (System.Exception ex)
            {
                Fail(args, $"執行 op={op} 失敗：{ex.Message}");
            }
        }

        void Op_Balance(Dictionary<string, string> args)
        {
            string persona = GetArg(args, "persona", "");
            if (string.IsNullOrEmpty(persona)) { Reject(args, "balance 缺少 persona"); return; }
            // 查詢就把三種都報出來 —— **不替使用者挑一種**。
            // 2026-08-18 券改批次制：「永久」「未過期限時」「可花總額」是三個不同的答案，
            // 只回一個數字的話，讀的人會拿它當成自己心裡想的那一種（而那不會報錯）。
            int aPermanent = UCL_CanvasVoucherLedger.GetPermanent(persona);
            int aExpiring = UCL_CanvasVoucherLedger.GetExpiring(persona);
            int bal = UCL_CanvasVoucherLedger.GetSpendable(persona);
            WriteLastOp(args, $"# 🎨 繪圖券 balance\n\n- persona: `{persona}`\n"
                      + $"- **可花總額: {bal}**（未過期限時 {aExpiring} ＋ 永久 {aPermanent}）\n"
                      + $"- 永久券: **{aPermanent}**　存著的，不會過期\n"
                      + $"- 未過期限時券: **{aExpiring}**　到期即作廢，過期後這個數字自己會掉\n"
                      + "\n> ⚠ 三個數字問的是**不同的問題** —— 規劃付款看「可花總額」，查存量看「永久券」。別拿其中一個當另一個用。\n");
            // 機讀出口（basecamp 2026-09-03，TASK-0114 ②）：本 op 原本**只有人讀文字**，
            // 於是程式消費端只剩兩條路 —— 去 regex 那份 md（措辭一改就靜默失配，
            // 而失配的樣子跟「這個 persona 沒有券」一模一樣），或自己重算一份券帳（兩寫者 drift）。
            // ⇒ 三個數字各自落一欄，名字與人讀那三行一一對應（不合併成一個「balance」，
            //   合併就是替使用者挑一種，而那正是上面那段註解在防的事）。
            UCL_AgentCommandRunner.ReportOutputValue(args, "spendable", bal.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "permanent", aPermanent.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "expiring", aExpiring.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "persona", persona);
            Debug.Log($"[CanvasVoucher] balance {persona}: spendable={bal} permanent={aPermanent} expiring={aExpiring}");
        }

        // ===========================================================
        // 區塊職責：op=usage —— 唯讀查「某一批（按 ref）用了幾張」，並**明寫讀數源**。
        // 物理意義：TASK-0198。這個問題原本只有一個消費端（自由時間收工公告），
        //          ⇒ 要驗那條回退路，唯一的尺就是被驗的那支本身（同源，判準④）。
        //          本 op 是第二個消費端：同一組 API、不同的呼叫者與輸出格式。
        //          ⛔ 刻意**不合併**兩個讀數源成一個數字 —— 「批次還在」與「批次已結清」
        //          是兩件不同的事，合併就回到 0195／0198 那族同形。
        // 數值影響：純讀，不寫券帳。查無時 `found=0` 並明說兩邊都查無（⛔ 不以發放量推導）。
        // ===========================================================
        void Op_Usage(Dictionary<string, string> args)
        {
            string persona = GetArg(args, "persona", "");
            string refText = GetArg(args, "ref", "");
            if (string.IsNullOrEmpty(persona)) { Reject(args, "usage 缺少 persona"); return; }
            if (string.IsNullOrEmpty(refText)) { Reject(args, "usage 缺少 ref（⛔ 不接受空 ref —— 那會變成查全部）"); return; }

            bool aFromBatches = UCL_CanvasVoucherLedger.TryGetUsageByRef(
                persona, refText, out int aGranted, out int aRemain, out int aUsed);
            int aForfeited = aRemain;
            bool aFromHistory = false;
            if (!aFromBatches)
            {
                aFromHistory = UCL_CanvasVoucherLedger.TryGetUsageFromHistoryByRef(
                    persona, refText, out aGranted, out aForfeited, out aUsed);
            }
            string aSource = aFromBatches ? "batches" : aFromHistory ? "history" : "none";

            WriteLastOp(args, aSource == "none"
                ? $"# 🔍 繪圖券 usage\n\n- persona: `{persona}`\n- ref: `{refText}`\n"
                + "- **查無這一批**（`batches` 與 `history` 兩邊都沒有）⇒ 用量無法判定。\n"
                + "\n> ⛔ 不以發放量推導（TASK-0195）。2026-09-11 之前被清掉的批次沒有可歸戶的清理列，"
                + "那些**永久只能答查無** —— 那是舊資料的事實，不是本 op 壞了（TASK-0198）。\n"
                : $"# 🔍 繪圖券 usage\n\n- persona: `{persona}`\n- ref: `{refText}`\n"
                + $"- 發放: **{aGranted}**／用掉: **{aUsed}**／作廢: **{aForfeited}**\n"
                + $"- 讀數源: `{aSource}`"
                + (aSource == "batches" ? "（批次還在帳上，作廢數＝此刻剩餘）\n" : "（批次已結清，自 history 的 grant − expire 結算）\n"));

            UCL_AgentCommandRunner.ReportOutputValue(args, "found", aSource == "none" ? "0" : "1");
            UCL_AgentCommandRunner.ReportOutputValue(args, "source", aSource);
            // ⛔ 查無時**不發**這三欄 —— 一個 `granted=10` 擺在 `found=0` 旁邊，
            //    就是把「10 − 0」這個減法遞給下一個呼叫端（TASK-0195 的原病）。
            //    ⇒ 欄位缺席比帶一個不可用的數字誠實，而機讀端讀不到欄位會炸，不會靜默算錯。
            if (aSource != "none")
            {
                UCL_AgentCommandRunner.ReportOutputValue(args, "granted", aGranted.ToString());
                UCL_AgentCommandRunner.ReportOutputValue(args, "used", aUsed.ToString());
                UCL_AgentCommandRunner.ReportOutputValue(args, "forfeited", aForfeited.ToString());
            }
            Debug.Log($"[CanvasVoucher] usage {persona} ref={refText}: source={aSource} granted={aGranted} used={aUsed} forfeited={aForfeited}");
        }

        void Op_Grant(Dictionary<string, string> args)
        {
            string persona = GetArg(args, "persona", "");
            string amountStr = GetArg(args, "amount", "0");
            string source = GetArg(args, "source", "manual_grant");
            string refText = GetArg(args, "ref", "");
            if (string.IsNullOrEmpty(persona)) { Reject(args, "grant 缺少 persona"); return; }
            if (!int.TryParse(amountStr, out int amount) || amount <= 0) { Reject(args, $"grant amount 無效或非正數: {amountStr}"); return; }

            // 期間限定券（Tim 2026-08-18）：`expires_at` 空 ＝ 永久券 ⇒ 不帶這個參數時行為與改動前逐值相同。
            string aExpiresAt = GetArg(args, "expires_at", "").Trim();
            var (before, after) = UCL_CanvasVoucherLedger.Grant(persona, amount, source, refText, aExpiresAt);
            WriteLastOp(args, $"# ✅ 繪圖券 grant\n\n- persona: `{persona}`\n- amount: **+{amount}**\n- source: `{source}`\n- balance: {before} → **{after}**\n");
        }

        void Op_Consume(Dictionary<string, string> args)
        {
            string persona = GetArg(args, "persona", "");
            string amountStr = GetArg(args, "amount", "0");
            string source = GetArg(args, "source", "canvas_place");
            string refText = GetArg(args, "ref", "");
            if (string.IsNullOrEmpty(persona)) { Reject(args, "consume 缺少 persona"); return; }
            if (!int.TryParse(amountStr, out int amount) || amount <= 0) { Reject(args, $"consume amount 無效或非正數: {amountStr}"); return; }

            var (before, after) = UCL_CanvasVoucherLedger.Consume(persona, amount, source, refText);
            WriteLastOp(args, $"# ✅ 繪圖券 consume\n\n- persona: `{persona}`\n- amount: **-{amount}**\n- use: `{source}`\n- balance: {before} → **{after}**\n");
        }

        // 借用 ChatTavern render 寫 _last_op.md（對齊 Cmd_Treasury 的 helper 做法，避免跨 asmdef 依賴問題）
        // ⚠ TASK-0116：**一定要把 args 交出去** —— 回傳檔要鏡寫進哪個 persona 的 lane，來源只有
        //   `args["_cmd_id"]`。不給就退回全域 static slot，而那個 slot 在併發的 lane 之間 last-write-wins
        //   ⇒ 本檔的券帳報告會落進**別人的** lane（那正是 0116 的原始症狀）。
        static void WriteLastOp(Dictionary<string, string> iArgs, string md) =>
            UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_ChatTavernRender.WriteLastOp(md, iArgs);

        static void Reject(Dictionary<string, string> iArgs, string msg)
        {
            WriteLastOp(iArgs, $"# ❌ CanvasVoucher Cmd Rejected\n\n{msg}\n");
            throw new System.InvalidOperationException(msg);
        }

        static void Fail(Dictionary<string, string> iArgs, string msg)
        {
            WriteLastOp(iArgs, $"# ❌ CanvasVoucher Cmd Failed\n\n{msg}\n");
            throw new System.Exception(msg);
        }

        static string GetArg(Dictionary<string, string> args, string key, string def) =>
            args != null && args.TryGetValue(key, out var v) ? v : def;
    }
}
#endif
