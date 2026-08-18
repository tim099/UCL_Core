// 區塊職責：問題回報單的資料模型與三個列舉（type / severity / status）。
// 物理意義：對應 AgentCommands/BugReports/bugs.jsonl 的一行，與 reports/<index>.md 的 frontmatter。
// 數值影響：純資料，無 IO。列舉一律**用字串進出**（jsonl 有 python 讀取端，enum 序號跨語言沒有意義）。
// 設計沿革：Plan_BugReport_System.md（Tim 2026-08-18 拍板）。
#if UNITY_EDITOR
using System;

namespace UCL.Core.EditorLib.AgentCommands.BugReport
{
    // ===========================================================
    // 區塊職責：回報的**種類** —— 這套系統收的不只是程式壞掉。
    // 物理意義：文件過時、提示缺一半、流程多繞三步，代價與 bug 一樣（下一個人白花時間），
    //          而且更常發生。沒有這一欄的話它們沒有落點，只會發在酒館閒聊裡然後沉掉。
    // 數值影響：只影響分類與顯示；不影響 severity（見 UCL_BugSeverity 的註解）。
    // ===========================================================
    public enum UCL_BugType
    {
        /// <summary>程式行為錯誤。</summary>
        Bug,
        /// <summary>文件與現況不符 / 過時。</summary>
        Doc,
        /// <summary>提示缺一半、錯誤訊息指錯地方、容易踩的坑。</summary>
        Friction,
        /// <summary>不算壞，但流程可以少幾步。</summary>
        Suggestion,
    }

    // ===========================================================
    // 區塊職責：嚴重度 —— 用「**現在誰被怎樣了**」定義，不是用「什麼東西壞了」。
    // 物理意義：原 RFC 的五級（blocker/critical/major/minor/trivial）在 4~8 人的團隊會退化成
    //          一片 major（預設值就是 major，填的人懶得想就吃預設）⇒ 等於沒有分級。
    // ⭐ 選這個軸的副作用：**換了 UCL_BugType 也不必重寫分級表** ——
    //   過時的文件天生就是 Wrong（看起來正常、講的是假話、讀的人會照著做），
    //   缺一半的提示也是 Wrong（不報錯，但把人引去查錯的地方）。
    // 數值影響：預設 Wrong（見 Cmd_BugReport）—— 因為實測上最常撞的就是這一格。
    // ===========================================================
    public enum UCL_BugSeverity
    {
        /// <summary>現在有人被它擋住，做不下去。</summary>
        Blocking,
        /// <summary>會產出錯的結果但還能跑 —— 「看起來正常但講的是假話」。</summary>
        Wrong,
        /// <summary>會嘴，但不會騙人。</summary>
        Annoying,
    }

    // ===========================================================
    // 區塊職責：單子的生命週期。
    // 物理意義：Stale **只能由掃描自動標**，不開放人手動設 ——
    //          人手動能標的狀態只會有人記得標一次，然後再也沒有。
    // 數值影響：Resolved / WontFix / Duplicate 皆視為已關（不進 open 讀數）。
    // ===========================================================
    public enum UCL_BugStatus
    {
        Open,
        InProgress,
        Stale,
        Resolved,
        WontFix,
        Duplicate,
    }

    /// <summary>
    /// 一張問題回報單。對應 <c>bugs.jsonl</c> 的一行；<c>reports/&lt;index&gt;.md</c> 是它的人可讀投影。
    /// </summary>
    /// <remarks>
    /// ⚠ **本型別刻意沒有任何 bool 欄位。** `UnityJsonSerializable` 會把 bool 寫成
    /// <c>"True"</c>/<c>"False"</c> 字串，而 python 端讀到的 <c>"False"</c> 是 **truthy**
    /// （2026-08-18 實證：`freetime.py` 因此把已收工的 session 判成還在跑，且完全不報錯）。
    /// 要加 bool 之前先讀 <c>UCL_SessionBase.SerializeToJson</c> 的註解，並照它 override。
    /// 目前所有狀態都用 <see cref="UCL_BugStatus"/> 字串表達，繞開整個問題。
    /// </remarks>
    public class UCL_BugReportEntry
    {
        // ⚠ 欄位名 = JSON 鍵名（FieldNameUnityVer 只脫 m_）—— 改名等於改 wire format，python 端會讀不到。
        public int index = 0;
        public string type = nameof(UCL_BugType.Bug).ToLowerInvariant();
        public string severity = nameof(UCL_BugSeverity.Wrong).ToLowerInvariant();
        public string status = nameof(UCL_BugStatus.Open).ToLowerInvariant();
        public string title = "";
        public string description = "";
        /// <summary>硬證據：error code / log 行號 / round-trip diff / 重現指令 / Cmd_Invoke 回傳值。必填。</summary>
        public string evidence = "";
        public string repro_steps = "";
        public string expected = "";
        public string actual = "";
        public string component = "";
        public string reporter = "";
        /// <summary>認領人（status=in_progress 時有值）。</summary>
        public string assignee = "";
        public string resolution = "";
        public string resolution_note = "";
        public string commit_sha = "";
        /// <summary>建立時刻（UTC ISO8601）。</summary>
        public string created_at = "";
        /// <summary>最後一次狀態變動時刻（UTC ISO8601）—— stale 判定就看它。</summary>
        public string updated_at = "";

        /// <summary>已關（不進 open 讀數）。Stale 不算已關 —— 它是 open 的一個更難看的名字。</summary>
        public bool IsClosed()
        {
            return string.Equals(status, "resolved", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "wontfix", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "duplicate", StringComparison.OrdinalIgnoreCase);
        }

        // 區塊職責：距離最後一次動作幾天。
        // 物理意義：stale 判定的唯一輸入。updated_at 解析不出來時**回 -1 而不是 0** ——
        //          回 0 會讓一張時戳壞掉的單看起來「剛剛才動過」，那正好是最該被看見的那種。
        public int DaysSinceUpdate(DateTime iNowUtc)
        {
            if (!DateTime.TryParse(updated_at, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal, out var aTs)) return -1;
            return (int)(iNowUtc - aTs).TotalDays;
        }
    }
}
#endif
