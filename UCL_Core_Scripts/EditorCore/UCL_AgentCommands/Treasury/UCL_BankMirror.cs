// 區塊職責：舊 `Treasury/` ledger → **新銀行**（`SCP_Bank*`）的鏡像（TASK-0235，雙寫並存期）。
// 物理意義：cursor ＋ 逐筆 pull，形狀**原樣照** `UCL_DiscordTreasuryMirror`（同一個 ledger、同一種 relkey 序）。
//           舊帳本仍是權威（D27）；本模組只是把每一筆錢**再說一次**給新銀行聽，
//           好讓「兩邊會不會同步」變成一個可以量的讀數 —— 那是權威切換（TASK-0216 ⑧-⑩）的前提。
//
// ⛔ **刻意不掛在 `UCL_TreasuryLedger.WriteEntry` 裡面**（Tim 2026-09-17 說「原流程額外去發 senate CLI」，
//    而我把那個「額外」放在寫帳路徑**外面**）：
//   🩸 ① 每一次 commit 領薪都會寫帳。在寫帳路徑上 spawn 一顆 75MB 的 self-contained `senate.exe`、
//      再等它去問常駐 Server，等於把 Editor 的 Cmd handler 卡在一個外部 process 上 ——
//      而發動那個 commit 的人，正在等這個 handler 回話。
//   🩸 ② 「鏡像失敗不連累舊帳本」用 cursor 是**結構保證**（舊 entry 早就落盤了，我只是還沒讀到），
//      用 try/catch 包在寫帳裡則是靠「記得包」。
//   📌 ③ 這個形狀不是我發明的：`UCL_DiscordTreasuryMirror` 的檔頭就寫著「寫入端零 spawn」，
//      而它的病史（python 版只在別人想起來時才跑 ⇒ 兩天積壓 125 筆）也已經被 native 版解掉了。
//   ⇒ 代價要講：鏡像**不是同步的**，兩邊在任一瞬間本來就會差幾秒。那是這個設計的性質，不是 bug；
//     ⛔ 所以對帳要問「靜置之後還差不差」，不是「此刻差不差」。
//
// 數值影響：只寫**新銀行**（透過 `senate cmd bank`，⇒ 寫入端仍然只有那顆 Server）。
//           ⛔ 完全不碰舊 `Treasury/`（本模組對它唯讀），也不碰餘額快取。
// 邊界：cursor 空 ⇒ **錨到最新、不回放歷史**（新銀行的開帳分錄已經含了歷史）並**大聲印出錨在哪**；
//       新銀行沒有那一戶 ⇒ **具名跳過**（記進 state 的 `skipped`，⛔ 不是靜默前進）；
//       senate 叫不起來／回非零 ⇒ **cursor 不動**、退避 30 秒、印警告（漏送可補，不會變成隱形漏）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Treasury
{
    public static class UCL_BankMirror
    {
        /// <summary>總開關。⚠ 預設 **開**：一個預設關掉的鏡像，跟沒有鏡像在畫面上同形。</summary>
        const string EnabledPrefKey = "UCL_BankMirror.Enabled";

        /// <summary>`senate` 執行檔（空 ＝ 走 PATH）。⚠ 反向對照就是把它指到一個不存在的檔。</summary>
        const string SenatePathPrefKey = "UCL_BankMirror.SenatePath";

        // ⭐ TASK-0216 ⑨（2026-09-18）：**權威切到新銀行之後，鏡像必須停** ——
        //   那時候錢是「直接寫進新銀行」，鏡像再把舊帳本的分錄推一次
        //   ⇒ 同一筆錢記兩次。⛔ 而那族的失效**沒有任何一層會喊**
        //   （兩邊分錄都合法、都有出處、`idem_key` 也不重複 —— TASK-0238 的血證）。
        // 📌 做成**推導**而不是「記得去關」：權威是旗標，關不關由它決定，
        //   ⇒ 「切了權威但忘記關鏡像」這個狀態在結構上不存在。
        //   ⚠ 也因此切回 `legacy` 時鏡像會自己回來，不必再記得開一次。
        public static bool Enabled
        {
            get { return !UCL_TreasuryAuthority.IsSenateBank && EditorPrefs.GetBool(EnabledPrefKey, true); }
            set { EditorPrefs.SetBool(EnabledPrefKey, value); }
        }

        public static string SenatePath
        {
            get { return EditorPrefs.GetString(SenatePathPrefKey, ""); }
            set { EditorPrefs.SetString(SenatePathPrefKey, value); }
        }

        // ===========================================================
        // 區塊職責：cursor state（`PromptQueue/_bank_mirror_state.json`）
        // 物理意義：`last_seen` ＝ 已經鏡像過的最大 relkey（`<date>/<檔名>`，ordinal 天然有序）。
        // ⚠ **刻意跟 Discord 那支分檔**：共用一個 cursor 會讓「Discord 送到哪」與「銀行鏡到哪」
        //   互相推進對方 —— 而它們失敗的理由完全不同。
        // ===========================================================
        static string StatePath
        {
            get { return Path.Combine(UCL_RepoPath.AgentCommandsDir, "PromptQueue", "_bank_mirror_state.json"); }
        }

        /// <summary>給 `Cmd_Treasury op=bank_mirror` 印出來用的 —— 看不到游標住哪就查不了它停在哪。</summary>
        public static string StatePathPublic { get { return StatePath; } }

        static string LedgerRoot
        {
            get { return Path.Combine(UCL_RepoPath.AgentCommandsDir, "Treasury", "ledger"); }
        }

        // ===========================================================
        // 區塊職責：**本區射程** —— 這一區的新銀行該不該記這個帳號。
        // 物理意義：判準是**推導**出來的，⛔ 不是一份手維護的清單
        //          （Tim 2026-09-17：「請不要在 code 中硬擋，按照正常規則讓沒綁 persona 的帳戶不跑遷移」）。
        //   規則：某個 persona 在**本區**（`UCL_CentralBankSettings.CurrencyId`）resolve 得到這個帳號 ⇒ 在射程內。
        //   ⭐ **借用別區綁定也算**（Tim 2026-09-17 更正）：借用的意思是「依他在別區的帳號 id，
        //     在本區真的開戶並綁定」⇒ 那是一個**在本區可以正常運作的帳戶**，不是別區的東西。
        //     🩸 我原本把它讀成「不屬於本區」，於是把 @kaguya 在用的 `Luna` 關掉了。
        //   ＋ 央行（沒有人綁它，但它是本區的機構帳）。
        // 🩸 為什麼不用清單：清單是**當下那批 id 的快照**。多一個人綁帳號、有人改綁定、有人搬區，
        //    清單不會自己跟上 —— 而它過期的時候，讀它的兩層會**一致地錯**（兩份答案相同，所以沒有人會發現）。
        // 數值影響：純讀綁定檔（`letters/<persona>/bank/<區>.md`）與設定，⛔ 不碰帳。
        // ===========================================================
        public static HashSet<string> ScopedAccounts(out string oRegion)
        {
            oRegion = UCL_CentralBankSettings.CurrencyId;
            var aOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string aName in UCL_PersonaProfile.PoolNames())
                {
                    string aAcc = UCL_PersonaProfile.GetBankAccount(aName, oRegion, out string _, out string _);
                    if (!string.IsNullOrWhiteSpace(aAcc)) aOut.Add(aAcc.Trim());
                }
                string aCentral = UCL_CentralBankSettings.CentralBankAccount;
                if (!string.IsNullOrWhiteSpace(aCentral)) aOut.Add(aCentral.Trim());
            }
            catch (Exception e) { Debug.LogWarning($"[BankMirror] 算本區射程時出事：{e.Message}"); }
            return aOut;
        }

        // ===========================================================
        // 區塊職責：**開帳水位線** —— 每一戶的新銀行帳是從哪一刻的舊餘額起算的。
        // 物理意義：遷移寫的開帳分錄自己帶著出處（`ref = "treasury-balances@<ISO 時刻>"`），
        //          那個時刻之前的舊分錄**已經含在 opening 金額裡** ⇒ 再鏡一次就是把同一塊錢算兩次。
        // 數值影響：純唯讀新銀行帳本；擋下的那幾筆會被具名跳過（⛔ 不靜默前進）。
        //
        // 🩸 血證（basecamp 2026-09-17，Bar/BTC 實測 2 筆）：
        //   本檔原有的守衛是「cursor 空 ⇒ 錨到最新、不回放歷史」——
        //   而它只保護**〔先遷移 → 後開鏡像〕**那個順序。
        //   Bar 這次是反過來的：鏡像先在跑（cursor 停在 10:22:5x），遷移才在 10:25:10 取快照
        //   ⇒ 區間 `(cursor, 快照]` 的兩筆（`ba16c3` / `01af1c`）被鏡像又推了一次，
        //     `claude-code` 4317→4318、`zeta` 3040→3041。
        //   ⛔ 而**沒有任何一層會喊**：兩邊的分錄都合法、都有出處，`idem_key` 也不重複
        //     （它們本來就是不同的兩筆）。今天只有 2 筆是運氣 ——
        //     區間大小等於「遷移那一刻鏡像落後多少」。
        // ⇒ 所以判準改成問**帳本自己**（每一戶的 opening 從哪一刻起算），
        //   而不是問 cursor 在哪 —— cursor 是一個會被順序影響的狀態，水位線不是。
        // ===========================================================
        const string c_OpeningKind = "opening_balance";
        const string c_OpeningRefPrefix = "treasury-balances@";

        static Dictionary<string, string> s_OpeningWatermarks = null;
        static double s_WatermarkRebuiltAt = -999;

        /// <summary>丟掉快取 —— 補搬一戶之後要重取，否則新開的那一戶在本輪沒有水位線。</summary>
        public static void InvalidateOpeningWatermarks() { s_OpeningWatermarks = null; }

        /// <summary>
        /// 這一戶的開帳水位線；查不到回 null。
        /// 🩸 **查不到要重建一次快取再回答**（節流 5 秒）—— 快取是一次性建的，
        ///   而補搬一戶（例如央行）之後它**不會自己更新** ⇒ 新開那一戶在快取裡永遠缺席，
        ///   於是守衛對它整個失效，而失效的樣子跟「這一戶沒有開帳」一模一樣。
        ///   ⛔ 這個洞是我裝守衛時自己留的，不是原本就有的。
        /// </summary>
        static string TryGetWatermark(string iAccountId)
        {
            var aMap = OpeningWatermarks();
            if (aMap.TryGetValue(iAccountId, out string aTs)) return aTs;
            if (EditorApplication.timeSinceStartup - s_WatermarkRebuiltAt < 5.0) return null;
            InvalidateOpeningWatermarks();
            aMap = OpeningWatermarks();
            return aMap.TryGetValue(iAccountId, out string aTs2) ? aTs2 : null;
        }

        static Dictionary<string, string> OpeningWatermarks()
        {
            if (s_OpeningWatermarks != null) return s_OpeningWatermarks;
            s_WatermarkRebuiltAt = EditorApplication.timeSinceStartup;
            var aOut = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string aSuffix = SCP.Core.Paths.SCP_PathRegistry
                    .Get(SCP.Core.Paths.SCP_PathId.BankRoot).DeriveSuffix;
                string aBankRoot = Path.Combine(UCL_RepoPath.AgentCommandsDir, aSuffix);
                foreach (var aEntry in SCP.Core.Bank.SCP_BankLedger.EnumerateEntries(aBankRoot))
                {
                    if (aEntry == null) continue;
                    if (!string.Equals(aEntry.Kind, c_OpeningKind, StringComparison.Ordinal)) continue;
                    string aRef = aEntry.Ref ?? "";
                    if (!aRef.StartsWith(c_OpeningRefPrefix, StringComparison.Ordinal)) continue;
                    string aTs = aRef.Substring(c_OpeningRefPrefix.Length).Trim();
                    if (aTs.Length == 0) continue;
                    // 同一戶有兩筆開帳（重跑／補搬）⇒ 取**最晚**的那一刻，⛔ 不是第一筆
                    if (!aOut.TryGetValue(aEntry.AccountId, out string aPrev)
                        || string.CompareOrdinal(aTs, aPrev) > 0)
                        aOut[aEntry.AccountId] = aTs;
                }
            }
            catch (Exception e)
            {
                // ⛔ fail-soft **不是**回空表就算了：空表的意思是「每一戶都沒有水位線」⇒ 守衛整個失效，
                //   而那跟「真的沒有開帳」長得一模一樣。⇒ 出聲，並讓下一輪重試（不寫進快取）。
                Debug.LogWarning($"[BankMirror] ⚠ 讀不到開帳水位線：{e.Message}"
                                 + " —— **本輪不套用重複防護**，下一輪重試。");
                return aOut;
            }
            s_OpeningWatermarks = aOut;
            return aOut;
        }

        static UCL.Core.JsonLib.JsonData LoadState()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    var aJd = UCL.Core.JsonLib.JsonData.ParseJson(File.ReadAllText(StatePath));
                    if (aJd != null && aJd.IsObject) return aJd;
                }
            }
            catch (Exception e) { Debug.LogWarning($"[BankMirror] state load fail: {e.Message}"); }
            return UCL.Core.JsonLib.JsonData.ParseJson("{}");
        }

        static void SaveState(UCL.Core.JsonLib.JsonData iState)
        {
            try
            {
                string aTmp = StatePath + ".tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllText(aTmp, iState.ToJsonBeautify(), new UTF8Encoding(false));
                if (File.Exists(StatePath)) File.Delete(StatePath);
                File.Move(aTmp, StatePath);
            }
            catch (Exception e) { Debug.LogWarning($"[BankMirror] state save fail: {e.Message}"); }
        }

        static string LoadCursor() { return LoadState().GetString("last_seen", ""); }

        static void SaveCursor(string iRelKey)
        {
            var aState = LoadState();
            aState["last_seen"] = iRelKey;
            SaveState(aState);
        }

        /// <summary>具名跳過 —— 記進 state 並印出來。⛔ 靜默前進會讓「鏡不了」長得像「鏡過了」。</summary>
        static void RecordSkip(string iRelKey, string iAccount, string iWhy)
        {
            var aState = LoadState();
            if (!aState.Contains("skipped")) aState["skipped"] = UCL.Core.JsonLib.JsonData.ParseJson("[]");
            var aRow = UCL.Core.JsonLib.JsonData.ParseJson("{}");
            aRow["relkey"] = iRelKey;
            aRow["account"] = iAccount;
            aRow["why"] = iWhy;
            aState["skipped"].Add(aRow);
            SaveState(aState);
            Debug.LogWarning($"[BankMirror] ⛔ 具名跳過 {iRelKey}（account={iAccount}）：{iWhy}"
                             + " —— 這一筆**新銀行沒有收到**。");
        }

        // ===========================================================
        // 區塊職責：cursor 之後的 relkey 升冪清單（date dir 名 < cursor date 的整夾 cheap prune）
        // ⚠ 與 `UCL_DiscordTreasuryMirror.CollectPending` 同形 —— 同一個 ledger、同一種排序。
        // ===========================================================
        static List<string> CollectPending(string iCursor)
        {
            var aPending = new List<string>();
            if (!Directory.Exists(LedgerRoot)) return aPending;
            string aCursorDate = string.IsNullOrEmpty(iCursor) ? "" : iCursor.Split('/')[0];
            var aDateDirs = Directory.GetDirectories(LedgerRoot);
            Array.Sort(aDateDirs, StringComparer.Ordinal);
            foreach (var aDir in aDateDirs)
            {
                string aName = Path.GetFileName(aDir);
                if (!string.IsNullOrEmpty(aCursorDate) && string.CompareOrdinal(aName, aCursorDate) < 0) continue;
                var aFiles = Directory.GetFiles(aDir, "*.json");
                Array.Sort(aFiles, StringComparer.Ordinal);
                foreach (var aFile in aFiles)
                {
                    string aRelKey = aName + "/" + Path.GetFileName(aFile);
                    if (string.CompareOrdinal(aRelKey, iCursor) > 0) aPending.Add(aRelKey);
                }
            }
            return aPending;
        }

        // ===========================================================
        // 區塊職責：單筆在途狀態機 —— 同一時刻最多一筆 entry 在鏡像
        // 物理意義：senate 在**背景執行緒**上跑（Editor 主迴圈一格都不等）；tick 只負責收割。
        // 數值影響：順序 ＝ 帳目時序；天然限速（一次一顆 process）。
        // ===========================================================
        static string s_InFlightRelKey = null;
        static System.Threading.Tasks.Task<RunResult> s_InFlight = null;
        static double s_BackoffUntil = -999;
        static bool s_BaselineLogged = false;

        struct RunResult
        {
            public int ExitCode;
            public string FirstLine;
            public string Error;
        }

        /// <summary>由 Editor 的 mirror daemon 每秒呼叫一次。⚠ 本支**自己**判 Enabled（⛔ 不跟 Discord 共用開關）。</summary>
        public static void Tick()
        {
            if (!Enabled) return;

            // ① 先收割在途的那一筆（⛔ 永遠先收割：不收割就永遠不會有下一筆）
            if (s_InFlight != null)
            {
                if (!s_InFlight.IsCompleted) return;
                var aTask = s_InFlight;
                string aRelKey = s_InFlightRelKey;
                s_InFlight = null;
                s_InFlightRelKey = null;

                RunResult aRes;
                try { aRes = aTask.Result; }
                catch (Exception e) { aRes = new RunResult { ExitCode = -1, Error = e.Message }; }

                if (aRes.ExitCode == 0)
                {
                    SaveCursor(aRelKey);
                }
                else if (IsNoSuchAccount(aRes))
                {
                    // 新銀行沒有這一戶 ⇒ 前進（⛔ 不卡住整條鏡像：卡住的話後面每一筆真的帳都跟著停），
                    // 但**分兩種說法**：在具名放棄清單裡＝預期；不在＝⚠ 非預期，那是要有人來看的。
                    string aAcc = AccountOf(aRelKey);
                    bool aExpected = !ScopedAccounts(out string _).Contains(aAcc);
                    RecordSkip(aRelKey, aAcc, aExpected
                        ? "新銀行沒有這一戶，而**本區也沒有人用這個帳號**（規則推導 ⇒ 預期）"
                        : "⚠ 新銀行沒有這一戶，而**本區有人用它** —— 非預期，要有人來看");
                    SaveCursor(aRelKey);
                }
                else
                {
                    // ⚠ cursor **不動** ⇒ 下一輪重試同一筆。漏送可補，而且看得見。
                    s_BackoffUntil = EditorApplication.timeSinceStartup + 30.0;
                    Debug.LogWarning($"[BankMirror] {aRelKey} 鏡像失敗（exit {aRes.ExitCode}）"
                                     + $"：{aRes.Error ?? aRes.FirstLine} —— **cursor 保留**，30 秒後重試。"
                                     + " ⛔ 舊帳本那一筆早就落盤了，本失敗不影響它。");
                }
                return;
            }

            if (EditorApplication.timeSinceStartup < s_BackoffUntil) return;

            // ② 挑下一筆
            string aCursor = LoadCursor();
            var aPendingList = CollectPending(aCursor);
            if (aPendingList.Count == 0) return;

            if (string.IsNullOrEmpty(aCursor))
            {
                // ⚠ baseline：新銀行的開帳分錄**已經含了歷史**（TASK-0216 第一階段）
                //   ⇒ 回放歷史 ＝ 把每一筆錢算兩次。所以第一次只錨不送。
                //   ⛔ 而它必須**大聲說錨在哪**：靜默錨定的症狀是「鏡像跑著，而那一段永遠沒有人送」。
                string aAnchor = aPendingList[aPendingList.Count - 1];
                SaveCursor(aAnchor);
                Debug.LogWarning($"[BankMirror] 首次啟動 ⇒ **錨到最新、不回放歷史**：{aAnchor}"
                                 + $"（跳過 {aPendingList.Count} 筆）。"
                                 + " ⚠ 新銀行的開帳分錄已經含了那一段；**開帳讀數之後、本錨點之前**的那幾筆"
                                 + "兩邊都不會有人補 ⇒ 它們會在對帳時現形，那是刻意的。");
                return;
            }

            string aNext = aPendingList[0];
            var aEntry = TryLoadEntry(aNext);
            if (aEntry == null)
            {
                // 壞檔／讀不動：跳過並前進（⛔ 不卡死），但**記名**。
                RecordSkip(aNext, "?", "entry 檔讀不動或格式不對");
                SaveCursor(aNext);
                return;
            }
            if (aEntry.IsAudit)
            {
                SaveCursor(aNext);   // `__audit` 不是錢的移動 —— 靜默前進（同 Discord 那支）
                return;
            }

            // ⚠ **送出之前**先問射程 —— ⛔ 不是等 `cmd bank` 回「沒有這一戶」才處理。
            // 🩸 那個事後判別擋不住既有帳戶：帳本 append-only ⇒ 一個「本區不該記」的帳號如果曾經被開過，
            //   它會是「存在而餘額 0」，於是「沒有這一戶」永遠不會回 ⇒ 鏡像把它再養起來。
            // ⭐ 判準是**規則不是清單**：本區有沒有人 resolve 得到這個帳號（含借用）。
            var aScope = ScopedAccounts(out string aRegion);
            if (!aScope.Contains(aEntry.AccountId))
            {
                RecordSkip(aNext, aEntry.AccountId,
                           $"本區（{aRegion}）沒有任何 persona 用這個帳號 ⇒ **不鏡它**"
                           + "（錢仍在舊帳本；哪天有人綁了它，這個判斷自己就會變）");
                SaveCursor(aNext);
                return;
            }

            // ⚠ **開帳水位線**：比這一戶的 opening 快照還舊的分錄，已經含在 opening 金額裡 ⇒ 不再鏡。
            //   ⛔ 判準是 `<=` 不是 `<`：快照是「那一刻的餘額」，**含**那一刻已落盤的分錄。
            string aWatermark = TryGetWatermark(aEntry.AccountId);
            if (aWatermark != null
                && aEntry.Ts.Length > 0
                && string.CompareOrdinal(aEntry.Ts, aWatermark) <= 0)
            {
                RecordSkip(aNext, aEntry.AccountId,
                           $"早於開帳快照（entry ts={aEntry.Ts} <= opening@{aWatermark}）"
                           + " ⇒ **這一筆已經含在 opening 金額裡**，再鏡一次就是算兩次");
                SaveCursor(aNext);
                return;
            }

            s_InFlightRelKey = aNext;
            // ⚠ `EditorPrefs` **只能在主執行緒讀** —— 所以路徑在這裡（tick ＝ 主執行緒）先取出來，
            //   再連同 entry 一起交給背景 task。
            // 🩸 2026-09-17 實測：第一版在 `RunSenate` 裡讀 `SenatePath` ⇒ 每一筆都回
            //   `GetString can only be called from the main thread`、exit -1、cursor 卡在原地重試。
            //   ⇒ 它**沒有**弄壞任何一筆帳（舊帳本早就落盤、新銀行一筆都沒動），
            //     而我是靠自己寫的那行警告看到的，不是靠想到。
            string aExe = SenatePath;
            if (string.IsNullOrWhiteSpace(aExe)) aExe = "senate";
            s_InFlight = System.Threading.Tasks.Task.Run(() => RunSenate(aEntry, aExe));

            if (!s_BaselineLogged)
            {
                Debug.Log($"[BankMirror] 開始鏡像（cursor={aCursor}，待鏡 {aPendingList.Count} 筆）");
                s_BaselineLogged = true;
            }
        }

        // ===========================================================
        // 區塊職責：把一筆舊 entry 翻成 `senate cmd bank` 的參數並實跑
        // ⚠ 用 `ArgumentList` 而不是拼 `Arguments` 字串 —— 照 `UCL_TaskWorkMemoryCli` 那條血證
        //   （引號同時扮演「綁詞」與「內容」兩個角色，而 CreateProcess 只認前者）。
        // ⚠ `idem_key` ＝ 舊分錄的 uuid ⇒ **同一筆重跑不會多一筆**（重試、domain reload、手動補跑都安全）。
        // ===========================================================
        static RunResult RunSenate(Entry iEntry, string iExe)
        {
            string aExe = iExe;
            try
            {
                var aPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = aExe,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                aPsi.ArgumentList.Add("cmd");
                aPsi.ArgumentList.Add("bank");
                aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("op=" + iEntry.Type);
                aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("account=" + iEntry.AccountId);
                aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("amount=" + iEntry.Amount);
                aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("kind=" + iEntry.Kind);
                aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("ref=" + iEntry.Ref);
                aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("caller=" + iEntry.Caller);
                aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("description=" + iEntry.Description);
                aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("idem_key=treasury/" + iEntry.Uuid);

                var aProc = System.Diagnostics.Process.Start(aPsi);
                if (aProc == null) return new RunResult { ExitCode = -1, Error = "Process.Start 回 null" };
                string aOut = aProc.StandardOutput.ReadToEnd();
                string aErr = aProc.StandardError.ReadToEnd();
                aProc.WaitForExit();
                string aFirst = FirstNonEmptyLine(aOut);
                return new RunResult
                {
                    ExitCode = aProc.ExitCode,
                    FirstLine = aFirst,
                    Error = aProc.ExitCode == 0 ? null : (aFirst + " / " + FirstNonEmptyLine(aErr)),
                };
            }
            catch (Exception e)
            {
                // ⚠ 這條路就是**反向對照**會走到的那一條（把 senate 指到不存在的檔）。
                return new RunResult { ExitCode = -1, Error = e.GetType().Name + ": " + e.Message };
            }
        }

        static bool IsNoSuchAccount(RunResult iRes)
        {
            string aAll = (iRes.FirstLine ?? "") + " " + (iRes.Error ?? "");
            return aAll.Contains("沒有這一戶") || aAll.Contains("沒開戶") || aAll.Contains("not found");
        }

        static string AccountOf(string iRelKey)
        {
            var aEntry = TryLoadEntry(iRelKey);
            return aEntry == null ? "?" : aEntry.AccountId;
        }

        // ===========================================================
        // 區塊職責：舊 entry 的最小讀取（⛔ 只讀本模組真的會用到的欄位）
        // ⚠ 三個必填欄（kind／ref／caller）空的時候**要有替代值**：`cmd bank` 會擋下空的，
        //   而被擋下的那一筆會變成「永遠重試」——一筆卡住，後面全停。
        // ===========================================================
        class Entry
        {
            public string Type = "credit";
            public string AccountId = "";
            public int Amount;
            public string Kind = "";
            public string Ref = "";
            public string Caller = "";
            public string Description = "";
            public string Uuid = "";
            /// <summary>舊分錄的 `ts`（ISO8601 Z）。⚠ 開帳水位線靠它比，⛔ 別拿檔名的時分秒代替。</summary>
            public string Ts = "";
            public bool IsAudit;
        }

        static Entry TryLoadEntry(string iRelKey)
        {
            try
            {
                string aPath = Path.Combine(LedgerRoot, iRelKey.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(aPath)) return null;
                if (iRelKey.Contains("__audit")) return new Entry { IsAudit = true };

                var aJd = UCL.Core.JsonLib.JsonData.ParseJson(File.ReadAllText(aPath));
                if (aJd == null || !aJd.IsObject) return null;

                string aType = aJd.GetString("type", "");
                if (aType != "credit" && aType != "debit") return null;

                string aUuid = aJd.GetString("uuid", "");
                var aEntry = new Entry
                {
                    Type = aType,
                    AccountId = aJd.GetString("account_id", ""),
                    Amount = aJd.GetInt("amount", 0),
                    Uuid = string.IsNullOrEmpty(aUuid) ? iRelKey : aUuid,
                    Ts = aJd.GetString("ts", ""),
                };
                if (aEntry.AccountId.Length == 0 || aEntry.Amount <= 0) return null;

                aEntry.Kind = Fallback(aJd.GetString("source_kind", ""), "treasury_mirror");
                aEntry.Ref = Fallback(aJd.GetString("source_ref", ""), "treasury-entry@" + iRelKey);
                aEntry.Caller = Fallback(aJd.GetString("sig_agent_id_claimed", ""), "bank-mirror");
                aEntry.Description = Fallback(aJd.GetString("source_description", ""),
                                              "舊 Treasury 鏡像（TASK-0235）");
                return aEntry;
            }
            catch { return null; }
        }

        static string Fallback(string iValue, string iDefault)
        {
            return string.IsNullOrWhiteSpace(iValue) ? iDefault : iValue;
        }

        static string FirstNonEmptyLine(string iText)
        {
            if (string.IsNullOrEmpty(iText)) return "";
            foreach (var aLine in iText.Split('\n'))
                if (aLine.Trim().Length > 0) return aLine.Trim();
            return "";
        }
    }
}
#endif
