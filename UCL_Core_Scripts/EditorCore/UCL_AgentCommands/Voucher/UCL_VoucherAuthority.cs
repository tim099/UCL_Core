// 區塊職責：**券的權威層** —— Unity 這側所有券的讀與寫，一律派給 Senate Server（`senate cmd voucher`）。
// 物理意義：券＝**以 id 區分的貨幣**（Tim 2026-09-18 拍板）。一種券一個 id，
//           落在 `letters/<persona>/vouchers/<券 id>.json`，由銀行 Server 管。
//           ⇒ 本層**不認識任何一種券** —— `canvas` 只是第一個 id，新增券種不必改這裡。
// 數值影響：所有數字都來自 Server 的 `🔢` 回傳，本層**一個字都不自己算**。
//
// 🩸 為什麼是「全部」而不是「只有寫入」（2026-09-18 的血證，就是這一天量到的）：
//   券的**消費端**先搬到新系統、而**發放端**留在 Unity 舊帳本 ⇒ 每場自由時間發的 10 張限時券
//   花不到、到期原地作廢，而舊帳面上它看起來還在。
//   實測四個人分岔，且每一位的差額**正好等於那批死掉的限時券**
//   （Sirius 113/103、apex-one 99/89、basecamp 67/57、calli 9/0）。
//   ⛔ 沒有任何一層會喊 —— 兩邊都是合法數字。
//   ⇒ 所以這一層的價值不在「方便」，在**讓「只切一半」變成結構上做不到的事**：
//     只要 Unity 這側沒有第二個寫檔的地方，就不可能再分岔一次。
//
// 🩸 判準：
//   ① **讀失敗 throw，⛔ 不回 0。** 「我讀不到」與「他真的沒券」在回傳值上同形，
//      而後者會讓呼叫端安靜地拒絕一筆合法的付款（或放行一筆不該過的）。
//   ② **寫失敗 throw，⛔ 不降級寫舊檔。** 降級留下的是「券發了」的畫面與一本沒動的帳。
//   ③ **`region` 必帶** —— 券不記歷史，`updated_region` 是唯一的「誰動過它」線索。
//   ④ **`before` 是另一次讀取，不在同一個交易裡** ⇒ 它只配拿來印 log，
//      ⛔ 不准拿來當「扣款前餘額」做任何判斷（那是 Server 那一側的事）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UCL.Core.EditorLib.AgentCommands.Treasury;

namespace UCL.Core.EditorLib.AgentCommands.Voucher
{
    /// <summary>券的唯一入口（Unity 側）—— 一律委派 Senate Server，本層不碰檔案。</summary>
    public static class UCL_VoucherAuthority
    {
        /// <summary>繪圖券的券 id。⚠ 這只是**一個** id，⛔ 不代表本層只認得它。</summary>
        public const string VoucherCanvas = "canvas";

        /// <summary>券檔的根（`letters/`）—— 委派唯一擁有者，⛔ 本層不自己拼路徑。</summary>
        public static string LettersRoot => UCL_LettersPath.Root;

        /// <summary>現地區域 ＝ 貨幣 id（與銀行同一格設定，⛔ 不另立一份）。</summary>
        public static string Region => UCL_CentralBankSettings.CurrencyId;

        /// <summary>一種券的三個餘額。⚠ 三個問題三個數字 —— ⛔ 沒有一個叫「餘額」的單數。</summary>
        public struct Reading
        {
            /// <summary>永久券。</summary>
            public int Permanent;
            /// <summary>**未過期**的限時券。</summary>
            public int Expiring;
            /// <summary>可花總額 ＝ 兩者之和。規劃付款用這個。</summary>
            public int Spendable;
        }

        // ===========================================================
        // 區塊：讀
        // ===========================================================

        public static Reading GetBalance(string iPersona, string iVoucher)
        {
            Dictionary<string, string> aValues = Run("讀餘額",
                "op=balance", "persona=" + iPersona, "voucher=" + iVoucher);
            return new Reading
            {
                Permanent = ValueInt(aValues, "permanent"),
                Expiring = ValueInt(aValues, "expiring"),
                Spendable = ValueInt(aValues, "spendable"),
            };
        }

