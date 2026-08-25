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

        // ===========================================================
        // 區塊職責：讀 frontmatter 的**單一欄位**。
        // 物理意義：狀態這種東西**有欄位就讀欄位** —— 整檔 substring 掃描會被內文誤觸發
        //   （一筆內文寫「已把舊的 supersede 掉」的 state 會誤標自己）。
        //   🩸 血證 2026-08-25（TASK-0015 F1）：舊版用整檔掃 "superseded"，
        //     那次矇對只是因為那個字剛好落在 frontmatter 裡。**矇對不是對。**
        // ⚠ 只掃第一個 `---` 到第二個 `---` 之間 —— 內文的 `key: value` 不算數。
        // 數值影響：純讀。找不到（或沒有 frontmatter）回空字串，**不代入預設值**。
        // ===========================================================
        public static string ReadFrontmatterField(string iPath, string iField)
        {
            try
            {
                if (!File.Exists(iPath)) return "";
                var aLines = File.ReadAllLines(iPath, Encoding.UTF8);
                int i = 0;
                while (i < aLines.Length && aLines[i].Trim().Length == 0) ++i;
                if (i >= aLines.Length || aLines[i].Trim() != "---") return ""; // 沒有 frontmatter
                for (++i; i < aLines.Length; ++i)
                {
                    if (aLines[i].Trim() == "---") break;                        // frontmatter 到此為止
                    int c = aLines[i].IndexOf(':');
                    if (c <= 0) continue;
                    if (aLines[i].Substring(0, c).Trim() == iField)
                        return aLines[i].Substring(c + 1).Trim();
                }
            }
            catch { /* 讀不到就回空 —— 空與 active 在輸出上會被分開講 */ }
            return "";
        }

        /// <summary>主題的 `status`（active / archived / …）。讀不到回空字串 —— 不假設 active。</summary>
        public static string TopicStatus(string iTopic)
            => ReadFrontmatterField(Path.Combine(TopicDir(iTopic), "_topic.md"), "status");

        /// <summary>單一 fragment 的 `status`。空字串一律**當成 active**（舊檔沒有這欄）。</summary>
        public static bool IsRetired(string iPath)
        {
            string aStatus = ReadFrontmatterField(iPath, "status");
            return aStatus.Length > 0
                && !aStatus.Equals("active", StringComparison.OrdinalIgnoreCase);
        }

        // ===========================================================
        // 區塊職責：**現行**的 `state` fragment ＋ 它多久沒動。
        // 物理意義：`state` 是「進度快照」那一型 —— 接手時第一眼該看的就是它。
        //   ⚠ 檔名前綴 `state_` 是記憶側 CLI 的慣例（`work_memory.py` 的五型之一）；
        //     這裡**只認前綴不猜語意**，認不出就說認不出。
        //
        // 🩸 血證 2026-08-25（TASK-0015 F1，basecamp 退件）—— 本函式的**選檔判準**曾經是錯的：
        //   舊版用 mtime 排序選「最新那筆」，而 `work_memory.py supersede` 會**重寫舊檔**
        //   （`save_fragment_meta` 把 status 改成 superseded）⇒ **mtime 被刷新**。
        //   一步式（`--new-id`）更毒：新檔先寫、舊檔後改 ⇒ **退場的那筆 mtime 反而比現行的晚**。
        //   ⇒ 於是「一筆 fragment 一旦退場，就變成被選中的那個」，接手的人第一眼讀到作廢的進度。
        //   📌 一般形：**mtime 是「檔案」的新鮮度，`status` 才是「內容」的新鮮度 —— 兩個量，別互相代表。**
        //   實測（探針 `probe-summit-f1`）：磁碟上有一筆 active、一筆 superseded 且後者 mtime 較新，
        //   舊版選了 superseded 那筆，而 active 那筆**完全沒出現在輸出裡**。
        //
        // ⇒ 現行判準：**先用 `status` 篩掉退場的，再在現行的裡面用 mtime 排序。**
        //   （現行 fragment 的 mtime ＝ 它自己的最後編輯時間，那個量在這裡是誠實的。）
        // 數值影響：純讀。oName 為空 ⇒ 沒有現行 state；此時 oRetired > 0 代表
        //   「有 state 但全部退場」，那與「一筆 state 都沒有」是**兩種不同的答案**，不可同形。
        // ===========================================================
        public static void LatestState(string iTopic, out string oName, out string oHeadline,
            out int oDays, out int oRetired)
        {
            oName = ""; oHeadline = ""; oDays = -1; oRetired = 0;
            try
            {
                string aDir = TopicDir(iTopic);
                if (aDir.Length == 0 || !Directory.Exists(aDir)) return;
                var aAll = Directory.GetFiles(aDir, "state_*.md");
                if (aAll.Length == 0) return;

                var aLive = aAll.Where(f => !IsRetired(f))
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).ToList();
                oRetired = aAll.Length - aLive.Count;
                if (aLive.Count == 0) return;   // 有 state 但全退場 —— 交給輸出層講清楚

                string aPath = aLive[0];
                oName = Path.GetFileNameWithoutExtension(aPath);
                oDays = (int)(DateTime.UtcNow - File.GetLastWriteTimeUtc(aPath)).TotalDays;
                // 標題：frontmatter 的 title，沒有就取第一行非空內文
                oHeadline = ReadFrontmatterField(aPath, "title");
                if (oHeadline.Length == 0)
                {
                    foreach (var aLine in File.ReadAllLines(aPath, Encoding.UTF8))
                    {
                        string aTrim = aLine.Trim();
                        if (aTrim.Length > 0 && !aTrim.StartsWith("---", StringComparison.Ordinal)
                            && !aTrim.StartsWith("#", StringComparison.Ordinal))
                        { oHeadline = aTrim; break; }
                    }
                }
            }
            catch (Exception ex)
            {
                oName = "";
                oHeadline = "讀取失敗：" + ex.Message;
            }
        }

        // ===========================================================
        // 區塊職責：主題層讀數（沒有現行 state 時，接手的人靠這幾個數字判斷這主題是死是活）。
        // 物理意義：取代舊版那句「⚠ 拿不到上次做到哪」的警告 —— 見 Describe 的說明。
        // 數值影響：純讀。oDays 為 -1 ⇒ 一個 fragment 都沒有（連 `_topic.md` 以外的檔都沒有）。
        // ===========================================================
        public static void TopicCounts(string iTopic, out int oTotal, out int oDecision,
            out int oPitfall, out int oDays)
        {
            oTotal = 0; oDecision = 0; oPitfall = 0; oDays = -1;
            try
            {
                string aDir = TopicDir(iTopic);
                if (aDir.Length == 0 || !Directory.Exists(aDir)) return;
                var aNewest = DateTime.MinValue;
                foreach (var aPath in Directory.GetFiles(aDir, "*.md"))
                {
                    string aName = Path.GetFileNameWithoutExtension(aPath);
                    if (aName.StartsWith("_", StringComparison.Ordinal)) continue; // `_topic` / `_index` 不是內容
                    ++oTotal;
                    if (aName.StartsWith("decision_", StringComparison.Ordinal)) ++oDecision;
                    else if (aName.StartsWith("pitfall_", StringComparison.Ordinal)) ++oPitfall;
                    var aT = File.GetLastWriteTimeUtc(aPath);
                    if (aT > aNewest) aNewest = aT;
                }
                if (oTotal > 0) oDays = (int)(DateTime.UtcNow - aNewest).TotalDays;
            }
            catch { /* 讀不到就維持 0／-1 —— 不編數字 */ }
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

            // ===========================================================
            // 🩸 血證 2026-08-25（basecamp 複驗第六格）：**呈現的大小聲分配曾經是反的。**
            //   `archive` 保留目錄只改 status（那正是它與 `delete` 的差別 —— 內容還要看得到），
            //   於是產生「主題在磁碟上、但已退場」這個狀態。而舊版對它印的是
            //   `🧠 …（status=archived）` —— 跟 active 那行**只差括號裡一個字**。
            //   反觀大聲的 📦 分支只在 `!TopicExists`（目錄消失＝ `delete` 之後）才觸發。
            //   ⇒ **最常發生的那條路拿到最小聲的呈現，最少發生的拿到最大聲的。**
            //   而讀的人看到的是一個「看起來還活著的主題」，然後照它接手。
            // 📌 一般形：**狀態的差別要反映在「形狀」上，不能只反映在「欄位值」上** ——
            //   欄位值要人去比對才看得出來，而接手的人不會有另一行可以比。
            // ===========================================================
            if (aStatus.Length > 0 && !aStatus.Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                string aCard = Path.Combine(TopicDir(aTopic), "_topic.md");
                string aMemSha = ReadFrontmatterField(aCard, "archived_commit");
                string aWhen = ReadFrontmatterField(aCard, "archived_at");
                TopicCounts(aTopic, out int aN, out int aD, out int aP, out _);

                var ab = new StringBuilder();
                ab.Append($"📦 **已退場（status=`{aStatus}`）**：`{aTopic}`");
                ab.Append(" —— 目錄還在、內容讀得到，但**沒有人在維護它**");
                ab.Append($"\n    · {aN} 筆（decision {aD}／pitfall {aP}）");
                if (aWhen.Length > 0) ab.Append($"　退場於 {aWhen}");
                ab.Append(aMemSha.Length == 0
                    ? "\n    · ⚠ 記憶側沒寫 `archived_commit` —— 拿不到「退場當下那一版」的定位點"
                    : $"\n    · 記憶側 `archived_commit`：`{aMemSha}`");
                // 契約①：Task 側那格由 Cmd_Task 寫，本檔只讀 —— 兩邊不一致只印不修。
                if (aSha.Length == 0)
                    ab.Append("\n    · ⚠ 本單的 `memory_archived_commit` 是空的"
                        + "（記憶側已退場而單子還不知道）⇒ `op=update --arg memory_archived_commit=<sha>`");
                else if (aMemSha.Length > 0 && !string.Equals(aSha, aMemSha, StringComparison.OrdinalIgnoreCase))
                    ab.Append($"\n    · ⚠ 兩邊的 sha 不一致（單子 `{aSha}` vs 記憶側 `{aMemSha}`）—— 人要看一眼");
                ab.Append("\n    · ⛔ 接手前先確認這份還適不適用：**它退場了，不是「還在做」**");
                return ab.ToString();
            }

            LatestState(aTopic, out string aName, out string aHead, out int aDays, out int aRetired);
            var sb = new StringBuilder();
            sb.Append($"🧠 `{aTopic}`");
            sb.Append(aStatus.Length == 0 ? "（status 讀不到）" : $"（status=`{aStatus}`）");

            if (aName.Length > 0)
            {
                sb.Append($"　最新 state：`{aName}`");
                sb.Append(aDays < 0 ? "（算不出天數）" : $"（{aDays} 天前）");
                if (aRetired > 0) sb.Append($"　[另有 {aRetired} 筆已退場，未列入]");
                if (aHead.Length > 0) sb.Append($"\n    · {Trunc(aHead, 160)}");
            }
            else if (aRetired > 0)
            {
                // 第五種答案：有 state 但**全部退場** —— 跟「一筆都沒有」不同形。
                // 這一格才是真的該亮燈：上一手把進度作廢了而沒有補新的。
                sb.Append($"　⚠ **{aRetired} 筆 `state` 全部已退場（superseded）** —— 沒有現行進度快照；");
                sb.Append("接手前要嘛去 git 史讀那幾筆，要嘛請上一手補一筆");
            }
            else
            {
                // ⚠ 主題乾淨（沒有任何 `state`）**不是異常，是拍板後的正確形狀** ——
                //   Tim 2026-08-24：「進度由 Task 本身紀錄，記憶不額外記進度」。
                //   🩸 舊版在這裡印「⚠ 接手的人拿不到上次做到哪」＝**把合規印成警告**。
                //   而誤報的代價跟漏報一樣真：一個天天亮的警示第三天就沒有人讀了，
                //   然後**真的壞掉那天它也還亮著**。⇒ 改印主題層讀數，讓人自己判斷死活。
                TopicCounts(aTopic, out int aTotal, out int aDec, out int aPit, out int aTouched);
                if (aTotal == 0)
                {
                    sb.Append("　主題已建立但**還沒有任何 fragment**（剛 init，尚未寫入內容）");
                }
                else
                {
                    sb.Append($"　{aTotal} 筆（decision {aDec}／pitfall {aPit}）");
                    sb.Append(aTouched < 0 ? "" : $"　最後更新 {aTouched} 天前");
                    sb.Append("　·「上次做到哪」看本單時間線，不看記憶（進度不進記憶）");
                }
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
