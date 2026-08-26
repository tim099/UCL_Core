// 區塊職責：任務單的資料模型與四個列舉（type / priority / status / role）。
// 物理意義：對應 AgentCommands/Tasks/tasks/<index>.md 的 frontmatter。
//          Task 是**跨人協作的交付承諾**；只有自己要記住的事留在見叢（`_keys_open.md`），
//          分流判準是一句當下答得出來的話：「有沒有第二個人在等這件事？」
// 數值影響：純資料，無 IO。列舉一律**用字串進出** —— 這份資料有 python 讀取端，
//          enum 序號跨語言沒有意義（BugReport 同一條規矩，照抄不改）。
// 設計沿革：Plan_Task_Management_System.md（gura 撰寫 / Tim 2026-08-24 拍板；
//          RFC 酒館 seq 13303 → 評審 13306 → 收斂 13307 → 邊界 13308 → 計畫確認 13310）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace UCL.Core.EditorLib.AgentCommands.TaskMgmt
{
    /// <summary>任務種類。</summary>
    public enum UCL_TaskType
    {
        Feature,
        Improvement,
        Refactor,
        /// <summary>技術調研 —— 產出是「知道了什麼」而不是「做好了什麼」。</summary>
        Spike,
        Subtask,
    }

    public enum UCL_TaskPriority
    {
        Urgent,
        High,
        Normal,
        Low,
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
        //   兩個守衛擋它流進資料：StatusEnum() 不把 "all" 當合法 status、
        //   UCL_TaskManagerPage.ApplyStatus 拒寫 all —— 少任何一個，`status: all` 就會落盤。
        all,
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
        Dev,
        Design,
        QA,
        PM,
        Reviewer,
        Sound,
        Art,
    }

    /// <summary>一位參與者：誰、什麼身分、什麼時候被指派。</summary>
    public class UCL_TaskParticipant
    {
        public string persona = "";
        public string role = "dev";
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
    /// 狀態一律用 <see cref="UCL_TaskStatus"/> 的字串表達。
    /// </remarks>
    public class UCL_TaskEntry
    {
        // ⚠ 欄位名 = frontmatter 鍵名 —— 改名等於改 wire format，python 端與後台頁會讀不到。
        public int index = 0;
        public string type = "feature";
        public string priority = "normal";
        public string status = "todo";
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

        // 區塊職責：`status` 的 enum 視圖（wire 欄位仍是字串 —— 這是讀法不是搬家）。
        // 物理意義：解析不出（手改壞檔／未知字串）回 **null**，不回任何合法狀態 ——
        //   「壞值」跟六種合法狀態的任何一種同形，都會讓壞檔看起來像一張正常的單。
        // ⚠ TryParse 會把 "5" 這種數字字串解析成功 —— IsDefined 擋掉（數字不是狀態）；
        // ⚠ `all` 是篩選成員不是狀態 —— 這裡是守衛之一（見 enum 上的註解），一樣回 null。
        public UCL_TaskStatus? StatusEnum()
        {
            if (System.Enum.TryParse<UCL_TaskStatus>((status ?? "").Trim(), true, out var aS)
                && System.Enum.IsDefined(typeof(UCL_TaskStatus), aS)
                && aS != UCL_TaskStatus.all)
                return aS;
            return null;
        }

        public string ParticipantsName => participants.ConcatToString();
        /// <summary>已關（不進 open 讀數）。</summary>
        public bool IsClosed()
            => string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

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
        public List<string> RolesOf(string iPersona)
        {
            var aList = new List<string>();
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
                if (string.Equals(p.role, "qa", StringComparison.OrdinalIgnoreCase))
                    aList.Add(p.persona);
            return aList;
        }
    }
}
#endif