        // ===========================================================
        // 區塊職責：某一批（按 `ref`）的用量 —— 發了幾張／還剩幾張／用了幾張。
        // 物理意義：自由時間收工要回報的是「**本場**那 10 顆用了幾顆」，
        //          而 `GetBalance` 回的是所有未過期限時券 ⇒ 同時持有兩場的券時它答的是別的問題。
        // 🩸 回 false ＝ **「我不知道」，⛔ 不是「一張都沒用」**。
        //   呼叫端不准做「發放量 − 0」——那是 TASK-0195 那隻病（查無被算成用完），
        //   所以回 false 時三個 out 值全部是 0，讓那個減法**在物理上拿不到數字**。
        // ⚠ 射程：券不記歷史 ⇒ 批次過期後的下一次寫入就被清掉，之後這支只會回 false。
        //   那是「不留歷史」這個拍板的**已知代價**，不是漏掉的一格。
        // ===========================================================
        public static bool TryGetUsage(string iPersona, string iVoucher, string iRef,
                                       out int oGranted, out int oRemain, out int oUsed)
        {
            oGranted = 0; oRemain = 0; oUsed = 0;
            if (string.IsNullOrEmpty(iRef)) return false;

            Dictionary<string, string> aValues = Run("讀某一批的用量",
                "op=usage", "persona=" + iPersona, "voucher=" + iVoucher, "ref=" + iRef);
            if (ValueInt(aValues, "found") != 1) return false;
            oGranted = ValueInt(aValues, "granted");
            oRemain = ValueInt(aValues, "remain");
            oUsed = ValueInt(aValues, "used");
            return true;
        }

        /// <summary>某一批（按 `ref`）**還花得掉**的張數。查無 ⇒ 0（⚠ 與「真的用完」同形，要分請走 <see cref="TryGetUsage"/>）。</summary>
        public static int GetAliveByRef(string iPersona, string iVoucher, string iRef)
        {
            if (string.IsNullOrEmpty(iRef)) return 0;
            Dictionary<string, string> aValues = Run("讀某一批的可花張數",
                "op=usage", "persona=" + iPersona, "voucher=" + iVoucher, "ref=" + iRef);
            return ValueInt(aValues, "found") == 1 ? ValueInt(aValues, "alive") : 0;
        }

        // ===========================================================
        // 區塊：寫
        // ===========================================================

        /// <summary>
        /// 發券。<paramref name="iExpiresAtIso"/> 空 ＝ 永久券；非空 ＝ 限時券。
        /// <para>回 (before, after) 的可花總額 —— ⚠ `before` 是**另一次讀取**（判準④），只配印 log。</para>
        /// </summary>
        public static (int before, int after) Grant(string iPersona, string iVoucher, int iAmount,
                                                    string iSource, string iRef, string iExpiresAtIso = null)
        {
            RequireAmount(iAmount);
            int aBefore = GetBalance(iPersona, iVoucher).Spendable;
            var aArgs = new List<string>
            {
                "op=grant", "persona=" + iPersona, "voucher=" + iVoucher,
                "amount=" + iAmount, "region=" + Region,
                "source=" + (iSource ?? ""), "ref=" + (iRef ?? ""),
            };
            string aExpires = (iExpiresAtIso ?? "").Trim();
            if (aExpires.Length > 0) aArgs.Add("expires_at=" + aExpires);

            Dictionary<string, string> aValues = Run("發券", aArgs.ToArray());
            int aAfter = ValueInt(aValues, "spendable");
            Debug.Log($"[Voucher] grant {iAmount} 張 `{iVoucher}` → {iPersona}"
                    + (aExpires.Length == 0 ? "（永久）" : $"（到 {aExpires}）")
                    + $"（可花 {aBefore} → {aAfter}）");
            return (aBefore, aAfter);
        }

        /// <summary>
        /// 用券。餘額不足由 Server 擋下 ⇒ 本層 throw（⛔ 不部分扣款 —— 半扣的帳沒有人能對）。
        /// </summary>
        public static (int before, int after) Consume(string iPersona, string iVoucher, int iAmount,
                                                      string iSource, string iRef)
        {
            RequireAmount(iAmount);
            int aBefore = GetBalance(iPersona, iVoucher).Spendable;
            Dictionary<string, string> aValues = Run("用券",
                "op=consume", "persona=" + iPersona, "voucher=" + iVoucher,
                "amount=" + iAmount, "region=" + Region,
                "source=" + (iSource ?? ""), "ref=" + (iRef ?? ""));
            int aAfter = ValueInt(aValues, "spendable");
            Debug.Log($"[Voucher] consume {iAmount} 張 `{iVoucher}` ← {iPersona}（可花 {aBefore} → {aAfter}）");
            return (aBefore, aAfter);
        }

