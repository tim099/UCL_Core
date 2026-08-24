// 區塊職責：任務單的磁碟層 —— index 配發 / 單檔讀寫 / 清單 / 依賴雙向寫入 / stale 與 blocker 讀數。
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

        // ⚠ `epics/` 與 `milestones/` **刻意不建立**：
        //   欄位（epic_id / milestone）留著避免日後 migration，但目前**沒有任何讀取端**。
        //   建了空目錄的話，下一個人看到 `epics/` 會以為 Epic 這件事已經在運作 ——
        //   而空目錄跟「還沒有人建 Epic」長得一模一樣（🩸 別造一個名字比事實大的東西）。
        //   要開始用 Epic 的那天再建，而那天會有一個真的 Epic 當第一顆探針。

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
            "## 驗收標準", "## 任務描述", "## 結單說明", "## 活動與討論時間線",
        };

        // ===========================================================
        // 區塊職責：寫一張單（新建 / 狀態變更 / 留言皆走這裡）。
        // 數值影響：一次讀（撈既有歷史與內文）＋ 一次寫。iActivityLine 為空＝不追加時間線。
        // ===========================================================
        public static void Save(UCL_TaskEntry e, string iCriteria, string iDescription, string iActivityLine)
        {
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
                sb.Append($"    role: {OneLine(p.role)}\n");
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
                    { aCur.role = After(aTrim, "role:"); continue; }
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
                        case "type": e.type = v; break;
                        case "priority": e.priority = v; break;
                        case "status": e.status = v; break;
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
                        case "closed_at": e.closed_at = v; break;
                        case "participants": aCur = null; break;
                    }
                }
                return e.index > 0 ? e : null;
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
            else { oError = $"認不得的關聯種類 '{iKind}'（blocked_by|blocks|related_to）"; return false; }
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
                if (!string.Equals(e.status, "in_progress", StringComparison.OrdinalIgnoreCase)) continue;
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
