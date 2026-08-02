// 區塊職責：列舉可實際承載 persona 的桌面 agent，與 persona 顯示歸屬及 bank 分離。
// 物理意義：此值是遠端視窗協作與 morning lock 的 routing metadata，不參與 bank / sender 身分決策。
// 數值影響：序列化固定為 enum 名稱；新增平台只追加 enum 值，不修改既有 lock 的字串值。
#if UNITY_EDITOR
using System;

namespace UCL.Core.EditorLib.AgentCommands
{
    public enum UCL_ActualAgent
    {
        None = 0,
        Codex,
        ClaudeCode,
        Antigravity,
    }

    public static class UCL_ActualAgentUtility
    {
        public static UCL_ActualAgent ParseOrNone(string value)
        {
            if (string.Equals(value, "Claude Code", StringComparison.OrdinalIgnoreCase)) return UCL_ActualAgent.ClaudeCode;
            return Enum.TryParse(value, true, out UCL_ActualAgent parsed) ? parsed : UCL_ActualAgent.None;
        }

        public static string ToStorageValue(UCL_ActualAgent value) => value == UCL_ActualAgent.None ? "" : value.ToString();

        /// <summary>轉為目前 Win32 視窗匹配使用的人類產品名稱；未特別映射者沿用 enum 名稱。</summary>
        public static string ToWindowTarget(UCL_ActualAgent value) => value == UCL_ActualAgent.ClaudeCode ? "Claude Code" : ToStorageValue(value);
    }
}
#endif
