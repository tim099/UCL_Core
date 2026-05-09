// 區塊職責：T40 Cmd_Treasury — agent CMD wrapper for Treasury Ledger
// 物理意義：thin wrapper 委派 UCL_TreasuryLedger Static API；agent 透過 run_cmd.py 觸發
// 數值影響：op-dispatch（balance / credit / debit / audit）
// 安全：debit 帳戶隔離鐵律由 Static API 處理；本層只 parse args + 寫 _last_op.md

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;   // 借 WriteLastOp / FailLastOp / RejectLastOp helper
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Treasury
{
    public class Cmd_Treasury : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Treasury";
        public override string ShortDescription => "Treasury Ledger — agent token 帳本（credit / debit / balance / audit）";

        public override string ArgsSchema =>
            "balance: account=帳戶ID（必填）[currency=tavern_token]\n" +
            "credit: account=帳戶ID amount=N source_kind=enum [source_ref=...] [description=...] [caller=自報agent_id] — 進帳\n" +
            "debit: account=帳戶ID amount=N use_kind=enum [use_ref=...] [description=...] [caller=自報agent_id] — 出帳；caller 必須==account（除非 system）\n" +
            "audit: account=帳戶ID [since_ts=ISO8601] — 列 entries\n" +
            "verify: account=帳戶ID — 跑 replay 驗 balance_after consistency";

        public override string ExampleArgs =>
            "op=balance;account=claude-da-xiaojie";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Treasury.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();   // 讓 UniTask signature 滿足

            string op = GetArg(args, "op", "").ToLowerInvariant();
            if (string.IsNullOrEmpty(op))
            {
                Cmd_Tavern_Helpers.RejectLastOp("缺少 op 參數（balance / credit / debit / audit / verify）");
                return;
            }

            try
            {
                switch (op)
                {
                    case "balance":  Op_Balance(args); break;
                    case "credit":   Op_Credit(args); break;
                    case "debit":    Op_Debit(args); break;
                    case "audit":    Op_Audit(args); break;
                    case "verify":   Op_Verify(args); break;
                    default:
                        Cmd_Tavern_Helpers.RejectLastOp($"未知 op: {op}");
                        break;
                }
            }
            catch (System.Exception ex)
            {
                Cmd_Tavern_Helpers.FailLastOp($"執行 op={op} 失敗：{ex.Message}\n{ex.StackTrace}");
            }
        }

        void Op_Balance(Dictionary<string, string> args)
        {
            string account = GetArg(args, "account", "");
            string currency = GetArg(args, "currency", "tavern_token");
            if (string.IsNullOrEmpty(account)) { Cmd_Tavern_Helpers.RejectLastOp("balance 缺少 account"); return; }

            int balance = UCL_TreasuryLedger.GetBalance(account, currency);
            string md = $"# 💰 Treasury balance\n\n- account: `{account}`\n- currency: {currency}\n- **balance: {balance}**\n";
            Cmd_Tavern_Helpers.WriteLastOp(md);
            Debug.Log($"[Treasury] balance {account} = {balance} {currency}");
        }

        void Op_Credit(Dictionary<string, string> args)
        {
            string account = GetArg(args, "account", "");
            string amountStr = GetArg(args, "amount", "0");
            string sourceKind = GetArg(args, "source_kind", "");
            string sourceRef = GetArg(args, "source_ref", "");
            string description = GetArg(args, "description", "");
            string caller = GetArg(args, "caller", "");
            string cmdId = GetArg(args, "cmd_id", "");

            if (string.IsNullOrEmpty(account)) { Cmd_Tavern_Helpers.RejectLastOp("credit 缺少 account"); return; }
            if (!int.TryParse(amountStr, out int amount) || amount <= 0)
            { Cmd_Tavern_Helpers.RejectLastOp($"credit amount 無效或非正數: {amountStr}"); return; }
            if (string.IsNullOrEmpty(sourceKind)) { Cmd_Tavern_Helpers.RejectLastOp("credit 缺少 source_kind"); return; }

            var entry = UCL_TreasuryLedger.Credit(account, amount, sourceKind, sourceRef, description, caller, cmdId);
            string md = BuildEntryMd("credit", entry);
            Cmd_Tavern_Helpers.WriteLastOp(md);
        }

        void Op_Debit(Dictionary<string, string> args)
        {
            string account = GetArg(args, "account", "");
            string amountStr = GetArg(args, "amount", "0");
            string useKind = GetArg(args, "use_kind", "");
            string useRef = GetArg(args, "use_ref", "");
            string description = GetArg(args, "description", "");
            string caller = GetArg(args, "caller", "");
            string cmdId = GetArg(args, "cmd_id", "");

            if (string.IsNullOrEmpty(account)) { Cmd_Tavern_Helpers.RejectLastOp("debit 缺少 account"); return; }
            if (!int.TryParse(amountStr, out int amount) || amount <= 0)
            { Cmd_Tavern_Helpers.RejectLastOp($"debit amount 無效或非正數: {amountStr}"); return; }
            if (string.IsNullOrEmpty(useKind)) { Cmd_Tavern_Helpers.RejectLastOp("debit 缺少 use_kind"); return; }

            var entry = UCL_TreasuryLedger.Debit(account, amount, useKind, useRef, description, caller, cmdId);
            string md = BuildEntryMd("debit", entry);
            Cmd_Tavern_Helpers.WriteLastOp(md);
        }

        void Op_Audit(Dictionary<string, string> args)
        {
            string account = GetArg(args, "account", "");
            string sinceTs = GetArg(args, "since_ts", "");
            if (string.IsNullOrEmpty(account)) { Cmd_Tavern_Helpers.RejectLastOp("audit 缺少 account"); return; }

            var entries = UCL_TreasuryLedger.Audit(account, sinceTs);
            var sb = new StringBuilder();
            sb.Append($"# 📒 Treasury audit — `{account}`");
            if (!string.IsNullOrEmpty(sinceTs)) sb.Append($" (since `{sinceTs}`)");
            sb.AppendLine($"\n\n共 {entries.Count} 筆 entries\n");
            foreach (var e in entries)
            {
                string flag = e.signature_mismatch ? " ⚠ sig_mismatch" : "";
                sb.AppendLine($"- [{e.ts}] `{e.type}` {e.amount} {e.currency} | {e.source_kind}({e.source_ref}) | balance: {e.balance_before}→{e.balance_after}{flag}");
            }
            Cmd_Tavern_Helpers.WriteLastOp(sb.ToString());
            Debug.Log($"[Treasury] audit {account} → {entries.Count} entries");
        }

        void Op_Verify(Dictionary<string, string> args)
        {
            string account = GetArg(args, "account", "");
            if (string.IsNullOrEmpty(account)) { Cmd_Tavern_Helpers.RejectLastOp("verify 缺少 account"); return; }

            var entries = UCL_TreasuryLedger.Audit(account, null);
            int expectedBalance = 0;
            int driftCount = 0;
            var sb = new StringBuilder();
            foreach (var e in entries)
            {
                int beforeMatch = e.balance_before;
                if (beforeMatch != expectedBalance)
                {
                    driftCount++;
                    sb.AppendLine($"- ⚠ DRIFT entry uuid={e.uuid}: balance_before={beforeMatch} but replay={expectedBalance}");
                }
                if (e.type == "credit") expectedBalance += e.amount;
                else if (e.type == "debit") expectedBalance -= e.amount;
                if (e.balance_after != expectedBalance)
                {
                    driftCount++;
                    sb.AppendLine($"- ⚠ DRIFT entry uuid={e.uuid}: balance_after={e.balance_after} but replay={expectedBalance}");
                }
            }
            string head = $"# 🔍 Treasury verify — `{account}`\n\n- entries: {entries.Count}\n- final balance (replay): {expectedBalance}\n- drift count: {driftCount}\n";
            string status = driftCount == 0 ? "\n✅ ledger consistent" : "\n❌ DRIFT detected\n\n" + sb.ToString();
            Cmd_Tavern_Helpers.WriteLastOp(head + status);
            Debug.Log($"[Treasury] verify {account}: {entries.Count} entries, drift={driftCount}");
        }

        string BuildEntryMd(string action, TreasuryLedgerEntry e)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# ✅ Treasury {action}");
            sb.AppendLine();
            sb.AppendLine($"- account: `{e.account_id}`");
            sb.AppendLine($"- amount: **{e.amount}** {e.currency}");
            sb.AppendLine($"- {(action == "credit" ? "source" : "use")}_kind: `{e.source_kind}`");
            if (!string.IsNullOrEmpty(e.source_ref)) sb.AppendLine($"- ref: `{e.source_ref}`");
            sb.AppendLine($"- balance: {e.balance_before} → **{e.balance_after}**");
            sb.AppendLine($"- ts: {e.ts}");
            sb.AppendLine($"- uuid: `{e.uuid}`");
            sb.AppendLine($"- env_marker: `{e.sig_env_marker}` (claimed: `{e.sig_agent_id_claimed}`)");
            if (e.signature_mismatch) sb.AppendLine($"- ⚠ **signature_mismatch** — Tim 可走 audit 查");
            return sb.ToString();
        }

        static string GetArg(Dictionary<string, string> args, string key, string def)
        {
            return args != null && args.TryGetValue(key, out var v) ? v : def;
        }
    }

    /// <summary>Helper static methods 借用 — 避免 dependency loop / asmdef 跨界問題。</summary>
    static class Cmd_Tavern_Helpers
    {
        public static void WriteLastOp(string md)
        {
            UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_ChatTavernRender.WriteLastOp(md);
        }

        public static void RejectLastOp(string msg)
        {
            string md = $"# ❌ Treasury Cmd Rejected\n\n{msg}\n";
            WriteLastOp(md);
            throw new System.InvalidOperationException(msg);
        }

        public static void FailLastOp(string msg)
        {
            string md = $"# ❌ Treasury Cmd Failed\n\n{msg}\n";
            WriteLastOp(md);
            throw new System.Exception(msg);
        }
    }
}
#endif
