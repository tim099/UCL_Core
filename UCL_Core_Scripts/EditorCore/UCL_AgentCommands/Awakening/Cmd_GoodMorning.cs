// 區塊職責：Cmd_GoodMorning — 早安流程的 Cmd 入口（Plan_Awakening_Flow_Simplification §8.8-§8.9）。
//          同一支 Cmd 以 step 參數分步，每步回傳「下一步怎麼操作、傳哪些參數」（R16 next 導引）。
// 物理意義：實際邏輯全在 UCL_AwakeningService（static，後台頁共用，R14）；本檔只做參數解析、
//          步驟分派、回傳落檔。P1 已載 step=audit / step=brief；step=wake / step=intro 於 P2 施工，
//          在那之前誠實回報未遷移並指路現行 Python 流程 —— 不假裝、不靜默。
// 數值影響：audit 純唯讀；brief 經 UCL_ProcessCli spawn python 生成（寫檔者是 Python 端，R20）。
//          回傳 payload 落 <DataRoot>/AwakenInit/_goodmorning_last.md（人讀），
//          brief 路徑與行數必在回傳值內（Tim 2026-08-13 拍板）。
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
    /// 早安流程 Cmd（分步）。P1 支援 step=audit / step=brief；wake / intro 於 P2 遷入。
    ///
    /// <para>典型用法：</para>
    /// <code>
    /// # 全 persona 對帳（C# 推導 vs registry 快取 vs lock 實況，唯讀）
    /// python &lt;UCL_Core&gt;/Tools~/AgentCommands/run_cmd.py run GoodMorning --arg step=audit
    ///
    /// # 生成 wake brief（經 Cmd 觸發 python，R20 正常流程唯一通道；Editor 未開才直跑 awakening.py brief）
    /// python &lt;UCL_Core&gt;/Tools~/AgentCommands/run_cmd.py run GoodMorning --arg step=brief --arg persona=Template
    /// </code>
    /// <para>回傳落檔：<c>AwakenInit/_goodmorning_last.md</c>（含 brief 絕對路徑＋行數＋next 導引）。</para>
    /// </summary>
    public class Cmd_GoodMorning : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "GoodMorning";

        public override string ShortDescription =>
            "早安流程 Cmd（step 分步 + next 導引）。P1: audit/brief 已載；wake/intro P2 遷入（現行走 awakening.py morning）。";

        public override string ArgsSchema =>
            "step=audit|brief|wake|intro (必填) — audit: 全 persona 對帳(唯讀); brief: 生成 wake brief(需 persona); " +
            "wake/intro: P2 施工中, 現行請走 awakening.py morning | " +
            "persona=<name> — step=brief 必填 | " +
            "回傳落檔 AwakenInit/_goodmorning_last.md（brief 路徑與行數在其中）";

        public override string ExampleArgs => "step=brief;persona=Template";

        public override string HelpURL =>
            "ucl_core:Docs~/zh-Hant/Plan/Plan_Awakening_Flow_Simplification.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string aStep = GetArg(args, "step", "").Trim().ToLowerInvariant();
            string aPersona = GetArg(args, "persona", "").Trim();

            string aReport;
            switch (aStep)
            {
                case "audit":
                    aReport = UCL_AwakeningService.AuditReport();
                    aReport += "\n## next\n- 對帳有 ⚠/🔧 → 人工看該 persona 的 wakes/ 與 registry；全綠 → 無事。\n";
                    break;

                case "brief":
                {
                    if (string.IsNullOrEmpty(aPersona))
                        throw new Exception("[GoodMorning] step=brief 需要 --arg persona=<name>");
                    // 長跑段丟背景執行緒 —— spawn python + WaitForExit 會擋 Editor 主執行緒
                    var aResult = await UniTask.RunOnThreadPool(
                        () => UCL_AwakeningService.RunBrief(aPersona, nameof(Cmd_GoodMorning)),
                        cancellationToken: token);
                    var aSb = new StringBuilder();
                    aSb.AppendLine($"# GoodMorning step=brief persona={aPersona}");
                    aSb.AppendLine();
                    aSb.AppendLine(aResult.report);
                    if (aResult.ok)
                    {
                        aSb.AppendLine("## brief 摘要（QA 欄位/格式用）");
                        aSb.AppendLine("```");
                        aSb.AppendLine(UCL_AwakeningService.SummarizeBrief(aResult.briefPath));
                        aSb.AppendLine("```");
                        aSb.AppendLine("## next");
                        aSb.AppendLine($"- required: Read `{aResult.briefPath}`（接回身分 —— 這步不自動化）");
                        aSb.AppendLine("- 之後: 發 self-intro（P2 遷入 step=intro 前，現行走 run_cmd Tavern op=post）");
                    }
                    aReport = aSb.ToString();
                    if (!aResult.ok)
                    {
                        WritePayload(aReport);   // 失敗報告也落檔 —— 讓 caller 讀得到原因
                        throw new Exception($"[GoodMorning] brief 生成失敗（詳見 {PayloadPath()}）");
                    }
                    break;
                }

                case "wake":
                case "intro":
                    // P2 施工項 —— 誠實拒絕並指路，不留「看起來成功」的空殼。
                    throw new Exception(
                        $"[GoodMorning] step={aStep} 尚未遷移（P2 施工中，見 Plan_Awakening_Flow_Simplification §8.9）。"
                        + " 現行早安流程：python <UCL_Core>/Tools~/AgentCommands/awakening.py morning --persona <P>");

                default:
                    throw new Exception(
                        $"[GoodMorning] step 必為 audit|brief|wake|intro（got '{aStep}'）。ArgsSchema: {ArgsSchema}");
            }

            WritePayload(aReport);
            Debug.Log($"[GoodMorning] step={aStep} 完成 → {PayloadPath()}");
        }

        static string PayloadPath()
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "AwakenInit", "_goodmorning_last.md");

        static void WritePayload(string iReport)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PayloadPath()));
                File.WriteAllText(PayloadPath(), iReport, new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GoodMorning] 回傳落檔失敗: {e.Message}");
            }
        }
    }
}
#endif
