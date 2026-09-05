// 區塊職責：Cmd_GoodNight — 晚安流程的 Cmd 入口（Plan_Goodnight_Flow_Simplification §7）。
//          與 Cmd_GoodMorning 對稱：step 分步、每步回傳檔 `## next` 指路、每步落檔
//          letters/<persona>/cmd/goodnight_<step>.md 供 QA（Tim 2026-08-13 六題拍板）。
// 物理意義：邏輯全在 UCL_AwakeningService（morning 同一 class，lock/registry/paths 共用）。
//          三步：check（唯讀＋酒館最後一眼）→ letter（收尾信親筆落檔）→ sleep（offline→
//          解鎖→單則下線廣播→expire token）。logout ＝獨立步驟（不綁晚安流程、persona 顯式必填、
//          不寫信不偽造心得 —— 廣播標明未留信），後台一鍵登出走同一條。
// 數值影響：權威狀態先落地、廣播 best-effort 殿後（順序不變式沿用）；廣播走 Cmd_Tavern
//          in-process，舊 goodnight 的「廣播逾時吐手動補發指令」graceful-degradation 從根消失。
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
    /// 晚安流程 Cmd（分步）。正常流程：check → [人工收尾] → letter → sleep；
    /// 手動登出 / cleanup：logout（單獨跑，不寫信）。
    /// <para>回傳落檔 letters/&lt;persona&gt;/cmd/goodnight_&lt;step&gt;.md。</para>
    /// </summary>
    public class Cmd_GoodNight : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "GoodNight";

        public override string ShortDescription =>
            "晚安流程 Cmd（step=check/portrait/letter/sleep/logout，每步回傳 next 導引並落檔）。"
            + "portrait 會擋 letter（畫像或顯式跳過理由二擇一）；logout 可單獨跑（cleanup，不寫信）。";

        public override string ArgsSchema =>
            "step=check|portrait|letter|sleep|logout (必填) — check: 唯讀起手+酒館最後一眼; "
            + "portrait: 投遞見人畫像(親筆)或顯式跳過; letter: 收尾信落檔(親筆); " +
            "sleep: offline+解鎖+單則下線廣播(需先寫信); logout: 獨立登出(不寫信, 廣播標明未留信) | " +
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

            switch (aStep)
            {
                case "check":
                {
                    var aResult = UCL_AwakeningService.StepCheck(aPersona);
                    WriteAndVerdict(args, aPersona, "check", aResult);
                    return;
                }

                case "portrait":
                {
                    var aResult = UCL_AwakeningService.StepPortrait(
                        aPersona,
                        GetArg(args, "about", ""),
                        GetArg(args, "headline", ""),
                        GetArg(args, "body", ""),
                        GetArg(args, "private_body", ""),
                        GetArg(args, "skip_reason", ""),
                        GetArg(args, "affinity", ""));
                    WriteAndVerdict(args, aPersona, "portrait", aResult);
                    return;
                }

                case "letter":
                {
                    var aResult = UCL_AwakeningService.StepLetter(aPersona, GetArg(args, "letter_body", ""));
                    WriteAndVerdict(args, aPersona, "letter", aResult);
                    return;
                }

                case "sleep":
                case "logout":
                {
                    bool aNoLetter = aStep == "logout";

                    var aResult = UCL_AwakeningService.PrepareSleep(
                        aPersona, aNoLetter,
                        out string aBroadcastBody, out string aToken, out var aP,
                        GetArg(args, "skip_reason", ""));
                    if (!aResult.ok)
                    {
                        WriteAndVerdict(args, aPersona, aStep, aResult);
                        return;   // WriteAndVerdict 已 throw
                    }

                    // ── E（TASK-0057）：關掉這個人**進行中的活動 session** ────────────────
                    // ⚠ 位置在 `PrepareSleep` **之後**（2026-09-05 @kiara QA 退回返工後改的）。
                    // 🩸 原本在它之前，而 `PrepareSleep` 有好幾條 blocked 出口（沒寫收尾信／lock 不對）⇒
                    //   **下線失敗、而場已經被關掉了，且不可回復**。她的活體：
                    //   `exit_code=1`、`_session.json` 還在（人沒下線），而 `sessions/Template.json`
                    //   已經 `active=false`、`end_reason=goodnight-sleep` —— **一個沒有發生過的事件**。
                    //   而使用者拿到的訊息是「去寫收尾信，然後再來睡」，那句話明確暗示「什麼都還沒發生」。
                    // 📌 我原本把它放前面的理由是「反過來的話關場那一步已經不在線」——
                    //   **那是推論不是讀數**。今天量了：`Cmd_StreamWatch.SettleResidueAsync` 全函式
                    //   對 `IsOnline` / `LockPath` **零命中** ⇒ 結算不依賴在線。
                    //   ⇒ 已證實的傷害贏過推測的傷害（憲法②）。
                    // ⚠ 只關**本人**的場；關場失敗**不擋**下線（附帶動作不得擋主動作）——
                    //   這個方向本來就守著，而反方向今天才補上。
                    var aSessionR = new StringBuilder();
                    string aSessionLine;
                    var aOwnSession = SCP.Core.Session.SCP_ActivitySessionStore.Load(
                        UCL_AgentCommandsPath.ScpDataRoot, aPersona);
                    if (aOwnSession == null || !aOwnSession.active)
                    {
                        // ⚠ 零場要**印出來**，不是沉默 —— 沉默時「沒有場」與「這段沒跑」同形。
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

                    // 單則下線廣播（summary 親筆段併入系統欄位；in-process 走 Cmd_Tavern，policy 全沿用）
                    string aSummary = (GetArg(args, "summary", "") ?? "").Trim();
                    string aSummaryBlock = string.IsNullOrEmpty(aSummary) ? "" : $"💭 **今日心得**\n{aSummary}\n\n";
                    string aBody = aBroadcastBody.Replace("{SUMMARY}", aSummaryBlock);
                    // 本夜顯式跳過畫像的理由要被看見（給了理由卻沒人看得見，那個參數就只是形式）
                    string aPortraitSkip = UCL_AwakeningService.PortraitSkipReasonToday(aPersona);
                    if (!aNoLetter && !string.IsNullOrEmpty(aPortraitSkip))
                        aBody += $"\n- 🖼 本夜未畫像，理由：{aPortraitSkip}";
                    string aNote = GetArg(args, "note", "");
                    if (!string.IsNullOrEmpty(aNote)) aBody += $"\n- Note: {aNote}";

                    var aMeta = UCL_RegistryMeta.LoadFromFile(UCL_AwakeningService.RegistryMetaPath);
                    string aActor = UCL_AwakeningService.ResolveBankAccount(aMeta,
                        UCL_AwakeningService.NormalizeAgent(aMeta, aP.agent ?? ""));
                    var aPostArgs = new Dictionary<string, string>
                    {
                        { "op", "post" },
                        { "room", "tavern" },
                        { "persona", aPersona },
                        { "body", aBody },
                        { "meta", "{\"tag\":\"goodnight-protocol\",\"category\":\"meta\",\"status-change\":\"offline\"}" },
                    };
                    // enforce ON 用；expire 在廣播後。no_token=true = 顯式不帶（enforce reject path 除錯，
                    // 對齊舊 goodnight --session-token "" 的三態語意）
                    bool aNoToken = GetArg(args, "no_token", "").ToLowerInvariant() == "true";
                    if (!aNoToken && !string.IsNullOrEmpty(aToken)) aPostArgs["session_token"] = aToken;
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

                    int aExpired = UCL_AwakeningService.ExpireTokens(aPersona, aNoLetter ? "logout" : "goodnight");

                    var aSb = new StringBuilder(aResult.report);
                    aSb.AppendLine();
                    aSb.AppendLine("## verify（讀回的事實）");
                    aSb.AppendLine($"- lock: exists={File.Exists(UCL_AwakeningService.LockPath(aPersona))}（應為 False）");
                    aSb.AppendLine($"- broadcast: {(aPostOk ? $"seq **{aPostCtx?.LastPostSeq ?? 0}**" : "未發（核心已落地，補發非必要 —— 同事看 lock 判在線）")}");
                    aSb.AppendLine($"- session_token expired: {aExpired} 筆");
                    aSb.AppendLine(aSessionLine);
                    if (aSessionR.Length > 0)
                    {
                        // 逐段細節照原樣附上（① 權威狀態／② 結算）—— 摘要那一行只給讀數，細節給查的人。
                        aSb.AppendLine(aSessionR.ToString().TrimEnd());
                    }
                    aSb.AppendLine("## next");
                    aSb.AppendLine("- 收工。明天醒來：senate ucmd run GoodMorning --arg step=wake --arg persona=" + aPersona);
                    if (!aNoLetter)
                        aSb.AppendLine("- （可選）還想花錢再睡 → ucl-spending-time（消費時間不綁死晚安）");
                    string aOutPath = PayloadPath(aPersona, aStep);
                    WritePayload(args, aOutPath, aSb.ToString());
                    Debug.Log($"[GoodNight] step={aStep} 完成 → {aOutPath}");
                    return;
                }

                default:
                    throw new Exception($"[GoodNight] step 必為 check|letter|sleep|logout（got '{aStep}'）。ArgsSchema: {ArgsSchema}");
            }
        }

        // 落點走 UCL_LettersPath（版面唯一實作，Plan_Letters_Dir_Layout §8.2 批次④）。
        // ⚠ 對側契約：python 端等價入口 = `_lib/ucl_paths.py::letters_cmd_payload()`。
        static string PayloadPath(string iPersona, string iStep)
            => UCL_LettersPath.CmdPayload(iPersona, "goodnight", iStep);

        void WriteAndVerdict(IDictionary<string, string> iArgs, string iPersona, string iStep, UCL_AwakeningService.StepResult iResult)
        {
            string aPath = PayloadPath(iPersona, iStep);
            WritePayload(iArgs, aPath, iResult.report);
            if (!iResult.ok)
                throw new Exception($"[GoodNight] step={iStep} blocked/失敗（詳見 {aPath}）");
            Debug.Log($"[GoodNight] step={iStep} 完成 → {aPath}");
        }

        static void WritePayload(IDictionary<string, string> iArgs, string iPath, string iReport)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iPath));
                File.WriteAllText(iPath, iReport, new UTF8Encoding(false));
                // 回報產出檔 → result 檔 outputs 欄，run_cmd 端隨 verdict 印路徑（不再靠 skill 背）
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
