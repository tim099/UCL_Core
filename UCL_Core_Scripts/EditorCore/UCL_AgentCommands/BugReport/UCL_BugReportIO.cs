// 區塊職責：問題回報單的磁碟層 —— index 配發 / 單檔讀寫 / 清單 / stale 讀數。
// 物理意義：AgentCommands/BugReports/ 底下的唯一寫入端。Cmd 與後台頁都走這裡，不各自碰檔案。
//
// 📌 **一單一檔**（Tim 2026-08-18 拍板，取代原本的 bugs.jsonl 單一事件流）：
//    共用的 append-only 檔是 git 衝突的磁鐵 —— 兩個人同時開單就是同一行尾端的 conflict，
//    而那個 conflict 每次都要人手工判斷「這兩行要不要都留」。一單一檔之後同時開單是
//    **兩個新檔案**，git 完全不需要合併任何東西。
//    ⇒ 事實來源＝`reports/<index>.md` 的 frontmatter；**沒有第二份索引**
//      （有索引就會有「索引與檔案不一致」那一類的 bug，而它們通常靜默）。
//
// 數值影響：純檔案 IO。index 配發含自我修復（計數檔落後於實際檔案時自動拉齊 + 大聲喊）。
// 設計沿革：Plan_BugReport_System.md（Tim 2026-08-18 拍板）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.BugReport
{
    public static class UCL_BugReportIO
    {
        public const int STALE_DAYS = 14;

        public static string Dir => Path.Combine(UCL_AgentCommandsPath.DataRoot, "BugReports");
        public static string IndexPath => Path.Combine(Dir, "_index.txt");
        public static string ReportsDir => Path.Combine(Dir, "reports");
        public static string LastReportPath => Path.Combine(Dir, "_last_bug_report.md");

        public static void EnsureDir()
        {
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(ReportsDir);
        }

        /// <summary>單張報告的路徑（檔名補零只為排序好看；內容以 frontmatter 的整數 index 為準）。</summary>
        public static string ReportPath(int iIndex)
            => Path.Combine(ReportsDir, iIndex.ToString("0000", CultureInfo.InvariantCulture) + ".md");

        // ===========================================================
        // 區塊職責：配發下一個 index（1 起，單調遞增）。
        //
        // 物理意義：計數檔 `_index.txt` 存「**已發出的最後一個**」，初始 0 ⇒ 第一張是 1。
        //   1-based 讓 `0` 同時是合法初始值與 `int.TryParse` 失敗的回退值 ——
        //   **兩者一致，解析失敗不會撞號**（0-based 得用 -1，而任何解析失敗都會把它讀成 0）。
        //
        // 🩸 自我修復的判準是 **`>` 不是 `>=`**（2026-08-18 Tim 在 console 抓到）：
        //   正常配發完 index N 之後，計數檔＝N、磁碟上最大檔案也＝N ⇒ **相等是正常狀態**。
        //   我照抄酒館 `IncrementAndGetSeq` 時連 `>=` 一起抄了，於是**第二張單開始每次都噴 LogError**。
        //   那個條件在酒館是死的（messages 早就改成一訊息一檔，jsonl 不再被 append ⇒ 永遠回 0），
        //   所以原地看不出問題 —— **抄一個正確的形狀，不等於抄到一個活著的形狀。**
        //   ⇒ 只有「磁碟上出現我從沒發過的號」（嚴格大於）才是真的有人繞過 Cmd 直接建檔。
        //
        // 數值影響：一次讀計數檔 + 一次列 reports 目錄；寫回計數檔。
        // ===========================================================
        public static int IncrementAndGetIndex()
        {
            EnsureDir();
            int aCounter = ReadCurrentIndex();
            int aDiskMax = ReadMaxIndexOnDisk();
            if (aDiskMax > aCounter)
            {
                Debug.LogError(
                    $"[BugReport] ⚠️ 偵測到繞過 Cmd 建立的單：reports/ 最大 index={aDiskMax} > 計數檔={aCounter}。" +
                    $" 自動把計數檔拉齊到 {aDiskMax}（避免後續 index 撞號）。" +
                    $" 開單一律走 Cmd_BugReport，不要手建 reports/*.md。");
                aCounter = aDiskMax;
            }
            int aNext = aCounter + 1;
            File.WriteAllText(IndexPath, aNext.ToString(CultureInfo.InvariantCulture), new UTF8Encoding(false));
            return aNext;
        }

        /// <summary>讀計數檔（已發出的最後一個）。檔不存在或壞掉一律回 0 ⇒ 下一張是 1。</summary>
        public static int ReadCurrentIndex()
        {
            if (!File.Exists(IndexPath)) return 0;
            try
            {
                string s = File.ReadAllText(IndexPath, Encoding.UTF8).Trim();
                return int.TryParse(s, out var v) ? v : 0;
            }
            catch { return 0; }
        }

        // 區塊職責：磁碟上實際存在的最大 index。
        // 物理意義：**檔案就是事實** —— 計數檔只是快取，它落後時以磁碟為準。
        // 數值影響：列目錄取檔名；認不得的檔名跳過（不讓一個雜檔擋住配發）。
        public static int ReadMaxIndexOnDisk()
        {
            if (!Directory.Exists(ReportsDir)) return 0;
            int aMax = 0;
            foreach (var aPath in Directory.GetFiles(ReportsDir, "*.md"))
            {
                string aName = Path.GetFileNameWithoutExtension(aPath);
                if (int.TryParse(aName, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > aMax)
                    aMax = v;
            }
            return aMax;
        }

        // ===========================================================
        // 區塊職責：寫一張單（新建或狀態變更皆走這裡）。
        // 物理意義：**整檔重寫，但歷史留在檔內**（`## 變更紀錄` 逐行 append）——
        //          一單一檔之後沒有共用事件流可以放歷史，而歷史不能丟：
        //          「這張單什麼時候被誰關的」是事後對帳唯一的依據。
        // 數值影響：一次讀（取既有歷史）+ 一次寫。iHistoryLine 為空＝不追加歷史行。
        // ===========================================================
        public static void Save(UCL_BugReportEntry e, string iDescription, string iEvidence,
            string iRepro, string iExpected, string iActual, string iHistoryLine)
        {
            EnsureDir();
            string aPath = ReportPath(e.index);

            // 既有歷史先撈出來 —— 整檔重寫會蓋掉它，而它是不可重建的
            var aHistory = new List<string>();
            if (File.Exists(aPath))
            {
                bool aIn = false;
                foreach (var aLine in File.ReadAllLines(aPath, Encoding.UTF8))
                {
                    if (aLine.StartsWith("## 變更紀錄", StringComparison.Ordinal)) { aIn = true; continue; }
                    if (aIn && aLine.StartsWith("## ", StringComparison.Ordinal)) aIn = false;
                    if (aIn && aLine.TrimStart().StartsWith("- ", StringComparison.Ordinal)) aHistory.Add(aLine.Trim());
                }
                // 內文欄位沒給的話沿用既有的（狀態變更不必重打描述）
                if (string.IsNullOrEmpty(iDescription)) iDescription = ReadSection(aPath, "## 描述");
                if (string.IsNullOrEmpty(iEvidence)) iEvidence = ReadSection(aPath, "## 硬證據（evidence）");
                if (string.IsNullOrEmpty(iRepro)) iRepro = ReadSection(aPath, "## 重現步驟");
                if (string.IsNullOrEmpty(iExpected)) iExpected = ReadSection(aPath, "## 預期");
                if (string.IsNullOrEmpty(iActual)) iActual = ReadSection(aPath, "## 實際");
            }
            if (!string.IsNullOrEmpty(iHistoryLine)) aHistory.Add("- " + iHistoryLine);

            var sb = new StringBuilder();
            sb.Append("---\n");
            sb.Append($"index: {e.index}\n");
            sb.Append($"type: {e.type}\n");
            sb.Append($"severity: {e.severity}\n");
            sb.Append($"status: {e.status}\n");
            sb.Append($"title: {OneLine(e.title)}\n");
            sb.Append($"component: {OneLine(e.component)}\n");
            sb.Append($"reporter: {OneLine(e.reporter)}\n");
            sb.Append($"assignee: {OneLine(e.assignee)}\n");
            sb.Append($"resolution: {OneLine(e.resolution)}\n");
            sb.Append($"commit_sha: {OneLine(e.commit_sha)}\n");
            sb.Append($"created_at: {e.created_at}\n");
            sb.Append($"updated_at: {e.updated_at}\n");
            sb.Append("---\n\n");
            sb.Append($"# BUG-{e.index} — {e.title}\n\n");
            sb.Append($"> `{e.type}` / `{e.severity}` / `{e.status}`　回報者：{Nz2(e.reporter)}");
            if (!string.IsNullOrEmpty(e.assignee)) sb.Append($"　認領：{e.assignee}");
            sb.Append("\n\n");
            sb.Append("## 描述\n\n").Append(Nz(iDescription)).Append("\n\n");
            sb.Append("## 硬證據（evidence）\n\n").Append(Nz(iEvidence)).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(iRepro)) sb.Append("## 重現步驟\n\n").Append(iRepro).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(iExpected)) sb.Append("## 預期\n\n").Append(iExpected).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(iActual)) sb.Append("## 實際\n\n").Append(iActual).Append("\n\n");
            sb.Append("## 變更紀錄\n\n");
            foreach (var h in aHistory) sb.Append(h).Append('\n');
            File.WriteAllText(aPath, sb.ToString(), new UTF8Encoding(false));
        }

        // ===========================================================
        // 區塊職責：讀出所有單子（掃 reports/ 逐檔解析 frontmatter）。
        // 物理意義：檔案就是事實，沒有第二份索引要對帳。
        // 數值影響：單量級預期數百；每檔只讀 frontmatter 那幾行就停。
        // ===========================================================
        public static List<UCL_BugReportEntry> LoadAll()
        {
            var aList = new List<UCL_BugReportEntry>();
            if (!Directory.Exists(ReportsDir)) return aList;
            foreach (var aPath in Directory.GetFiles(ReportsDir, "*.md"))
            {
                var e = LoadFile(aPath);
                if (e != null) aList.Add(e);
            }
            aList.Sort((a, b) => a.index.CompareTo(b.index));
            return aList;
        }

        public static UCL_BugReportEntry Find(int iIndex)
        {
            string aPath = ReportPath(iIndex);
            return File.Exists(aPath) ? LoadFile(aPath) : null;
        }

        static UCL_BugReportEntry LoadFile(string iPath)
        {
            try
            {
                var e = new UCL_BugReportEntry();
                bool aIn = false;
                foreach (var aLine in File.ReadAllLines(iPath, Encoding.UTF8))
                {
                    if (aLine.StartsWith("---", StringComparison.Ordinal))
                    {
                        if (!aIn) { aIn = true; continue; }
                        break;                                  // frontmatter 收尾，不必再往下讀
                    }
                    if (!aIn) continue;
                    int c = aLine.IndexOf(':');
                    if (c <= 0) continue;
                    string k = aLine.Substring(0, c).Trim();
                    string v = aLine.Substring(c + 1).Trim();
                    switch (k)
                    {
                        case "index": int.TryParse(v, out e.index); break;
                        case "type": e.type = v; break;
                        case "severity": e.severity = v; break;
                        case "status": e.status = v; break;
                        case "title": e.title = v; break;
                        case "component": e.component = v; break;
                        case "reporter": e.reporter = v; break;
                        case "assignee": e.assignee = v; break;
                        case "resolution": e.resolution = v; break;
                        case "commit_sha": e.commit_sha = v; break;
                        case "created_at": e.created_at = v; break;
                        case "updated_at": e.updated_at = v; break;
                    }
                }
                return e.index > 0 ? e : null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BugReport] 讀取失敗，跳過：{iPath}（{ex.Message}）");
                return null;
            }
        }

        // ===========================================================
        // 區塊職責：open / stale 讀數 —— 早安 brief 與後台頁共用同一個算法。
        // 物理意義：**stale 不是另一種「已關」，是 open 的一個更難看的名字** ⇒ 算進 open，另外再報一個數。
        // 數值影響：updated_at 壞掉的單 DaysSinceUpdate 回 -1 ⇒ **不算 stale**，另外用 oBroken 報出來
        //          （不要混進 stale 數字裡假裝知道它幾天沒動）。
        // ===========================================================
        public static void CountOpen(out int oOpen, out int oStale, out int oBroken)
        {
            oOpen = 0; oStale = 0; oBroken = 0;
            var aNow = DateTime.UtcNow;
            foreach (var e in LoadAll())
            {
                if (e.IsClosed()) continue;
                oOpen++;
                int aDays = e.DaysSinceUpdate(aNow);
                if (aDays < 0) oBroken++;
                else if (aDays >= STALE_DAYS) oStale++;
            }
        }

        static string ReadSection(string iPath, string iHeading)
        {
            try
            {
                var sb = new StringBuilder();
                bool aIn = false;
                foreach (var aLine in File.ReadAllLines(iPath, Encoding.UTF8))
                {
                    if (aLine.StartsWith(iHeading, StringComparison.Ordinal)) { aIn = true; continue; }
                    if (aIn && aLine.StartsWith("## ", StringComparison.Ordinal)) break;
                    if (aIn) sb.Append(aLine).Append('\n');
                }
                string s = sb.ToString().Trim();
                return s == "_(未填)_" ? "" : s;
            }
            catch { return ""; }
        }

        // frontmatter 是一行一值 —— 換行會把後面的內容變成別的 key，所以進 frontmatter 前一律壓成單行。
        static string OneLine(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        static string Nz(string s) => string.IsNullOrWhiteSpace(s) ? "_(未填)_" : s;
        static string Nz2(string s) => string.IsNullOrWhiteSpace(s) ? "unknown" : s;
    }
}
#endif
