// 區塊職責：任務單的磁碟層 —— index 配發 / 單檔讀寫 / 清單 / 依賴雙向寫入 / stale 與 blocker 讀數。
//
// ⚠⚠ **這個檔的併發安全來自「一把鎖 ＋ 只有兩個寫入入口」**（TASK-0163，2026-09-09 起）。
//   入口只有 `Mutate`（改一張既有的單）與 `Create`（開一張新的單）——
//   兩者都在 `s_RmwLock` 內完成「讀 → 改 → 寫」，而 `Save` 已經是 **private**：
//   ⇒ 呼叫端在型別上拿不到一個「不在鎖內的 entry」，也拿不到 `Save`。
//   🩸 舊的前提是「單一主執行緒 ＋ RMW 中間沒有 `await`」（TASK-0026，2026-08-25），
//     它靠 11 行 `⛔ [RMW-END]` 註解維持，而那個慣例被量出**兩個表達不出來的形狀**：
//     ① 跨函式（`UCL_TaskReconcile.WriteSkip` 把 entry 當參數收，前哨貼不到）
//     ② 跨迴圈輪次（`OpSweep` 的 Save 在含 `await` 的迴圈裡 —— 前哨每一輪都印在正確位置上，
//        **而它看不見迴圈**）〔@basecamp 2026-09-08 量的〕
//     ⇒ 修法不是第三種註解，是讓錯的動作在型別上不存在。
//   ⛔ **射程：同一個 process 內。** python／另一個 Editor 實例同時寫，本鎖答不出來
//     （那要檔案鎖，不在 TASK-0163 射程）。前提破掉的症狀仍然是靜默的
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
    // ===========================================================
    // 區塊職責：一次寫入要落盤的東西 —— 時間線那一行 ＋（選填）兩個內文區塊。
    // 物理意義：`Save` 對空的 `criteria`／`description` 的語意是**「沿用磁碟那一份」**
    //   （跟留言與 `resolution_note` 同一族的沿用規則）⇒ 所以「不動內文」與「把內文寫成空的」
    //   在參數上長得一樣，而前者是常態。⇒ 用三個具名的建構子把意圖說出來，不讓呼叫端傳空字串猜。
    // 🩸 為什麼需要它：`Mutate` 原本固定呼叫 `Save(e, "", "", line)` ⇒ 它在型別上**寫不了內文欄位**，
    //   於是 `OpCreate`／`OpUpdate`／`OpCheck` 三支過不去 —— 而 `OpCheck` 寫的就是驗收那一欄，
    //   它偏偏是最該進鎖的（序號取自鎖外讀的未勾清單，兩人同時勾會吃掉別人的**署名**）。
    // 邊界：`Skip` ＝ 這次不寫（鎖內判定不成立的出口）。判準是 `activity` 是否為空。
    // ===========================================================
    public struct UCL_TaskWrite
    {
        public string activity;      // 時間線那一行；空 ⇒ 這次不寫
        public string criteria;      // 空 ⇒ 沿用磁碟那一份
        public string description;   // 空 ⇒ 沿用磁碟那一份

        /// <summary>這次不寫（鎖內重判不成立）。⛔ 不是失敗，是刻意零寫入。</summary>
        public static UCL_TaskWrite Skip => default;
        /// <summary>只寫時間線一行，內文兩區沿用磁碟。</summary>
        public static UCL_TaskWrite Line(string iActivity)
            => new UCL_TaskWrite { activity = iActivity };
        /// <summary>連內文一起寫（`criteria` / `description` 給空字串＝那一區沿用）。</summary>
        public static UCL_TaskWrite Body(string iActivity, string iCriteria, string iDescription)
            => new UCL_TaskWrite { activity = iActivity, criteria = iCriteria, description = iDescription };

        public bool WillWrite => !string.IsNullOrEmpty(activity);
    }

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
        // ⛔ **private ＋ 必須在鎖內呼叫（TASK-0163）**：它自己就是一段 read-modify-write
        //   （讀計數檔 → +1 → 寫回）。它曾經是 public 且不持鎖 ⇒ 兩條 lane 同時開單會**配到同一個 index**，
        //   而症狀是第二張單把第一張整檔覆蓋掉（檔頭那句「index 撞號」講的就是這個）。
        //   ⇒ 現在唯一呼叫端是 `Create`，而 `Create` 在鎖內。
        static int IncrementAndGetIndexLocked()
        {
            AssertHoldsRmwLock(nameof(IncrementAndGetIndexLocked), -1);
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
        // 區塊職責：**量新的那個不變式** —— 「寫檔的時候，鎖在手上」。
        // 🩸 這一格是換代來的（TASK-0163 ②）：前一版是 `AssertMainThread`，它量的是舊前提
        //   （單一主執行緒）。上鎖之後那道守衛會變成兩件事之一，而兩件都不能留：
        //     · 若仍留著「非主緒就 LogError」⇒ offload 之後**每次都叫**，
        //       而一個每次都叫的 LogError 三天內會被人拿掉（那時真正的錯就沒有人在看了）。
        //     · 若直接刪掉不換 ⇒ 這個檔會變成「沒有任何一層在量自己的前提」。
        //   ⇒ 所以它不是化石，是**反過來量**：從「誰在哪條緒上」換成「寫的時候有沒有持鎖」。
        // 物理意義：`Monitor.IsEntered` 問的是**本執行緒**是否已經進入那把鎖 ——
        //   而 `Save` 現在是 private，唯一的外部路徑是 `Mutate` / `Create`（兩者都在鎖內）
        //   ⇒ 它會叫的唯一情境是**本類別內部**有人新加了一條繞過鎖的寫入路徑。
        //   ⚠ 那不是假想：`IncrementAndGetIndex` 就曾經是 public 且不持鎖（index 撞號的來源）。
        // 邊界：⛔ 它答不出跨 process（另一個 Editor 實例／python）—— 那要檔案鎖。
        //   所以這道探針的射程是「本 process 內有沒有人繞過入口」，不是「這個檔安全了」。
        // ===========================================================
        static void AssertHoldsRmwLock(string iWho, int iIndex)
        {
            if (System.Threading.Monitor.IsEntered(s_RmwLock)) return;
            Debug.LogError(
                $"[Task] ⚠️ {iWho}(index={iIndex}) **在鎖外寫檔**（tid={System.Threading.Thread.CurrentThread.ManagedThreadId}）。"
                + " 本檔的併發安全來自 `s_RmwLock` ＋ 只有 `Mutate`／`Create` 兩個入口，"
                + " 而這行讀數說有一條路徑繞過了它們。"
                + " ⇒ 去看是誰在鎖外呼叫了 `Save`（它是 private ⇒ 只可能是本類別內部新加的路徑），"
                + " 把那段包進 `Mutate`／`Create`；⛔ 不要把這行拿掉，也不要另外開一把鎖"
                + "（第二把鎖擋不住第一把，而兩把鎖的錯是靜默的）。");
        }

        // ⛔ **private（TASK-0163）**：唯一的寫入路徑是 `Mutate`／`Create`，它們在鎖內呼叫本方法。
        //   🩸 改成 private 才是那條規則的實體 —— 在此之前它是 public，而 13 個呼叫端裡
        //   有 2 個不在 `Task/` 目錄底下（後台頁），⇒ 兩個人各自 grep 那個目錄都少數了兩個。
        //   **「射程由目錄決定」是枚舉盲區的一種**：缺的那兩個不會出現在自己的清單上。
        static void Save(UCL_TaskEntry e, string iCriteria, string iDescription, string iActivityLine)
        {
            AssertHoldsRmwLock(nameof(Save), e?.index ?? -1);
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
                // 🩸 TASK-0158：`resolution_note`（`## 結單說明`，含 QA 代簽紀錄）**只有寫入端**——
                //   `LoadFile` 只解析 frontmatter，body 區塊全靠這裡逐段撈回來，而這一段漏了。
                //   ⇒ 任何重新落檔的 op（link / comment / update / claim…）載入時它是空字串，
                //     於是**整段被靜默刪掉**：那個 op 回 Success、它自己要改的欄位也真的對了，
                //     少掉的那一段沒有任何一層在看（歷史已發生 10 次，跨 5 個人的 commit）。
                //   ⚠ 語意與上面兩行一致：**「沒給」＝沿用，不是「清空」**。
                //     沒有任何 op 需要清掉結單說明；真要清就直接改檔案。
                if (string.IsNullOrEmpty(e.resolution_note)) e.resolution_note = ReadSection(aPath, "## 結單說明");
            }
            if (!string.IsNullOrEmpty(iActivityLine)) aTimeline.Add("- " + iActivityLine);

            var sb = new StringBuilder();
            sb.Append("---\n");
            sb.Append($"index: {e.index}\n");
            sb.Append($"id: {e.Id}\n");
            sb.Append($"type: {e.type}\n");
            sb.Append($"priority: {e.priority}\n");
            // severity=none 不落行 —— 缺席即 none（非缺陷單常態），既有單零 diff
            if (e.severity != UCL_TaskSeverity.none) sb.Append($"severity: {e.severity}\n");
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
            sb.Append($"> `{e.type}` / "
                + (e.severity == UCL_TaskSeverity.none ? "" : $"`{e.severity}` / ")
                + $"`{e.priority}` / `{e.status}`　開單：{Nz2(e.reporter)}");
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
        // 區塊職責：**唯一**帶鎖的 read-modify-write 入口（TASK-0163 形狀甲）
        // 物理意義：本檔的寫入是「讀整檔 → 改 → 重寫整檔」，而跨度**起於呼叫端的 READ**
        //          （`Find`／`Require`），不是起於 `Save`。⇒ 一把只包住 `Save` 的鎖擋不住
        //          「A 讀 → B 讀 → A 寫 → B 寫」，B 的整檔重寫會把 A 那次靜默吃掉。
        //          ⇒ 所以鎖必須包住 READ ＋ 改 ＋ WRITE 三段，而讓它包得住的唯一辦法是
        //          **把那三段收成一個型別擋得住的入口**：`e` 只在 lambda 裡存在，
        //          呼叫端拿不到一個「不在鎖內的 e」。
        // 🩸 為什麼不是再貼一種註解：舊慣例 `⛔ [RMW-END]` 已經被量出**兩個表達不出來的形狀** ——
        //   ① 跨函式（`UCL_TaskReconcile.WriteSkip` 把 `e` 當參數收，前哨貼不到）
        //   ② 跨迴圈輪次（`Cmd_Task.OpSweep` 的 Save 在含 `await` 的迴圈裡，
        //      前哨每一輪都印在正確位置上，**而它看不見迴圈**）—— @basecamp 2026-09-08 量的。
        //   ⇒ 修法不是第三種註解，是讓錯的動作在型別上不存在。
        // 數值影響：同一個 process 內序列化 Task 檔的 RMW。⛔ **不跨 process**
        //          （python / 別的 Editor 實例同時寫，本鎖答不出來 —— 那要檔案鎖，不在本單射程）。
        // 邊界：
        //   · `iMutator` 回傳**時間線那一行**；回 `null` 或空 ⇒ **不寫**（判定在鎖內重做的出口，形狀乙）。
        //   · 單不存在 ⇒ 回 `false`，不寫、不丟例外（呼叫端自己決定那算不算錯）。
        //   · ⛔ `iMutator` 裡**不得 await**：那會在持鎖狀態下把控制權交出去。
        //     今天 11 個 RMW 跨度實測全部 await-free（@basecamp 2026-09-08）⇒ 同步 lambda 蓋得住，
        //     所以本入口**刻意不提供 async 版本** —— 需要它的那天，要解的是「持鎖 await」那個更大的題。
        //   · ✅ 遷移已完成（2026-09-09）：**`Save` 是 private，外部寫入端 0 個。**
        //     驗法（不寫死數字 —— 寫了會過期而過期的斷言不會叫）：
        //     `grep -rn "UCL_TaskIO.Save(" --include=*.cs Assets/` ⇒ 只該剩註解，不該有呼叫。
        //     🩸 而遷移面的真值是 **13** 不是 11：`Cmd_Task` 11 ＋ `UCL_TaskReconcile` 1 ＋
        //     **`UCL_TaskManagerPage` 2** —— 最後那兩個我與 @basecamp **兩個人都沒數到**，
        //     因為兩份清單都是 grep `Task/` 那個目錄撈的，而後台頁不在那個目錄裡。
        //     📌 「射程由目錄決定」是枚舉盲區的一種：缺的那兩個不會出現在自己的清單上。
        //   · ⚠ TASK-0163 ②（守衛換代）已同日完成：`AssertMainThread` 換成 `AssertHoldsRmwLock`
        //     （見那個區塊）。④（`Cmd_Task` 走 `EnterBackground`）仍開著 —— 那是 TASK-0162 的事。
        // ===========================================================
        static readonly object s_RmwLock = new object();

        public static bool Mutate(int iIndex, System.Func<UCL_TaskEntry, UCL_TaskWrite> iMutator)
        {
            if (iMutator == null) return false;
            lock (s_RmwLock)
            {
                var e = Find(iIndex);          // 鎖內重讀 —— 呼叫端鎖外撈的那份只算「提示」
                if (e == null) return false;
                var aWrite = iMutator(e);
                if (!aWrite.WillWrite) return false;   // 判定在鎖內不成立 ⇒ 不寫（`UCL_TaskWrite.Skip`）
                Save(e, aWrite.criteria ?? "", aWrite.description ?? "", aWrite.activity);
                return true;
            }
        }

        // ===========================================================
        // 區塊職責：開一張新的單 —— **配號與落檔在同一把鎖內**。
        // 物理意義：`IncrementAndGetIndexLocked` 自己是一段 RMW（讀計數檔 → +1 → 寫回），
        //   而開單的內文有一部分**依賴那個號碼**（bug 單的驗收骨架寫著 `Fixes TASK-<n>`）
        //   ⇒ 呼叫端必須在鎖內才拿得到號碼 ⇒ 用一個「收號碼、回 (單, 要寫什麼)」的 lambda。
        // 🩸 為什麼不能讓呼叫端先配號再自己 Save：那正是舊路 ——
        //   兩條 lane 同時開單會配到同一個 index，而第二張把第一張**整檔覆蓋**，兩邊都回 Success。
        // 邊界：
        //   · `iBuild` 回 `null` 單 ⇒ 回 -1、不寫（呼叫端自己決定那算不算錯）。
        //   · `iBuild` 裡 ⛔ 不得 `await`（持鎖交出控制權）—— 同 `Mutate`，本入口刻意沒有 async 版本。
        //   · 回傳值＝真正落盤的 index；⛔ 別再從 lambda 外面的變數推它。
        // ===========================================================
        public static int Create(System.Func<int, (UCL_TaskEntry entry, UCL_TaskWrite write)> iBuild)
        {
            if (iBuild == null) return -1;
            lock (s_RmwLock)
            {
                int aIndex = IncrementAndGetIndexLocked();
                var (e, aWrite) = iBuild(aIndex);
                if (e == null) return -1;
                e.index = aIndex;              // 號碼由本入口決定，不信呼叫端填的那格
                Save(e, aWrite.criteria ?? "", aWrite.description ?? "", aWrite.activity ?? "");
                return aIndex;
            }
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
                        case "type":
                            e.type = UCL_TaskWire.ParseOr(v, UCL_TaskType.feature, $"{iPath} type");
                            if (e.type == UCL_TaskType.all)
                            {
                                UnityEngine.Debug.LogError($"[Task] {iPath} type: `all` 是篩選成員不是任務種類 —— 落回 `feature`，去修單檔 frontmatter");
                                e.type = UCL_TaskType.feature;
                            }
                            break;
                        case "priority": e.priority = UCL_TaskWire.ParseOr(v, UCL_TaskPriority.normal, $"{iPath} priority"); break;
                        case "severity": e.severity = UCL_TaskWire.ParseOr(v, UCL_TaskSeverity.none, $"{iPath} severity"); break;
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

        // ===========================================================
        // 區塊職責：驗收標準那一段的**勾選格**讀寫（TASK-0119）。
        // 物理意義：`## 驗收標準` 是一段自由 markdown，勾選格是其中行首為 `- [ ]` / `- [x]` 的那些行。
        //   ⇒ 本區塊是**唯一**認得那個形狀的地方；`Cmd_Task` 只拿序號與署名進來，不碰字串格式。
        //
        // 🩸 為什麼勾要**帶署名**（本單開單人的原話）：
        //   「驗收是簽名行為 —— 沒有署名的勾等於沒有勾。」
        //   ⇒ 所以勾完的行尾會多一段 `　✅ <persona> <yyyy-MM-dd>`，而**不是**只翻五個字元
        //     （見叢 `SCP_Cmd_Keys` 是只翻五個字元，因為那是給自己看的個人清單，沒有第二個讀者）。
        //   ⛔ 署名**只有這一份**（可見文字），不另寫 HTML 註解 ——
        //     兩種標記就是兩份真相，而它們會漂（本檔檔頭同一條）。
        //
        // ⚠ 序號的口徑：**未勾清單的 1-based 序號**，不是檔案行號、也不是所有勾選格的序號。
        //   （與見叢 `--arg done_index` 同一個口徑，刻意一致 —— 兩套口徑會讓人在兩邊各數錯一次。）
        // 數值影響：純字串處理，不碰磁碟；寫回由呼叫端走 Save(criteria) 一次完成。
        // ===========================================================
        /// <summary>驗收標準整段（給呼叫端讀原文用；空的 / `_(未填)_` 回空字串）。</summary>
        public static string ReadCriteria(int iIndex) => ReadSection(TaskPath(iIndex), "## 驗收標準");

        /// <summary>行首是勾選格嗎？回傳它的標記寬度（`- [ ] ` ／ `- [x] `），不是就回 0。</summary>
        static int CriteriaBoxWidth(string iLine, out bool oChecked)
        {
            oChecked = false;
            if (iLine == null) return 0;
            // ⚠ 只認**行首**（不 TrimStart）—— 縮排的 `- [ ]` 是上一格的續行內容，
            //   把它算進去會讓「第 3 格」在兩個人眼裡指到不同的行。
            if (iLine.StartsWith("- [ ] ", StringComparison.Ordinal)) return 6;
            if (iLine.StartsWith("- [x] ", StringComparison.Ordinal)) { oChecked = true; return 6; }
            if (iLine.StartsWith("- [X] ", StringComparison.Ordinal)) { oChecked = true; return 6; }
            return 0;
        }

        /// <summary>未勾的驗收格（1-based 序號 → 那一行的內容，已去掉 `- [ ] ` 前綴）。</summary>
        public static List<string> ListUncheckedCriteria(string iCriteria)
        {
            var aOut = new List<string>();
            foreach (var aLine in (iCriteria ?? "").Replace("\r", "").Split('\n'))
                if (CriteriaBoxWidth(aLine, out bool aChecked) > 0 && !aChecked)
                    aOut.Add(aLine.Substring(6).Trim());
            return aOut;
        }

        /// <summary>已勾的驗收格（只給讀數用：印「3/7 格已驗」那個分母的另一半）。</summary>
        public static List<string> ListCheckedCriteria(string iCriteria)
        {
            var aOut = new List<string>();
            foreach (var aLine in (iCriteria ?? "").Replace("\r", "").Split('\n'))
                if (CriteriaBoxWidth(aLine, out bool aChecked) > 0 && aChecked)
                    aOut.Add(aLine.Substring(6).Trim());
            return aOut;
        }

        /// <summary>
        /// 把未勾清單裡的第 <paramref name="iOneBased"/> 格勾起來並簽名。
        /// <para>成功 ⇒ 回勾起來的那一行內容，`ioCriteria` 已換成新的整段；
        /// 失敗（序號越界）⇒ 回 <c>null</c> 且 `ioCriteria` **一個字元都不動**。</para>
        /// ⚠ 多筆勾銷要**由大到小**呼叫，否則前一次勾完會讓後面的序號位移
        /// （未勾清單會縮短）—— 呼叫端負責排序，這裡不猜。
        /// </summary>
        public static string CheckOffCriteria(ref string ioCriteria, int iOneBased, string iActor, DateTime iNowLocal)
        {
            if (iOneBased < 1) return null;
            var aLines = new List<string>((ioCriteria ?? "").Replace("\r", "").Split('\n'));
            int aSeen = 0;
            for (int i = 0; i < aLines.Count; i++)
            {
                if (CriteriaBoxWidth(aLines[i], out bool aChecked) == 0 || aChecked) continue;
                if (++aSeen != iOneBased) continue;
                string aBody = aLines[i].Substring(6);
                // 署名接在行尾 —— 全角空白當分隔，跟本 repo 其他「讀數＋出處」同一個排版慣例。
                aLines[i] = "- [x] " + aBody.TrimEnd()
                    + $"　✅ {Nz2(iActor)} {iNowLocal:yyyy-MM-dd}";
                ioCriteria = string.Join("\n", aLines);
                return aBody.Trim();
            }
            return null;
        }

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
