
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 08/18 2026
// 「你現在在自由時間中」的流程提示 —— 給任何活動類 Cmd 在自己的回傳值尾端掛一段。
#if UNITY_EDITOR
using System.Text;

namespace UCL.Core.EditorLib.AgentCommands
{
    // ===========================================================
    // 區塊職責：Editor 側活動類 Cmd（Cmd_Sculpture／Cmd_DocEdit）掛「你在自由時間中，下一步該做什麼」的入口。
    // 物理意義：判準與文字的**唯一實作**在 SCP_Core `SCP_FreeTimeHint`（TASK-0360 起本支只委派）。
    //   🩸 此前兩支檔頭都寫著「文字與另一份逐字相同，改一份要改另一份」—— 那是一句要靠記得的規矩，
    //     而自由時間搬到 Senate 那天，SCP 版改指 `senate cmd free-time`，本支還在教 `run FreeTime`。
    //     ⇒ 修法不是再抄一次，是讓這裡**沒有文字可以過期**。
    // 數值影響：純輸出；不在自由時間時一個字都不印（理由見 SCP_FreeTimeHint 檔頭：噪音會讓人略過整段）。
    // 用法：UCL_FreeTimeHint.Append(aReport, aPersona);
    // ===========================================================
    public static class UCL_FreeTimeHint
    {
        /// <summary>若 iPersona 此刻在自由時間中，往 ioReport 尾端附一段「▶ 下一步」；回傳有沒有附。</summary>
        public static bool Append(StringBuilder ioReport, string iPersona)
        {
            bool aDone = SCP.Core.Session.SCP_FreeTimeHint.Append(ioReport, UCL_AgentCommandsPath.ScpDataRoot, iPersona, out string aWarn);
            // 提示失敗不該影響本體 —— 但也不靜默（靜默的話「沒印」與「查不到」同形）。
            if (!string.IsNullOrEmpty(aWarn)) UnityEngine.Debug.LogWarning($"[FreeTimeHint] {aWarn}");
            return aDone;
        }
    }
}
#endif
