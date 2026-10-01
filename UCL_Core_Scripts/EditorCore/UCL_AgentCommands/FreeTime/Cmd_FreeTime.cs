// 區塊職責：Cmd_FreeTime —— **已搬到 Senate**（TASK-0360，2026-10-01）；本支只剩兩件事：
//          ① 舊入口被呼叫時**指路**（印出對應的 `senate cmd free-time` 指令、非零退出）
//          ② 把 FreeTime 這個 kind 登記給 Editor 的 UCL_SessionKindHost（`SessionClose` 收過期殘留要它）
// 物理意義：流程本體在 SCP_Core `SCP_FreeTimeFlow`（`senate cmd free-time`，不需要 Editor）。
//          ⛔ 這裡**不開場、不發券、不寫 session、不發酒館** —— 兩條路都能開場的話，
//          同一個人會在兩邊各有一份「現在在不在自由時間」，而兩份漂開時誰都不會叫。
// 數值影響：唯一寫入是回傳檔 letters/<persona>/cmd/freetime_<step>.md（指路內容）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace UCL.Core.EditorLib.AgentCommands.FreeTime
{
    public class Cmd_FreeTime : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "FreeTime";

        public override string ShortDescription =>
            "⛔ 已搬到 Senate（TASK-0360）—— 改跑 `senate cmd free-time`（不需要 Editor）。本支只印指路、非零退出。";

        public override string ArgsSchema =>
            "（已搬家）step=start|next|end|list|shuffle|show、persona 等參數原樣帶到 `senate cmd free-time`";

        public override string ExampleArgs => "step=start;persona=Template;until=23:59";

        public override string HelpURL => "ucl_core:Skills~/ucl-free-time/SKILL.md";

        public override UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string aStep = GetArg(args, "step", "").Trim().ToLowerInvariant();
            string aPersona = GetArg(args, "persona", "").Trim();
            string aCmd = RedirectLine(args, aStep, aPersona);
            if (!string.IsNullOrEmpty(aPersona))
            {
                string aPath = UCL_LettersPath.CmdPayload(aPersona, "freetime", string.IsNullOrEmpty(aStep) ? "moved" : aStep);
                var aR = new StringBuilder();
                aR.AppendLine($"# FreeTime step={aStep} persona={aPersona}");
                aR.AppendLine();
                aR.AppendLine("## ⛔ 已搬到 Senate（TASK-0360）");
                aR.AppendLine("- 自由時間的流程現在住在 `senate cmd free-time`（**不需要 Unity Editor**）。");
                aR.AppendLine("- 本次**什麼都沒做**：沒開場、沒發券、沒寫 session、沒發酒館。");
                aR.AppendLine("- 改跑：");
                aR.AppendLine("```bash");
                aR.AppendLine(aCmd);
                aR.AppendLine("```");
                try
                {
                    UCL_LettersPath.EnsurePayloadDir(aPath);
                    File.WriteAllText(aPath, aR.ToString(), new UTF8Encoding(false));
                    UCL_AgentCommandRunner.ReportOutputFile(args, aPath);
                }
                catch (Exception e) { UnityEngine.Debug.LogWarning($"[FreeTime] 指路回傳落檔失敗 {aPath}: {e.Message}"); }
            }
            // 非零退出：舊入口「成功」的話，呼叫端會以為場開了 —— 那正是兩份狀態的起點。
            throw new Exception($"[FreeTime] 已搬到 Senate（TASK-0360），本次什麼都沒做 —— 改跑：{aCmd}");
        }

        // 把呼叫端給的參數原樣翻成 senate cmd 的一行（只帶本支宣告過的那幾個名字）。
        static string RedirectLine(Dictionary<string, string> iArgs, string iStep, string iPersona)
        {
            var aSb = new StringBuilder("senate cmd free-time");
            aSb.Append(" --arg persona=").Append(string.IsNullOrEmpty(iPersona) ? "<P>" : iPersona);
            aSb.Append(" --arg step=").Append(string.IsNullOrEmpty(iStep) ? "<start|next|end|list|shuffle|show>" : iStep);
            foreach (string aKey in new[] { "until", "reason", "id", "count", "roll" })
                if (iArgs.TryGetValue(aKey, out string aVal) && !string.IsNullOrEmpty(aVal))
                    aSb.Append(" --arg ").Append(aKey).Append('=').Append(aVal);
            if (iArgs.ContainsKey("body")) aSb.Append(" --arg-file body=<檔>");
            return aSb.ToString();
        }

        // ===========================================================
        // 區塊職責：把「自由時間這個 kind 在 Editor 這側怎麼收工」登記給 UCL_SessionKindHost。
        // 物理意義：流程搬走了，但 Editor 的 `SessionClose`（`senate cmd sessions op=close` 目前委派給它）
        //          還要靠這筆登記才關得了一場過期的自由時間 ⇒ **登記留著，流程不留**。
        // 數值影響：零 IO。`SettleResidueAsync = null` ＝ 自由時間沒有金流結算（不是還沒接）。
        // ===========================================================
        [UnityEditor.InitializeOnLoadMethod]
        static void RegisterSessionKind()
            => UCL_SessionKindHost.Register(new UCL_SessionKindEntry
            {
                Kind = SCP.Core.Session.SCP_ActivitySessionKind.FreeTime,
                CmdName = "FreeTime",
                HasStepEnd = true,
                SettleResidueAsync = null,
            });
    }
}
#endif
