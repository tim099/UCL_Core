
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 08/18 2026
// 自由時間 session 的 typed model（取代 Cmd_FreeTime 內散落的 JsonData 逐鍵讀寫）。
#if UNITY_EDITOR
namespace UCL.Core.EditorLib.AgentCommands
{
    // ===========================================================
    // 區塊職責：一場自由時間 session 的持久化資料模型。
    // 物理意義：對應 AgentCommands/sessions/<persona>.json（**一 persona 一檔位**，開新場覆寫）。
    //          ⚠ 路徑不含 kind（TASK-0054 拍板⑤ 扁平化）—— kind 是 SCP_ActivitySession 的 json 欄位，
    //          於是「同一個人同時兩種 session」在資料形狀層就不可能。舊 <Kind>/sessions/ 不做 migration。
    //          共通欄位（persona / session_id / start_ts / end_ts / until_local /
    //          active / end_reason / ended_at）在 SCP_ActivitySession；這裡只加自由時間自己的。
    // 數值影響：序列化結果與 typed model 之前的手搭格式**逐鍵相同**
    //          （鍵的先後順序可能不同 —— 兩端都按鍵取值，不靠順序）。既有檔不需遷移。
    // ===========================================================
    /// <summary>
    /// 自由時間 session（`sessions/&lt;persona&gt;.json`，`kind` 欄位為 <c>FreeTime</c>）。
    /// 欄位命名規則與跨語言讀取端的約束見 <see cref="SCP.Core.Session.SCP_ActivitySession"/> 的 remarks。
    /// </summary>
    /// <remarks>
    /// ⚠ 基底是 **SCP 那側**的 <see cref="SCP.Core.Session.SCP_ActivitySession"/>（TASK-0127 ⑦，2026-09-04）——
    /// 共通欄位與 IO 都只剩一份實作，兩個宿主（Unity／Senate）讀寫同一份檔走同一條路。
    /// 本類別因此**不再是** <c>UnityJsonSerializable</c>：序列化走 `SCP_JsonMapper`（bool 原生、
    /// 未知鍵由基底的 `Raw` 保留），所以這裡不需要任何 `SerializeToJson` override。
    /// </remarks>
    public class UCL_FreeTimeSession : SCP.Core.Session.SCP_ActivitySession
    {
        /// <summary>已擲過幾輪活動（自由時間專屬 —— 其他 session 類型沒有這個概念）。</summary>
        public int rounds = 0;

        // 區塊職責：本場「真的開始做了幾件活動」與最後一件是什麼。
        // 物理意義：原本只有 rounds（換骰次數），於是「本場做了幾件」在資料上不存在，只能靠事後回想。
        //          ⚠ 這個欄位一度被拿來當**指控的來源**（`rounds - activities_done >= 2`
        //          就印「別再骰了」）—— Tim 2026-09-04 拍板移除那條警告：
        //          **自由時間不是強制活動**，而那個差在真實資料上響 3 次、被券帳打臉 3 次。
        //          ⇒ 本欄位現在**只是紀錄**，不是尺。要判「哪些活動很久沒做」請走
        //          `UCL_FreeTimeActivityStatsIO`（飢餓度，另一套，不經過這裡）。
        // 數值影響：由 Cmd_FreeTimeActivity 遞增（活動入口唯一寫入端）；舊 session 沒有此欄位 → 0。
        /// <summary>本場實際開始過幾件活動（走 Cmd_FreeTimeActivity 才算）。</summary>
        public int activities_done = 0;
        /// <summary>最後一件開始的活動 id（空＝本場還沒開始任何活動）。</summary>
        public string activity = "";
    }
}
#endif
