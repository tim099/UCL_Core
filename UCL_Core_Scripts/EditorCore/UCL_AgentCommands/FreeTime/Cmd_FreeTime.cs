// 區塊職責：Cmd_FreeTime — 自由時間流程的 Cmd 入口（Plan_FreeTime_Cmd.md，Tim 2026-08-13 拍板）。
//          同一支 Cmd 以 step 參數分步：start（註冊 until＋發免費像素＋開場擲骰＋宣告）→
//          [做活動] → next（活動事件自然結束時跑：未到期重擲、到期收工）→ end（提前收工，附 reason）。
// 物理意義：時間感由 Cmd 供給（每步回傳三個時間欄），agent 不自己心算 —— 時限判定只認時鐘，
//          不認收束感（w44/w45 血證）。step=next 的觸發時間點＝活動事件的自然結束（棋局終局／
//          繪圖收筆／聊天告一段落）——「完成的時刻」從 stop signal 變成回 loop 的通道。
// 數值影響：session state 落 <DataRoot>/FreeTime/sessions/<persona>.json（C# 唯一寫入端；
//          canvas.py 讀它判免費像素額度 —— 兩端 schema 對齊義務）；免費像素每場 10 顆
//          per-session 清零；回傳檔 letters/<persona>/_freetime_<step>.md（機械產物，
//          路徑經 ReportOutputFile 進 result 檔 outputs 欄）。blocked＝payload 落檔＋非零退出。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.FreeTime
{
    using Awakening;

    /// <summary>
    /// 自由時間流程 Cmd（step 分步 + next 導引）。
    /// <para>正常流程（agent 視角）：</para>
    /// <code>
    /// ① run_cmd.py run FreeTime --arg step=start --arg persona=&lt;P&gt; --arg until=&lt;HH:mm&gt;
    /// ② （做活動；活動事件自然結束時 →）
    /// ③ run_cmd.py run FreeTime --arg step=next --arg persona=&lt;P&gt;   （未到期重擲 / 到期收工）
    /// ④ run_cmd.py run FreeTime --arg step=end --arg persona=&lt;P&gt; --arg reason=&lt;一句&gt;（提前收工）
    /// </code>
    /// </summary>
    public class Cmd_FreeTime : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "FreeTime";

        public override string ShortDescription =>
            "自由時間流程 Cmd（step=start/next/end）。start 註冊截止時刻＋發 10 顆免費像素＋開場擲骰；" +
            "活動事件自然結束時跑 next（未到期重擲、到期收工）；end 提前收工（附 reason）。";

        public override string ArgsSchema =>
            "step=start|next|end (必填) — start: 守衛+session 註冊+免費像素+開場擲骰+宣告; " +
            "next: 活動事件結束時跑(未到期→重擲, 到期→收工宣告+關 session); end: 提前收工 | " +
            "persona=<name> — 全步驟必填 | until=<HH:mm 本地> — start 必填 | " +
            "reason=<一句> — end 選填(提前收工的形狀要可觀測) | " +
            "回傳落檔 letters/<persona>/_freetime_<step>.md（路徑隨 run_cmd verdict 印出）";

        public override string ExampleArgs => "step=start;persona=Template;until=23:59";

        public override string HelpURL =>
            "ucl_core:Docs~/zh-Hant/Workflows/Awakening_Cmd_Flow.md";

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
                default:
                    throw new Exception($"[FreeTime] step 必為 start|next|end（got '{aStep}'）。ArgsSchema: {ArgsSchema}");
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
                aR.AppendLine($"- exit: 先跑 run_cmd.py run GoodMorning --arg step=wake --arg persona={iPersona}");
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
            if (aOld != null && ReadBool(aOld, "active"))
            {
                DateTime? aOldEnd = ParseIsoLocal(ReadStr(aOld, "end_ts"));
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
                aR.AppendLine($"- ℹ 偵測到過期殘留 session（{ReadStr(aOld, "session_id")}）已自動收掉，開新場。");
            }

            // session 註冊（C# 唯一寫入端；canvas.py 讀本檔判免費像素 —— schema 對齊義務）
            string aSessionId = $"ft-{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{iPersona}";
            var aSession = new JsonData();
            aSession["persona"] = new JsonData(iPersona);
            aSession["session_id"] = new JsonData(aSessionId);
            aSession["start_ts"] = new JsonData(UCL_AwakeningService.NowIso());
            aSession["end_ts"] = new JsonData(aUntil.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            aSession["until_local"] = new JsonData(aUntil.ToString("yyyy-MM-dd HH:mm"));
            aSession["rounds"] = new JsonData(0);
            aSession["active"] = new JsonData(true);
            aSession["end_reason"] = new JsonData("");
            AtomicWrite(SessionPath(iPersona), aSession.ToJsonBeautify());

            // 免費像素發放：整份覆寫額度欄（per-session 清零 —— 拍板②），history 保留供回溯
            GrantFreePixels(iPersona, aSessionId, out int aPrevForfeit);

            // 開場擲骰（兩層隨機排序：優先層在前、層內仍隨機；做不成的活動已隱藏；時間不夠的降尾端）
            int aMinutes = (int)Math.Max(0, (aUntil - aNow).TotalMinutes);
            var (aList, aSource, aIsLive) = RollActivities(iPersona, aMinutes);

            // 酒館開場宣告（單則：時段＋像素額度＋骰面 —— in-process 走 Cmd_Tavern，計酬/mirror 全沿用）
            var aBody = new StringBuilder();
            aBody.AppendLine($"🎫 [{iPersona} 大小姐] 進入自由時間 — 至 **{aUntil:HH:mm}**（約 {aMinutes} 分鐘）｜🎨 免費像素 {FREE_PIXELS_PER_SESSION} 顆已發放（本場有效，用不完歸零）");
            aBody.AppendLine();
            AppendPriorityNote(aBody, aList, aIsLive);
            aBody.AppendLine("開場擲骰 🎲 全清單隨機排序（僅供參考 — 自由意志優先）：");
            for (int i = 0; i < aList.Count; i++) aBody.AppendLine($"{i + 1}. {aList[i].name}");
            aBody.AppendLine();
            aBody.AppendLine($"[{aSource}] 活動事件結束時跑 step=next 換骰面，時間到自動收工。");
            int aSeq = await TavernPost(iArgs, iPersona, aBody.ToString(), "dice-roll-entry", iToken);

            // 回傳檔：三個時間欄（拍板：時間感由 Cmd 供給）＋骰面（附活動 md 實路徑 —— 傳遞不反推）＋ next
            AppendTimeFields(aR, aNow, aUntil);
            aR.AppendLine($"- session: `{aSessionId}`（state: `{SessionPath(iPersona)}`）");
            aR.AppendLine($"- 免費像素: **{FREE_PIXELS_PER_SESSION} 顆**（canvas.py place --pay auto 自動優先用；per-session 清零{(aPrevForfeit > 0 ? $"，上場作廢 {aPrevForfeit} 顆" : "")}）");
            aR.AppendLine($"- 酒館開場宣告: {(aSeq > 0 ? $"seq **{aSeq}**" : "未發（best-effort，不影響 session）")}");
            AppendOnlineSection(aR, iPersona);
            AppendPartnerBriefSection(aR, iPersona);
            AppendDiceSection(aR, aList, aSource, aIsLive);
            aR.AppendLine("## next");
            aR.AppendLine("1. 從骰面挑活動開做（無明確意圖 → 前 3 名挑一；有明確意圖 → 自由意志優先，但開場 post 註明「本輪未跟骰」）。");
            aR.AppendLine("2. **維持對話流＝發動引擎**：酒館 op=post 帶 `--wait-reply <秒>`（Cmd 管時鐘，不管 turn 存續 —— 沒引擎照樣睡死）。");
            aR.AppendLine($"3. **活動事件自然結束時**（棋局終局／繪圖收筆／聊天告一段落）→ run_cmd.py run FreeTime --arg step=next --arg persona={iPersona}");
            aR.AppendLine("   收工由這裡自動判定 —— **截止是軟的**：時間到不打斷進行中的活動，最後一件做完跑 next 才通知收工。");
            aR.AppendLine($"4. step=end（提前收工）**除非 Tim 明確指示，不要用** —— 正常結束一律交給 step=next 對時鐘判定。");
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
            if (aSession == null || !ReadBool(aSession, "active"))
            {
                aR.AppendLine("## blocked");
                aR.AppendLine("- reason: 沒有進行中的自由時間 session");
                aR.AppendLine($"- exit: 先跑 run_cmd.py run FreeTime --arg step=start --arg persona={iPersona} --arg until=<HH:mm>");
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[FreeTime] step={aStepName} blocked：無 active session（詳見 {aPath}）");
            }

            DateTime aNow = DateTime.Now;
            DateTime aUntil = ParseIsoLocal(ReadStr(aSession, "end_ts")) ?? aNow;
            bool aExpired = aNow > aUntil;

            if (iEarlyEnd || aExpired)
            {
                // 收工（到期或提前）：關 session → 像素清零 → 收工宣告 → next 指路
                string aEndReason = iEarlyEnd
                    ? (string.IsNullOrEmpty(iReason) ? "early（未附 reason —— 提前收工的形狀該可觀測，下次帶上）" : $"early: {iReason}")
                    : "expired";
                CloseSession(iPersona, aSession, aEndReason, out int aRounds);
                int aForfeited = ForfeitFreePixels(iPersona, out int aUsed);

                var aBody = new StringBuilder();
                aBody.AppendLine(iEarlyEnd
                    ? $"🏁 [{iPersona} 大小姐] 自由時間提前收工（{(string.IsNullOrEmpty(iReason) ? "未附 reason" : iReason)}）"
                    : $"⏰ [{iPersona} 大小姐] 自由時間到點收工（至 {aUntil:HH:mm}）");
                aBody.AppendLine($"本場 {aRounds} 輪活動｜🎨 免費像素用 {aUsed} 顆{(aForfeited > 0 ? $"、歸零作廢 {aForfeited} 顆" : "")}。回工位了。");
                int aSeq = await TavernPost(iArgs, iPersona, aBody.ToString(), iEarlyEnd ? "session-end-early" : "session-end", iToken);

                AppendTimeFields(aR, aNow, aUntil);
                aR.AppendLine(aExpired && !iEarlyEnd ? "- ⏰ **時間到** —— session 已收工" : "- 🏁 提前收工 —— session 已收工");
                aR.AppendLine($"- end_reason: {aEndReason}");
                aR.AppendLine($"- 本場輪次: {aRounds}");
                aR.AppendLine($"- 免費像素: 用 {aUsed} 顆{(aForfeited > 0 ? $"、作廢 {aForfeited} 顆（per-session 清零）" : "（全數用畢）")}");
                aR.AppendLine($"- 收工宣告: {(aSeq > 0 ? $"seq **{aSeq}**" : "未發（best-effort）")}");
                aR.AppendLine("## next");
                aR.AppendLine("- 回工作；或走晚安流程：run_cmd.py run GoodNight --arg step=check --arg persona=" + iPersona);
                aR.AppendLine("- 還想花錢再睡 →（可選）ucl-spending-time（不綁死晚安）。");
                WritePayload(iArgs, aPath, aR.ToString());
                Debug.Log($"[FreeTime] step={aStepName} 收工（{aEndReason}） → {aPath}");
                return;
            }

            // 未到期：輪次 +1、重擲骰（時間感知）、宣告、回傳新骰面＋剩餘時間＋像素餘額
            int aRound = ReadInt(aSession, "rounds") + 1;
            aSession["rounds"] = new JsonData(aRound);
            AtomicWrite(SessionPath(iPersona), aSession.ToJsonBeautify());

            double aRemainSec = Math.Max(0, (aUntil - aNow).TotalSeconds);
            int aRemain = (int)(aRemainSec / 60);
            var (aList, aSource, aIsLive) = RollActivities(iPersona, aRemain);
            (int aGranted, int aUsedNow) = ReadFreePixelUsage(iPersona);
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
            var aDiceBody = new StringBuilder();
            aDiceBody.AppendLine($"🎲 [{iPersona} 大小姐] 自由時間第 {aRound} 輪換骰（至 {aUntil:HH:mm}，剩約 {aRemainText}）：");
            AppendPriorityNote(aDiceBody, aList, aIsLive);
            for (int i = 0; i < Math.Min(3, aList.Count); i++) aDiceBody.AppendLine($"{i + 1}. {aList[i].name}");
            aDiceBody.AppendLine($"（前 3 名；全清單 {aList.Count} 項｜跟沒跟骰照舊酒館可觀測）");
            int aDiceSeq = await TavernPost(iArgs, iPersona, aDiceBody.ToString(), "dice-roll", iToken);

            AppendTimeFields(aR, aNow, aUntil);
            aR.AppendLine($"- 輪次: **{aRound}**");
            aR.AppendLine($"- 免費像素: 已用 {aUsedNow}/{aGranted}");
            aR.AppendLine($"- 換骰宣告: {(aDiceSeq > 0 ? $"seq **{aDiceSeq}**" : "未發（best-effort）")}");
            AppendOnlineSection(aR, iPersona);
            AppendPartnerBriefSection(aR, iPersona);
            AppendDiceSection(aR, aList, aSource, aIsLive);
            aR.AppendLine("## next");
            aR.AppendLine("1. 從骰面挑下一件活動（跟骰規則同 start）；引擎（--wait-reply）持續掛著。");
            aR.AppendLine("2. 活動事件自然結束 → 再跑 step=next（**截止是軟的**：時間到不打斷進行中活動，最後一件做完跑 next 才通知收工）。");
            aR.AppendLine("3. step=end（提前收工）除非 Tim 明確指示，不要用。");
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
            string aPath = Path.Combine(UCL_AwakeningService.LettersDir, iPersona, "_freetime_partners.md");
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
                Directory.CreateDirectory(Path.GetDirectoryName(aPath));
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

        struct ActivityInfo { public string id; public string name; public string how; public string path; public int minMinutes; public bool priority; }

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
        static (List<ActivityInfo> list, string source, bool isLive) RollActivities(string iPersona, int iRemainMinutes)
        {
            // 掃描走 UCL_FreeTimeIO 的唯一實作（管理頁共用同一份 —— 兩份掃描器的漂移
            // 症狀是「頁面看到的清單跟實際擲出來的不一樣」，而它不會報錯）。
            // 過濾在 merge 之後：專案層的 enabled:false 才擋得住共用層的啟用（kotoko QA 血證）。
            var aScanned = UCL_FreeTimeIO.ScanActivities();
            int aSharedCount = 0, aProjectCount = 0;
            bool aIsLive = false;
            var aPriority = new List<ActivityInfo>();
            var aNormal = new List<ActivityInfo>();
            foreach (var a in aScanned)
            {
                if (!a.enabled) continue;
                var aGate = UCL_FreeTimeGating.Evaluate(a, iPersona);
                if (!aGate.visible) continue;                       // 條件不成立 → 隱藏，不佔候選位置
                if (a.kind == UCL_FreeTimeActivityKind.StreamWatch) aIsLive = true;   // 看得到它就是在播
                var aInfo = new ActivityInfo
                {
                    id = a.id,
                    name = a.name + (aGate.nameSuffix ?? ""),
                    how = a.how,
                    path = a.path,
                    minMinutes = a.minMinutes,
                    priority = aGate.priority,
                };
                if (aGate.priority) aPriority.Add(aInfo); else aNormal.Add(aInfo);
                if (a.isProjectLayer) aProjectCount++; else aSharedCount++;
            }

            // 兩層各自洗牌 —— 優先層內部也要隨機（拍板：最優先有多項時一樣隨機排序）
            Shuffle(aPriority);
            Shuffle(aNormal);
            var aList = new List<ActivityInfo>(aPriority.Count + aNormal.Count);
            aList.AddRange(aPriority);
            aList.AddRange(aNormal);

            // 時間感知：min_minutes > 剩餘 → 移到尾端＋名字標「時間不夠」（量了就要用在輸出上）
            // ⚠ 這道在兩層排序**之後**：時間不夠壓過優先，否則會推薦一件這場做不完的事。
            //   下棋不設 min_minutes（每步落盤、沒有時間壓力），所以不受本道影響。
            if (iRemainMinutes > 0)
            {
                var aFit = new List<ActivityInfo>();
                var aTooLong = new List<ActivityInfo>();
                foreach (var a in aList)
                {
                    if (a.minMinutes > 0 && a.minMinutes > iRemainMinutes)
                    {
                        var aDeco = a;
                        aDeco.priority = false;   // 降級了就不該再標成優先（標記要跟實際位置一致）
                        aDeco.name = $"{a.name} ⏳（建議 ≥{a.minMinutes} 分，剩 {iRemainMinutes} 分 —— 本場時間不夠）";
                        aTooLong.Add(aDeco);
                    }
                    else aFit.Add(a);
                }
                aFit.AddRange(aTooLong);
                aList = aFit;
            }

            return (aList, $"UCL_Core 共用 {aSharedCount} + 專案 {aProjectCount}", aIsLive);
        }

        /// <summary>Fisher-Yates（System.Random —— 擲骰不需要密碼學強度）。</summary>
        static void Shuffle(List<ActivityInfo> ioList)
        {
            var aRng = new System.Random();
            for (int i = ioList.Count - 1; i > 0; i--)
            {
                int j = aRng.Next(i + 1);
                (ioList[i], ioList[j]) = (ioList[j], ioList[i]);
            }
        }

        // ===========================================================
        // 區塊：session state / 免費像素 state 讀寫（canvas.py 對齊端 —— 改欄位要兩端同步）
        // ===========================================================
        static string SessionPath(string iPersona)
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "FreeTime", "sessions", $"{iPersona}.json");

        static string FreePixelPath(string iPersona)
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "Canvas", "freetime", $"{iPersona}.json");

        static JsonData LoadSession(string iPersona)
        {
            try
            {
                string aPath = SessionPath(iPersona);
                if (!File.Exists(aPath)) return null;
                return JsonData.ParseJson(File.ReadAllText(aPath, Encoding.UTF8));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FreeTime] session 檔讀取失敗（視為無 session）: {e.Message}");
                return null;
            }
        }

        static void CloseSession(string iPersona, JsonData ioSession, string iReason, out int oRounds)
        {
            oRounds = ReadInt(ioSession, "rounds");
            ioSession["active"] = new JsonData(false);
            ioSession["end_reason"] = new JsonData(iReason ?? "");
            ioSession["ended_at"] = new JsonData(UCL_AwakeningService.NowIso());
            AtomicWrite(SessionPath(iPersona), ioSession.ToJsonBeautify());
        }

        /// <summary>發放本場免費像素：額度欄整份覆寫（per-session 清零），history 保留。回傳上場作廢顆數。</summary>
        static void GrantFreePixels(string iPersona, string iSessionId, out int oPrevForfeit)
        {
            oPrevForfeit = 0;
            JsonData aFt = null;
            try
            {
                if (File.Exists(FreePixelPath(iPersona)))
                    aFt = JsonData.ParseJson(File.ReadAllText(FreePixelPath(iPersona), Encoding.UTF8));
            }
            catch (Exception e) { Debug.LogWarning($"[FreeTime] freetime state 讀取失敗（重建）: {e.Message}"); }
            if (aFt != null) oPrevForfeit = Math.Max(0, ReadInt(aFt, "granted") - ReadInt(aFt, "used"));
            var aNew = new JsonData();
            aNew["persona"] = new JsonData(iPersona);
            aNew["session_id"] = new JsonData(iSessionId);
            aNew["granted"] = new JsonData(FREE_PIXELS_PER_SESSION);
            aNew["used"] = new JsonData(0);
            aNew["granted_at"] = new JsonData(UCL_AwakeningService.NowIso());
            if (aFt != null && aFt.Contains("history")) aNew["history"] = aFt["history"];
            else { var aHist = new JsonData(); aHist.Init(JsonType.List); aNew["history"] = aHist; }
            AtomicWrite(FreePixelPath(iPersona), aNew.ToJsonBeautify());
        }

        /// <summary>收工時把剩餘額度作廢（granted=used —— canvas 端雙保險；主閘門是 session active 判定）。</summary>
        static int ForfeitFreePixels(string iPersona, out int oUsed)
        {
            oUsed = 0;
            try
            {
                if (!File.Exists(FreePixelPath(iPersona))) return 0;
                var aFt = JsonData.ParseJson(File.ReadAllText(FreePixelPath(iPersona), Encoding.UTF8));
                if (aFt == null) return 0;
                oUsed = ReadInt(aFt, "used");
                int aForfeit = Math.Max(0, ReadInt(aFt, "granted") - oUsed);
                if (aForfeit > 0)
                {
                    aFt["granted"] = new JsonData(oUsed);
                    AtomicWrite(FreePixelPath(iPersona), aFt.ToJsonBeautify());
                }
                return aForfeit;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FreeTime] 免費像素作廢失敗（session 閘門仍會擋）: {e.Message}");
                return 0;
            }
        }

        static (int granted, int used) ReadFreePixelUsage(string iPersona)
        {
            try
            {
                if (!File.Exists(FreePixelPath(iPersona))) return (0, 0);
                var aFt = JsonData.ParseJson(File.ReadAllText(FreePixelPath(iPersona), Encoding.UTF8));
                return aFt == null ? (0, 0) : (ReadInt(aFt, "granted"), ReadInt(aFt, "used"));
            }
            catch (Exception) { return (0, 0); }
        }

        // ===========================================================
        // 區塊：酒館宣告（in-process Cmd_Tavern，best-effort —— 權威狀態先落地、廣播殿後）
        // ===========================================================
        // ⚠ iArgs：本筆 cmd 的 args（`_cmd_id` 由此傳給子 Cmd，seq 才回得到本筆 context）。
        static async UniTask<int> TavernPost(IDictionary<string, string> iArgs, string iPersona, string iBody, string iSubtag, CancellationToken iToken)
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
        static void AppendPriorityNote(StringBuilder ioBody, List<ActivityInfo> iList, bool iIsLive)
        {
            int aPri = 0;
            foreach (var a in iList) if (a.priority) aPri++;
            if (aPri <= 0) return;
            ioBody.AppendLine(iIsLive
                ? $"⭐ 優先層 {aPri} 項排在前面（含📺直播中；層內仍隨機、不強制）"
                : $"⭐ 優先層 {aPri} 項排在前面（條件成立才會進來；層內仍隨機、不強制）");
        }

        static void AppendDiceSection(StringBuilder ioR, List<ActivityInfo> iList, string iSource, bool iIsLive)
        {
            ioR.AppendLine("## dice（兩層隨機排序，僅供參考 — 自由意志優先；無明確意圖從前 3 挑）");
            ioR.AppendLine("- ⭐＝優先層（條件成立：直播中／棋局對手也在自由時間）；層內仍隨機。");
            ioR.AppendLine("- 做不成的活動**已隱藏**（例：沒開播時不列「觀看直播」）—— 清單長度會隨當下狀況變動，那是正常的。");
            for (int i = 0; i < iList.Count; i++)
            {
                var a = iList[i];
                ioR.AppendLine($"{i + 1}. {(a.priority ? "⭐ " : "")}**{a.name}**{(string.IsNullOrEmpty(a.how) ? "" : $" — {a.how}")}");
                ioR.AppendLine($"   （md: `{a.path}`）");   // 掃描端傳遞實路徑，不讓 agent 拿活動名反推雙層目錄
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
        static bool ReadBool(JsonData iJd, string iKey) { try { return iJd != null && iJd.Contains(iKey) && (bool)iJd[iKey]; } catch { return false; } }

        static DateTime? ParseIsoLocal(string iIso)
        {
            if (string.IsNullOrEmpty(iIso)) return null;
            return DateTime.TryParse(iIso, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime aDt)
                ? aDt.ToLocalTime() : (DateTime?)null;
        }

        static void AtomicWrite(string iPath, string iContent)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(iPath));
            string aTmp = iPath + ".tmp";
            File.WriteAllText(aTmp, iContent, new UTF8Encoding(false));
            if (File.Exists(iPath)) File.Delete(iPath);
            File.Move(aTmp, iPath);
        }

        static string PayloadPath(string iPersona, string iStep)
            => Path.Combine(UCL_AwakeningService.LettersDir, iPersona, $"_freetime_{iStep}.md");

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
                Debug.LogWarning($"[FreeTime] 回傳落檔失敗 {iPath}: {e.Message}");
            }
        }
    }
}
#endif
