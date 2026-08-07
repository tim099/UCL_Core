// 區塊職責：Editor 端「跑一條 git 指令」的唯一共用封裝
// 物理意義：UCL_GitSubmoduleSyncPage 與 UCL_AutoCommitPage 都要直呼 git CLI ——
//          Process 樣板（雙 stream 非阻塞讀 / ProcessRegistry 登記 / 逾時 kill /
//          GIT_TERMINAL_PROMPT）各寫一份就是漂移的起點，收攏成一個靜態方法。
// 數值影響：本身不判斷指令安全性 —— 讀寫語意由呼叫端的 args 決定；
//          只保證「不 deadlock、不留孤兒、不停在看不見的認證提示」三件事。
#if UNITY_EDITOR
using System;
using System.Diagnostics;

namespace UCL.Core.EditorLib
{
    /// <summary>
    /// 跑 git CLI 的共用封裝（Editor 專用、只在背景執行緒呼叫）。
    /// 認證走系統 git credential manager，與命令列行為一致。
    /// </summary>
    public static class UCL_GitCli
    {
        /// <summary>
        /// 執行 <c>git <paramref name="args"/></c>。
        /// 只在背景執行緒呼叫（WaitForExit 會擋住呼叫端）。
        /// </summary>
        /// <param name="workDir">git 工作目錄（repo 內任意路徑皆可）</param>
        /// <param name="args">git 參數字串（呼叫端自行引號）</param>
        /// <param name="procTag">UCL_ProcessRegistryService 的 tag（呼叫端各自一個，批次開始時自行 KillAllByTag）</param>
        /// <param name="owner">登記時的擁有者名稱（通常 nameof(頁面類)）</param>
        /// <param name="timeoutMs">逾時上限；命中會強制 kill（不留孤兒）</param>
        public static (int exit, string stdout, string stderr) Run(
            string workDir, string args, string procTag, string owner, int timeoutMs)
        {
            var so = new System.Text.StringBuilder();
            var se = new System.Text.StringBuilder();
            int exit = -1;
            int pid = -1;
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo.FileName = "git";
                    p.StartInfo.Arguments = args;
                    p.StartInfo.WorkingDirectory = workDir;
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardOutput = true;
                    p.StartInfo.RedirectStandardError = true;
                    p.StartInfo.CreateNoWindow = true;
                    p.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
                    p.StartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
                    // 認證失敗時 git 會停在終端等輸入，而這裡沒有終端 ——
                    // 關掉讓它直接 fail，錯誤才會離開私有欄位（卡住的失敗最難抓）。
                    p.StartInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                    // stdout / stderr 同時非阻塞讀 —— 只讀一個 stream 時 child 填滿另一邊
                    // buffer 會互卡成永久 deadlock（本專案踩過不只一次）。
                    p.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
                    p.ErrorDataReceived += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };
                    p.Start();
                    // spawn 後立刻登記（身分 = PID + name + start time）—— 短命指令也要登記，
                    // pull / push 走網路可能活數分鐘，夠跨一次 domain reload 變孤兒。
                    UCL_ProcessRegistryService.Register(p, procTag,
                        $"git {Truncate(args, 60)}", owner);
                    pid = p.Id;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { /* 已死就算了 —— 目的只是別留孤兒 */ }
                        se.AppendLine($"[UCL_GitCli] git {args} 逾時（{timeoutMs / 1000}s）— 已強制結束");
                    }
                    else
                    {
                        exit = p.ExitCode;
                    }
                }
            }
            catch (Exception e)
            {
                se.AppendLine(e.ToString());
            }
            finally
            {
                if (pid > 0) UCL_ProcessRegistryService.Unregister(pid, procTag);
            }
            return (exit, so.ToString().TrimEnd(), se.ToString().TrimEnd());
        }

        static string Truncate(string s, int n)
            => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
#endif
