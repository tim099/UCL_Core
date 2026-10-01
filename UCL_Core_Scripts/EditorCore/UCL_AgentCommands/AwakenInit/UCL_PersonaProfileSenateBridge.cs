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
                    aProc.StartInfo.ArgumentList.Add("persona-profile");
                    aProc.StartInfo.ArgumentList.Add("--arg"); aProc.StartInfo.ArgumentList.Add("op=" + iOp);
                    aProc.StartInfo.ArgumentList.Add("--arg"); aProc.StartInfo.ArgumentList.Add("data_root=" + UCL_AgentCommandsPath.DataRoot);
                    if (iArgs != null)
                        foreach (var kv in iArgs)
                        {
                            if (kv.Value == null) continue;
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
                    using (UCL_ProcessRegistryService.RegisterScope(aProc, TAG, $"persona 寫入委派（senate cmd persona-profile op={iOp}）",
                                                                    nameof(UCL_PersonaProfileSenateBridge)))
                    {
                        aProc.BeginOutputReadLine();
                        aProc.BeginErrorReadLine();
                        if (!aProc.WaitForExit((int)(OUTER_TIMEOUT_SEC * 1000)))
                            return (-2, $"`senate cmd persona-profile op={iOp}` 等了 {OUTER_TIMEOUT_SEC}s 還沒結束 —— 這是「**不知道**」不是「沒寫」。"
                                        + "⛔ 不要直接重打，先回讀那個欄位。已收到的輸出：\n" + aOut);
                        aProc.WaitForExit();
                        aExit = aProc.ExitCode;
                    }
                }
            }
            finally
            {
                Debug.Log($"[PersonaProfile] ⏱ senate cmd persona-profile op={iOp} 🔢 hop_ms = {aWatch.ElapsedMilliseconds}（exit={aExit}）");
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
