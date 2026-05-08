
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/05 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
//
// 區塊職責：匯出當前 Registry 中所有 Agent Command Handler 的 metadata 為單一 Markdown 檔。
// 物理意義：Page 上「Export Cmd Catalog」按鈕的 Cmd 等價版本 — 讓 Agent 可以從 queue.json 觸發
//          同樣的匯出，產生「給 LLM 一次掃過就知道有哪些指令可用」的目錄檔。
// 數值影響：在指定 outputPath 寫入或覆寫一份 Markdown；不修改任何遊戲狀態。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Agent Command：匯出當前所有已註冊 Handler 的 metadata 為單一 Markdown 檔。
    /// 與 <see cref="UCL.Core.EditorLib.Page.UCL_AgentCommandsPage"/> 上的 Export Cmd Catalog 按鈕等價。
    ///
    /// 參數：
    /// - <c>outputPath</c>（選填）：輸出檔案路徑，相對專案根目錄，預設 <c>AgentCommands/commands_catalog.md</c>
    /// </summary>
    public class Cmd_ExportCommandCatalog : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "ExportCommandCatalog";

        public override string ShortDescription =>
            "Export all registered Agent Command handlers to a single Markdown catalog file.";

        public override string ArgsSchema =>
            "outputPath=Output file path relative to project root (default AgentCommands/commands_catalog.md)";

        /// <summary>Page「Fill Example」按鈕一鍵填入用 — 用預設輸出路徑匯出 catalog。</summary>
        public override string ExampleArgs => "outputPath=AgentCommands/commands_catalog.md";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_ExportCommandCatalog.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            // 區塊職責：解析 outputPath、收集 handlers、呼叫共用的 RenderCatalogMarkdown，寫檔
            // 物理意義：Cmd 與 Page button 共用 Catalog 渲染邏輯，避免不一致
            // 數值影響：只在指定路徑建立 / 覆寫 Markdown
            string outputPath = args != null && args.TryGetValue("outputPath", out var p) ? p : null;
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = "AgentCommands/commands_catalog.md";
            }

            string projectRoot = GetProjectRoot();
            string absOut = Path.IsPathRooted(outputPath)
                ? outputPath
                : Path.Combine(projectRoot, outputPath);
            string outDir = Path.GetDirectoryName(absOut);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            var handlers = UCL_AgentCommandRegistry.ListHandlers();
            string content = RenderCatalogMarkdown(handlers);
            File.WriteAllText(absOut, content, new UTF8Encoding(false));

            Debug.Log($"[Cmd:ExportCommandCatalog] Exported {handlers.Count} command(s) → {outputPath}");

            await UniTask.CompletedTask;
        }

        // ===========================================================
        // 共用渲染邏輯（被 Page 按鈕與本 Cmd 同時呼叫）
        // ===========================================================

        /// <summary>
        /// 把所有 handler 的 metadata 渲染成單一 Markdown 字串。
        /// Page 端 Export 按鈕也呼叫此方法，確保兩處輸出一致。
        /// </summary>
        public static string RenderCatalogMarkdown(IReadOnlyList<UCL_AgentCommandHandlerBase> handlers)
        {
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine("title: Agent Commands Catalog (Auto-Generated)");
            sb.AppendLine("description: 列出當前所有已註冊的 Agent Command Handler 與其 metadata；給 AI agent 一次掃過就知道有哪些指令可用");
            sb.AppendLine($"generated: {DateTime.Now:yyyy-MM-ddTHH:mm:ss}");
            sb.AppendLine($"total_commands: {handlers.Count}");
            sb.AppendLine("source: Cmd_ExportCommandCatalog or UCL_AgentCommandsPage \"Export Cmd Catalog\" button");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("# 🤖 Agent Commands Catalog");
            sb.AppendLine();
            sb.AppendLine($"> 由 [Cmd_ExportCommandCatalog](../CardGame/Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Cmd_ExportCommandCatalog.cs) 或 [UCL_AgentCommandsPage](../CardGame/Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_AgentCommandsPage.cs) 的 **Export Cmd Catalog** 按鈕產出。");
            sb.AppendLine($">");
            sb.AppendLine($"> 共 **{handlers.Count}** 個 Handler 自動註冊（reflection-discovered from `UCL_AgentCommandHandlerBase` 子類）。");
            sb.AppendLine();
            sb.AppendLine("## 速查表");
            sb.AppendLine();
            sb.AppendLine("| CommandType | 描述 | Args Schema |");
            sb.AppendLine("|---|---|---|");
            foreach (var h in handlers)
            {
                string typeCol = $"`{h.CommandType}`";
                string desc = string.IsNullOrEmpty(h.ShortDescription) ? "—" : EscapeTableCell(h.ShortDescription);
                string argsCol = string.IsNullOrEmpty(h.ArgsSchema) ? "—" : EscapeTableCell(h.ArgsSchema);
                sb.AppendLine($"| {typeCol} | {desc} | {argsCol} |");
            }
            sb.AppendLine();
            sb.AppendLine("## 詳細 Metadata");
            sb.AppendLine();
            foreach (var h in handlers)
            {
                Type t = h.GetType();
                sb.AppendLine($"### `{h.CommandType}`");
                sb.AppendLine();
                sb.AppendLine($"- **Handler Class**: `{t.FullName}`");
                sb.AppendLine($"- **Assembly**: `{t.Assembly.GetName().Name}`");
                if (!string.IsNullOrEmpty(h.ShortDescription))
                {
                    sb.AppendLine($"- **Short Description**: {h.ShortDescription}");
                }
                if (!string.IsNullOrEmpty(h.HelpURL))
                {
                    sb.AppendLine($"- **HelpURL**: `{h.HelpURL}`");
                }
                if (!string.IsNullOrEmpty(h.ArgsSchema))
                {
                    sb.AppendLine();
                    sb.AppendLine("**Args Schema**:");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(h.ArgsSchema);
                    sb.AppendLine("```");
                }
                sb.AppendLine();
                sb.AppendLine("**queue.json 範例**:");
                sb.AppendLine();
                sb.AppendLine("```json");
                sb.AppendLine("{");
                sb.AppendLine($"  \"Id\": \"{DateTime.Now:yyyyMMdd-HHmmss}-{h.CommandType.ToLower()}\",");
                sb.AppendLine($"  \"Type\": \"{h.CommandType}\",");
                sb.AppendLine("  \"Mode\": \"OneShot\",");
                sb.AppendLine("  \"Executed\": false,");
                sb.AppendLine("  \"Args\": {}");
                sb.AppendLine("}");
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
            sb.AppendLine("## 觸發方式");
            sb.AppendLine();
            sb.AppendLine("1. **編輯器 UI**：開啟 `UCL_AgentCommandsPage` → Add Command 表單選 Type → Add → Run Pending");
            sb.AppendLine("2. **直接編輯 queue.json**：複製本檔案各指令的 `queue.json 範例` 區塊填上 Args，存到 `AgentCommands/queue.json`，按 `Tools/UCL/Agent Commands/Run Pending`");
            sb.AppendLine("3. **Python 包裝器**（推薦給 Agent，含 lock-file 自動觸發）：見 [run_cmd.py](../Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py)");
            sb.AppendLine("4. **Unity 批次模式**（headless / CI）：`unity.exe -batchmode -projectPath <path> -executeMethod UCL.Core.EditorLib.AgentCommands.UCL_AgentCommandRunner.Menu_RunPending -quit`");
            sb.AppendLine();
            sb.AppendLine("詳見 [AgentCommands_Workflow](../docs/Workflows/AgentCommands_Workflow.md)。");
            return sb.ToString();
        }

        // 區塊職責：取得 Unity 專案根目錄（CardGame/）
        // 物理意義：Application.dataPath 為 CardGame/Assets，往上 1 層即 Unity project root。
        //          輸出 commands_catalog.md 落在 CardGame/AgentCommands/，避開外層 git root + submodule。
        static string GetProjectRoot()
        {
            return UCL_RepoPath.UnityProjectRoot;
        }

        static string EscapeTableCell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ");
        }
    }
}
#endif
