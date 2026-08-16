// 區塊職責：T40 Cmd_Treasury — agent CMD wrapper for Treasury Ledger
// 物理意義：thin wrapper 委派 UCL_TreasuryLedger Static API；agent 透過 run_cmd.py 觸發
// 數值影響：op-dispatch（12 個 op：餘額 / 進出帳 / 守恆轉帳 / 請款單 / 轉帳單 / 每日結帳）
// 安全：debit 帳戶隔離鐵律由 Static API 處理；本層只 parse args + 寫 _last_op.md
// @doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_Treasury.md（§2 op 一覽 / §4 kind 不驗值）

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

        // ⚠ source_kind / use_kind 標「分類字串」而非「enum」是刻意的：C# 只驗非空、**不驗值**，
        //   rules.json 的 income_sources / spending_uses 是宣告沒有任何程式讀它把關
        //   （2026-08-04 實測：credit 有 15 種、debit 有 17 種用過但未宣告的值）。
        //   寫「enum」會讓 caller 以為有人把關 —— 那是文件層說謊，理由見 Cmd_Treasury.md §4。
        public override string ArgsSchema =>
            "balance: account=帳戶ID（必填）[currency=tavern_token]\n" +
            "credit: account=帳戶ID amount=N source_kind=分類字串(必填,不驗值) [source_ref=...] [description=...] [caller=自報agent_id] [idempotency_key=...] — 進帳\n" +
            "debit: account=帳戶ID amount=N use_kind=分類字串(必填,不驗值) [use_ref=...] [description=...] [caller=自報agent_id] [idempotency_key=...] — 出帳；caller 必須==account（除非 system）\n" +
            "transfer (T55): from_account to_account amount use_kind source_kind [reason_ref] [description] [tx_id] [caller=system] — 跨帳戶守恆轉移；atomic dual entry 共用 tx_id；mid-fail rollback\n" +
            "audit: account=帳戶ID [since_ts=ISO8601] — 列 entries\n" +
            "verify: account=帳戶ID — 跑 replay 驗 balance_after consistency\n" +
            "request: target_bank=收款bank amount=N reason=為什麼該付 [source_kind=commit|tim_grant|...] [source_ref=SHA/task_id] [agent=請款者agent] [persona=請款者persona] — 開請款單（不動錢，等 Tim 從 UCL_BankAdminPage 批款）\n" +
            "request_list: [pending_only=true|false] [max=200] — 列請款單\n" +
            "request_cancel: request_id=<id> [note=原因] — 撤回自己開的請款單\n" +
            "transfer_request: from_bank=出款bank to_bank=收款bank amount=N reason=為什麼該搬 [kind=manual_transfer] [agent=] [persona=] — 開轉帳單（不動錢，總量守恆；請款單消耗公庫，兩者刻意分開）\n" +
            "closing_generate: （無參數）— 補算所有「已完結但未結帳」的 UTC 日；只寫 closing/*.json，不動餘額\n" +
            "closing_list: （無參數）— 列已結帳日期與當前讀取基準";

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
                // 錯誤訊息列全部 op —— 舊版只列 5 個，漏掉的 7 個對讀錯誤訊息的人等於不存在。
                Cmd_Tavern_Helpers.RejectLastOp(
                    "缺少 op 參數（balance / credit / debit / transfer / audit / verify / "
                    + "request / request_list / request_cancel / transfer_request / "
                    + "closing_generate / closing_list）");
                return;
            }

            try
            {
                switch (op)
                {
                    case "balance":  Op_Balance(args); break;
                    case "credit":   Op_Credit(args); break;
                    case "debit":    Op_Debit(args); break;
                    case "transfer": Op_Transfer(args); break;   // T55 closed economy v2
                    case "audit":    Op_Audit(args); break;
                    case "verify":   Op_Verify(args); break;
                    // 請款流程（Tim 2026-07-31 拍板）—— agent 開單，Tim 從 UCL_BankAdminPage 批款
                    case "request":        Op_Request(args); break;
                    case "request_list":   Op_RequestList(args); break;
                    case "request_cancel": Op_RequestCancel(args); break;
                    // 轉帳單（2026-08-04）—— 開單提案「A→B 搬錢」，Tim 從後台「💸 轉帳審批」核准。
                    // 與 request 分開的理由：請款消耗公庫、轉帳總量守恆，審批者要能一眼分辨。
                    case "transfer_request": Op_TransferRequest(args); break;
                    // 每日結帳（2026-08-04）—— 平時由酒保跨日 tick 自動產生；
                    // 這個 op 是給人手動補算用的（首次上線 backfill / 確認結帳狀態）。
                    // 規格明訂「不自動 rebuild」，但**人必須有辦法補算** —— 否則
                    // 一個不可手動觸發的機制既難驗證也難救援（可測性不是奢侈品）。
                    case "closing_generate": Op_ClosingGenerate(args); break;
                    case "closing_list": Op_ClosingList(args); break;
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
            // 機器可讀的回報 —— 呼叫端（python `_lib/treasury_cmd.treasury_balance`）拿這個值，
            // 不必去 parse markdown。⚠ 沒有這一欄的時代，python 端各自全掃帳本自己算：
            // 四份複製品、每份 14,985 檔逐檔 json.load，冷檔案快取下近兩分鐘
            // （2026-08-16 basecamp 量測：morning 的 brief 被拖到 112s，08-13 更撞 timeout 被 kill）。
            // 「銀行/token 一律走 C#」是 Tim 2026-08-04 的定調，寫入端當時就搬了，讀取端漏在原地。
            UCL_AgentCommandRunner.ReportOutputValue("balance", balance.ToString());
            UCL_AgentCommandRunner.ReportOutputValue("account", account);
            UCL_AgentCommandRunner.ReportOutputValue("currency", currency);
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

            // 冪等鍵（選帶）— 同 Op_Debit；退款 / 撥款重跑不該入帳兩次
            string idemKey = GetArg(args, "idempotency_key", "");
            var entry = UCL_TreasuryLedger.Credit(account, amount, sourceKind, sourceRef, description, caller, cmdId, idemKey);
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
            // 冪等鍵（選帶）— caller 顯式宣告「這筆要防重」；空 = 照舊不判重
            string idemKey = GetArg(args, "idempotency_key", "");

            if (string.IsNullOrEmpty(account)) { Cmd_Tavern_Helpers.RejectLastOp("debit 缺少 account"); return; }
            if (!int.TryParse(amountStr, out int amount) || amount <= 0)
            { Cmd_Tavern_Helpers.RejectLastOp($"debit amount 無效或非正數: {amountStr}"); return; }
            if (string.IsNullOrEmpty(useKind)) { Cmd_Tavern_Helpers.RejectLastOp("debit 缺少 use_kind"); return; }

            var entry = UCL_TreasuryLedger.Debit(account, amount, useKind, useRef, description, caller, cmdId, idemKey);
            string md = BuildEntryMd("debit", entry);
            Cmd_Tavern_Helpers.WriteLastOp(md);
        }

        // 區塊職責：T55 closed economy v2 — atomic 跨帳戶 transfer
        // 物理意義：from_account 出 amount → to_account 入 amount，雙 ledger entry 共用 tx_id
        // 數值影響：寫 2 筆 ledger entries (Debit + Credit)；mid-fail rollback fire transfer_rollback credit
        // 安全：from balance < amount 由 UCL_TreasuryLedger.Debit 內部 throw；本層只 orchestrate
        void Op_Transfer(Dictionary<string, string> args)
        {
            // 解析必填 + 可選參數
            string fromAccount = GetArg(args, "from_account", "");   // 出帳方
            string toAccount = GetArg(args, "to_account", "");       // 進帳方
            string amountStr = GetArg(args, "amount", "0");          // 金額（正整數）
            string useKind = GetArg(args, "use_kind", "");           // from 端 use_kind enum
            string sourceKind = GetArg(args, "source_kind", "");     // to 端 source_kind enum
            string reasonRef = GetArg(args, "reason_ref", "");       // 業務 ref（可選）
            string description = GetArg(args, "description", "");    // 人類描述
            string caller = GetArg(args, "caller", "");              // 簽章 agent_id
            string txId = GetArg(args, "tx_id", "");                 // 交易 id（自帶或自動生成）

            // 驗證 args
            if (string.IsNullOrEmpty(fromAccount)) { Cmd_Tavern_Helpers.RejectLastOp("transfer 缺少 from_account"); return; }
            if (string.IsNullOrEmpty(toAccount)) { Cmd_Tavern_Helpers.RejectLastOp("transfer 缺少 to_account"); return; }
            if (fromAccount == toAccount) { Cmd_Tavern_Helpers.RejectLastOp("transfer from==to 自轉禁止"); return; }
            if (!int.TryParse(amountStr, out int amount) || amount <= 0)
            { Cmd_Tavern_Helpers.RejectLastOp($"transfer amount 無效或非正數: {amountStr}"); return; }
            if (amount > 1000) { Cmd_Tavern_Helpers.RejectLastOp($"transfer amount 超過 max_per_transfer=1000: {amount}"); return; }
            if (string.IsNullOrEmpty(useKind)) { Cmd_Tavern_Helpers.RejectLastOp("transfer 缺少 use_kind (from 端)"); return; }
            if (string.IsNullOrEmpty(sourceKind)) { Cmd_Tavern_Helpers.RejectLastOp("transfer 缺少 source_kind (to 端)"); return; }

            // 沒帶 tx_id 自動生成 tx_<8 位 hex>
            if (string.IsNullOrEmpty(txId))
            {
                txId = "tx_" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
            }

            // 組 fullRef：tx_id 在前以利 grep；reason_ref 在後保留業務語意
            string fullRef = string.IsNullOrEmpty(reasonRef) ? txId : (txId + "|" + reasonRef);

            // Step 1: Debit from_account（不足會 throw → 整個 cmd fail，沒有 dangling state）
            TreasuryLedgerEntry debitEntry;
            try
            {
                debitEntry = UCL_TreasuryLedger.Debit(fromAccount, amount, useKind, fullRef, description, caller, txId);
            }
            catch (System.Exception ex)
            {
                Cmd_Tavern_Helpers.FailLastOp($"transfer Debit 失敗: {ex.Message}");
                return;
            }

            // Step 2: Credit to_account（罕見失敗如 disk full → 必須 rollback）
            TreasuryLedgerEntry creditEntry;
            try
            {
                creditEntry = UCL_TreasuryLedger.Credit(toAccount, amount, sourceKind, fullRef, description, caller, txId);
            }
            catch (System.Exception ex)
            {
                // Rollback：退錢給 from_account（用 transfer_rollback 標明）
                try
                {
                    UCL_TreasuryLedger.Credit(fromAccount, amount, "transfer_rollback", txId + "|rollback",
                        "Rollback failed transfer: " + ex.Message, "system", txId + "_rollback");
                }
                catch (System.Exception rollbackEx)
                {
                    Cmd_Tavern_Helpers.FailLastOp($"transfer Credit 失敗 + Rollback 也失敗: orig={ex.Message} / rollback={rollbackEx.Message} / DANGLING DEBIT entry uuid={debitEntry.uuid}");
                    return;
                }
                Cmd_Tavern_Helpers.FailLastOp($"transfer Credit 失敗已 rollback: {ex.Message}");
                return;
            }

            // 寫 _last_op.md 給 caller 看結果
            var sb = new StringBuilder();
            sb.AppendLine("# 🔁 Treasury Transfer");
            sb.AppendLine();
            sb.AppendLine($"- tx_id: `{txId}`");
            sb.AppendLine($"- from: `{fromAccount}` → to: `{toAccount}`");
            sb.AppendLine($"- amount: **{amount}** {debitEntry.currency}");
            sb.AppendLine($"- use_kind (from): `{useKind}`");
            sb.AppendLine($"- source_kind (to): `{sourceKind}`");
            if (!string.IsNullOrEmpty(reasonRef)) sb.AppendLine($"- reason_ref: `{reasonRef}`");
            sb.AppendLine();
            sb.AppendLine("## Debit entry (from)");
            sb.AppendLine($"- balance: {debitEntry.balance_before} → **{debitEntry.balance_after}**");
            sb.AppendLine($"- uuid: `{debitEntry.uuid}`");
            sb.AppendLine();
            sb.AppendLine("## Credit entry (to)");
            sb.AppendLine($"- balance: {creditEntry.balance_before} → **{creditEntry.balance_after}**");
            sb.AppendLine($"- uuid: `{creditEntry.uuid}`");
            Cmd_Tavern_Helpers.WriteLastOp(sb.ToString());
            Debug.Log($"[Treasury] transfer {fromAccount} → {toAccount} = {amount} (tx={txId})");
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

        // ===========================================================
        // 區塊：請款流程（Tim 2026-07-31 拍板）— agent 開單 → Tim 在 UCL_BankAdminPage 批款
        // 物理意義：補上「agent 主張該收錢」這條正規管道。在此之前只有兩種極端：
        //          ① 自動 hook（work_post / commit 公告）—— 規則寫死，超出規則的勞動無處可請
        //          ② 請 Tim 手動 credit —— 沒有單據、沒有稽核痕跡、講過就忘
        //          請款單填補中間：**有單據、可審批、可駁回、可追溯**。
        // 數值影響：op=request / request_cancel 完全不動餘額（純檔案）；
        //          錢只在 Tim 於後台按「核准」時才由 UCL_TreasuryRequestStore.Approve 產生。
        // 邊界：target_bank 必須是**bank id 不是 persona 名** —— 2026-07-31 血證：
        //      commit hook 拿貼文 sender 當帳戶，summit 帶 persona 名 `summit`（bank 應為 `zeta`）
        //      → 錢進影子帳戶。這裡改為顯式宣告 + 後台人眼二次確認，不做任何推斷。
        // ===========================================================
        void Op_Request(Dictionary<string, string> args)
        {
            string targetBank = GetArg(args, "target_bank", "");
            string reason = GetArg(args, "reason", "");
            string amountRaw = GetArg(args, "amount", "");
            if (string.IsNullOrEmpty(targetBank)) { Cmd_Tavern_Helpers.RejectLastOp("request 缺少 target_bank（收款 bank id，例 cc / zeta / Myth —— 不是 persona 名）"); return; }
            if (string.IsNullOrEmpty(reason)) { Cmd_Tavern_Helpers.RejectLastOp("request 缺少 reason —— 審批者要有東西可判，不接受無理由請款"); return; }
            if (!int.TryParse(amountRaw, out int amount) || amount <= 0)
            { Cmd_Tavern_Helpers.RejectLastOp($"request 的 amount 需為正整數（收到 '{amountRaw}'）"); return; }

            try
            {
                var req = UCL_TreasuryRequestStore.Create(
                    targetBank: targetBank,
                    amount: amount,
                    reason: reason,
                    sourceKind: GetArg(args, "source_kind", "manual_request"),
                    sourceRef: GetArg(args, "source_ref", ""),
                    requesterAgent: GetArg(args, "agent", GetArg(args, "caller", "")),
                    requesterPersona: GetArg(args, "persona", ""),
                    currency: GetArg(args, "currency", "tavern_token"));

                var sb = new StringBuilder();
                sb.AppendLine($"# 🧾 請款單已開 — `{req.request_id}`");
                sb.AppendLine();
                sb.AppendLine($"- 金額：**{req.amount} {req.currency}**");
                sb.AppendLine($"- 收款 bank：**{req.target_bank}**");
                sb.AppendLine($"- 理由：{req.reason}");
                sb.AppendLine($"- source_kind / ref：{req.source_kind} / {(string.IsNullOrEmpty(req.source_ref) ? "(無)" : req.source_ref)}");
                sb.AppendLine($"- 請款者：{req.requester_agent}@{req.requester_persona}");
                sb.AppendLine($"- 狀態：**{req.status}** —— 錢還沒動，等 Tim 從 UCL_BankAdminPage 的「📨 請款審批」批款");
                Cmd_Tavern_Helpers.WriteLastOp(sb.ToString());
            }
            catch (System.ArgumentException ex) { Cmd_Tavern_Helpers.RejectLastOp($"request 參數不合法：{ex.Message}"); }
        }

        // 區塊職責：op=transfer_request —— 開一張「從 A 轉到 B」的待審轉帳單。
        // 物理意義：讓「動別人帳戶的錢」也有提案通道，而不是只能由後台手按。
        //          主要用途是**歸戶**（把錢從孤兒 / 打錯字的帳戶搬回正主）——
        //          這種搬移必須留下「誰提的、為什麼」，否則事後只看得到 ledger 兩筆莫名的進出。
        // 數值影響：**零** —— 只寫一張 pending 單，核准才動錢。
        // 邊界：from == to / amount <= 0 / 缺 reason 一律拒收（由 Store 丟 ArgumentException 轉成 reject）。
        //      **不檢查 from 是否為合法帳戶** —— 歸戶的出款方本來就常是不合法的孤兒帳戶。
        void Op_TransferRequest(Dictionary<string, string> args)
        {
            string fromBank = GetArg(args, "from_bank", "");
            string toBank = GetArg(args, "to_bank", "");
            string reason = GetArg(args, "reason", "");
            string amountRaw = GetArg(args, "amount", "");
            if (string.IsNullOrEmpty(fromBank)) { Cmd_Tavern_Helpers.RejectLastOp("transfer_request 缺少 from_bank（出款 bank id，不是 persona 名）"); return; }
            if (string.IsNullOrEmpty(toBank)) { Cmd_Tavern_Helpers.RejectLastOp("transfer_request 缺少 to_bank（收款 bank id）"); return; }
            if (string.IsNullOrEmpty(reason)) { Cmd_Tavern_Helpers.RejectLastOp("transfer_request 缺少 reason —— 審批者要有東西可判"); return; }
            if (!int.TryParse(amountRaw, out int amount) || amount <= 0)
            { Cmd_Tavern_Helpers.RejectLastOp($"transfer_request 的 amount 需為正整數（收到 '{amountRaw}'）"); return; }

            try
            {
                var req = UCL_TreasuryTransferRequestStore.Create(
                    fromBank: fromBank,
                    toBank: toBank,
                    amount: amount,
                    reason: reason,
                    kind: GetArg(args, "kind", "manual_transfer"),
                    requesterAgent: GetArg(args, "agent", GetArg(args, "caller", "")),
                    requesterPersona: GetArg(args, "persona", ""),
                    currency: GetArg(args, "currency", "tavern_token"));

                var sb = new StringBuilder();
                sb.AppendLine($"# 💸 轉帳單已開 — `{req.request_id}`");
                sb.AppendLine();
                sb.AppendLine($"- 金額：**{req.amount} {req.currency}**");
                sb.AppendLine($"- 出款 bank：**{req.from_bank}**");
                sb.AppendLine($"- 收款 bank：**{req.to_bank}**");
                sb.AppendLine($"- 分類：{req.kind}");
                sb.AppendLine($"- 理由：{req.reason}");
                sb.AppendLine($"- 提案者：{req.requester_agent}@{req.requester_persona}");
                sb.AppendLine($"- 狀態：**{req.status}** —— 錢還沒動，等 Tim 從 UCL_BankAdminPage 的「💸 轉帳審批」核准");
                Cmd_Tavern_Helpers.WriteLastOp(sb.ToString());
            }
            catch (System.ArgumentException ex) { Cmd_Tavern_Helpers.RejectLastOp($"transfer_request 參數不合法：{ex.Message}"); }
        }

        // 區塊職責：op=closing_generate —— 補齊所有「已完結但尚未結帳」的 UTC 日期。
        // 物理意義：平時由酒保跨日 tick 自動跑；本 op 給人手動補算（首次上線 / 確認狀態）。
        // 數值影響：只寫 Treasury/closing/*.json，**不動任何餘額、不動 ledger**。
        // 邊界：今天不會被結帳（今天還在寫）；已結過的日期不重複寫。
        void Op_ClosingGenerate(Dictionary<string, string> args)
        {
            int n = UCL_TreasuryClosing.GenerateMissing(out string summary);
            var sb = new StringBuilder();
            sb.AppendLine($"# 📘 每日結帳 — 新產生 {n} 份");
            sb.AppendLine();
            sb.AppendLine($"- {summary}");
            sb.AppendLine($"- 已結帳日期共 {UCL_TreasuryClosing.ListClosingDateKeys().Count} 份");
            sb.AppendLine($"- 落檔位置：`{UCL_TreasuryPaths.GetClosingRoot()}`");
            sb.AppendLine();
            sb.AppendLine("餘額讀取 = 最近一份結帳 + 該日之後的 entry。已關帳期間不重算。");
            Cmd_Tavern_Helpers.WriteLastOp(sb.ToString());
        }

        // 區塊職責：op=closing_list —— 列出已結帳日期與最新一份的內容摘要。
        void Op_ClosingList(Dictionary<string, string> args)
        {
            var keys = UCL_TreasuryClosing.ListClosingDateKeys();
            var sb = new StringBuilder();
            sb.AppendLine($"# 📘 已結帳日期（{keys.Count} 份）");
            sb.AppendLine();
            if (keys.Count == 0)
            {
                sb.AppendLine("_(尚無結帳 — 餘額仍走全量重放；跑 `op=closing_generate` 可補算)_");
            }
            else
            {
                sb.AppendLine($"- 最早：`{keys[0]}`　最新：`{keys[keys.Count - 1]}`");
                var latest = UCL_TreasuryClosing.LoadLatestBefore(
                    UCL_TreasuryPaths.DateKey(System.DateTime.UtcNow));
                if (latest != null)
                {
                    sb.AppendLine($"- 讀取基準：`{latest.DateKey}`（{latest.Balances.Count} 個帳戶／幣別，"
                                  + $"累計 entry {latest.CumulativeEntryCount}）");
                }
            }
            Cmd_Tavern_Helpers.WriteLastOp(sb.ToString());
        }

        void Op_RequestList(Dictionary<string, string> args)
        {
            bool pendingOnly = GetArg(args, "pending_only", "true").ToLowerInvariant() != "false";
            if (!int.TryParse(GetArg(args, "max", "200"), out int max) || max <= 0) max = 200;
            var list = UCL_TreasuryRequestStore.List(pendingOnly, max);

            var sb = new StringBuilder();
            sb.AppendLine($"# 🧾 請款單列表（{(pendingOnly ? "只列 pending" : "全部")}，共 {list.Count} 筆）");
            sb.AppendLine();
            if (list.Count == 0) sb.AppendLine("（無）");
            foreach (var r in list)
            {
                sb.AppendLine($"- `{r.request_id}` **{r.amount} {r.currency}** → `{r.target_bank}`　[{r.status}]"
                    + $"　{r.requester_agent}@{r.requester_persona}　{r.requested_at}");
                sb.AppendLine($"    理由：{r.reason}");
                if (!string.IsNullOrEmpty(r.decision_note)) sb.AppendLine($"    審批備註：{r.decision_note}");
            }
            Cmd_Tavern_Helpers.WriteLastOp(sb.ToString());
            Debug.Log($"[Treasury] request_list: {list.Count} 筆（pendingOnly={pendingOnly}）");
        }

        void Op_RequestCancel(Dictionary<string, string> args)
        {
            string id = GetArg(args, "request_id", "");
            if (string.IsNullOrEmpty(id)) { Cmd_Tavern_Helpers.RejectLastOp("request_cancel 缺少 request_id"); return; }
            try
            {
                var req = UCL_TreasuryRequestStore.Close(
                    id, UCL_TreasuryRequestStore.StatusCancelled,
                    decidedBy: GetArg(args, "agent", GetArg(args, "caller", "agent")),
                    note: GetArg(args, "note", ""));
                Cmd_Tavern_Helpers.WriteLastOp($"# 🗑 請款單已撤回 — `{req.request_id}`\n\n"
                    + $"- 原請款：{req.amount} {req.currency} → `{req.target_bank}`\n- 狀態：**{req.status}**\n");
            }
            catch (System.Exception ex) { Cmd_Tavern_Helpers.RejectLastOp($"request_cancel 失敗：{ex.Message}"); }
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
