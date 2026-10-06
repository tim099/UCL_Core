// 區塊職責：Editor → `senate cmd task` 的**轉交橋** —— 任務單的每一筆寫入都從這裡出去（TASK-0349）。
// 物理意義：任務單唯一的寫入端是 Senate Server（`task-write`，SCP_Core `SCP_TaskOps`／`SCP_TaskStore`）。
//           Editor 這側**不再有寫入面**（`UCL_TaskIO` 的 Save／Mutate／Create／Link 已刪）⇒
//           `Cmd_Task` 的寫入 op、後台頁的按鈕、晚安的 skip 全部經這一支 spawn 一次 `senate cmd task`。
//           形狀照 `UCL_ChatTavernIO.DelegateAppendToServer`（TASK-0341，Tim 2026-09-22 拍板 A：整條委派走 CLI）。
//
// ⚠ ⑪ **這一跳 process 是過渡期的代價，不是終局形狀**（同酒館那支的判斷）：終局是整套搬進 Senate ⇒
//   ⛔ 不要為這一跳做最佳化（常駐 client、連線池…）—— 那些在遷移當天會整包丟掉。
// ⚠ 本函式**同步等**那顆 CLI —— 呼叫端負責**不在主執行緒上呼叫**
//   （`Cmd_Task` 已經在 `EnterBackground` 之後；後台頁用 `UniTask.RunOnThreadPool`）。
// ⚠ 參數一律走 `--arg-file`（每個值一顆暫存檔）：內文含引號／反引號／換行，在 argv 上是地雷，
//   ⛔ 也不必分「哪些夠短可以直接塞」—— 兩套規則裡總有一條會漏。
// 數值影響：一次 process 起落（＋CLI 等 Server、發通知、寫工作記憶）；結果四態照入口：
//   0 已寫／1 閘擋下（零寫入）／2 參數錯／6 確定沒寫／7 **不知道**。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.AgentCommands.TaskMgmt
{
    public sealed class UCL_TaskSenateResult
    {
        /// <summary>CLI 的退出碼；-1 ＝ 沒拿到（叫不到 senate／外層逾時 —— 那兩種會丟例外，不會回到這裡）。</summary>
        public int ExitCode = -1;
        /// <summary>stdout ＋ stderr 原文。</summary>
        public string Output = "";
        /// <summary>入口落的回傳檔（`letters/<P>/cmd/task_<op>.md`）；CLI 沒印就是空字串。</summary>
        public string PayloadPath = "";
        /// <summary>`🔢 k = v` 讀數（明文契約：CLI 全部 Cmd 共用的機器讀數通道）。</summary>
        public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);

        public bool Ok => ExitCode == 0;
        /// <summary>**不知道有沒有寫**（送出了但等不到回執）⇒ ⛔ 先回讀那張單，別直接重打。</summary>
        public bool Unknown => ExitCode == 7;
        public string Value(string iKey) => Values.TryGetValue(iKey, out string v) ? v : "";

        /// <summary>回傳檔全文（讀不到回空字串）。</summary>
        public string ReadPayload()
        {
            try { return PayloadPath.Length > 0 && File.Exists(PayloadPath) ? File.ReadAllText(PayloadPath, Encoding.UTF8) : ""; }
            catch (Exception) { return ""; }
        }
    }

    public static class UCL_TaskSenateBridge
    {
        const string SENATE_EXE_NAME = "senate";
        const string TAG = "task_write_cli";

        // 🩸 兩層逾時的大小關係是刻意的（同酒館那支）：內層（入口等 Server）60s ＋ autostart 上限 20s ＋ 通知（每則 ≤30s）
        //   ⇒ 外層給 300s，讓內層先逾時 ⇒ 外層拿到的是「它說了什麼」而不是「我不知道」。
        const int INNER_TIMEOUT_SEC = 60;
        const double OUTER_TIMEOUT_SEC = 300.0;

        /// <summary>
        /// 跑一次 `senate cmd task --arg op=<op> --arg persona=<persona> …`。
        /// <para>⛔ 叫不到 senate、外層逾時 ⇒ **丟例外**（前者＝確定沒寫，後者＝**不知道**，訊息各自說清楚）。
        /// 其餘結果（含閘擋下）以 <see cref="UCL_TaskSenateResult.ExitCode"/> 回來 —— 呼叫端自己決定那算不算錯。</para>
        /// </summary>
        public static UCL_TaskSenateResult Run(string iOp, string iPersona, IDictionary<string, string> iArgs)
        {
            if (string.IsNullOrWhiteSpace(iPersona))
                throw new InvalidOperationException("[Task] 轉交 senate 需要 persona（時間線與署名是它）—— ⛔ 不猜");
            var aTmps = new List<string>();
            var aOut = new StringBuilder();
            var aErr = new StringBuilder();
            var aResult = new UCL_TaskSenateResult();
            var aWatch = Stopwatch.StartNew();
            try
            {
                using (var aProc = new Process())
                {
                    aProc.StartInfo.FileName = SENATE_EXE_NAME;   // 靠 PATH（同酒館那支；⛔ 不新增第四套路徑解析器）
                    aProc.StartInfo.ArgumentList.Add("cmd");
                    aProc.StartInfo.ArgumentList.Add("task");
                    void Arg(string k, string v) { aProc.StartInfo.ArgumentList.Add("--arg"); aProc.StartInfo.ArgumentList.Add(k + "=" + v); }
                    Arg("op", iOp);
                    Arg("persona", iPersona.Trim());
                    Arg("timeout", INNER_TIMEOUT_SEC.ToString());
                    if (iArgs != null)
                        foreach (var kv in iArgs)
                        {
                            if (kv.Value == null) continue;
                            string aTmp = Path.Combine(Path.GetTempPath(), "ucl_task_arg_" + Guid.NewGuid().ToString("N") + ".txt");
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
                    aProc.ErrorDataReceived += (iS, iE) => { if (iE.Data != null) lock (aErr) aErr.AppendLine(iE.Data); };
                    try { aProc.Start(); }
                    catch (System.ComponentModel.Win32Exception e)
                    {
                        throw new InvalidOperationException("[Task] 叫不到 `senate`（PATH 上沒有它）⇒ **這一筆沒有寫**。"
                            + "　出路：把 Senate 的 `publish` 加進 PATH（任務單只由 Senate Server 寫，⛔ 沒有本地退路，TASK-0349）。"
                            + "　原始錯誤：" + e.Message, e);
                    }
                    using (UCL_ProcessRegistryService.RegisterScope(aProc, TAG, $"任務寫入委派（senate cmd task op={iOp}）", nameof(UCL_TaskSenateBridge)))
                    {
                        aProc.BeginOutputReadLine();
                        aProc.BeginErrorReadLine();
                        if (!aProc.WaitForExit((int)(OUTER_TIMEOUT_SEC * 1000)))
                            throw new InvalidOperationException($"[Task] `senate cmd task op={iOp}` 等了 {OUTER_TIMEOUT_SEC}s 還沒結束 —— "
                                + "這是「**不知道**」不是「沒寫」。⛔ **不要重打**：那一筆可能已經寫了（留言會多一則、勾選會多一格）。"
                                + "　先回讀那張單再決定。已收到的輸出：\n" + aOut + aErr);
                        aProc.WaitForExit();   // 讓非同步讀完 stream
                        aResult.ExitCode = aProc.ExitCode;
                    }
                }
            }
            finally
            {
                Debug.Log($"[Task] ⏱ senate cmd task op={iOp} 🔢 hop_ms = {aWatch.ElapsedMilliseconds}（exit={aResult.ExitCode}）");
                foreach (string t in aTmps) { try { File.Delete(t); } catch (Exception) { } }
            }
            aResult.Output = aOut.ToString() + aErr.ToString();
            foreach (string aLine in aResult.Output.Replace("\r", "").Split('\n'))
            {
                Match m = Regex.Match(aLine, @"^\s*🔢\s+(\S+)\s+=\s?(.*)$");
                if (m.Success) { aResult.Values[m.Groups[1].Value] = m.Groups[2].Value.Trim(); continue; }
                Match p = Regex.Match(aLine, @"📄\s*回傳檔[:：]\s*(.+)$");
                if (p.Success && aResult.PayloadPath.Length == 0) aResult.PayloadPath = p.Groups[1].Value.Trim();
            }
            return aResult;
        }
    }
}
#endif
