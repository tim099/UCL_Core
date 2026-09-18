// 區塊職責：**舊 `Treasury/` 這本帳的唯讀歷史** —— 它凍結於 2026-09-18 權威切換那一刻。
// 物理意義：2026-09-18 之後所有的錢都寫進 Senate 新銀行（`Bank/`），舊帳本**不再長新分錄**
//          （TASK-0216 ⑩ 的讀數：切換後全樹逐檔掃，新檔 0）。Tim 2026-09-18 拍板：
//          **「不刪，轉唯讀就好」** ⇒ 檔案原封不動留著，而**讀它的入口要說出它是歷史**。
// 數值影響：純讀，一毛都不動。
//
// 🩸 為什麼要獨立成一個叫「History」的類，而不是留在 `UCL_TreasuryLedger` 裡：
//   留在那裡的話，`UCL_TreasuryLedger.GetBalance`（問**現在**的錢、答案來自新銀行）
//   與 `UCL_TreasuryLedger.LoadAllEntries`（問**歷史**、答案來自凍結的舊帳）
//   會是同一個類上的兩支 API，而**它們回答的是兩個不同時代的問題**。
//   ⇒ 2026-09-18 的血證：保管費的「今天扣過了嗎」判重就是這樣壞的 ——
//     它呼叫的是舊帳本的掃描，而今天的扣款寫在新銀行 ⇒ **它永遠找不到，而找不到跟沒扣過同形**。
//   把名字換掉不會讓人變聰明，但它讓「我要的是現在還是歷史」變成呼叫時就要回答的問題。
//
// ⛔ 不准拿本類的東西去回答「**現在**有多少錢／**今天**做過什麼」——
//   那兩個問題的答案在新銀行那邊（`UCL_TreasuryLedger.GetBalance` / `SCP_BankLedger`）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Treasury
{
    /// <summary>舊 `Treasury/` 帳本的**唯讀歷史**（凍結於 2026-09-18）。</summary>
    public static class UCL_TreasuryHistory
    {
        // ==========================================================

        // 區塊職責：Replay — 載入全部 ledger entries（sort by ts）

        // 物理意義：walk ledger/<date>/*.json + ordinal sort

        // 數值影響：純讀；caller 用來算 balance / audit

        // ==========================================================

        public static List<TreasuryLedgerEntry> LoadAllEntries()

        {

            var list = new List<TreasuryLedgerEntry>();

            string root = UCL_TreasuryPaths.GetLedgerRoot();

            if (!Directory.Exists(root)) return list;



            string[] files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);

            Array.Sort(files, (a, b) =>

            {

                string ra = a.Substring(root.Length).Replace('\\', '/');

                string rb = b.Substring(root.Length).Replace('\\', '/');

                return string.CompareOrdinal(ra, rb);

            });



            foreach (var f in files)

            {

                try

                {

                    var entry = UCL_TreasuryLedger.ParseEntry(File.ReadAllText(f, Encoding.UTF8));

                    if (entry != null) list.Add(entry);

                }

                catch (Exception ex)

                {

                    Debug.LogError($"[Treasury] Skipping malformed ledger entry {Path.GetFileName(f)}: {ex.Message}");

                }

            }

            return list;

        }



        // ==========================================================

        // 區塊職責：只載入「某個結帳日之後」的 entry（跨日結算用）

        // 物理意義：結帳檔是已關帳期間的權威記錄（見 UCL_TreasuryClosing 檔頭），

        //          所以「結帳日之後的日期夾」就是全部尚未被結帳涵蓋的帳。

        //          呼叫端把結帳檔的餘額當種子、再疊上本函式回傳的 entry，

        //          即可得到與全量重放**等價**的結果，而成本從 O(全部歷史) 降到 O(未關帳期間)。

        // 數值影響：純讀。日期夾以 yyyy-MM-dd 命名，字典序 == 時間序，直接字串比大小。

        // ⚠ 邊界（這裡有一隻紅隊抓到的坑）：範圍必須是「**結帳日之後全部**」，

        //   不可以只抓「今天前後幾夾」。實測：結帳落後 3 天時，固定三夾會漏掉

        //   08-12 才誕生的 `Template` 帳戶 —— 而結帳落後正是 GenerateMissing 失敗時的常態

        //   （它包在 try/catch 裡、刻意不擋保管費）。**降級路徑上的假設最容易沒人驗。**

        // ⚠ afterDateKeyExclusive 為 null/空 → 回傳全部（等同 LoadAllEntries 的集合，

        //   但不排序）。呼叫端沒有結帳檔可用時就是走這條，慢但正確。

        // ==========================================================

        public static List<TreasuryLedgerEntry> LoadEntriesAfterDate(string afterDateKeyExclusive)

        {

            var list = new List<TreasuryLedgerEntry>();

            string root = UCL_TreasuryPaths.GetLedgerRoot();

            if (!Directory.Exists(root)) return list;



            foreach (var dir in Directory.GetDirectories(root))

            {

                string dayKey = Path.GetFileName(dir);

                // 非日期夾一律跳過（不猜、不容錯 —— 猜錯會把不該算的錢算進來）

                if (!DateTime.TryParseExact(dayKey, "yyyy-MM-dd",

                        System.Globalization.CultureInfo.InvariantCulture,

                        System.Globalization.DateTimeStyles.None, out _)) continue;

                if (!string.IsNullOrEmpty(afterDateKeyExclusive)

                    && string.CompareOrdinal(dayKey, afterDateKeyExclusive) <= 0) continue;



                foreach (var f in Directory.GetFiles(dir, "*.json"))

                {

                    try

                    {

                        var entry = UCL_TreasuryLedger.ParseEntry(File.ReadAllText(f, Encoding.UTF8));

                        if (entry != null) list.Add(entry);

                    }

                    catch (Exception ex)

                    {

                        Debug.LogError($"[Treasury] Skipping malformed ledger entry {Path.GetFileName(f)}: {ex.Message}");

                    }

                }

            }

            return list;

        }



        // ==========================================================

        // 區塊職責：Audit — 給定 account 列其全 ledger entries

        // ==========================================================

        public static List<TreasuryLedgerEntry> Audit(string accountId, string sinceTs = null)

        {

            var all = LoadAllEntries();

            return all.Where(e =>

                e.account_id == accountId &&

                (string.IsNullOrEmpty(sinceTs) || string.CompareOrdinal(e.ts, sinceTs) > 0)

            ).ToList();

        }
    }
}
#endif
