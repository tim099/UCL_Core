// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/04 2026
// 文件關聯：對應的多語系說明文件
// English: Docs~/en/UCL_EditorPage/UCL_AgentCommandsPage.md
// 日本語: Docs~/ja/UCL_EditorPage/UCL_AgentCommandsPage.md
// 简体中文: Docs~/zh-Hans/UCL_EditorPage/UCL_AgentCommandsPage.md
// 繁體中文: Docs~/zh-Hant/UCL_EditorPage/UCL_AgentCommandsPage.md
// Abstract base class for Agent Command handlers.
// 子類自動被 UCL_AgentCommandRegistry 反射掃描並註冊。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Agent Command Handler 抽象基底。
    ///
    /// 新增一條指令的標準寫法：寫一個繼承此類別的 class，覆寫 <see cref="CommandType"/> 與
    /// <see cref="ExecuteAsync"/>。Registry 會在 static ctor 內透過反射自動掃到並註冊。
    /// </summary>
    public abstract class UCL_AgentCommandHandlerBase
    {
        /// <summary>指令類型名稱（對應 queue.json 的 Type 欄位，大小寫不敏感）。</summary>
        public abstract string CommandType { get; }

        /// <summary>一行描述（用於 Page UI 列表，給人類看）。</summary>
        public virtual string ShortDescription => "";

        /// <summary>支援的 Args 格式說明（純文字 / Markdown，給 Page 顯示）。</summary>
        public virtual string ArgsSchema => "";

        /// <summary>HelpURL — Page 上「查看說明」按鈕跳轉的目標。可使用 ucl_core: / eov_docs: prefix。</summary>
        public virtual string HelpURL => "";

        /// <summary>實際執行邏輯（async）。</summary>
        public abstract UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token);
    }
}
#endif
