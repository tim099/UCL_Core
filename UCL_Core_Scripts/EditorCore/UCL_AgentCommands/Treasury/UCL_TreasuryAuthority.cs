// 區塊職責：**錢怎麼寫出去** —— Unity 這一側動錢的唯一出口。
// 物理意義：錢記在 Senate 新銀行（`Bank/`）。**讀**直接問 `SCP_BankLedger`（純讀、in-process 安全），
//          **寫**一律派給常駐的 Senate Server（`senate cmd bank`）。
// ⛔ 沒有「另一本帳」可以選了：`money_authority` 旗標與 `legacy` 分支已於 TASK-0242 ④ 整段移除
//   （Tim 2026-09-18：「不刪，轉唯讀就好」⇒ 舊 `Treasury/` 的**資料**當時原封不動留著）。
//   ⚖ 而 2026-09-22 Tim 改了那一格：「Treasury/ledger ＆ 相關文件一起廢棄（刪除），歷史由 git 紀錄」
//   ⇒ TASK-0274 把舊帳本與 `UCL_TreasuryHistory`／`UCL_TreasuryClosing` 整支刪除，
//     明細改問新銀行（`UCL_TreasuryLedger.Audit`）⇒ **餘額與明細回到同一本帳**。
//   📌 退路現在只在 git 歷史上，⛔ 不在工作區、也不在程式碼裡。
//
// 🔬 為什麼寫入一定要繞 Server，而讀取不必（兩個不同的理由，⛔ 別合成一句「統一走 Server」）：
//   `SCP_BankLedger` 檔頭自己寫著 —— 一筆一檔＋uuid 檔名 ⇒ credit append 天生沒有碰撞；
//   臨界區只有 debit 的「讀餘額 → 比大小 → 落檔」，而它靠的是
//   **in-process lock ＋「單一寫入端」這個前提**，原文：
//     「『單一寫入端』是前提不是保證，本層擋不住有人在 Server 之外呼叫它。」
//   ⇒ Editor 直接呼叫 `SCP_BankLedger.Debit` ＝ **安靜地多一個寫入端**（TOCTOU，兩邊都不會紅）。
//   ⇒ 所以寫入端一律派給 Server；⛔ 而 credit 也照走同一條，理由不是技術是**可稽核**：
//     兩種錢走兩條路的話，「這筆是誰寫的」就有兩個答案，而它們漂掉時兩邊都自圓其說。
//
// ⚠ 兩格代價，寫在這裡而不是埋在實作裡：
//   ① 每筆寫錢多一次 Server 往返（2026-09-18 實測 `server-ping` **1.216s**）。
//   ② Server 沒跑 ⇒ 寫錢**整筆失敗**且不降級。那是**好的失效**（大聲、看得見），
//      ⛔ 但它把「Editor 開著就能動錢」變成「Editor ＋ Server 都要開著」。
#if UNITY_EDITOR
using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Treasury
{
    public static class UCL_TreasuryAuthority
    {
        // ===========================================================
        // 區塊職責：`senate` 執行檔的位置 —— **反向對照那顆旋鈕**。
        // 物理意義：空 ＝ 走 PATH；指到一個不存在的檔 ⇒ 每一筆寫錢都**整筆失敗且不降級**
        //          （TASK-0241 ⑥ 就是這樣量的，TASK-0249 也用它把券那條路弄紅）。
        // 🩸 它本來住在雙寫鏡像那支身上（TASK-0242 ④ 已整支退場，史料在 git），
        //   而**這顆旋鈕不該跟著陪葬** —— 它量的是「派給 Server 這條路通不通」，
        //   跟「要不要鏡像」是兩件事。⇒ 搬到這裡，跟著 Post/Pay 一起住。
        // ⚠ 存在 `EditorPrefs`（本機的）：它是**量測用的旋鈕**不是權威設定，
        //   ⛔ 不要把它跟「錢記在哪本帳」那種要跨 process 可見的東西混在一起。
        // ⚠ 2026-09-18 換過一次 key（原本掛在鏡像的命名空間下）。⇒ 換 key 的代價是**這台機器上原本設過的值會回到預設**，
        //   而預設是「空 ＝ 走 PATH」——那正是正常狀態，所以這次的重設是往安全的方向壞。
        //   ⛔ 但要知道它發生了：如果你原本指著一顆特製的 senate，換版之後要再設一次。
        // ===========================================================
        const string SenatePathPrefKey = "UCL_Treasury.SenatePath";

        // ⚠ **`EditorPrefs` 是主執行緒 only** —— 而本 getter 在 `PostRaw` 的第一行被呼叫，
        //   也就是說：**每一筆寫錢都會碰它**。而寫錢的呼叫端不保證在主緒
        //   （`Cmd_StreamWatch` 的 `cycle` 走 `UCL_AgentCmdOffload.EnterBackground` ⇒ 整個 step 在 thread pool 上）。
        // 🩸 TASK-0252/0253（2026-09-18 實測，五人同場對照組）：背景緒讀它 ⇒
        //   `GetString can only be called from the main thread` ⇒ `Credit` 整筆 throw ⇒ **薪水沒發**，
        //   而 session 照樣關閉、收播公告照樣發、台帳照樣 append ⇒ 三人被吃掉。
        //   同場走 `step=start/join` 殘留補結算那兩位（仍在主緒）**發薪成功** —— 單一變因就是執行緒。
        // ⇒ 照本 repo 既有形狀修：**主緒讀一次寫進 static 快取，背景緒讀快取**
        //   （與 `UCL_RepoPath` 那族同構，而 `UCL_AgentCmdOffload.PrewarmMainThreadCaches()` 負責在切背景前暖它）。
        // ⚠ 快取未暖時背景緒回 `""`（＝走 PATH，正是預設值）—— 那是安全的方向，
        //   ⛔ 但它跟「使用者真的設成空」同形 ⇒ 所以**出一次聲**，不要靜默退化。
        static string s_SenatePathCache;
        static bool s_SenatePathWarmed;
        static bool s_WarnedColdOffMain;

        public static string SenatePath
        {
            get
            {
                bool aIsMain = UCL_AgentCmdSlowLog.MainThreadId < 0
                               || System.Threading.Thread.CurrentThread.ManagedThreadId == UCL_AgentCmdSlowLog.MainThreadId;
                if (aIsMain)
                {
                    s_SenatePathCache = EditorPrefs.GetString(SenatePathPrefKey, "");
                    s_SenatePathWarmed = true;
                    return s_SenatePathCache;
                }
                if (!s_SenatePathWarmed && !s_WarnedColdOffMain)
                {
                    s_WarnedColdOffMain = true;
                    Debug.LogWarning("[Treasury] SenatePath 在**背景緒**被讀，而快取還沒暖過"
                        + " ⇒ 這一筆用預設值（空＝走 PATH）。若這台機器指著特製的 senate，這筆會派錯執行檔。"
                        + " 修法：handler 第一行呼叫 UCL_AgentCmdOffload.EnterBackground()（它會先 prewarm 再切背景）。本警告只出一次。");
                }
                return s_SenatePathCache ?? "";
            }
            set
            {
                EditorPrefs.SetString(SenatePathPrefKey, value);
                s_SenatePathCache = value;     // ⚠ 寫入端同步更新快取 —— 否則背景緒會一直拿到舊值，
                s_SenatePathWarmed = true;     //    而「旋鈕轉了沒生效」跟「旋鈕設定本身沒用」同形。
            }
        }

        /// <summary>新銀行帳本根 —— **沿用描述表那一格的算式**，⛔ 不在這裡再拼一次字面。</summary>
        public static string BankRoot
        {
            get
            {
                // 🚚 舊 `Treasury/` → 新 `Bank/` 的自動遷移（TASK-0275）。冪等，已是新版時零成本。
                //   掛在這裡的理由：Unity 這側**所有**碰銀行的路都會先問這個根 ⇒ 它是必經路。
                //   ⚠ 而它是「取值裡的副作用」—— 接受它的判準寫在 `SCP_BankMigration` 檔頭：
                //     要求每個消費端記得先跑一支遷移指令，等於把正確性押在人記得上。
                EnsureBankMigrated();
                return System.IO.Path.Combine(
                    UCL_RepoPath.AgentCommandsDir,
                    SCP.Core.Paths.SCP_PathRegistry.Get(SCP.Core.Paths.SCP_PathId.BankRoot).DeriveSuffix);
            }
        }

        /// <summary>跑一次自動遷移並把它說的話印出來（⛔ 靜默的搬檔跟沒搬長得一樣）。</summary>
        static void EnsureBankMigrated()
        {
            SCP.Core.Bank.SCP_BankMigration.EnsureOnce(UCL_RepoPath.AgentCommandsDir);
            var aReport = SCP.Core.Bank.SCP_BankMigration.LastReport;
            if (aReport == null || aReport.Count == 0) return;
            SCP.Core.Bank.SCP_BankMigration.ClearReport();
            UnityEngine.Debug.Log("[Treasury] 銀行資料自動遷移：\n  · " + string.Join("\n  · ", aReport));
        }

        // ===========================================================
        // 區塊職責：把一筆錢派給 Senate Server 寫進新銀行。
        // 物理意義：**同步等**（呼叫端的語意就是「這筆錢到底有沒有動」），失敗 throw。
        // ⚠ 用 `ArgumentList` 不拼字串 —— 照雙寫鏡像那條血證
        //   （引號同時扮演「綁詞」與「內容」兩個角色，而 CreateProcess 只認前者）。
        // ⚠ `idem_key` 由呼叫端給：同一筆重送不會扣第二次。⛔ 沒給就不判重（與舊行為一致）。
        // ===========================================================
        /// <summary>
        /// 派一筆錢給 Server。<paramref name="oDuplicate"/> ＝ **這一筆是冪等命中**（Server 回既有那一筆，
        /// <b>這次沒有動錢</b>）。
        /// <para>🩸 為什麼一定要把這一格回傳出去（2026-09-18 實測）：呼叫端若拿「金額」去算
        /// before/after，冪等命中時它會印出一段**根本沒發生的餘額變化**（實測同一把 key 送兩次，
        /// 兩次都印 `146 → 151`，而錢只動了一次）。⇒ 那是一句每一欄都合法的假話。</para>
        /// </summary>
        public static void Post(string iType, string iAccount, int iAmount, string iKind, string iRef,
                                string iDescription, string iCaller, string iCmdId, string iIdemKey)
            => Post(iType, iAccount, iAmount, iKind, iRef, iDescription, iCaller, iCmdId, iIdemKey, out _);

        public static void Post(string iType, string iAccount, int iAmount, string iKind, string iRef,
                                string iDescription, string iCaller, string iCmdId, string iIdemKey,
                                out bool oDuplicate)
        {
            var aValues = PostRaw(iType, iAccount, iAmount, iKind, iRef, iDescription, iCaller, iCmdId, iIdemKey, null);
            oDuplicate = aValues != null && aValues.TryGetValue("duplicate", out string aDup) && aDup == "1";
        }

        /// <summary>同 <see cref="Post"/>，但回 Server 印的 `🔢` 值表（`pay` 要靠它分辨券付了幾張）。</summary>
        static System.Collections.Generic.Dictionary<string, string> PostRaw(
            string iType, string iAccount, int iAmount, string iKind, string iRef,
            string iDescription, string iCaller, string iCmdId, string iIdemKey,
            System.Collections.Generic.List<string> iExtraArgs)
        {
            string aExe = SenatePath;
            if (string.IsNullOrWhiteSpace(aExe)) aExe = "senate";

            string aOut = "", aErr = "";
            int aExit;
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
                aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("op=" + iType);
                // ⛔ 不帶 `bank_root`（也不要拿 `data_root` 頂替 —— 那是用另一個手填值繞過同一條拍板）。
                //   它是 `<資料根>/Bank` 的推導值（Tim 2026-09-17）；TASK-0260 起 Cmd 端**拒絕手填**。
                //   🩸 2026-09-22：這一行沒跟著拿掉 ⇒ 每一筆領薪 exit 2，而 `post 主流程不受影響`
                //     讓發文照樣回 `announce = Posted` ⇒ **整天 0 筆落帳而沒有任何一層喊**（TASK-0273）。
                aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("account=" + iAccount);
                aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("amount=" + iAmount);
                aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("kind=" + Fallback(iKind, "unspecified"));
                aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("ref=" + Fallback(iRef, "-"));
                aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("caller=" + Fallback(iCaller, "system"));
                if (!string.IsNullOrEmpty(iDescription))
                { aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("description=" + iDescription); }
                if (!string.IsNullOrEmpty(iCmdId))
                { aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("cmd_id=" + iCmdId); }
                if (!string.IsNullOrEmpty(iIdemKey))
                { aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("idem_key=" + iIdemKey); }
                if (iExtraArgs != null)
                    foreach (string aArg in iExtraArgs)
                    { aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add(aArg); }

                var aProc = System.Diagnostics.Process.Start(aPsi);
                if (aProc == null) throw new InvalidOperationException("Process.Start 回 null");
                aOut = aProc.StandardOutput.ReadToEnd();
                aErr = aProc.StandardError.ReadToEnd();
                aProc.WaitForExit();
                aExit = aProc.ExitCode;
            }
            catch (Exception e)
            {
                // ⚠ 這條路就是**反向對照**會走到的那一條（把 senate 指到一個不存在的檔）。
                throw new InvalidOperationException(
                    $"[Treasury] 權威＝新銀行，而派給 Senate Server 失敗（{e.GetType().Name}: {e.Message}）"
                    + $" —— 這筆錢**沒有動**。⛔ 不降級寫舊帳本。", e);
            }

            if (aExit != 0)
                throw new InvalidOperationException(
                    $"[Treasury] 新銀行拒絕這一筆（exit {aExit}）：{PickReason(aOut, aErr)}"
                    + " —— 這筆錢**沒有動**。");

            // `🔢 k = v` 收成表（`pay` 靠 paid_voucher／paid_token 分辨這筆是怎麼付的）
            var aValues = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string aRaw in (aOut ?? "").Split('\n'))
            {
                string aLine = aRaw.Trim();
                int aMark = aLine.IndexOf("🔢", StringComparison.Ordinal);
                if (aMark < 0) continue;
                aLine = aLine.Substring(aMark + "🔢".Length).Trim();
                int aEq = aLine.IndexOf('=');
                if (aEq <= 0) continue;
                aValues[aLine.Substring(0, aEq).Trim()] = aLine.Substring(aEq + 1).Trim();
            }
            return aValues;
        }

        // ===========================================================
        // 區塊職責：**一筆消費** —— 自動先扣酒館券（個人錢包），不足的才扣 token。
        // 物理意義：規則（哪些 kind 算主動消費、怎麼拆）住在 **Server 那一側**
        //          （`SCP_SpendPolicy`），本層只是把參數送過去。
        //   🩸 ⛔ **不要在 Unity 這側複製那份白名單**：兩份名單漂掉時，
        //     「這裡算消費、那裡不算」會長出第二套政策，而兩邊各自都讀得出合法答案。
        // 數值影響：最多動兩本帳（券帳與 token 帳）。失敗 throw ⇒ 呼叫端不會以為付過了。
        // ⚠ 回傳 (券付了幾張, token 付了幾個)，⛔ 不回總額 ——
        //   「3 券 ＋ 7 token」與「10 token」只差一個總數，付款方式看不出來的話，
        //   券被吃掉就不會有人知道。
        // ===========================================================
        public static (int voucher, int token) Pay(string iAccount, string iWalletPersona, int iAmount,
                                                   string iKind, string iRef, string iDescription,
                                                   string iCaller, string iCmdId, string iIdemKey)
        {
            if (string.IsNullOrWhiteSpace(iWalletPersona))
                throw new ArgumentException("[Treasury] pay 需要 wallet_persona —— ⛔ 不從帳號反查（反查錯就是花掉別人的券）");

            var aExtra = new System.Collections.Generic.List<string>
            {
                "wallet_persona=" + iWalletPersona,
                "letters_root=" + UCL_LettersPath.Root,
            };
            System.Collections.Generic.Dictionary<string, string> aValues =
                PostRaw("pay", iAccount, iAmount, iKind, iRef, iDescription, iCaller, iCmdId, iIdemKey, aExtra);

            int aVoucher = ReadInt(aValues, "paid_voucher");
            int aToken = ReadInt(aValues, "paid_token");
            Debug.Log($"[Treasury] pay {iAmount}（{iKind}）← {iAccount}／錢包 {iWalletPersona}"
                    + $" ＝ 酒館券 {aVoucher} ＋ token {aToken}");
            return (aVoucher, aToken);
        }

        // ⛔ 缺這一欄**不回 0** —— 「Server 沒印這個數字」與「真的是 0」不是同一件事，
        //   而後者會讓呼叫端把一次沒發生的扣款當成發生過。
        static int ReadInt(System.Collections.Generic.Dictionary<string, string> iValues, string iKey)
        {
            if (!iValues.TryGetValue(iKey, out string aRaw) || !int.TryParse(aRaw, out int aValue))
                throw new InvalidOperationException(
                    $"[Treasury] pay 成功而讀不到 `{iKey}` ⇒ **我不知道這筆是怎麼付的**，⛔ 不當作 0");
            return aValue;
        }

        static string Fallback(string iValue, string iFallback)
            => string.IsNullOrWhiteSpace(iValue) ? iFallback : iValue;

        // ===========================================================
        // 區塊職責：從 CLI 輸出裡挑出**真正的理由**那一行。
        // 🩸 2026-09-18 實測（書店捐贈餘額不足那次）：挑「第一行非空白」挑到的是
        //   `🔢 delegate_host = server` ⇒ 「餘額不足」被一個路由讀數蓋掉，
        //   例外訊息長得像壞在傳輸層，而它其實是一個正常的拒絕。
        //   ⚠ 這一格我當天稍早在券那側修過（`UCL_VoucherAuthority.PickReason`）
        //   並且留了一句「同族：Treasury 也會有同一個症狀」——
        //   **而那句註解沒有修好任何東西**，它只是讓我知道它壞著。⇒ 這次真的修。
        // ===========================================================
        static string PickReason(string iOut, string iErr)
        {
            string aHit = FindMarked(iOut);
            if (aHit.Length == 0) aHit = FindMarked(iErr);
            if (aHit.Length > 0) return aHit;
            return FirstLine(iOut) + " / " + FirstLine(iErr);
        }

        static string FindMarked(string iText)
        {
            if (string.IsNullOrEmpty(iText)) return "";
            foreach (var aLine in iText.Split('\n'))
            {
                string aTrim = aLine.Trim();
                if (aTrim.IndexOf('✗') >= 0) return aTrim;
            }
            return "";
        }

        static string FirstLine(string iText)
        {
            if (string.IsNullOrEmpty(iText)) return "";
            foreach (var aLine in iText.Split('\n'))
                if (!string.IsNullOrWhiteSpace(aLine)) return aLine.Trim();
            return "";
        }
    }
}
#endif