        static void RequireAmount(int iAmount)
        {
            if (iAmount <= 0) throw new ArgumentException($"amount 需為正整數: {iAmount}");
        }

        // ===========================================================
        // 區塊職責：派一趟 `senate cmd voucher`，把 `🔢 k = v` 收成表。
        // ⚠ 用 `ArgumentList` 不拼字串 —— 引號在拼字串時同時扮演「綁詞」與「內容」兩個角色，
        //   而 CreateProcess 只認前者（`UCL_BankMirror` 那條血證）。
        // ⛔ 非零退出一律 throw：把「這一趟沒成功」變成呼叫端躲不掉的事。
        // ===========================================================
        static Dictionary<string, string> Run(string iWhat, params string[] iArgs)
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
                aPsi.ArgumentList.Add("voucher");
                aPsi.ArgumentList.Add("--arg"); aPsi.ArgumentList.Add("letters_root=" + LettersRoot);
                foreach (string aArg in iArgs)
                {
                    aPsi.ArgumentList.Add("--arg");
                    aPsi.ArgumentList.Add(aArg);
                }

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
                    $"[Voucher] {iWhat} 失敗 —— 派給 Senate Server 這一步就炸了"
                    + $"（{e.GetType().Name}: {e.Message}）。券**沒有動**。⛔ 不降級寫舊帳本。", e);
            }

            if (aExit != 0)
                throw new InvalidOperationException(
                    $"[Voucher] {iWhat} 被拒絕（exit {aExit}）：{PickReason(aOut, aErr)}"
                    + " —— 券**沒有動**。");

            return ParseValues(aOut);
        }

        /// <summary>收 `🔢 key = value` 行。⚠ 同名多次時**後者覆蓋前者**（與 CLI 印的順序一致）。</summary>
        static Dictionary<string, string> ParseValues(string iText)
        {
            var aOut = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(iText)) return aOut;
            foreach (string aRaw in iText.Split('\n'))
            {
                string aLine = aRaw.Trim();
                int aMark = aLine.IndexOf("🔢", StringComparison.Ordinal);
                if (aMark < 0) continue;
                aLine = aLine.Substring(aMark + "🔢".Length).Trim();
                int aEq = aLine.IndexOf('=');
                if (aEq <= 0) continue;
                aOut[aLine.Substring(0, aEq).Trim()] = aLine.Substring(aEq + 1).Trim();
            }
            return aOut;
        }

        // ⛔ 缺這一格**不回 0** —— 那正是判準①要擋的同形：
        //   「Server 沒印這個數字」與「這個數字真的是 0」完全不是同一件事。
        static int ValueInt(Dictionary<string, string> iValues, string iKey)
        {
            if (!iValues.TryGetValue(iKey, out string aRaw))
                throw new InvalidOperationException(
                    $"[Voucher] Server 沒回 `{iKey}` —— ⛔ 不當作 0"
                    + "（那會讓「讀不到」長得跟「沒有券」一模一樣）");
            if (!int.TryParse(aRaw, out int aValue))
                throw new InvalidOperationException($"[Voucher] `{iKey}` 不是整數（收到 '{aRaw}'）");
            return aValue;
        }

        // ===========================================================
        // 區塊職責：從 CLI 的輸出裡挑出**真正的理由**那一行。
        // 🩸 2026-09-18 實測：第一版挑「第一行非空白」，而 CLI 的第一行是
        //   `🔢 delegate_host = server` ⇒ 「券不足」被一個路由讀數蓋掉，
        //   例外訊息長得像壞在傳輸層，而它其實是一個正常的拒絕。
        //   ⇒ 先找帶 `✗` 的那行（Cmd 的拒絕都帶它），找不到才退回第一行。
        // 📌 同族的 `UCL_TreasuryAuthority` **同日稍晚也修好了**（原本這裡只留一句
        //   「它會有同一個症狀」——⚠ 而那句註解沒有修好任何東西，它只是讓我知道它壞著，
        //   然後它在幾小時後咬到書店捐贈那條真實路徑）。
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
            foreach (string aLine in iText.Split('\n'))
            {
                string aTrim = aLine.Trim();
                if (aTrim.IndexOf('✗') >= 0) return aTrim;
            }
            return "";
        }

        static string FirstLine(string iText)
        {
            if (string.IsNullOrEmpty(iText)) return "";
            foreach (string aLine in iText.Split('\n'))
                if (!string.IsNullOrWhiteSpace(aLine)) return aLine.Trim();
            return "";
        }
    }
}
#endif
