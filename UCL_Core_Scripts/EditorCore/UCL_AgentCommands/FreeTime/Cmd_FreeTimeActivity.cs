// 區塊職責：Cmd_FreeTimeActivity —— **已搬到 Senate**（TASK-0360，2026-10-01）；本支只剩指路。
// 物理意義：活動層（pick／step／done）的本體在 SCP_Core `SCP_FreeTimeActivityOps`（`senate cmd free-time-activity`）。
//          ⛔ 這裡**不選活動、不代跑、不發酒館、不碰活動統計** —— 理由同 Cmd_FreeTime 檔頭（兩條路＝兩份狀態）。
// 數值影響：唯一寫入是回傳檔 letters/<persona>/cmd/freetime_activity.md（指路內容）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace UCL.Core.EditorLib.AgentCommands.FreeTime
{
    public class Cmd_FreeTimeActivity : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "FreeTimeActivity";

        public override string ShortDescription =>
            "⛔ 已搬到 Senate（TASK-0360）—— 改跑 `senate cmd free-time-activity`（不需要 Editor）。本支只印指路、非零退出。";

        public override string ArgsSchema =>
            "（已搬家）op=pick|step|done、persona、activity、step、step_args 等參數原樣帶到 `senate cmd free-time-activity`";

        public override string ExampleArgs => "op=pick;persona=basecamp;activity=chess";

        public override string HelpURL => "ucl_core:Skills~/ucl-free-time/SKILL.md";

        public override UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string aOp = GetArg(args, "op", "").Trim().ToLowerInvariant();
            string aPersona = GetArg(args, "persona", "").Trim();
            var aSb = new StringBuilder("senate cmd free-time-activity");
            aSb.Append(" --arg persona=").Append(string.IsNullOrEmpty(aPersona) ? "<P>" : aPersona);
            aSb.Append(" --arg op=").Append(string.IsNullOrEmpty(aOp) ? "<pick|step|done>" : aOp);
            foreach (string aKey in new[] { "activity", "step", "followed_dice" })
                if (args.TryGetValue(aKey, out string aVal) && !string.IsNullOrEmpty(aVal))
                    aSb.Append(" --arg ").Append(aKey).Append('=').Append(aVal);
            // ⚠ step_args 的寫法也換了：只剩 cmd 路由，值是 `--arg k=v …`（不再是 python 的 `--flag`）
            if (args.ContainsKey("step_args")) aSb.Append(" --arg step_args=\"--arg k=v …\"");
            if (args.ContainsKey("body")) aSb.Append(" --arg-file body=<檔>");
            string aCmd = aSb.ToString();

            if (!string.IsNullOrEmpty(aPersona))
            {
                string aPath = UCL_LettersPath.CmdPayload(aPersona, "freetime", "activity");
                var aR = new StringBuilder();
                aR.AppendLine($"# FreeTimeActivity op={aOp} persona={aPersona}");
                aR.AppendLine();
                aR.AppendLine("## ⛔ 已搬到 Senate（TASK-0360）");
                aR.AppendLine("- 活動層現在住在 `senate cmd free-time-activity`（**不需要 Unity Editor**）。");
                aR.AppendLine("- 本次**什麼都沒做**：沒選活動、沒代跑、沒發酒館、沒動活動統計。");
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
                catch (Exception e) { UnityEngine.Debug.LogWarning($"[FreeTimeActivity] 指路回傳落檔失敗 {aPath}: {e.Message}"); }
            }
            throw new Exception($"[FreeTimeActivity] 已搬到 Senate（TASK-0360），本次什麼都沒做 —— 改跑：{aCmd}");
        }
    }
}
#endif
