
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 08/18 2026
// 自由時間 session 的 typed model（取代 Cmd_FreeTime 內散落的 JsonData 逐鍵讀寫）。
#if UNITY_EDITOR
namespace UCL.Core.EditorLib.AgentCommands
{
    // ===========================================================
    // 區塊職責：一場自由時間 session 的持久化資料模型。
    // 物理意義：對應 AgentCommands/FreeTime/sessions/<persona>.json（一 persona 一檔，開新場覆寫）。
    //          共通欄位（persona / session_id / start_ts / end_ts / until_local /
    //          active / end_reason / ended_at）在 UCL_SessionBase；這裡只加自由時間自己的。
    // 數值影響：序列化結果與 typed model 之前的手搭格式**逐鍵相同**
    //          （鍵的先後順序可能不同 —— 兩端都按鍵取值，不靠順序）。既有檔不需遷移。
    // ===========================================================
    /// <summary>
    /// 自由時間 session（`FreeTime/sessions/&lt;persona&gt;.json`）。
    /// 欄位命名規則與跨語言讀取端的約束見 <see cref="UCL_SessionBase"/> 的 remarks。
    /// </summary>
    public class UCL_FreeTimeSession : UCL_SessionBase
    {
        /// <summary>已擲過幾輪活動（自由時間專屬 —— 其他 session 類型沒有這個概念）。</summary>
        public int rounds = 0;

        // 區塊職責：本場「真的開始做了幾件活動」與最後一件是什麼。
        // 物理意義：原本只有 rounds（換骰次數），於是**「一直重骰卻什麼都沒做」在資料上不存在** ——
        //          沒有人能指出它，只能靠事後回想。有了 activities_done，那件事變成
        //          `rounds` 與 `activities_done` 的差，是一個可以被印出來、被比較的數字。
        // 數值影響：由 Cmd_FreeTimeActivity 遞增（活動入口唯一寫入端）；舊 session 沒有此欄位 → 0。
        /// <summary>本場實際開始過幾件活動（走 Cmd_FreeTimeActivity 才算）。</summary>
        public int activities_done = 0;
        /// <summary>最後一件開始的活動 id（空＝本場還沒開始任何活動）。</summary>
        public string activity = "";
    }
}
#endif
