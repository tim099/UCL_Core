// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 07/29 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
//
// 區塊職責：把所有 Cmd 的機器可讀參數規格匯出成 commands_schema.json（Cmd 版入口）。
// 物理意義：與「Cmd 後台管理頁」的同步按鈕**等價** — 兩者呼叫同一個
//          UCL_CmdSchemaExporter.Export()，讓 agent 不必開 Editor UI 也能觸發同步。
//          新增／修改任何 Cmd 之後都該跑一次（見 Docs~/{lang}/UCL_EditorPage/UCL_AgentCommandsPage.md）。
// 數值影響：只寫 <RepoRoot>/AgentCommands/commands_schema.json，且內容未變時不落筆。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Agent Command：匯出所有 Cmd 的機器可讀 args schema，供 Python client 端預檢使用。
    ///
    /// <para>參數：無。（產物路徑固定 <c>&lt;RepoRoot&gt;/AgentCommands/commands_schema.json</c>，
    /// 刻意不開放覆寫 —— Python 端是照固定路徑找的，讓它可變只會製造「寫到別處所以永遠不同步」。）</para>
    /// </summary>
    public class Cmd_ExportCmdSchema : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "ExportCmdSchema";

        public override string ShortDescription =>
            "匯出所有 Cmd 的機器可讀 args schema (commands_schema.json)，供 Python client 端預檢；新增/修改 Cmd 後請跑一次。";

        public override string ArgsSchema =>
            "(無參數) — 產物固定寫到 <RepoRoot>/AgentCommands/commands_schema.json；內容未變則不寫檔。";

        public override string ExampleArgs => "";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/Plan/Plan_AgentCmd_Schema_Reflection_Export.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            // 區塊職責：委派給唯一實作並把結果寫進 _last_op.md 給 client 讀
            // 物理意義：本 Cmd 不自己組 JSON —— 面板按鈕與本 Cmd 必須產出逐字相同的東西，
            //          共用 static 方法是唯一能保證這件事的作法（各寫一份 = 本設計在治的病的下一例）。
            // 數值影響：Written=false 代表已同步、什麼都沒動，這也是成功結果不是錯誤。
            var r = UCL_CmdSchemaExporter.Export();

            var sb = new System.Text.StringBuilder();
            // 「因停用而跳過」必須與「已同步」分開報：兩者 Written 都是 false，但一個是**沒檢查**、
            // 一個是**檢查過且一致**。報成同一句就是同碼失聲（caller 分不出來，還以為同步好了）。
            if (r.SkippedDisabled)
            {
                sb.AppendLine("# ⏸ Cmd Schema 匯出已跳過（預檢停用中）");
                sb.AppendLine();
                sb.AppendLine("- 狀態：**未生成、未寫檔** —— 本機的 schema 預檢處於停用狀態");
                sb.AppendLine($"- 旗標檔：`{UCL_CmdSchemaExporter.DisableFlagPath}`");
                sb.AppendLine();
                sb.AppendLine("重新啟用：控制台 → Cmd 後台管理頁 → 勾回「啟用 schema 預檢」，或刪除上面那個旗標檔。");
                sb.AppendLine("停用期間 Python 端會跳過參數預檢（等同產物不存在），Cmd 執行本身不受影響。");
                UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_ChatTavernRender.WriteLastOp(sb.ToString());
                Debug.Log("[Cmd:ExportCmdSchema] skipped — schema preflight disabled on this machine.");
                return;
            }

            sb.AppendLine("# ✅ Cmd Schema 匯出完成");
            sb.AppendLine();
            sb.AppendLine(r.Written
                ? "- 狀態：**已更新產物**"
                : "- 狀態：**內容未變，未寫檔**（已是同步狀態）");
            sb.AppendLine($"- 產物：`{r.Path}`");
            sb.AppendLine($"- cmd 總數：{r.CommandCount}（其中 {r.SpecCount} 個有宣告 ArgsSpec）");
            sb.AppendLine($"- source_hash：`{r.SourceHash}`");
            sb.AppendLine();
            sb.AppendLine("Python 端 (`tavern_cmd.py`) 會讀這份產物做參數預檢；hash 不符時自動降級為不預檢。");
            // _last_op.md 是 client 讀 cmd 結果的共用管道（Cmd_AutoMessage / Cmd_Bartender 亦然），
            // 實作住在 ChatTavern 子命名空間 → 此處完整限定，不為了一行加 using。
            UCL.Core.EditorLib.AgentCommands.ChatTavern.UCL_ChatTavernRender.WriteLastOp(sb.ToString());

            Debug.Log($"[Cmd:ExportCmdSchema] {(r.Written ? "updated" : "unchanged")} — "
                    + $"{r.CommandCount} cmd(s) / {r.SpecCount} with spec → {r.Path}");
        }
    }
}
#endif
