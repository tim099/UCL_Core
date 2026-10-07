// 區塊職責：Editor → `senate cmd <名>` 的 spawn 小工具 —— 靠 PATH 上的 `senate`、每個值一顆 `--arg-file`。
// 物理意義：Editor 這側要叫 Senate 指令的地方（酒館發文 UCL_TavernSenatePost）共用這一支，⛔ 不另寫第二支 process 呼叫器。
//           形狀同 `UCL_TaskSenateBridge`（TASK-0349）。
// 數值影響：一次 process 起落，同步等 ⇒ 呼叫端自己決定要不要丟背景執行緒。
//           叫不到 senate ⇒ exit -1（確定沒跑）；外層逾時 ⇒ exit -2（不知道，先回讀再決定）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.AgentCommands
{
    public static class UCL_SenateCli
    {
        const string SENATE_EXE_NAME = "senate";
        const string TAG = "senate_cli";

        /// <summary>
        /// 跑一支 `senate cmd &lt;iCmd&gt;`，回傳 (exit code, stdout＋stderr)。
        /// <para>⚠ 同步等 —— 秒級的指令（例：要等酒館 Server 的 `tavern-post`）呼叫端**必須在背景執行緒上呼叫**。</para>
        /// <para>⛔ 不送資料根：路徑一律由 Senate 照後台設定補上，CLI 不收手給的路徑（TASK-0391）。</para>
        /// <para><paramref name="iSendTargetDataRoot"/>：`tavern-post` 系列要 true —— 送 `target_data_root=<本 Editor 的資料根>`，
        /// 那不是指定路徑，是宣告「我是哪一棵」，Senate 拿去跟設定檔的專案比對、比不到就擋（TASK-0366）。</para>
        /// </summary>
        public static (int exitCode, string output) RunCmd(string iCmd, IDictionary<string, string> iArgs, double iTimeoutSec,
                                                           bool iSendTargetDataRoot = false)
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
                    if (iSendTargetDataRoot)
                    { aProc.StartInfo.ArgumentList.Add("--arg"); aProc.StartInfo.ArgumentList.Add("target_data_root=" + UCL_AgentCommandsPath.DataRoot); }
                    if (iArgs != null)
                        foreach (var kv in iArgs)
                        {
                            if (kv.Value == null) continue;
                            if (kv.Key == "op") { aProc.StartInfo.ArgumentList.Add("--arg"); aProc.StartInfo.ArgumentList.Add("op=" + kv.Value); continue; }
                            // 每個值一顆檔：內文含引號／換行／中文，在 argv 上是地雷（⛔ 不分「哪些夠短」）
                            string aTmp = Path.Combine(Path.GetTempPath(), "ucl_senate_arg_" + Guid.NewGuid().ToString("N") + ".txt");
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
                        return (-1, "叫不到 `senate`（PATH 上沒有它）⇒ **這一筆沒有跑**。"
                                    + "　出路：把 Senate 的 `publish` 加進 PATH。原始錯誤：" + e.Message);
                    }
                    using (UCL_ProcessRegistryService.RegisterScope(aProc, TAG, $"Senate 指令委派（senate cmd {aLabel}）",
                                                                    nameof(UCL_SenateCli)))
                    {
                        aProc.BeginOutputReadLine();
                        aProc.BeginErrorReadLine();
                        if (!aProc.WaitForExit((int)(iTimeoutSec * 1000)))
                            return (-2, $"`senate cmd {aLabel}` 等了 {iTimeoutSec}s 還沒結束 —— 這是「**不知道**」不是「沒跑」。"
                                        + "⛔ 不要直接重打，先回讀結果。已收到的輸出：\n" + aOut);
                        aProc.WaitForExit();
                        aExit = aProc.ExitCode;
                    }
                }
            }
            finally
            {
                Debug.Log($"[SenateCli] ⏱ senate cmd {aLabel} 🔢 hop_ms = {aWatch.ElapsedMilliseconds}（exit={aExit}）");
                foreach (string t in aTmps) { try { File.Delete(t); } catch (Exception) { } }
            }
            return (aExit, aOut.ToString());
        }
    }
}
#endif
