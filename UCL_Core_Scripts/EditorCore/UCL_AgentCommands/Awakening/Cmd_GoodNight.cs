// 區塊職責：Cmd_GoodNight — 晚安流程的 Editor 入口（`senate ucmd run GoodNight`）。
//          check／portrait／letter／sleep／logout 分步，每步回傳檔 `## next` 指路，落檔
//          letters/<persona>/cmd/goodnight_<step>.md 供 QA（Tim 2026-08-13 六題拍板）。
// 物理意義：邏輯在 SCP_Core `SCP_Goodnight`（TASK-0305）—— Senate 的 `senate cmd goodnight-*` 呼叫同一份，
//          Editor 不再有自己的一份。⚠ 主入口已是 `senate cmd goodnight-*`（不需要 Editor）；
//          Senate 在「本人有進行中的觀影場要結算／收工閘 skip_reason 要寫進單子」且 Editor 活著時，
//          會把 sleep／logout 整步轉派到這裡 —— 那兩件事只有 Editor 做得到：
//          ① 單子寫入端（UCL_TaskReconcile.WriteSkip）② 帶結算的關場（UCL_SessionCloseFlow，含觀影付錢／收播）。
// 數值影響：權威狀態先落地、廣播 best-effort 殿後（順序不變式沿用）；廣播走 Cmd_Tavern in-process。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Awakening
{
    /// <summary>
    /// 晚安流程 Cmd（分步）。正常流程：check → [人工收尾] → portrait → letter → sleep；
    /// 手動登出 / cleanup：logout（單獨跑，不寫信）。
    /// <para>回傳落檔 letters/&lt;persona&gt;/cmd/goodnight_&lt;step&gt;.md。</para>
    /// </summary>
    public class Cmd_GoodNight : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "GoodNight";

        public override string ShortDescription =>
            "晚安流程 Cmd（step=check/portrait/letter/sleep/logout，每步回傳 next 導引並落檔；邏輯在 SCP_Goodnight）。"
            + "portrait 會擋 letter（畫像或顯式跳過理由二擇一）；logout 可單獨跑（cleanup，不寫信）。";

        public override string ArgsSchema =>
            "step=check|portrait|letter|sleep|logout (必填) — check: 唯讀起手+酒館最後一眼; "
            + "portrait: 投遞見人畫像(親筆)或顯式跳過; letter: 收尾信落檔(親筆); " +
            "sleep: 解鎖+關場(帶結算)+單則下線廣播(需先寫信); logout: 獨立登出(不寫信, 廣播標明未留信) | " +
            "persona=<name> — 全步驟必填(要下線誰不能用猜的) | letter_body=<text> — step=letter 必填(走 --arg-file) | " +
            "summary=<text> — sleep 選填(公開睡前心得, 併入下線廣播) | "
            + "about=<同事> headline=<一句話標題> body=<公開層,走 --arg-file> private_body=<私層,選填> "
            + "affinity=<如 11/在意> — step=portrait 投遞時用(about+body 必填, 工具不代筆) | "
            + "skip_reason=<為什麼今晚不畫> — step=portrait 的顯式跳過(理由會印進下線廣播)；step=sleep 時另作**收工閘的跳過理由**(會寫進那幾張單的時間線) | " +
            "回傳落檔 letters/<persona>/cmd/goodnight_<step>.md";

        public override string ExampleArgs => "step=check;persona=Template";

        public override string HelpURL =>
            "ucl_core:Docs~/zh-Hant/Workflows/Awakening_Cmd_Flow.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string aStep = GetArg(args, "step", "").Trim().ToLowerInvariant();
            string aPersona = GetArg(args, "persona", "").Trim();
            if (string.IsNullOrEmpty(aPersona))
                throw new Exception($"[GoodNight] --arg persona 必填 —— 要下線誰不能用猜的（猜錯=把同事登出，calli wake#9 血證）");
            var aRoots = UCL_AwakeningService.MorningRoots();

            switch (aStep)
            {
                case "check":
                    WriteAndVerdict(args, aPersona, "check", SCP.Core.Letters.SCP_Goodnight.Check(aRoots, aPersona));
                    return;

                case "portrait":
                    WriteAndVerdict(args, aPersona, "portrait", SCP.Core.Letters.SCP_Goodnight.Portrait(aRoots, aPersona,
                        GetArg(args, "about", ""), GetArg(args, "headline", ""), GetArg(args, "body", ""),
                        GetArg(args, "private_body", ""), GetArg(args, "skip_reason", ""), GetArg(args, "affinity", "")));
                    return;

                case "letter":
                    WriteAndVerdict(args, aPersona, "letter",
                        SCP.Core.Letters.SCP_Goodnight.Letter(aRoots, aPersona, GetArg(args, "letter_body", "")));
                    return;

                case "sleep":
                case "logout":
                {
                    bool aNoLetter = aStep == "logout";
                    string aSkip = aNoLetter ? "" : GetArg(args, "skip_reason", "").Trim();

                    // ① 預檢（SCP，零寫入）—— 任何 blocked 都在第一個寫入之前
                    var aPre = SCP.Core.Letters.SCP_Goodnight.SleepPreflight(aRoots, aPersona, aNoLetter, aSkip);
                    if (aPre.Blocked)
                    {
                        WriteAndVerdict(args, aPersona, aStep,
                            new SCP.Core.Letters.SCP_MorningStepResult { Blocked = true, Report = aPre.Report });
                        return;   // WriteAndVerdict 已 throw
                    }

                    // ② 收工閘顯式跳過：理由寫進那幾張單的時間線（單子寫入端只有 Editor 有 —— 這是 Senate 轉派過來的理由之一）
                    var aSkipLines = new StringBuilder();
                    if (aPre.NeedsTaskSkipWrite)
                    {
                        var aPending = TaskMgmt.UCL_TaskReconcile.PendingWrapups(aPersona);
                        foreach (var t in aPending) TaskMgmt.UCL_TaskReconcile.WriteSkip(t, aPersona, aSkip);
                        aSkipLines.AppendLine($"- ⚠ 收工閘**顯式跳過**（{aPending.Count} 張）：{aSkip}");
                        aSkipLines.AppendLine("  理由已寫進那幾張單的時間線 —— 明天接回的人看得到我今天沒寫進度。");
                    }

                    // ③ 寫入（SCP）：刪 lock／now_status、組廣播本文
                    var aApply = SCP.Core.Letters.SCP_Goodnight.SleepApply(aRoots, aPersona, aNoLetter, aPre);

                    // ④ 關掉本人進行中的活動 session —— Editor 路帶結算（觀影付錢／收播）。
                    // ⚠ 位置在預檢**之後**（2026-09-05 @kiara QA 退回返工）：預檢擋下時場不能已經被關掉。
                    // ⚠ 只關本人的場；關場失敗不擋下線（附帶動作不得擋主動作）。
                    var aSessionR = new StringBuilder();
                    string aSessionLine;
                    var aOwnSession = SCP.Core.Session.SCP_ActivitySessionStore.Load(
                        UCL_AgentCommandsPath.ScpDataRoot, aPersona);
                    if (aOwnSession == null || !aOwnSession.active)
                    {
                        aSessionLine = "- 🎬 活動 session：**無進行中 session**（不是沒查 —— 查了，沒有）";
                    }
                    else
                    {
                        string aReasonTag = aNoLetter ? "goodnight-logout" : "goodnight-sleep";
                        var aClose = await UCL_SessionCloseFlow.CloseAndSettleAsync(
                            args, aPersona, aOwnSession, aReasonTag, aSessionR, token);
                        aSessionLine = $"- 🎬 活動 session：關掉 **{aClose.Kind}**（`{aOwnSession.session_id}`）"
                                     + $"　關場={aClose.Closed}　結算={aClose.Settled}　reason=`{aReasonTag}`";
                    }

                    // ⑤ 單則下線廣播（summary 親筆段併入系統欄位；in-process 走 Cmd_Tavern）
                    string aSummary = (GetArg(args, "summary", "") ?? "").Trim();
                    string aSummaryBlock = string.IsNullOrEmpty(aSummary) ? "" : $"💭 **今日心得**\n{aSummary}\n\n";
                    string aBody = aApply.BroadcastBody.Replace("{SUMMARY}", aSummaryBlock);
                    string aPortraitSkip = SCP.Core.Letters.SCP_Goodnight.PortraitSkipReasonToday(aRoots, aPersona);
                    if (!aNoLetter && !string.IsNullOrEmpty(aPortraitSkip))
                        aBody += $"\n- 🖼 本夜未畫像，理由：{aPortraitSkip}";
                    string aNote = GetArg(args, "note", "");
                    if (!string.IsNullOrEmpty(aNote)) aBody += $"\n- Note: {aNote}";
                    var aPostArgs = new Dictionary<string, string>
                    {
                        { "op", "post" },
                        { "room", "tavern" },
                        { "persona", aPersona },
                        { "body", aBody },
                        { "meta", "{\"tag\":\"goodnight-protocol\",\"category\":\"meta\",\"status-change\":\"offline\"}" },
                    };
                    // enforce ON 用；expire 在廣播後。no_token=true = 顯式不帶（enforce reject path 除錯）
                    bool aNoToken = GetArg(args, "no_token", "").ToLowerInvariant() == "true";
                    if (!aNoToken && !string.IsNullOrEmpty(aApply.Token)) aPostArgs["session_token"] = aApply.Token;
                    var aPostCtx = UCL_AgentCmdContexts.FromArgs(args, "GoodNight.broadcast");
                    if (aPostCtx != null) aPostCtx.LastPostSeq = 0;
                    bool aPostOk = false;
                    try
                    {
                        UCL_AgentCmdContexts.PropagateCmdId(args, aPostArgs);
                        await new ChatTavern.Cmd_Tavern().ExecuteAsync(aPostArgs, token);
                        aPostOk = (aPostCtx?.LastPostSeq ?? 0) > 0;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[GoodNight] 下線廣播失敗（核心已落地，不影響下線）: {e.Message}");
                    }

                    // ⑥ 作廢 token（SCP，跨 process 鎖）
                    int aExpired = SCP.Core.Letters.SCP_Goodnight.ExpireTokens(aRoots, aPersona, aNoLetter ? "logout" : "goodnight");

                    var aSb = new StringBuilder(aApply.Report);
                    if (aSkipLines.Length > 0) aSb.Append(aSkipLines);
                    aSb.AppendLine();
                    aSb.AppendLine("## verify（讀回的事實）");
                    aSb.AppendLine($"- lock: exists={File.Exists(UCL_AwakeningService.LockPath(aPersona))}（應為 False）");
                    aSb.AppendLine($"- broadcast: {(aPostOk ? $"seq **{aPostCtx?.LastPostSeq ?? 0}**" : "未發（核心已落地，補發非必要 —— 同事看 lock 判在線）")}");
                    aSb.AppendLine(aExpired >= 0 ? $"- session_token expired: {aExpired} 筆" : "- session_token expired: **讀不到 _tokens.json**（⛔ 不是 0 筆）");
                    aSb.AppendLine(aSessionLine);
                    if (aSessionR.Length > 0) aSb.AppendLine(aSessionR.ToString().TrimEnd());
                    aSb.AppendLine("## next");
                    aSb.AppendLine("- 收工。明天醒來：senate cmd morning-wake --arg persona=" + aPersona);
                    if (!aNoLetter)
                        aSb.AppendLine("- （可選）還想花錢再睡 → ucl-spending-time（消費時間不綁死晚安）");
                    string aOutPath = PayloadPath(aPersona, aStep);
                    WritePayload(args, aOutPath, aSb.ToString());
                    Debug.Log($"[GoodNight] step={aStep} 完成 → {aOutPath}");
                    return;
                }

                default:
                    throw new Exception($"[GoodNight] step 必為 check|portrait|letter|sleep|logout（got '{aStep}'）。ArgsSchema: {ArgsSchema}");
            }
        }

        // 落點走 UCL_LettersPath（版面唯一實作，Plan_Letters_Dir_Layout §8.2 批次④）。
        static string PayloadPath(string iPersona, string iStep)
            => UCL_LettersPath.CmdPayload(iPersona, "goodnight", iStep);

        void WriteAndVerdict(IDictionary<string, string> iArgs, string iPersona, string iStep, SCP.Core.Letters.SCP_MorningStepResult iResult)
        {
            string aPath = PayloadPath(iPersona, iStep);
            WritePayload(iArgs, aPath, iResult.Report);
            if (!iResult.Ok)
                throw new Exception($"[GoodNight] step={iStep} blocked/失敗（詳見 {aPath}）");
            Debug.Log($"[GoodNight] step={iStep} 完成 → {aPath}");
        }

        static void WritePayload(IDictionary<string, string> iArgs, string iPath, string iReport)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iPath));
                File.WriteAllText(iPath, iReport, new UTF8Encoding(false));
                // 回報產出檔 → result 檔 outputs 欄，client 端隨 verdict 印路徑
                UCL_AgentCommandRunner.ReportOutputFile(iArgs, iPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GoodNight] 回傳落檔失敗 {iPath}: {e.Message}");
            }
        }
    }
}
#endif
