// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 09/05 2026
// Coding session 的 typed model（TASK-0058）。
#if UNITY_EDITOR
namespace UCL.Core.EditorLib.AgentCommands
{
    // ===========================================================
    // 區塊職責：一場「改 C#」施工 session 的持久化資料模型。
    // 物理意義：對應 AgentCommands/sessions/<persona>.json（一 persona 一檔位，`kind` 欄為 Coding）。
    //          共通欄位（persona / session_id / start_ts / end_ts / until_local / active /
    //          end_reason / ended_at）在 SCP_ActivitySession；這裡只加施工場自己的。
    //          ⚠ 本 kind **沒有預定時長** ⇒ `end_ts` 刻意留空。SCP_ActivitySession.IsRunningAt
    //          在 end_ts 解析不出來時回 true（只信 active），所以一場沒人 exit 的施工場
    //          **會一直擋住所有人** —— 那是設計（獨佔要有人負責交回），不是 bug。
    //          ⇒ 代價由「擋下時必須報得出持有者與退出指令原文」承擔，見 Cmd_Coding 的 blocked 區塊。
    // 數值影響：只多一個 `status` 鍵；舊檔沒有此欄位 → 空字串。
    // ===========================================================
    /// <summary>
    /// Coding session（`sessions/&lt;persona&gt;.json`，`kind` 欄位為 <c>Coding</c>）。
    /// </summary>
    /// <remarks>
    /// ⚠ `status` 是**這一場在改什麼**的一句話，進場必填。
    /// 它同時被寫進 persona lock 的 `now_status`（走 <c>UCL_AwakeningService.UpdateNowStatus</c>
    /// —— 那是唯一寫入通道）⇒ 顯示端（UCL_LoginStatusPage／catchup 在線清單）**不必認識本 kind**。
    /// 🩸 這裡刻意**不**另開一個顯示用欄位：多一個狀態欄就會有「兩個都活、內容不同」的那一天，
    /// 而那時沒有任何一層會出聲（我 2026-09-04 立的《無錨引用》）。
    /// ⇒ session 檔那份是**權威**（誰持有、在改什麼），lock 那份是**投影**（給人看）。
    /// </remarks>
    public class UCL_CodingSession : SCP.Core.Session.SCP_ActivitySession
    {
        /// <summary>這一場在改什麼（一句話，進場必填；可用 op=status 更新）。</summary>
        public string status = "";

        /// <summary>status 最後一次更新的時刻（本地時間字串，與 start_ts 同格式）。</summary>
        public string status_updated = "";

        /// <summary>退出時是不是走了 force（跳過編譯閘）—— 留給對帳，預設空。</summary>
        public string force_reason = "";
    }
}
#endif
