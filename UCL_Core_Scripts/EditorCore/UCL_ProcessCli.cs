// 區塊職責：Editor 端「跑一顆外部 process 並拿回 stdout/stderr」的唯一共用封裝
// 物理意義：這段樣板有四件事**每一份手抄都會漏掉其中一件**，而漏掉的那件都不會當場叫：
//          ① stdout / stderr 要同時非阻塞讀 —— 只讀一邊時 child 填滿另一邊 buffer 會永久 deadlock
//          ② spawn 後立刻登記 ProcessRegistry —— 否則跨一次 domain reload 就變孤兒（屍潮）
//          ③ 逾時要強制 kill —— 等待中的失敗是最難抓的失敗
//          ④ finally 一定 Unregister —— 只在成功路徑取消登記的話，失敗的那顆會永遠掛在名單上
//          本檔是從 UCL_GitCli 抽出來的：那支原本是唯一一份完整樣板，但它寫死 FileName="git"，
//          於是 UCL_LibraryManagePage 要跑 python 時**又手刻了第二份**。第三個呼叫端出現時
//          （Persona 後台跑 awakening.py）不再加第三份，改把樣板本身收攏到這裡。
// 數值影響：本身不判斷指令安全性 —— 讀寫語意由呼叫端的 fileName / args 決定；
//          只保證「不 deadlock、不留孤兒、不停在看不見的提示」三件事。
// 邊界：只在**背景執行緒**呼叫（WaitForExit 會擋住呼叫端）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace UCL.Core.EditorLib
{
    /// <summary>
    /// 跑外部 CLI 的共用封裝（Editor 專用、只在背景執行緒呼叫）。
    /// git 專用的薄殼見 <see cref="UCL_GitCli"/>。
    /// </summary>
    public static class UCL_ProcessCli
    {
        /// <summary>
        /// 執行 <c>&lt;fileName&gt; &lt;args&gt;</c> 並等它結束。
        /// </summary>
        /// <param name="fileName">執行檔（"git" / "python" / 絕對路徑皆可）</param>
        /// <param name="args">參數字串（**呼叫端自行引號** —— 含空白的路徑要自己包 "）</param>
        /// <param name="workDir">工作目錄；空字串 = 繼承 Editor 的</param>
        /// <param name="procTag">UCL_ProcessRegistryService 的 tag（呼叫端各自一個）</param>
        /// <param name="owner">登記時的擁有者名稱（通常 nameof(頁面類)）</param>
        /// <param name="timeoutMs">逾時上限；命中會強制 kill（不留孤兒）</param>
        /// <param name="env">額外環境變數（可 null）。例：git 的 GIT_TERMINAL_PROMPT=0</param>
        /// <param name="displayName">登記名稱；null = 用 "fileName args(截斷)"</param>
        public static (int exit, string stdout, string stderr) Run(
            string fileName, string args, string workDir, string procTag, string owner,
            int timeoutMs, IDictionary<string, string> env = null, string displayName = null)
        {
            var so = new System.Text.StringBuilder();
            var se = new System.Text.StringBuilder();
            int exit = -1;
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo.FileName = fileName;
                    p.StartInfo.Arguments = args;
                    if (!string.IsNullOrEmpty(workDir)) p.StartInfo.WorkingDirectory = workDir;
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardOutput = true;
                    p.StartInfo.RedirectStandardError = true;
                    p.StartInfo.CreateNoWindow = true;
                    // 子行程的輸出一律當 UTF-8 讀 —— 中文報表在 Windows 預設 codepage 下會變亂碼，
                    // 而亂碼的報表看起來像「工具壞了」，其實是我們讀錯編碼（讀端的錯冒充被讀端的錯）。
                    p.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
                    p.StartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
                    if (env != null)
                    {
                        foreach (var kv in env) p.StartInfo.EnvironmentVariables[kv.Key] = kv.Value;
                    }
                    // stdout / stderr 同時非阻塞讀 —— 見檔頭 ①
                    p.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
                    p.ErrorDataReceived += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };
                    p.Start();
                    // spawn 後立刻登記（見檔頭 ②）。用 RegisterScope 不用 Register+finally：
                    // 反登記的成對性由語言保證，不靠下一個改這支的人記得補 finally
                    // —— 那正是 UCL_ProcessRegistryService 自己 docstring 給的理由。
                    using var procScope_ = UCL_ProcessRegistryService.RegisterScope(
                        p, procTag, displayName ?? $"{fileName} {Truncate(args, 60)}", owner);
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { /* 已死就算了 —— 目的只是別留孤兒 */ }
                        se.AppendLine($"[UCL_ProcessCli] {fileName} {args} 逾時"
                                      + $"（{timeoutMs / 1000}s）— 已強制結束");
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
            return (exit, so.ToString().TrimEnd(), se.ToString().TrimEnd());
        }

        static string Truncate(string s, int n)
            => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
#endif
