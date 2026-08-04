// 區塊職責：每日結帳（Daily Closing）—— 產生與讀取「已關帳期間」的帳戶餘額。
// 物理意義：ledger 是 append-only 的 entry 流，算餘額原本要重放全部歷史。
//          結帳把「某個 UTC 日結束時的各帳戶餘額」定下來，之後算餘額只需
//          「最近一份結帳 + 該日之後的 entry」——成本從 O(全部歷史) 變成 O(今日)。
//
// ⚠ 核心語意（Tim 2026-08-04 拍板，這條反轉過一次）：
//   **結帳檔是已關帳期間的權威記錄**，不是「快取」。
//   舊日期的 ledger 本就不該再變；若真有 bug 把一筆 entry 寫進已關帳的日期夾，
//   **建檔的紀錄比那一筆單獨的帳更權威** —— 它不會被算進餘額，而讀取演算法
//   本來就只重放「結帳日之後」的日期夾，所以那筆天然落在範圍外，
//   不需要任何額外邏輯去忽略它，也不需要偵測或重建。
//   （初版設計把結帳當快取，於是得煩惱「快取與真相不一致怎麼辦」；
//     換成「結帳就是該期間的帳」之後，那個不一致在定義上不存在。
//     這正是真實會計的做法：已關帳的期間就是關帳了，遲到的憑證進當期而非改寫歷史。）
//
// 數值影響：只寫 `Treasury/closing/<UTC日>.json`，不動 ledger、不動任何餘額。
// 設計取捨：
//   - **只為「有 entry 的日期」寫結帳**：沒有 entry 的日子餘額與前一份完全相同，
//     寫了是純重複。讀取端「找 date < today 的最新一份」對日期有缺口一樣成立。
//   - `audit` 區塊在產出當下順手算（那時本來就抓了全量，**免費**），
//     但**不參與讀取判斷** —— 記錄而不執法（apex-one 2026-08-04 提案，Tim 定調不當 gate）。
//     哪天真的懷疑異常，有東西可以比對、可以去 git 查。
// @doc-sync: Assets/Plugins/UCL_Core/Docs~/zh-Hant/Workflows/Treasury_Workflow.md
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Treasury
{
    /// <summary>一份每日結帳的內容。</summary>
    public class TreasuryClosingRecord
    {
        public string DateKey;                 // UTC yyyy-MM-dd
        /// <summary>key = accountId + "\n" + currency（與 ledger 快取同一組 key 規則）。</summary>
        public Dictionary<string, int> Balances = new Dictionary<string, int>();
        public int CumulativeEntryCount;
        public string LastEntryRel;
        public long GrossCredit;
        public long GrossDebit;
    }

    public static class UCL_TreasuryClosing
    {
        const string KeySep = "\n";

        static string BalKey(string accountId, string currency)
            => accountId + KeySep + (string.IsNullOrEmpty(currency) ? "tavern_token" : currency);

        // ===========================================================
        // 區塊：讀取 —— 找出可用的結帳基準
        // ===========================================================

        /// <summary>ledger 內所有日期夾名（yyyy-MM-dd），已排序。</summary>
        static List<string> ListLedgerDateKeys()
        {
            var result = new List<string>();
            string root = UCL_TreasuryPaths.GetLedgerRoot();
            if (!Directory.Exists(root)) return result;
            foreach (var d in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(d);
                if (DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out _))
                    result.Add(name);
            }
            result.Sort(StringComparer.Ordinal);   // yyyy-MM-dd 字典序 == 時間序
            return result;
        }

        /// <summary>已存在的結帳日期（yyyy-MM-dd），已排序。</summary>
        public static List<string> ListClosingDateKeys()
        {
            var result = new List<string>();
            string root = UCL_TreasuryPaths.GetClosingRoot();
            if (!Directory.Exists(root)) return result;
            foreach (var f in Directory.GetFiles(root, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out _))
                    result.Add(name);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// 取「日期嚴格小於 beforeDateKey」的最新一份結帳；沒有回 null。
        /// </summary>
        /// <remarks>
        /// 嚴格小於是關鍵：**今天還沒關帳**，今天的 entry 必須被重放而不是被結帳覆蓋。
        /// 用 &lt;= 會把今天已寫入的部分當成「已定案」，之後今天再進帳就算不到了。
        /// </remarks>
        public static TreasuryClosingRecord LoadLatestBefore(string beforeDateKey)
        {
            var keys = ListClosingDateKeys();
            for (int i = keys.Count - 1; i >= 0; i--)
            {
                if (string.CompareOrdinal(keys[i], beforeDateKey) >= 0) continue;
                var rec = TryRead(keys[i]);
                if (rec != null) return rec;
                // 讀壞就往更早找 —— 舊的結帳一樣有效，只是要多重放幾天
                Debug.LogWarning($"[TreasuryClosing] {keys[i]}.json 讀取失敗，往更早的結帳退。");
            }
            return null;
        }

        static TreasuryClosingRecord TryRead(string dateKey)
        {
            try
            {
                string path = UCL_TreasuryPaths.GetClosingPath(dateKey);
                if (!File.Exists(path)) return null;
                var json = JsonData.ParseJson(File.ReadAllText(path, Encoding.UTF8));
                if (json == null || !json.IsObject) return null;
                var rec = new TreasuryClosingRecord { DateKey = json.GetString("date", dateKey) };

                var bal = json.Get("balances");
                if (bal != null && bal.IsObject && bal.Dic != null)
                {
                    foreach (var accountId in bal.Dic.Keys)
                    {
                        var perCurrency = bal[accountId];
                        if (perCurrency == null || !perCurrency.IsObject || perCurrency.Dic == null) continue;
                        foreach (var cur in perCurrency.Dic.Keys)
                            rec.Balances[BalKey(accountId, cur)] = perCurrency.GetInt(cur, 0);
                    }
                }
                var audit = json.Get("audit");
                if (audit != null && audit.IsObject)
                {
                    rec.CumulativeEntryCount = audit.GetInt("cumulative_entry_count", 0);
                    rec.LastEntryRel = audit.GetString("last_entry_rel", "");
                }
                return rec;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TreasuryClosing] 讀 {dateKey} 失敗: {ex.Message}");
                return null;
            }
        }

        // ===========================================================
        // 區塊：產生 —— 把所有「已完結但還沒結帳」的日期補齊
        // 物理意義：向前遞推 closing(D) = closing(D-1) + entries(D)。
        //          只處理**嚴格早於今日(UTC)** 的日期 —— 今天還在寫，還不能關帳。
        // 數值影響：寫 0..N 份結帳檔；不動 ledger。回傳新產生的份數。
        // 邊界：Editor 關了一週再開 → 一次補 7 份（每份都要，因為它們是各日的權威記錄）。
        // ===========================================================
        public static int GenerateMissing(out string summary)
        {
            summary = "";
            try
            {
                string todayKey = UCL_TreasuryPaths.DateKey(DateTime.UtcNow);
                var ledgerDays = ListLedgerDateKeys();
                if (ledgerDays.Count == 0) { summary = "ledger 無任何日期夾，不需結帳"; return 0; }

                var closedSet = new HashSet<string>(ListClosingDateKeys(), StringComparer.Ordinal);

                // 待結帳 = 有 entry、早於今天、且還沒結過的日期
                var todo = ledgerDays
                    .Where(d => string.CompareOrdinal(d, todayKey) < 0 && !closedSet.Contains(d))
                    .ToList();
                if (todo.Count == 0) { summary = "所有已完結的日期都已結帳"; return 0; }

                // 基準 = 待結帳中最早那天之前的最新結帳（沒有就從零開始）
                var baseRec = LoadLatestBefore(todo[0]);
                var running = baseRec != null
                    ? new Dictionary<string, int>(baseRec.Balances)
                    : new Dictionary<string, int>();
                int cumCount = baseRec?.CumulativeEntryCount ?? 0;

                // ⚠ 基準之後、但早於 todo[0] 的日期若也有 entry，代表結帳有缺口
                //   （例如手動刪過某天的結帳檔）。那些日期必須一併重放進 running，
                //   否則新產生的結帳會漏算它們 —— 這是唯一一處需要「補算」的地方。
                string baseKey = baseRec?.DateKey ?? "";
                foreach (var d in ledgerDays)
                {
                    if (string.CompareOrdinal(d, baseKey) <= 0) continue;
                    if (string.CompareOrdinal(d, todo[0]) >= 0) break;
                    ApplyDay(d, running, ref cumCount, out _, out _);
                }

                string root = UCL_TreasuryPaths.GetLedgerRoot();
                Directory.CreateDirectory(UCL_TreasuryPaths.GetClosingRoot());
                int written = 0;
                long grossC = 0, grossD = 0;
                var doneKeys = new List<string>();

                foreach (var day in todo)
                {
                    ApplyDay(day, running, ref cumCount, out long dc, out long dd);
                    grossC += dc; grossD += dd;
                    string lastRel = LastEntryRelOfDay(root, day);
                    WriteClosing(day, running, cumCount, lastRel, grossC, grossD);
                    written++; doneKeys.Add(day);
                }

                summary = written == 0 ? "無新結帳"
                    : $"新結帳 {written} 份：{string.Join(", ", doneKeys)}（累計 entry {cumCount}）";
                return written;
            }
            catch (Exception ex)
            {
                summary = $"結帳失敗（不影響餘額正確性，下次再試）: {ex.Message}";
                Debug.LogWarning($"[TreasuryClosing] {summary}");
                return 0;
            }
        }

        /// <summary>把某一天的 entry 全部累進 running；順手回報當日 credit / debit 總額。</summary>
        static void ApplyDay(string dayKey, Dictionary<string, int> running, ref int cumCount,
                             out long dayCredit, out long dayDebit)
        {
            dayCredit = 0; dayDebit = 0;
            string dir = Path.Combine(UCL_TreasuryPaths.GetLedgerRoot(), dayKey);
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                cumCount++;
                TreasuryLedgerEntry e = null;
                try { e = UCL_TreasuryLedger.ParseEntry(File.ReadAllText(f, Encoding.UTF8)); }
                catch (Exception ex)
                {
                    // 壞檔跳過但仍計入 cumCount —— 它確實存在於磁碟上，
                    // 不計入會讓 audit 數字跟現實對不上（而 audit 的用途正是事後對帳）。
                    Debug.LogError($"[TreasuryClosing] 跳過壞檔 {Path.GetFileName(f)}: {ex.Message}");
                    continue;
                }
                if (e == null || string.IsNullOrEmpty(e.account_id)) continue;
                string key = BalKey(e.account_id, e.currency);
                running.TryGetValue(key, out int bal);
                if (e.type == "credit") { bal += e.amount; dayCredit += e.amount; }
                else if (e.type == "debit") { bal -= e.amount; dayDebit += e.amount; }
                else continue;
                running[key] = bal;
            }
        }

        static string LastEntryRelOfDay(string root, string dayKey)
        {
            try
            {
                string dir = Path.Combine(root, dayKey);
                if (!Directory.Exists(dir)) return "";
                var files = Directory.GetFiles(dir, "*.json");
                if (files.Length == 0) return "";
                Array.Sort(files, StringComparer.Ordinal);
                return "/" + dayKey + "/" + Path.GetFileName(files[files.Length - 1]);
            }
            catch { return ""; }
        }

        // 寫檔：atomic（tmp + move），避免半寫檔被讀到。
        // 餘額 0 的帳戶**照樣寫入** —— 不寫的話「歸零」跟「這個帳戶不存在」在下游長得一樣
        // （2026-08-04 三人一致：金融語意上兩者本質不同）。
        static void WriteClosing(string dayKey, Dictionary<string, int> running,
                                 int cumCount, string lastRel, long grossC, long grossD)
        {
            var byAccount = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
            foreach (var kv in running)
            {
                int sep = kv.Key.IndexOf(KeySep, StringComparison.Ordinal);
                if (sep < 0) continue;
                string acc = kv.Key.Substring(0, sep);
                string cur = kv.Key.Substring(sep + KeySep.Length);
                if (!byAccount.TryGetValue(acc, out var m))
                    byAccount[acc] = m = new SortedDictionary<string, int>(StringComparer.Ordinal);
                m[cur] = kv.Value;
            }

            var root = new JsonData();
            root["date"] = new JsonData(dayKey);
            root["generated_at"] = new JsonData(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff",
                CultureInfo.InvariantCulture) + "Z");
            var bal = new JsonData();
            foreach (var kv in byAccount)
            {
                var per = new JsonData();
                foreach (var c in kv.Value) per[c.Key] = new JsonData(c.Value);
                bal[kv.Key] = per;
            }
            root["balances"] = bal;
            var audit = new JsonData();
            audit["cumulative_entry_count"] = new JsonData(cumCount);
            audit["last_entry_rel"] = new JsonData(lastRel ?? "");
            audit["gross_credit"] = new JsonData((int)grossC);
            audit["gross_debit"] = new JsonData((int)grossD);
            audit["_note"] = new JsonData("稽核用，不參與讀取判斷（記錄而不執法）");
            root["audit"] = audit;

            string path = UCL_TreasuryPaths.GetClosingPath(dayKey);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonBeautify(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            Debug.Log($"[TreasuryClosing] 結帳 {dayKey} — {byAccount.Count} 帳戶，累計 entry {cumCount}");
        }
    }
}
#endif
