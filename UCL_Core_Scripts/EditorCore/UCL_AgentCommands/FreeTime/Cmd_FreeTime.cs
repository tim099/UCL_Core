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
                case "start": await StepStart(aPersona, GetArg(args, "until", "").Trim(), token); return;
                case "next":  await StepNext(aPersona, token, iEarlyEnd: false, iReason: null); return;
                case "end":   await StepNext(aPersona, token, iEarlyEnd: true, iReason: GetArg(args, "reason", "").Trim()); return;
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
        async UniTask StepStart(string iPersona, string iUntil, CancellationToken iToken)
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
                WritePayload(aPath, aR.ToString());
                throw new Exception($"[FreeTime] step=start blocked：persona 不在線（詳見 {aPath}）");
            }

            // 守衛②：until 必填且可解析
            DateTime aNow = DateTime.Now;
            if (!TryParseUntil(iUntil, aNow, out DateTime aUntil, out string aUntilErr))
            {
                aR.AppendLine("## blocked");
                aR.AppendLine($"- reason: {aUntilErr}");
                aR.AppendLine("- how: --arg until=<HH:mm 本地時刻>（例 until=12:30；深夜跨日自動判定）");
                WritePayload(aPath, aR.ToString());
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
                    WritePayload(aPath, aR.ToString());
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

            // 開場擲骰（全清單隨機排序；時間感知排序＋直播感知：直播中 stream-watch 鎖第 1 位不強制）
            int aMinutes = (int)Math.Max(0, (aUntil - aNow).TotalMinutes);
            var (aList, aSource, aIsLive) = RollActivities(aMinutes);

            // 酒館開場宣告（單則：時段＋像素額度＋骰面 —— in-process 走 Cmd_Tavern，計酬/mirror 全沿用）
            var aBody = new StringBuilder();
            aBody.AppendLine($"🎫 [{iPersona} 大小姐] 進入自由時間 — 至 **{aUntil:HH:mm}**（約 {aMinutes} 分鐘）｜🎨 免費像素 {FREE_PIXELS_PER_SESSION} 顆已發放（本場有效，用不完歸零）");
            aBody.AppendLine();
            if (aIsLive) aBody.AppendLine("📺 Tim 直播中 — 「觀看直播」鎖定第 1 位（不強制；選它 → /ucl-stream-watch）");
            aBody.AppendLine("開場擲骰 🎲 全清單隨機排序（僅供參考 — 自由意志優先）：");
            for (int i = 0; i < aList.Count; i++) aBody.AppendLine($"{i + 1}. {aList[i].name}");
            aBody.AppendLine();
            aBody.AppendLine($"[{aSource}] 活動事件結束時跑 step=next 換骰面，時間到自動收工。");
            int aSeq = await TavernPost(iPersona, aBody.ToString(), "dice-roll-entry", iToken);

            // 回傳檔：三個時間欄（拍板：時間感由 Cmd 供給）＋骰面（附活動 md 實路徑 —— 傳遞不反推）＋ next
            AppendTimeFields(aR, aNow, aUntil);
            aR.AppendLine($"- session: `{aSessionId}`（state: `{SessionPath(iPersona)}`）");
            aR.AppendLine($"- 免費像素: **{FREE_PIXELS_PER_SESSION} 顆**（canvas.py place --pay auto 自動優先用；per-session 清零{(aPrevForfeit > 0 ? $"，上場作廢 {aPrevForfeit} 顆" : "")}）");
            aR.AppendLine($"- 酒館開場宣告: {(aSeq > 0 ? $"seq **{aSeq}**" : "未發（best-effort，不影響 session）")}");
            AppendDiceSection(aR, aList, aSource, aIsLive);
            aR.AppendLine("## next");
            aR.AppendLine("1. 從骰面挑活動開做（無明確意圖 → 前 3 名挑一；有明確意圖 → 自由意志優先，但開場 post 註明「本輪未跟骰」）。");
            aR.AppendLine("2. **維持對話流＝發動引擎**：酒館 op=post 帶 `--wait-reply <秒>`（Cmd 管時鐘，不管 turn 存續 —— 沒引擎照樣睡死）。");
            aR.AppendLine($"3. **活動事件自然結束時**（棋局終局／繪圖收筆／聊天告一段落）→ run_cmd.py run FreeTime --arg step=next --arg persona={iPersona}");
            aR.AppendLine("   收工由這裡自動判定 —— **截止是軟的**：時間到不打斷進行中的活動，最後一件做完跑 next 才通知收工。");
            aR.AppendLine($"4. step=end（提前收工）**除非 Tim 明確指示，不要用** —— 正常結束一律交給 step=next 對時鐘判定。");
            WritePayload(aPath, aR.ToString());
            Debug.Log($"[FreeTime] step=start 完成 session={aSessionId} → {aPath}");
        }

        // ===========================================================
        // 區塊：step=next / step=end — 活動邊界檢查點（到期判定在此對系統時鐘）
        // 物理意義：next 由「活動事件自然結束」觸發（Tim 拍板）——未到期＝重擲換下一件、
        //          到期＝收工；end＝人主動提前收工（reason 可觀測，不靜默）。
        //          「過期的 session 再 next 一次」必須是收工不是報錯（卡點 3 —— 超時回來的人
        //          要有出口）。
        // ===========================================================
        async UniTask StepNext(string iPersona, CancellationToken iToken, bool iEarlyEnd, string iReason)
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
                WritePayload(aPath, aR.ToString());
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
                int aSeq = await TavernPost(iPersona, aBody.ToString(), iEarlyEnd ? "session-end-early" : "session-end", iToken);

                AppendTimeFields(aR, aNow, aUntil);
                aR.AppendLine(aExpired && !iEarlyEnd ? "- ⏰ **時間到** —— session 已收工" : "- 🏁 提前收工 —— session 已收工");
                aR.AppendLine($"- end_reason: {aEndReason}");
                aR.AppendLine($"- 本場輪次: {aRounds}");
                aR.AppendLine($"- 免費像素: 用 {aUsed} 顆{(aForfeited > 0 ? $"、作廢 {aForfeited} 顆（per-session 清零）" : "（全數用畢）")}");
                aR.AppendLine($"- 收工宣告: {(aSeq > 0 ? $"seq **{aSeq}**" : "未發（best-effort）")}");
                aR.AppendLine("## next");
                aR.AppendLine("- 回工作；或走晚安流程：run_cmd.py run GoodNight --arg step=check --arg persona=" + iPersona);
                aR.AppendLine("- 還想花錢再睡 →（可選）ucl-spending-time（不綁死晚安）。");
                WritePayload(aPath, aR.ToString());
                Debug.Log($"[FreeTime] step={aStepName} 收工（{aEndReason}） → {aPath}");
                return;
            }

            // 未到期：輪次 +1、重擲骰（時間感知）、宣告、回傳新骰面＋剩餘時間＋像素餘額
            int aRound = ReadInt(aSession, "rounds") + 1;
            aSession["rounds"] = new JsonData(aRound);
            AtomicWrite(SessionPath(iPersona), aSession.ToJsonBeautify());

            int aRemain = (int)Math.Max(0, (aUntil - aNow).TotalMinutes);
            var (aList, aSource, aIsLive) = RollActivities(aRemain);
            (int aGranted, int aUsedNow) = ReadFreePixelUsage(iPersona);
            // 末段判定（apex-one seq 11180：剩最後幾分鐘時，next 該給的不是新骰面）——
            // 量了剩餘時間就要用在建議上，否則 Cmd 的建議沒有鑑別力。
            bool aTail = aRemain < 5;

            var aDiceBody = new StringBuilder();
            aDiceBody.AppendLine($"🎲 [{iPersona} 大小姐] 自由時間第 {aRound} 輪換骰（至 {aUntil:HH:mm}，剩約 {aRemain} 分）：");
            if (aTail)
                aDiceBody.AppendLine($"⏳ **剩 {aRemain} 分 —— 不建議起新活動**。收尾現有的；最後一件做完再跑 step=next 收工。");
            else
            {
                if (aIsLive) aDiceBody.AppendLine("📺 Tim 直播中 — 「觀看直播」鎖定第 1 位（不強制）");
                for (int i = 0; i < Math.Min(3, aList.Count); i++) aDiceBody.AppendLine($"{i + 1}. {aList[i].name}");
                aDiceBody.AppendLine($"（前 3 名；全清單 {aList.Count} 項｜跟沒跟骰照舊酒館可觀測）");
            }
            int aDiceSeq = await TavernPost(iPersona, aDiceBody.ToString(), "dice-roll", iToken);

            AppendTimeFields(aR, aNow, aUntil);
            aR.AppendLine($"- 輪次: **{aRound}**");
            aR.AppendLine($"- 免費像素: 已用 {aUsedNow}/{aGranted}");
            aR.AppendLine($"- 換骰宣告: {(aDiceSeq > 0 ? $"seq **{aDiceSeq}**" : "未發（best-effort）")}");
            if (aTail)
            {
                aR.AppendLine($"## dice（末段 —— 剩 {aRemain} 分）");
                aR.AppendLine($"- ⏳ **不建議起新活動**（新骰面已略 —— 在任何剩餘時間下都輸出同一份建議的 Cmd，建議沒有鑑別力）。");
                aR.AppendLine("- 收尾現有的活動或對話；最後一件做完再跑 step=next，由 Cmd 判定收工。");
            }
            else AppendDiceSection(aR, aList, aSource, aIsLive);
            aR.AppendLine("## next");
            aR.AppendLine("1. 從骰面挑下一件活動（跟骰規則同 start）；引擎（--wait-reply）持續掛著。");
            aR.AppendLine("2. 活動事件自然結束 → 再跑 step=next（**截止是軟的**：時間到不打斷進行中活動，最後一件做完跑 next 才通知收工）。");
            aR.AppendLine("3. step=end（提前收工）除非 Tim 明確指示，不要用。");
            WritePayload(aPath, aR.ToString());
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
        struct ActivityInfo { public string id; public string name; public string how; public string path; public int minMinutes; }

        /// <summary>
        /// 擲骰＋時間感知排序（Tim 2026-08-13 補拍，源自 apex-one seq 11180 實跑回饋）：
        /// 活動 md 選填 `min_minutes`（建議所需分鐘，如 TRPG 20）——剩餘時間不足的活動
        /// **排到清單尾端並標明時間不夠**（不隱藏 —— 資訊不丟，判斷已由 Cmd 代做）。
        /// 「一個在任何剩餘時間下都輸出同一份建議的 Cmd，它的建議沒有鑑別力」。
        /// </summary>
        static (List<ActivityInfo> list, string source, bool isLive) RollActivities(int iRemainMinutes)
        {
            string aCoreRel = UCL_EditorPath.CorePath;
            string aSharedDir = string.IsNullOrEmpty(aCoreRel) ? null
                : Path.GetFullPath(Path.Combine(UCL_RepoPath.UnityProjectRoot, aCoreRel, "Docs~/zh-Hant/FreeTime/Activities"));
            string aProjectDir = Path.GetFullPath(Path.Combine(UCL_RepoPath.RepoRoot, "docs/FreeTime/Activities"));

            var aMerged = new Dictionary<string, (ActivityInfo info, bool enabled)>();
            int aSharedCount = 0, aProjectCount = 0;
            ScanActivityDir(aSharedDir, aMerged);
            var aSharedIds = new HashSet<string>(aMerged.Keys);
            ScanActivityDir(aProjectDir, aMerged);   // 同 id 專案層覆蓋（含停用覆蓋）

            var aList = new List<ActivityInfo>();
            foreach (var kv in aMerged)
            {
                if (!kv.Value.enabled) continue;
                aList.Add(kv.Value.info);
                if (aSharedIds.Contains(kv.Key) && kv.Value.info.path != null
                    && aProjectDir != null && kv.Value.info.path.StartsWith(aProjectDir, StringComparison.OrdinalIgnoreCase))
                    aProjectCount++;   // 專案層覆蓋共用層的算專案
                else if (aSharedIds.Contains(kv.Key)) aSharedCount++;
                else aProjectCount++;
            }

            // Fisher-Yates（System.Random —— 擲骰不需要密碼學強度）
            var aRng = new System.Random();
            for (int i = aList.Count - 1; i > 0; i--)
            {
                int j = aRng.Next(i + 1);
                (aList[i], aList[j]) = (aList[j], aList[i]);
            }

            // 時間感知：min_minutes > 剩餘 → 移到尾端＋名字標「時間不夠」（量了就要用在輸出上）
            if (iRemainMinutes > 0)
            {
                var aFit = new List<ActivityInfo>();
                var aTooLong = new List<ActivityInfo>();
                foreach (var a in aList)
                {
                    if (a.minMinutes > 0 && a.minMinutes > iRemainMinutes)
                    {
                        var aDeco = a;
                        aDeco.name = $"{a.name} ⏳（建議 ≥{a.minMinutes} 分，剩 {iRemainMinutes} 分 —— 本場時間不夠）";
                        aTooLong.Add(aDeco);
                    }
                    else aFit.Add(a);
                }
                aFit.AddRange(aTooLong);
                aList = aFit;
            }

            // 直播感知：旗標＋控制開關對帳（孤兒旗標血證 2026-07-30 —— enabled=false 視為沒直播）
            bool aIsLive = TryGetLiveTitle(out string aLiveTitle);
            if (aIsLive)
            {
                int aIdx = aList.FindIndex(a => a.id == "stream-watch");
                if (aIdx < 0) aIsLive = false;
                else
                {
                    var aDeco = aList[aIdx];
                    aDeco.name = string.IsNullOrEmpty(aLiveTitle) ? $"{aDeco.name} (直播中)" : $"{aDeco.name} 本場節目: {aLiveTitle}";
                    aList.RemoveAt(aIdx);
                    aList.Insert(0, aDeco);
                }
            }
            return (aList, $"UCL_Core 共用 {aSharedCount} + 專案 {aProjectCount}", aIsLive);
        }

        static void ScanActivityDir(string iDir, Dictionary<string, (ActivityInfo, bool)> ioMerged)
        {
            if (string.IsNullOrEmpty(iDir) || !Directory.Exists(iDir)) return;
            foreach (var aMd in Directory.GetFiles(iDir, "*.md"))
            {
                if (Path.GetFileName(aMd).StartsWith("_")) continue;   // _README.md 等說明檔不算活動
                try
                {
                    string aId = UCL_AwakeningService.ReadFrontmatterField(aMd, "id") ?? Path.GetFileNameWithoutExtension(aMd);
                    string aName = UCL_AwakeningService.ReadFrontmatterField(aMd, "name") ?? Path.GetFileNameWithoutExtension(aMd);
                    string aHow = UCL_AwakeningService.ReadFrontmatterField(aMd, "how") ?? "";
                    bool aEnabled = !string.Equals(UCL_AwakeningService.ReadFrontmatterField(aMd, "enabled") ?? "true",
                        "false", StringComparison.OrdinalIgnoreCase);
                    // min_minutes（選填）：活動建議所需分鐘 —— 剩餘時間不足時排尾標明（不隱藏）
                    int aMinMinutes = 0;
                    int.TryParse(UCL_AwakeningService.ReadFrontmatterField(aMd, "min_minutes") ?? "", out aMinMinutes);
                    ioMerged[aId] = (new ActivityInfo { id = aId, name = aName, how = aHow, path = aMd, minMinutes = aMinMinutes }, aEnabled);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[FreeTime] 活動 md 讀取失敗，跳過：{aMd}（{e.Message}）");
                }
            }
        }

        static bool TryGetLiveTitle(out string oTitle)
        {
            oTitle = null;
            try
            {
                string aInfoPath = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", "_live_info.json");
                if (!File.Exists(aInfoPath)) return false;
                string aCfgPath = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", "_config.json");
                if (File.Exists(aCfgPath))
                {
                    var aCfg = JsonData.ParseJson(File.ReadAllText(aCfgPath, Encoding.UTF8));
                    if (aCfg != null && aCfg.Contains("enabled") && !(bool)aCfg["enabled"]) return false;   // 旗標是殘留不是事實
                }
                var aInfo = JsonData.ParseJson(File.ReadAllText(aInfoPath, Encoding.UTF8));
                if (aInfo != null && aInfo.Contains("stream_title")) oTitle = aInfo["stream_title"].ToString();
                return true;
            }
            catch (Exception) { return false; }   // fail-soft：誤判沒直播只少一個推薦，誤判有直播會誤導三個 persona
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
        static async UniTask<int> TavernPost(string iPersona, string iBody, string iSubtag, CancellationToken iToken)
        {
            try
            {
                var aLock = UCL_AwakeningService.ReadLock(iPersona);
                if (aLock == null || string.IsNullOrEmpty(aLock.bank_account))
                {
                    Debug.LogWarning($"[FreeTime] lock 讀不到 bank（宣告跳過）persona={iPersona}");
                    return 0;
                }
                var aArgs = new Dictionary<string, string>
                {
                    { "op", "post" },
                    { "room", "tavern" },
                    { "agent", aLock.bank_account },
                    { "persona", iPersona },
                    { "body", iBody },
                    { "meta", $"{{\"tag\":\"free-time\",\"subtag\":\"{iSubtag}\",\"category\":\"chat\"}}" },
                };
                if (!string.IsNullOrEmpty(aLock.session_token)) aArgs["session_token"] = aLock.session_token;
                ChatTavern.Cmd_Tavern.LastPostSeq = 0;
                await new ChatTavern.Cmd_Tavern().ExecuteAsync(aArgs, iToken);
                return ChatTavern.Cmd_Tavern.LastPostSeq;
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

        static void AppendDiceSection(StringBuilder ioR, List<ActivityInfo> iList, string iSource, bool iIsLive)
        {
            ioR.AppendLine("## dice（隨機排序，僅供參考 — 自由意志優先；無明確意圖從前 3 挑）");
            if (iIsLive) ioR.AppendLine("- 📺 直播中 — stream-watch 鎖第 1 位（不強制）");
            for (int i = 0; i < iList.Count; i++)
            {
                var a = iList[i];
                ioR.AppendLine($"{i + 1}. **{a.name}**{(string.IsNullOrEmpty(a.how) ? "" : $" — {a.how}")}");
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

        static void WritePayload(string iPath, string iReport)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iPath));
                File.WriteAllText(iPath, iReport, new UTF8Encoding(false));
                // 回報產出檔 → result 檔 outputs 欄，run_cmd 端隨 verdict 印路徑（不再靠 skill 背）
                UCL_AgentCommandRunner.ReportOutputFile(iPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FreeTime] 回傳落檔失敗 {iPath}: {e.Message}");
            }
        }
    }
}
#endif
