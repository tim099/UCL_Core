// 區塊職責：Cmd_GoodMorning — 早安流程在 Editor 端只剩的一步：step=audit（全 persona 對帳，唯讀）。
// 物理意義：早安四步（wake／brief／intro／catchup）的唯一入口是 `senate cmd morning-*`（不需要 Editor，
//          邏輯在 SCP_Core `SCP_Morning`）；Editor 版的 wake／brief／intro 已於 TASK-0353 刪除。
//          留下 audit 是因為 Senate 的 `wake-audit` 還委派到這裡（`Cmd_WakeAudit.UnityCmdType`）——
//          那一步讀的 registry 對帳邏輯（UCL_AwakeningService.AuditReport）還住在 Unity 端。
// 數值影響：回傳值落檔 AwakenInit/_goodmorning_audit.md；不寫任何 persona 狀態。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Awakening
{
    /// <summary>
    /// 早安流程的 Editor 端殘留步驟：step=audit（Senate `wake-audit` 委派進來）。
    /// <para>其餘步驟走 `senate cmd morning-wake --arg persona=&lt;P&gt;`，每步回傳檔指路下一步。</para>
    /// </summary>
    public class Cmd_GoodMorning : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "GoodMorning";

        public override string ShortDescription =>
            "早安流程的 Editor 端步驟：只剩 step=audit（全 persona 對帳，唯讀）。早安四步走 `senate cmd morning-*`。";

        public override string ArgsSchema =>
            "step=audit (必填) — 全 persona 對帳(唯讀) | 回傳落檔 AwakenInit/_goodmorning_audit.md | " +
            "wake/brief/intro 已移除 → senate cmd morning-wake / morning-brief / morning-intro";

        public override string ExampleArgs => "step=audit";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/Plan/completed/Plan_Awakening_Flow_Simplification.md";

        public override UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string aStep = GetArg(args, "step", "").Trim().ToLowerInvariant();
            if (aStep != "audit")
            {
                // 已刪的步驟要明確失敗並指路 —— 安靜回成功會讓呼叫端以為登入／廣播發生了。
                throw new Exception(
                    $"[GoodMorning] step 只剩 audit（got '{aStep}'）。wake／brief／intro 已移除（TASK-0353），"
                    + "改走 `senate cmd morning-wake --arg persona=<P>`（每步回傳檔指路下一步）。");
            }

            string aReport = UCL_AwakeningService.AuditReport()
                + "\n## next\n- 對帳有 ⚠/🔧 → 人工看該 persona 的 wakes/ 與 registry；全綠 → 無事。\n";
            string aPath = Path.Combine(UCL_AgentCommandsPath.DataRoot, "AwakenInit", "_goodmorning_audit.md");
            WritePayload(args, aPath, aReport);
            Debug.Log($"[GoodMorning] step=audit 完成 → {aPath}");
            return UniTask.CompletedTask;
        }

        static void WritePayload(IDictionary<string, string> iArgs, string iPath, string iReport)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iPath));
                File.WriteAllText(iPath, iReport, new UTF8Encoding(false));
                // 回報產出檔 → result 檔 outputs 欄，run_cmd 端隨 verdict 印路徑
                UCL_AgentCommandRunner.ReportOutputFile(iArgs, iPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GoodMorning] 回傳落檔失敗 {iPath}: {e.Message}");
            }
        }
    }
}
#endif
