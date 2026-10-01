// 區塊職責：Editor → `senate cmd persona-profile` 的**轉交橋** —— Editor 這側每一筆 persona 檔寫入都從這裡出去（TASK-0361）。
// 物理意義：Tim 2026-10-01「寫入端整合到 Senate（Unity 端不留）」。persona 檔（`profile/`、`bank/`）唯一的寫入端是
//           SCP_Core `SCP_PersonaProfileWrite`；Editor 頁面（身分後台建 persona、登入狀態頁改 actual_agent、
//           email registry、Plurk 帳號）全部經這一支 spawn 一次 CLI。⛔ `UCL_PersonaProfile` 不留任何寫入方法。
//           形狀照 `UCL_TaskSenateBridge`（TASK-0349）：靠 PATH 上的 `senate`、每個值一顆 `--arg-file`。
// ⚠ 這一跳 process 是過渡期的代價（同任務單那支的判斷）：終局是頁面本身搬進 Senate ⇒ ⛔ 不要為它做最佳化。
// ⚠ 本函式**同步等**那顆 CLI。`persona-profile` 是原生指令（不等 Server），一次約一秒 ——
//   頁面按鈕上直接呼叫可以接受；批次呼叫請自己移到背景執行緒。
// 數值影響：一次 process 起落。結果：exit 0 ＝ 已寫（CLI 寫完有讀回）；其餘 ＝ 沒寫（原因在 Output）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.AgentCommands
{
    public static class UCL_PersonaProfileSenateBridge
    {
        const string SENATE_EXE_NAME = "senate";
        const string TAG = "persona_profile_cli";
        const double OUTER_TIMEOUT_SEC = 60.0;

        /// <summary>
        /// 跑一次 `senate cmd persona-profile --arg op=&lt;op&gt; …`。回傳 (exit code, stdout＋stderr)。
        /// <para>⛔ 叫不到 senate ⇒ exit -1 ＋ 說明（**確定沒寫**）；逾時 ⇒ exit -2（**不知道**，先回讀再決定）。</para>
        /// </summary>
        public static (int exitCode, string output) Run(string iOp, IDictionary<string, string> iArgs)
        {
            var a = new Dictionary<string, string>(StringComparer.Ordinal) { ["op"] = iOp };
            if (iArgs != null) foreach (var kv in iArgs) a[kv.Key] = kv.Value;
            return RunCmd("persona-profile", a, OUTER_TIMEOUT_SEC);
        }

        /// <summary>
        /// 跑任意一支 `senate cmd &lt;iCmd&gt;`（TASK-0361：登入狀態頁的登出改走 `goodnight-logout`）。
        /// <para>⚠ 同步等 —— `goodnight-logout` 可能要等酒館 Server 與 Editor 的 SessionClose（觀影結算），
        /// 呼叫端**必須在背景執行緒上呼叫**（否則 Editor 主緒卡住 ⇒ 它自己的 SessionClose 永遠跑不到 ⇒ 互等到逾時）。</para>
        /// </summary>
        public static (int exitCode, string output) RunCmd(string iCmd, IDictionary<string, string> iArgs, double iTimeoutSec)
        {
            string aLabel = iCmd + (iArgs != null && iArgs.TryGetValue("op", out string aOpV) ? " op=" + aOpV : "");
            var aTmps = new List<string>();
            var aOut = new StringBuilder();
            var aWatch = Stopwatch.StartNew();
            int aExit = -1;
            try
            {
                using (var aProc = new Process())
                {
                    aProc.StartInfo.FileName = SENATE_EXE_NAME;
                    aProc.StartInfo.ArgumentList.Add("cmd");
                    aProc.StartInfo.ArgumentList.Add(iCmd);
                    aProc.StartInfo.ArgumentList.Add("--arg"); aProc.StartInfo.ArgumentList.Add("data_root=" + UCL_AgentCommandsPath.DataRoot);
                    if (iArgs != null)
                        foreach (var kv in iArgs)
                        {
                            if (kv.Value == null) continue;
                            if (kv.Key == "op") { aProc.StartInfo.ArgumentList.Add("--arg"); aProc.StartInfo.ArgumentList.Add("op=" + kv.Value); continue; }
                            // 每個值一顆檔：內文含引號／換行／中文，在 argv 上是地雷（⛔ 不分「哪些夠短」）
                            string aTmp = Path.Combine(Path.GetTempPath(), "ucl_pp_arg_" + Guid.NewGuid().ToString("N") + ".txt");
                            File.WriteAllText(aTmp, kv.Value, new UTF8Encoding(false));
                            aTmps.Add(aTmp);
                            aProc.StartInfo.ArgumentList.Add("--arg-file");
                            aProc.StartInfo.ArgumentList.Add(kv.Key + "=" + aTmp);
                        }
                    aProc.StartInfo.WorkingDirectory = UCL_RepoPath.RepoRoot;
                    aProc.StartInfo.UseShellExecute = false;
                    aProc.StartInfo.RedirectStandardOutput = true;
                    aProc.StartInfo.RedirectStandardError = true;
                    aProc.StartInfo.CreateNoWindow = true;
                    aProc.StartInfo.StandardOutputEncoding = new UTF8Encoding(false);
                    aProc.StartInfo.StandardErrorEncoding = new UTF8Encoding(false);
                    aProc.OutputDataReceived += (iS, iE) => { if (iE.Data != null) lock (aOut) aOut.AppendLine(iE.Data); };
                    aProc.ErrorDataReceived += (iS, iE) => { if (iE.Data != null) lock (aOut) aOut.AppendLine(iE.Data); };
                    try { aProc.Start(); }
                    catch (System.ComponentModel.Win32Exception e)
                    {
                        return (-1, "叫不到 `senate`（PATH 上沒有它）⇒ **這一筆沒有寫**。persona 檔只由 Senate 寫（TASK-0361，⛔ 沒有本地退路）。"
                                    + "　出路：把 Senate 的 `publish` 加進 PATH。原始錯誤：" + e.Message);
                    }
                    using (UCL_ProcessRegistryService.RegisterScope(aProc, TAG, $"Senate 寫入委派（senate cmd {aLabel}）",
                                                                    nameof(UCL_PersonaProfileSenateBridge)))
                    {
                        aProc.BeginOutputReadLine();
                        aProc.BeginErrorReadLine();
                        if (!aProc.WaitForExit((int)(iTimeoutSec * 1000)))
                            return (-2, $"`senate cmd {aLabel}` 等了 {iTimeoutSec}s 還沒結束 —— 這是「**不知道**」不是「沒寫」。"
                                        + "⛔ 不要直接重打，先回讀那個欄位。已收到的輸出：\n" + aOut);
                        aProc.WaitForExit();
                        aExit = aProc.ExitCode;
                    }
                }
            }
            finally
            {
                Debug.Log($"[PersonaProfile] ⏱ senate cmd {aLabel} 🔢 hop_ms = {aWatch.ElapsedMilliseconds}（exit={aExit}）");
                foreach (string t in aTmps) { try { File.Delete(t); } catch (Exception) { } }
            }
            return (aExit, aOut.ToString());
        }

        /// <summary>寫一個身分欄（`op=set`）。成功 ⇒ true；失敗 ⇒ false ＋ <paramref name="oError"/>（CLI 原文）。</summary>
        public static bool SetField(string iPersona, string iField, string iValue, string iActor, string iReason, out string oError)
        {
            var (aExit, aOutText) = Run("set", new Dictionary<string, string>
            {
                ["persona"] = iPersona ?? "", ["field"] = iField ?? "", ["value"] = iValue ?? "",
                ["actor"] = iActor ?? "", ["reason"] = iReason ?? "",
            });
            oError = aExit == 0 ? "" : FirstFailLine(aOutText);
            return aExit == 0;
        }

        /// <summary>建一個新 persona（`op=create`）：本區綁定 ＋ 身分欄。<paramref name="iFieldsJson"/> 是 JSON 物件。</summary>
        public static bool Create(string iPersona, string iAccount, string iFieldsJson, string iActor, string iReason, out string oError)
        {
            var (aExit, aOutText) = Run("create", new Dictionary<string, string>
            {
                ["persona"] = iPersona ?? "", ["account"] = iAccount ?? "", ["fields"] = iFieldsJson ?? "",
                ["actor"] = iActor ?? "", ["reason"] = iReason ?? "",
            });
            oError = aExit == 0 ? "" : FirstFailLine(aOutText);
            return aExit == 0;
        }

        /// <summary>從 CLI 輸出挑出 `✗` 那一行（沒有就回全文）—— 給頁面顯示用。</summary>
        static string FirstFailLine(string iOutput)
        {
            foreach (string ln in (iOutput ?? "").Replace("\r", "").Split('\n'))
                if (ln.TrimStart().StartsWith("✗", StringComparison.Ordinal)) return ln.Trim();
            return (iOutput ?? "").Trim();
        }
    }
}
#endif
