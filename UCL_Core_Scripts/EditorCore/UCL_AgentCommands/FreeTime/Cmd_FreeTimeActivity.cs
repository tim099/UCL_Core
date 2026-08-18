
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 08/18 2026
// 自由時間「活動層」入口 —— 選活動 / 收活動，兩步都把下一步指回流程。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.FreeTime
{
    // ===========================================================
    // 區塊職責：把自由時間的活動包一層 Cmd（Tim 2026-08-18 拍板）。
    //
    // 物理意義：原本活動是「自己去跑 chess.py / canvas.py / library.py / Cmd_Sculpture」，
    //          於是自由時間的流程提示**只活在 Cmd_FreeTime 的回傳檔裡** ——
    //          而人一旦進到活動工具，那些工具的輸出一個字都沒提自由時間，
    //          流程就斷在那裡（Tim 觀察：很容易中斷、或一直重骰卻沒開工）。
    //          原本的修法是「在五個活動工具的收尾各加一段提示」——
    //          那是**五個不同的收尾**，其中一個漏掉不會有人發現。
    //          包一層之後，提示長在**唯一的入口**上。
    //
    // 迴圈形狀（Tim 定的）：
    //   FreeTime step=next（骰清單 ＋ 讀未讀訊息 ＋ 可帶 body 聊天）
    //     → FreeTimeActivity op=pick   （回傳「這件活動怎麼執行」）
    //     → FreeTimeActivity op=step   （**代跑一步**，回傳工具輸出 ＋ 下一步）… 可重複
    //     → FreeTimeActivity op=done   （回傳「去換骰」）
    //     → 回到 step=next … 直到 Cmd 宣布收工
    //
    // ⚠ **我一開始判斷錯，記在這裡**：原設計是「本 Cmd 不代跑活動」，理由寫成
    //    「下棋／繪圖是多步互動，一次性 Cmd 跑不完」。Tim 2026-08-18 點破 ——
    //    那是把兩件事混成一件：**活動橫跨很多步 ≠ 一次呼叫做不完一步**。
    //    走一子、放一個像素本來就是次秒級的一次性動作。
    //    ⇒ 於是 op=step 存在：代跑**一步**，然後在回傳檔接上下一步。
    //    （原本那個理由對「包整場」是對的，對「包一步」是錯的 —— 同一句話換了範圍就變號。）
    //
    // 職責：守衛（真的在自由時間嗎）／記錄（選了什麼、做了幾件）／**代跑一步**／宣告／指路。
    // 數值影響：寫 session 的 activity / activities_done；spawn 一顆 python（op=step）；
    //          發酒館訊息（pick / done）；每個 op 各寫一份回傳檔。
    // ===========================================================
    /// <summary>
    /// 自由時間活動層。<c>op=pick</c> 選活動（回傳執行方式）、<c>op=done</c> 收活動（回傳換骰指路）。
    /// </summary>
    public class Cmd_FreeTimeActivity : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "FreeTimeActivity";

        public override string ShortDescription =>
            "自由時間活動層：op=pick 選活動並取得執行方式；op=done 收活動並指回換骰。";

        public override string ArgsSchema =>
            "op=pick|step|done（required） | persona=<名字>（required） | " +
            "step=<子命令，op=step 必填；須在該活動 md 的 steps 白名單內> | " +
            "step_args=<傳給工具的其餘參數，原樣附在子命令之後> | " +
            "activity=<活動 id，op=pick 必填；不確定就跑 op=pick 不帶它，會列清單> | " +
            "body=<開場／收筆想跟同事說的話，可選> | " +
            "followed_dice=true|false（預設 true；false 會在宣告裡註明「本輪未跟骰」）";

        public override string ExampleArgs => "op=pick;persona=basecamp;activity=chess";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/Workflows/Awakening_Cmd_Flow.md";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string aOp = GetArg(args, "op", "").Trim().ToLowerInvariant();
            string aPersona = GetArg(args, "persona", "").Trim();
            if (string.IsNullOrEmpty(aPersona))
            {
                // 不猜身分 —— 多 persona 環境猜錯會替別人記活動，而那看起來完全正常。
                throw new Exception("[FreeTimeActivity] 需要 --arg persona=<名字>（不猜身分）");
            }
            if (aOp != "pick" && aOp != "step" && aOp != "done")
            {
                throw new Exception($"[FreeTimeActivity] op 必為 pick|step|done（got '{aOp}'）");
            }

            string aPath = Cmd_FreeTime.PayloadPath(aPersona, "activity");
            var aR = new StringBuilder();
            aR.AppendLine($"# FreeTimeActivity op={aOp} persona={aPersona}  ts=`{DateTime.Now:yyyy-MM-dd HH:mm:sszzz}`（本地時間）");
            aR.AppendLine();

            // ── 守衛：必須真的在自由時間中 ────────────────────────────────
            // 只看 active 不夠（超時沒收工的人會停在 true）—— 判準走 session base 的唯一那條。
            var aSession = Cmd_FreeTime.LoadSession(aPersona);
            DateTime aNow = DateTime.Now;
            bool aRunning = aSession != null && aSession.IsRunningAt(aNow, out DateTime? aEnd);
            aEnd = aSession != null ? UCL_SessionBase.ParseIsoToLocal(aSession.end_ts) : null;
            if (!aRunning)
            {
                aR.AppendLine("## blocked");
                aR.AppendLine(aSession == null
                    ? "- reason: 沒有自由時間 session"
                    : aSession.active
                        ? $"- reason: session 已過期（預定收工 {aSession.until_local}）—— 超時未收工的殘留"
                        : $"- reason: session 已收工（{(string.IsNullOrEmpty(aSession.end_reason) ? "未記原因" : aSession.end_reason)}）");
                aR.AppendLine($"- exit①: 開新場 → run_cmd.py run FreeTime --arg step=start --arg persona={aPersona} --arg until=<HH:mm>");
                aR.AppendLine($"- exit②: 過期殘留要結算 → run_cmd.py run FreeTime --arg step=next --arg persona={aPersona}（它會宣布收工）");
                Cmd_FreeTime.WritePayload(args, aPath, aR.ToString());
                throw new Exception($"[FreeTimeActivity] blocked：不在自由時間中（詳見 {aPath}）");
            }

            int aRemain = aEnd.HasValue ? (int)Math.Max(0, (aEnd.Value - aNow).TotalMinutes) : 0;
            aR.AppendLine("## time（時間感由 Cmd 供給 —— 別自己心算）");
            aR.AppendLine($"- 當前時間: **{aNow:yyyy-MM-dd HH:mm}**　自由時間到: **{aSession.until_local}**　剩餘: **{aRemain} 分**");
            aR.AppendLine($"- 本場換骰 {aSession.rounds} 輪｜活動實作 {aSession.activities_done} 件");
            aR.AppendLine();

            string aBody = GetArg(args, "body", "").Trim();
            bool aFollowedDice = GetArg(args, "followed_dice", "true").Trim().ToLowerInvariant() != "false";

            if (aOp == "pick")
            {
                await OpPick(args, aPersona, aSession, aBody, aFollowedDice, aRemain, aR, aPath, token);
            }
            else if (aOp == "step")
            {
                await OpStep(args, aPersona, aSession, aRemain, aR, aPath, token);
            }
            else
            {
                await OpDone(args, aPersona, aSession, aBody, aRemain, aR, aPath, token);
            }
        }

        // ===========================================================
        // 區塊職責：op=pick —— 選一件活動，回傳**它怎麼執行**。
        // 物理意義：執行方式取自活動 md 的 frontmatter（`how`）與 md 路徑本身，
        //          **不在本檔另建一張對照表** —— 兩份清單漂移時，症狀是
        //          「Cmd 說這樣跑、md 說那樣跑」，而兩邊都不會報錯。
        // 數值影響：session.activity / activities_done 遞增；發一則開場宣告。
        // ===========================================================
        static async UniTask OpPick(Dictionary<string, string> iArgs, string iPersona,
            UCL_FreeTimeSession ioSession, string iBody, bool iFollowedDice, int iRemain,
            StringBuilder ioR, string iPath, CancellationToken iToken)
        {
            string aWant = GetArg(iArgs, "activity", "").Trim().ToLowerInvariant();
            var aAll = UCL_FreeTimeIO.ScanActivities();
            UCL_FreeTimeActivity aHit = null;
            foreach (var a in aAll)
            {
                if (!a.enabled) continue;
                if (string.Equals(a.id, aWant, StringComparison.OrdinalIgnoreCase)) { aHit = a; break; }
            }

            if (aHit == null)
            {
                // 不猜活動 —— 猜錯會把「下棋」記成「閱讀」，而帳面上看不出來。
                ioR.AppendLine("## blocked");
                ioR.AppendLine(string.IsNullOrEmpty(aWant)
                    ? "- reason: 沒給 activity"
                    : $"- reason: 找不到活動 id '{aWant}'");
                ioR.AppendLine("- 可用的 id（掃活動 md 得來，不是寫死的清單）：");
                foreach (var a in aAll)
                {
                    if (!a.enabled) continue;
                    ioR.AppendLine($"  - `{a.id}` — {a.name}");
                }
                ioR.AppendLine($"- 用法: run_cmd.py run FreeTimeActivity --arg op=pick --arg persona={iPersona} --arg activity=<id>");
                Cmd_FreeTime.WritePayload(iArgs, iPath, ioR.ToString());
                throw new Exception($"[FreeTimeActivity] op=pick blocked：活動 id 無效（詳見 {iPath}）");
            }

            // 記錄選擇（活動層是 activities_done 的唯一寫入端）
            ioSession.activity = aHit.id;
            ioSession.activities_done += 1;
            Cmd_FreeTime.SaveSession(iPersona, ioSession);

            // 開場宣告（跟骰／未跟骰要可觀測 —— 原本靠人自己在 post 裡註明）
            var aPost = new StringBuilder();
            aPost.AppendLine($"▶️ [{iPersona} 大小姐] 自由時間開做：**{aHit.name}**"
                             + (iFollowedDice ? "" : "（**本輪未跟骰** —— 自由意志優先）"));
            if (!string.IsNullOrEmpty(iBody))
            {
                aPost.AppendLine();
                aPost.AppendLine(iBody);
            }
            int aSeq = await Cmd_FreeTime.TavernPost(iArgs, iPersona, aPost.ToString(), "activity-pick", iToken);

            ioR.AppendLine($"## 已選：**{aHit.name}**（id `{aHit.id}`）");
            ioR.AppendLine($"- 跟骰: {(iFollowedDice ? "是" : "**否 —— 已在宣告註明未跟骰**")}");
            ioR.AppendLine($"- 開場宣告: {(aSeq > 0 ? $"seq **{aSeq}**" : "未發（best-effort）")}");
            if (aHit.minMinutes > 0 && iRemain < aHit.minMinutes)
            {
                // 不擋 —— 截止是軟的；但要說清楚，別讓人以為系統認可這個選擇沒有代價。
                ioR.AppendLine($"- ⏳ **本場時間可能不夠**（建議 ≥{aHit.minMinutes} 分，剩 {iRemain} 分）—— 沒擋你，但別怪骰子");
            }
            ioR.AppendLine();
            ioR.AppendLine("## 怎麼執行（取自活動 md 的 frontmatter，不是本 Cmd 另編的）");
            ioR.AppendLine($"- {(string.IsNullOrEmpty(aHit.how) ? "（該 md 沒填 how）" : aHit.how)}");
            ioR.AppendLine($"- 📄 細節全文：`{aHit.path}`");
            ioR.AppendLine();
            // ⚠ 這一段 2026-08-18 實跑時補上 —— 原本只指 op=done，漏了 op=step（後加的那個 op）。
            //   漏的後果不是報錯，是**代跑能力隱形**：讀的人不知道可以讓 Cmd 跑，就自己去跑工具，
            //   然後流程又斷在工具那邊 —— 正是這整層要解的問題本身。
            ioR.AppendLine("## ▶ 下一步");
            if (!string.IsNullOrEmpty(aHit.tool) && aHit.steps != null && aHit.steps.Count > 0)
            {
                ioR.AppendLine($"**本活動支援 Cmd 代跑一步**（工具 `{aHit.tool}`）—— 一步一步來，每一步的回傳都會接上下一步：");
                ioR.AppendLine("```bash");
                ioR.AppendLine($"python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona {iPersona} run FreeTimeActivity \\");
                ioR.AppendLine($"    --arg op=step --arg persona={iPersona} --arg activity={aHit.id} \\");
                ioR.AppendLine($"    --arg step=<{string.Join("|", aHit.steps)}> --arg step_args=\"<其餘參數>\"");
                ioR.AppendLine("```");
                ioR.AppendLine("- 也可以自己直接跑上面那支工具 —— 但走 op=step 的話輸出會併進回傳檔，流程不會斷。");
            }
            else
            {
                ioR.AppendLine($"本活動**尚未支援 Cmd 代跑** —— 照上面的方式自己跑（要接的話在 `{aHit.path}` 的 frontmatter 加 `tool:` / `steps:`）。");
            }
            ioR.AppendLine();
            ioR.AppendLine("這件活動告一段落再跑這行（**不要直接跳去換骰**）：");
            ioR.AppendLine("```bash");
            ioR.AppendLine($"python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona {iPersona} run FreeTimeActivity \\");
            ioR.AppendLine($"    --arg op=done --arg persona={iPersona} [--arg-file body=<一句心得／收筆>]");
            ioR.AppendLine("```");
            ioR.AppendLine("- 走 op=done 而不是直接換骰，是為了讓「做完了」跟「放棄了」在帳上不同形。");
            Cmd_FreeTime.WritePayload(iArgs, iPath, ioR.ToString());
            Debug.Log($"[FreeTimeActivity] op=pick {iPersona} → {aHit.id} → {iPath}");
        }

        // ===========================================================
        // 區塊職責：op=step —— **代跑活動的一步**，回傳結果並接上下一步。
        //
        // 物理意義：Tim 2026-08-18 點破的那一格 —— 我原本判斷「活動是多步互動，Cmd 跑不完」，
        //          那是把兩件事混在一起：**活動橫跨很多步 ≠ 一次呼叫做不完一步**。
        //          下棋走一子、繪圖放一個像素本來就是次秒級的一次性動作；
        //          拆成一步之後，每一步的回傳檔都能接上下一步 —— 流程就再也斷不掉。
        //
        // 邊界（刻意的）：
        //   - **白名單**：step 必須在該活動 md 的 `steps` 裡。沒有白名單＝把任意 argv
        //     交給外部程式（CLI 注入面）。`tool` / `steps` 空 ⇒ 拒跑並指回 op=pick ——
        //     **「還沒接」與「壞掉」要長得不一樣**。
        //   - **超時 60s**：一步本來就該是次秒級；跑超過一分鐘的東西不是「一步」。
        //   - **process 一律登記**（硬規則），tag 串 persona ——
        //     🩸 StreamWatch 2026-08-16 血證：全場共用 tag ＋ 預設 singleton ⇒
        //     後起跑的人會殺掉別人正在跑的那顆，症狀是 exit=-1 且 stderr 全空。
        //     修法不是 allowMultiple（那是把保護關掉），是把 singleton 縮到 per-persona。
        //   - **stdout 原樣搬**，不由 C# 改寫 —— 工具已經分好的區別（例如「0 筆」與「查不到」）
        //     任何重新措辭都可能把它磨平。
        //
        // 數值影響：spawn 一顆 python、寫一份回傳檔；**不動 activities_done**（那在 pick 記）。
        // ===========================================================
        static async UniTask OpStep(Dictionary<string, string> iArgs, string iPersona,
            UCL_FreeTimeSession iSession, int iRemain,
            StringBuilder ioR, string iPath, CancellationToken iToken)
        {
            string aWant = GetArg(iArgs, "activity", "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(aWant)) aWant = (iSession.activity ?? "").Trim().ToLowerInvariant();
            string aStep = GetArg(iArgs, "step", "").Trim();
            string aStepArgs = GetArg(iArgs, "step_args", "").Trim();

            UCL_FreeTimeActivity aHit = null;
            foreach (var a in UCL_FreeTimeIO.ScanActivities())
            {
                if (a.enabled && string.Equals(a.id, aWant, StringComparison.OrdinalIgnoreCase)) { aHit = a; break; }
            }
            if (aHit == null)
            {
                ioR.AppendLine("## blocked");
                ioR.AppendLine($"- reason: 找不到活動 id '{aWant}'（op=step 要 --arg activity=，或先跑 op=pick 記錄選擇）");
                Cmd_FreeTime.WritePayload(iArgs, iPath, ioR.ToString());
                throw new Exception($"[FreeTimeActivity] op=step blocked：活動無效（詳見 {iPath}）");
            }
            if (string.IsNullOrEmpty(aHit.tool) || aHit.steps == null || aHit.steps.Count == 0)
            {
                // 還沒接 ≠ 壞掉。指回 op=pick 讓人自己跑，不假裝這裡有能力。
                ioR.AppendLine("## blocked");
                ioR.AppendLine($"- reason: 活動 **{aHit.name}** 尚未支援代跑（md frontmatter 沒有 `tool` / `steps`）");
                ioR.AppendLine($"- exit: 走 op=pick 取得指令自己跑 → run FreeTimeActivity --arg op=pick --arg persona={iPersona} --arg activity={aHit.id}");
                ioR.AppendLine($"- 要接的話：在 `{aHit.path}` 的 frontmatter 加 `tool:` 與 `steps:`");
                Cmd_FreeTime.WritePayload(iArgs, iPath, ioR.ToString());
                throw new Exception($"[FreeTimeActivity] op=step blocked：{aHit.id} 未支援代跑（詳見 {iPath}）");
            }
            bool aAllowed = false;
            foreach (var aS in aHit.steps)
            {
                if (string.Equals(aS, aStep, StringComparison.OrdinalIgnoreCase)) { aAllowed = true; break; }
            }
            if (!aAllowed)
            {
                ioR.AppendLine("## blocked");
                ioR.AppendLine(string.IsNullOrEmpty(aStep) ? "- reason: 沒給 step" : $"- reason: step '{aStep}' 不在白名單內");
                ioR.AppendLine($"- 可用 step（取自 `{aHit.path}` 的 `steps`）：{string.Join(" / ", aHit.steps)}");
                Cmd_FreeTime.WritePayload(iArgs, iPath, ioR.ToString());
                throw new Exception($"[FreeTimeActivity] op=step blocked：step 不在白名單（詳見 {iPath}）");
            }

            var aRun = await RunToolStep(aHit, aStep, aStepArgs, iPersona, iToken);

            ioR.AppendLine($"## {aHit.name} — step `{aStep}`　{(aRun.ok ? "✅ 成功" : "❌ 失敗")}");
            ioR.AppendLine($"- 工具: `{aHit.tool}`　參數: `{aStep} {aStepArgs}`".TrimEnd());
            if (!aRun.ok) ioR.AppendLine($"- 錯誤: {aRun.err}");
            ioR.AppendLine();
            ioR.AppendLine("### 工具輸出（原樣，未經改寫）");
            ioR.AppendLine("```");
            ioR.AppendLine(string.IsNullOrWhiteSpace(aRun.stdout) ? "(無輸出)" : aRun.stdout.TrimEnd());
            ioR.AppendLine("```");
            ioR.AppendLine();
            ioR.AppendLine($"## ▶ 下一步（自由時間**進行中**，剩 {iRemain} 分）");
            ioR.AppendLine("- 這件活動還要再走一步 → 再跑一次 op=step（換 `--arg step=` / `--arg step_args=`）");
            ioR.AppendLine($"- 這件活動告一段落 → `run FreeTimeActivity --arg op=done --arg persona={iPersona} [--arg-file body=<一句心得>]`");
            ioR.AppendLine("- ⚠ 別直接跳去 step=next —— 走 op=done 才留下「做完了」的紀錄（跟「放棄了」不同形）。");
            Cmd_FreeTime.WritePayload(iArgs, iPath, ioR.ToString());
            Debug.Log($"[FreeTimeActivity] op=step {iPersona} {aHit.id}/{aStep} ok={aRun.ok} → {iPath}");
        }

        // 區塊職責：跑一步外部工具（照 Cmd_StreamWatch 的 spawn 慣例，含它踩過的坑）。
        // 數值影響：等待丟 thread pool（主執行緒不凍）；60s 超時；非 0 exit 回失敗但仍把 stdout 交回去
        //          —— 失敗時的輸出往往就是原因，吞掉它等於把診斷資訊丟了。
        static async UniTask<(bool ok, string stdout, string err)> RunToolStep(
            UCL_FreeTimeActivity iActivity, string iStep, string iStepArgs, string iPersona, CancellationToken iToken)
        {
            try
            {
                string aTool = System.IO.Path.Combine(
                    UCL_EditorPath.CorePath, "Tools~", "AgentCommands", iActivity.tool);
                if (!System.IO.File.Exists(aTool)) return (false, "", $"找不到工具：{aTool}");

                // 區塊職責：參數走 **ArgumentList（argv 陣列）**，不自己組單一字串。
                //
                // 物理意義：`Arguments` 是一個字串，於是**引號同時要扮演兩個角色** ——
                //          「當內容送過去」與「把多個詞綁成一個 argument」。一個字元兩種語意，
                //          而 CreateProcess 只認後者。⇒ 早上為了 JSON payload 補的 `\"` 逃脫
                //          解決了前者，但也讓引號**永遠不再具有綁詞能力**。
                //
                // 🩸 2026-08-18 兩次實跑（同一個字元，兩個相反的症狀）：
                //   ① `--pixels [{"x":518,...}]` → 引號被 CreateProcess 吃掉 ⇒ canvas.py 回
                //      「JSON 解析失敗」（錯誤訊息指向 canvas.py，真因在這一層）。
                //   ② `--say "多個 詞"` → 逃脫後的 `\"` 成了**內容**，argparse 只吃到 `"多個`，
                //      `詞"` 變成多餘 positional ⇒ exit=2，**而那一步從未發生**。
                //
                // ⇒ 修法不是再調一次逃脫規則（那是在兩個相反需求之間挑一邊），
                //   而是**把兩個角色分到兩層**：切詞在這裡做（引號＝綁詞），
                //   逐個 token 交給 ArgumentList（.NET 依平台規則自己逃脫＝內容原樣抵達）。
                //   兩件事因此不再共用同一個字元。
                var aPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "python",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                };
                aPsi.ArgumentList.Add(aTool);
                aPsi.ArgumentList.Add(iStep);
                foreach (var aTok in SplitStepArgs(iStepArgs)) aPsi.ArgumentList.Add(aTok);

                var aProc = System.Diagnostics.Process.Start(aPsi);
                if (aProc == null) return (false, "", "Process.Start 回 null");

                // tag 串 persona —— 同一人的新一步收掉自己上一顆，別人的完全不碰（見上方血證）
                using var aScope = UCL_ProcessRegistryService.RegisterScope(
                    aProc, $"freetime_activity_{iPersona}",
                    $"自由時間活動一步（{iActivity.id}/{iStep}・{iPersona}）", nameof(Cmd_FreeTimeActivity));

                string aOut = "", aErr = "";
                bool aExited = await System.Threading.Tasks.Task.Run(() =>
                {
                    aOut = aProc.StandardOutput.ReadToEnd();
                    aErr = aProc.StandardError.ReadToEnd();
                    return aProc.WaitForExit(60000);
                }, iToken);

                if (!aExited) { try { aProc.Kill(); } catch { } return (false, aOut, "timeout(>60s) —— 一步不該跑這麼久"); }
                if (aProc.ExitCode != 0) return (false, aOut, $"exit={aProc.ExitCode}; {(aErr ?? "").Trim()}");
                return (true, aOut, "");
            }
            catch (Exception e)
            {
                return (false, "", $"spawn exception: {e.Message}");
            }
        }

        // ===========================================================
        // 區塊職責：把 `step_args` 這一行字切成 argv token。
        //
        // 規則兩條，分別對應引號的兩種身分：
        //   ① **引號在 token 開頭 ＝ 綁詞用**：吃到配對的收尾引號為止，**引號本身不進內容**。
        //   ② **引號在 token 中間 ＝ 內容**（JSON 語法）：原樣保留，
        //      但**它一樣會讓引號內的空白不切詞** —— 否則帶空白的 JSON 會被切成兩半。
        //
        // 物理意義：這兩條同時滿足三個原本互相打架的需求 ——
        //   - `--say "多個 詞"`              → 開頭引號 ⇒ 綁成一個 argument `多個 詞`
        //   - `[{"x":518,"y":1}]`            → 開頭是 `[` ⇒ 引號原樣送到
        //   - `[{"k":"值 含空白"}]`          → 開頭是 `[` ⇒ 引號原樣送到，**且空白不切詞**
        //   舊做法（單一 Arguments 字串）沒有「token 開頭」這個概念，所以無法區分前兩者。
        //
        // 🩸 第三個 case 是我第二次才補上的：第一版只寫了「開頭才算綁詞」，
        //   拿兩個**沒有空白**的 JSON 驗過就以為成立 —— 直到端到端實跑餵了一個
        //   `"值 含空白"` 才切成兩半（`unrecognized arguments: 含空白"}]`）。
        //   單元驗證只證明我想到的 case，端到端才會餵我沒想到的那個。
        //
        // 數值影響：純字串處理。未配對的引號 ⇒ 讀到行尾為止（**不丟例外**：
        //   少打一個引號時，讓工具自己抱怨參數，比在這裡炸掉整個 step 更好查）。
        //   空字串 / 全空白 ⇒ 回空清單（不產生一個空 token —— 那會變成多餘的 positional）。
        // ===========================================================
        internal static List<string> SplitStepArgs(string iRaw)
        {
            var aOut = new List<string>();
            if (string.IsNullOrWhiteSpace(iRaw)) return aOut;

            int i = 0;
            while (i < iRaw.Length)
            {
                while (i < iRaw.Length && char.IsWhiteSpace(iRaw[i])) i++;   // 跳過分隔空白
                if (i >= iRaw.Length) break;

                var aTok = new StringBuilder();
                if (iRaw[i] == '"')
                {
                    // ① 開頭引號 ＝ 綁詞：吃到配對的收尾引號（或行尾）為止，引號本身不進內容
                    i++;
                    while (i < iRaw.Length && iRaw[i] != '"') { aTok.Append(iRaw[i]); i++; }
                    if (i < iRaw.Length) i++;   // 收尾引號
                }
                else
                {
                    // ② 非引號開頭：中間的 `"` 是**內容**（原樣保留），但它同樣切換
                    //    「引號內」狀態 —— 引號內的空白不切詞，否則帶空白的 JSON 會斷成兩半。
                    bool aInQuote = false;
                    while (i < iRaw.Length && (aInQuote || !char.IsWhiteSpace(iRaw[i])))
                    {
                        if (iRaw[i] == '"') aInQuote = !aInQuote;
                        aTok.Append(iRaw[i]);
                        i++;
                    }
                }
                if (aTok.Length > 0) aOut.Add(aTok.ToString());
            }
            return aOut;
        }

        // ===========================================================
        // 區塊職責：op=done —— 收一件活動，把下一步指回換骰。
        // 物理意義：這一步存在的唯一理由是**接住流程** —— 活動做完那一刻是最容易斷線的位置
        //          （手上剛有產物、注意力在產物上，而換骰指令在上一份回傳檔裡）。
        // 數值影響：發一則收筆宣告（可選 body）；**不改 activities_done**（那在 pick 就記了 ——
        //          在這裡再加一次會讓同一件活動被算兩遍）。
        // ===========================================================
        static async UniTask OpDone(Dictionary<string, string> iArgs, string iPersona,
            UCL_FreeTimeSession iSession, string iBody, int iRemain,
            StringBuilder ioR, string iPath, CancellationToken iToken)
        {
            string aWhat = string.IsNullOrEmpty(iSession.activity) ? "（本場沒有經 op=pick 記錄的活動）" : iSession.activity;
            var aPost = new StringBuilder();
            aPost.AppendLine($"⏹ [{iPersona} 大小姐] 活動收筆：**{aWhat}**（剩 {iRemain} 分）");
            if (!string.IsNullOrEmpty(iBody))
            {
                aPost.AppendLine();
                aPost.AppendLine(iBody);
            }
            int aSeq = await Cmd_FreeTime.TavernPost(iArgs, iPersona, aPost.ToString(), "activity-done", iToken);

            ioR.AppendLine($"## 已收筆：{aWhat}");
            ioR.AppendLine($"- 收筆宣告: {(aSeq > 0 ? $"seq **{aSeq}**" : "未發（best-effort）")}");
            if (string.IsNullOrEmpty(iSession.activity))
            {
                // 「沒有記錄」與「有記錄」要長得不一樣 —— 前者代表流程被繞過，那是資訊不是錯誤。
                ioR.AppendLine("- ⚠ 本場沒有經 `op=pick` 選過活動 —— 這則收筆記在帳上，但沒有對應的開工紀錄。");
            }
            ioR.AppendLine();
            ioR.AppendLine("## ▶ 下一步（換骰 —— **順便讀未讀訊息、順便跟同事講話**）");
            ioR.AppendLine("```bash");
            ioR.AppendLine($"python <UCL_Core>/Tools~/AgentCommands/run_cmd.py --persona {iPersona} run FreeTime \\");
            ioR.AppendLine($"    --arg step=next --arg persona={iPersona} [--arg-file body=<想跟同事說的話>]");
            ioR.AppendLine("```");
            ioR.AppendLine("- 換骰的回傳檔**同一份**就含：未讀酒館訊息（會推已讀游標）＋ 新骰面 ＋ 剩餘時間。");
            ioR.AppendLine("- **截止是軟的**：時間到不打斷進行中的活動；到期時換骰那一步會自己宣布收工並結算。");
            Cmd_FreeTime.WritePayload(iArgs, iPath, ioR.ToString());
            Debug.Log($"[FreeTimeActivity] op=done {iPersona}（{aWhat}）→ {iPath}");
        }
    }
}
#endif
