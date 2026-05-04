
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/05 2026
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/04 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 最基礎的 DebugLog Command 作為範例。
    /// 職責：從 Args 中提取 "msg" 並輸出到 Unity Console 控制台。
    /// 物理意義：提供開發者或 Agent 的連線、運行與日誌測試工具。
    /// 數值影響：無修改任何遊戲或編輯器狀態，純粹輸出 Log。
    /// </summary>
    public class Cmd_DebugLog : UCL_AgentCommandHandlerBase
    {
        /// <summary>
        /// [職責] 指令類型識別字串。
        /// </summary>
        public override string CommandType => "DebugLog";

        /// <summary>
        /// [職責] 指令功能簡介（給 Page 列表顯示）。
        /// </summary>
        public override string ShortDescription => "Prints the provided message to the Unity Console.";

        /// <summary>
        /// [職責] 支援的參數說明，格式為 Key=描述。
        /// </summary>
        public override string ArgsSchema => "msg=The text message to print in the console (optional, default: 'Hello World')";

        /// <summary>
        /// [職責] 開啟說明的 URL。
        /// </summary>
        public override string HelpURL => "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_DebugLog.md";

        /// <summary>
        /// [職責] 執行核心邏輯。
        /// [物理意義] 將參數中的內容解讀並呈現到開發者日誌中。
        /// [數值影響] 僅影響日誌輸出，不變更資料。
        /// </summary>
        /// <param name="args">鍵值對字典參數，包含 msg</param>
        /// <param name="token">非同步取消權杖</param>
        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            // 區塊職責：提取 msg 參數並執行 Debug.Log 呼叫
            // 物理意義：向 Unity 控制台寫入一筆等級為 Log 的資訊
            // 數值影響：無
            string msg = args != null && args.TryGetValue("msg", out var text) ? text : "Hello World";
            Debug.Log($"[AgentCmd:DebugLog] {msg}");

            await UniTask.CompletedTask;
        }
    }
}
#endif
