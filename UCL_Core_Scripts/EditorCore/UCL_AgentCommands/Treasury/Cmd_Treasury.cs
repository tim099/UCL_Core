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
            "request／request_list／request_cancel／transfer_request: ⛔ **已搬到 Senate**（TASK-0327）⇒ `senate cmd bank-request --arg op=request|transfer|cancel|list`\n" +
            "verify: ⛔ **已退場**（TASK-0274）—— 新銀行分錄沒有 balance_before/after 可對；改跑 closing_list / audit\n" +
            "closing_generate: （無參數）— 補算所有「已完結但未結帳」的 UTC 日；只寫 closing/*.json，不動餘額\n" +
            "closing_list: （無參數）— 列已結帳日期與當前讀取基準\n" +
            "senate_cli: （無參數＝只看現況）[senate_path=絕對路徑|clear] — 派給 Server 用的 `senate` 執行檔；指到不存在的檔＝**寫錢那條路的反向對照**";

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
                    + "closing_generate / closing_list）");
                return;
            }

            try
            {
                switch (op)
                {
                    case "balance":  Op_Balance(args); break;
                    case "balances": Op_Balances(args); break;   // 整批唯讀（遷移／畫表用）
                    case "senate_cli": Op_SenateCli(args); break;  // 派給 Server 用的 senate 執行檔（反向對照那顆旋鈕）
                    case "credit":   Op_Credit(args); break;
                    case "debit":    Op_Debit(args); break;
                    case "transfer": Op_Transfer(args); break;   // T55 closed economy v2
                    case "audit":    Op_Audit(args); break;
                    case "verify":   Op_Verify(args); break;
                    // ⛔ 請款／轉帳單的開單、撤單、列單已搬到 Senate（TASK-0327，2026-09-28）——
                    //   審批本來就在 Senate，開單也搬過去之後，`Bank/requests`／`transfer_requests` 只剩一個寫入端。
                    case "request":
                    case "request_list":
                    case "request_cancel":
                    case "transfer_request":
                        Cmd_Tavern_Helpers.RejectLastOp(args, $"⛔ op={op} 已搬到 Senate（TASK-0327，2026-09-28）："
                            + "`senate cmd bank-request --arg op=request|transfer|cancel|list`"
                            + "（request→op=request、transfer_request→op=transfer、request_cancel→op=cancel、request_list→op=list；"
                            + "參數名相同：target_bank／from_bank／to_bank／amount／reason／source_kind／source_ref／funding／kind，另外要 `--arg persona=<你>`）。"
                            + "審批照舊 `senate cmd bank --arg op=approve`。");
                        break;
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
            // 機器可讀的回報 —— 呼叫端拿這個值（外部呼叫已改走 Senate `bank op=balance`，TASK-0333），
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
        // 區塊職責：`op=senate_cli` —— 派給 Server 用的 `senate` 執行檔（唯一的旋鈕）。
        // 物理意義：那個值住在 `EditorPrefs`，而 EditorPrefs **只有開著 Editor 的人點得到** ——
        //          一個沒有入口的開關，對 agent 來說等於不存在。
        // 數值影響：不動任何帳。改的是「用哪顆 senate 去派那筆錢」。
        // ⭐ 指到一個不存在的檔 ＝ **寫錢那條路的反向對照**：整筆失敗、⛔ 不降級。
        //   TASK-0241 ⑥ 與 TASK-0249 的紅燈都是用它弄出來的。
        // 🩸 它本來是 `op=bank_mirror` 的第二顆旋鈕（第一顆是鏡像開關）。
        //   鏡像在 TASK-0242 ④ 整支退場 —— 而**這顆旋鈕量的是另一件事**，所以留下來獨立成 op。
        // ==========================================================
        void Op_SenateCli(Dictionary<string, string> args)
        {
            string aPath = GetArg(args, "senate_path", "");

            var sb = new StringBuilder();
            sb.AppendLine("## 派給 Server 用的 `senate` 執行檔");

            if (aPath.Length > 0)
            {
                string aSet = aPath == "clear" ? "" : aPath;
                UCL_TreasuryAuthority.SenatePath = aSet;
                sb.AppendLine($"- 路徑改為：`{(aSet.Length == 0 ? "（空 ⇒ 走 PATH）" : aSet)}`");
                if (aSet.Length > 0 && !System.IO.File.Exists(aSet))
                    sb.AppendLine("  - ⚠ **那個檔不存在** —— 每一筆寫錢都會**整筆失敗且不降級**。"
                                  + "這正是反向對照要的狀態；驗完記得 `senate_path=clear`。");
            }

            sb.AppendLine($"- 現況：`{(UCL_TreasuryAuthority.SenatePath.Length == 0 ? "（走 PATH）" : UCL_TreasuryAuthority.SenatePath)}`");
            sb.AppendLine("- ⚠ 這是**本機**設定（EditorPrefs）⇒ 換一台機器不會跟著走。");
            Cmd_Tavern_Helpers.WriteLastOp(args, sb.ToString());
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

            // ⚠ 這一支問的是**歷史**（舊 `Treasury/`，凍結於 2026-09-18 權威切換）——
            //   ⛔ 它答不出切換之後的任何一筆。定語印在回傳檔裡，⛔ 不靠使用者記得。
            var entries = UCL_TreasuryLedger.Audit(account, sinceTs);
            var sb = new StringBuilder();
            sb.Append($"# 📒 Treasury audit（**歷史**）— `{account}`");
            if (!string.IsNullOrEmpty(sinceTs)) sb.Append($" (since `{sinceTs}`)");
            sb.AppendLine($"\n\n共 {entries.Count} 筆 entries\n");
            sb.AppendLine("> ⚠ **資料源是舊 `Treasury/`，它凍結於 2026-09-18 權威切換那一刻**"
                          + "（Tim 拍板：不刪、轉唯讀）。⛔ 切換之後的帳**不在這裡** —— 那些在新銀行"
                          + "（`senate cmd bank`）。⇒ 這張表為空**不代表沒有交易**，只代表那一段不在這本帳上。\n");
            foreach (var e in entries)
            {
                string flag = e.signature_mismatch ? " ⚠ sig_mismatch" : "";
                sb.AppendLine($"- [{e.ts}] `{e.type}` {e.amount} {e.currency} | {e.source_kind}({e.source_ref}) | balance: {e.balance_before}→{e.balance_after}{flag}");
            }
            Cmd_Tavern_Helpers.WriteLastOp(args, sb.ToString());
            Debug.Log($"[Treasury] audit {account} → {entries.Count} entries");
        }

        // 🔴 2026-09-22（TASK-0274）本 op 退場為**指路 stub**，而它是刻意不刪的。
        //   它驗的是舊帳本每一筆自帶的 `balance_before` / `balance_after` 跟重放對不對得上 ——
        //   **新銀行的分錄沒有那兩個欄位**（餘額由 `SCP_BankLedger` 重放算出，不存在分錄上）。
        //   🩸 舊帳本刪掉之後若讓它照跑：那兩欄一律是 0 ⇒ 除了第一筆以外**每一筆都報 DRIFT**
        //     ⇒ 一面永遠亮紅燈的儀表，比沒有儀表更糟（看的人會學會忽略它）。
        //   ⛔ 而「整支刪掉」也不對：呼叫它的人會得到「未知 op」，那答不出**為什麼**沒有了。
        //   ⇒ 退場成一句說得出理由的拒絕。真要驗新帳本，該驗的是別的東西
        //     （例如結帳鏈：`op=closing_list` 會說暖啟動用不用得上）。
        void Op_Verify(Dictionary<string, string> args)
        {
            Cmd_Tavern_Helpers.RejectLastOp(args,
                "op=verify 已退場（TASK-0274）—— 它驗的是舊帳本分錄自帶的 "
                + "`balance_before`/`balance_after` 與重放是否一致，而**新銀行的分錄沒有那兩個欄位**"
                + "（餘額是重放算出來的，不存在分錄上）⇒ 照跑會每一筆都誤報 DRIFT。"
                + "⇒ 要驗帳本健康度改跑 `op=closing_list`（它會說結帳鏈接不接得上、暖啟動用不用得上）；"
                + "要看明細跑 `op=audit`。");
        }

        // 區塊職責：op=closing_generate —— 補齊所有「已完結但尚未結帳」的 UTC 日期。
        // 物理意義：平時由酒保跨日 tick 自動跑；本 op 給人手動補算（首次上線 / 確認狀態）。
        // 數值影響：只寫 `Bank/closing/*.json`，**不動任何餘額、不動 ledger**。
        // 邊界：今天不會被結帳（今天還在寫）；已結過的日期不重複寫。
        // 🩸 2026-09-22（TASK-0274）換了受詞：本支原本結的是**凍結的舊帳本**
        //   （`Treasury/ledger`，09-18 起不再長）⇒ 它每次被叫都「沒有要結的」，
        //   而那跟「今天真的沒有新的一天要結」在畫面上是同一句話。
        void Op_ClosingGenerate(Dictionary<string, string> args)
        {
            string bankRoot = UCL_TreasuryAuthority.BankRoot;
            var problems = new List<string>();
            int n = SCP.Core.Bank.SCP_BankClosing.GenerateMissing(bankRoot, out string summary, problems);
            var sb = new StringBuilder();
            sb.AppendLine($"# 📘 每日結帳 — 新產生 {n} 份");
            sb.AppendLine();
            sb.AppendLine($"- {summary}");
            sb.AppendLine($"- 已結帳日期共 {SCP.Core.Bank.SCP_BankClosing.ClosedDayKeys(bankRoot).Count} 份");
            sb.AppendLine($"- 落檔位置：`{SCP.Core.Bank.SCP_BankClosing.ClosingDir(bankRoot)}`");
            sb.AppendLine();
            sb.AppendLine("餘額讀取 = 最近一份結帳 + 該日之後的 entry。已關帳期間不重算。");
            if (problems.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"⚠ **有 {problems.Count} 格讀不動**（⛔ 不當成「那裡沒有東西」）：");
                foreach (string w in problems) sb.AppendLine("  · " + w);
            }
            Cmd_Tavern_Helpers.WriteLastOp(args, sb.ToString());
        }

        // 區塊職責：op=closing_list —— 列出已結帳日期與**暖啟動基準**。
        void Op_ClosingList(Dictionary<string, string> args)
        {
            string bankRoot = UCL_TreasuryAuthority.BankRoot;
            var keys = SCP.Core.Bank.SCP_BankClosing.ClosedDayKeys(bankRoot);
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
                // ⚠ 「有結帳檔」與「暖啟動用得上」是兩件事 —— 鏈驗不過時後者是 null。
                //   ⛔ 兩者同形的話，一個被砍斷的鏈看起來會跟健康的一模一樣。
                var basis = SCP.Core.Bank.SCP_BankClosing.FindWarmStart(bankRoot, out string why);
                if (basis != null)
                    sb.AppendLine($"- 讀取基準：`{basis.Date}`（{basis.Balances.Count} 種幣別，"
                                  + $"該日 entry {basis.EntryCount}）");
                else
                    sb.AppendLine($"- ⚠ **暖啟動用不上**（改走全量重放）：{why}");
            }
            Cmd_Tavern_Helpers.WriteLastOp(args, sb.ToString());
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
