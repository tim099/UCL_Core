// ⛔⛔ 銀行相關操作**一律走本類的 API，不准自己解析原檔**（Tim 2026-08-20 拍板）⛔⛔
//   包含**餘額讀取**在內：`GetBalance`（單一帳戶）／`GetAllBalances`（整批，畫表用）。
//   ❌ 不准自己讀 `Treasury/ledger/**.json` 重放、不准自己 parse `accounts/_balances.snapshot.txt`、
//     不准在呼叫端另建一份餘額快取。
//   為什麼是硬規則（三條都出過事，而且都不會當場叫）：
//     ① **正確性**：餘額不是「把檔案加總」——它有關帳基準（`closing/<日>.json` warm start，
//        見 TryWarmStartFromClosing_NoLock）、增量 watermark、壞檔處理。自己重放會得到
//        一個看起來很合理、但少算或多算了一段的數字。
//     ② **效能**：本類的快取是**唯一**該存在的那份。呼叫端各自重放的代價是
//        每次列舉／解析整個 ledger（實測 14,000+ 檔）——
//        🩸 2026-08-20 銀行後台新增兩個表格區，各自對 40 個帳戶現場查餘額 ⇒ 開頁卡一分鐘、
//          IMGUI 跳 `Getting control 8's position in a group with only 8 controls`、
//          Unity 內部 PropertyEditor 連鎖 NullReferenceException。
//     ③ **一致性**：兩份餘額來源遲早會給出不同答案，而兩邊都能自圓其說、都不報錯。
//   ⇒ 判準：**`Draw*` 只准讀記憶體**；要畫一整張表就叫 `GetAllBalances()` 一次，
//     把結果存成頁面欄位，並在 LoadData／操作後顯式失效。
//
// 區塊職責：T40 Treasury Ledger Static API
// 物理意義：append-only ledger 三大 op：Credit / Debit / GetBalance；replay 算餘額
// 數值影響：所有寫操作走本 module；CMD / IMGUI 都是 thin wrapper（per Plan §3 三層架構）
// 安全：actor_signature 偵測 env_marker 防盜用；不主動 reject mismatch（log warning + audit）
// 修法 2026-05-11 (Tim QA TreasuryEnvMarker): caller-side detect thread-through CurrentCallerEnvMarker slot

