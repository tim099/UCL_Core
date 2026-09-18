// 區塊職責：**金流權威** —— 錢現在記在哪一本帳上，以及寫入端怎麼走。
// 物理意義：TASK-0216 第三階段（Tim 2026-09-18：「全面改用 Senate 銀行作為實際金流」）。
//          權威＝`senate_bank` 時：**讀**問新銀行（`SCP_BankLedger`，純讀、in-process 安全），
//          **寫**一律派給常駐的 Senate Server（`senate cmd bank`）。
// 數值影響：切到 `senate_bank` 之後，舊 `Treasury/ledger/` **不再長出任何新分錄**
//          —— 那正是 TASK-0216 ⑨ 的判準（「舊寫入端長不出新分錄」，⛔ 不是「我改了 N 處」）。
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
// ⚠ 三格代價，寫在這裡而不是埋在實作裡（TASK-0216 留言同一份）：
//   ① 每筆寫錢多一次 Server 往返（2026-09-18 實測 `server-ping` **1.216s**）。
//   ② Server 沒跑 ⇒ 寫錢**整筆失敗**且不降級。那是**好的失效**（大聲、看得見），
//      ⛔ 但它把「Editor 開著就能動錢」變成「Editor ＋ Server 都要開著」。
//   ③ 切過去的同時**雙寫鏡像必須停**（`UCL_BankMirror`）——
//      否則同一筆錢被記兩次，而那正是 TASK-0238 修過的那族：
//      兩邊分錄都合法、`idem_key` 也不重複、**沒有任何一層會喊**。
//
// 🩸 為什麼開關存在 `Treasury/bank_settings.json` 而不是 `EditorPrefs`：
//   EditorPrefs 是**這台機器的**。權威切換依條文要「明著宣布」（⑦），
//   而一個存在本機的權威旗標，失效的樣子正是「這台切了、那台沒切」——
//   兩套同時自稱權威，且雙方都讀得出一個合理的答案。
//   ⇒ 落在資料檔裡：一份事實、跨 process 可見、進版控、Senate 側也讀得到。
#if UNITY_EDITOR
using System;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Treasury
{
    /// <summary>錢記在哪一本帳上。</summary>
    public enum UCL_MoneyAuthority
    {
        /// <summary>舊 `Treasury/`（2026-09-18 之前的唯一權威）。</summary>
        Legacy = 0,
        /// <summary>Senate 新銀行（`Bank/`）—— 讀問它、寫派給 Server。</summary>
        SenateBank = 1,
    }

    public static class UCL_TreasuryAuthority
    {
        public const string SettingsKey = "money_authority";
        public const string ValueLegacy = "legacy";
        public const string ValueSenateBank = "senate_bank";

        /// <summary>
        /// 目前的金流權威。⛔ 預設 `Legacy` —— 認不得的值也回 `Legacy` 並**出聲**：
        /// 一個打錯字就靜默切換權威的設定，比沒有設定危險。
        /// </summary>
        public static UCL_MoneyAuthority Current
        {
            get
            {
                string v = (UCL_CentralBankSettings.MoneyAuthorityRaw ?? "").Trim().ToLowerInvariant();
                if (v.Length == 0 || v == ValueLegacy) return UCL_MoneyAuthority.Legacy;
                if (v == ValueSenateBank) return UCL_MoneyAuthority.SenateBank;
                Debug.LogError($"[TreasuryAuthority] `{SettingsKey}` 落盤值認不得（'{v}'）⇒ 本次當作 "
                               + $"`{ValueLegacy}`。合法值只有 `{ValueLegacy}` / `{ValueSenateBank}`。");
                return UCL_MoneyAuthority.Legacy;
            }
        }

        public static bool IsSenateBank => Current == UCL_MoneyAuthority.SenateBank;

        /// <summary>新銀行帳本根 —— **沿用描述表那一格的算式**，⛔ 不在這裡再拼一次字面。</summary>
        public static string BankRoot
            => System.IO.Path.Combine(
                UCL_RepoPath.AgentCommandsDir,
                SCP.Core.Paths.SCP_PathRegistry.Get(SCP.Core.Paths.SCP_PathId.BankRoot).DeriveSuffix);

        // ===========================================================
        // 區塊職責：把一筆錢派給 Senate Server 寫進新銀行。
        // 物理意義：**同步等**（呼叫端的語意就是「這筆錢到底有沒有動」），失敗 throw。
        // ⚠ 用 `ArgumentList` 不拼字串 —— 照 `UCL_BankMirror` 那條血證
        //   （引號同時扮演「綁詞」與「內容」兩個角色，而 CreateProcess 只認前者）。
        // ⚠ `idem_key` 由呼叫端給：同一筆重送不會扣第二次。⛔ 沒給就不判重（與舊行為一致）。
        // ===========================================================
        public static void Post(string iType, string iAccount, int iAmount, string iKind, string iRef,
                                string iDescription, string iCaller, string iCmdId, string iIdemKey)
        {
            string aExe = UCL_BankMirror.SenatePath;
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
                aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("bank_root=" + BankRoot);
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
                    $"[Treasury] 新銀行拒絕這一筆（exit {aExit}）：{FirstLine(aOut)} / {FirstLine(aErr)}"
                    + " —— 這筆錢**沒有動**。");
        }

        static string Fallback(string iValue, string iFallback)
            => string.IsNullOrWhiteSpace(iValue) ? iFallback : iValue;

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
