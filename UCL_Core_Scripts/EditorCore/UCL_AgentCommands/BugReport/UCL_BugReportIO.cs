// 區塊職責：問題回報單的磁碟層 —— index 配發 / jsonl append / 讀清單 / 報告 md / stale 掃描。
// 物理意義：AgentCommands/BugReports/ 底下的唯一寫入端。Cmd 與後台頁都走這裡，不各自碰檔案。
// 數值影響：純檔案 IO。index 配發含 illicit-write 自我修復（照抄酒館 _seq.txt 的形狀，見下）。
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
        public static string JsonlPath => Path.Combine(Dir, "bugs.jsonl");
        public static string IndexPath => Path.Combine(Dir, "_index.txt");
        public static string ReportsDir => Path.Combine(Dir, "reports");
        public static string LastReportPath => Path.Combine(Dir, "_last_bug_report.md");

        public static void EnsureDir()
        {
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(ReportsDir);
        }

        // ===========================================================
        // 區塊職責：配發下一個 index（1 起，單調遞增）。
        //
        // 物理意義：**照抄 UCL_ChatTavernIO.IncrementAndGetSeq 的形狀，一處都不改。**
        //   那支已經解過同一題，而且解過它自己踩過的那次：
        //   原版只讀計數檔 +1；若有人繞過 Cmd 直接 append jsonl，計數檔不知情 ⇒ 下次合法配發撞號。
        //   🩸 **已實際發生**：Antigravity standby_loop.py 直寫 messages.jsonl，
        //      造成 tavern seq 57~76 各重複 2 次。
        //   ⇒ 修法：寫前 peek jsonl 最大 index，>= counter 就自動拉齊 + LogError 大聲喊。
        //
        // 📌 為什麼計數檔存「已發出的最後一個」而不是「下一個要發的」：
        //   index 是 **1-based**（Tim 2026-08-18），所以初始值 0 既是合法起點、
        //   也是 int.TryParse 失敗時的回退值 —— **兩者一致，解析失敗不會撞號**。
        //   （0-based 的話初始值得是 -1，而任何解析失敗都會把它讀成 0 ⇒ 第一筆就撞。
        //    那正是本設計曾經打算偏離酒館的唯一理由，改 1-based 之後偏離取消。）
        //
        // 數值影響：一次讀計數檔 + 一次反向掃 jsonl 尾端；寫回計數檔。
        // ===========================================================
        public static int IncrementAndGetIndex()
        {
            EnsureDir();
            int aCounter = ReadCurrentIndex();
            int aJsonlMax = ReadMaxIndexFromJsonl();
            if (aJsonlMax >= aCounter)
            {
                Debug.LogError(
                    $"[BugReport] ⚠️ 偵測到繞過 Cmd 的直寫：bugs.jsonl 最大 index={aJsonlMax} >= 計數檔={aCounter}。" +
                    $" 自動把計數檔拉齊到 {aJsonlMax}（避免後續 index 撞號）。" +
                    $" 單子一律走 Cmd_BugReport，不要手改 bugs.jsonl。");
                aCounter = aJsonlMax;
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

        // 區塊職責：反向掃 jsonl 找最大 index（給上面的 sanity check 用）。
        // 數值影響：append-only ⇒ 通常最後一行就是最大值，找到第一筆合法的就停。
        //          壞行跳過；檔不存在或全壞回 0。
        public static int ReadMaxIndexFromJsonl()
        {
            if (!File.Exists(JsonlPath)) return 0;
            try
            {
                var aLines = File.ReadAllLines(JsonlPath, Encoding.UTF8);
                for (int i = aLines.Length - 1; i >= 0; i--)
                {
                    int aIdx = ExtractInt(aLines[i], "index");
                    if (aIdx > 0) return aIdx;
                }
            }
            catch (Exception e) { Debug.LogWarning($"[BugReport] 掃 jsonl 最大 index 失敗：{e.Message}"); }
            return 0;
        }

        // ===========================================================
        // 區塊職責：append 一行到 bugs.jsonl（append-only 事件流）。
        // 物理意義：狀態變更**不就地改舊行**，而是 append 新的一行 —— 保留完整歷史。
        //          讀取端（LoadAll）以「同 index 取最後一行」為當前值。
        // 數值影響：單次 append；不重寫既有內容（append-only 帳本被就地改寫是另一種災難）。
        // ===========================================================
        public static void Append(UCL_BugReportEntry iEntry)
        {
            EnsureDir();
            var sb = new StringBuilder();
            sb.Append('{');
            J(sb, "index", iEntry.index); sb.Append(',');
            J(sb, "type", iEntry.type); sb.Append(',');
            J(sb, "severity", iEntry.severity); sb.Append(',');
            J(sb, "status", iEntry.status); sb.Append(',');
            J(sb, "title", iEntry.title); sb.Append(',');
            J(sb, "component", iEntry.component); sb.Append(',');
            J(sb, "reporter", iEntry.reporter); sb.Append(',');
            J(sb, "assignee", iEntry.assignee); sb.Append(',');
            J(sb, "resolution", iEntry.resolution); sb.Append(',');
            J(sb, "commit_sha", iEntry.commit_sha); sb.Append(',');
            J(sb, "created_at", iEntry.created_at); sb.Append(',');
            J(sb, "updated_at", iEntry.updated_at);
            sb.Append('}');
            File.AppendAllText(JsonlPath, sb.ToString() + "\n", new UTF8Encoding(false));
        }

        // ===========================================================
        // 區塊職責：讀出所有單子的**當前狀態**（同 index 取最後一行）。
        // 物理意義：jsonl 是事件流不是狀態表 —— 直接把每一行當一張單會看到同一張單的多個版本。
        // 數值影響：一次全讀；單量級預期 < 數千行，成本可忽略。壞行跳過不中斷。
        // ===========================================================
        public static List<UCL_BugReportEntry> LoadAll()
        {
            var aMap = new Dictionary<int, UCL_BugReportEntry>();
            if (!File.Exists(JsonlPath)) return new List<UCL_BugReportEntry>();
            foreach (var aLine in File.ReadAllLines(JsonlPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(aLine)) continue;
                int aIdx = ExtractInt(aLine, "index");
                if (aIdx <= 0) continue;
                aMap[aIdx] = new UCL_BugReportEntry
                {
                    index = aIdx,
                    type = ExtractStr(aLine, "type"),
                    severity = ExtractStr(aLine, "severity"),
                    status = ExtractStr(aLine, "status"),
                    title = ExtractStr(aLine, "title"),
                    component = ExtractStr(aLine, "component"),
                    reporter = ExtractStr(aLine, "reporter"),
                    assignee = ExtractStr(aLine, "assignee"),
                    resolution = ExtractStr(aLine, "resolution"),
                    commit_sha = ExtractStr(aLine, "commit_sha"),
                    created_at = ExtractStr(aLine, "created_at"),
                    updated_at = ExtractStr(aLine, "updated_at"),
                };
            }
            var aList = new List<UCL_BugReportEntry>(aMap.Values);
            aList.Sort((a, b) => a.index.CompareTo(b.index));
            return aList;
        }

        public static UCL_BugReportEntry Find(int iIndex)
        {
            foreach (var e in LoadAll()) if (e.index == iIndex) return e;
            return null;
        }

        // ===========================================================
        // 區塊職責：open / stale 讀數 —— 早安 brief 與後台頁共用同一個算法。
        // 物理意義：**stale 不是另一種「已關」，是 open 的一個更難看的名字** ——
        //          所以它算進 open 裡，另外再單獨報一個數。
        //          一張沒人動的 open 單跟沒有那張單長得一模一樣，這個讀數就是讓它出聲的唯一機制。
        // 數值影響：純讀。updated_at 壞掉的單 DaysSinceUpdate 回 -1 ⇒ **不算 stale**
        //          （壞時戳另外用 oBroken 報出來，不要混進 stale 數字裡假裝知道它幾天沒動）。
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

        /// <summary>單張報告的 md 路徑（檔名補零只為排序好看；內容以整數 index 為準）。</summary>
        public static string ReportPath(int iIndex)
            => Path.Combine(ReportsDir, iIndex.ToString("0000", CultureInfo.InvariantCulture) + ".md");

        // 區塊職責：寫單張報告的人可讀 md（frontmatter + 內文）。
        // 數值影響：整檔覆寫 —— 這一份是**投影**，事實來源是 bugs.jsonl。
        public static void WriteReportMd(UCL_BugReportEntry e, string iDescription, string iEvidence,
            string iRepro, string iExpected, string iActual)
        {
            EnsureDir();
            var sb = new StringBuilder();
            sb.Append("---\n");
            sb.Append($"index: {e.index}\n");
            sb.Append($"type: {e.type}\n");
            sb.Append($"severity: {e.severity}\n");
            sb.Append($"status: {e.status}\n");
            sb.Append($"component: {e.component}\n");
            sb.Append($"reporter: {e.reporter}\n");
            sb.Append($"created_at: {e.created_at}\n");
            sb.Append($"updated_at: {e.updated_at}\n");
            sb.Append("generated: mechanical   # 事實來源是 bugs.jsonl；手改此檔不會回寫\n");
            sb.Append("---\n\n");
            sb.Append($"# BUG-{e.index} — {e.title}\n\n");
            sb.Append($"> `{e.type}` / `{e.severity}` / `{e.status}`　回報者：{e.reporter}\n\n");
            sb.Append("## 描述\n\n").Append(Nz(iDescription)).Append("\n\n");
            sb.Append("## 硬證據（evidence）\n\n").Append(Nz(iEvidence)).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(iRepro)) sb.Append("## 重現步驟\n\n").Append(iRepro).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(iExpected)) sb.Append("## 預期\n\n").Append(iExpected).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(iActual)) sb.Append("## 實際\n\n").Append(iActual).Append("\n\n");
            File.WriteAllText(ReportPath(e.index), sb.ToString(), new UTF8Encoding(false));
        }

        static string Nz(string s) => string.IsNullOrWhiteSpace(s) ? "_(未填)_" : s;

        // ─── 極簡 JSON 取值（與 Cmd_NoteLesson 同慣例：手搭，不引依賴）────────────
        static void J(StringBuilder sb, string k, string v)
            => sb.Append('"').Append(k).Append("\":").Append(ToJsonString(v ?? ""));
        static void J(StringBuilder sb, string k, int v)
            => sb.Append('"').Append(k).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture));

        public static string ToJsonString(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        static string ExtractStr(string iLine, string iKey)
        {
            string aPat = "\"" + iKey + "\":\"";
            int i = iLine.IndexOf(aPat, StringComparison.Ordinal);
            if (i < 0) return "";
            i += aPat.Length;
            var sb = new StringBuilder();
            while (i < iLine.Length)
            {
                char c = iLine[i];
                if (c == '\\' && i + 1 < iLine.Length)
                {
                    char n = iLine[i + 1];
                    sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n == 'r' ? '\r' : n);
                    i += 2; continue;
                }
                if (c == '"') break;
                sb.Append(c); i++;
            }
            return sb.ToString();
        }

        static int ExtractInt(string iLine, string iKey)
        {
            string aPat = "\"" + iKey + "\":";
            int i = iLine.IndexOf(aPat, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += aPat.Length;
            int aStart = i;
            while (i < iLine.Length && (char.IsDigit(iLine[i]) || iLine[i] == '-')) i++;
            return int.TryParse(iLine.Substring(aStart, i - aStart), out var v) ? v : 0;
        }
    }
}
#endif
