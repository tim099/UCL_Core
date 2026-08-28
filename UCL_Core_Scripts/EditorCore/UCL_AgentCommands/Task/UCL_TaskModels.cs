// 區塊職責：任務單的資料模型與四個列舉（type / priority / status / role）。
// 物理意義：對應 AgentCommands/Tasks/tasks/<index>.md 的 frontmatter。
//          Task 是**跨人協作的交付承諾**；只有自己要記住的事留在見叢（`_keys_open.md`），
//          分流判準是一句當下答得出來的話：「有沒有第二個人在等這件事？」
// 數值影響：純資料，無 IO。列舉欄位在 C# 端用 enum、**wire 上一律是字串（成員名逐字＝wire 字串）**——
//          這份資料有 python 讀取端，enum 序號跨語言沒有意義（BugReport 同一條規矩，照抄不改）；
//          字串只存在於 IO 邊界，唯一轉換點是 UCL_TaskWire。
// 設計沿革：Plan_Task_Management_System.md（gura 撰寫 / Tim 2026-08-24 拍板；
//          RFC 酒館 seq 13303 → 評審 13306 → 收斂 13307 → 邊界 13308 → 計畫確認 13310）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace UCL.Core.EditorLib.AgentCommands.TaskMgmt
{
    // ⚠ 下列列舉全部遵守 UCL_TaskStatus 的同一條約定：**成員名＝frontmatter wire 字串，逐字相同**
    //   （刻意小寫，不遵 C# PascalCase）—— ToString()/TryParse 即完成雙向轉換，沒有第二張對照表可以漂。
    //   改名或增減成員＝改 wire format，動之前先盤 python 端與既有單檔。
    /// <summary>任務種類。</summary>
    public enum UCL_TaskType
    {
        // ⚠ `all` 不是任務種類 —— **篩選用**成員（同 UCL_TaskStatus 的約定，Tim 2026-08-28）。
        //   放第一位讓它成為 default(UCL_TaskType)（篩選的預設就是全部）。
        //   兩個守衛擋它流進資料：Cmd_Task create 拒收、UCL_TaskIO.LoadFile 讀檔拒收。
        all,
        feature,
        improvement,
        refactor,
        /// <summary>技術調研 —— 產出是「知道了什麼」而不是「做好了什麼」。</summary>
        spike,
        subtask,
        /// <summary>缺陷修復（Tim 2026-08-28 拍板入詞彙表，活體 TASK-0065）。
        /// 缺陷單一律走這裡（TASK-0086 整併後唯一入口）：evidence 必填、criteria 三段骨架自帶、
        /// severity 標傷害形狀。歷史 BUG-1~50 凍結在 AgentCommands/BugReports/reports/。</summary>
        bug,
        /// <summary>主 Task（傘）—— 大項目的收納單位，子單以 `epic_id` 指回來（Tim 2026-08-28 拍板入詞彙表；
        /// 前身是 `tags=[epic]` 慣例，型別化後篩選與看板可直取）。開法見 Task_Management_Workflow §1.6。</summary>
        epic,
    }

    public enum UCL_TaskPriority
    {
        urgent,
        high,
        normal,
        low,
    }

    // ===========================================================
    // 區塊職責：傷害形狀（TASK-0086，Tim 2026-08-28 拍板入 schema）。
    // 物理意義：跟 priority 是兩把尺 —— priority 是排程語言（先做誰），
    //   severity 是診斷語言（現在誰被怎樣了）。折進 priority 會丟掉
    //   「wrong＝會騙人但能跑」這個資訊，而那正是最該被看見的一種。
    //   判準沿 BugReport：blocking＝有人被擋住做不下去；wrong＝產出錯的結果
    //   但還能跑（安靜的錯）；annoying＝會嘴，但不會騙人。
    // ⚠ `none` 是「未標注」不是第四種傷害 —— 非缺陷單的常態。放首位當 default；
    //   wire 上 none **不落行**（缺席即 none，既有單零 diff）。
    // ===========================================================
    public enum UCL_TaskSeverity
    {
        none,
        blocking,
        wrong,
        annoying,
    }

    // ===========================================================
    // 區塊職責：任務的生命週期。
    // 物理意義：`InReview` 之所以存在，是因為它**有一個機械的守衛**（QA 未簽不得結單，
    //          見 UCL_TaskIO.QaGateBlocked）。
    //   🩸 沒有守衛的狀態會變成單子的養老院 —— 而養老院在看板上看起來像「有在動」。
    //   ⇒ 判準（酒館 seq 13306）：**每一個狀態轉換都要指名它掛在哪一條某人本來就會走的路上。**
    //     指不出來的那個狀態，欄位會永遠停在初始值，而那看起來跟「正在進行」一模一樣。
    //     現行掛點：Todo→InProgress 掛在 `op=claim`／InProgress→InReview 掛在 commit
    //     訊息的 `Fixes TASK-n`／InReview→Done 掛在 QA 的 `op=resolve`。
    // 數值影響：done / cancelled 視為已關（不進 open 讀數）。
    //
    // ⚠ 成員名 ＝ frontmatter `status` 的 wire 字串，**逐字相同**（刻意小寫蛇形，不遵 C# PascalCase）：
    //   ToString()/TryParse 即完成雙向轉換，沒有第二張對照表可以漂。UI 顯示直接用成員名原文
    //   （Tim 2026-08-26 拍板；PopupAuto 的 enum 版沒 localize 詞條時本來就回 key 原文）。
    //   改名或增減成員＝改 wire format —— python 端與所有既有單檔都讀這些字串，動之前先盤消費端。
    //   🩸 本 enum 原以 PascalCase 定義且**零消費端**（2026-08-26 grep 證實）——「寫得進查不出來」同族；
    //   本次收斂成 wire-exact 並接上頁面消費端，才第一次真的被用。
    // ===========================================================
    public enum UCL_TaskStatus
    {
        // ⚠ `all` 不是生命週期狀態 —— 它是**篩選用**的成員（Tim 2026-08-26：不另開第二個 enum，
        //   直接加在這裡）。放第一位讓它成為 default(UCL_TaskStatus)（篩選的預設就是全部）。
        //   兩個守衛擋它流進資料：UCL_TaskIO.LoadFile 的 status parse 不把 "all"/"open" 當合法 status、
        //   UCL_TaskManagerPage.ApplyStatus 拒寫 all —— 少任何一個，`status: all` 就會落盤。
        all,
        open,
        backlog,
        todo,
        in_progress,
        in_review,
        done,
        cancelled,
    }

    /// <summary>參與者身分。**角色不是欄位問題，是「誰真的會做」的問題** —— 掛名而沒有判準的角色等於沒有人。</summary>
    public enum UCL_TaskRole
    {
        dev,
        design,
        qa,
        pm,
        reviewer,
        sound,
        art,
    }

    // ===========================================================
    // 區塊職責：wire 字串 ↔ enum 的**唯一轉換點**（四個列舉共用）。
    // 物理意義：frontmatter 與 python 端仍以字串進出，C# 端欄位改用 enum 之後，
    //   字串只存在於 IO 邊界 —— 這個 class 就是那條邊界。
    // ⚠ 純數字不收：TryParse 會把 "3" 解析成序號 3 的成員，而數字不是狀態。
    // ===========================================================
    public static class UCL_TaskWire
    {
        public static bool TryParse<T>(string iWire, out T oValue) where T : struct, Enum
        {
            oValue = default;
            string s = (iWire ?? "").Trim();
            if (s.Length == 0 || char.IsDigit(s[0]) || s[0] == '-') return false;
            return Enum.TryParse(s, true, out oValue) && Enum.IsDefined(typeof(T), oValue);
        }

        // 讀檔專用：解析不出要**出聲**再落回 iFallback —— 一個壞值不該讓整張單消失，
        // 但也不准安靜地變成一個合法值（錯誤要離開私有欄位才算存在）。
        public static T ParseOr<T>(string iWire, T iFallback, string iContext) where T : struct, Enum
        {
            if (TryParse(iWire, out T aV)) return aV;
            UnityEngine.Debug.LogError($"[Task] {iContext}: '{iWire}' 不是合法的 {typeof(T).Name}"
                + $"（{string.Join("|", Enum.GetNames(typeof(T)))}）—— 落回 `{iFallback}`，去修單檔 frontmatter");
            return iFallback;
        }
    }

    /// <summary>一位參與者：誰、什麼身分、什麼時候被指派。</summary>
    public class UCL_TaskParticipant
    {
        public string persona = "";
        public UCL_TaskRole role = UCL_TaskRole.dev;
        public string assigned_at = "";

        public override string ToString()
        {
            return $"[{role}]{persona}";
        }
    }

    // ===========================================================
    // 區塊職責：一則留言。
    // 物理意義：**討論與活動紀錄是兩種東西**，所以在單檔裡是兩個區塊：
    //   `## 活動與討論時間線` 記「系統做了什麼」（狀態變更、link、commit）——機械寫的；
    //   `## 留言` 記「人說了什麼」——有作者、有內文、可多行。
    //   混在一起的話，「誰說的」會被 append 成一行純文字，而下一個讀的人分不出
    //   那句話是系統敘述還是同事的意見（Tim 2026-08-24：留言要有可判別的區域與留言者）。
    // 數值影響：落在 md 的 `### 💬 #<id>　<persona>　<iso>` 標頭 ＋ 其後的內文行。
    //   ⚠ **只有這一份表示法**（不另加 HTML 註解標記）—— 兩種標記就是兩份真相，而它們會漂。
    // ===========================================================
    public class UCL_TaskComment
    {
        public int id = 0;
        public string persona = "";
        /// <summary>UTC ISO8601。</summary>
        public string at = "";
        public string body = "";
    }

    /// <summary>
    /// 一張任務單。事實來源＝<c>tasks/&lt;index&gt;.md</c> 的 frontmatter ＋內文區塊；
    /// **沒有第二份索引**（有索引就會有「索引與檔案不一致」那一類 bug，而它們通常靜默）。
    /// </summary>
    /// <remarks>
    /// ⚠ **本型別刻意沒有任何 bool 欄位。** `UnityJsonSerializable` 會把 bool 寫成
    /// <c>"True"</c>/<c>"False"</c> 字串，而 python 端讀到的 <c>"False"</c> 是 **truthy**
    /// （2026-08-18 實證：`freetime.py` 因此把已收工的 session 判成還在跑，且完全不報錯）。
    /// 狀態一律用 <see cref="UCL_TaskStatus"/> 表達（wire 上是成員名字串）。
    /// </remarks>
    public class UCL_TaskEntry : UCL.Core.JsonLib.UnityJsonSerializable
    {
        // ⚠ 欄位名 = frontmatter 鍵名 —— 改名等於改 wire format，python 端與後台頁會讀不到。
        public int index = 0;
        public UCL_TaskType type = UCL_TaskType.feature;
        public UCL_TaskPriority priority = UCL_TaskPriority.normal;
        /// <summary>傷害形狀（none＝未標注；type=bug 開單預設 wrong）—— 見 <see cref="UCL_TaskSeverity"/>。</summary>
        public UCL_TaskSeverity severity = UCL_TaskSeverity.none;
        public UCL_TaskStatus status = UCL_TaskStatus.todo;
        public string title = "";
        public string milestone = "";
        public string epic_id = "";
        public string reporter = "";
        public string resolution_note = "";
        public string created_at = "";
        /// <summary>最後一次狀態變動（UTC ISO8601）—— stale 判定的唯一輸入。</summary>
        public string updated_at = "";
        public string closed_at = "";
        // 最後一次 `op=wrapup` 的時戳（UTC）。空 ＝ 從來沒收過工，**或**這張單早於本欄位。
        // ⚠ 讀取端必須能分辨這兩者 —— 見 UCL_TaskReconcile.LastWrapupUtc（缺值時回頭問時間線）。
        // 2026-08-25 TASK-0036：述詞②從「有沒有收過工」改成「最後一次收工之後有沒有又動過」。
        public string last_wrapup_at = "";

        // ===========================================================
        // 區塊職責：Task ↔ 工作記憶的錨點（TASK-0015；契約見工作記憶
        //   `task-management-system/decision_contract-task-memory`）。
        // 物理意義：**錨點放 Task 檔而不是記憶側** —— 因為記憶會被歸檔或刪除，
        //   而 Task 檔一定還在（它是承諾紀錄）。
        // ⚠ 契約①：這兩格**歸 Cmd_Task 寫**；記憶側的 `task_indices` / `status` 歸 CLI，
        //   **兩邊不互寫** —— 這條連結有兩個獨立寫入者（不同語言、不同 process），
        //   互寫就是分散式寫入衝突，而它會在併發時安靜地覆蓋。
        // ⚠ 單值字串（basecamp 拍板 ①）：錨點必須唯一才叫「穩定」；
        //   一單對多主題的發散留在記憶側的 `link`（那一層本來就是為關聯設計的）。
        // ===========================================================
        public string memory_topic = "";
        /// <summary>記憶被歸檔／刪除時的 commit sha —— 「已歸檔」與「沒有記憶」不可以同形。</summary>
        public string memory_archived_commit = "";

        public List<UCL_TaskParticipant> participants = new List<UCL_TaskParticipant>();
        public List<int> blocked_by = new List<int>();
        public List<int> blocks = new List<int>();
        public List<int> related_to = new List<int>();
        public List<int> subtask_indices = new List<int>();
        public List<string> tags = new List<string>();
        public List<string> commit_shas = new List<string>();
        /// <summary>討論留言（由單檔的 `## 留言` 區塊解析而來，寫回時整區重寫）。</summary>
        public List<UCL_TaskComment> comments = new List<UCL_TaskComment>();

        public string Id => "TASK-" + index.ToString("0000", System.Globalization.CultureInfo.InvariantCulture);

        public string ParticipantsName => participants.ConcatToString();
        /// <summary>已關（不進 open 讀數）。</summary>
        public bool IsClosed()
            => status == UCL_TaskStatus.done || status == UCL_TaskStatus.cancelled;

        // 區塊職責：距離最後一次動作幾天。
        // 物理意義：stale 判定的唯一輸入。updated_at 解析不出來時**回 -1 而不是 0** ——
        //          回 0 會讓一張時戳壞掉的單看起來「剛剛才動過」，而那正好是最該被看見的那種。
        public int DaysSinceUpdate(DateTime iNowUtc)
        {
            if (!DateTime.TryParse(updated_at, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal, out var aTs)) return -1;
            return (int)(iNowUtc - aTs).TotalDays;
        }

        /// <summary>某 persona 在這張單上的角色清單（可能不只一個）。</summary>
        public List<UCL_TaskRole> RolesOf(string iPersona)
        {
            var aList = new List<UCL_TaskRole>();
            if (string.IsNullOrEmpty(iPersona)) return aList;
            foreach (var p in participants)
                if (string.Equals(p.persona, iPersona, StringComparison.OrdinalIgnoreCase))
                    aList.Add(p.role);
            return aList;
        }

        /// <summary>這張單上掛著 QA 的人（可能多位）。空清單＝沒有人被指名驗收。</summary>
        public List<string> QaPersonas()
        {
            var aList = new List<string>();
            foreach (var p in participants)
                if (p.role == UCL_TaskRole.qa)
                    aList.Add(p.persona);
            return aList;
        }
    }
}
#endif
