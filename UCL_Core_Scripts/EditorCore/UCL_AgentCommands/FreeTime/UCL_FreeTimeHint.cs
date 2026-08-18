
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 08/18 2026
// 「你現在在自由時間中」的流程提示 —— 給任何活動類 Cmd 在自己的回傳值尾端掛一段。
#if UNITY_EDITOR
using System;
using System.Text;

namespace UCL.Core.EditorLib.AgentCommands
{
    // ===========================================================
    // 區塊職責：讓活動類 Cmd 自己回報「你在自由時間中，下一步該做什麼」。
    //
    // 物理意義：自由時間最容易斷在**活動做完那一刻** —— 手上剛有產物、注意力在產物上，
    //          而換骰指令在上一份回傳檔裡。原本的修法有兩條，兩條都比這條差：
    //            ① 在五個活動工具的收尾各加一段提示 → 那是**五個不同的收尾**，
    //               漏掉一個不會有人發現；
    //            ② 把活動流程抽離成 service 讓自由時間層重跑 → 產生**第二條流程**，
    //               而兩條流程漂移時兩邊都不報錯。
    //          Tim 2026-08-18 給的第三條最小：**Cmd 自己查 session，自己在回傳值多印一段。**
    //          沒有第二條流程、沒有五個落點，只有一個 helper 與一行呼叫。
    //
    // 數值影響：純輸出。不在自由時間時**一個字都不印**（免得無關的 Cmd 每次都多一段噪音 ——
    //          噪音會讓人開始略過整個區塊，那比沒有提示更糟）。
    //
    // 用法（活動類 Cmd 在組完自己的回傳值之後）：
    //   UCL_FreeTimeHint.Append(aReport, aPersona);
    // ⛔ 別掛在跟自由時間無關的 Cmd 上（commit / 記帳 / 登入）—— 見上面那句噪音。
    // ===========================================================
    public static class UCL_FreeTimeHint
    {
        /// <summary>
        /// 若 iPersona 此刻在自由時間中，往 ioReport 尾端附一段「▶ 下一步」；否則不動它。
        /// 回傳是否有附（呼叫端通常不需要，但「有沒有印」不該只能靠肉眼判斷）。
        /// </summary>
        public static bool Append(StringBuilder ioReport, string iPersona)
        {
            if (ioReport == null || string.IsNullOrEmpty(iPersona)) return false;
            try
            {
                // 判準走 session base 的唯一那條（active 且未過 end_ts）——
                // 只看 active 會把超時沒回來收工的人算成在線，然後對他印一段已經無效的指路。
                var aRunning = UCL_SessionService.FindRunning(iPersona);
                foreach (var aKv in aRunning)
                {
                    if (aKv.Key != UCL_SessionKind.FreeTime) continue;
                    var aS = aKv.Value;
                    aS.IsRunningAt(DateTime.Now, out DateTime? aEnd);
                    int aRemain = aEnd.HasValue
                        ? (int)Math.Max(0, (aEnd.Value - DateTime.Now).TotalMinutes) : 0;

                    ioReport.AppendLine();
                    ioReport.AppendLine($"## ▶ 你在自由時間中（到 {aS.until_local}，剩 {aRemain} 分）");
                    ioReport.AppendLine("- 這件活動還要再走一步 → 再跑一次同一支 Cmd（活動是一步一步的，不必一次做完）。");
                    ioReport.AppendLine($"- 這件活動告一段落 → `run FreeTimeActivity --arg op=done --arg persona={iPersona} [--arg-file body=<一句心得>]`");
                    ioReport.AppendLine($"- 之後換骰（**順便讀未讀訊息、順便跟同事講話**）→ `run FreeTime --arg step=next --arg persona={iPersona} [--arg-file body=<想說的話>]`");
                    ioReport.AppendLine("- **截止是軟的**：時間到不打斷進行中的活動；到期時換骰那一步會自己宣布收工並結算。");
                    return true;
                }
            }
            catch (Exception e)
            {
                // 提示失敗不該影響本體 —— 但也不靜默（靜默的話「沒印」與「查不到」同形）。
                UnityEngine.Debug.LogWarning($"[FreeTimeHint] 附掛失敗（{iPersona}）：{e.Message}");
            }
            return false;
        }
    }
}
#endif
