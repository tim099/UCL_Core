// 區塊職責：任務單的**讀取層**（Editor 端）—— 路徑、單檔解析、清單、blocker／子任務讀數、驗收格的讀法。
// 物理意義：⛔ **本檔不寫任務單**（TASK-0349，2026-09-30）。任務單唯一的寫入端是 Senate Server（`task-write`）：
//           寫入面（Save／Mutate／Create／Link／Unlink／配號）移植到 SCP_Core `SCP_TaskStore`，
//           Editor 這側的寫入呼叫一律經 `UCL_TaskSenateBridge` 轉交 `senate cmd task`。
//           ⇒ 寫入面在這裡**整段刪掉**而不是留著不用：留著的 public `Mutate` 會被下一個人順手呼叫，
//             而那等於安靜地多一個寫入端（兩個寫入端的輸出長得一模一樣）。
//   🩸 搬之前的形狀：本檔的鎖（`s_RmwLock`）只擋得住同一個 process；配號（`_index.txt`）在參數檢查之前
//     ⇒ 打錯 enum 會吃掉號碼（2026-09-30：0351／0352）；`Link`／`Unlink` 是鎖外寫兩次。
//
// 📌 **一單一檔**（照 BugReport 的母版，Tim 2026-08-18 拍板的形狀）：事實來源＝`tasks/<index>.md`；**沒有第二份索引**。
// ⚠ 區塊標題判定是**整行相等**（與 SCP 讀寫兩端同一條，TASK-0349）—— 前綴比對會把內文的
//   `## 驗收標準怎麼處置（…）` 當成區塊邊界（0126／0204 的結單說明在那一行被截斷）。
// 數值影響：純檔案讀取。
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
        // 數值影響：純字串比對；清單要與寫入端（SCP_Core `SCP_TaskStore.Render`）實際寫出的標題**逐字一致**（改一邊要改另一邊）。
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

        static string UnescapeCommentLine(string iLine)
            => iLine.StartsWith("\\#", StringComparison.Ordinal) ? iLine.Substring(1) : iLine;

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
        // 數值影響：純字串處理，不碰磁碟。⛔ 勾選（寫）在 SCP_Core `SCP_TaskStore.CheckOffCriteria`，本檔只讀。
        // ===========================================================
        /// <summary>驗收標準整段（給呼叫端讀原文用；空的 / `_(未填)_` 回空字串）。</summary>
        public static string ReadCriteria(int iIndex) => ReadSection(TaskPath(iIndex), "## 驗收標準");

        /// <summary>任務描述整段（給呼叫端讀原文用；空的 / `_(未填)_` 回空字串）。</summary>
        public static string ReadDescription(int iIndex) => ReadSection(TaskPath(iIndex), "## 任務描述");

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

        // ===========================================================
        // 區塊職責：讀出某一條驗收標準行上的「指定簽名人」標記 `[signer:<persona>]`。
        // 物理意義：驗收條文有時是**一格一個尺度**的 —— 「⑤c @Sirius 的搬遷確認：只有本人能簽」。
        //          而 op=check 的權限是**整張單一個尺度** ⇒ 本人被擋、開單人反而代簽得掉，
        //          兩把尺在同一格上給出相反的答案（TASK-0194 活體）。本標記讓那條條文
        //          從「靠自律」變成一個機制擋得到的東西。
        // 🩸 為什麼**不認裸 `@persona`**：那個形狀會誤命中 —— TASK-0146 的
        //   「④ 第一本搬 @gura《深海對拍錄》」裡 @gura 是**被搬的書的主人**，不是簽名人。
        //   ⇒ 「這一格屬於誰」與「這一格提到誰」必須不同形，否則守衛會擋錯人，
        //     而擋錯人的樣子跟擋對人一模一樣。
        // 數值影響：純字串解析，無副作用；沒有標記回 null（＝這一格走整張單的尺度）。
        // ===========================================================
        /// <summary>
        /// 取出驗收標準行上的 `[signer:&lt;persona&gt;]` 指定簽名人；沒有標記回 <c>null</c>。
        /// <para>⛔ 不認裸 `@persona` —— 那會把「提到誰」誤讀成「誰負責簽」。</para>
        /// </summary>
        public static string CriteriaSigner(string iCriteriaLine)
        {
            if (string.IsNullOrEmpty(iCriteriaLine)) return null;
            var aMatch = System.Text.RegularExpressions.Regex.Match(
                iCriteriaLine, @"\[signer:\s*([A-Za-z0-9_\-\.]+)\s*\]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return aMatch.Success ? aMatch.Groups[1].Value : null;
        }

        static bool IsSectionHeading(string iLine)
        {
            foreach (var aH in SECTION_HEADINGS)
                if (string.Equals(iLine.TrimEnd(), aH, StringComparison.Ordinal)) return true;
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
                    if (string.Equals(aLine.TrimEnd(), iHeading, StringComparison.Ordinal)) { aIn = true; continue; }
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
                    if (string.Equals(aLine.TrimEnd(), "## 留言", StringComparison.Ordinal)) { aIn = true; continue; }
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

    }
}
#endif
