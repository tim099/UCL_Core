// 區塊職責：Editor 端「跑一條 git 指令」的薄殼 —— 只補 git 專屬的兩件事
// 物理意義：Process 樣板本體 2026-08-11 抽到 UCL_ProcessCli。抽的理由：本檔原本是唯一一份
//          完整樣板，但它寫死 FileName="git"，於是 UCL_LibraryManagePage 要跑 python 時
//          **又手刻了第二份**；Persona 後台要跑 awakening.py 時不再加第三份。
//          本檔留下的只有：執行檔名 "git" + GIT_TERMINAL_PROMPT=0。
// 數值影響：本身不判斷指令安全性 —— 讀寫語意由呼叫端的 args 決定。
//          「不 deadlock、不留孤兒、不停在看不見的認證提示」由 UCL_ProcessCli 保證。
#if UNITY_EDITOR

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
            // 薄殼：Process 樣板本體在 UCL_ProcessCli（2026-08-11 抽出）。
            // 本檔只保留「git 專屬」的兩件事，其餘一行都不重複：
            //   ① 執行檔名 "git"
            //   ② GIT_TERMINAL_PROMPT=0 —— 認證失敗時 git 會停在終端等輸入，而這裡沒有終端。
            //      關掉讓它直接 fail，錯誤才會離開私有欄位（卡住的失敗最難抓）。
            // 簽名刻意不變 —— 既有呼叫端（SubmoduleSync / AutoCommit）一行都不用改。
            return UCL_ProcessCli.Run("git", args, workDir, procTag, owner, timeoutMs,
                env: new System.Collections.Generic.Dictionary<string, string>
                {
                    ["GIT_TERMINAL_PROMPT"] = "0",
                },
                displayName: $"git {Truncate(args, 60)}");
        }

        static string Truncate(string s, int n)
            => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
#endif