// 2026-05-13 (Zeta): 去掉 #if UNITY_EDITOR guard — 純 file IO + crypto + replay 邏輯, 無 Editor 依賴.
// deps (RepoPath / TreasuryPaths) 已同步 strip guard.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Treasury
{
    public static class UCL_TreasuryLedger
    {
        // ==========================================================
        // 區塊職責：UUID6 生成 — 同 T38 PerMsgFile
        // ==========================================================
        static readonly RandomNumberGenerator s_Rng = RandomNumberGenerator.Create();
        static string GenerateUUID6()
        {
            byte[] buf = new byte[3];
            s_Rng.GetBytes(buf);
            return BitConverter.ToString(buf).Replace("-", "").ToLowerInvariant();
        }

        // ==========================================================
        // 區塊職責: Caller env_marker thread-through slot (Tim 2026-05-11 QA Bug fix TreasuryEnvMarker)
        // 物理意義: caller-side (Python run_cmd.py) 抓得到 CLAUDECODE / ANTIGRAVITY_SESSION 等 env vars,
        //          長期 Unity Editor process 抓不到 — UCL_AgentCommandRunner 開 cmd 前從 args["_caller_env_marker"]
        //          設這個 slot, DetectEnvMarker 優先讀; 沒設 → fallback in-process detect (legacy 行為)
        // 數值影響: 整體 cmd 執行生命週期讀同一個值; finally clear 避免 cross-cmd leak
        // ⚠ 2026-08-17：本 slot 降級為 **fallback**，不再是主要來源。
        //   原因：Cmd 併行（不同 persona queue 同時跑）時它是全域單例 ——
        //   B 起跑會覆蓋 A 的值，於是 **A 的 ledger entry 記成 B 的 env_marker**。
        //   那筆帳每一欄都合法、不會紅，只是來源記錯人 ⇒ audit 時查不出來。
        //   ⇒ 主要來源改為 per-cmd context（見 DetectEnvMarker(string iCmdId)）。
        //   本 slot 保留給「拿不到 cmdId」的舊路徑（IMGUI 手動操作 / 直寫 queue.json）。
        public static string CurrentCallerEnvMarker { get; set; }

        // ==========================================================
        // 區塊職責：偵測 env_marker — Claude Code / Antigravity / Gemini / unknown
        // 物理意義：optional caller-passed slot 優先 (Phase 1 修法); fallback in-process env detect (legacy)
        // 數值影響：env_marker 寫進 ledger entry，事後 audit 查
        // ==========================================================
        public static string DetectEnvMarker() => DetectEnvMarker(null);

        /// <summary>
        /// 依 cmdId 解析 env_marker —— **併行安全版**（2026-08-17）。
        /// <para>
        /// 解析順序：per-cmd context（cmdId 索引，唯一併行安全的來源）
        /// → 全域 slot（fallback，拿不到 cmdId 的舊路徑）→ in-process env detect（legacy）。
        /// </para>
        /// <para>
        /// 🩸 為什麼非做不可：全域 slot 在不同 persona queue 併行時會被後起的 cmd 覆蓋，
        /// 結果是 **A 的帳記成 B 的來源**。那筆 entry 每一欄都合法、不會報錯 ——
        /// 而 env_marker 正是事後 audit「這筆是誰寫的」的依據。
        /// ⚠ AsyncLocal 不能用：basecamp 2026-08-16 實測 SelfTestConcurrent → 併發流下全 LEAK。
        /// 唯一可行的是**顯式索引**，而 Credit / Debit 本來就收 cmdId，所以這裡零新參數。
        /// </para>
        /// </summary>
        public static string DetectEnvMarker(string iCmdId)
        {
#if UNITY_EDITOR
            // tier-0：per-cmd context（併行安全 —— 每筆 cmd 自己的值，不受他人起跑影響）
            if (!string.IsNullOrEmpty(iCmdId))
            {
                var aCtx = UCL_AgentCmdContexts.Get(iCmdId);
                if (aCtx != null && !string.IsNullOrEmpty(aCtx.CallerEnvMarker))
                    return aCtx.CallerEnvMarker;
            }

            // tier-1：全域 slot（Tim 2026-05-11 QA fix 的原路徑）——
            // 併行時可能是別人的值，所以只當拿不到 cmdId 時的 fallback。
            if (!string.IsNullOrEmpty(CurrentCallerEnvMarker))
                return CurrentCallerEnvMarker;

            // Fallback: in-process detect (legacy, 直寫 queue.json 沒走 Python 時的容錯)
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDECODE")))
                return "claude-code";
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTIGRAVITY_SESSION"))
             || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTIGRAVITY_USER_ID")))
                return "antigravity";
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
             || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_SESSION")))
                return "gemini";
#endif
            return "unknown";
        }

        // ==========================================================
        // 區塊職責：帳號歸一 —— 寫任何一筆帳之前，把呼叫端給的字串換成註冊在案的正式帳號
        // 物理意義：此前 account_id 是純字串直寫，於是 agent 名大小寫（`Zeta` vs bank `zeta`）、
        //          persona 名（`summit`）、舊命名（`zeta-bank`）各自生出一個有錢沒主人的孤兒帳戶。
        //          解析規則的唯一實作在 UCL_BankResolve（不在這裡再寫一套）。
        // 數值影響：**決定錢落進哪個帳戶**。歸一命中 → 寫正式帳號；查不到 → 原樣寫入並警告
        //          （不丟棄：丟棄會讓一筆真實勞動的薪水無聲消失，比記在錯帳戶更難查）。
        // 邊界：已銷戶帳號一律拒收 —— 銷戶的前提是餘額 0 且無人綁定，還有錢進來就是有路徑沒清乾淨，
        //      那必須當場炸出來，不能安靜補一筆讓帳戶自己復活。
        // ==========================================================
        static string ResolveAccountOrThrow(string accountId, bool resolveAccount, string opLabel)
        {
#if UNITY_EDITOR
            string resolved = accountId;
            if (resolveAccount)
            {
                var r = UCL_BankResolve.Resolve(accountId);
                if (r.Changed)
                {
                    Debug.Log($"[Treasury] 帳號歸一（{opLabel}）：{r.Trace}");
                    resolved = r.AccountId;
                }
                else if (r.IsUnresolved)
                {
                    Debug.LogWarning(
                        $"[Treasury] ⚠ 帳號 `{accountId}` 查無對應（{opLabel}）—— 本筆仍會入帳，" +
                        $"但它是**孤兒帳戶**（沒有 agent_banks / system_accounts 對應）。" +
                        $"要嘛去 registry 補登記，要嘛走銀行後台標記遷移。");
                }
            }

            if (UCL_BankResolve.IsClosed(resolved, out string closeReason))
            {
                throw new InvalidOperationException(
                    $"[Treasury] 帳號 `{resolved}` 已銷戶，拒絕 {opLabel}（銷戶理由：{closeReason}）。" +
                    $"還有金流打進已銷戶帳號 = 有呼叫路徑沒清乾淨，請查來源而不是重開帳戶。");
            }
            return resolved;
#else
            //   ⛔ player build：`resolved` 的宣告在上面那個 guard 內（Tim `2b2a4f73` 加的），
            //   而這一行原本落在 #endif 之後 ⇒ CS0103「resolved 不存在」。
            //   歸一與銷戶檢查都要 Editor 端的 registry ⇒ 非 Editor 原樣回傳，
            //   與 `resolveAccount=false` 同語意（不是靜默失敗）。
            return accountId;
#endif
        }

        // ==========================================================
        // 區塊職責：Credit — 進帳
        // 物理意義：寫一筆 credit entry；Tim grant / task_completion 等都走這
        // 數值影響：account 餘額 += amount；ledger 多一筆 entry
        // 邊界：amount 必 > 0；source_kind 必填
        // resolveAccount：預設歸一。**歸戶轉帳必須傳 false** —— 那種單子的收付方就是要指名
        //          字面上的那個孤兒帳戶，歸一會把出款方導去別處，讓孤兒的錢永遠搬不走
        //          而且轉帳看起來還成功了。
        // ==========================================================
        public static TreasuryLedgerEntry Credit(
            string accountId,
            int amount,
            string sourceKind,
            string sourceRef = null,
            string description = null,
            string callerAgentId = null,
            string cmdId = null,
            string idempotencyKey = null,
            bool resolveAccount = true)
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("accountId 必填");
            if (amount <= 0) throw new ArgumentException($"amount 必 > 0（傳入 {amount}）");
            if (string.IsNullOrEmpty(sourceKind)) throw new ArgumentException("sourceKind 必填");

            accountId = ResolveAccountOrThrow(accountId, resolveAccount, $"credit {amount} ({sourceKind})");

            return WriteEntry(TreasuryEntryType.Credit, accountId, amount, sourceKind, sourceRef, description, callerAgentId, cmdId, idempotencyKey);
        }

        // ==========================================================
        // 區塊職責：Debit — 出帳
        // 物理意義：寫一筆 debit entry；tavern_post / bartender_drink 等
        // 數值影響：account 餘額 -= amount；餘額不足 → throw（per rules.json policies.negative_balance_allowed）
        // 安全：account_id 必須等於 callerAgentId（per Plan §2.5 帳戶隔離鐵律）
        //       例外：callerAgentId 為空 / "system" 視為系統內部呼叫，跳過驗
        // ==========================================================
        // resolveAccount：預設歸一（同 Credit）。歸戶轉帳的出款方必須傳 false，
        //          否則會從歸一後的正主帳戶扣錢 —— 那不是搬帳，那是把正主的錢扣掉。
        public static TreasuryLedgerEntry Debit(
            string accountId,
            int amount,
            string useKind,
            string useRef = null,
            string description = null,
            string callerAgentId = null,
            string cmdId = null,
            string idempotencyKey = null,
            bool resolveAccount = true)
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("accountId 必填");
            if (amount <= 0) throw new ArgumentException($"amount 必 > 0（傳入 {amount}）");
            if (string.IsNullOrEmpty(useKind)) throw new ArgumentException("useKind 必填");

            accountId = ResolveAccountOrThrow(accountId, resolveAccount, $"debit {amount} ({useKind})");

            // 帳戶隔離鐵律：debit 時 caller 必須是自己 account
            // ⚠ 比對前 caller 也要歸一：accountId 已被歸一成 bank，而 callerAgentId 常是 agent 名
            //   （`Zeta` vs bank `zeta`）。只歸一一邊會讓每一筆合法扣款都被判成盜用 —— 而那個
            //   例外訊息長得像資安事件，會把人帶去查一個不存在的攻擊。
            string callerResolved = callerAgentId;
            if (resolveAccount && !string.IsNullOrEmpty(callerAgentId) && callerAgentId != "system")
                callerResolved = UCL_BankResolve.Resolve(callerAgentId).AccountId;

            if (!string.IsNullOrEmpty(callerResolved) && callerResolved != "system" && callerResolved != accountId)
            {
                throw new InvalidOperationException(
                    $"[Treasury] 不可動用對方帳戶：callerAgentId={callerAgentId}"
                    + (string.Equals(callerResolved, callerAgentId, StringComparison.Ordinal) ? "" : $"（歸一後 {callerResolved}）")
                    + $" 嘗試 debit accountId={accountId}");
            }

            // ⭐ TASK-0242 ④：**判重不在這一層了** —— 唯一的判重權威是 Server（`idem_key`）。
            // 🩸 這裡本來有一段「先掃帳本找同 key 的 entry」。權威切到新銀行之後，它掃的是
            //   一本**已經凍結**的帳 ⇒ 它永遠找不到重複，而「沒有重複」與「我看的是錯的帳本」
            //   在回傳上一模一樣（`null` 兩種意思共用一個出口）。
            //   ⇒ 拿掉它比留著準：留著的話，一個永遠回 null 的判重看起來就像判重有在跑。
            // ⚠ 代價要講明：**呼叫端必須把 `idempotencyKey` 傳下去**才有判重 ——
            //   沒傳的呼叫端從此完全沒有第二層保護（本層也不再假裝有）。

            // 餘額檢查
            int currentBalance = GetBalance(accountId);
            if (currentBalance < amount)
            {
                throw new InvalidOperationException(
                    $"[Treasury] {accountId} 餘額不足：當前 {currentBalance} < 請求 debit {amount}");
            }

            return WriteEntry(TreasuryEntryType.Debit, accountId, amount, useKind, useRef, description, callerAgentId, cmdId, idempotencyKey);
        }

        // ==========================================================
        // 區塊職責：Pay — **一筆消費**（自動先扣酒館券，不足的才扣 token）
        // 物理意義：Tim 2026-09-18 拍板。哪些 `useKind` 算主動消費、怎麼拆，
        //          規則**只住在 Server 那一側**（`SCP_SpendPolicy` 的白名單）——
        //          ⛔ 這裡不判斷，也不在 Unity 複製一份名單（兩份會漂，而兩邊都讀得出合法答案）。
        // 數值影響：最多動兩本帳（`letters/<p>/vouchers/tavern.json` 與 token 帳）。
        // ⚠ **只有權威是新銀行時才有這條路** —— 舊帳本沒有錢包的概念。
        //   ⇒ 舊權威下直接 throw，⛔ 不默默退化成純 debit：
        //     那會讓「券沒被吃」看起來像正常付款，而它其實是這一格根本沒生效。
        // ⚠ 回 (券付幾張, token 付幾個)，⛔ 不回總額。
        // ==========================================================
        public static (int voucher, int token) Pay(
            string accountId,
            string walletPersona,
            int amount,
            string useKind,
            string useRef = null,
            string description = null,
            string callerAgentId = null,
            string cmdId = null,
            string idempotencyKey = null,
            bool resolveAccount = true)
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("accountId 必填");
            if (string.IsNullOrEmpty(walletPersona)) throw new ArgumentException("walletPersona 必填 —— ⛔ 不從帳號反查（反查錯就是花掉別人的券）");
            if (amount <= 0) throw new ArgumentException($"amount 必 > 0（傳入 {amount}）");
            if (string.IsNullOrEmpty(useKind)) throw new ArgumentException("useKind 必填 —— 它同時決定署名**與這筆算不算主動消費**");

            accountId = ResolveAccountOrThrow(accountId, resolveAccount, $"pay {amount} ({useKind})");
            return UCL_TreasuryAuthority.Pay(accountId, walletPersona, amount, useKind, useRef,
                                             description, callerAgentId, cmdId, idempotencyKey);
        }

        // ==========================================================
        // 區塊職責：寫 entry 共用底層
        // 物理意義：建 TreasuryLedgerEntry + 寫 .json 檔（沿用 T38 atomic per-file）
        // 數值影響：自動填 ts / uuid / sig_*；append entry 到 ledger/<date>/
        // ==========================================================
        // ==========================================================
        // 區塊職責：權威＝新銀行時的寫入路徑 —— 派給 Senate Server，回傳一筆**等價的** entry。
        // 物理意義：呼叫端的 14 個地方全部拿 `TreasuryLedgerEntry`，所以這裡要把 Server 的結果
        //          翻回同一個型別；⛔ 而它**不落任何檔到舊 `Treasury/`**。
        // 🩸 `balance_before/after` 這兩欄新銀行**刻意不存**（`SCP_BankLedger` 檔頭②：
        //   去正規化的冗餘，併發時會說謊而沒有一層會喊）。這裡填的是**問新銀行問到的當下值**，
        //   ⚠ 它是給人讀的診斷欄，⛔ 不是權威 —— 餘額的真相永遠是重放求和。
        // ==========================================================
        static TreasuryLedgerEntry WriteEntryViaSenateBank(
            TreasuryEntryType type,
            string accountId,
            int amount,
            string sourceKind,
            string sourceRef,
            string description,
            string callerAgentId,
            string cmdId,
            string idempotencyKey)
        {
            string typeStr = type == TreasuryEntryType.Credit ? "credit" : "debit";

            // ⛔ 派出去之前不自己判餘額 —— 新銀行的 debit 臨界區才是那個判準；
            //   在這裡多判一次只會製造「我這邊算過了」的錯覺（而它讀的是另一個時刻）。
            UCL_TreasuryAuthority.Post(typeStr, accountId, amount, sourceKind, sourceRef,
                                       description, callerAgentId, cmdId, idempotencyKey,
                                       out bool aDuplicate);

            int balanceAfter = GetBalance(accountId);      // 回讀 —— 問的就是新銀行
            var entry = new TreasuryLedgerEntry
            {
                ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                uuid = GenerateUUID6(),
                type = typeStr,
                amount = amount,
                currency = "tavern_token",
                account_id = accountId,
                source_kind = sourceKind,
                source_ref = sourceRef ?? "",
                source_description = description ?? "",
                // ⭐ 冪等命中時**錢沒有動** ⇒ before ＝ after。
                //   🩸 舊版無條件用金額回推 before，於是同一把 idem_key 送兩次，兩次都印出
                //     同一段「146 → 151」的變化 —— 而第二次一毛都沒動（2026-09-18 實測）。
                //     ⛔ 那不是小數點問題：它讓「這次沒發生」跟「這次發生了」印出同一句話。
                balance_before = aDuplicate
                    ? balanceAfter
                    : (type == TreasuryEntryType.Credit ? balanceAfter - amount : balanceAfter + amount),
                balance_after = balanceAfter,
                sig_agent_id_claimed = string.IsNullOrEmpty(callerAgentId) ? accountId : callerAgentId,
                sig_process_id = "",
                sig_env_marker = DetectEnvMarker(cmdId),
                sig_cmd_id = cmdId ?? "",
                signature_mismatch = false,
                idempotency_key = idempotencyKey,
            };
            return entry;
        }

        static TreasuryLedgerEntry WriteEntry(
            TreasuryEntryType type,
            string accountId,
            int amount,
            string sourceKind,
            string sourceRef,
            string description,
            string callerAgentId,
            string cmdId,
            string idempotencyKey = null)
        {
            // ⭐ TASK-0216 ⑨（Tim 2026-09-18）：權威＝新銀行時，這一層**整個改成派給 Senate Server**，
            //   ⛔ 舊 `Treasury/ledger/` 一個新檔都不長 —— 那正是本單的判準
            //   （「**舊寫入端長不出新分錄**」，⛔ 不是「我改了 N 處」）。
            // 📌 切換點放在 WriteEntry 而不是 14 個呼叫端：Credit/Debit 都經過這裡，
            //   ⇒ 呼叫端一行不用改，而「繞過去」在結構上不存在（未來新 caller 也一樣）。
            // ⭐ TASK-0242 ④（Tim 2026-09-18「不刪，轉唯讀就好」）：舊 `Treasury/` 的**寫入實作整段移除**。
            //   這一層現在**無條件**派給 Senate Server —— ⛔ 沒有第二條路，也沒有旗標可以把它切回去。
            // 🩸 為什麼不留一個「切回舊帳本」的分支當退路：那條分支**編得過、讀得到、呼叫得到**，
            //   而它一旦被走到，錢會寫進一本已經凍結的帳，且兩邊各自都是合法數字 —— 沒有任何一層會喊。
            //   退路要留在**資料**上（舊帳本檔案原封不動、在 git 裡），⛔ 不是留在**程式碼**上。
            return WriteEntryViaSenateBank(type, accountId, amount, sourceKind, sourceRef,
                                           description, callerAgentId, cmdId, idempotencyKey);
        }


        // ==========================================================
        // 區塊職責：agent_id ↔ env_marker 對應檢查 — 判定本筆 ledger entry 該不該被標 signature_mismatch
        // 物理意義：caller env 是 Python run_cmd.py 開的 Claude / Antigravity / Gemini env；
        //          claimedAgent 是寫進 ledger 的 sig_agent_id_claimed 欄位（誰自稱發了這筆）
        // 數值影響：return true → signature_mismatch=false (信任本筆)；return false → 標 mismatch + Discord ⚠
        // 邊界：
        //   - envMarker = "unknown" → 偵測不到 env (e.g. Editor 直寫 queue.json) 不算 mismatch
        //   - agentId 為空 → 沒 claim 不算 mismatch
        //   - agentId = "system" → **合法 wildcard** (Tim 2026-05-12 QA Bug fix TreasuryWorkPostSysMismatch)
        //     work_post / token_parse 等 Op_Post hook 走 internal auto-credit, 必須傳 callerAgentId="system"
        //     以區隔 user-initiated credit. "system" 跟任何 env 都不該被視為 mismatch — 否則所有 auto-credit
        //     都會被誤標 ⚠, 真正的 false positive 反被淹沒 (250+ 筆歷史 entries 已遭此污染)
        // ==========================================================
        static bool MatchesEnvMarker(string agentId, string envMarker)
        {
            if (envMarker == "unknown") return true;   // 偵測不到不算 mismatch（避免 false positive）
            if (string.IsNullOrEmpty(agentId)) return true;
            string lowerId = agentId.ToLowerInvariant();

            // T+ QA fix: "system" 是合法 internal caller (Op_Post auto-credit hooks 用)
            //            不該被誤判 mismatch — 跟任何 env_marker 都視為相容
            if (lowerId == "system") return true;

            switch (envMarker)
            {
                case "claude-code": return lowerId.Contains("claude");
                case "antigravity": return lowerId.Contains("antigravity") || lowerId.Contains("gemini");
                case "gemini":      return lowerId.Contains("gemini");
                default:            return true;
            }
        }

        // ==========================================================
        // 區塊職責：GetBalance — 增量快取版（語意等價原版全帳本 replay）
        // 物理意義：先同步快取到磁碟現況（只 parse 新檔）, 再 O(1) 查表。
        //          餘額 = credit 總和 - debit 總和, 加法交換律 → 與 parse 順序無關,
        //          與原版「sort by ts 後逐筆累加」結果完全一致。
        // 數值影響：純讀無副作用（除快取熱化 / snapshot 回寫）。
        // ==========================================================
        // ==========================================================
        // 區塊職責：**一次取回所有帳戶餘額**（同一份快取，單次同步）。
        // 物理意義：UI 要畫一張表時需要的是「全部帳戶的餘額」，而不是對每個帳戶各問一次。
        //   `GetBalance` 單次很便宜，但它每次都會 `SyncBalanceCache_NoLock()`（列舉 ledger 路徑）——
        //   40 個帳戶 × 每個 repaint frame，就變成每秒列舉幾十萬個路徑。
        //   🩸 2026-08-20 實例：銀行後台新增兩個表格區之後，開頁卡一分鐘、
        //     IMGUI 跳 `Getting control 8's position in a group with only 8 controls`，
        //     Unity 內部 PropertyEditor 跟著 NullReferenceException。
        //   ⇒ 呼叫端不該各自建自己的快取（那是同一件事的第二份）——
        //     真相源只有這裡的 `s_BalanceCache`，本 API 就是它的批次出口。
        // 數值影響：純讀。回傳是**快照複本**，呼叫端拿去畫表不會被後續變動影響；
        //   要拿最新值就重新叫一次（同步成本只有增量部分）。
        // ==========================================================
        // ⚠ 權威＝新銀行時，這兩支**改問新銀行**（TASK-0216 ⑨）。
        //   ⛔ 不是「兩邊都讀再挑一個」—— 那會讓「我有多少錢」有兩個答案，
        //     而它們漂掉時兩邊都自圓其說（⑦ 要防的正是這個）。
        //   📌 讀取端直接 in-process 讀新銀行是安全的（純讀、無臨界區）；
        //     只有**寫入**要繞 Server，理由見 `UCL_TreasuryAuthority` 檔頭。
        public static Dictionary<string, int> GetAllBalances(string currency = "tavern_token")
            => SCP.Core.Bank.SCP_BankLedger.GetAllBalances(UCL_TreasuryAuthority.BankRoot, currency);

        public static int GetBalance(string accountId, string currency = "tavern_token")
            => SCP.Core.Bank.SCP_BankLedger.GetBalance(UCL_TreasuryAuthority.BankRoot, accountId, currency);

        // 區塊職責：某帳戶的分錄明細（畫「歷史」那一欄用）。
        // 🩸 2026-09-22（TASK-0274）從 `UCL_TreasuryHistory` 搬過來，而那支已整支刪除。
        //   它存在的理由是「現在的錢在新銀行、歷史在凍結的舊帳本，兩個時代要用兩個名字分開」——
        //   ⇒ **舊帳本刪掉之後那個理由就沒了**，而留著一個叫 History 卻讀著新帳本的類，
        //     是把一個已經消失的區分寫在名字上騙下一個人。
        //   ⛔ 而它原本的失效樣子最冷：餘額是今天的、明細停在 09-18，
        //     兩個數字加不回去而程式裡寫了一段免責解釋它 ——
        //     **一段解釋為什麼兩個數字對不起來的註解，多數時候是在描述一個該修的東西。**
        public static List<TreasuryLedgerEntry> Audit(string accountId, string sinceTs = null)
        {
            var list = new List<TreasuryLedgerEntry>();
            string bankRoot = UCL_TreasuryAuthority.BankRoot;
            if (!Directory.Exists(bankRoot)) return list;

            var problems = new List<string>();
            string wanted = SCP.Core.Bank.SCP_BankId.Normalize(accountId).Id;
            foreach (SCP.Core.Bank.SCP_BankEntry e in
                     SCP.Core.Bank.SCP_BankLedger.EnumerateEntries(bankRoot, problems))
            {
                if (!string.Equals(e.AccountId, wanted, StringComparison.Ordinal)) continue;
                if (!string.IsNullOrEmpty(sinceTs) && string.CompareOrdinal(e.AtUtc, sinceTs) <= 0) continue;
                list.Add(new TreasuryLedgerEntry
                {
                    ts = e.AtUtc,
                    type = e.Type == SCP.Core.Bank.SCP_BankEntryType.Debit ? "debit" : "credit",
                    amount = e.Amount,
                    account_id = e.AccountId,
                    source_kind = e.Kind,
                    source_ref = e.Ref,
                });
            }
            // ⚠ 讀不動的分錄要被看見 —— 少掉的那幾筆在明細上跟「沒有那幾筆」同形。
            if (problems.Count > 0)
                Debug.LogWarning($"[Treasury] 帳本有 {problems.Count} 筆讀不動（明細會少那幾筆）：\n  · "
                                 + string.Join("\n  · ", problems));
            return list;
        }

        // ==========================================================
        // 區塊職責：JSON serialize / parse — 簡易手寫（同 ChatTavern 慣例）
        // ==========================================================
        public static string SerializeEntry(TreasuryLedgerEntry e)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"ts\":\"").Append(EscapeStr(e.ts)).Append("\"");
            sb.Append(",\"uuid\":\"").Append(EscapeStr(e.uuid)).Append("\"");
            sb.Append(",\"type\":\"").Append(EscapeStr(e.type)).Append("\"");
            sb.Append(",\"amount\":").Append(e.amount);
            sb.Append(",\"currency\":\"").Append(EscapeStr(e.currency)).Append("\"");
            sb.Append(",\"account_id\":\"").Append(EscapeStr(e.account_id)).Append("\"");
            sb.Append(",\"source_kind\":\"").Append(EscapeStr(e.source_kind)).Append("\"");
            sb.Append(",\"source_ref\":\"").Append(EscapeStr(e.source_ref)).Append("\"");
            sb.Append(",\"source_description\":\"").Append(EscapeStr(e.source_description)).Append("\"");
            sb.Append(",\"balance_before\":").Append(e.balance_before);
            sb.Append(",\"balance_after\":").Append(e.balance_after);
            sb.Append(",\"sig_agent_id_claimed\":\"").Append(EscapeStr(e.sig_agent_id_claimed)).Append("\"");
            sb.Append(",\"sig_process_id\":\"").Append(EscapeStr(e.sig_process_id)).Append("\"");
            sb.Append(",\"sig_env_marker\":\"").Append(EscapeStr(e.sig_env_marker)).Append("\"");
            sb.Append(",\"sig_cmd_id\":\"").Append(EscapeStr(e.sig_cmd_id)).Append("\"");
            sb.Append(",\"signature_mismatch\":").Append(e.signature_mismatch ? "true" : "false");
            // 冪等鍵條件式 emit — 沒帶 key 的 entry 序列化結果與舊版逐字相同（backward compat）
            if (!string.IsNullOrEmpty(e.idempotency_key))
            {
                sb.Append(",\"idempotency_key\":\"").Append(EscapeStr(e.idempotency_key)).Append("\"");
            }
            sb.Append("}");
            return sb.ToString();
        }

        public static TreasuryLedgerEntry ParseEntry(string json)
        {
            // 簡易 parser — 用 regex 抽欄位（v1 prototype 可接受）
            var e = new TreasuryLedgerEntry();
            e.ts                    = ExtractStringField(json, "ts");
            e.uuid                  = ExtractStringField(json, "uuid");
            e.type                  = ExtractStringField(json, "type");
            e.amount                = ExtractIntField(json, "amount");
            e.currency              = ExtractStringField(json, "currency");
            e.account_id            = ExtractStringField(json, "account_id");
            e.source_kind           = ExtractStringField(json, "source_kind");
            e.source_ref            = ExtractStringField(json, "source_ref");
            e.source_description    = ExtractStringField(json, "source_description");
            e.balance_before        = ExtractIntField(json, "balance_before");
            e.balance_after         = ExtractIntField(json, "balance_after");
            e.sig_agent_id_claimed  = ExtractStringField(json, "sig_agent_id_claimed");
            e.sig_process_id        = ExtractStringField(json, "sig_process_id");
            e.sig_env_marker        = ExtractStringField(json, "sig_env_marker");
            e.sig_cmd_id            = ExtractStringField(json, "sig_cmd_id");
            e.signature_mismatch    = ExtractBoolField(json, "signature_mismatch");
            e.idempotency_key       = ExtractStringField(json, "idempotency_key");
            return e;
        }

        static string ExtractStringField(string json, string key)
        {
            string token = "\"" + key + "\":";
            int idx = json.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return "";
            int colon = idx + token.Length;
            // skip whitespace + opening quote
            while (colon < json.Length && (json[colon] == ' ' || json[colon] == '\t')) colon++;
            if (colon >= json.Length || json[colon] != '"') return "";
            int q1 = colon;
            int q2 = q1 + 1;
            while (q2 < json.Length && json[q2] != '"')
            {
                if (json[q2] == '\\' && q2 + 1 < json.Length) q2 += 2;
                else q2++;
            }
            if (q2 > q1) return UnescapeStr(json.Substring(q1 + 1, q2 - q1 - 1));
            return "";
        }

        static int ExtractIntField(string json, string key)
        {
            string token = "\"" + key + "\":";
            int idx = json.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return 0;
            int p = idx + token.Length;
            while (p < json.Length && (json[p] == ' ' || json[p] == '\t')) p++;
            int start = p;
            while (p < json.Length && (json[p] == '-' || (json[p] >= '0' && json[p] <= '9'))) p++;
            if (p == start) return 0;
            return int.TryParse(json.AsSpan(start, p - start), out var v) ? v : 0;
        }

        static bool ExtractBoolField(string json, string key)
        {
            string token = "\"" + key + "\":";
            int idx = json.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return false;
            int p = idx + token.Length;
            while (p < json.Length && (json[p] == ' ' || json[p] == '\t')) p++;
            return p < json.Length && json[p] == 't';
        }

        static string EscapeStr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        static string UnescapeStr(string s)
        {
            if (string.IsNullOrEmpty(s) || !s.Contains('\\')) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char nx = s[++i];
                    switch (nx)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(nx); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
