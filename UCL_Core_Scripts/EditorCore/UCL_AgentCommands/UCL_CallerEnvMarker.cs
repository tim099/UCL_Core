// 區塊職責：呼叫端環境標記（Claude Code / Antigravity / Gemini / unknown）—— 執行器在開跑前設、在收尾時清。
// 物理意義：caller 端（senate CLI）抓得到 CLAUDECODE / ANTIGRAVITY_SESSION 等環境變數，常駐的 Unity Editor 抓不到
//          ⇒ 執行器從 args["_caller_env_marker"] 讀出來。主要來源是 per-cmd context（`UCL_AgentCmdContext.CallerEnvMarker`）；
//          本類的全域 slot 只給「拿不到 cmdId」的路徑（IMGUI 手動操作 / 直寫 queue.json）當 fallback。
// 數值影響：純記憶體，零 IO。
// ⚠ 全域 slot 在不同 persona queue 併行時會被後起的 cmd 覆蓋 ⇒ 有 cmdId 就一律走 Detect(cmdId)。
//   AsyncLocal 不能用（UniTask 不捕捉 ExecutionContext，併發流下全 LEAK）—— 唯一可行的是顯式索引。
#if UNITY_EDITOR
using System;

namespace UCL.Core.EditorLib.AgentCommands
{
    public static class UCL_CallerEnvMarker
    {
        /// <summary>全域 fallback slot（執行器每筆 cmd 開跑前設、finally 清）。</summary>
        public static string Current { get; set; }

        /// <summary>解析順序：per-cmd context → 全域 slot → in-process 環境變數 → "unknown"。</summary>
        public static string Detect(string iCmdId = null)
        {
            if (!string.IsNullOrEmpty(iCmdId))
            {
                var aCtx = UCL_AgentCmdContexts.Get(iCmdId);
                if (aCtx != null && !string.IsNullOrEmpty(aCtx.CallerEnvMarker))
                    return aCtx.CallerEnvMarker;
            }
            if (!string.IsNullOrEmpty(Current))
                return Current;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDECODE")))
                return "claude-code";
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTIGRAVITY_SESSION"))
             || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTIGRAVITY_USER_ID")))
                return "antigravity";
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
             || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_SESSION")))
                return "gemini";
            return "unknown";
        }
    }
}
#endif
