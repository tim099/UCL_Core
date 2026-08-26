
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 08/18 2026
// 區塊職責：查「某個 persona 此刻在哪種 session」的 read-only Cmd（鏡像 UCL_SessionAdminPage 的資料）。
// 物理意義：session 狀態原本只能自己去 cat <kind>/sessions/<persona>.json，而「在不在」不是
//          單看 active 就能答（超時沒收工的人會停在 active=true）。判準收在 UCL_SessionService，
//          本 Cmd 只是把它曝光給 CLI/agent。
// 數值影響：純讀檔，不寫任何 session。輸出一份回傳檔給呼叫端讀。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Session 現況查詢（read-only）。
    ///
    /// <para>典型用法：</para>
    /// <code>
    /// # basecamp 現在在哪種 session
    /// python run_cmd.py --persona basecamp run SessionStatus --arg persona=basecamp
    ///
    /// # 全部 persona 的總覽（含已收工的歷史）
    /// python run_cmd.py --persona basecamp run SessionStatus --arg scope=all
    /// </code>
    /// </summary>
    public class Cmd_SessionStatus : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "SessionStatus";

        public override string ShortDescription =>
            "查某 persona 此刻在哪種 session（FreeTime…），或列出全部 session 檔的總覽。read-only。";

        public override string ArgsSchema =>
            "persona=<名字>（scope=persona 時必填） | " +
            "scope=persona|all（預設 persona）—— all 列出每個已登記 kind 底下所有 session 檔";

        public override string ExampleArgs => "persona=basecamp";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_SessionStatus.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            string aScope = GetArg(args, "scope", "persona").Trim().ToLowerInvariant();
            string aPersona = GetArg(args, "persona", "").Trim();
            if (aScope != "persona" && aScope != "all")
            {
                throw new Exception($"[SessionStatus] scope 必為 persona|all（got '{aScope}'）");
            }
            if (aScope == "persona" && string.IsNullOrEmpty(aPersona))
            {
                // 不猜「現在是誰」——多 persona 環境猜錯會回報別人的狀態，而那看起來完全正常。
                throw new Exception("[SessionStatus] scope=persona 需要 --arg persona=<名字>（不猜身分）");
            }

            var aR = new StringBuilder();
            aR.AppendLine($"# SessionStatus scope={aScope}"
                          + (aScope == "persona" ? $" persona={aPersona}" : "")
                          + $"  ts=`{DateTime.Now:yyyy-MM-dd HH:mm:sszzz}`（本地時間）");
            aR.AppendLine();
            // ⚠ 這一行是回報的一部分，不是裝飾：空結果的語意是「在**這些** kind 裡沒查到」，
            //   不是「這個人不在任何 session」。沒印掃描範圍的「沒查到」會被讀成後者。
            aR.AppendLine($"- 掃描範圍（已登記 kind）：{string.Join(" / ", UCL_SessionService.ScannedKinds())}");
            aR.AppendLine();

            if (aScope == "persona")
            {
                var aRunning = UCL_SessionService.FindRunning(aPersona);
                // ===========================================================
                // 機讀出口（TASK-0052）：python 消費端（canvas.py 免費像素資格等）退出直讀 session 檔後，
                // 「在不在自由時間」的答案從這裡拿 —— run_cmd 會把 values 隨 verdict 印出（🔢 key = value），
                // result 檔的 values 欄也帶著，機器不必 parse 人讀報告。
                // ⚠ 在算出結果的當下 push（ReportOutputValue 的 remarks：不要事後 pull static）。
                // ===========================================================
                var aKindNames = new List<string>();
                foreach (var aKv in aRunning) aKindNames.Add(aKv.Key);
                UCL_AgentCommandRunner.ReportOutputValue(args, "running_kinds",
                    aKindNames.Count == 0 ? "-" : string.Join(",", aKindNames));
                UCL_AgentCommandRunner.ReportOutputValue(args, "in_free_time",
                    aKindNames.Contains(UCL_SessionKind.FreeTime) ? "1" : "0");
                if (aRunning.Count == 0)
                {
                    aR.AppendLine($"## 結果：{aPersona} 目前**不在**任何已登記的 session 中");
                    // 檔案存在但不算進行中時要講清楚是哪一種 —— 「沒有檔」與「有檔但過期」
                    // 對使用者的下一步不同（後者可能要補收工）。
                    foreach (string aKind in UCL_SessionService.ScannedKinds())
                    {
                        var aPeek = UCL_SessionService.Peek(aKind, aPersona);
                        if (aPeek == null)
                        {
                            aR.AppendLine($"- {aKind}: 無 session 檔");
                            continue;
                        }
                        var aEnd = UCL_SessionBase.ParseIsoToLocal(aPeek.end_ts);
                        string aWhy = aPeek.active
                            ? $"⚠ active=true 但已過 end_ts（{aEnd:yyyy-MM-dd HH:mm}）—— 超時未收工的殘留"
                            : $"已收工（{(string.IsNullOrEmpty(aPeek.end_reason) ? "未記原因" : aPeek.end_reason)}）";
                        aR.AppendLine($"- {aKind}: {aWhy}　session_id={aPeek.session_id}");
                    }
                }
                else
                {
                    aR.AppendLine($"## 結果：{aPersona} 進行中的 session {aRunning.Count} 筆");
                    foreach (var aKv in aRunning)
                    {
                        var aS = aKv.Value;
                        aS.IsRunningAt(DateTime.Now, out DateTime? aEnd);
                        string aRemain = aEnd.HasValue
                            ? $"{(int)Math.Max(0, (aEnd.Value - DateTime.Now).TotalMinutes)} 分"
                            : "（無 end_ts —— 只能信 active）";
                        aR.AppendLine($"- **{aKv.Key}**　session_id={aS.session_id}");
                        aR.AppendLine($"    開場 {aS.start_ts}　預定收工 {aS.until_local}　剩 {aRemain}");
                    }
                }
            }
            else
            {
                foreach (string aKind in UCL_SessionService.ScannedKinds())
                {
                    var aPersonas = UCL_SessionService.ListPersonas(aKind);
                    aR.AppendLine($"## {aKind}（{aPersonas.Count} 份 session 檔）");
                    if (aPersonas.Count == 0)
                    {
                        aR.AppendLine("- （無）");
                        continue;
                    }
                    foreach (string aWho in aPersonas)
                    {
                        var aS = UCL_SessionService.Peek(aKind, aWho);
                        if (aS == null) { aR.AppendLine($"- {aWho}: ⚠ 讀取失敗"); continue; }
                        bool aRun = aS.IsRunningAt(DateTime.Now, out DateTime? aEnd);
                        string aState = aRun ? "🟢 進行中"
                            : aS.active ? "⚠ 殘留（active 但過期）" : "⚪ 已收工";
                        aR.AppendLine($"- {aWho}: {aState}　收工時刻 {aS.until_local}"
                                      + (string.IsNullOrEmpty(aS.end_reason) ? "" : $"　reason={aS.end_reason}"));
                    }
                    aR.AppendLine();
                }
            }

            aR.AppendLine("## next");
            aR.AppendLine("- 後台頁：Tools/UCL/ToolBox → 🗂 Session 管理（同一份資料的視覺化）");
            aR.AppendLine("- ⚠ 未登記的 session 種類不在掃描範圍內（見 UCL_SessionKind.Kinds 的註解）");

            // ===========================================================
            // 區塊職責：回傳檔落 per-persona（TASK-0059，第四宿主 —— 0052 QA 實跑撞到：
            //   `Session/_session_status.md` 全域槽、不叫 `_last_` 所以 grep 口徑漏過它）。
            //   caller ＝ lane persona（--persona 戳進 args）；scope=all 也是「誰查的」的視圖，
            //   一樣落自己的檔（`sessionstatus_persona.md` / `sessionstatus_all.md`）。
            // ⚠ 舊路徑零程式讀取端（2026-08-26 grep 全庫僅本行寫入）⇒ 覆寫成指路 stub，不留空殼。
            // ===========================================================
            string aActor = GetArg(args, "persona", "unknown").Trim();
            if (aActor.Length == 0) aActor = "unknown";
            string aPayload = UCL_LettersPath.CmdPayload(aActor, "sessionstatus", aScope);
            Directory.CreateDirectory(Path.GetDirectoryName(aPayload));
            File.WriteAllText(aPayload, aR.ToString(), new UTF8Encoding(false));
            Debug.Log($"[SessionStatus] scope={aScope} persona={aActor} → {aPayload}");
            UCL_AgentCommandRunner.ReportOutputFile(args, aPayload);

            string aOutDir = Path.Combine(UCL_AgentCommandsPath.DataRoot, "Session");
            Directory.CreateDirectory(aOutDir);
            File.WriteAllText(Path.Combine(aOutDir, "_session_status.md"),
                "# （已退場）SessionStatus 回傳檔不再寫在這裡\n\n"
                + "> 這裡曾是**全域單槽**，兩個人同時查 session 狀態會互相覆蓋（TASK-0059，與 TASK-0026 ① 同族）。\n\n"
                + "回傳檔現在落在 **`letters/<persona>/cmd/sessionstatus_<scope>.md`** ——\n"
                + "`run_cmd.py` 會直接印出「📄 回傳檔：<路徑>」，照那一行讀，不要背路徑。\n",
                new UTF8Encoding(false));
        }
    }
}
#endif
