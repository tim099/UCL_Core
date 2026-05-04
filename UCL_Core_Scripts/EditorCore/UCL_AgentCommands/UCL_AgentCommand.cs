
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/04 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
// Editor-only Agent Command system (framework layer).
// agent 透過 queue.json 排隊指令，使用者在 Editor 內按按鈕觸發。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 單一指令的執行模式。
    /// </summary>
    public enum UCL_AgentCommandMode
    {
        /// <summary>一次性指令：執行成功後會被標記為 Executed，下次按 Run 會跳過。</summary>
        OneShot,
        /// <summary>可重複指令：每次按 Run 都會再執行一次。</summary>
        Repeatable,
    }

    /// <summary>
    /// 一筆 Agent Command 的完整資料模型（對應 queue.json 內的單一條目）。
    /// </summary>
    [Serializable]
    public class UCL_AgentCommand
    {
        /// <summary>唯一 ID（建議用 timestamp + 短名）</summary>
        public string Id;
        /// <summary>指令類型（對應 UCL_AgentCommandRegistry 註冊的 key）</summary>
        public string Type;
        /// <summary>OneShot / Repeatable</summary>
        public UCL_AgentCommandMode Mode = UCL_AgentCommandMode.OneShot;
        /// <summary>已執行成功的次數。OneShot 成功後會直接被 runner 從 queue 中移除，因此此欄位主要對 Repeatable 有意義。</summary>
        public int RunCount = 0;
        /// <summary>選填：給 handler 的參數（key/value 字串對）</summary>
        public Dictionary<string, string> Args = new();

        /// <summary>建立時間（ISO 8601）</summary>
        public string CreatedAt;
        /// <summary>上次執行時間（null 表示尚未執行）</summary>
        public string LastRunAt;
        /// <summary>上次執行結果："Success" / "Failed" / null</summary>
        public string LastRunResult;
        /// <summary>上次執行錯誤訊息（成功時為 null）</summary>
        public string LastRunError;

        /// <summary>選填：給人類看的描述（agent 留下的備註）</summary>
        public string Description;
    }

    /// <summary>
    /// queue.json 的根結構。
    /// </summary>
    [Serializable]
    public class UCL_AgentCommandQueueData
    {
        public List<UCL_AgentCommand> Commands = new();
    }
}
#endif
