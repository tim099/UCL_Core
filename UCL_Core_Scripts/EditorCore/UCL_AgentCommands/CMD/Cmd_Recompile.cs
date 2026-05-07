// 區塊職責：觸發 Unity 重新編譯 scripts — 配合 Python `run_cmd.py recompile` 子命令做「等到 compile 完成才視為成功」。
// 物理意義：本 Cmd 純呼叫 CompilationPipeline.RequestScriptCompilation()，立刻返回 Success；
//          編譯實際完成的事件（與錯誤計數）由 UCL_CompileErrorTracker 寫進
//          AgentCommands/.compile_status.json，Python 端 poll mtime + 內容判定真完成。
// 數值影響：觸發 domain reload — 任何 in-flight async Cmd 都會被中斷；故本 Cmd 設計為「丟出請求即返回」。
//          不要在 Cmd 內 await EditorApplication.isCompiling，會被 reload 殺掉。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 通知 Unity 重新編譯所有 scripts。本 Cmd 僅發出請求並立即返回；實際 compile 完成事件
    /// 由 <c>UCL_CompileErrorTracker</c> 寫入 <c>AgentCommands/.compile_status.json</c>，Python 端 poll mtime 判定。
    /// </summary>
    /// <remarks>
    /// 推薦走 <c>python run_cmd.py recompile</c> — 它會 record pre-mtime → submit Cmd → 等 mtime 推進 →
    /// 讀 errors / warnings 報告。直接 submit 本 Cmd 不會等到 compile 完，需自行 poll。
    /// </remarks>
    public class Cmd_Recompile : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Recompile";

        public override string ShortDescription =>
            "Trigger Unity script recompile (use Python `recompile` subcommand to wait until compile finishes).";

        public override string ArgsSchema =>
            "refresh=Whether to also call AssetDatabase.Refresh() before requesting recompile (true|false, default true)";

        /// <summary>Page「Fill Example」按鈕一鍵填入用 — 預設參數即可觸發完整 refresh + recompile。</summary>
        public override string ExampleArgs => "refresh=true";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Recompile.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            // 區塊職責：先 AssetDatabase.Refresh 偵測檔案變動（特別是 agent 直接寫檔的場景），再請求重編
            // 物理意義：Refresh 會掃 Assets/ 偵測新增 / 修改 / 刪除的 .cs，產生 import 事件 → 觸發 compile;
            //          RequestScriptCompilation 強制 compile pipeline 跑一次（即使無檔案變動也會跑）
            // 數值影響：兩者結合確保「不論 Editor 是否在背景」都會編譯
            bool doRefresh = true;
            if (args != null && args.TryGetValue("refresh", out var r))
            {
                doRefresh = !string.Equals(r, "false", System.StringComparison.OrdinalIgnoreCase);
            }

            if (doRefresh)
            {
                Debug.Log("[AgentCmd:Recompile] AssetDatabase.Refresh()");
                AssetDatabase.Refresh();
            }
            Debug.Log("[AgentCmd:Recompile] CompilationPipeline.RequestScriptCompilation()");
            CompilationPipeline.RequestScriptCompilation();

            // 注意：不要 await EditorApplication.isCompiling，domain reload 會殺掉 async；
            //       Python 端 poll .compile_status.json 為唯一可靠的「等待完成」管道
            await UniTask.CompletedTask;
        }
    }
}
#endif
