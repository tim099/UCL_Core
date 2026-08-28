// 區塊職責：任務單的磁碟層 —— index 配發 / 單檔讀寫 / 清單 / 依賴雙向寫入 / stale 與 blocker 讀數。
//
// ⚠⚠ **這個檔的併發安全來自「單一主執行緒 ＋ read-modify-write 中間沒有 `await`」，
//     不是來自任何鎖。** 這裡沒有鎖，而且是刻意沒有（TASK-0026，2026-08-25）。
//   讀數：三個 persona 同時 `op=create` ⇒ 3 檔連號零空洞；兩人同秒 `op=comment` ⇒ 兩則都在。
//   ⇒ 所以**加一把守不到東西的鎖**被判為有害：它會讓下一個人不再去問這裡到底安不安全。
//   ⛔ 要動多執行緒／多 process 的人請從這一段開始讀：**前提一旦破，症狀是靜默的**
//     （整檔覆蓋、留言消失、index 撞號 —— 沒有一格會紅）。
// 物理意義：AgentCommands/Tasks/ 底下的唯一寫入端。Cmd 與後台頁都走這裡，不各自碰檔案。
//
// 📌 **一單一檔**（照 BugReport 的母版，Tim 2026-08-18 拍板的形狀）：
//    共用的 append-only 檔是 git 衝突的磁鐵 —— 兩個人同時開單就是同一行尾端的 conflict。
//    一單一檔之後同時開單是**兩個新檔案**，git 不需要合併任何東西。
//    ⇒ 事實來源＝`tasks/<index>.md`；**沒有第二份索引**。
//
// 📌 **整檔重寫，但歷史留在檔內**（`## 活動與討論時間線` 逐行 append）——
//    一單一檔之後沒有共用事件流可以放歷史，而歷史不能丟：
//    「這張單什麼時候被誰推到哪一格」是事後對帳唯一的依據。
//
// 數值影響：純檔案 IO。index 配發含自我修復（計數檔落後於磁碟時拉齊 ＋ 大聲喊）。
// 設計沿革：Plan_Task_Management_System.md（Tim 2026-08-24 拍板）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.TaskMgmt
{
    public static class UCL_TaskIO
    {
        /// <summary>InProgress 超過這個天數沒動 ⇒ stale。與 BugReport 同一個數字，刻意不另開一個旋鈕。</summary>
        public const int STALE_DAYS = 14;

        public static string Dir => Path.Combine(UCL_AgentCommandsPath.DataRoot, "Tasks");
        public static string IndexPath => Path.Combine(Dir, "_index.txt");
        public static string TasksDir => Path.Combine(Dir, "tasks");
        public static string LastReportPath => Path.Combine(Dir, "_last_task_report.md");

        // ⚠ `epics/` 與 `milestones/` **兩個目錄刻意不建立** ——
        //   建了空目錄的話，下一個人看到 `epics/` 會以為 Epic 這件事已經在運作，
        //   而空目錄跟「還沒有人建 Epic」長得一模一樣（🩸 別造一個名字比事實大的東西）。
        //
        // 🩸 而**「目錄沒建」≠「欄位沒生效」，我自己把這兩件事講成同一件**
        //   （basecamp PM 對帳 2026-08-24，酒館 seq 13527 抓到）：
        //   我在回傳檔與 commit 訊息裡寫「epic_id / milestone / related_to 三格沒有讀取端」，
        //   而實際上 —— **三格裡有兩格是活的**：
        //     · `milestone`  ✅ **有讀取端**：`OpList` 真的套 Where 篩選、`OpUpdate` 可改
        //     · `related_to` ✅ **有讀取端**：`OpShow` / `OpLink` 會印它、`op=link` 能雙向寫
        //     · `epic_id`    ⛔ 只有 `create` 一個寫入端（`Cmd_Task.cs:127`），**沒有讀取端**（這格我沒講錯）
        //     · `tags`       ⛔ 同上（`Cmd_Task.cs:131`）—— 寫得進去、查不出來
        //       ⇒ 追蹤主 Task 目前**只有人眼**；`op=list --arg tag=` 排在 TASK-0009（basecamp）
        //   （以上四格是 **grep 出來的**，不是憑「我記得我寫過什麼」——
        //     憑記憶正是上面那個低報的成因）
        //   ⇒ 這是「訊息比事實小」那一族：低報讓能力隱形 ——
        //     讀說明的人以為那個功能不存在，於是繞道、或再實作一次。
        //     高報會在第一次使用時當場失敗（它自己會叫），低報不會叫。
        //   ⇒ 判準：宣告「這格沒有讀者」之前，**去 grep 那個欄位名**，不要憑「我記得我沒寫」。

        public static void EnsureDir()
        {
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(TasksDir);
        }

        /// <summary>單張任務的路徑（檔名補零只為排序好看；內容以 frontmatter 的整數 index 為準）。</summary>
        public static string TaskPath(int iIndex)
            => Path.Combine(TasksDir, iIndex.ToString("0000", CultureInfo.InvariantCulture) + ".md");

        // ===========================================================
        // 區塊職責：配發下一個 index（1 起，單調遞增）。
        // 物理意義：計數檔存「**已發出的最後一個**」，初始 0 ⇒ 第一張是 1。
        //   1-based 讓 `0` 同時是合法初始值與 `int.TryParse` 失敗的回退值（兩者一致，解析失敗不撞號）。
        // 🩸 自我修復的判準是 **`>` 不是 `>=`**（BugReport 2026-08-18 的血證，照抄）：
        //   正常配發完 N 之後，計數檔＝N、磁碟最大檔案也＝N ⇒ **相等是正常狀態**。
        //   用 `>=` 會讓第二張單開始每次都噴 LogError。
        //   只有「磁碟上出現我從沒發過的號」（嚴格大於）才是真的有人繞過 Cmd 直接建檔。
        // 數值影響：一次讀計數檔 ＋ 一次列目錄；寫回計數檔。
        // ===========================================================
        public static int IncrementAndGetIndex()
        {
            EnsureDir();
            int aCounter = ReadCurrentIndex();
            int aDiskMax = ReadMaxIndexOnDisk();
            if (aDiskMax > aCounter)
            {
                Debug.LogError(
                    $"[Task] ⚠️ 偵測到繞過 Cmd 建立的單：tasks/ 最大 index={aDiskMax} > 計數檔={aCounter}。" +
                    $" 自動把計數檔拉齊到 {aDiskMax}（避免後續 index 撞號）。" +
                    $" 開單一律走 Cmd_Task，不要手建 tasks/*.md。");
                aCounter = aDiskMax;
            }
            int aNext = aCounter + 1;
            File.WriteAllText(IndexPath, aNext.ToString(CultureInfo.InvariantCulture), new UTF8Encoding(false));
            return aNext;
        }

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

        /// <summary>磁碟上實際存在的最大 index。**檔案就是事實**，計數檔只是快取。</summary>
        public static int ReadMaxIndexOnDisk()
        {
            if (!Directory.Exists(TasksDir)) return 0;
            int aMax = 0;
            foreach (var aPath in Directory.GetFiles(TasksDir, "*.md"))
            {
                string aName = Path.GetFileNameWithoutExtension(aPath);
                if (int.TryParse(aName, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > aMax)
                    aMax = v;
            }
            return aMax;
        }

        // ===========================================================
        // 區塊職責：本單檔「頂層區塊」的完整清單 —— 區塊邊界只認這幾個標題。
        // 🩸 BUG-7（BugReport 同族）：邊界若認「任何 `## `」，內文自己的小節標題就會被當成區塊結束，
        //    於是狀態一變更（＝整檔重寫 → 撈舊內文）內容就靜默變空，而單子看起來像從沒填過。
        // 數值影響：純字串比對；清單要與 Save() 實際寫出的標題**逐字一致**（改一邊要改另一邊）。
        // ===========================================================
        static readonly string[] SECTION_HEADINGS =
        {
            "## 驗收標準", "## 任務描述", "## 結單說明", "## 留言", "## 活動與討論時間線",
        };

        // ===========================================================
        // 區塊職責：留言的**唯一表示法** —— `### 💬 #<id>　<persona>　<iso>`。
        // 物理意義：留言區要能被機器認出「一則的邊界」與「是誰說的」，同時人讀得懂。
        //   ⇒ 只用這一行當標記，**不另加 HTML 註解** —— 兩種標記就是兩份真相，而它們會漂
        //     （🩸 這個 repo 最貴的錯誤形狀：同一件事有兩份，而併起來才知道原本有幾個）。
        // 數值影響：解析靠這條 regex；寫回靠 CommentHeader()。**改一邊要改另一邊**，所以放在一起。
        // ===========================================================
        static readonly System.Text.RegularExpressions.Regex COMMENT_HEAD = new System.Text.RegularExpressions.Regex(
            @"^###\s+💬\s+#(?<id>\d+)\s+(?<persona>\S+)\s+(?<at>\S+)\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        static string CommentHeader(UCL_TaskComment c)
            => $"### 💬 #{c.id} {c.persona} {c.at}";

        // ===========================================================
        // 區塊職責：留言內文裡「看起來像區塊標題」的行要逃脫。
        //
        // 🩸 2026-08-24 實測（我自己下的探針）：一則留言的內文中間有一行是 `## 驗收標準`，
        //    下一次寫入（整檔重寫 → 重新解析）時 parser 在那一行判定「區塊結束」⇒
        //    **那則留言的第三行永久消失，而檔案看起來完全正常**。
        //    ⇒ 這是本 repo 最貴的形狀：一次寫入之後靜默丟資料，沒有任何一層會喊。
        //
        // 修法：寫入時在行首的 `#` 前加一個 `\`（markdown 的字面轉義，渲染仍是 `#`），
        //   讀取時脫掉。**兩個方向都在這裡**，所以改一邊必然看到另一邊。
        // ⚠ 為什麼不改成「更聰明的邊界判定」：那要猜「這個 `##` 是留言內容還是區塊標題」，
        //   而猜錯的兩種結果都是靜默的。逃脫是把歧義**消掉**，不是把它判對。
        // ===========================================================
        static string EscapeCommentLine(string iLine)
            => iLine.TrimStart().StartsWith("#", StringComparison.Ordinal) ? "\\" + iLine : iLine;

        static string UnescapeCommentLine(string iLine)
            => iLine.StartsWith("\\#", StringComparison.Ordinal) ? iLine.Substring(1) : iLine;

        // ===========================================================
        // 區塊職責：寫一張單（新建 / 狀態變更 / 留言皆走這裡）。
        // 數值影響：一次讀（撈既有歷史與內文）＋ 一次寫。iActivityLine 為空＝不追加時間線。
        // ===========================================================
        // ===========================================================
        // 區塊職責：把「寫入必須發生在主執行緒」這個**前提**變成會出聲的東西。
        // 物理意義：本檔沒有鎖（見檔頭）。整檔重寫的安全性完全建立在
        //   「所有寫入都在同一條執行緒、且 read-modify-write 中間沒有 yield 點」之上。
        //   ⇒ 那是一個**沒有任何機械在保護的前提**，而它破掉的時候症狀是靜默的。
        //   🩸 2026-08-25：`UCL_TaskWorkMemoryCli.cs:74` 有一個 `await Task.Run(...)`，
        //     由 `Cmd_Task.OpWrapup` 呼叫 —— 它**目前**站在 `Save` 之後，所以安全。
        //     而「站在哪一邊」是一次程式碼搬移就會改變的事。**這行斷言就是那次搬移的告警。**
        // ⚠ 只出聲**不丟例外**：寫入本身仍然照做。
        //   丟例外會把「前提破了」變成「使用者的操作失敗」——
        //   而那會逼下一個人把斷言拿掉，不是去修前提。
        // 數值影響：正常路徑零成本（一次 int 比較），且**不改變任何行為**。
        // ===========================================================
        // ⚠ 定錨方式刻意**不是**「第一次寫入時記下當前執行緒」：
        //   那樣的話，萬一第一次寫入本身就在非主執行緒上，它會錨到錯的那條，
        //   然後**反過來對所有正確的呼叫誤報** —— 一個會誤報的告警活不過三天。
        //   ⇒ 走 `[InitializeOnLoadMethod]`（Unity 保證在主執行緒跑，本 repo 既有慣例）。
        // 邊界：錨沒設成（理論上不該發生）⇒ 這道斷言**停用但出聲一次**。
        //   🩸 這一格是我自己第一版犯的：原本寫「錨沒設成就 return」，於是
        //   「錨對上了」與「根本沒在量」**在輸出上完全同形（都是安靜）**——
        //   而那正是本檔今天修的一整族（找不到 vs 不存在、被 ignore vs 乾淨）。
        //   ⇒ 「量不到」不可以長得像「量到了而且正常」。出聲一次，不重複洗版。
        static int s_MainThreadId = -1;
        static bool s_WarnedNoAnchor = false;

        [UnityEditor.InitializeOnLoadMethod]
        static void AnchorMainThread()
            => s_MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;

        static void AssertMainThread(string iWho, int iIndex)
        {
            int aNow = System.Threading.Thread.CurrentThread.ManagedThreadId;
            if (s_MainThreadId < 0)
            {
                if (!s_WarnedNoAnchor)
                {
                    s_WarnedNoAnchor = true;
                    Debug.LogWarning("[Task] 主執行緒錨沒設成（InitializeOnLoadMethod 沒跑到）⇒"
                        + " **併發前提這一輪沒有人在量**。這不是「安全」，是「沒有讀數」。");
                }
                return;
            }
            if (aNow == s_MainThreadId) return;
            Debug.LogError(
                $"[Task] ⚠️ {iWho}(index={iIndex}) 跑在**非主執行緒**上（tid={aNow}，主={s_MainThreadId}）。"
                + " 本檔沒有鎖 —— 併發安全完全依賴『單一主執行緒 ＋ RMW 中間沒有 await』，"
                + " 而這行讀數說那個前提已經破了。"
                + " ⇒ 去看是誰在 read-modify-write 中間加了 await（最可能是 Cmd_Task 的某個 Op），"
                + " 把它移到 Save 之後；或者這裡真的需要鎖了，那要開一張新單而不是把這行拿掉。");
        }

        public static void Save(UCL_TaskEntry e, string iCriteria, string iDescription, string iActivityLine)
        {
            AssertMainThread(nameof(Save), e?.index ?? -1);
            EnsureDir();
            string aPath = TaskPath(e.index);

            var aTimeline = new List<string>();
            if (File.Exists(aPath))
            {
                bool aIn = false;
                foreach (var aLine in File.ReadAllLines(aPath, Encoding.UTF8))
                {
                    if (aLine.StartsWith("## 活動與討論時間線", StringComparison.Ordinal)) { aIn = true; continue; }
                    if (aIn && IsSectionHeading(aLine)) aIn = false;
                    if (aIn && aLine.TrimStart().StartsWith("- ", StringComparison.Ordinal)) aTimeline.Add(aLine.Trim());
                }
                // 留言：呼叫端沒帶（e.comments 空）時從磁碟撈回來 ——
                // 整檔重寫會蓋掉它，而它跟時間線一樣是不可重建的
                if (e.comments.Count == 0) e.comments = ReadComments(aPath);
                // 內文欄位沒給就沿用既有的 —— 狀態變更不必重打驗收標準與描述
                if (string.IsNullOrEmpty(iCriteria)) iCriteria = ReadSection(aPath, "## 驗收標準");
                if (string.IsNullOrEmpty(iDescription)) iDescription = ReadSection(aPath, "## 任務描述");
            }
            if (!string.IsNullOrEmpty(iActivityLine)) aTimeline.Add("- " + iActivityLine);

            var sb = new StringBuilder();
            sb.Append("---\n");
            sb.Append($"index: {e.index}\n");
            sb.Append($"id: {e.Id}\n");
            sb.Append($"type: {e.type}\n");
            sb.Append($"priority: {e.priority}\n");
            sb.Append($"status: {e.status}\n");
            sb.Append($"title: {OneLine(e.title)}\n");
            sb.Append($"reporter: {OneLine(e.reporter)}\n");
            sb.Append("participants:\n");
            foreach (var p in e.participants)
            {
                sb.Append($"  - persona: {OneLine(p.persona)}\n");
                sb.Append($"    role: {p.role}\n");
                sb.Append($"    assigned_at: {OneLine(p.assigned_at)}\n");
            }
            sb.Append($"milestone: {OneLine(e.milestone)}\n");
            sb.Append($"epic_id: {OneLine(e.epic_id)}\n");
            sb.Append($"blocked_by: {IntList(e.blocked_by)}\n");
            sb.Append($"blocks: {IntList(e.blocks)}\n");
            sb.Append($"related_to: {IntList(e.related_to)}\n");
            sb.Append($"subtask_indices: {IntList(e.subtask_indices)}\n");
            sb.Append($"tags: {StrList(e.tags)}\n");
            sb.Append($"commit_shas: {StrList(e.commit_shas)}\n");
            sb.Append($"created_at: {e.created_at}\n");
            sb.Append($"updated_at: {e.updated_at}\n");
            sb.Append($"closed_at: {e.closed_at}\n");
            sb.Append($"last_wrapup_at: {e.last_wrapup_at}\n");
            sb.Append($"memory_topic: {OneLine(e.memory_topic)}\n");
            sb.Append($"memory_archived_commit: {OneLine(e.memory_archived_commit)}\n");
            sb.Append("---\n\n");

            sb.Append($"# {e.Id} — {e.title}\n\n");
            sb.Append($"> `{e.type}` / `{e.priority}` / `{e.status}`　開單：{Nz2(e.reporter)}");
            if (e.participants.Count > 0)
            {
                sb.Append("　參與：");
                for (int i = 0; i < e.participants.Count; i++)
                {
                    if (i > 0) sb.Append("、");
                    sb.Append($"{e.participants[i].persona}({e.participants[i].role})");
                }
            }
            else
            {
                // 沒有人被指派時**明說**，而不是印一片空白 ——
                // 空白看起來像「還沒填」，而這一格的意思是「現在沒有人在做這件事」
                sb.Append("　⚠ **尚無參與者**（沒有人在做這件事）");
            }
            sb.Append("\n\n");
            sb.Append("## 驗收標準\n\n").Append(Nz(iCriteria)).Append("\n\n");
            sb.Append("## 任務描述\n\n").Append(Nz(iDescription)).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(e.resolution_note))
                sb.Append("## 結單說明\n\n").Append(e.resolution_note).Append("\n\n");

            // 留言區 —— 一則一個 `### 💬` 標頭 ＋ 內文。空的時候也印區塊標題，
            // 因為「這張單沒有人討論過」跟「這個區塊不存在」不是同一件事。
            sb.Append("## 留言\n\n");
            if (e.comments.Count == 0)
            {
                sb.Append("_(還沒有人留言)_\n\n");
            }
            else
            {
                foreach (var c in e.comments)
                {
                    sb.Append(CommentHeader(c)).Append('\n');
                    foreach (var aLine in (c.body ?? "").TrimEnd().Replace("\r", "").Split('\n'))
                        sb.Append(EscapeCommentLine(aLine)).Append('\n');
                    sb.Append('\n');
                }
            }
            sb.Append("## 活動與討論時間線\n\n");
            foreach (var h in aTimeline) sb.Append(h).Append('\n');
            File.WriteAllText(aPath, sb.ToString(), new UTF8Encoding(false));
        }

        public static List<UCL_TaskEntry> LoadAll()
        {
            var aList = new List<UCL_TaskEntry>();
            if (!Directory.Exists(TasksDir)) return aList;
            foreach (var aPath in Directory.GetFiles(TasksDir, "*.md"))
            {
                var e = LoadFile(aPath);
                if (e != null) aList.Add(e);
            }
            aList.Sort((a, b) => a.index.CompareTo(b.index));
            return aList;
        }

        public static UCL_TaskEntry Find(int iIndex)
        {
            string aPath = TaskPath(iIndex);
            return File.Exists(aPath) ? LoadFile(aPath) : null;
        }

        // ===========================================================
        // 區塊職責：解析一張單的 frontmatter。
        // ⚠ participants 是**巢狀清單**，所以這裡是手寫的極簡 YAML 子集 parser：
        //   只認 `  - persona:` / `    role:` / `    assigned_at:` 三種縮排行。
        //   認不得的行**跳過但不吞掉整張單** —— 一個雜鍵不該讓一張單消失。
        // ===========================================================
        static UCL_TaskEntry LoadFile(string iPath)
        {
            try
            {
                var e = new UCL_TaskEntry();
                bool aIn = false;
                UCL_TaskParticipant aCur = null;
                foreach (var aLine in File.ReadAllLines(iPath, Encoding.UTF8))
                {
                    if (aLine.StartsWith("---", StringComparison.Ordinal))
                    {
                        if (!aIn) { aIn = true; continue; }
                        break;
                    }
                    if (!aIn) continue;

                    string aTrim = aLine.TrimStart();
                    if (aTrim.StartsWith("- persona:", StringComparison.Ordinal))
                    {
                        aCur = new UCL_TaskParticipant { persona = After(aTrim, "- persona:") };
                        if (aCur.persona.Length > 0) e.participants.Add(aCur);
                        continue;
                    }
                    if (aCur != null && aTrim.StartsWith("role:", StringComparison.Ordinal))
                    { aCur.role = UCL_TaskWire.ParseOr(After(aTrim, "role:"), UCL_TaskRole.dev, $"{iPath} participants.role"); continue; }
                    if (aCur != null && aTrim.StartsWith("assigned_at:", StringComparison.Ordinal))
                    { aCur.assigned_at = After(aTrim, "assigned_at:"); continue; }

                    if (aLine.StartsWith(" ", StringComparison.Ordinal)) continue;   // 其餘縮排行不是頂層鍵
                    int c = aLine.IndexOf(':');
                    if (c <= 0) continue;
                    string k = aLine.Substring(0, c).Trim();
                    string v = aLine.Substring(c + 1).Trim();
                    switch (k)
                    {
                        case "index": int.TryParse(v, out e.index); break;
                        case "type": e.type = UCL_TaskWire.ParseOr(v, UCL_TaskType.feature, $"{iPath} type"); break;
                        case "priority": e.priority = UCL_TaskWire.ParseOr(v, UCL_TaskPriority.normal, $"{iPath} priority"); break;
                        case "status":
                            e.status = UCL_TaskWire.ParseOr(v, UCL_TaskStatus.todo, $"{iPath} status");
                            // `all` / `open` 是篩選成員不是狀態 —— 落盤檔帶著它們＝壞檔，一樣出聲退回 todo
                            if (e.status == UCL_TaskStatus.all || e.status == UCL_TaskStatus.open)
                            {
                                UnityEngine.Debug.LogError($"[Task] {iPath} status: `{v}` 是篩選成員不是狀態 —— 落回 `todo`，去修單檔 frontmatter");
                                e.status = UCL_TaskStatus.todo;
                            }
                            break;
                        case "title": e.title = v; break;
                        case "reporter": e.reporter = v; break;
                        case "milestone": e.milestone = v; break;
                        case "epic_id": e.epic_id = v; break;
                        case "blocked_by": e.blocked_by = ParseIntList(v); break;
                        case "blocks": e.blocks = ParseIntList(v); break;
                        case "related_to": e.related_to = ParseIntList(v); break;
                        case "subtask_indices": e.subtask_indices = ParseIntList(v); break;
                        case "tags": e.tags = ParseStrList(v); break;
                        case "commit_shas": e.commit_shas = ParseStrList(v); break;
                        case "created_at": e.created_at = v; break;
                        case "updated_at": e.updated_at = v; break;
                        case "last_wrapup_at": e.last_wrapup_at = v; break;
                        case "closed_at": e.closed_at = v; break;
                        case "memory_topic": e.memory_topic = v; break;
                        case "memory_archived_commit": e.memory_archived_commit = v; break;
                        case "participants": aCur = null; break;
                    }
                }
                if (e.index <= 0) return null;
                e.comments = ReadComments(iPath);
                return e;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Task] 讀取失敗，跳過：{iPath}（{ex.Message}）");
                return null;
            }
        }

        // ===========================================================
        // 區塊職責：依賴關係的**雙向**寫入。
        // 物理意義：`A.blocked_by += B` 必須同時 `B.blocks += A`。
        // 🩸 單向寫入是**靜默錯**：從 A 看得到「我被 B 卡住」，從 B 完全看不出「我卡住了誰」——
        //    而「我卡住了誰」正是催 B 的人唯一的依據。兩邊都寫，或兩邊都不寫。
        // 數值影響：兩次讀 ＋ 兩次寫（各自 append 一行時間線）。回傳是否真的有變動。
        // ===========================================================
        public static bool Link(int iIndex, int iTarget, string iKind, string iActor, out string oError)
        {
            oError = "";
            if (iIndex == iTarget) { oError = "不能把一張單連到自己"; return false; }
            var a = Find(iIndex);
            var b = Find(iTarget);
            if (a == null) { oError = $"TASK-{iIndex} 不存在"; return false; }
            if (b == null) { oError = $"TASK-{iTarget} 不存在"; return false; }

            string aNow = NowUtc();
            bool aChanged = false;
            if (string.Equals(iKind, "blocked_by", StringComparison.OrdinalIgnoreCase))
            {
                aChanged |= AddOnce(a.blocked_by, iTarget);
                aChanged |= AddOnce(b.blocks, iIndex);
                if (aChanged)
                {
                    Touch(a, aNow); Touch(b, aNow);
                    Save(a, "", "", $"{aNow}　`link`　{iActor} 標記被 {b.Id} 阻塞");
                    Save(b, "", "", $"{aNow}　`link`　{iActor} 標記它阻塞了 {a.Id}");
                }
            }
            else if (string.Equals(iKind, "blocks", StringComparison.OrdinalIgnoreCase))
            {
                aChanged |= AddOnce(a.blocks, iTarget);
                aChanged |= AddOnce(b.blocked_by, iIndex);
                if (aChanged)
                {
                    Touch(a, aNow); Touch(b, aNow);
                    Save(a, "", "", $"{aNow}　`link`　{iActor} 標記它阻塞了 {b.Id}");
                    Save(b, "", "", $"{aNow}　`link`　{iActor} 標記被 {a.Id} 阻塞");
                }
            }
            // ===========================================================
            // 區塊職責：父子關係（主 Task ↔ 子任務）。
            // 物理意義：**兩個欄位一起寫才叫一個關係** ——
            //   子的 `epic_id` 指向父（`TASK-0008` 這種字串），父的 `subtask_indices` 收子的號碼。
            //   只寫一邊的話：從父看不到子（追蹤斷）或從子看不到父（接手時不知道自己屬於哪條線），
            //   而兩種殘缺都不會報錯。
            // 🩸 這也是 `epic_id` 第一個**寫入端以外的意義**：在此之前它只有 create 能填、沒人讀，
            //   而 basecamp PM 對帳（seq 13527）點名它「寫得進查不出來」。
            // ===========================================================
            else if (string.Equals(iKind, "subtask_of", StringComparison.OrdinalIgnoreCase))
            {
                if (a.epic_id != b.Id) { a.epic_id = b.Id; aChanged = true; }
                aChanged |= AddOnce(b.subtask_indices, iIndex);
                if (aChanged)
                {
                    Touch(a, aNow); Touch(b, aNow);
                    Save(a, "", "", $"{aNow}　`link`　{iActor} 標記它是 {b.Id} 的子任務（epic_id={b.Id}）");
                    Save(b, "", "", $"{aNow}　`link`　{iActor} 收 {a.Id} 為子任務");
                }
            }
            else if (string.Equals(iKind, "has_subtask", StringComparison.OrdinalIgnoreCase))
            {
                aChanged |= AddOnce(a.subtask_indices, iTarget);
                if (b.epic_id != a.Id) { b.epic_id = a.Id; aChanged = true; }
                if (aChanged)
                {
                    Touch(a, aNow); Touch(b, aNow);
                    Save(a, "", "", $"{aNow}　`link`　{iActor} 收 {b.Id} 為子任務");
                    Save(b, "", "", $"{aNow}　`link`　{iActor} 標記它是 {a.Id} 的子任務（epic_id={a.Id}）");
                }
            }
            else if (string.Equals(iKind, "related_to", StringComparison.OrdinalIgnoreCase))
            {
                aChanged |= AddOnce(a.related_to, iTarget);
                aChanged |= AddOnce(b.related_to, iIndex);
                if (aChanged)
                {
                    Touch(a, aNow); Touch(b, aNow);
                    Save(a, "", "", $"{aNow}　`link`　{iActor} 關聯 {b.Id}");
                    Save(b, "", "", $"{aNow}　`link`　{iActor} 關聯 {a.Id}");
                }
            }
            else
            {
                oError = $"認不得的關聯種類 '{iKind}'"
                    + "（blocked_by|blocks|subtask_of|has_subtask|related_to）";
                return false;
            }
            return aChanged;
        }

        // ===========================================================
        // 區塊職責：解除關聯 —— Link 的**雙向對稱**反操作（TASK-0033 ②）。
        // 物理意義：link 只能建不能解 ⇒ 掛錯或探針用的關聯**永久留在對方單子上**，
        //   而「探針用完當場標記」是慣例 ⇒ 每張有掛關聯的探針都留一行拆不掉的殘骸。
        //   （blocker 閘只認未關單 ⇒ 殘骸不影響判定，影響的是讀的人看到什麼 —— 可讀性帳，不是正確性帳。）
        // ⚠ 解除要留時間線紀錄 —— **移除關聯是一個決定，不是打掃**；
        //   兩張單各留一筆 `unlink`，跟 Link 的雙寫完全對稱（單向移除是靜默錯的鏡像）。
        // 數值影響：回傳「有沒有真的拆掉東西」；關聯本來就不存在 ⇒ false 且不寫檔（跟 Link 的冪等同形）。
        // ===========================================================
        public static bool Unlink(int iIndex, int iTarget, string iKind, string iActor, out string oError)
        {
            oError = "";
            if (iIndex == iTarget) { oError = "不能對自己解除關聯"; return false; }
            var a = Find(iIndex);
            var b = Find(iTarget);
            if (a == null) { oError = $"TASK-{iIndex} 不存在"; return false; }
            if (b == null) { oError = $"TASK-{iTarget} 不存在"; return false; }

            string aNow = NowUtc();
            bool aChanged = false;
            if (string.Equals(iKind, "blocked_by", StringComparison.OrdinalIgnoreCase))
            {
                aChanged |= a.blocked_by.Remove(iTarget);
                aChanged |= b.blocks.Remove(iIndex);
                if (aChanged)
                {
                    Touch(a, aNow); Touch(b, aNow);
                    Save(a, "", "", $"{aNow}　`unlink`　{iActor} 解除「被 {b.Id} 阻塞」");
                    Save(b, "", "", $"{aNow}　`unlink`　{iActor} 解除「它阻塞了 {a.Id}」");
                }
            }
            else if (string.Equals(iKind, "blocks", StringComparison.OrdinalIgnoreCase))
            {
                aChanged |= a.blocks.Remove(iTarget);
                aChanged |= b.blocked_by.Remove(iIndex);
                if (aChanged)
                {
                    Touch(a, aNow); Touch(b, aNow);
                    Save(a, "", "", $"{aNow}　`unlink`　{iActor} 解除「它阻塞了 {b.Id}」");
                    Save(b, "", "", $"{aNow}　`unlink`　{iActor} 解除「被 {a.Id} 阻塞」");
                }
            }
            else if (string.Equals(iKind, "subtask_of", StringComparison.OrdinalIgnoreCase))
            {
                if (a.epic_id == b.Id) { a.epic_id = ""; aChanged = true; }
                aChanged |= b.subtask_indices.Remove(iIndex);
                if (aChanged)
                {
                    Touch(a, aNow); Touch(b, aNow);
                    Save(a, "", "", $"{aNow}　`unlink`　{iActor} 解除「它是 {b.Id} 的子任務」（epic_id 清空）");
                    Save(b, "", "", $"{aNow}　`unlink`　{iActor} 移出子任務 {a.Id}");
                }
            }
            else if (string.Equals(iKind, "has_subtask", StringComparison.OrdinalIgnoreCase))
            {
                aChanged |= a.subtask_indices.Remove(iTarget);
                if (b.epic_id == a.Id) { b.epic_id = ""; aChanged = true; }
                if (aChanged)
                {
                    Touch(a, aNow); Touch(b, aNow);
                    Save(a, "", "", $"{aNow}　`unlink`　{iActor} 移出子任務 {b.Id}");
                    Save(b, "", "", $"{aNow}　`unlink`　{iActor} 解除「它是 {a.Id} 的子任務」（epic_id 清空）");
                }
            }
            else if (string.Equals(iKind, "related_to", StringComparison.OrdinalIgnoreCase))
            {
                aChanged |= a.related_to.Remove(iTarget);
                aChanged |= b.related_to.Remove(iIndex);
                if (aChanged)
                {
                    Touch(a, aNow); Touch(b, aNow);
                    Save(a, "", "", $"{aNow}　`unlink`　{iActor} 解除與 {b.Id} 的關聯");
                    Save(b, "", "", $"{aNow}　`unlink`　{iActor} 解除與 {a.Id} 的關聯");
                }
            }
            else
            {
                oError = $"認不得的關聯種類 '{iKind}'"
                    + "（blocked_by|blocks|subtask_of|has_subtask|related_to）";
                return false;
            }
            return aChanged;
        }

        // ===========================================================
        // 區塊職責：**還沒關掉的 blocker 清單** —— 結單守衛的唯一輸入。
        // 物理意義：`blocked_by` 裡指到的單只要還沒關，這張就不准推 Done（機械攔截，不是提醒）。
        //   ⚠ 指到一張**不存在**的單也算未解 —— 「查不到」不等於「已經解決」。
        // ===========================================================
        public static List<string> OpenBlockers(UCL_TaskEntry e)
        {
            var aOut = new List<string>();
            if (e == null) return aOut;
            foreach (int i in e.blocked_by)
            {
                var b = Find(i);
                if (b == null) { aOut.Add($"TASK-{i}（**單子不存在** —— 查不到不等於已解決）"); continue; }
                if (!b.IsClosed()) aOut.Add($"{b.Id} `{b.status}` {b.title}");
            }
            return aOut;
        }

        // ===========================================================
        // 區塊職責：子任務的**進度讀數** —— 幾張、幾張已關、剩哪些沒關。
        // 物理意義：主 Task 的意義就是這個數字。沒有它的話 `subtask_indices` 只是一串號碼，
        //   而「這條線還剩多少」得靠人一張一張點開 —— 那不是追蹤，是人眼盤點。
        // ⚠ 指到**不存在**的子單也要報出來（`oMissing`）——「查不到」不等於「已完成」。
        // ===========================================================
        public static void SubtaskProgress(UCL_TaskEntry e,
            out int oTotal, out int oClosed, out List<string> oOpenList, out List<int> oMissing)
        {
            oTotal = 0; oClosed = 0;
            oOpenList = new List<string>();
            oMissing = new List<int>();
            if (e == null) return;
            foreach (int i in e.subtask_indices)
            {
                oTotal++;
                var c = Find(i);
                if (c == null) { oMissing.Add(i); continue; }
                if (c.IsClosed()) oClosed++;
                else oOpenList.Add($"{c.Id} `{c.status}` {c.title}");
            }
        }

        /// <summary>把 `TASK-0008` / `8` / `0008` 都收成整數；認不出回 -1（不猜）。</summary>
        public static int ParseTaskRef(string iRaw)
        {
            string s = (iRaw ?? "").Trim();
            if (s.Length == 0) return -1;
            if (s.StartsWith("TASK-", StringComparison.OrdinalIgnoreCase)) s = s.Substring(5);
            return int.TryParse(s.TrimStart('0').Length == 0 ? "0" : s,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;
        }

        /// <summary>QA 閘門：單上有 QA 而動手結單的人不是那位 QA ⇒ 擋。回 null＝可以過。</summary>
        public static string QaGateBlocked(UCL_TaskEntry e, string iActor, string iQaNote)
        {
            var aQa = e.QaPersonas();
            if (aQa.Count == 0) return null;                       // 沒指名 QA ⇒ 由開單人或 PM 結
            foreach (var q in aQa)
                if (string.Equals(q, iActor, StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.IsNullOrWhiteSpace(iQaNote)) return null;   // 有附驗收紀錄 ⇒ 放行（RFC §2④）
            return $"這張單指名的 QA 是 {string.Join(" / ", aQa)}，而動手的是 {iActor}。"
                 + " 要嘛由那位 QA 跑 resolve，要嘛帶 `--arg qa_note=<驗收紀錄>`（誰驗的、驗了什麼讀數）。";
        }

        // 區塊職責：open / stale / blocked 讀數 —— Cmd、後台頁與晚安對帳共用同一個算法。
        // 物理意義：**stale 不是另一種「已關」，是 open 的一個更難看的名字** ⇒ 算進 open，另外再報一個數。
        // 數值影響：updated_at 壞掉的單 DaysSinceUpdate 回 -1 ⇒ **不算 stale**，另外用 oBroken 報出來
        //          （不要混進 stale 數字裡假裝知道它幾天沒動）。
        public static void CountStats(out int oOpen, out int oStale, out int oBroken, out int oBlocked)
        {
            oOpen = 0; oStale = 0; oBroken = 0; oBlocked = 0;
            var aNow = DateTime.UtcNow;
            foreach (var e in LoadAll())
            {
                if (e.IsClosed()) continue;
                oOpen++;
                if (OpenBlockers(e).Count > 0) oBlocked++;
                if (e.status != UCL_TaskStatus.in_progress) continue;
                int aDays = e.DaysSinceUpdate(aNow);
                if (aDays < 0) oBroken++;
                else if (aDays >= STALE_DAYS) oStale++;
            }
        }

        public static void Touch(UCL_TaskEntry e, string iNowUtc) => e.updated_at = iNowUtc;

        public static string NowUtc() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        // ── 小工具 ────────────────────────────────────────────────
        static bool AddOnce(List<int> ioList, int iValue)
        {
            if (ioList.Contains(iValue)) return false;
            ioList.Add(iValue);
            return true;
        }

        static bool IsSectionHeading(string iLine)
        {
            foreach (var aH in SECTION_HEADINGS)
                if (iLine.StartsWith(aH, StringComparison.Ordinal)) return true;
            return false;
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
                    if (aIn && IsSectionHeading(aLine)) break;
                    if (aIn) sb.Append(aLine).Append('\n');
                }
                string s = sb.ToString().Trim();
                return s == "_(未填)_" ? "" : s;
            }
            catch { return ""; }
        }

        static string After(string iLine, string iPrefix)
            => iLine.Substring(iPrefix.Length).Trim();

        // ===========================================================
        // 區塊職責：把 `## 留言` 區塊解析成留言清單。
        // 物理意義：一則的邊界＝下一個 `### 💬` 標頭或下一個頂層區塊標題。
        // ⚠ 認不出標頭的行（例如有人手改壞了格式）**歸給前一則的內文**而不是丟掉 ——
        //   丟掉會讓「有人手改壞了」與「他沒寫過那句話」長得一樣。
        // 數值影響：一次讀檔。回空清單＝這張單沒有留言（或沒有留言區塊）。
        // ===========================================================
        public static List<UCL_TaskComment> ReadComments(string iPath)
        {
            var aOut = new List<UCL_TaskComment>();
            try
            {
                if (!File.Exists(iPath)) return aOut;
                bool aIn = false;
                UCL_TaskComment aCur = null;
                var aBody = new StringBuilder();
                foreach (var aLine in File.ReadAllLines(iPath, Encoding.UTF8))
                {
                    if (aLine.StartsWith("## 留言", StringComparison.Ordinal)) { aIn = true; continue; }
                    if (!aIn) continue;
                    if (IsSectionHeading(aLine)) break;                 // 區塊結束

                    var aM = COMMENT_HEAD.Match(aLine);
                    if (aM.Success)
                    {
                        Flush(aOut, ref aCur, aBody);
                        aCur = new UCL_TaskComment
                        {
                            persona = aM.Groups["persona"].Value,
                            at = aM.Groups["at"].Value,
                        };
                        int.TryParse(aM.Groups["id"].Value, out aCur.id);
                        continue;
                    }
                    if (aCur != null) aBody.Append(UnescapeCommentLine(aLine)).Append('\n');
                }
                Flush(aOut, ref aCur, aBody);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Task] 留言解析失敗（當成沒有留言，但這一行是那個失敗的讀數）：{iPath}（{ex.Message}）");
            }
            return aOut;
        }

        static void Flush(List<UCL_TaskComment> ioList, ref UCL_TaskComment ioCur, StringBuilder ioBody)
        {
            if (ioCur != null)
            {
                ioCur.body = ioBody.ToString().Trim();
                ioList.Add(ioCur);
            }
            ioCur = null;
            ioBody.Length = 0;
        }

        /// <summary>下一則留言的編號（現有最大 +1）—— 編號只為讓人指名「第幾則」。</summary>
        public static int NextCommentId(UCL_TaskEntry e)
        {
            int aMax = 0;
            foreach (var c in e.comments) if (c.id > aMax) aMax = c.id;
            return aMax + 1;
        }

        static string IntList(List<int> iList)
        {
            if (iList == null || iList.Count == 0) return "[]";
            return "[" + string.Join(", ", iList.ConvertAll(v => v.ToString(CultureInfo.InvariantCulture))) + "]";
        }

        static string StrList(List<string> iList)
        {
            if (iList == null || iList.Count == 0) return "[]";
            return "[" + string.Join(", ", iList.ConvertAll(OneLine)) + "]";
        }

        static List<int> ParseIntList(string iRaw)
        {
            var aOut = new List<int>();
            string s = (iRaw ?? "").Trim().Trim('[', ']');
            if (s.Length == 0) return aOut;
            foreach (var aPart in s.Split(','))
                if (int.TryParse(aPart.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    aOut.Add(v);
            return aOut;
        }

        static List<string> ParseStrList(string iRaw)
        {
            var aOut = new List<string>();
            string s = (iRaw ?? "").Trim().Trim('[', ']');
            if (s.Length == 0) return aOut;
            foreach (var aPart in s.Split(','))
            {
                string t = aPart.Trim();
                if (t.Length > 0) aOut.Add(t);
            }
            return aOut;
        }

        // frontmatter 是一行一值 —— 換行會把後面的內容變成別的 key，所以進 frontmatter 前一律壓成單行。
        static string OneLine(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        static string Nz(string s) => string.IsNullOrWhiteSpace(s) ? "_(未填)_" : s;
        static string Nz2(string s) => string.IsNullOrWhiteSpace(s) ? "unknown" : s;
    }
}
#endif
