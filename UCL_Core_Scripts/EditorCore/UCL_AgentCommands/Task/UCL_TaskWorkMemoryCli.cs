// 區塊職責：**代跑** `work_memory.py` —— C# 這一側寫記憶的唯一合法方式。
//
// 物理意義：契約第①條（basecamp 2026-08-24，工作記憶 `decision_contract-task-memory`）：
//   記憶側的唯一寫入端是 python CLI。C# 直接寫記憶檔會生出**第二套格式** ——
//   兩個寫入者、不同語言、不同 process ⇒ 分散式寫入衝突，而它會在併發時安靜地覆蓋。
//   ⇒ 所以這裡不寫檔，只**代跑**（`Cmd_FreeTimeActivity op=step` 已有先例）。
//   使用者只要一道指令，而架構不因為 UX 而變醜。
//
// ⚠ 逃脫那一層照抄先例的修法：用 `ArgumentList` 而不是拼 `Arguments` 字串 ——
//   🩸 2026-08-18 血證：`Arguments` 讓引號同時扮演「綁詞」與「內容」兩個角色，
//   而 CreateProcess 只認前者 ⇒ 逃脫規則怎麼調都是在兩個相反需求之間挑一邊。
//   逐個 token 交給 ArgumentList，.NET 依平台規則自己逃脫，內容原樣抵達。
//
// ⚠ **內文一律走 `--body-file`**，不進命令列 —— 心得會含引號、反引號、換行，
//   而那些字元在任何一層命令列上都是地雷（本 repo 今天已被咬過三次）。
// 2026-08-24 summit（TASK-0019）
#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using Cysharp.Threading.Tasks;

namespace UCL.Core.EditorLib.AgentCommands.TaskMgmt
{
    public static class UCL_TaskWorkMemoryCli
    {
        /// <summary>
        /// `work_memory.py` 的路徑。
        /// <para>⚠ 走 <c>UCL_EditorPath.CorePath</c> —— **既有的解析器**（`Cmd_FreeTimeActivity`
        /// 代跑工具時用的同一個）。🩸 我第一版寫了 `UCL_CorePath.CoreRoot`，那個型別**根本不存在**：
        /// 憑印象造第四套路徑解析正是 `ucl-core-paths` 那份 skill 存在的理由 ——
        /// 而寫死或猜錯的安裝路徑跨專案通常是**靜默**壞（File.Exists 失敗後 fail-soft）。</para>
        /// </summary>
        public static string ToolPath()
            => Path.Combine(UCL_EditorPath.CorePath, "Tools~", "AgentCommands", "work_memory.py")
                .Replace('\\', '/');

        /// <summary>
        /// 代跑 `work_memory.py add`。回 (成功, stdout, 錯誤說明)。
        /// <para>⚠ 內文由呼叫端寫進暫存檔後以 <c>--body-file</c> 傳入 —— 工具**不代筆**內容。</para>
        /// </summary>
        public static async UniTask<(bool ok, string stdout, string detail)> AddAsync(
            string iTopic, string iType, string iId, string iTitle, string iBodyFile, string iBy)
        {
            string aTool = ToolPath();
            if (!File.Exists(aTool))
                return (false, "", $"找不到 work_memory.py（{aTool}）—— 這是路徑問題，不是「沒有記憶系統」");

            try
            {
                var aPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "python",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                aPsi.ArgumentList.Add(aTool);
                aPsi.ArgumentList.Add("add");
                aPsi.ArgumentList.Add("--topic"); aPsi.ArgumentList.Add(iTopic);
                aPsi.ArgumentList.Add("--type"); aPsi.ArgumentList.Add(iType);
                aPsi.ArgumentList.Add("--id"); aPsi.ArgumentList.Add(iId);
                aPsi.ArgumentList.Add("--title"); aPsi.ArgumentList.Add(iTitle);
                aPsi.ArgumentList.Add("--body-file"); aPsi.ArgumentList.Add(iBodyFile);
                if (!string.IsNullOrWhiteSpace(iBy)) { aPsi.ArgumentList.Add("--by"); aPsi.ArgumentList.Add(iBy); }

                var aProc = System.Diagnostics.Process.Start(aPsi);
                if (aProc == null) return (false, "", "Process.Start 回 null");

                string aOut = "", aErr = "";
                bool aExited = await System.Threading.Tasks.Task.Run(() =>
                {
                    aOut = aProc.StandardOutput.ReadToEnd();
                    aErr = aProc.StandardError.ReadToEnd();
                    return aProc.WaitForExit(60000);
                });
                if (!aExited)
                {
                    try { aProc.Kill(); } catch { }
                    return (false, aOut, "timeout(>60s) —— 寫一筆記憶不該跑這麼久");
                }
                if (aProc.ExitCode != 0)
                    return (false, aOut, $"exit={aProc.ExitCode}; {(aErr ?? "").Trim()}");
                return (true, (aOut ?? "").Trim(), "");
            }
            catch (Exception ex)
            {
                return (false, "", ex.Message);
            }
        }
    }
}
#endif
