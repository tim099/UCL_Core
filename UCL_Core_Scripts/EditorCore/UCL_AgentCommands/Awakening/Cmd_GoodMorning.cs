// 區塊職責：Cmd_GoodMorning — 早安流程的 Cmd 入口（Plan_Awakening_Flow_Simplification §8.8-§8.9）。
//          同一支 Cmd 以 step 參數分步，每步回傳「下一步怎麼操作、傳哪些參數」（R16 next 導引）。
// 物理意義：實際邏輯在 SCP_Core `SCP_Morning`（TASK-0303：Senate 的 morning-* 與本檔呼叫同一份，
//          Editor 不再有自己的一份）；本檔只做參數解析、步驟分派、回傳落檔。
//          ⚠ 主入口已是 `senate cmd morning-*`（不需要 Editor）；本 Cmd 留給 `senate ucmd run GoodMorning` 那條路。四步：wake（守衛+狀態寫入，不廣播）→ brief（就地跑 SCP_WakeBrief，
//          2026-09-01 起不再 spawn python）→ [agent Read brief] → intro（單則上線廣播＋next 指路 catchup，R21）。
// 數值影響：每一步的回傳值落檔（Tim 2026-08-13 拍板供 QA）——
//          persona 步驟 → letters/<persona>/cmd/goodmorning_<step>.md（回傳檔一律住 cmd/，
//          目錄本身即宣告「機器寫的、該步驟重跑即覆寫」）；全域步驟 audit → AwakenInit/_goodmorning_audit.md。
//          blocked / 失敗一律「payload 落檔＋非零 verdict」雙讀回（§8.9 P2 卡點③）。
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
    /// 早安流程 Cmd（step 分步 + next 導引）。
    ///
    /// <para>正常流程（agent 視角）：</para>
    /// <code>
    /// ① senate ucmd run GoodMorning --arg step=wake  --arg persona=&lt;P&gt; [--arg model=&lt;M&gt;] [--arg actual_agent=&lt;A&gt;]
    /// ② senate ucmd run GoodMorning --arg step=brief --arg persona=&lt;P&gt;
    /// ③ Read（step=brief 回傳的 brief 路徑）
    /// ④ senate ucmd run GoodMorning --arg step=intro --arg persona=&lt;P&gt; --arg-file body=&lt;檔&gt;   ← body 親筆
    /// </code>
    /// <para>每步回傳值落檔 letters/&lt;persona&gt;/cmd/goodmorning_&lt;step&gt;.md；audit 落 AwakenInit/_goodmorning_audit.md。</para>
    /// </summary>
    public class Cmd_GoodMorning : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "GoodMorning";

        public override string ShortDescription =>
            "早安流程 Cmd（step=wake/brief/intro/audit，每步回傳 next 導引並落檔）。brief 就地跑 SCP_WakeBrief；"
            + "Editor 未開時的備援是 `senate cmd wake-brief`（就地跑同一支 SCP_WakeBrief）。";

        public override string ArgsSchema =>
            "step=wake|brief|intro|audit (必填) — wake: 守衛+狀態寫入(不廣播); brief: 生成 wake brief; " +
            "intro: 單則上線廣播(需 body 親筆, Cmd 只組系統欄位); audit: 全 persona 對帳(唯讀) | " +
            "persona=<name> — wake/brief/intro 必填 | model=<M> / actual_agent=<Codex|ClaudeCode|Antigravity> — wake 選填 | " +
            "body=<text> — intro 必填(長文走 --arg-file body=<檔>) | note=<text> — intro 選填 | " +
            "回傳落檔 letters/<persona>/cmd/goodmorning_<step>.md（audit → AwakenInit/_goodmorning_audit.md）";

        public override string ExampleArgs => "step=wake;persona=Template";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/Plan/completed/Plan_Awakening_Flow_Simplification.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string aStep = GetArg(args, "step", "").Trim().ToLowerInvariant();
            string aPersona = GetArg(args, "persona", "").Trim();
            string aEnvMarker = GetArg(args, "_caller_env_marker", "");

            switch (aStep)
            {
                case "audit":
                {
                    string aReport = UCL_AwakeningService.AuditReport()
                        + "\n## next\n- 對帳有 ⚠/🔧 → 人工看該 persona 的 wakes/ 與 registry；全綠 → 無事。\n";
                    string aPath = Path.Combine(UCL_AgentCommandsPath.DataRoot, "AwakenInit", "_goodmorning_audit.md");
                    WritePayload(args, aPath, aReport);
                    Debug.Log($"[GoodMorning] step=audit 完成 → {aPath}");
                    return;
                }

                case "wake":
                {
                    RequirePersona(aStep, aPersona);
                    var aResult = UCL_AwakeningService.StepWake(
                        aPersona, GetArg(args, "model", ""), GetArg(args, "actual_agent", ""), aEnvMarker);
                    string aPath = UCL_AwakeningService.StepPayloadPath(aPersona, "wake");
                    WritePayload(args, aPath, aResult.report);
                    if (!aResult.ok)
                        throw new Exception($"[GoodMorning] step=wake blocked/失敗（詳見 {aPath}）");
                    Debug.Log($"[GoodMorning] step=wake 完成 → {aPath}");
                    return;
                }

                case "brief":
                {
                    RequirePersona(aStep, aPersona);
                    // 根在主執行緒先解析（DataRoot／RepoRoot 的快取屬主執行緒資源）；
                    // 長跑段（brief 組裝要讀幾十個檔）丟背景執行緒，不擋 Editor 主執行緒。
                    var aRoots = UCL_AwakeningService.MorningRoots();
                    var aResult = await UniTask.RunOnThreadPool(
                        () => SCP.Core.Letters.SCP_Morning.Brief(aRoots, aPersona), cancellationToken: token);
                    var aSb = new StringBuilder();
                    aSb.AppendLine($"# GoodMorning step=brief persona={aPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
                    aSb.AppendLine();
                    aSb.AppendLine(aResult.Report);
                    // QA 欄位/格式摘要**不進回傳值**（Tim 2026-08-13 拍板）——agent 的下一步是讀 brief 本體，
                    // 摘要對它是純噪音；人工 QA 走後台頁「📄 生成 brief」按鈕（那裡會顯示 SummarizeBrief）。
                    if (aResult.Ok)
                    {
                        aSb.AppendLine("## next");
                        int aNo = 1;
                        aSb.AppendLine($"{aNo++}. **required** — Read `{aResult.BriefPath}`（接回身分 —— 這步不自動化）");
                        // 條件步驟 B2：無自我介紹文件 → 讀完 brief 後先補件（intro 前置守衛會實擋）
                        if (SCP.Core.Letters.SCP_Morning.FindGlossaryPersonaEntry(aRoots, aPersona) == null)
                        {
                            var aTodo = SCP.Core.Letters.SCP_Morning.SelfIntroTodoLines(aRoots, aPersona);
                            aSb.AppendLine($"{aNo++}. **required** — {aTodo[0]}");
                            for (int i = 1; i < aTodo.Count; i++) aSb.AppendLine(aTodo[i]);
                        }
                        // intro 那一步的指路只有一份（SCP_Morning.IntroNextLines）—— Senate 與 Editor 兩個入口印同一句。
                        foreach (string aLine in SCP.Core.Letters.SCP_Morning.IntroNextLines(aPersona, ref aNo)) aSb.AppendLine(aLine);
                    }
                    string aPath = UCL_AwakeningService.StepPayloadPath(aPersona, "brief");
                    WritePayload(args, aPath, aSb.ToString());
                    if (!aResult.Ok)
                        throw new Exception($"[GoodMorning] brief 生成失敗（詳見 {aPath}）");
                    Debug.Log($"[GoodMorning] step=brief 完成（{aResult.BriefLines} 行）→ {aPath}");
                    return;
                }

                case "intro":
                {
                    RequirePersona(aStep, aPersona);
                    string aBody = GetArg(args, "body", "");
                    string aPath = UCL_AwakeningService.StepPayloadPath(aPersona, "intro");
                    if (string.IsNullOrEmpty(aBody))
                    {
                        WritePayload(args, aPath, "## blocked\n- reason: intro 缺 body —— 自介內容必須 persona 親筆（憲法⑥：屬於自己的東西自己寫），Cmd 只組系統欄位\n- how: --arg-file body=<檔路徑>（長文不經 shell 解析層；⛔ 不是 --arg-stdin，senate 認不得那個旗標）\n");
                        throw new Exception($"[GoodMorning] step=intro 缺 body（詳見 {aPath}）");
                    }

                    // 前置守衛與標頭都在 SCP_Core（SCP_Morning，TASK-0303）—— Senate 的 morning-intro 呼叫同一份。
                    var aRoots = UCL_AwakeningService.MorningRoots();
                    var aCheck = SCP.Core.Letters.SCP_Morning.PrecheckIntro(aRoots, aPersona);
                    if (!aCheck.Ok)
                    {
                        WritePayload(args, aPath, $"# GoodMorning step=intro persona={aPersona}\n\n## blocked\n- reason: {aCheck.Error}\n");
                        throw new Exception($"[GoodMorning] step=intro 前置檢查未過（詳見 {aPath}）");
                    }
                    var aLock = aCheck.Lock;

                    // 兩則併一則（§8.6）：系統欄位段（SCP_Morning 組）＋ 親筆 body —— 走 Cmd_Tavern in-process
                    // post（op=share 同款模式），token enforce / schema 檢查 / 計酬 / mirror 全沿用不重寫。
                    int aWake = 0;
                    string aLayerRole = "";
                    try
                    {
                        // 走接縫，不直讀中央檔（BUG-29 ①：那個檔已退場，而 layer_role 的真相在 profile/）
                        var aPJd = AgentCommands.UCL_PersonaProfile.GetRaw(aPersona);
                        var aP = new UCL_PersonaData(); if (aPJd != null) aP.DeserializeFromJson(aPJd);
                        if (aPJd != null) { aWake = aP.wake_count; aLayerRole = aP.layer_role; }
                    }
                    catch (Exception e) { Debug.LogWarning($"[GoodMorning] persona 檔讀取失敗（自介標頭降級）: {e.Message}"); }
                    string aHeader = SCP.Core.Letters.SCP_Morning.BuildIntroHeader(
                        aRoots, aPersona, aLock.Agent, aLock.Model, aLock.BankAccount, aWake, aLayerRole);
                    string aNote = GetArg(args, "note", "");
                    if (!string.IsNullOrEmpty(aNote)) aHeader += $"\n- Note: {aNote}";
                    string aMerged = aHeader + "\n\n---\n\n" + aBody;

                    var aPostArgs = new Dictionary<string, string>
                    {
                        { "op", "post" },
                        { "room", "tavern" },
                        { "persona", aPersona },
                        { "body", aMerged },
                        { "session_token", aLock.SessionToken },   // enforce ON 時的通行證；OFF 時無害
                        { "meta", "{\"tag\":\"goodmorning-protocol\",\"category\":\"meta\",\"status-change\":\"online\",\"decision\":\"preferred\"}" },
                    };
                    // in-process 呼叫 → 把「我是哪筆 cmd」帶進子 args，seq 才回得到我的 context
                    // （舊制走 Cmd_Tavern.LastPostSeq 全域 static，併行時會拿到別人的號碼）
                    UCL_AgentCmdContexts.PropagateCmdId(args, aPostArgs);
                    var aPostCtx = UCL_AgentCmdContexts.FromArgs(args, "GoodMorning.intro");
                    if (aPostCtx != null) aPostCtx.LastPostSeq = 0;
                    await new ChatTavern.Cmd_Tavern().ExecuteAsync(aPostArgs, token);
                    int aSeq = aPostCtx?.LastPostSeq ?? 0;

                    var aSb = new StringBuilder();
                    aSb.AppendLine($"# GoodMorning step=intro persona={aPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
                    aSb.AppendLine();
                    if (aSeq <= 0)
                    {
                        aSb.AppendLine("## blocked\n- reason: 廣播未落檔（Cmd_Tavern 拒絕或失敗 —— 詳見 ChatTavern/_last_op.md）");
                        WritePayload(args, aPath, aSb.ToString());
                        throw new Exception($"[GoodMorning] step=intro 廣播失敗（詳見 {aPath} 與 ChatTavern/_last_op.md）");
                    }
                    // verify：讀回落地的訊息檔（可讀回的事實，不是 ✓）
                    string aMsgPath = Path.Combine(UCL_AgentCommandsPath.DataRoot, "ChatTavern", "rooms", "tavern",
                        "messages", DateTime.UtcNow.ToString("yyyy-MM-dd"), aSeq.ToString("D8") + ".json");
                    aSb.AppendLine("## verify（讀回的事實）");
                    aSb.AppendLine($"- seq: **{aSeq}**");
                    aSb.AppendLine($"- message: `{aMsgPath}`（exists={File.Exists(aMsgPath)}）");
                    aSb.AppendLine($"- brief 前置: `{aCheck.BriefPath}`（{aCheck.BriefLines} 行，mtime 晚於 locked_at）");
                    aSb.AppendLine("## next");
                    // 🩸 指路只寫**現在還活著的入口**。判準不是「寫得對不對」，是
                    //    **回傳檔的 next 是可直接照跑的指令** —— 這個預期是 R16/R17 整套流程的地基，
                    //    破一次，下一個人就得開始懷疑每一條 next（BUG-31，basecamp wake#68 實撞）。
                    //    ⚠ 而退場的入口不一定留得下 stub：檔案被刪之後，照跑得到的是
                    //    「找不到檔案」，一句話都不解釋 ⇒ **不可以靠「它自己會印指路」兜底**。
                    aSb.AppendLine($"1. **required** — 酒館 catchup（知道在線同事＋追上訊息；照 ucl-ding 流程但**不強制回**）：");
                    aSb.AppendLine($"   senate cmd morning-catchup --arg persona={aPersona}");
                    aSb.AppendLine($"   （回傳檔 `letters/{aPersona}/cmd/ding_brief.md`；`--persona` 同時決定 queue 路由並戳進 args，不必再寫 --arg persona=）");
                    aSb.AppendLine("2. 之後照 brief §9 的今日動作清單走（見林 OVERDUE / 見森待折是 morning 的一部分，不是選配）。");
                    WritePayload(args, aPath, aSb.ToString());
                    Debug.Log($"[GoodMorning] step=intro 完成 seq={aSeq} → {aPath}");
                    return;
                }

                default:
                    throw new Exception(
                        $"[GoodMorning] step 必為 wake|brief|intro|audit（got '{aStep}'）。ArgsSchema: {ArgsSchema}");
            }
        }

        static void RequirePersona(string iStep, string iPersona)
        {
            if (string.IsNullOrEmpty(iPersona))
                throw new Exception($"[GoodMorning] step={iStep} 需要 --arg persona=<name>");
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
                Debug.LogWarning($"[GoodMorning] 回傳落檔失敗 {iPath}: {e.Message}");
            }
        }
    }
}
#endif
