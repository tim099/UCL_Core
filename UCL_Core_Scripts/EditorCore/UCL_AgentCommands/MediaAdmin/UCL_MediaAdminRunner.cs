// 區塊職責：影音管理 python 工具 (media_admin.py) 的 C# 端唯一呼叫點 — AdminPage (未來含 Cmd) 共用。
// 物理意義：async spawn `python <UCL_Core>/Tools~/AgentCommands/media_admin.py <op ...>`，抓 stdout/stderr。
//          跑在 thread pool，不佔 Editor main thread (對齊 UCL_KnowledgeBaseRunner 的反凍結教訓)。
// 設計取捨：script 住 UCL_Core submodule (跨專案共用)，路徑走 UCL_EditorPath.CorePath 動態解析，
//          不硬編 install path (那會在不同掛載點漂掉，見 ucl-core-paths 慣例)。
// 2026-07-25 (kaguya/Luna)：本 runner = 「影音管理唯一真相源 = python」在 C# 端的薄橋接。
#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.AgentCommands.MediaAdmin
{
    /// <summary>media_admin.py 執行結果。</summary>
    public struct MediaAdminRunResult
    {
        public bool Launched;     // process 是否成功啟動
        public bool TimedOut;     // 是否逾時被 kill
        public int ExitCode;      // 進程 exit code (未啟動 = -1)
        public string Stdout;     // 標準輸出 (python 印的 text/json)
        public string Stderr;     // 標準錯誤
        public string Error;      // C# 端啟動/例外訊息 (成功則空)

        /// <summary>對 caller 顯示用 — 優先 stdout，退回 error/stderr。</summary>
        public string DisplayText =>
            !string.IsNullOrEmpty(Stdout) ? Stdout
            : !string.IsNullOrEmpty(Error) ? Error
            : !string.IsNullOrEmpty(Stderr) ? Stderr
            : "(無輸出)";
    }

    /// <summary>
    /// 影音管理 (STT/OCR) python 工具的 async 執行器。UCL_MediaAdminPage 使用；
    /// 若日後開 Cmd_MediaAdmin 也走本 runner，確保「人在 Editor 點按鈕」與「agent 走 Cmd」同一條路。
    /// </summary>
    public static class UCL_MediaAdminRunner
    {
        // Process 註冊中心的 tag（硬規則：每顆外部 Process 都要登記）。
        const string PROC_TAG = "media_admin_py";

        /// <summary>
        /// media_admin.py 絕對路徑。走 UCL_EditorPath.CorePath 動態解析 UCL_Core 掛載點，
        /// 跨專案安全 (不硬編 Assets/Plugins/UCL_Core)。
        /// </summary>
        public static string ScriptPath =>
            Path.Combine(UCL_RepoPath.UnityProjectRoot, UCL_EditorPath.CorePath,
                         "Tools~", "AgentCommands", "media_admin.py").Replace('\\', '/');

        public static bool ScriptExists => File.Exists(ScriptPath);

        // 區塊職責：async 執行一次 media_admin.py <argLine>
        // 物理意義：thread pool 上 spawn + 阻塞等結果，不卡 main thread；token cancel / 逾時 → kill process。
        // 數值影響：timeoutMs 逾時回 TimedOut=true；不 throw，一律回結構化 result 讓 caller 決定顯示。
        public static async UniTask<MediaAdminRunResult> RunAsync(
            string argLine, CancellationToken token, int timeoutMs = 120000)
        {
            if (!ScriptExists)
            {
                return new MediaAdminRunResult
                {
                    Launched = false, ExitCode = -1,
                    Error = $"❌ 找不到 media_admin.py（預期於 {ScriptPath}）。本專案可能未啟用影音管理工具。",
                };
            }

            // 背景執行緒跑阻塞的 python 進程 (不佔 main thread)。
            // ⚠ 不傳 cancellationToken 給 RunOnThreadPool — RunBlocking 內部已 honor token，
            //    讓它自然結束好回主執行緒 (對齊 UCL_KnowledgeBaseRunner 的 race 迴避註記)。
            var result = await UniTask.RunOnThreadPool(() => RunBlocking(argLine, timeoutMs, token));

            // ⚠ 關鍵：切回 main thread 再返回 — 對齊其他 handler 行為，繞開 pooled UniTaskSource 回收 race。
            await UniTask.SwitchToMainThread();
            return result;
        }

        static MediaAdminRunResult RunBlocking(string argLine, int timeoutMs, CancellationToken token)
        {
            int aPid = -1;
            var result = new MediaAdminRunResult { ExitCode = -1 };
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "python",                                   // 主流系統皆有；PATH 未命中由 OS 報錯
                    Arguments = $"\"{ScriptPath}\" {argLine}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,                // C# 端以 UTF-8 解碼 python 輸出
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = UCL_RepoPath.RepoRoot,              // media_admin.py 自算路徑，這裡對齊 repo 根
                };
                // python 端 stdout 在 redirect 下預設 cp950 → emoji crash；強制 utf-8 (與 script 內 reconfigure 雙保險)
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    result.Error = "❌ python 進程啟動失敗（Process.Start 回 null）。";
                    return result;
                }
                result.Launched = true;
                // 硬規則：C# 開的每顆外部 Process 都要登記（Coding_Standards.md「外部 Process」）。
                // ⚠ 這裡最需要登記，理由跟「會不會卡住」無關：下面那個輪詢 + Kill 的逾時防護
                //   **只在 C# 的 Process 物件還活著時有效**。domain reload 一來物件沒了，
                //   那顆 python 就沒有任何人管得到 —— 而它看起來是「已經處理過逾時」的那種，
                //   最容易讓人以為安全。登記讓防護跨 domain reload 存活（身分從磁碟讀回）。
                try {
                    UCL_ProcessRegistryService.Register(proc, PROC_TAG,
                        "media_admin.py", nameof(UCL_MediaAdminRunner));
                    aPid = proc.Id;
                } catch (Exception regEx) {
                    // 登記失敗不該擋住工作本身（process 已經在跑了），但要出聲：
                    // 靜默失敗會讓這顆變成沒人管的孤兒，而那正是登記要防的事。
                    Debug.LogWarning($"[UCL_MediaAdminRunner] Process 登記失敗（該顆將無法被註冊中心接管）: {regEx.Message}");
                }

                // 非同步讀兩條 pipe，避免其一填滿造成死結
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();

                // 逾時 / cancel 輪詢等待（每 100ms 檢查 token）
                int waited = 0;
                const int step = 100;
                while (!proc.HasExited)
                {
                    if (token.IsCancellationRequested || waited >= timeoutMs)
                    {
                        try { proc.Kill(); } catch { /* 已結束 */ }
                        result.TimedOut = waited >= timeoutMs;
                        result.Error = token.IsCancellationRequested ? "⚠ 已取消（token cancel）。" : $"⚠ 逾時 {timeoutMs}ms 已中止。";
                        break;
                    }
                    Thread.Sleep(step);
                    waited += step;
                }

                result.Stdout = SafeResult(outTask);
                result.Stderr = SafeResult(errTask);
                if (proc.HasExited) result.ExitCode = proc.ExitCode;
            }
            catch (Exception e)
            {
                result.Error = $"❌ media_admin.py 執行例外: {e.Message}";
                Debug.LogWarning($"[MediaAdminRunner] argLine=`{argLine}` fail: {e}");
            }
            finally
            {
                // 反登記放 finally —— 例外路徑也要清，否則記錄檔留一個已死的 PID，
                // 讓 UCL_ProcessAdminPage 顯示不存在的 process（監控畫面說謊比沒有監控更糟）。
                if (aPid > 0) UCL_ProcessRegistryService.Unregister(aPid, PROC_TAG);
            }
            return result;
        }

        static string SafeResult(System.Threading.Tasks.Task<string> t)
        {
            try { return t?.Result ?? ""; } catch { return ""; }
        }

        /// <summary>quote 一個帶空白的 arg 值，並把內部雙引號降級成單引號（避免 shell 破引號）。</summary>
        public static string QuoteArg(string v) => "\"" + (v ?? "").Replace("\"", "'") + "\"";
    }
}
#endif
