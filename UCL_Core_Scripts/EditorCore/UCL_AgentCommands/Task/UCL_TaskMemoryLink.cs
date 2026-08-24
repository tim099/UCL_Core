// 區塊職責：Task → 工作記憶的**讀取端**（錨點解讀）。TASK-0015 的 C# 那半。
//
// 物理意義：錨點放在 Task 檔（`memory_topic` / `memory_archived_commit`），
//   因為**記憶會被歸檔或刪除，而 Task 檔一定還在**（它是承諾紀錄）。
//   ⇒ 於是「那份記憶現在在哪」這個問題有**四種答案**，而它們絕不可以長得一樣：
//     ① 沒有掛記憶（小單不需要）
//     ② 主題在 ⇒ 印它的 state 摘要（接手時第一眼要看的東西）
//     ③ 已歸檔／刪除 ⇒ 印「已歸檔（sha）」—— 那是「曾經有，現在在 git 裡」
//     ④ 掛了主題但**磁碟上找不到** ⇒ 印「⚠ 指向一個不存在的主題」
//   🩸 ③ 與 ④ 若都印成「沒有記憶」，就是「找不到 vs 什麼都沒有」那隻 ——
//     而那隻今天已經在別的地方咬過我們（關鍵字查失敗跟記憶不存在同形）。
//
// ⚠ 契約①（basecamp，工作記憶 `decision_contract-task-memory`）：
//   **本檔只讀記憶側，絕不寫。** 記憶側的 `task_indices` / `status` 歸 python CLI 寫；
//   Task 側的兩個欄位歸 Cmd_Task 寫。這條連結有兩個獨立寫入者（不同語言、不同 process），
//   互寫就是分散式寫入衝突 —— 而它會在併發時安靜地覆蓋。
// 2026-08-24 summit（TASK-0015）
#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace UCL.Core.EditorLib.AgentCommands.TaskMgmt
{
    public static class UCL_TaskMemoryLink
    {
        public static string MemoryRoot => Path.Combine(UCL_AgentCommandsPath.DataRoot, "WorkMemory");

        public static string TopicDir(string iTopic)
            => string.IsNullOrWhiteSpace(iTopic) ? "" : Path.Combine(MemoryRoot, iTopic.Trim());

        /// <summary>主題在磁碟上存在嗎（`_topic.md` 才算，空目錄不算）。</summary>
        public static bool TopicExists(string iTopic)
        {
            string aDir = TopicDir(iTopic);
            return aDir.Length > 0 && File.Exists(Path.Combine(aDir, "_topic.md"));
        }

        /// <summary>主題的 `status`（active / archived / …）。讀不到回空字串 —— 不假設 active。</summary>
        public static string TopicStatus(string iTopic)
        {
            try
            {
                string aPath = Path.Combine(TopicDir(iTopic), "_topic.md");
                if (!File.Exists(aPath)) return "";
                foreach (var aLine in File.ReadAllLines(aPath, Encoding.UTF8))
                {
                    if (aLine.StartsWith("---", StringComparison.Ordinal)) continue;
                    int c = aLine.IndexOf(':');
                    if (c <= 0) continue;
                    if (aLine.Substring(0, c).Trim() == "status") return aLine.Substring(c + 1).Trim();
                }
            }
            catch { /* 讀不到就回空 —— 見下方：空與 active 在輸出上會被分開講 */ }
            return "";
        }

        // ===========================================================
        // 區塊職責：最新的 `state` fragment ＋ 它多久沒動。
        // 物理意義：`state` 是「進度快照」那一型 —— 接手時第一眼該看的就是它。
        //   ⚠ 檔名前綴 `state_` 是記憶側 CLI 的慣例（`work_memory.py` 的五型之一）；
        //     這裡**只認前綴不猜語意**，認不出就說認不出。
        // 數值影響：純讀。回傳 (檔名, 標題行, 檔案 mtime 天數)；沒有 state 時 oName 為空。
        // ===========================================================
        public static void LatestState(string iTopic, out string oName, out string oHeadline, out int oDays)
        {
            oName = ""; oHeadline = ""; oDays = -1;
            try
            {
                string aDir = TopicDir(iTopic);
                if (aDir.Length == 0 || !Directory.Exists(aDir)) return;
                var aFiles = Directory.GetFiles(aDir, "state_*.md")
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).ToList();
                if (aFiles.Count == 0) return;
                string aPath = aFiles[0];
                oName = Path.GetFileNameWithoutExtension(aPath);
                oDays = (int)(DateTime.UtcNow - File.GetLastWriteTimeUtc(aPath)).TotalDays;
                // 標題：frontmatter 的 title，沒有就取第一行非空內文
                foreach (var aLine in File.ReadAllLines(aPath, Encoding.UTF8))
                {
                    string aTrim = aLine.Trim();
                    if (aTrim.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                    { oHeadline = aTrim.Substring(6).Trim(); break; }
                    if (aTrim.Length > 0 && !aTrim.StartsWith("---", StringComparison.Ordinal)
                        && !aTrim.StartsWith("#", StringComparison.Ordinal)
                        && oHeadline.Length == 0) oHeadline = aTrim;
                }
                // ⚠ superseded 的 state 仍會被選到（它還在磁碟上）——
                //   那是刻意的：**「被取代」跟「不存在」是兩件事**，而讀的人有權知道最新那筆長什麼樣。
                //   標記由下面的輸出層印出來（檔名或內文含 superseded 時）。
                if (File.ReadAllText(aPath, Encoding.UTF8).IndexOf("superseded",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    oHeadline = "⚠[內容含 superseded 標記] " + oHeadline;
            }
            catch (Exception ex)
            {
                oName = "";
                oHeadline = "讀取失敗：" + ex.Message;
            }
        }

        // ===========================================================
        // 區塊職責：把錨點解讀成**一行可讀的話**（四種答案各自不同形）。
        // 數值影響：純讀。回傳字串一定非空 —— 「沒印」與「沒有記憶」不可以同形。
        // ===========================================================
        public static string Describe(UCL_TaskEntry e)
        {
            if (e == null) return "（沒有單）";
            string aTopic = (e.memory_topic ?? "").Trim();
            string aSha = (e.memory_archived_commit ?? "").Trim();

            if (aTopic.Length == 0)
                return aSha.Length == 0
                    ? "—（沒有掛工作記憶；小單不需要）"
                    : $"⚠ 沒有 `memory_topic` 卻有 `memory_archived_commit={aSha}` —— 兩格不一致，人要看一眼";

            if (!TopicExists(aTopic))
                return aSha.Length > 0
                    ? $"📦 **已歸檔／刪除**：主題 `{aTopic}` 不在磁碟上，紀錄在 commit `{aSha}`"
                        + "（**這不是「沒有記憶」** —— 它在 git 裡，去那顆 commit 找）"
                    : $"⚠ **指向一個不存在的主題** `{aTopic}`，而且沒有歸檔 sha ——"
                        + " 這是「連結壞了」不是「沒有記憶」（要嘛主題被手動刪、要嘛名字打錯）";

            string aStatus = TopicStatus(aTopic);
            LatestState(aTopic, out string aName, out string aHead, out int aDays);
            var sb = new StringBuilder();
            sb.Append($"🧠 `{aTopic}`");
            sb.Append(aStatus.Length == 0 ? "（status 讀不到）" : $"（status=`{aStatus}`）");
            if (aName.Length == 0)
            {
                sb.Append("　⚠ **主題在但沒有任何 `state` fragment** ——");
                sb.Append(" 接手的人拿不到「上次做到哪」（那正是跨多日大 Task 最常死的地方）");
            }
            else
            {
                sb.Append($"　最新 state：`{aName}`");
                sb.Append(aDays < 0 ? "（算不出天數）" : $"（{aDays} 天前）");
                if (aHead.Length > 0) sb.Append($"\n    · {Trunc(aHead, 160)}");
            }
            return sb.ToString();
        }

        static string Trunc(string s, int n)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
#endif
