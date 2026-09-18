// 區塊職責：T40 Cmd_Treasury — agent CMD wrapper for Treasury Ledger
// 物理意義：thin wrapper 委派 UCL_TreasuryLedger Static API；agent 透過 run_cmd.py 觸發
// 數值影響：op-dispatch（13 個 op：餘額 / 整批餘額 / 進出帳 / 守恆轉帳 / 請款單 / 轉帳單 / 每日結帳）
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
            "closing_list: （無參數）— 列已結帳日期與當前讀取基準\n" +
            "bank_diff: [currency=tavern_token] [out_path=報表路徑] — 舊 Treasury vs 新銀行**逐戶**對帳（純讀，TASK-0235 ④）\n" +
            "bank_mirror: （無參數＝只看現況）[enabled=1|0] [senate_path=絕對路徑|clear] — 鏡像的開關與 senate 執行檔（TASK-0235）";

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
                Cmd_Tavern_Helpers.RejectLastOp(args, 
                    "缺少 op 參數（balance / balances / credit / debit / transfer / audit / verify / "
                    + "request / request_list / request_cancel / transfer_request / "
                    + "closing_generate / closing_list）");
                return;
            }

            try
            {
                switch (op)
                {
                    case "balance":  Op_Balance(args); break;
                    case "balances": Op_Balances(args); break;   // 整批唯讀（遷移／畫表用）
                    case "bank_diff": Op_BankDiff(args); break;  // 舊帳本 vs 新銀行 逐戶對帳（TASK-0235 ④）
                    case "bank_mirror": Op_BankMirror(args); break;  // 鏡像的開關／senate 路徑／游標（TASK-0235）
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
                        Cmd_Tavern_Helpers.RejectLastOp(args, $"未知 op: {op}");
                        break;
                }
            }
            catch (System.Exception ex)
            {
                Cmd_Tavern_Helpers.FailLastOp(args, $"執行 op={op} 失敗：{ex.Message}\n{ex.StackTrace}");
            }
        }

        void Op_Balance(Dictionary<string, string> args)
        {
            string account = GetArg(args, "account", "");
            string currency = GetArg(args, "currency", "tavern_token");
            if (string.IsNullOrEmpty(account)) { Cmd_Tavern_Helpers.RejectLastOp(args, "balance 缺少 account"); return; }

            int balance = UCL_TreasuryLedger.GetBalance(account, currency);
            // 機器可讀的回報 —— 呼叫端（python `_lib/treasury_cmd.treasury_balance`）拿這個值，
            // 不必去 parse markdown。⚠ 沒有這一欄的時代，python 端各自全掃帳本自己算：
            // 四份複製品、每份 14,985 檔逐檔 json.load，冷檔案快取下近兩分鐘
            // （2026-08-16 basecamp 量測：morning 的 brief 被拖到 112s，08-13 更撞 timeout 被 kill）。
            // 「銀行/token 一律走 C#」是 Tim 2026-08-04 的定調，寫入端當時就搬了，讀取端漏在原地。
            UCL_AgentCommandRunner.ReportOutputValue(args, "balance", balance.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "account", account);
            UCL_AgentCommandRunner.ReportOutputValue(args, "currency", currency);
            string md = $"# 💰 Treasury balance\n\n- account: `{account}`\n- currency: {currency}\n- **balance: {balance}**\n";
            Cmd_Tavern_Helpers.WriteLastOp(args, md);
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

            if (string.IsNullOrEmpty(account)) { Cmd_Tavern_Helpers.RejectLastOp(args, "credit 缺少 account"); return; }
            if (!int.TryParse(amountStr, out int amount) || amount <= 0)
            { Cmd_Tavern_Helpers.RejectLastOp(args, $"credit amount 無效或非正數: {amountStr}"); return; }
            if (string.IsNullOrEmpty(sourceKind)) { Cmd_Tavern_Helpers.RejectLastOp(args, "credit 缺少 source_kind"); return; }

            // 冪等鍵（選帶）— 同 Op_Debit；退款 / 撥款重跑不該入帳兩次
            string idemKey = GetArg(args, "idempotency_key", "");
            var entry = UCL_TreasuryLedger.Credit(account, amount, sourceKind, sourceRef, description, caller, cmdId, idemKey);
            string md = BuildEntryMd("credit", entry);
            Cmd_Tavern_Helpers.WriteLastOp(args, md);
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

            if (string.IsNullOrEmpty(account)) { Cmd_Tavern_Helpers.RejectLastOp(args, "debit 缺少 account"); return; }
            if (!int.TryParse(amountStr, out int amount) || amount <= 0)
            { Cmd_Tavern_Helpers.RejectLastOp(args, $"debit amount 無效或非正數: {amountStr}"); return; }
            if (string.IsNullOrEmpty(useKind)) { Cmd_Tavern_Helpers.RejectLastOp(args, "debit 缺少 use_kind"); return; }

            var entry = UCL_TreasuryLedger.Debit(account, amount, useKind, useRef, description, caller, cmdId, idemKey);
            string md = BuildEntryMd("debit", entry);
            Cmd_Tavern_Helpers.WriteLastOp(args, md);
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
            if (string.IsNullOrEmpty(fromAccount)) { Cmd_Tavern_Helpers.RejectLastOp(args, "transfer 缺少 from_account"); return; }
            if (string.IsNullOrEmpty(toAccount)) { Cmd_Tavern_Helpers.RejectLastOp(args, "transfer 缺少 to_account"); return; }
            if (fromAccount == toAccount) { Cmd_Tavern_Helpers.RejectLastOp(args, "transfer from==to 自轉禁止"); return; }
            if (!int.TryParse(amountStr, out int amount) || amount <= 0)
            { Cmd_Tavern_Helpers.RejectLastOp(args, $"transfer amount 無效或非正數: {amountStr}"); return; }
            if (amount > 1000) { Cmd_Tavern_Helpers.RejectLastOp(args, $"transfer amount 超過 max_per_transfer=1000: {amount}"); return; }
            if (string.IsNullOrEmpty(useKind)) { Cmd_Tavern_Helpers.RejectLastOp(args, "transfer 缺少 use_kind (from 端)"); return; }
            if (string.IsNullOrEmpty(sourceKind)) { Cmd_Tavern_Helpers.RejectLastOp(args, "transfer 缺少 source_kind (to 端)"); return; }

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
                Cmd_Tavern_Helpers.FailLastOp(args, $"transfer Debit 失敗: {ex.Message}");
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
                    Cmd_Tavern_Helpers.FailLastOp(args, $"transfer Credit 失敗 + Rollback 也失敗: orig={ex.Message} / rollback={rollbackEx.Message} / DANGLING DEBIT entry uuid={debitEntry.uuid}");
                    return;
                }
                Cmd_Tavern_Helpers.FailLastOp(args, $"transfer Credit 失敗已 rollback: {ex.Message}");
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
            Cmd_Tavern_Helpers.WriteLastOp(args, sb.ToString());
            Debug.Log($"[Treasury] transfer {fromAccount} → {toAccount} = {amount} (tx={txId})");
        }

        // ==========================================================
        // 區塊職責：整批餘額唯讀出口 —— 遷移（TASK-0216／0223）與「畫一整張表」的那條路。
        // 物理意義：⛔ 呼叫端**不准**自己重放 ledger、也不准 parse `accounts/_balances.snapshot.txt`
        //          （本檔案頂端 Tim 2026-08-20 的硬規則）。那條禁令留下一個缺口：
        //          單帳戶有 `op=balance`，整批**沒有出口** ⇒ 想畫表的人只剩「自己重放」這條被禁的路。
        //          本 op 就是補那個缺口 —— 它只是 `UCL_TreasuryLedger.GetAllBalances()` 的薄殼。
        // 數值影響：**純讀**，不動任何帳。寫的只有可選的報表檔（呼叫端指定路徑）。
        // ⚠ 報表檔是**某一刻的快照**：它一落盤就開始過期，所以每一份都自帶 `generated_at`
        //   與帳戶數 —— 沒有那兩行的話，「剛才的餘額」與「三天前的餘額」在檔案上同形。
        // ⚠ 格式是 TSV（`id` + tab + `currency` + tab + `balance`）而不是 JSON：id 裡可能有空白
        //   （實測有一個帳戶叫 `Federal Reserve System`），但**不可能有 tab**；
        //   ⇒ 這個分隔字元不需要跳脫，而不需要跳脫的格式沒有「跳脫寫錯」那一族失效。
        // ==========================================================
        // ==========================================================
        // 區塊職責：`op=bank_mirror` —— 鏡像（`UCL_BankMirror`）的現況與兩個旋鈕。
        // 物理意義：那兩個值住在 `EditorPrefs`，而 EditorPrefs **只有開著 Editor 的人點得到** ——
        //          一個沒有入口的開關，對 agent 來說等於不存在。
        // 數值影響：不動任何帳。改的是「要不要鏡」與「用哪顆 senate」。
        // ⚠ `senate_path` 指到一個不存在的檔 ＝ TASK-0235 ② 的**反向對照**（鏡像會失敗，舊帳本不受影響）。
        // ==========================================================
        void Op_BankMirror(Dictionary<string, string> args)
        {
            string aEnabled = GetArg(args, "enabled", "");
            string aPath = GetArg(args, "senate_path", "");

            var sb = new StringBuilder();
            sb.AppendLine("## 新銀行鏡像（UCL_BankMirror）");

            if (aEnabled.Length > 0)
            {
                bool aOn = aEnabled == "1" || aEnabled.ToLowerInvariant() == "true";
                UCL_BankMirror.Enabled = aOn;
                sb.AppendLine($"- 開關改為：**{(aOn ? "開" : "關")}**");
            }
            if (aPath.Length > 0)
            {
                string aSet = aPath == "clear" ? "" : aPath;
                UCL_BankMirror.SenatePath = aSet;
                sb.AppendLine($"- senate 路徑改為：`{(aSet.Length == 0 ? "（空 ⇒ 走 PATH）" : aSet)}`");
                if (aSet.Length > 0 && !System.IO.File.Exists(aSet))
                    sb.AppendLine("  - ⚠ **那個檔不存在** —— 鏡像會逐筆失敗（cursor 保留、舊帳本不受影響）。"
                                  + "這正是反向對照要的狀態；驗完記得 `senate_path=clear`。");
            }

            sb.AppendLine($"- 現況：開關 **{(UCL_BankMirror.Enabled ? "開" : "關")}**"
                          + $"／senate `{(UCL_BankMirror.SenatePath.Length == 0 ? "（走 PATH）" : UCL_BankMirror.SenatePath)}`");
            sb.AppendLine($"- 游標檔：`{UCL_BankMirror.StatePathPublic}`");
            sb.AppendLine("- ⚠ 鏡像是**非同步**的：剛寫完帳的那幾秒兩邊本來就會差。"
                          + "判準是「靜置之後還差不差」（`op=bank_diff`）。");
            Cmd_Tavern_Helpers.WriteLastOp(args, sb.ToString());
        }

        // ==========================================================
        // 區塊職責：`op=bank_diff` —— 舊 `Treasury/` 與**新銀行**（`SCP_Bank*`）**逐戶**並排。
        // 物理意義：雙寫並存期（TASK-0235）的那個讀數：它回答「兩邊會不會同步」，
        //          而那是權威切換（TASK-0216 ⑧）唯一的前提。
        // 數值影響：**純讀兩本帳**，一毛都不動。
        // ⛔ **不印總差額** —— 兩個方向相反的錯會互相抵消，而抵消之後畫面上是一個漂亮的 0。
        //    ⇒ 一律逐戶點名，並且把「只有舊的有」「只有新的有」分成兩類（它們的成因不同：
        //      前者是鏡像還沒追上或具名跳過，後者是新銀行被人多寫了一筆）。
        // ⚠ 鏡像是**非同步**的 ⇒ 剛寫完帳的那幾秒本來就會差。判準是「靜置之後還差不差」。
        // ==========================================================
        void Op_BankDiff(Dictionary<string, string> args)
        {
            string currency = GetArg(args, "currency", "tavern_token");
            string outPath = GetArg(args, "out_path", "");

            var aOld = UCL_TreasuryLedger.GetAllBalances(currency);

            // 新銀行的根：**沿用描述表那一格的算式**（`<資料根>/Bank`），⛔ 不在這裡再拼一次字面 ——
            // 同一個路徑第二處拼字，兩邊漂掉時兩邊都讀得出一個「看起來正常」的目錄。
            string aSuffix = SCP.Core.Paths.SCP_PathRegistry.Get(SCP.Core.Paths.SCP_PathId.BankRoot).DeriveSuffix;
            string aBankRoot = System.IO.Path.Combine(UCL_RepoPath.AgentCommandsDir, aSuffix);

            // ⚠ 第二參數是**幣別**不是問題清單（我第一版把它當 oProblems 傳，編譯器擋下來了）。
            //   ⇒ 兩本帳要問同一個幣別，否則「零差額」可能只是在比兩個不同的東西。
            var aProblems = new List<string>();
            var aNew = SCP.Core.Bank.SCP_BankLedger.GetAllBalances(aBankRoot, currency);

            // 新銀行的帳號 id 一律小寫 ⇒ 舊帳本那側也要正規化才對得起來（`Zeta` vs `zeta`）。
            var aOldNorm = new Dictionary<string, int>();
            foreach (var kv in aOld)
            {
                string k = (kv.Key ?? "").Trim().ToLowerInvariant();
                if (k.Length == 0) continue;
                aOldNorm.TryGetValue(k, out int prev);
                aOldNorm[k] = prev + kv.Value;
            }

            var aIds = new List<string>();
            foreach (var k in aOldNorm.Keys) if (!aIds.Contains(k)) aIds.Add(k);
            foreach (var k in aNew.Keys) if (!aIds.Contains(k)) aIds.Add(k);
            aIds.Sort(System.StringComparer.Ordinal);

            // ⚠ **本區射程是推導出來的**（`UCL_BankMirror.ScopedAccounts`），⛔ 不是一份手維護的清單。
            //   規則：本區有 persona resolve 得到這個帳號（**含借用別區綁定** —— 那在本區是真的開戶、真的能用）＋ 央行。
            //   🩸 清單版寫過一版又拆掉：清單是當下那批 id 的快照，有人改綁定它不會跟上，
            //     而它過期時讀它的兩層會**一致地錯**（兩份答案相同 ⇒ 沒有人會發現）。
            var aScope = UCL_BankMirror.ScopedAccounts(out string aRegionId);

            int aSame = 0, aDiff = 0, aWaivedHit = 0, aUnexpected = 0, aNewOnly = 0;
            var sb = new StringBuilder();
            sb.AppendLine("| 帳號 | 舊 Treasury | 新銀行 | 判定 |");
            sb.AppendLine("|---|---:|---:|---|");
            foreach (var id in aIds)
            {
                bool hasOld = aOldNorm.TryGetValue(id, out int o);
                bool hasNew = aNew.TryGetValue(id, out int n);
                if (!hasNew && o == 0) continue;            // 兩邊都沒有錢的戶不佔版面

                // ⚠ **射程優先判**，⛔ 不是只在「新銀行沒有這一戶」時才判：
                //   一個本區不該記的帳號如果曾經被開過，它會是「存在而餘額 0」而落進「金額不同」⇒ 把閘打紅。
                if (!aScope.Contains(id))
                {
                    ++aWaivedHit;
                    sb.AppendLine($"| `{id}` | {(hasOld ? o.ToString() : "—")} | {(hasNew ? n.ToString() : "—")} "
                                  + $"| ・不在本區射程（{aRegionId} 沒有人用這個帳號；錢仍在舊帳本） |");
                    continue;
                }
                if (hasOld && hasNew && o == n) { ++aSame; continue; }

                // ⛔ 走到這裡的一定**不在**具名放棄清單裡（上面已經 continue 掉了）⇒ 以下三格全部計入閘。
                string why;
                if (!hasNew) { ++aUnexpected; why = "⚠ **未預期缺戶** —— 本區有人用它，而新銀行沒有這一戶"; }
                else if (!hasOld) { ++aNewOnly; why = "⚠ 只有新銀行有 —— 有人多寫了一筆"; }
                else { ++aDiff; why = (n - o).ToString("+#;-#;0") + "（鏡像還沒追上，或漏了一筆）"; }
                sb.AppendLine($"| `{id}` | {(hasOld ? o.ToString() : "—")} | {(hasNew ? n.ToString() : "—")} | {why} |");
            }

            string stamp = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var head = new StringBuilder();
            head.AppendLine($"## 舊 Treasury vs 新銀行 —— 逐戶對帳（{stamp}）");
            head.AppendLine($"- 新銀行根：`{aBankRoot}`");
            // ⚠ 射程的輸入是 **persona pool ✕ 該人在本區 resolve 到的帳號**（`UCL_BankMirror.ScopedAccounts`
            //   ⇒ `UCL_PersonaProfile.PoolNames()`），⛔ **不只是** `bank/<區>.md` 那幾個檔 ——
            //   pool 少一個人、或某人在本區 resolve 不到帳號，射程就會少一戶。
            // 🩸 血證（basecamp 2026-09-18）：09-17 閘內 11 戶（相符 8／不同 2／缺 1），
            //   09-18 閘內 10 戶全部相符，而 `bank/Florin.md` 那 21 個檔**自 08-20 一個字沒動**。
            //   ⇒ 戶數變了而輸入檔沒變 ⇒ 變的是 pool 那一側，**而它不會出現在任何一格**。
            // ⇒ 所以射程要**逐 id 點名**：TASK-0216 ⑧ 要的是「連續 N 天逐戶零差額」，
            //   而只印計數的話，N 天可以由 **N 批不同的受測體**湊成 —— 那種失效跟真的通過同形。
            var aScopeIds = new List<string>(aScope);
            aScopeIds.Sort(System.StringComparer.Ordinal);
            head.AppendLine($"- 本區 `{aRegionId}` 射程：**{aScope.Count} 個帳號**"
                            + "（由 **persona pool** ✕ 各人在本區 resolve 到的帳號 ＋ 央行**推導**，⛔ 不是清單）");
            head.AppendLine($"  - 射程逐 id（⛔ 跨日要比這一行，不是比戶數）：{string.Join("、", aScopeIds.ConvertAll(x => $"`{x}`"))}");
            head.AppendLine($"- **相符 {aSame} 戶**／金額不同 **{aDiff}** 戶／⚠ 未預期缺戶 **{aUnexpected}** 戶"
                            + $"／只有新的有 **{aNewOnly}** 戶／・不在本區射程 {aWaivedHit} 戶（不計入閘）");
            if (aDiff == 0 && aUnexpected == 0 && aNewOnly == 0)
                head.AppendLine("- ✅ **逐戶零差額**（⛔ 這是此刻的讀數 —— 鏡像非同步，剛寫完帳的幾秒本來就會差；"
                                + "⛔ 也**不含**射程外那批：它們本來就不該在這本帳上）");
            foreach (var p in aProblems) head.AppendLine($"- ⚠ 讀新銀行時：{p}");
            head.AppendLine();

            string body = head.ToString() + sb.ToString();
            Cmd_Tavern_Helpers.WriteLastOp(args, body);

            if (!string.IsNullOrEmpty(outPath))
            {
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(outPath, body, new UTF8Encoding(false));
                }
                catch (System.Exception e) { Debug.LogWarning($"[Treasury] bank_diff 報表寫不出來：{e.Message}"); }
            }
        }

        void Op_Balances(Dictionary<string, string> args)
        {
            string currency = GetArg(args, "currency", "tavern_token");
            string outPath = GetArg(args, "out_path", "");

            var balances = UCL_TreasuryLedger.GetAllBalances(currency);

            // 排序：讓兩次輸出可逐行對拍。⛔ 不排序的話 diff 會滿江紅而其實沒有任何數字變。
            var ids = new List<string>(balances.Keys);
            ids.Sort(System.StringComparer.Ordinal);

            int nonZero = 0;
            long total = 0;
            foreach (var id in ids) { if (balances[id] != 0) ++nonZero; total += balances[id]; }

            string stamp = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            string wrote = "";
            if (!string.IsNullOrEmpty(outPath))
            {
                // ⚠ 分隔字元與行尾都用具名常數，⛔ 不在字串裡寫字面跳脫 ——
                //   這一段 2026-09-16 被寫壞過一次：原始碼穿過 shell 那一層時 `\t` 被展開成真的 tab、
                //   `'\n'` 斷成兩行 ⇒ **它連編都編不過**。而「跳脫被吃掉」與「我打錯字」在 diff 上同形。
                const char TAB = '\t';
                const char LF = '\n';
                var tsv = new StringBuilder();
                tsv.Append("# generated_at").Append(TAB).Append(stamp).Append(LF);
                tsv.Append("# currency").Append(TAB).Append(currency).Append(LF);
                tsv.Append("# accounts").Append(TAB).Append(ids.Count).Append(LF);
                foreach (var id in ids)
                    tsv.Append(id).Append(TAB).Append(currency).Append(TAB).Append(balances[id]).Append(LF);
                try
                {
                    string dir = System.IO.Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllText(outPath, tsv.ToString(), new UTF8Encoding(false));
                    wrote = outPath;
                }
                catch (System.Exception e)
                {
                    // ⚠ 寫不出去要**出聲**：報表檔缺席與「這次沒有要報表」在呼叫端那側同形。
                    Cmd_Tavern_Helpers.FailLastOp(args, $"balances 讀到了，但報表寫不出去：{e.GetType().Name}: {e.Message}");
                    return;
                }
            }

            UCL_AgentCommandRunner.ReportOutputValue(args, "currency", currency);
            UCL_AgentCommandRunner.ReportOutputValue(args, "account_count", ids.Count.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "nonzero_count", nonZero.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "total", total.ToString());
            UCL_AgentCommandRunner.ReportOutputValue(args, "generated_at", stamp);
            UCL_AgentCommandRunner.ReportOutputValue(args, "out_path", wrote);

            var md = new StringBuilder();
            md.AppendLine("# 💰 Treasury balances（整批，唯讀）");
            md.AppendLine();
            md.AppendLine($"- currency: `{currency}`　generated_at: `{stamp}`");
            md.AppendLine($"- 帳戶 **{ids.Count}** 戶／其中有餘額的 **{nonZero}** 戶／合計 **{total}**");
            md.AppendLine(wrote.Length > 0 ? $"- 報表檔：`{wrote}`（TSV）" : "- 報表檔：**沒有要**（沒給 `out_path`）");
            md.AppendLine();
            md.AppendLine("| 帳戶 | 餘額 |");
            md.AppendLine("|---|---:|");
            foreach (var id in ids) md.AppendLine($"| `{id}` | {balances[id]} |");
            md.AppendLine();
            md.AppendLine("> ⚠ 這是**某一刻的快照**，它一印出來就開始過期。要現況請重跑。");
            Cmd_Tavern_Helpers.WriteLastOp(args, md.ToString());
            Debug.Log($"[Treasury] balances → {ids.Count} accounts / nonzero {nonZero} / total {total}");
        }

        void Op_Audit(Dictionary<string, string> args)
        {
            string account = GetArg(args, "account", "");
            string sinceTs = GetArg(args, "since_ts", "");
            if (string.IsNullOrEmpty(account)) { Cmd_Tavern_Helpers.RejectLastOp(args, "audit 缺少 account"); return; }

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
            Cmd_Tavern_Helpers.WriteLastOp(args, sb.ToString());
            Debug.Log($"[Treasury] audit {account} → {entries.Count} entries");
        }

        void Op_Verify(Dictionary<string, string> args)
        {
            string account = GetArg(args, "account", "");
            if (string.IsNullOrEmpty(account)) { Cmd_Tavern_Helpers.RejectLastOp(args, "verify 缺少 account"); return; }

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
            Cmd_Tavern_Helpers.WriteLastOp(args, head + status);
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
        // 邊界：target_bank 必須是**agent id 不是 persona 名** —— 2026-07-31 血證：
        //      commit hook 拿貼文 sender 當帳戶，summit 帶 persona 名 `summit`（bank 應為 `zeta`）
        //      → 錢進影子帳戶。這裡改為顯式宣告 + 後台人眼二次確認，不做任何推斷。
        // ===========================================================
        void Op_Request(Dictionary<string, string> args)
        {
            string targetBank = GetArg(args, "target_bank", "");
            string reason = GetArg(args, "reason", "");
            string amountRaw = GetArg(args, "amount", "");
            if (string.IsNullOrEmpty(targetBank)) { Cmd_Tavern_Helpers.RejectLastOp(args, "request 缺少 target_bank（收款 agent id，例 cc / zeta / Myth —— 不是 persona 名）"); return; }
            if (string.IsNullOrEmpty(reason)) { Cmd_Tavern_Helpers.RejectLastOp(args, "request 缺少 reason —— 審批者要有東西可判，不接受無理由請款"); return; }
            if (!int.TryParse(amountRaw, out int amount) || amount <= 0)
            { Cmd_Tavern_Helpers.RejectLastOp(args, $"request 的 amount 需為正整數（收到 '{amountRaw}'）"); return; }

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
                Cmd_Tavern_Helpers.WriteLastOp(args, sb.ToString());
            }
            catch (System.ArgumentException ex) { Cmd_Tavern_Helpers.RejectLastOp(args, $"request 參數不合法：{ex.Message}"); }
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
            if (string.IsNullOrEmpty(fromBank)) { Cmd_Tavern_Helpers.RejectLastOp(args, "transfer_request 缺少 from_bank（出款 agent id，不是 persona 名）"); return; }
            if (string.IsNullOrEmpty(toBank)) { Cmd_Tavern_Helpers.RejectLastOp(args, "transfer_request 缺少 to_bank（收款 agent id）"); return; }
            if (string.IsNullOrEmpty(reason)) { Cmd_Tavern_Helpers.RejectLastOp(args, "transfer_request 缺少 reason —— 審批者要有東西可判"); return; }
            if (!int.TryParse(amountRaw, out int amount) || amount <= 0)
            { Cmd_Tavern_Helpers.RejectLastOp(args, $"transfer_request 的 amount 需為正整數（收到 '{amountRaw}'）"); return; }

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
                Cmd_Tavern_Helpers.WriteLastOp(args, sb.ToString());
            }
            catch (System.ArgumentException ex) { Cmd_Tavern_Helpers.RejectLastOp(args, $"transfer_request 參數不合法：{ex.Message}"); }
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
            Cmd_Tavern_Helpers.WriteLastOp(args, sb.ToString());
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
            Cmd_Tavern_Helpers.WriteLastOp(args, sb.ToString());
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
            Cmd_Tavern_Helpers.WriteLastOp(args, sb.ToString());
            Debug.Log($"[Treasury] request_list: {list.Count} 筆（pendingOnly={pendingOnly}）");
        }

        void Op_RequestCancel(Dictionary<string, string> args)
        {
            string id = GetArg(args, "request_id", "");
            if (string.IsNullOrEmpty(id)) { Cmd_Tavern_Helpers.RejectLastOp(args, "request_cancel 缺少 request_id"); return; }
            try
            {
                var req = UCL_TreasuryRequestStore.Close(
                    id, UCL_TreasuryRequestStore.StatusCancelled,
                    decidedBy: GetArg(args, "agent", GetArg(args, "caller", "agent")),
                    note: GetArg(args, "note", ""));
                Cmd_Tavern_Helpers.WriteLastOp(args, $"# 🗑 請款單已撤回 — `{req.request_id}`\n\n"
                    + $"- 原請款：{req.amount} {req.currency} → `{req.target_bank}`\n- 狀態：**{req.status}**\n");
            }
            catch (System.Exception ex) { Cmd_Tavern_Helpers.RejectLastOp(args, $"request_cancel 失敗：{ex.Message}"); }
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
        // ⚠ TASK-0116：args 必須一路帶到這裡 —— 回傳檔鏡寫進哪個 persona 的 lane，
        //   唯一來源是 `args["_cmd_id"]`；不給就退回全域 static slot，而它在併發 lane 之間
        //   last-write-wins（本檔寫的是**錢**的報告，落錯 lane 的代價是別人讀到自己的餘額）。
        public static void WriteLastOp(System.Collections.Generic.IDictionary<string, string> iArgs, string md)
        {
            UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_ChatTavernRender.WriteLastOp(md, iArgs);
        }

        public static void RejectLastOp(System.Collections.Generic.IDictionary<string, string> iArgs, string msg)
        {
            string md = $"# ❌ Treasury Cmd Rejected\n\n{msg}\n";
            WriteLastOp(iArgs, md);
            throw new System.InvalidOperationException(msg);
        }

        public static void FailLastOp(System.Collections.Generic.IDictionary<string, string> iArgs, string msg)
        {
            string md = $"# ❌ Treasury Cmd Failed\n\n{msg}\n";
            WriteLastOp(iArgs, md);
            throw new System.Exception(msg);
        }
    }
}
#endif
