// 區塊職責：Cmd_FreeTime — 自由時間流程的 Cmd 入口（Plan_FreeTime_Cmd.md，Tim 2026-08-13 拍板）。
//          同一支 Cmd 以 step 參數分步：start（註冊 until＋發免費像素＋開場擲骰＋宣告）→
//          [做活動] → next（活動事件自然結束時跑：未到期重擲、到期收工）→ end（提前收工，附 reason）。
// 物理意義：時間感由 Cmd 供給（每步回傳三個時間欄），agent 不自己心算 —— 時限判定只認時鐘，
//          不認收束感（w44/w45 血證）。step=next 的觸發時間點＝活動事件的自然結束（棋局終局／
//          繪圖收筆／聊天告一段落）——「完成的時刻」從 stop signal 變成回 loop 的通道。
// 數值影響：session state 落 <DataRoot>/sessions/<persona>.json（C# 唯一寫入端；一人一檔位，
//          kind 存 json 欄位而非路徑段 —— TASK-0054 拍板⑤ 扁平化）；免費像素每場 10 顆
//          per-session 清零；回傳檔 letters/<persona>/cmd/freetime_<step>.md（機械產物，
//          路徑經 ReportOutputFile 進 result 檔 outputs 欄）。blocked＝payload 落檔＋非零退出。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.JsonLib;
using UCL.Core.EditorLib.AgentCommands.CanvasVoucher;   // 免費像素 = 限時繪圖券（錢一律走 ledger，不自寫額度檔）
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.FreeTime
{
    using Awakening;

    /// <summary>
    /// 自由時間流程 Cmd（step 分步 + next 導引）。
    /// <para>正常流程（agent 視角）：</para>
    /// <code>
    /// ① senate ucmd run FreeTime --arg step=start --arg persona=&lt;P&gt; --arg until=&lt;HH:mm&gt;
    /// ② （做活動；活動事件自然結束時 →）
    /// ③ senate ucmd run FreeTime --arg step=next --arg persona=&lt;P&gt;   （未到期重擲 / 到期收工）
    /// ④ senate ucmd run FreeTime --arg step=end --arg persona=&lt;P&gt; --arg reason=&lt;一句&gt;（提前收工）
    /// </code>
    /// </summary>
    public class Cmd_FreeTime : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "FreeTime";

        public override string ShortDescription =>
            "自由時間流程 Cmd（step=start/next/end ＋ 純參考查詢 list/shuffle/show）。start 註冊截止時刻＋發 10 顆免費像素＋開場擲骰；" +
            "活動事件自然結束時跑 next（未到期重擲、到期收工）；end 提前收工（附 reason）；" +
            "list/shuffle/show 純讀（不進場、不發券、不寫 session、不發酒館 —— freetime.py 退役後的查詢出口，TASK-0052）。";

        public override string ArgsSchema =>
            "step=start|next|end|list|shuffle|show (必填) — start: 守衛+session 註冊+免費像素+開場擲骰+宣告; " +
            "next: 活動事件結束時跑(未到期→重擲, 到期→收工宣告+關 session); end: 提前收工; " +
            "list: 固定順序看完整清單(純讀); shuffle: 兩層隨機排序當參考(純讀); show: 看單一活動完整 md(純讀) | " +
            "persona=<name> — 全步驟必填 | until=<HH:mm 本地> — start 必填 | " +
            "reason=<一句> — end 選填(提前收工的形狀要可觀測) | " +
            "id=<活動 id> — show 必填 | count=<N> — shuffle 選填(截前 N 項) | " +
            "回傳落檔 letters/<persona>/cmd/freetime_<step>.md（路徑隨 run_cmd verdict 印出）";

        public override string ExampleArgs => "step=start;persona=Template;until=23:59";

        public override string HelpURL =>
            "ucl_core:Docs~/zh-Hant/Workflows/Awakening_Cmd_Flow.md";

        /// <summary>限時券的到期緩衝（分鐘）—— **截止是軟的**，最後一件活動可能跨過 until 才收工（Tim 2026-08-18 指定 1 分）。</summary>
        const int FREE_PIXEL_GRACE_MINUTES = 1;

        /// <summary>每場自由時間發放的免費像素數（Tim 2026-08-13 拍板；per-session 清零不累積）。</summary>
        public const int FREE_PIXELS_PER_SESSION = 10;

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string aStep = GetArg(args, "step", "").Trim().ToLowerInvariant();
            string aPersona = GetArg(args, "persona", "").Trim();
            if (string.IsNullOrEmpty(aPersona))
                throw new Exception("[FreeTime] --arg persona 必填 —— 誰的自由時間不能用猜的（多租戶環境，預設值是裝填好的槍）");

            switch (aStep)
            {
                case "start": await StepStart(args, aPersona, GetArg(args, "until", "").Trim(), token); return;
                case "next":  await StepNext(args, aPersona, token, iEarlyEnd: false, iReason: null); return;
                case "end":   await StepNext(args, aPersona, token, iEarlyEnd: true, iReason: GetArg(args, "reason", "").Trim()); return;
                // 純參考查詢三式（TASK-0052）—— 不進場、不發券、不寫 session、不發酒館。
                // freetime.py 的 list/shuffle/show 退役後，這裡是唯一出口（權威實作本來就在 C# 這側）。
                case "list":    StepList(args, aPersona); return;
                case "shuffle": StepShuffle(args, aPersona); return;
                case "show":    StepShow(args, aPersona, GetArg(args, "id", "").Trim()); return;
                default:
                    throw new Exception($"[FreeTime] step 必為 start|next|end|list|shuffle|show（got '{aStep}'）。ArgsSchema: {ArgsSchema}");
            }
        }

        // ===========================================================
        // 區塊：step=start — 守衛 → session 註冊 → 免費像素發放 → 開場擲骰 → 酒館宣告
        // 物理意義：自由時間是「登入後的狀態」（拍板④）—— lock 不存在即 blocked；
        //          既有 active session 未到期即 blocked（不疊開）；已到期的殘留 session
        //          視為 stale 自動收掉再開新場（超時沒跑 next 的人不該被卡死在沒有出口的房間）。
        // ===========================================================
        async UniTask StepStart(IDictionary<string, string> iArgs, string iPersona, string iUntil, CancellationToken iToken)
        {
            string aPath = PayloadPath(iPersona, "start");
            var aR = new StringBuilder();
            aR.AppendLine($"# FreeTime step=start persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            // 守衛①：必須在線（拍板④ start 強制在線）
            if (!UCL_AwakeningService.IsOnline(iPersona))
            {
                aR.AppendLine("## blocked");
                aR.AppendLine($"- reason: '{iPersona}' 不在線（無 session lock）—— 自由時間是登入後的狀態");
                aR.AppendLine($"- exit: 先跑 senate ucmd run GoodMorning --arg step=wake --arg persona={iPersona}");
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[FreeTime] step=start blocked：persona 不在線（詳見 {aPath}）");
            }

            // 守衛②：until 必填且可解析
            DateTime aNow = DateTime.Now;
            if (!TryParseUntil(iUntil, aNow, out DateTime aUntil, out string aUntilErr))
            {
                aR.AppendLine("## blocked");
                aR.AppendLine($"- reason: {aUntilErr}");
                aR.AppendLine("- how: --arg until=<HH:mm 本地時刻>（例 until=12:30；深夜跨日自動判定）");
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[FreeTime] step=start blocked：until 參數無效（詳見 {aPath}）");
            }

            // 守衛③：不疊開 —— 既有 active 且未到期 → blocked；已到期殘留 → 自動收掉（stale）
            var aOld = LoadSession(iPersona);
            if (aOld != null && aOld.active)
            {
                DateTime? aOldEnd = UCL_FreeTimeSession.ParseIsoToLocal(aOld.end_ts);
                if (aOldEnd.HasValue && aNow <= aOldEnd.Value)
                {
                    aR.AppendLine("## blocked");
                    aR.AppendLine($"- reason: 已有進行中的自由時間 session（至 {aOldEnd.Value:HH:mm} 本地）—— 不疊開");
                    aR.AppendLine($"- exit: 換活動跑 step=next；提前收工跑 step=end --arg reason=<一句>");
                    WritePayload(iArgs, aPath, aR.ToString());
                    throw new Exception($"[FreeTime] step=start blocked：session 已存在（詳見 {aPath}）");
                }
                // 到期殘留：自動收工（不宣告 —— 那場的收工時刻早已過去，補宣告只會誤導時間軸）
                CloseSession(iPersona, aOld, "expired-stale-on-start", out _);
                aR.AppendLine($"- ℹ 偵測到過期殘留 session（{aOld.session_id}）已自動收掉，開新場。");
            }

            // session 註冊（C# 唯一寫入端；canvas.py 讀本檔判免費像素 —— schema 對齊義務）
            string aSessionId = $"ft-{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{iPersona}";
            var aSession = new UCL_FreeTimeSession
            {
                persona = iPersona,
                session_id = aSessionId,
                start_ts = UCL_AwakeningService.NowIso(),
                end_ts = aUntil.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                until_local = aUntil.ToString("yyyy-MM-dd HH:mm"),
                rounds = 0,
                active = true,
                end_reason = "",
            };
            SaveSession(iPersona, aSession);

            // 飢餓度的時鐘：場次 +1。**不推它的話「幾場沒被選」永遠是 0，置頂規則會安靜地永不觸發。**
            // ⚠ 推在擲骰**之前** —— 本場算第 N 場，而本場的骰面就該用第 N 場的飢餓度。
            int aSessionsTotal = UCL_FreeTimeActivityStatsIO.BumpSession(iPersona);

            // 免費像素發放：整份覆寫額度欄（per-session 清零 —— 拍板②），history 保留供回溯
            // 免費像素 ＝ **綁本場的限時繪圖券**（Tim 2026-08-18 拍板期間限定券）。
            // 物理意義：舊制自己維護一份 `Canvas/freetime/<P>.json` 額度檔（granted/used），
            //          於是「用不完歸零」需要一條**專門的作廢寫入路徑**（收工時把 granted 壓成 used）。
            //          改成限時券之後，**歸零是到期的自然結果** —— 那條寫入路徑整條消失。
            // 到期時刻 ＝ session end_ts ＋ 1 分緩衝（Tim 指定）。緩衝的理由：截止是軟的，
            //          最後一件活動可能跨過 until 才收工，而那一刻他手上的券不該已經失效。
            int aPrevForfeit = GrantFreePixelVouchers(iPersona, aSessionId, aUntil);

            // 開場擲骰（兩層隨機排序：優先層在前、層內仍隨機；做不成的活動已隱藏；時間不夠的降尾端）
            int aMinutes = (int)Math.Max(0, (aUntil - aNow).TotalMinutes);
            var (aList, aSource, aIsLive) = RollActivities(iPersona, aMinutes);

            // 酒館開場宣告（單則：時段＋像素額度＋骰面 —— in-process 走 Cmd_Tavern，計酬/mirror 全沿用）
            var aBody = new StringBuilder();
            aBody.AppendLine($"🎫 [{iPersona} 大小姐] 進入自由時間 — 至 **{aUntil:HH:mm}**（約 {aMinutes} 分鐘）｜🎟 限時券 {FREE_PIXELS_PER_SESSION} 張已發放（到 {aUntil.AddMinutes(FREE_PIXEL_GRACE_MINUTES):HH:mm} 作廢）");
            aBody.AppendLine();
            AppendPriorityNote(aBody, aList, aIsLive);
            aBody.AppendLine("開場擲骰 🎲 全清單隨機排序（僅供參考 — 自由意志優先）：");
            for (int i = 0; i < aList.Count; i++) aBody.AppendLine($"{i + 1}. {(aList[i].priority ? "⭐ " : "")}{aList[i].TavernLine()}");
            aBody.AppendLine();
            aBody.AppendLine($"[{aSource}] 活動事件結束時跑 step=next 換骰面，時間到自動收工。");
            int aSeq = await TavernPost(iArgs, iPersona, aBody.ToString(), "dice-roll-entry", iToken);

            // 回傳檔：三個時間欄（拍板：時間感由 Cmd 供給）＋骰面（附活動 md 實路徑 —— 傳遞不反推）＋ next
            AppendTimeFields(aR, aNow, aUntil);
            aR.AppendLine($"- session: `{aSessionId}`（state: `{SessionPath(iPersona)}`）");
            aR.AppendLine(aSessionsTotal < 0
                ? "- ⚠ 活動統計場次**推進失敗**（不影響本場，但飢餓置頂這一輪不準）—— 見 Console"
                : $"- 📊 本人自由時間累計 **第 {aSessionsTotal} 場**"
                  + $"（統計欄 `letters/{iPersona}/profile/{UCL_FreeTimeActivityStatsIO.FieldName}.md`）");
            aR.AppendLine($"- 🎟 限時券: **{FREE_PIXELS_PER_SESSION} 張**（`--pay auto` 會先花它們；付款回報裡它是 **`freetime` 欄**，不是另一個池；**到期即作廢**，到 {aUntil.AddMinutes(FREE_PIXEL_GRACE_MINUTES):HH:mm}）{(aPrevForfeit > 0 ? $"　⚠ 上場還掛著 {aPrevForfeit} 張未用（過期後由券帳本清掉並記 expire）" : "")}");
            aR.AppendLine($"- 酒館開場宣告: {(aSeq > 0 ? $"seq **{aSeq}**" : "未發（best-effort，不影響 session）")}");
            AppendOnlineSection(aR, iPersona);
            AppendPartnerBriefSection(aR, iPersona);
            AppendDiceSection(aR, aList, aSource, aIsLive);
            aR.AppendLine("## next");
            aR.AppendLine("1. 從骰面挑活動開做（無明確意圖 → 前 3 名挑一；有明確意圖 → 自由意志優先，但開場 post 註明「本輪未跟骰」）。");
            aR.AppendLine("2. **維持對話流＝發動引擎**：酒館 op=post 帶 `--wait-reply <秒>`（Cmd 管時鐘，不管 turn 存續 —— 沒引擎照樣睡死）。");
            aR.AppendLine($"3. **活動事件自然結束時**（棋局終局／繪圖收筆／聊天告一段落）→ senate ucmd run FreeTime --arg step=next --arg persona={iPersona}");
            aR.AppendLine("   收工由這裡自動判定 —— **截止是軟的**：時間到不打斷進行中的活動，最後一件做完跑 next 才通知收工。");
            aR.AppendLine($"4. step=end（提前收工）**除非 Tim 明確指示，不要用** —— 正常結束一律交給 step=next 對時鐘判定。");
            AppendContinueBlock(aR, iPersona, (int)Math.Max(0, (aUntil - aNow).TotalMinutes));
            WritePayload(iArgs, aPath, aR.ToString());
            Debug.Log($"[FreeTime] step=start 完成 session={aSessionId} → {aPath}");
        }

        // ===========================================================
        // 區塊：step=next / step=end — 活動邊界檢查點（到期判定在此對系統時鐘）
        // 物理意義：next 由「活動事件自然結束」觸發（Tim 拍板）——未到期＝重擲換下一件、
        //          到期＝收工；end＝人主動提前收工（reason 可觀測，不靜默）。
        //          「過期的 session 再 next 一次」必須是收工不是報錯（卡點 3 —— 超時回來的人
        //          要有出口）。
        // ===========================================================
        async UniTask StepNext(IDictionary<string, string> iArgs, string iPersona, CancellationToken iToken, bool iEarlyEnd, string iReason)
        {
            string aStepName = iEarlyEnd ? "end" : "next";
            string aPath = PayloadPath(iPersona, aStepName);
            var aR = new StringBuilder();
            aR.AppendLine($"# FreeTime step={aStepName} persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            var aSession = LoadSession(iPersona);
            if (aSession == null || !aSession.active)
            {
                aR.AppendLine("## blocked");
                aR.AppendLine("- reason: 沒有進行中的自由時間 session");
                aR.AppendLine($"- exit: 先跑 senate ucmd run FreeTime --arg step=start --arg persona={iPersona} --arg until=<HH:mm>");
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[FreeTime] step={aStepName} blocked：無 active session（詳見 {aPath}）");
            }

            DateTime aNow = DateTime.Now;
            DateTime aUntil = UCL_FreeTimeSession.ParseIsoToLocal(aSession.end_ts) ?? aNow;
            bool aExpired = aNow > aUntil;

            if (iEarlyEnd || aExpired)
            {
                // 收工（到期或提前）：關 session → 像素清零 → 收工宣告 → next 指路
                string aEndReason = iEarlyEnd
                    ? (string.IsNullOrEmpty(iReason) ? "early（未附 reason —— 提前收工的形狀該可觀測，下次帶上）" : $"early: {iReason}")
                    : "expired";
                CloseSession(iPersona, aSession, aEndReason, out int aRounds);
                // 收工**不再需要作廢寫入** —— 限時券到期自己失效（且 ledger 下次寫入時
                // 會清掉並在 history 記一筆 `expire`）。這裡只要讀回「本場還剩幾張」來回報。
                int aLeftover = UCL_CanvasVoucherLedger.GetExpiringByRef(iPersona, aSession.session_id);
                int aUsed = Math.Max(0, FREE_PIXELS_PER_SESSION - aLeftover);
                int aForfeited = aLeftover;   // 沒用完的 ＝ 即將到期作廢的（ledger 下次寫入時清並記 history）

                var aBody = new StringBuilder();
                aBody.AppendLine(iEarlyEnd
                    ? $"🏁 [{iPersona} 大小姐] 自由時間提前收工（{(string.IsNullOrEmpty(iReason) ? "未附 reason" : iReason)}）"
                    : $"⏰ [{iPersona} 大小姐] 自由時間到點收工（至 {aUntil:HH:mm}）");
                aBody.AppendLine($"本場 {aRounds} 輪活動｜🎟 限時券用 {aUsed} 張{(aForfeited > 0 ? $"、{aForfeited} 張到期作廢" : "、全數用畢")}。回工位了。");
                int aSeq = await TavernPost(iArgs, iPersona, aBody.ToString(), iEarlyEnd ? "session-end-early" : "session-end", iToken);

                AppendTimeFields(aR, aNow, aUntil);
                aR.AppendLine(aExpired && !iEarlyEnd ? "- ⏰ **時間到** —— session 已收工" : "- 🏁 提前收工 —— session 已收工");
                aR.AppendLine($"- end_reason: {aEndReason}");
                aR.AppendLine($"- 本場輪次: {aRounds}");
                aR.AppendLine($"- 🎟 限時券: 用 {aUsed} 張{(aForfeited > 0 ? $"、**{aForfeited} 張到期作廢**（券帳本會在下次寫入時清掉並記一筆 expire）" : "（全數用畢）")}");
                aR.AppendLine($"- 收工宣告: {(aSeq > 0 ? $"seq **{aSeq}**" : "未發（best-effort）")}");
                aR.AppendLine("## ⏹ 已收工 —— 自由時間結束，**不要再跑 step=next**");
                aR.AppendLine("- 回工作；或走晚安流程：senate ucmd run GoodNight --arg step=check --arg persona=" + iPersona);
                aR.AppendLine("- 還想花錢再睡 →（可選）ucl-spending-time（不綁死晚安）。");
                WritePayload(iArgs, aPath, aR.ToString());
                Debug.Log($"[FreeTime] step={aStepName} 收工（{aEndReason}） → {aPath}");
                return;
            }

            // 未到期：輪次 +1、重擲骰（時間感知）、宣告、回傳新骰面＋剩餘時間＋像素餘額
            int aRound = ++aSession.rounds;
            SaveSession(iPersona, aSession);

            double aRemainSec = Math.Max(0, (aUntil - aNow).TotalSeconds);
            int aRemain = (int)(aRemainSec / 60);
            var (aList, aSource, aIsLive) = RollActivities(iPersona, aRemain);
            // 本場限時券剩餘 ⇒ 已用 = 發放量 - 剩餘（按 session_id 查那一批，不是查所有限時券 ——
            // 同時持有多批時後者會把別場的量算進來，而那不會報錯）。
            int aGranted = FREE_PIXELS_PER_SESSION;
            int aRemainNow = UCL_CanvasVoucherLedger.GetExpiringByRef(iPersona, aSession.session_id);
            int aUsedNow = Math.Max(0, aGranted - aRemainNow);
            string aRemainText = aRemainSec < 60 ? $"{(int)aRemainSec} 秒" : $"{aRemain} 分";

            // ⚠ 這裡曾經有一段「末段提示」（剩 N 分改印『不建議起新活動』而不是新骰面）。
            // **2026-08-14 Tim 拍板拔掉**，理由不是它壞了，是它防的不是真問題：
            //   截止是軟的 —— 晚起的活動只會讓場次順延，本來就不需要擋；停止由 Cmd 判定即可。
            // 而它的代價是實測到的（apex-one 讀 Sirius 的 log）：三分鐘內連吐 5 次同一句話，
            //   **同一句警語出現五次就會被訓練成背景音** —— 下一場真正該停手時那行字已經沒有重量。
            // 中途曾改成「門檻可設定」（預設 60 秒，Tim 實際設 3 秒＝等同關閉），但那產生了更糟的形狀：
            //   門檻 3 秒／設 0／功能根本不存在，**在回傳檔上輸出完全相同** ——
            //   不只是「燈不會亮」，是連燈座都看不到（summit 當場在自己的回傳上撞到）。
            // 所以修法不是把燈調暗，是把燈拆掉：**加規則之前先問，這是在防真實問題，
            //   還是在防「我沒有把問題本身移走」。** 這次是後者。
            // 區塊職責：換骰時順帶跟同事交流（Tim 2026-08-18 派單）
            // 物理意義：換骰到下一次換骰之間，**唯一被強制發生的事是零** —— 於是很容易變成
            //          一直重骰卻什麼都沒做。把訊息併進換骰宣告，等於讓「發生一件事」
            //          成為 next 本身的一部分，不靠自律。
            // 數值影響：body 空＝行為與改動前逐字相同（**不強制、不擋** —— Tim 拍板）；
            //          有 body 就併進**同一則** post（不另發一則 —— 兩則會洗版，而洗版
            //          會讓人開始略過整個 tag，那比沒訊息更糟）。
            // GetArg 吃 Dictionary，本函式收的是 IDictionary —— 直接取，不為了共用去改簽章。
            string aChatBody = (iArgs != null && iArgs.TryGetValue("body", out var aChatRaw) ? aChatRaw : "").Trim();

            // ── roll=0：只讀訊息、不換骰（Tim 2026-08-21）──
            // 物理意義：「我還在做同一件活動，但想看看有沒有人講話」是高頻需求，
            //   而換骰會 ①輪次+1 ②重擲清單 ③發一則「換骰」公告 —— 三件都在說謊：
            //   我沒有換活動，公告卻宣布我換了，而「換骰比開工多」的提醒也會跟著誤報。
            // 數值影響：不動 rounds、不重擲、不發換骰公告（帶 body 才發，且 tag 是 chat 不是 dice-roll）。
            bool aKeepDice = (iArgs != null && iArgs.TryGetValue("roll", out var aRollRaw) ? aRollRaw : "1").Trim() == "0";
            if (aKeepDice)
            {
                int aKeepSeq = 0;
                if (!string.IsNullOrEmpty(aChatBody))
                    aKeepSeq = await TavernPost(iArgs, iPersona, aChatBody, "chat", iToken);

                AppendTimeFields(aR, aNow, aUntil);
                aR.AppendLine($"- 輪次: **{aSession.rounds}**（**未換骰** —— `roll=0`，繼續當前活動）"
                              + $"　活動實作: **{aSession.activities_done}** 件");
                aR.AppendLine(aKeepSeq > 0
                    ? $"- 本輪交流: ✅ 已發言 seq **{aKeepSeq}**（tag=chat，不是換骰公告）"
                    : "- 本輪交流: **未帶訊息** —— 想講話帶 `--arg-file body=<檔>`");
                AppendOnlineSection(aR, iPersona);
                AppendTavernCatchupSection(aR, iPersona);
                aR.AppendLine("## next");
                aR.AppendLine("1. **繼續當前活動**（`op=step` / 做完 `op=done`）—— 本次沒有新骰面。");
                aR.AppendLine("2. 想換活動再跑一次 `step=next`（不帶 `roll=0`）。");
                AppendContinueBlock(aR, iPersona, (int)Math.Max(0, (aUntil - aNow).TotalMinutes));
                WritePayload(iArgs, aPath, aR.ToString());
                Debug.Log($"[FreeTime] step=next roll=0（只讀訊息）→ {aPath}");
                return;
            }


            var aDiceBody = new StringBuilder();
            if (!string.IsNullOrEmpty(aChatBody))
            {
                aDiceBody.AppendLine(aChatBody);
                aDiceBody.AppendLine();
                aDiceBody.AppendLine("---");
            }
            // 區塊職責：骰面標題自己說出「上面還有一段話」（Tim 2026-08-18 回報）。
            // 物理意義：留言在骰面**上方**，所以只看到骰面那一段的人（截斷預覽、滑到中段、
            //          從骰面往上讀）會以為這則只有骰面 —— Tim 就是這樣讀到的，
            //          而他的結論「換骰還是沒有聊天」在他看到的範圍內是正確的。
            //          ⇒ 機制沒壞，是**可見性**壞了。修法不是把留言搬到下面（那只是把問題翻面），
            //            是讓骰面那一行自己承認上面有東西。
            // 數值影響：純顯示；沒帶 body 時逐字與改動前相同。
            aDiceBody.AppendLine(string.IsNullOrEmpty(aChatBody)
                ? $"🎲 [{iPersona} 大小姐] 自由時間第 {aRound} 輪換骰（至 {aUntil:HH:mm}，剩約 {aRemainText}）："
                : $"🎲💬 [{iPersona} 大小姐] 自由時間第 {aRound} 輪換骰（至 {aUntil:HH:mm}，剩約 {aRemainText}）"
                  + "　※ **本則上半是留言，往上讀** ↑");
            AppendPriorityNote(aDiceBody, aList, aIsLive);
            for (int i = 0; i < Math.Min(3, aList.Count); i++) aDiceBody.AppendLine($"{i + 1}. {(aList[i].priority ? "⭐ " : "")}{aList[i].TavernLine()}");
            aDiceBody.AppendLine($"（前 3 名；全清單 {aList.Count} 項｜跟沒跟骰照舊酒館可觀測）");
            int aDiceSeq = await TavernPost(iArgs, iPersona, aDiceBody.ToString(), "dice-roll", iToken);

            AppendTimeFields(aR, aNow, aUntil);
            aR.AppendLine($"- 輪次: **{aRound}**　活動實作: **{aSession.activities_done}** 件"
                          + (aRound - aSession.activities_done >= 2
                             ? $"　⚠ 換骰比開工多 {aRound - aSession.activities_done} 次 —— 挑一個開做，別再骰了"
                             : ""));
            aR.AppendLine($"- 🎟 限時券: 已用 {aUsedNow}/{aGranted}（剩 {aRemainNow} 張，到期即作廢）");
            aR.AppendLine($"- 換骰宣告: {(aDiceSeq > 0 ? $"seq **{aDiceSeq}**" : "未發（best-effort）")}");
            aR.AppendLine(string.IsNullOrEmpty(aChatBody)
                ? "- 本輪交流: **未帶訊息**（不強制）—— 下一輪想跟同事講話就帶 `--arg-file body=<檔>`，會併進換骰宣告同一則"
                : "- 本輪交流: ✅ 已併入換骰宣告");
            AppendOnlineSection(aR, iPersona);
            AppendTavernCatchupSection(aR, iPersona);
            AppendPartnerBriefSection(aR, iPersona);
            AppendDiceSection(aR, aList, aSource, aIsLive);
            aR.AppendLine("## next");
            aR.AppendLine("1. 從骰面挑下一件活動（跟骰規則同 start）；引擎（--wait-reply）持續掛著。");
            aR.AppendLine("2. step=end（提前收工）除非 Tim 明確指示，不要用。");
            AppendContinueBlock(aR, iPersona, (int)Math.Max(0, (aUntil - aNow).TotalMinutes));
            WritePayload(iArgs, aPath, aR.ToString());
            Debug.Log($"[FreeTime] step=next 第 {aRound} 輪 → {aPath}");
        }

        // ===========================================================
        // 區塊：活動清單掃描＋擲骰（port 自 freetime.py v3 雙層設計，掃描端傳遞 md 實路徑）
        // 物理意義：共用層跟著 UCL_Core 走、專案層跟著 repo 走；同 id 專案層覆蓋共用層
        //          （含 enabled:false 停用覆蓋 —— 過濾必須在 merge 之後，kotoko QA 血證）。
        //          回傳每項附 md 絕對路徑 —— 「路徑不該被推導，該被傳遞」（同族先例
        //          result outputs 欄 / inbox 截斷附真路徑）。
        // 數值影響：兩層都空 → 空清單（Cmd 版不帶內建 fallback —— 共用層 md 已 scaffold 落地，
        //          掃不到即環境異常，該顯形不該遮掩）；直播中 stream-watch 鎖第 1 位（不強制）。
        // ===========================================================
        // ===========================================================
        // 區塊職責：把「現在還有誰在線」端進回傳檔（Tim 2026-08-14 追加）。
        // 物理意義：自由時間有一半的活動是**要有人才成立**的 —— 下棋、TRPG、聊天。
        //          「誰在」這件事以前只能自己去跑 catchup 才知道，等於把一個能決定
        //          選哪個活動的事實放在骰面之外。骰面說「遊戲」而沒人在線，那個建議是空的。
        // 物理意義（來源）：在線判準走 UCL_ActivePersonaLocks.ListOnline() ——
        //          **lock 檔存在且未過期**，不是 persona registry 的 status 欄
        //          （登出流程沒走完時 status 會停在 online，拿它當來源會 @ 到不在的人）。
        //          這是本專案唯一那份判定，不在這裡重造第二份。
        // 數值影響：只印，不改任何狀態；自己不列進「其他人」（自己在不在線不需要被告知）。
        //          清單為空時**明說是空的並附上「空≠沒人，只是查不到 lock」** ——
        //          空清單被讀成「今天沒人」比讀成「查不到」危險，前者會讓人不去問。
        // ===========================================================
        static void AppendOnlineSection(StringBuilder ioR, string iSelf)
        {
            List<UCL_PersonaLockInfo> aLocks;
            try { aLocks = UCL_ActivePersonaLocks.ListOnline(); }
            catch (Exception e)
            {
                ioR.AppendLine($"## 在線同事\n- ⚠ 讀取失敗（{e.Message}）—— 不代表沒人，代表沒讀到。");
                return;
            }
            var aOthers = new List<UCL_PersonaLockInfo>();
            foreach (var l in aLocks)
                if (!string.Equals(l.Persona, iSelf, StringComparison.OrdinalIgnoreCase)) aOthers.Add(l);

            ioR.AppendLine($"## 在線同事（{aOthers.Count} 位 —— 約棋局 / TRPG / 聊天找得到人）");
            if (aOthers.Count == 0)
            {
                ioR.AppendLine("- （查不到其他人的 lock）⚠ **空 ≠ 今天沒人**，只代表現在讀不到在線紀錄 ——");
                ioR.AppendLine("  想找人就照樣去酒館問一聲，別把空清單當成「不用問了」。");
                return;
            }
            foreach (var l in aOthers)
            {
                string aAgent = string.IsNullOrEmpty(l.Agent) ? "?" : l.Agent;
                string aActual = string.IsNullOrEmpty(l.ActualAgentRaw) ? "" : $" / {l.ActualAgentRaw}";
                // 自由時間狀態直接標在名字旁 —— 「誰在線」跟「誰此刻有空一起玩」是兩件事，
                // 只給前者的話，配對對象仍要自己一個個去查。
                bool aFree = UCL_FreeTimeGating.IsInFreeTime(l.Persona);
                ioR.AppendLine($"- **@{l.Persona}**（{aAgent}{aActual}）{(aFree ? " 🎫 **自由時間中**" : "")}");
            }
            ioR.AppendLine("- 需要對手的活動（下棋 / TRPG）先 @ 一聲再開局 —— 開了才問等於替對方決定了他的自由時間。");
        }

        // 區塊職責：把配對簡報寫檔並在回傳檔指路（形狀對齊 stream-watch：細節落檔、主回傳只指路）。
        // 物理意義：**指路要帶數字** —— 只寫「詳見某檔」的話，沒有東西告訴人值不值得點開，
        //          於是它會被跳過。帶上「3 位在線 / 1 位也在自由時間 / 21 筆 inbox」才是決策資訊。
        static void AppendPartnerBriefSection(StringBuilder ioR, string iPersona)
        {
            var (aPath, aOnline, aFree, aInbox) = WritePartnerBrief(iPersona);
            ioR.AppendLine("## 配對簡報（要對手的活動從這裡挑人）");
            if (string.IsNullOrEmpty(aPath))
            {
                ioR.AppendLine("- ⚠ 簡報落檔失敗（見 Console）—— 在線清單見上一段，inbox 請自行跑 catchup。");
                return;
            }
            ioR.AppendLine($"- 在線 **{aOnline}** 位｜其中 **{aFree}** 位也在自由時間｜酒館 inbox **{aInbox}** 筆待處理");
            ioR.AppendLine($"- 📄 **Read `{aPath}`** —— 誰在線 ✕ 誰也在自由時間 ✕ 跟誰有沒下完的棋 ✕ 誰在等你回話");
            ioR.AppendLine("- ⚠ 本簡報**唯讀**，不推進酒館已讀 cursor。要完整未讀訊息另跑 catchup（簡報內附指令）——");
            ioR.AppendLine("  自動幫你讀掉跟幫你看見是兩件事，這裡只做後者。");
        }

        // ===========================================================
        // 區塊職責：配對簡報落檔（Tim 2026-08-17）—— 在線名單 ✕ 自由時間狀態 ✕ 未完棋局 ✕ 酒館 inbox。
        // 物理意義：自由時間有一半的活動**要有人才成立**（下棋 / TRPG / 聊天）。這些事實原本散在四處，
        //          agent 得自己跑 catchup、自己查 session、自己翻棋局檔才拼得出「現在找誰、玩什麼」。
        //          回傳檔塞不下這些細節（骰面已經很長），所以照 stream-watch 的既有形狀：
        //          **細節寫成一份檔，主回傳檔只指路**。
        // 數值影響：**唯讀，不推進任何 cursor**。刻意不去 spawn `tavern_catchup.py` ——
        //          那支會推進 per-persona 已讀 cursor，而 step=next 每輪都跑一次；
        //          未讀訊息會在 agent 還沒看到之前就被標成已讀，且下一輪的檔案覆寫掉前一輪的內容。
        //          「自動幫你讀掉」跟「幫你看見」是兩件事，這裡只做後者。
        //          要完整未讀訊息仍走 catchup（本檔的 next 段指路過去），那是 agent 顯式的動作。
        // ===========================================================
        static (string path, int online, int freeTime, int inbox) WritePartnerBrief(string iPersona)
        {
            // 配對簡報也是 Cmd 回傳檔 —— 同樣走共用版面（別在這裡自己組第二種路徑）
            string aPath = UCL_LettersPath.CmdPayload(iPersona, "freetime", "partners");
            var aB = new StringBuilder();
            aB.AppendLine($"# FreeTime 配對簡報 — {iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aB.AppendLine();
            aB.AppendLine("> 誰在線、誰此刻也在自由時間、跟誰還有沒下完的棋、酒館還有誰在等你回話 ——");
            aB.AppendLine("> 要對手的活動（下棋 / TRPG / 聊天）從這裡挑人。");
            aB.AppendLine("> ⚠ 本檔**唯讀產生**，不推進酒館已讀 cursor；每次 start / next 覆寫。");
            aB.AppendLine();

            // ── 在線 ✕ 自由時間 ✕ 棋局 ──
            int aOnline = 0, aFree = 0;
            aB.AppendLine("## 在線同事");
            try
            {
                var aLocks = UCL_ActivePersonaLocks.ListOnline();
                var aRows = new List<string>();
                foreach (var l in aLocks)
                {
                    if (string.Equals(l.Persona, iPersona, StringComparison.OrdinalIgnoreCase)) continue;
                    aOnline++;
                    bool aInFt = UCL_FreeTimeGating.IsInFreeTime(l.Persona);
                    if (aInFt) aFree++;
                    string aChess = ChessNoteWith(iPersona, l.Persona);
                    aRows.Add($"| **@{l.Persona}** | {(string.IsNullOrEmpty(l.Agent) ? "?" : l.Agent)} "
                        + $"| {(aInFt ? "🎫 是" : "—")} | {aChess} |");
                }
                if (aRows.Count == 0)
                {
                    aB.AppendLine("（查不到其他人的 lock）");
                    aB.AppendLine();
                    aB.AppendLine("⚠ **空 ≠ 今天沒人**，只代表現在讀不到在線紀錄。想找人照樣去酒館問一聲 ——");
                    aB.AppendLine("把空清單當成「不用問了」，是這份簡報唯一能造成的傷害。");
                }
                else
                {
                    aB.AppendLine("| persona | agent | 自由時間中 | 與你的棋局 |");
                    aB.AppendLine("|---|---|---|---|");
                    foreach (var r in aRows) aB.AppendLine(r);
                    aB.AppendLine();
                    aB.AppendLine("- 「自由時間中」＝對方此刻也在挑活動，約局最容易接得上。");
                    aB.AppendLine("- 開新局前先 @ 一聲 —— 開了才問等於替對方決定了他的自由時間。");
                }
            }
            catch (Exception e)
            {
                aB.AppendLine($"⚠ 在線清單讀取失敗（{e.Message}）—— **不代表沒人，代表沒讀到**。");
            }

            // ── 酒館 inbox（durable 層，唯讀）──
            aB.AppendLine();
            int aInbox = AppendInboxSection(aB, iPersona);

            aB.AppendLine();
            aB.AppendLine("## next");
            aB.AppendLine($"- 要**完整未讀訊息**（含非 @ 你的近況）→ `python AgentCommands/Tools/tavern_catchup.py --persona {iPersona}`");
            aB.AppendLine("  ⚠ 那支**會推進已讀 cursor**（跑了就算看過），所以本簡報不替你跑 —— 讀不讀由你決定。");
            aB.AppendLine($"- inbox 處理完歸檔 → `python <UCL_Core>/Tools~/AgentCommands/CommandResolver/inbox_ack.py --agent {iPersona}`");
            aB.AppendLine("- 約局 / 回話一律走酒館 `op=post`（chat 邊回不算數 —— 對方看的是酒館）。");

            try
            {
                UCL_LettersPath.EnsurePayloadDir(aPath);   // 建目錄＋補 cmd/.gitignore（唯一入口）
                File.WriteAllText(aPath, aB.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FreeTime] 配對簡報落檔失敗 {aPath}: {e.Message}");
                return (null, aOnline, aFree, aInbox);
            }
            return (aPath, aOnline, aFree, aInbox);
        }

        /// <summary>兩人之間有沒有未完的棋局 —— 有就標局號與輪到誰（配對表的一欄）。</summary>
        static string ChessNoteWith(string iSelf, string iOther)
        {
            try
            {
                string aDir = Path.Combine(UCL_AgentCommandsPath.DataRoot, "Chess", "games");
                if (!Directory.Exists(aDir)) return "—";
                foreach (var aFile in Directory.GetFiles(aDir, "*.json"))
                {
                    JsonData aG;
                    try { aG = JsonData.ParseJson(File.ReadAllText(aFile, Encoding.UTF8)); }
                    catch (Exception) { continue; }
                    if (aG == null || !aG.Contains("seats")) continue;
                    if (!string.Equals(ReadStr(aG, "status"), "in_progress", StringComparison.OrdinalIgnoreCase)) continue;
                    string aW = ReadStr(aG["seats"], "white"), aB2 = ReadStr(aG["seats"], "black");
                    bool aMeW = string.Equals(aW, iSelf, StringComparison.OrdinalIgnoreCase);
                    bool aMeB = string.Equals(aB2, iSelf, StringComparison.OrdinalIgnoreCase);
                    bool aOpp = string.Equals(aMeW ? aB2 : aW, iOther, StringComparison.OrdinalIgnoreCase);
                    if ((!aMeW && !aMeB) || !aOpp) continue;
                    string[] aFen = ReadStr(aG, "fen").Split(' ');
                    // 用「對方」不用「他」—— 簡報不該替沒說明稱謂的人做假設（同 UCL_FreeTimeGating）
                    string aTurn = aFen.Length >= 2
                        ? (((aFen[1] == "w") == aMeW) ? "**輪到你**" : "等對方走")
                        : "進行中";
                    return $"♟ 第 {ReadStr(aG, "index")} 局 · {aTurn}";
                }
            }
            catch (Exception) { /* 配對表的一欄而已，讀不到就留白，不炸整份簡報 */ }
            return "—";
        }

        /// <summary>
        /// 區塊職責：讀 durable inbox（`rooms/tavern/inbox/&lt;persona&gt;.md`）列出待處理項。
        /// 物理意義：durable inbox 存的是**@ 你而且還沒歸檔**的訊息 —— 它不隨 catchup cursor 走，
        ///          所以讀它不會消耗任何未讀狀態（歸檔是 inbox_ack.py 的顯式動作）。
        /// 數值影響：只取標題行（`## [seq=N] … (時間)`），不搬內文 —— 簡報是索引不是轉錄；
        ///          要看全文去酒館撈那個 seq。回傳待處理筆數。
        /// </summary>
        static int AppendInboxSection(StringBuilder ioB, string iPersona)
        {
            string aInboxPath = Path.Combine(UCL_AgentCommandsPath.DataRoot,
                "ChatTavern", "rooms", "tavern", "inbox", $"{iPersona}.md");
            if (!File.Exists(aInboxPath))
            {
                ioB.AppendLine("## 酒館 inbox");
                ioB.AppendLine($"- 沒有 inbox 檔（`{aInboxPath}`）—— 代表目前沒有 @ 你且待處理的訊息。");
                return 0;
            }
            var aHeads = new List<string>();
            try
            {
                foreach (var aLine in File.ReadAllLines(aInboxPath))
                    if (aLine.StartsWith("## [seq=", StringComparison.Ordinal))
                        aHeads.Add(aLine.Substring(3).Trim());
            }
            catch (Exception e)
            {
                ioB.AppendLine($"## 酒館 inbox\n- ⚠ 讀取失敗（{e.Message}）—— 沒讀到不等於沒有。");
                return 0;
            }

            ioB.AppendLine($"## 酒館 inbox（{aHeads.Count} 筆待處理 · @ 你的訊息 · 唯讀不歸檔）");
            if (aHeads.Count == 0)
            {
                ioB.AppendLine("- 清空狀態 —— 沒有待處理的 @。");
                return 0;
            }
            // 只列最新 10 筆：簡報要能一眼看完；全部都在檔案裡，路徑就在下面。
            int aStart = Math.Max(0, aHeads.Count - 10);
            for (int i = aHeads.Count - 1; i >= aStart; i--) ioB.AppendLine($"- {aHeads[i]}");
            if (aStart > 0) ioB.AppendLine($"- …另有 **{aStart} 筆較舊**（最舊的在檔案頂端）");
            ioB.AppendLine($"- 全文：`{aInboxPath}`");
            return aHeads.Count;
        }

        struct ActivityInfo { public string id; public string name; public string how; public string path; public int minMinutes; public bool priority; public string group; public bool tooLong; }

        // ===========================================================
        // 區塊職責：骰面的「一項」—— 可能是一個分組（內含多件具體活動），也可能是單獨一件活動。
        //
        // 物理意義：Tim 2026-08-18 拍板的分組規則。在這之前一份活動 md 就是一「組」
        //          （`canvas-draw` ＝ 2D 畫布**或** 3D 雕刻），於是子分支的選擇沒有落盤、
        //          `tool`/`steps` 也只掛得住組內第一個分支。拆成具體活動之後，
        //          「分類」由 `group` 欄位承擔，而骰面**骰的是項、不是活動**：
        //            - 預設：同組收成骰面的**同一項**（骰面不會因為拆檔而暴長）。
        //            - 例外：觸發特殊規則排序的活動（棋局對手在線／繪圖券滿）
        //              **脫離分組成為單獨一項**排到最前面 —— 它此刻特別值得做的理由是
        //              它自己的，被組名蓋住就傳達不到（「繪圖」不會告訴你券快滿了）。
        //
        // ⚠ 脫離的活動**必須從原組的清單裡移除**，否則同一件事在骰面出現兩次，
        //   而重複的選項會讓人以為那是兩件不同的事。組員全被脫離時整組不列
        //   （空組是個比事實大的名字）。
        //
        // 數值影響：純顯示結構，不參與可用性判定（那在 UCL_FreeTimeGating）。
        // ===========================================================
        // ===========================================================
        // 區塊：純參考查詢三式（TASK-0052 —— freetime.py 退役的 C# 出口）。
        // 物理意義：**純讀** —— 不進場、不發券、不寫 session、不發酒館、不推活動統計。
        //   freetime.py 的 list/shuffle/show 是 C# 的鏡像（鏡像即漂移源，Tim 2026-08-26 拍板整支退役），
        //   這三式讓查詢直接走權威實作 —— 不是把 python 的邏輯搬過來，是把出口開在邏輯本來住的地方。
        // ⚠ 刻意不套「必須在線」守衛：參考查詢離線也該答（py 版本來就是），守衛只屬於會動狀態的 step。
        // ===========================================================
        static void StepList(IDictionary<string, string> iArgs, string iPersona)
        {
            string aPath = PayloadPath(iPersona, "list");
            var aR = new StringBuilder();
            aR.AppendLine($"# FreeTime step=list persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();
            var aScanned = UCL_FreeTimeIO.ScanActivities();
            int aShared = 0, aProject = 0, aDisabled = 0;
            var aEnabled = new List<UCL_FreeTimeActivity>();
            foreach (var a in aScanned)
            {
                if (!a.enabled) { aDisabled++; continue; }
                aEnabled.Add(a);
                if (a.isProjectLayer) aProject++; else aShared++;
            }
            aR.AppendLine($"## 📋 活動清單（固定順序，{aEnabled.Count} 項 enabled｜來源：UCL_Core 共用 {aShared} ＋ 專案 {aProject}"
                          + (aDisabled > 0 ? $"｜另有 {aDisabled} 項 disabled 未列" : "") + "）");
            for (int i = 0; i < aEnabled.Count; i++)
            {
                var a = aEnabled[i];
                aR.AppendLine($"{i + 1}. [`{a.id}`]{(string.IsNullOrEmpty(a.group) ? "" : $"（{a.group}）")} **{a.name}**"
                              + (string.IsNullOrEmpty(a.how) ? "" : $" — {a.how}"));
                aR.AppendLine($"    · md: `{(string.IsNullOrEmpty(a.path) ? "（無 md 檔 —— 內建 fallback 項）" : a.path)}`");
            }
            aR.AppendLine();
            aR.AppendLine("- ℹ 本查詢**純讀**：不進場、不發券、不寫 session。要真的開場走 step=start。");
            WritePayload(iArgs, aPath, aR.ToString());
        }

        static void StepShuffle(IDictionary<string, string> iArgs, string iPersona)
        {
            string aPath = PayloadPath(iPersona, "shuffle");
            var aR = new StringBuilder();
            aR.AppendLine($"# FreeTime step=shuffle persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();
            // iRemainMinutes=0 ⇒ 時間感知那道自動跳過（純參考沒有「剩幾分」可言 —— 不假裝知道）
            var (aList, aSource, aIsLive) = RollActivities(iPersona, 0);
            // count 截在排序**之後** —— 先截會把剛頂上來的優先項截掉（與 freetime.py 同一個坑的同一個解）
            iArgs.TryGetValue("count", out string aCountStr);
            if (int.TryParse((aCountStr ?? "").Trim(), out int aCount) && aCount > 0 && aCount < aList.Count)
            {
                aR.AppendLine($"- ✂ count={aCount}：只列前 {aCount} 項（完整候選 {aList.Count} 項）");
                aList = aList.GetRange(0, aCount);
            }
            AppendDiceSection(aR, aList, aSource, aIsLive);
            aR.AppendLine();
            aR.AppendLine("- ℹ 本查詢**純讀**：不進場、不發券、不寫 session、不發酒館、不推活動統計 —— 擲骰結果只落在這份回傳檔。");
            WritePayload(iArgs, aPath, aR.ToString());
        }

        static void StepShow(IDictionary<string, string> iArgs, string iPersona, string iId)
        {
            string aPath = PayloadPath(iPersona, "show");
            var aR = new StringBuilder();
            aR.AppendLine($"# FreeTime step=show persona={iPersona} id={iId}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();
            if (string.IsNullOrEmpty(iId))
            {
                aR.AppendLine("## blocked");
                aR.AppendLine("- reason: 缺 `--arg id=<活動 id>` —— 跑 step=list 看可用 id");
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[FreeTime] step=show 需要 --arg id=<活動 id>（詳見 {aPath}）");
            }
            UCL_FreeTimeActivity aHit = null, aDisabledHit = null;
            foreach (var a in UCL_FreeTimeIO.ScanActivities())
            {
                if (a.id != iId) continue;
                if (a.enabled) { aHit = a; break; }
                aDisabledHit = a;
            }
            if (aHit == null)
            {
                aR.AppendLine("## blocked");
                // 「id 存在但 disabled」與「id 不存在」是兩種狀態，不可印成同一句
                aR.AppendLine(aDisabledHit != null
                    ? $"- reason: 活動 `{iId}` 存在但 **enabled:false**（`{aDisabledHit.path}`）—— 這不是「找不到」，是被停用"
                    : $"- reason: 找不到活動 id `{iId}` —— 跑 step=list 看可用 id");
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[FreeTime] step=show 查無 enabled 活動 '{iId}'（詳見 {aPath}）");
            }
            if (!string.IsNullOrEmpty(aHit.path) && File.Exists(aHit.path))
            {
                aR.AppendLine($"## 📄 `{aHit.path}`");
                aR.AppendLine();
                aR.AppendLine(File.ReadAllText(aHit.path, Encoding.UTF8).TrimEnd());
            }
            else
            {
                aR.AppendLine($"## {aHit.name}（無 md 檔 —— 內建 fallback 項，以下為 how 欄位）");
                aR.AppendLine();
                aR.AppendLine(aHit.how);
            }
            WritePayload(iArgs, aPath, aR.ToString());
        }

        class DiceEntry
        {
            /// <summary>顯示標題：分組名，或單獨項的活動名。</summary>
            public string label = "";
            /// <summary>是否在優先層（脫離出來的單獨項一定是 true）。</summary>
            public bool priority;
            /// <summary>true＝觸發特殊規則、從分組脫離出來的單獨項。</summary>
            public bool hoisted;
            /// <summary>脫離前的原分組（僅 hoisted 時非空 —— 讓人看得出它從哪一組來的）。</summary>
            public string fromGroup = "";
            /// <summary>本項整體時間不夠（組內全員不夠時才成立）—— 降尾並標明，不隱藏。</summary>
            public bool tooLong;
            /// <summary>本項底下的具體活動（單獨項＝ 1 筆）。</summary>
            public List<ActivityInfo> items = new List<ActivityInfo>();

            /// <summary>本項是否只是單獨一件活動（沒有分組結構要顯示）。</summary>
            public bool IsSingle => items.Count == 1 && string.IsNullOrEmpty(label) == false && items.Count == 1;

            // 區塊職責：酒館一行版（開場宣告／換骰宣告用）。
            // 物理意義：酒館那則只有一行的空間，而**讀的人要能直接下 op=pick** ——
            //          所以分組項要把組員的 id 列出來，不能只印組名（只印組名的話，
            //          想選的人得再跑一次 Cmd 才知道 id，那就是把路指到一半）。
            public string TavernLine()
            {
                string aMark = tooLong ? " ⏳（本場時間不夠）" : "";
                if (items.Count == 1)
                {
                    var a = items[0];
                    string aFrom = hoisted && !string.IsNullOrEmpty(fromGroup) ? $"（{fromGroup} 組）" : "";
                    return $"{a.name}{aFrom}　`{a.id}`{aMark}";
                }
                var aIds = new List<string>();
                foreach (var a in items) aIds.Add($"{a.name} `{a.id}`");
                return $"{label}{aMark} — {string.Join(" ／ ", aIds)}";
            }
        }

        /// <summary>
        /// 擲骰＋兩層排序（Tim 2026-08-17 拍板 kind 標記方案）。
        /// <para>順序的三道處理，各自防的是不同的事：</para>
        /// <list type="number">
        /// <item><b>可用性（隱藏）</b>：kind 特殊邏輯判定做不成的活動**整項不列入候選**
        ///       —— 沒開播的陪看留在骰面上，是在浪費一個選項的位置。</item>
        /// <item><b>優先層</b>：條件成立的活動排在前段（<b>層內仍隨機</b> —— 優先不等於指定，
        ///       多項同時優先時彼此的順序仍是骰出來的）。</item>
        /// <item><b>時間感知</b>：min_minutes 不足者一律降到最尾並標明（<b>不隱藏</b> ——
        ///       做得成但不划算，資訊留著讓人自己判斷）。這道**壓過優先層**：
        ///       「最優先但這場做不完」是自相矛盾的建議。</item>
        /// </list>
        /// </summary>
        static (List<DiceEntry> list, string source, bool isLive) RollActivities(string iPersona, int iRemainMinutes)
        {
            // 掃描走 UCL_FreeTimeIO 的唯一實作（管理頁共用同一份 —— 兩份掃描器的漂移
            // 症狀是「頁面看到的清單跟實際擲出來的不一樣」，而它不會報錯）。
            // 過濾在 merge 之後：專案層的 enabled:false 才擋得住共用層的啟用（kotoko QA 血證）。
            var aScanned = UCL_FreeTimeIO.ScanActivities();
            int aSharedCount = 0, aProjectCount = 0;
            bool aIsLive = false;
            var aVisible = new List<ActivityInfo>();

            // ── 第一道：可用性 ＋ 第二道：時間感知（都在活動層判定，與分組無關）──────
            foreach (var a in aScanned)
            {
                if (!a.enabled) continue;
                var aGate = UCL_FreeTimeGating.Evaluate(a, iPersona);
                if (!aGate.visible) continue;                       // 條件不成立 → 隱藏，不佔候選位置
                if (a.kind == UCL_FreeTimeActivityKind.StreamWatch) aIsLive = true;   // 看得到它就是在播

                // ⚠ 時間感知在**脫離之前**判定，因為「時間不夠壓過優先」——
                //   一件這場做不完的活動不該被拉到最前面當推薦（那是自相矛盾的建議）。
                //   iRemainMinutes<=0 時不做這道（剩餘算不出來就不假裝知道）。
                bool aTooLong = iRemainMinutes > 0 && a.minMinutes > 0 && a.minMinutes > iRemainMinutes;
                aVisible.Add(new ActivityInfo
                {
                    id = a.id,
                    name = a.name + (aGate.nameSuffix ?? "")
                           + (aTooLong ? $" ⏳（建議 ≥{a.minMinutes} 分，剩 {iRemainMinutes} 分 —— 本場時間不夠）" : ""),
                    how = a.how,
                    path = a.path,
                    minMinutes = a.minMinutes,
                    priority = aGate.priority && !aTooLong,   // 降級了就不該再標成優先（標記要跟實際位置一致）
                    group = a.group,
                    tooLong = aTooLong,
                });
                if (a.isProjectLayer) aProjectCount++; else aSharedCount++;
            }

            // ── 第二道半：飢餓置頂（Tim 2026-08-24）──────────────────────────
            // 物理意義：券囤積那條是綁 kind 的特殊邏輯，**這條是通用的** ——
            //   任何活動「太久沒被選」都該被頂一次，判準不看它是什麼活動。
            //   所以它不住在 UCL_FreeTimeGating 的 kind switch 裡，住在這裡（唯一看得到全清單的地方）：
            //   ⚠ 上限 STARVE_HOIST_MAX 需要全域視野 —— 每項各自判定的話沒有一項知道自己是第幾餓。
            // ⚠ 不動 visible（飢餓不能讓一個做不成的活動復活）；
            //   也不覆蓋 tooLong（時間不夠壓過優先 —— 那條規則在上面已經定了）。
            var aStats = UCL_FreeTimeActivityStatsIO.Load(iPersona);
            var aStarveIds = new List<string>();
            foreach (var a in aVisible) if (!a.tooLong) aStarveIds.Add(a.id);
            var aStarved = UCL_FreeTimeActivityStatsIO.PickStarved(aStats, aStarveIds, out int aStarveOverflow);
            if (aStarved.Count > 0)
            {
                for (int i = 0; i < aVisible.Count; i++)
                {
                    var a = aVisible[i];
                    if (a.tooLong || !aStarved.TryGetValue(a.id, out int aGap)) continue;
                    a.priority = true;
                    a.name += UCL_FreeTimeActivityStatsIO.StarveSuffix(aGap, aStats.Picks(a.id));
                    aVisible[i] = a;   // ActivityInfo 是 struct ⇒ 寫回去，不然改的是複本
                }
            }

            // ── 第三道：脫離（Tim 2026-08-18）──────────────────────────────
            // 觸發特殊規則排序的活動**從分組脫離成單獨一項**排最前。
            // 理由：它此刻特別值得做的理由是它自己的（棋局對手在線／券快滿），
            //      被組名蓋住就傳達不到 —— 「繪圖」這個組名不會告訴你券超過 100 了。
            var aHoisted = new List<DiceEntry>();
            var aRest = new List<ActivityInfo>();
            foreach (var a in aVisible)
            {
                if (a.priority)
                {
                    var aEntry = new DiceEntry { label = a.name, priority = true, hoisted = !string.IsNullOrEmpty(a.group), fromGroup = a.group };
                    aEntry.items.Add(a);
                    aHoisted.Add(aEntry);
                }
                else aRest.Add(a);   // 脫離的必須從原組移除 —— 同一件事在骰面出現兩次會被讀成兩件事
            }

            // ── 第四道：分組（未脫離的收成組項；未分組者自成一項）────────────
            // 用 List 而非 Dictionary 保序：先出現的組先建，之後整批洗牌
            // （Dictionary 的列舉序是實作細節，靠它就是把隨機性交給不保證的東西）。
            var aGroups = new List<DiceEntry>();
            foreach (var a in aRest)
            {
                if (string.IsNullOrEmpty(a.group))
                {
                    var aSolo = new DiceEntry { label = a.name, priority = false };
                    aSolo.items.Add(a);
                    aGroups.Add(aSolo);
                    continue;
                }
                DiceEntry aFound = null;
                foreach (var g in aGroups) if (g.items.Count > 0 && g.label == a.group) { aFound = g; break; }
                if (aFound == null)
                {
                    aFound = new DiceEntry { label = a.group, priority = false };
                    aGroups.Add(aFound);
                }
                aFound.items.Add(a);
            }

            // 組項整體時間不夠 ＝ 組內**全員**都不夠。有一個做得成就不該把整組標成做不完。
            foreach (var g in aGroups)
            {
                bool aAllTooLong = g.items.Count > 0;
                foreach (var a in g.items) if (!a.tooLong) { aAllTooLong = false; break; }
                g.tooLong = aAllTooLong;
            }

            // 兩層各自洗牌 —— 優先層內部也要隨機（拍板：最優先有多項時一樣隨機排序）
            Shuffle(aHoisted);
            Shuffle(aGroups);

            // 時間不夠的組項降到最尾（與原本的活動層行為一致，只是單位從活動變成項）
            var aFit = new List<DiceEntry>();
            var aTail = new List<DiceEntry>();
            foreach (var g in aGroups) { if (g.tooLong) aTail.Add(g); else aFit.Add(g); }

            var aList = new List<DiceEntry>(aHoisted.Count + aGroups.Count);
            aList.AddRange(aHoisted);
            aList.AddRange(aFit);
            aList.AddRange(aTail);
            // ⚠ overflow 一定要說出來：「只有 2 項餓」與「有 9 項餓而我只頂 2 項」在骰面上同形。
            string aStarveNote = aStarved.Count == 0 ? ""
                : $"｜💤 飢餓置頂 {aStarved.Count} 項"
                  + (aStarveOverflow > 0 ? $"（另有 {aStarveOverflow} 項也超過 {UCL_FreeTimeActivityStatsIO.STARVE_THRESHOLD} 場沒選，本輪沒頂上來）" : "");
            string aStatsNote = aStats.loaded ? $"｜本人第 {aStats.sessionsTotal} 場" : "｜⚠ 尚無活動統計（不是 0 場，是沒有讀數）";
            return (aList, $"UCL_Core 共用 {aSharedCount} + 專案 {aProjectCount}{aStatsNote}{aStarveNote}", aIsLive);
        }

        /// <summary>Fisher-Yates（System.Random —— 擲骰不需要密碼學強度）。</summary>
        static void Shuffle<T>(List<T> ioList)
        {
            var aRng = new System.Random();
            for (int i = ioList.Count - 1; i > 0; i--)
            {
                int j = aRng.Next(i + 1);
                (ioList[i], ioList[j]) = (ioList[j], ioList[i]);
            }
        }

        // ===========================================================
        // 區塊：session state 讀寫（canvas.py 對齊端 —— 改欄位要兩端同步）
        // ⚠ 免費像素的額度檔（`Canvas/freetime/<P>.json`）2026-08-18 已廢除 —— 改成限時繪圖券，
        //   發放走 ledger、歸零是到期的自然結果。那條「第二套錢」不再存在。
        // ===========================================================
        // 路徑委派 UCL_SessionService —— 這條組法曾在三個檔各寫一份
        // （本檔、UCL_FreeTimeGating、Cmd_Sculpture），改一處另兩處指舊位置且不報錯。
        static string SessionPath(string iPersona)
            => UCL_SessionService.SessionPath(iPersona);   // 扁平化後路徑不吃 kind（TASK-0054 拍板⑤）

        // 區塊職責：session 檔的讀 / 寫 / 收工 —— 三處都走 typed model，不再逐鍵手搭。
        // 物理意義：鍵名從「字串」變成「欄位」⇒ 打錯是編譯期錯誤，不是讀回預設值。
        //          （讀回預設值在這裡長得跟「這個人沒有 session」一模一樣，那是最難查的一種。）
        // 數值影響：JSON 逐鍵與舊格式相同（欄位名＝鍵名，見 UCL_FreeTimeSession 的命名警告），
        //          既有檔不需遷移，python 端讀 active / end_ts 不受影響。
        internal static UCL_FreeTimeSession LoadSession(string iPersona)
            => UCL_SessionService.Load<UCL_FreeTimeSession>(UCL_SessionKind.FreeTime, iPersona);

        internal static void SaveSession(string iPersona, UCL_FreeTimeSession iSession)
            => UCL_SessionService.Save(UCL_SessionKind.FreeTime, iPersona, iSession);

        /// <summary>收工。rounds 是自由時間專屬的，所以由本檔取出回報；翻旗標與記時刻走 service。</summary>
        static void CloseSession(string iPersona, UCL_FreeTimeSession ioSession, string iReason, out int oRounds)
        {
            oRounds = ioSession.rounds;
            UCL_SessionService.Close(UCL_SessionKind.FreeTime, iPersona, ioSession, iReason);
        }

        // ===========================================================
        // 區塊職責：發放本場的免費像素 —— **一批綁本場的限時繪圖券**。
        //
        // 物理意義：2026-08-18 前這裡寫的是 `Canvas/freetime/<persona>.json`（granted/used 額度檔），
        //          而那份檔是**券系統之外的第二套錢**：它需要自己的發放、自己的消費、
        //          以及一條專門的「用不完歸零」作廢路徑。三個消費端（canvas.py / Cmd_Sculpture /
        //          本檔）各自讀它、各自算可用量。
        //   ⇒ Tim 拍板併入券系統：免費像素 ＝ 限時券。**歸零變成到期的自然結果**，
        //     作廢路徑整條刪掉（`ForfeitFreePixels` / `ReadFreePixelUsage` / `FreePixelPath` 一併移除）。
        //
        // 數值影響：發 FREE_PIXELS_PER_SESSION 張限時券，`ref` ＝ session_id
        //   （那是「這批屬於哪一場」的唯一憑據，回報本場已用時按它查）。
        //   到期 ＝ until ＋ **1 分緩衝**：截止是軟的，最後一件活動可能跨過 until 才收工。
        //   回傳「上一場作廢了幾張」—— 已由 ledger 在寫入時清理並記 history，這裡只是讀來回報。
        // ⚠ 錢一律走 ledger（唯一寫入 owner），不自己寫券檔。
        // ===========================================================
        static int GrantFreePixelVouchers(string iPersona, string iSessionId, DateTime iUntilLocal)
        {
            // 先讀「還掛在身上的舊限時券」＝ 上一場沒用完的（此刻可能還沒被清理）
            int aPrevLeftover = UCL_CanvasVoucherLedger.GetExpiring(iPersona);

            string aExpiresIso = iUntilLocal.AddMinutes(FREE_PIXEL_GRACE_MINUTES)
                .ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fff",
                    System.Globalization.CultureInfo.InvariantCulture) + "Z";
            UCL_CanvasVoucherLedger.Grant(iPersona, FREE_PIXELS_PER_SESSION,
                "freetime", iSessionId, aExpiresIso);
            return aPrevLeftover;
        }

        // ===========================================================
        // 區塊：酒館宣告（in-process Cmd_Tavern，best-effort —— 權威狀態先落地、廣播殿後）
        // ===========================================================
        // ⚠ iArgs：本筆 cmd 的 args（`_cmd_id` 由此傳給子 Cmd，seq 才回得到本筆 context）。
        // ===========================================================
        // 區塊職責：固定位置的「續跑」區塊 —— 自由時間最容易斷在「做完一件事就沒有下一步」。
        // 物理意義：原本這行指令埋在 next 清單的第 3 條，跟其他三條長得一樣 ——
        //          而**看起來一樣的東西不會被當成動作**。獨立成一個位置固定、只有一條指令的區塊，
        //          讓「還沒結束」在視覺上就跟「已收工」不同（收工時同一位置變成 ⏹，且不給指令）。
        // 數值影響：純輸出；不影響任何判定。剩餘分鐘由呼叫端傳入（時間感一律由 Cmd 供給）。
        // ===========================================================
        static void AppendContinueBlock(StringBuilder ioR, string iPersona, int iRemainMinutes)
        {
            ioR.AppendLine();
            ioR.AppendLine($"## ▶ 下一步（自由時間**進行中**，剩 {iRemainMinutes} 分）");
            // 區塊職責：把「社交對話」寫成**同時進行**而不是一個選項（Tim 2026-08-18）。
            // 物理意義：social-chat 已 enabled:false 併進本流程 —— 換骰這一步本身就在讀訊息、發訊息。
            //          不寫明的話它會變成「消失的活動」：骰面上看不到，也沒人知道它去哪了。
            ioR.AppendLine("💬 **社交對話是同時進行的，不是另一個選項** —— 換骰這一步本身就在讀未讀訊息、");
            ioR.AppendLine("　 也可以帶 `body` 跟同事講話。所以不必為了「跟人互動」去挑一個活動；");
            ioR.AppendLine("　 挑你想做的事，講話在換骰時一起發生。");
            ioR.AppendLine();
            ioR.AppendLine("活動告一段落就跑這行 —— **截止是軟的**，時間到不打斷進行中的活動，最後一件做完跑它才收工：");
            ioR.AppendLine("```bash");
            ioR.AppendLine($"senate ucmd run FreeTime --persona {iPersona} \\");
            ioR.AppendLine($"    --arg step=next --arg persona={iPersona} [--arg-file body=<想跟同事說的話>]");
            ioR.AppendLine("```");
            ioR.AppendLine("- `body` **可選**（不強制）—— 帶了就併進換骰宣告同一則，換骰同時跟同事交流。");
        }

        // ===========================================================
        // ===========================================================
        // 區塊職責：把**未讀酒館訊息**併進換骰回傳檔，並比照叮推進已讀游標。
        // 物理意義：Tim 2026-08-18 拍板兩件事 ——
        //   ① 骰面與訊息要在**同一份檔**（分兩處＝一定有一處不會被讀到）；
        //   ② **要推游標**：換骰是高頻動作，只看不推的話未讀會整場堆積，
        //      下一次真的 catchup 一次倒出來 —— 那等於沒有人在讀。
        // ⚠ 2026-08-21：呈現改**委派** `UCL_TavernCatchupService.AppendUnreadSection`。
        //   在此之前這裡自帶一份渲染，跟叮那份的截斷長度／排除規則各自演化
        //   ⇒ 同一批訊息在兩處長得不一樣，而兩邊都不報錯。截斷長度改由後台設定
        //   （`UCL_ChatTavernSettings.MessageBodyClip`），不再寫死在這裡。
        // ===========================================================
        static void AppendTavernCatchupSection(StringBuilder ioR, string iPersona)
        {
            ChatTavern.UCL_TavernCatchupService.AppendUnreadSection(
                ioR, iPersona, "tavern",
                iQuietSystem: false,          // 自由時間看得到打款／結算（那也是同事動態）
                iIncludeSelf: false,
                iBodyClip: ChatTavern.UCL_ChatTavernSettings.MessageBodyClip,
                iBodyClipMentioned: ChatTavern.UCL_ChatTavernSettings.MessageBodyClipMentioned,
                iAdvance: true,
                out _, out _, out _, out _);
            ioR.AppendLine("- inbox（@ 我的待處理）不在本段範圍 —— 那走 `run Tavern --arg op=catchup`"
                           + "（舊的 `tavern_catchup.py` 已是指路 stub）。");
        }

        // internal：活動入口 Cmd_FreeTimeActivity 複用同一支發文（含「bank 解析失敗不擋發言」那個修正）——
        // 各自再寫一份的話，其中一份遲早會退回「沒錢就沒聲音」。
        internal static async UniTask<int> TavernPost(IDictionary<string, string> iArgs, string iPersona, string iBody, string iSubtag, CancellationToken iToken)
        {
            try
            {
                // 區塊職責：自由時間的開場 / 換骰 / 收工宣告發文
                // ⚠ 2026-08-14 修（apex-one 讀 code 抓到）：此處原本是
                //     `lock 讀不到 bank → LogWarning + return 0`，也就是**沒錢就沒聲音**。
                //   那與同日立的原則正好相反：解析不到帳號只影響計酬，**不擋發言** ——
                //   發言權與收款權是兩回事。而且失敗形式是 LogWarning + 回 0，
                //   於是宣告會**安靜地不出現**，同事看酒館只會以為「她這場沒發」。
                //   現在 bank 已完全不參與發文（計酬由 persona 反解），閘門的前提本身也消失了。
                var aLock = UCL_AwakeningService.ReadLock(iPersona);
                var aArgs = new Dictionary<string, string>
                {
                    { "op", "post" },
                    { "room", "tavern" },
                    { "persona", iPersona },
                    { "body", iBody },
                    { "meta", $"{{\"tag\":\"free-time\",\"subtag\":\"{iSubtag}\",\"category\":\"chat\"}}" },
                };
                if (aLock != null && !string.IsNullOrEmpty(aLock.session_token)) aArgs["session_token"] = aLock.session_token;
                var aPostCtx = UCL_AgentCmdContexts.FromArgs(iArgs, "FreeTime.post");
                if (aPostCtx != null) aPostCtx.LastPostSeq = 0;
                UCL_AgentCmdContexts.PropagateCmdId(iArgs, aArgs);
                await new ChatTavern.Cmd_Tavern().ExecuteAsync(aArgs, iToken);
                return aPostCtx?.LastPostSeq ?? 0;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FreeTime] 酒館宣告失敗（best-effort，不影響 session）: {e.Message}");
                return 0;
            }
        }

        // ===========================================================
        // 區塊：小工具（時間欄 / 骰面段 / until 解析 / JsonData 讀取 / 原子寫 / payload）
        // ===========================================================
        static void AppendTimeFields(StringBuilder ioR, DateTime iNow, DateTime iUntil)
        {
            // 三個時間欄（Tim 拍板）：時間感由 Cmd 供給，agent 不自己心算（第七型未遂血證）
            ioR.AppendLine("## time（時間感由本 Cmd 供給 —— 別自己心算）");
            ioR.AppendLine($"- 當前時間: **{iNow:yyyy-MM-dd HH:mm}**（本地）");
            ioR.AppendLine($"- 自由時間到: **{iUntil:HH:mm}**（軟截止 —— 時間到不打斷進行中活動，最後一件做完跑 next 才收工）");
            ioR.AppendLine($"- 剩餘: **{(int)Math.Max(0, (iUntil - iNow).TotalMinutes)} 分鐘**");
        }

        // 區塊職責：優先層的一行說明（開場宣告／換骰宣告共用一份 —— 兩處各寫一次，
        //          遲早只有一邊跟著改）。物理意義：優先**不是指定**，仍是參考。
        static void AppendPriorityNote(StringBuilder ioBody, List<DiceEntry> iList, bool iIsLive)
        {
            int aPri = 0;
            foreach (var a in iList) if (a.priority) aPri++;
            if (aPri <= 0) return;
            ioBody.AppendLine(iIsLive
                ? $"⭐ 優先層 {aPri} 項排在前面（含📺直播中；層內仍隨機、不強制）"
                : $"⭐ 優先層 {aPri} 項排在前面（條件成立才會進來；層內仍隨機、不強制）");
        }

        // 區塊職責：回傳檔的骰面段 —— 項（分組／單獨）在外層，具體活動在內層。
        // 物理意義：**要能直接下 op=pick 的東西是具體活動的 id**，所以組項一定要把組員的
        //          id / how / md 路徑列出來。只印組名的骰面等於把路指到一半 ——
        //          讀的人還得再跑一次 Cmd 才知道能填什麼，而那正是這層要消滅的斷點。
        // 數值影響：純輸出。組員數 1 的項（未分組活動、脫離出來的單獨項）不重複印一層縮排。
        // 骰面印「這是什麼」，**不印「怎麼做」**（Tim 2026-08-21）。
        // 物理意義：挑活動時需要的是**一句話認出它**；執行細節（md 全文路徑）在挑之前沒有用，
        //          每輪重印一次只是把未讀訊息與時間欄擠出視線。
        //          ⇒ 細節長在**需要它的那一刻**：`op=pick` 的回傳檔會印該活動 md 的全文路徑。
        // ⚠ 刻意不留 `verbose` 旋鈕 —— 沒有呼叫端會傳 true 的分支等於一條沒人走的路，遲早有人把它接回去。
        static void AppendDiceSection(StringBuilder ioR, List<DiceEntry> iList, string iSource, bool iIsLive)
        {
            ioR.AppendLine("## dice（兩層隨機排序，僅供參考 — 自由意志優先；無明確意圖從前 3 挑）");
            ioR.AppendLine("- ⭐＝優先層（條件成立：直播中／棋局對手也在自由時間）；層內仍隨機。");
            ioR.AppendLine("- 做不成的活動**已隱藏**（例：沒開播時不列「觀看直播」）—— 清單長度會隨當下狀況變動，那是正常的。");
            ioR.AppendLine("- **同組收成同一項**；觸發特殊規則的活動會**脫離分組成單獨一項**排最前（理由跟著印在它旁邊）。");
            ioR.AppendLine("- `op=pick` 要填的是**具體活動 id**（下面反引號裡那個），不是組名。");
            ioR.AppendLine("- ℹ 這裡只說「是什麼」；**怎麼做**（活動 md 全文路徑）在 `op=pick` 之後才印。");
            for (int i = 0; i < iList.Count; i++)
            {
                var aEntry = iList[i];
                if (aEntry.items.Count == 1)
                {
                    var a = aEntry.items[0];
                    string aFrom = aEntry.hoisted && !string.IsNullOrEmpty(aEntry.fromGroup)
                        ? $"（⭐ 自「{aEntry.fromGroup}」組脫離 —— 此刻特別值得做）" : "";
                    ioR.AppendLine($"{i + 1}. {(aEntry.priority ? "⭐ " : "")}**{a.name}**　`{a.id}`{aFrom}"
                                   + (string.IsNullOrEmpty(a.how) ? "" : $" — {a.how}"));
                    continue;
                }
                ioR.AppendLine($"{i + 1}. {(aEntry.priority ? "⭐ " : "")}**{aEntry.label}**"
                               + $"（{aEntry.items.Count} 項{(aEntry.tooLong ? "，本組全員本場時間不夠" : "")}）");
                foreach (var a in aEntry.items)
                {
                    ioR.AppendLine($"   - `{a.id}` **{a.name}**"
                                   + (string.IsNullOrEmpty(a.how) ? "" : $" — {a.how}"));
                }
            }
            ioR.AppendLine($"- [清單來源: {iSource}]");
        }

        /// <summary>HH:mm 解析：已過的時刻若在 12 小時內視為打錯（blocked），超過 12 小時視為跨日（+1 天）。</summary>
        static bool TryParseUntil(string iUntil, DateTime iNow, out DateTime oUntil, out string oError)
        {
            oUntil = default;
            oError = null;
            if (string.IsNullOrEmpty(iUntil)) { oError = "until 必填（--arg until=<HH:mm 本地>）"; return false; }
            if (!TimeSpan.TryParse(iUntil, out TimeSpan aTod) || aTod < TimeSpan.Zero || aTod >= TimeSpan.FromDays(1))
            { oError = $"until 解析失敗：'{iUntil}'（需 HH:mm，例 12:30）"; return false; }
            oUntil = iNow.Date + aTod;
            if (oUntil <= iNow)
            {
                if ((iNow - oUntil) > TimeSpan.FromHours(12)) oUntil = oUntil.AddDays(1);   // 深夜跨日（23:50 → 00:30）
                else { oError = $"until={iUntil} 已過（現在 {iNow:HH:mm}）—— 時限判定只認時鐘"; return false; }
            }
            return true;
        }

        static string ReadStr(JsonData iJd, string iKey) => iJd != null && iJd.Contains(iKey) ? iJd[iKey].ToString() : "";
        static int ReadInt(JsonData iJd, string iKey) { try { return iJd != null && iJd.Contains(iKey) ? int.Parse(iJd[iKey].ToString()) : 0; } catch { return 0; } }
        static bool ReadBool(JsonData iJd, string iKey) { try { return iJd != null && iJd.GetBool(iKey, false); } catch { return false; } }

        static DateTime? ParseIsoLocal(string iIso)
        {
            if (string.IsNullOrEmpty(iIso)) return null;
            return DateTime.TryParse(iIso, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime aDt)
                ? aDt.ToLocalTime() : (DateTime?)null;
        }

        static void AtomicWrite(string iPath, string iContent)
        {
            UCL_LettersPath.EnsurePayloadDir(iPath);       // 建目錄＋補 cmd/.gitignore（唯一入口）
            string aTmp = iPath + ".tmp";
            File.WriteAllText(aTmp, iContent, new UTF8Encoding(false));
            if (File.Exists(iPath)) File.Delete(iPath);
            File.Move(aTmp, iPath);
        }

        // 回傳檔落點走 UCL_LettersPath —— **版面只有一份實作**。
        // 2026-08-18 Tim 拍板搬進 `letters/<persona>/cmd/`：letters 頂層原本人寫的信與機器回傳檔
        // 混住，而 `Cmd_DocEdit` 找「最新那封信」時實測抓到了 `_freetime_next.md`。
        // ⇒ 「是不是信」從靠檔名前綴猜，變成位置的問題。
        // ⚠ 對側契約：python 端是 `_lib/ucl_paths.py::letters_cmd_payload()`，兩端要一起改。
        internal static string PayloadPath(string iPersona, string iStep)
            => UCL_LettersPath.CmdPayload(iPersona, "freetime", iStep);

        internal static void WritePayload(IDictionary<string, string> iArgs, string iPath, string iReport)
        {
            try
            {
                UCL_LettersPath.EnsurePayloadDir(iPath);   // 建目錄＋補 cmd/.gitignore（唯一入口）
                File.WriteAllText(iPath, iReport, new UTF8Encoding(false));
                // 回報產出檔 → result 檔 outputs 欄，run_cmd 端隨 verdict 印路徑（不再靠 skill 背）
                UCL_AgentCommandRunner.ReportOutputFile(iArgs, iPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FreeTime] 回傳落檔失敗 {iPath}: {e.Message}");
            }
        }
    }
}
#endif
