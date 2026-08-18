
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
    }
}
#endif
