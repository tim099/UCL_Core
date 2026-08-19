// 區塊職責：本地 LLM 管理工具（llm_admin.py）的 C# 端唯一呼叫點 —— AdminPage（未來含 Cmd）共用。
// 物理意義：async spawn `python <UCL_Core>/Tools~/AgentCommands/llm_admin.py <op ...>`，抓 stdout/stderr。
//          跑在 thread pool，不佔 Editor main thread（模型下載動輒數分鐘，卡主執行緒＝Editor 凍結）。
// 數值影響：本 runner 不解讀語意，只負責「跑起來、不留孤兒、把輸出帶回主執行緒」。
//          安裝類 op 的逾時另給（見 UCL_LLMModelAdminPage 的呼叫端）—— 下載慢不是異常。
// 設計取捨：
//   · **Process 樣板走既有的 UCL_ProcessCli**（不再手刻一份）—— 它已經處理了 deadlock、逾時 kill、
//     UTF-8 讀取與 UCL_ProcessRegistryService 登記。手刻的那份少掉的正是那些踩過坑才補上的防護。
//   · **不由 Editor 啟動 ollama 服務** —— 那是一顆常駐 process，domain reload 會清掉 C# 端的
//     控制權但 OS 層的它不會死（屍潮）。服務生命週期歸使用者/OS，本頁只查得到、打得到。
//   · script 路徑走 UCL_RepoPath.CoreTool，不硬編掛載位置（跨專案必壞，且是靜默壞）。
#if UNITY_EDITOR
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace UCL.Core.EditorLib.AgentCommands.LLMAdmin
{
    /// <summary>llm_admin.py 的執行結果。</summary>
    public struct LLMAdminRunResult
    {
        public bool Launched;      // process 是否成功啟動
        public int ExitCode;       // 進程 exit code（未啟動 = -1）
        public string Stdout;      // 標準輸出（python 印的 json/text）
        public string Stderr;      // 標準錯誤
        public string Error;       // C# 端啟動/例外訊息（成功則空）

        /// <summary>成功與否 —— exit 0 才算。⚠ 「有輸出」不等於成功，python 失敗時也會印東西。</summary>
        public bool Ok => Launched && ExitCode == 0;

        /// <summary>顯示用：優先 stdout，退回 error/stderr。</summary>
        public string DisplayText =>
            !string.IsNullOrEmpty(Stdout) ? Stdout
            : !string.IsNullOrEmpty(Error) ? Error
            : !string.IsNullOrEmpty(Stderr) ? Stderr
            : "(無輸出)";
    }

    /// <summary>
    /// 本地 LLM 模型管理 python 工具的 async 執行器。
    /// <see cref="Page.UCL_LLMModelAdminPage"/> 使用；日後若開 Cmd_LLMAdmin 也走本 runner，
    /// 確保「人在 Editor 點按鈕」與「agent 走 Cmd」是同一條路徑（兩條路會各自漂）。
    /// </summary>
    public static class UCL_LLMAdminRunner
    {
        const string PROC_TAG = "llm_admin_py";       // Process 註冊中心的 tag（硬規則：每顆都要登記）

        /// <summary>llm_admin.py 絕對路徑（走 UCL_RepoPath，不硬編掛載點）。</summary>
        public static string ScriptPath => UCL_RepoPath.CoreTool("llm_admin.py");

        public static bool ScriptExists => File.Exists(ScriptPath);

        /// <summary>
        /// 執行一次 <c>llm_admin.py &lt;argLine&gt;</c>。
        /// </summary>
        /// <param name="argLine">op 與參數（呼叫端自行引號）</param>
        /// <param name="timeoutMs">逾時上限；命中會強制 kill（安裝類要給大值）</param>
        public static async UniTask<LLMAdminRunResult> RunAsync(string argLine, int timeoutMs = 120000)
        {
            if (!ScriptExists)
            {
                // 找不到腳本要**吵**：靜默回空結果會讓畫面看起來像「沒有模型」
                return new LLMAdminRunResult
                {
                    Launched = false, ExitCode = -1,
                    Error = $"❌ 找不到 llm_admin.py（預期於 {ScriptPath}）",
                };
            }

            string aScript = ScriptPath;
            var aResult = await UniTask.RunOnThreadPool(() =>
            {
                UCL_ProcessRegistryService.KillAllByTag(PROC_TAG);   // 前一輪的殘骸先收
                var (aExit, aOut, aErr) = UCL_ProcessCli.Run(
                    "python", $"\"{aScript}\" {argLine}", null, PROC_TAG,
                    nameof(UCL_LLMAdminRunner), timeoutMs,
                    // python 預設在 Windows 用 cp950 寫 stdout ⇒ 中文報表會變亂碼，
                    // 而亂碼看起來像「工具壞了」（讀端的錯冒充被讀端的錯）
                    env: new System.Collections.Generic.Dictionary<string, string>
                    {
                        ["PYTHONIOENCODING"] = "utf-8",
                    },
                    displayName: $"llm_admin {argLine}");
                return new LLMAdminRunResult
                {
                    Launched = true, ExitCode = aExit, Stdout = aOut, Stderr = aErr,
                };
            });

            // ⚠ 切回主執行緒再返回 —— 呼叫端拿到結果就會動 GUI 狀態
            await UniTask.SwitchToMainThread();
            return aResult;
        }
    }
}
#endif
