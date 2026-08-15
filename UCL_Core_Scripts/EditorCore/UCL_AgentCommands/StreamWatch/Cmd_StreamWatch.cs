// 區塊職責：Cmd_StreamWatch — 觀影模式的 Cmd 入口（Plan_StreamWatch_Cmd.md，Tim 2026-08-15 拍板全部規格）。
//          同一支 Cmd 以 step 分步：start（守衛＋media 鎖定＋註冊 ends_at＋開播公告）→
//          cycle（取素材＋到期/中斷判定＋狀態相依 next）→ observe → note。
// 物理意義：**沒有 step=end** —— agent 不能自己結束 session；兩個終止（到期／Tim 停錄影）
//          都由 cycle 對系統時鐘與 _screenstream/_config.json 的 enabled 判定。
//          「自動」指的是**判斷自動**（Cmd 算好告訴你），不是觸發自動 —— 不新增任何常駐偵測。
// 數值影響：session state 落 <DataRoot>/StreamWatch/sessions/<persona>.json（C# 唯一寫入端）；
//          回傳檔 letters/<persona>/_streamwatch_<step>.md（路徑經 ReportOutputFile 進 result outputs）。
// ⚠ 阻塞紀律（Tim 2026-08-15 指示 + WorkMemory/unitask-editor-async）：
//   縮圖牆是外部 process，**一律 await Task.Run 包起來**，不得在主執行緒輪詢 WaitForExit。
//   照抄 UCL_BartenderDaemon.RunBalanceQuery 會自動繼承它的同步性（那支因 out 參數不可能 async）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.JsonLib;
using UCL.Core.EditorLib.AgentCommands.Treasury;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.StreamWatch
{
    using Awakening;

    /// <summary>
    /// 觀影模式流程 Cmd（step 分步 + 每步回傳檔指下一步）。
    /// <para>正常流程：</para>
    /// <code>
    /// ① run_cmd.py run StreamWatch --arg step=start --arg persona=&lt;P&gt; --arg until=&lt;HH:mm&gt; [--arg media=&lt;work&gt;]
    /// ② run_cmd.py run StreamWatch --arg step=cycle --arg persona=&lt;P&gt;      （取素材；到期/中斷判定在此）
    /// ③ Read 回傳檔給的縮圖牆與字幕路徑 → 寫評論
    /// ④ run_cmd.py run StreamWatch --arg step=observe --arg persona=&lt;P&gt; --arg-file body=&lt;評論&gt;
    /// </code>
    /// </summary>
    public class Cmd_StreamWatch : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "StreamWatch";

        public override string ShortDescription =>
            "觀影模式 Cmd（step=start/cycle/observe/note）。start 鎖定媒材＋註冊看到幾點；" +
            "cycle 自己合成縮圖牆並判定到期/中斷；**沒有 end —— agent 不能自己結束 session**。";

        public override string ArgsSchema =>
            "step=start|cycle|observe|note (必填) | persona=<name> — 全步驟必填 | " +
            "until=<HH:mm 本地> — start 必填 | media=<work-slug> — start 選填（不給則由 Cmd 問） | " +
            "body=<內文> — observe/note 必填（長文走 --arg-file） | " +
            "回傳落檔 letters/<persona>/_streamwatch_<step>.md（路徑隨 run_cmd verdict 印出）";

        public override string ExampleArgs => "step=start;persona=Template;until=23:59";

        public override string HelpURL => "ucl_core:Docs~/zh-Hant/Plan/Plan_StreamWatch_Cmd.md";

        /// <summary>每輪縮圖牆的格數上限（Tim 2026-08-15：一輪約讀 12–16 張）。</summary>
        const int MAX_TILES = 16;

        /// <summary>每場 observation 計酬上限（Plan §6：沒有上限的按量計酬就是印鈔許可證）。</summary>
        const int OBSERVATION_CAP = 12;

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string aStep = GetArg(args, "step", "").Trim().ToLowerInvariant();
            string aPersona = GetArg(args, "persona", "").Trim();
            if (string.IsNullOrEmpty(aPersona))
                throw new Exception($"[StreamWatch] persona 必填。ArgsSchema: {ArgsSchema}");

            switch (aStep)
            {
                case "start": await StepStart(aPersona, GetArg(args, "until", "").Trim(),
                                              GetArg(args, "media", "").Trim(), token); return;
                case "cycle": await StepCycle(aPersona, token); return;
                case "observe": await StepObserve(aPersona, GetArg(args, "body", ""), token); return;
                case "note": await StepNote(aPersona, GetArg(args, "body", ""), token); return;
                default:
                    throw new Exception($"[StreamWatch] step 必為 start|cycle|observe|note（join 施工中，got '{aStep}'）。ArgsSchema: {ArgsSchema}");
            }
        }

        // ===========================================================
        // 區塊：step=start — 守衛 → media 鎖定 → session 註冊 → 開播公告
        // 物理意義：media_id 是**共享鍵**（Plan §4）：先查既有 work，命中就用；沒命中才建新的並回報；
        //          不確定就 blocked 問人 —— 憑印象取 slug 正是製造 work 分裂的那一步，
        //          而分裂之後既有 reader 的心得對新場次永遠隱形且不會有錯誤訊息。
        // ===========================================================
        async UniTask StepStart(string iPersona, string iUntil, string iMedia, CancellationToken iToken)
        {
            string aPath = PayloadPath(iPersona, "start");
            var aR = new StringBuilder();
            aR.AppendLine($"# StreamWatch step=start persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            // 守衛①：必須在線
            if (!UCL_AwakeningService.IsOnline(iPersona))
            {
                Blocked(aR, aPath, $"'{iPersona}' 不在線（無 session lock）",
                        $"先跑 run_cmd.py run GoodMorning --arg step=wake --arg persona={iPersona}");
                throw new Exception($"[StreamWatch] step=start blocked：persona 不在線（詳見 {aPath}）");
            }

            // 守衛②：until 必填且可解析
            DateTime aNow = DateTime.Now;
            if (!TryParseUntil(iUntil, aNow, out DateTime aUntil, out string aUntilErr))
            {
                Blocked(aR, aPath, aUntilErr, "--arg until=<HH:mm 本地時刻>（例 until=23:30；深夜跨日自動判定）");
                throw new Exception($"[StreamWatch] step=start blocked：until 無效（詳見 {aPath}）");
            }

            // 守衛③：不疊開（到期殘留自動收掉 —— 沒跑 cycle 的人不該被卡死在沒有出口的房間）
            var aOld = LoadSession(iPersona);
            if (aOld != null && ReadBool(aOld, "active"))
            {
                DateTime? aOldEnd = ParseIsoLocal(ReadStr(aOld, "end_ts"));
                if (aOldEnd.HasValue && aNow <= aOldEnd.Value)
                {
                    Blocked(aR, aPath, $"已有進行中的觀影 session（至 {aOldEnd.Value:HH:mm} 本地）—— 不疊開",
                            $"跑 step=cycle 繼續；到期或 Tim 停錄影時 cycle 會自己判定收工");
                    throw new Exception($"[StreamWatch] step=start blocked：session 已存在（詳見 {aPath}）");
                }
                aR.AppendLine($"- ℹ 偵測到過期殘留 session（{ReadStr(aOld, "session_id")}）已自動收掉，開新場。");
            }

            // 守衛④：media 是共享鍵 —— 不給就 blocked，不猜
            if (string.IsNullOrEmpty(iMedia))
            {
                var aExisting = ListExistingWorks();
                aR.AppendLine("## blocked");
                aR.AppendLine("- reason: 未指定 media —— **媒材身分是共享鍵，不能由記憶供給**");
                aR.AppendLine("- why: 同一部片被兩個人取成兩個 slug ⇒ work 裂開，既有 reader 的心得對新場次**永遠隱形且不會報錯**");
                aR.AppendLine("- how: --arg media=<work-slug>；**先看下面既有清單有沒有這部**，有就用它，沒有才建新的");
                aR.AppendLine();
                aR.AppendLine($"### 既有 work（{aExisting.Count} 筆 — 命中就用，不要另取新名）");
                foreach (var w in aExisting) aR.AppendLine($"- `{w}`");
                aR.AppendLine();
                aR.AppendLine("⚠ 片名不確定 ⇒ **問 Tim，不要猜**。");
                WritePayload(aPath, aR.ToString());
                throw new Exception($"[StreamWatch] step=start blocked：media 未指定（詳見 {aPath}）");
            }

            bool aIsNewWork = !WorkExists(iMedia);

            // session 註冊（C# 唯一寫入端）
            string aSessionId = $"sw-{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{iPersona}";
            var aSession = new JsonData();
            aSession["persona"] = new JsonData(iPersona);
            aSession["session_id"] = new JsonData(aSessionId);
            aSession["role"] = new JsonData("primary");
            aSession["media_id"] = new JsonData(iMedia);
            aSession["start_ts"] = new JsonData(UCL_AwakeningService.NowIso());
            aSession["end_ts"] = new JsonData(aUntil.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            aSession["until_local"] = new JsonData(aUntil.ToString("yyyy-MM-dd HH:mm"));
            aSession["cursor_epoch"] = new JsonData(0);   // 0 = 尚未取材，首輪由 montage 決定窗口
            aSession["cycles"] = new JsonData(0);
            aSession["observations"] = new JsonData(0);
            aSession["tiles_total"] = new JsonData(0);
            aSession["start_seq"] = new JsonData(0);
            aSession["end_seq"] = new JsonData(0);
            aSession["note_written"] = new JsonData(false);
            aSession["active"] = new JsonData(true);
            aSession["settled_at"] = new JsonData("");
            aSession["end_reason"] = new JsonData("");
            AtomicWrite(SessionPath(iPersona), aSession.ToJsonBeautify());

            // 開播公告（記 start_seq —— 匯出區間的左端點，寫入當下就知道，不必事後回頭數）
            int aMinutes = (int)Math.Max(0, (aUntil - aNow).TotalMinutes);
            var aBody = new StringBuilder();
            aBody.AppendLine($"📺 [{iPersona} 大小姐] 開播觀影 — 看到 **{aUntil:HH:mm}**（約 {aMinutes} 分鐘）｜媒材 `{iMedia}`{(aIsNewWork ? " ⚠ **新 work**" : "")}");
            aBody.AppendLine();
            aBody.AppendLine("陪同觀眾可跑 `step=join` 加入（挑段細看；主劇情由本場主觀影者在酒館帶）。");
            int aSeq = await TavernPost(iPersona, aBody.ToString(), "watch-start", iToken);
            if (aSeq > 0)
            {
                aSession["start_seq"] = new JsonData(aSeq);
                AtomicWrite(SessionPath(iPersona), aSession.ToJsonBeautify());
            }

            // 回傳檔
            aR.AppendLine($"- session: `{aSessionId}`（state: `{SessionPath(iPersona)}`）");
            aR.AppendLine($"- media: `{iMedia}`{(aIsNewWork ? "　⚠ **這是新 work** —— 若這部片其實已存在於 Library，現在喊停比事後合併便宜" : "　✅ 命中既有 work")}");
            aR.AppendLine($"- 看到: {aUntil:HH:mm}（約 {aMinutes} 分鐘）");
            aR.AppendLine($"- 開播公告: {(aSeq > 0 ? $"seq **{aSeq}**（匯出區間左端點）" : "未發（best-effort，不影響 session）")}");
            AppendRetentionLine(aR);
            aR.AppendLine();
            aR.AppendLine("## next");
            aR.AppendLine($"1. **取素材**：run_cmd.py run StreamWatch --arg step=cycle --arg persona={iPersona}");
            aR.AppendLine("2. 依回傳檔給的**絕對路徑** Read 縮圖牆與字幕 → 寫觀戰評論");
            aR.AppendLine($"3. **發評論**：run_cmd.py run StreamWatch --arg step=observe --arg persona={iPersona} --arg-file body=<評論>");
            aR.AppendLine("4. 回到 1 —— **收工不用你判斷**：到期或 Tim 停錄影時，cycle 會告訴你並提示寫接續點。");
            WritePayload(aPath, aR.ToString());
            Debug.Log($"[StreamWatch] step=start 完成 session={aSessionId} media={iMedia} → {aPath}");
        }

        // ===========================================================
        // 區塊：step=cycle — 取素材 ＋ 到期/中斷判定 ＋ 狀態相依 next
        // 物理意義：本步是**唯一的終止判定點**。判定只認兩個顯式事實：系統時鐘、_config.json 的 enabled。
        // ⚠ 中斷判定**不推論 frame 新鮮度**：實測活樣本 enabled=false 而 994 張 frame 仍在磁碟上 ——
        //   「錄影停了」與「frame 沒變新」是兩件事，用後者推論會把 daemon 打嗝讀成中斷而誤殺 session。
        // ===========================================================
        async UniTask StepCycle(string iPersona, CancellationToken iToken)
        {
            string aPath = PayloadPath(iPersona, "cycle");
            var aR = new StringBuilder();
            aR.AppendLine($"# StreamWatch step=cycle persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            var aS = LoadSession(iPersona);
            if (aS == null || !ReadBool(aS, "active"))
            {
                Blocked(aR, aPath, "無進行中的觀影 session",
                        $"先跑 run_cmd.py run StreamWatch --arg step=start --arg persona={iPersona} --arg until=<HH:mm> --arg media=<work>");
                throw new Exception($"[StreamWatch] step=cycle blocked：無 active session（詳見 {aPath}）");
            }

            DateTime aNow = DateTime.Now;
            DateTime? aEnd = ParseIsoLocal(ReadStr(aS, "end_ts"));
            bool aExpired = aEnd.HasValue && aNow >= aEnd.Value;
            bool aRecordingOff = !IsRecordingEnabled(out string aCfgNote);

            // ── 終止判定（唯一的一處）───────────────────
            if (aExpired || aRecordingOff)
            {
                bool aByInterrupt = aRecordingOff;
                string aReason = aByInterrupt ? "Tim 停止錄影（_config.json enabled=false）" : "到期";
                aR.AppendLine("## 收工判定");
                aR.AppendLine($"- 判定: **{aReason}**");
                aR.AppendLine($"- 依據: {(aByInterrupt ? aCfgNote : $"now={aNow:HH:mm:ss} >= ends_at={aEnd:HH:mm:ss}")}");
                aR.AppendLine("- ⚠ 本判定只認**顯式狀態**（系統時鐘／`enabled` 欄位），不推論 frame 新鮮度。");
                aR.AppendLine();

                // 接續點未寫 ⇒ **不擋**（Tim 拍板），但要**吵**：這裡列、收播公告也列
                if (!ReadBool(aS, "note_written"))
                {
                    aR.AppendLine("⚠ **本場未寫接續點** —— 不擋結算，但下次續看接不回進度。");
                    aR.AppendLine($"   要補：run_cmd.py run StreamWatch --arg step=note --arg persona={iPersona} --arg-file body=<接續點>");
                    aR.AppendLine("   （至少要有：看到哪／下次從哪接／人物與伏筆狀態）");
                    aR.AppendLine();
                }

                await SettleAsync(iPersona, aS, aByInterrupt, aNow, aEnd, aR, iToken);
                WritePayload(aPath, aR.ToString());
                Debug.Log($"[StreamWatch] step=cycle 收工結算（{aReason}）→ {aPath}");
                return;
            }

            // ── 進行中：取素材 ──────────────────────────────────
            // ⚠ CorePath 走 AssetDatabase，**main-thread-only** ⇒ 路徑必須在 await 之前解析完
            //   （Cmd_GoodMorning.cs:86 同樣的坑）。
            string aScript = ResolveMontageScript();
            string aOutPath = MontageOutPath(iPersona);
            double aCursor = ReadDouble(aS, "cursor_epoch");

            if (string.IsNullOrEmpty(aScript))
            {
                Blocked(aR, aPath, "解析不到 screenstream_montage.py（CorePath 空或檔案不存在）",
                        "確認 UCL_Core 掛載位置與 Tools~/AgentCommands/screenstream_montage.py 是否存在");
                throw new Exception($"[StreamWatch] step=cycle blocked：找不到縮圖牆工具（詳見 {aPath}）");
            }

            var (aOcrOn, aSttOn) = ReadSensorFlags();
            DateTime aRunStart = DateTime.Now;
            var (aOk, aStdout, aErr) = await RunMontageAsync(aScript, aCursor, aOutPath, aOcrOn, aSttOn, iToken);
            if (!aOk)
            {
                aR.AppendLine("## blocked");
                aR.AppendLine($"- reason: 縮圖牆合成失敗 — {aErr}");
                aR.AppendLine("- exit: 確認 ScreenStream 是否有 frame；重跑 step=cycle");
                WritePayload(aPath, aR.ToString());
                throw new Exception($"[StreamWatch] step=cycle blocked：montage 失敗（詳見 {aPath}）");
            }

            var aInfo = ParseMontageReport(aStdout);

            // 推進 cursor（用 report 的 next-cursor —— 不是 wall-clock，抖動下仍首尾嚴絲合縫）
            if (aInfo.NextCursor > 0)
            {
                aS["cursor_epoch"] = new JsonData(aInfo.NextCursor);
            }
            aS["cycles"] = new JsonData(ReadInt(aS, "cycles") + 1);
            aS["tiles_total"] = new JsonData(ReadInt(aS, "tiles_total") + aInfo.Tiles);
            aS["last_tiles"] = new JsonData(aInfo.Tiles);
            aS["last_span_seconds"] = new JsonData(aInfo.SpanSeconds);
            AtomicWrite(SessionPath(iPersona), aS.ToJsonBeautify());

            int aRemain = aEnd.HasValue ? (int)Math.Max(0, (aEnd.Value - aNow).TotalMinutes) : 0;
            string aSubPath = Path.ChangeExtension(aOutPath, ".subtitles.md");
            // ⚠ **只驗存在會被殘留檔騙**（2026-08-15 首跑實證：字幕檔是四天前 08-11 的，
            //   而 File.Exists 照樣回 true ⇒ 回傳檔把它當成本輪字幕端給 agent 讀）。
            //   同族血證：RunBrief 的「檔存在且行數>0」被隔夜殘留滿足（wake#49）。
            //   ⇒ 判準改成 **mtime 必須晚於本輪起跑**。壞要往吵的方向壞：寧可說沒有，不可端舊的。
            bool aHasSub = false;
            string aSubNote = "";
            try
            {
                if (File.Exists(aSubPath))
                {
                    DateTime aSubMtime = File.GetLastWriteTime(aSubPath);
                    if (aSubMtime >= aRunStart.AddSeconds(-1)) aHasSub = true;
                    else aSubNote = $"（磁碟上有一份 {aSubMtime:MM-dd HH:mm} 的殘留檔 —— **不是本輪的，已忽略**）";
                }
            }
            catch { }

            aR.AppendLine("## 本輪素材");
            aR.AppendLine($"- 縮圖牆   : `{aOutPath}`　← 直接 Read");
            aR.AppendLine(aHasSub
                ? $"- 字幕     : `{aSubPath}`　← 直接 Read（**本輪產出**，mtime 已驗）"
                : $"- 字幕     : **本輪無字幕**（不是欄位不存在 —— 這一輪確實沒有）{aSubNote}");
            aR.AppendLine($"- 涵蓋     : {aInfo.SpanText}");
            aR.AppendLine($"- 格數     : {aInfo.Tiles}　**每格 ≈{aInfo.PerTileSeconds:F0}s**　← 落後越多每格越粗，丟的是細節不是時間");
            AppendRetentionLine(aR);
            aR.AppendLine($"- 感官     : OCR {(aOcrOn ? "開" : "關")}／STT {(aSttOn ? "開" : "關")}（讀自 _config.json，**不必傳旗標**）");
            aR.AppendLine($"- 剩餘     : {aRemain} 分鐘（到 {aEnd:HH:mm}）");
            aR.AppendLine($"- 本場累計 : cycles={ReadInt(aS, "cycles")}｜observations={ReadInt(aS, "observations")}");
            aR.AppendLine();
            aR.AppendLine("## next");
            aR.AppendLine($"1. Read 上面的縮圖牆{(aHasSub ? "與字幕" : "")}路徑");
            aR.AppendLine($"2. run_cmd.py run StreamWatch --arg step=observe --arg persona={iPersona} --arg-file body=<你的評論>");
            aR.AppendLine($"3. 之後再跑 step=cycle —— **收工不用你判斷**，時間到或 Tim 停錄影時這一步會告訴你。");
            WritePayload(aPath, aR.ToString());
            Debug.Log($"[StreamWatch] step=cycle tiles={aInfo.Tiles} span={aInfo.SpanSeconds:F0}s → {aPath}");
        }

        // ===========================================================
        // 區塊：結算 ＋ 收播公告（Plan §6）
        // 物理意義：費率以帳上真的在跑的東西為錨 —— 一筆 commit ＝ 5 token。
        //   在場費 1 token/10 分鐘（上限 6，僅 primary）＋ observation 1 token/筆（上限 12）。
        //   **零 observation ⇒ 在場費也不發** —— phantom 守衛：
        //   否則「在場」變成一個掛著就能滿足的訊號（phantom-alive 的計酬版）。
        // ⚠ paid_min 的上限方向：
        //   到期 ⇒ 算到 **ends_at**（不是 agent 回來呼叫的時間，否則回得越晚領越多）
        //   中斷 ⇒ 算到 **中斷被發現的時刻**（沒看的不能領）
        // ⚠ 判重兩層：熱路徑讀 session 的 settled_at（本來就在讀那個檔，零額外成本）；
        //   寫入閘讀 ledger 的 useRef（事實源）。代理可以錯，只要它錯的方向是「多問一次」。
        // ===========================================================
        const int BASE_MINUTES_PER_TOKEN = 10;
        const int BASE_CAP = 6;

        static async UniTask SettleAsync(string iPersona, JsonData ioS, bool iByInterrupt,
                                         DateTime iNow, DateTime? iEnd, StringBuilder ioR, CancellationToken iToken)
        {
            // 熱路徑判重
            string aSettledAt = ReadStr(ioS, "settled_at");
            if (!string.IsNullOrEmpty(aSettledAt))
            {
                ioR.AppendLine($"- 結算: **已於 {aSettledAt} 結算過**（熱路徑判重，未重複發薪）");
                ioR.AppendLine();
                ioR.AppendLine("## next");
                ioR.AppendLine("1. 本場已收工。要再看請跑 step=start 開新場。");
                return;
            }

            string aSessionId = ReadStr(ioS, "session_id");
            string aMedia = ReadStr(ioS, "media_id");
            int aObs = ReadInt(ioS, "observations");
            DateTime? aStart = ParseIsoLocal(ReadStr(ioS, "start_ts"));
            // ⚠ 上限**永遠**是 ends_at —— 兩個終止條件可能同時成立（到期了、Tim 也停了錄影），
            //   而中斷被發現的時刻可以晚於截止。取中斷時刻 ⇒ **回得越晚領越多**，
            //   正是 Plan §6 點名要防的那條。2026-08-15 首次結算實踩：
            //   start 17:06 / ends_at 17:15 / 中斷發現於 17:20 ⇒ 誤算 14 分（應為 8 分），多發 1 token。
            //   ⇒ 兩者取小：沒看的不能領，過了截止的也不能領。
            DateTime aPaidUntil = iByInterrupt ? iNow : (iEnd ?? iNow);
            if (iEnd.HasValue && aPaidUntil > iEnd.Value) aPaidUntil = iEnd.Value;
            int aPaidMin = aStart.HasValue ? (int)Math.Max(0, (aPaidUntil - aStart.Value).TotalMinutes) : 0;

            int aObsPay = Math.Min(aObs, OBSERVATION_CAP);
            int aBasePay = Math.Min(aPaidMin / BASE_MINUTES_PER_TOKEN, BASE_CAP);
            bool aPhantom = aObs <= 0;
            if (aPhantom) aBasePay = 0;
            int aTotal = aBasePay + aObsPay;

            string aPayNote;
            if (aPhantom)
            {
                aPayNote = "**未發薪** —— 本場 0 筆 observation（phantom 守衛：在場費也不發）";
                aTotal = 0;
            }
            else
            {
                var aRes = UCL_TreasuryAccountResolver.Resolve(iPersona);
                if (aRes.IsUnresolved || string.IsNullOrEmpty(aRes.AccountId))
                {
                    aPayNote = $"**未發薪** —— persona `{iPersona}` 解析不到正式帳號（{aRes.Trace}）";
                    aTotal = 0;
                }
                else if (AlreadyCredited($"streamwatch-{aSessionId}"))
                {
                    aPayNote = $"**未重複發薪** —— ledger 已有 `streamwatch-{aSessionId}`（寫入閘判重，事實源）";
                    aTotal = 0;
                }
                else
                {
                    try
                    {
                        UCL_TreasuryLedger.Credit(
                            accountId: aRes.AccountId, amount: aTotal,
                            sourceKind: "stream_watch",
                            sourceRef: $"streamwatch-{aSessionId}",
                            description: $"觀影結算 {aMedia}：在場 {aPaidMin} 分→{aBasePay} ＋ observation {aObs}→{aObsPay}",
                            callerAgentId: "system", cmdId: $"streamwatch-{aSessionId}");
                        aPayNote = $"**+{aTotal} token** → `{aRes.AccountId}`（在場 {aPaidMin} 分＝{aBasePay}／observation {aObs} 筆＝{aObsPay}）";
                    }
                    catch (Exception e)
                    {
                        aPayNote = $"**發薪失敗** —— {e.Message}（session 仍關閉，帳待補）";
                        aTotal = 0;
                    }
                }
            }

            // 收播公告（記 end_seq —— 匯出區間右端點）
            var aBody = new StringBuilder();
            aBody.AppendLine($"📺 [{iPersona} 大小姐] 收播 — {(iByInterrupt ? "**Tim 停止錄影**" : "**到期**")}｜媒材 `{aMedia}`");
            aBody.AppendLine();
            aBody.AppendLine($"- 本場：{ReadInt(ioS, "cycles")} 輪 ／ **{aObs} 筆觀戰評論** ／ 在場 {aPaidMin} 分鐘");
            aBody.AppendLine($"- 結算：{aPayNote}");
            if (!ReadBool(ioS, "note_written"))
                aBody.AppendLine("- ⚠ **本場未寫接續點** —— 下次續看接不回進度（不擋結算，但這件事要看得見）");
            aBody.AppendLine($"- 場次紀錄：seq {ReadInt(ioS, "start_seq")} → 本則（`tavern` 房；中間混雜其他訊息是刻意的）");
            int aSeq = await TavernPost(iPersona, aBody.ToString(), "watch-end", iToken);

            ioS["active"] = new JsonData(false);
            ioS["settled_at"] = new JsonData(UCL_AwakeningService.NowIso());
            ioS["end_reason"] = new JsonData(iByInterrupt ? "recording-stopped" : "expired");
            ioS["paid_minutes"] = new JsonData(aPaidMin);
            ioS["paid_total"] = new JsonData(aTotal);
            ioS["end_seq"] = new JsonData(aSeq);
            AtomicWrite(SessionPath(iPersona), ioS.ToJsonBeautify());

            ioR.AppendLine($"- 本場統計: cycles={ReadInt(ioS, "cycles")}｜observations={aObs}｜在場 {aPaidMin} 分鐘");
            ioR.AppendLine($"- 結算    : {aPayNote}");
            ioR.AppendLine($"- 收播公告: {(aSeq > 0 ? $"seq **{aSeq}**" : "未發（best-effort）")}");
            ioR.AppendLine($"- 場次紀錄: seq **{ReadInt(ioS, "start_seq")} → {aSeq}**（匯出區間，`tavern` 房）");
            ioR.AppendLine();
            ioR.AppendLine("## next");
            ioR.AppendLine("1. 本場已收工結算，session 已關閉。");
            ioR.AppendLine($"2. 要再看：run_cmd.py run StreamWatch --arg step=start --arg persona={iPersona} --arg until=<HH:mm> --arg media=<work>");
        }

        // 區塊職責：ledger 判重 —— **有界掃描**（只掃最近結帳日之後）
        // ⚠ 不用 LoadAllEntries()：那支重放全帳本（本專案 14,700+ 檔），
        //   2026-08-15 已因為它讓初開 Editor 卡三分鐘而從跨日結算移除（見 1188e7a）。
        // ⚠ 查詢失敗時**保守視為已發**：壞要往安全的方向壞 —— 少發一次可以補，重複發薪收不回。
        static bool AlreadyCredited(string iUseRef)
        {
            try
            {
                string aToday = DateTime.UtcNow.ToString("yyyy-MM-dd");
                var aRec = UCL_TreasuryClosing.LoadLatestBefore(aToday);
                var aEntries = UCL_TreasuryLedger.LoadEntriesAfterDate(aRec?.DateKey);
                foreach (var e in aEntries)
                    if (e != null && e.type == "credit" && e.source_ref == iUseRef) return true;
                return false;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[StreamWatch] ledger 判重失敗，保守視為已發（不重複發薪）：{e.Message}");
                return true;
            }
        }

        // ===========================================================
        // 區塊：step=observe — 發評論 ＋ 記帳
        // 物理意義：**先發文、後記帳**（Plan §5）。順序不可換：
        //   先發後記 ⇒ 發了沒記＝訊息看得到、帳少一筆 ⇒ **可補、可見**
        //   先記後發 ⇒ 記了沒發＝**帳上有一筆沒人看過的評論**，那是 phantom 且沒有地方會叫
        // ⚠ 合併發文與記帳的用途：舊流程那條「每輪發完評論必跑 record_observation」的自律規則
        //   之所以存在，正因為兩者是兩步。**合併之後那個分岔不存在了，規則不必記。**
        // ⚠ frame 數不由 agent 傳 —— 取自上一次 cycle 當下記進 session 的值（幹活的副產物）。
        // ===========================================================
        async UniTask StepObserve(string iPersona, string iBody, CancellationToken iToken)
        {
            string aPath = PayloadPath(iPersona, "observe");
            var aR = new StringBuilder();
            aR.AppendLine($"# StreamWatch step=observe persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            var aS = LoadSession(iPersona);
            if (aS == null || !ReadBool(aS, "active"))
            {
                Blocked(aR, aPath, "無進行中的觀影 session",
                        $"先跑 run_cmd.py run StreamWatch --arg step=start --arg persona={iPersona} --arg until=<HH:mm> --arg media=<work>");
                throw new Exception($"[StreamWatch] step=observe blocked：無 active session（詳見 {aPath}）");
            }
            if (string.IsNullOrWhiteSpace(iBody))
            {
                Blocked(aR, aPath, "body 為空 —— 觀戰評論不能是空的",
                        $"--arg-file body=<檔案>（長文走檔案，不經 shell）");
                throw new Exception($"[StreamWatch] step=observe blocked：body 為空（詳見 {aPath}）");
            }
            // 守衛：沒有對應的取材紀錄 ⇒ 拒收（Plan §12 —— 不是靜靜算錢）
            int aLastTiles = ReadInt(aS, "last_tiles");
            if (ReadInt(aS, "cycles") <= 0 || aLastTiles <= 0)
            {
                Blocked(aR, aPath, "本場尚無取材紀錄 —— 沒看過就沒有可記的觀察",
                        $"先跑 run_cmd.py run StreamWatch --arg step=cycle --arg persona={iPersona}");
                throw new Exception($"[StreamWatch] step=observe blocked：無取材紀錄（詳見 {aPath}）");
            }

            // ① 先發文
            double aSpan = ReadDouble(aS, "last_span_seconds");
            var aBody = new StringBuilder();
            aBody.AppendLine(iBody.TrimEnd());
            aBody.AppendLine();
            aBody.AppendLine($"— 本輪素材：{aLastTiles} 格／涵蓋 {aSpan:F0}s（**每格 ≈{(aLastTiles > 0 ? aSpan / aLastTiles : 0):F0}s**）｜媒材 `{ReadStr(aS, "media_id")}`");
            int aSeq = await TavernPost(iPersona, aBody.ToString(), "watch-observe", iToken);

            // ② 後記帳（發文失敗就不記 —— 帳上不留沒人看過的評論）
            if (aSeq <= 0)
            {
                aR.AppendLine("## blocked");
                aR.AppendLine("- reason: 酒館發文失敗 ⇒ **不記帳**（先記後發會在帳上留一筆沒人看過的評論）");
                aR.AppendLine("- exit: 重跑 step=observe（評論內容請保留）");
                WritePayload(aPath, aR.ToString());
                throw new Exception($"[StreamWatch] step=observe blocked：發文失敗，未記帳（詳見 {aPath}）");
            }
            int aObs = ReadInt(aS, "observations") + 1;
            aS["observations"] = new JsonData(aObs);
            aS["last_observe_seq"] = new JsonData(aSeq);
            AtomicWrite(SessionPath(iPersona), aS.ToJsonBeautify());

            DateTime? aEnd = ParseIsoLocal(ReadStr(aS, "end_ts"));
            int aRemain = aEnd.HasValue ? (int)Math.Max(0, (aEnd.Value - DateTime.Now).TotalMinutes) : 0;
            aR.AppendLine($"- 評論已發: seq **{aSeq}**（先發後記 —— 發文成功才記帳）");
            aR.AppendLine($"- 本場累計: cycles={ReadInt(aS, "cycles")}｜**observations={aObs}**（計酬上限 {OBSERVATION_CAP}）");
            aR.AppendLine($"- 剩餘    : {aRemain} 分鐘（到 {aEnd:HH:mm}）");
            aR.AppendLine();
            aR.AppendLine("## next");
            aR.AppendLine($"1. 繼續：run_cmd.py run StreamWatch --arg step=cycle --arg persona={iPersona}");
            aR.AppendLine("2. **收工不用你判斷** —— 到期或 Tim 停錄影時，cycle 會告訴你並提示寫接續點。");
            WritePayload(aPath, aR.ToString());
            Debug.Log($"[StreamWatch] step=observe seq={aSeq} obs={aObs} → {aPath}");
        }

        // ===========================================================
        // 區塊：step=note — 寫接續點（Tim 2026-08-15：心得必寫，理由是「下次續看無法追回進度」）
        // 物理意義：必寫的**不是「心得」，是「接續點」** —— 心得可以短、可以主觀，Cmd 管不了品質；
        //          接續點是結構化可檢查的：看到哪／下次從哪接／人物與伏筆狀態。
        // ⚠ Tim 拍板**不擋結算**（擋的失敗模式是 agent 消失 ⇒ 錢卡住而心得照樣沒寫）。
        //   所以本步只負責「寫得容易」，遺漏由收工通知與下一場 start 明列 —— **不擋，但也不安靜**。
        // ===========================================================
        async UniTask StepNote(string iPersona, string iBody, CancellationToken iToken)
        {
            string aPath = PayloadPath(iPersona, "note");
            var aR = new StringBuilder();
            aR.AppendLine($"# StreamWatch step=note persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            var aS = LoadSession(iPersona);
            if (aS == null || !ReadBool(aS, "active"))
            {
                Blocked(aR, aPath, "無進行中的觀影 session",
                        $"先跑 run_cmd.py run StreamWatch --arg step=start --arg persona={iPersona} --arg until=<HH:mm> --arg media=<work>");
                throw new Exception($"[StreamWatch] step=note blocked：無 active session（詳見 {aPath}）");
            }
            if (string.IsNullOrWhiteSpace(iBody))
            {
                aR.AppendLine("## blocked");
                aR.AppendLine("- reason: body 為空 —— 接續點是下次續看唯一接得回進度的東西");
                aR.AppendLine("- how: --arg-file body=<檔案>，內容至少要有三件：");
                aR.AppendLine("  1. **看到哪**（集數／時間點／劇情位置）");
                aR.AppendLine("  2. **下次從哪接**");
                aR.AppendLine("  3. **人物與伏筆的當前狀態**");
                WritePayload(aPath, aR.ToString());
                throw new Exception($"[StreamWatch] step=note blocked：body 為空（詳見 {aPath}）");
            }

            string aMedia = ReadStr(aS, "media_id");
            var aBody = new StringBuilder();
            aBody.AppendLine($"📌 [{iPersona} 大小姐] 觀影接續點 — 媒材 `{aMedia}`");
            aBody.AppendLine();
            aBody.AppendLine(iBody.TrimEnd());
            int aSeq = await TavernPost(iPersona, aBody.ToString(), "watch-note", iToken);

            aS["note_written"] = new JsonData(true);
            aS["note_seq"] = new JsonData(aSeq);
            AtomicWrite(SessionPath(iPersona), aS.ToJsonBeautify());

            aR.AppendLine($"- 接續點已寫並發布: {(aSeq > 0 ? $"seq **{aSeq}**" : "發文失敗（**接續點仍已記進 session**）")}");
            aR.AppendLine($"- media: `{aMedia}`");
            aR.AppendLine();
            aR.AppendLine("## next");
            aR.AppendLine($"1. 跑 run_cmd.py run StreamWatch --arg step=cycle --arg persona={iPersona}");
            aR.AppendLine("   —— 若已到期／Tim 已停錄影，那一步會完成收工；否則會繼續給下一輪素材。");
            WritePayload(aPath, aR.ToString());
            Debug.Log($"[StreamWatch] step=note seq={aSeq} → {aPath}");
        }

        // ===========================================================
        // 區塊：縮圖牆合成 —— **非阻塞**（Tim 2026-08-15 指示）
        // 物理意義：外部 process 的等待丟 thread pool，await 回來自動落主執行緒。
        // ⚠ 不可照抄 UCL_BartenderDaemon.RunBalanceQuery —— 那支用 out 參數 ⇒ 不可能 async，
        //   內部 while+WaitForExit 是主執行緒輪詢（2026-07-26 加的是可取消，不是非阻塞）。
        // ⚠ async 化讓 out 參數消失 ⇒ 回 tuple；**呼叫端漏接 err 就是靜默失敗**，所以失敗必落回傳檔。
        // ===========================================================
        static async UniTask<(bool ok, string stdout, string err)> RunMontageAsync(
            string iScript, double iCursor, string iOutPath, bool iOcr, bool iStt, CancellationToken iToken)
        {
            try
            {
                var aArgs = new StringBuilder();
                aArgs.Append("\"").Append(iScript).Append("\" make");
                if (iCursor > 0) aArgs.Append(" --after-mtime ").Append(iCursor.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                aArgs.Append(" --max-tiles ").Append(MAX_TILES);
                // 區塊職責：感官旗標**不由呼叫端記得帶**（Tim 2026-08-15）——
                // 物理意義：OCR/STT 的開關已經在 _config.json 裡（`ocr_enabled` / `stt_enabled`），
                //          那才是事實源。要人再傳一次 flag，等於把同一件事存兩個地方，
                //          而漏帶的那次**不會報錯，只會安靜地沒有字幕**（2026-08-15 首跑實踩：
                //          我自己寫的第一版就忘了帶 --ocr，於是端出四天前的殘留 sidecar）。
                // ⇒ 開著就給，不用問。規則長在通道上，不掛在記憶裡。
                if (iOcr) aArgs.Append(" --ocr");
                if (iStt) aArgs.Append(" --stt");
                aArgs.Append(" --out \"").Append(iOutPath).Append("\"");

                var aPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = aArgs.ToString(),
                    WorkingDirectory = UCL_RepoPath.UnityProjectRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                using var aProc = System.Diagnostics.Process.Start(aPsi);
                if (aProc == null) return (false, "", "Process.Start 回 null");

                // 硬規則：每顆外部 Process 都要登記（Coding_Standards「外部 Process」）
                using var aScope = UCL_ProcessRegistryService.RegisterScope(
                    aProc, "streamwatch_montage", "縮圖牆合成（cycle 內）", nameof(Cmd_StreamWatch));

                string aOut = "", aErr = "";
                // ⚠ 這裡是本檔唯一會等外部程式的地方 —— 丟 thread pool，主執行緒不凍。
                bool aExited = await System.Threading.Tasks.Task.Run(() =>
                {
                    aOut = aProc.StandardOutput.ReadToEnd();
                    aErr = aProc.StandardError.ReadToEnd();
                    return aProc.WaitForExit(60000);
                }, iToken);

                if (!aExited) { try { aProc.Kill(); } catch { } return (false, aOut, "timeout(>60s)"); }
                if (aProc.ExitCode != 0) return (false, aOut, $"exit={aProc.ExitCode}; {Truncate(aErr, 300)}");
                return (true, aOut, "");
            }
            catch (Exception e)
            {
                return (false, "", $"spawn exception: {e.Message}");
            }
        }

        struct MontageInfo
        {
            public int Tiles;
            public double SpanSeconds;
            public double NextCursor;
            public string SpanText;
            public double PerTileSeconds => Tiles > 0 ? SpanSeconds / Tiles : 0;
        }

        // 區塊職責：從 montage report 取事實 —— **數字由工具產生，不經過 agent 的鍵盤**（Plan §5.1）
        static MontageInfo ParseMontageReport(string iStdout)
        {
            var aInfo = new MontageInfo { SpanText = "(未解析)" };
            if (string.IsNullOrEmpty(iStdout)) return aInfo;
            var aTiles = System.Text.RegularExpressions.Regex.Match(iStdout, @"\((\d+) tiles\)");
            if (aTiles.Success) int.TryParse(aTiles.Groups[1].Value, out aInfo.Tiles);
            var aSpan = System.Text.RegularExpressions.Regex.Match(iStdout, @"time span\s*:\s*(.+)");
            if (aSpan.Success) aInfo.SpanText = aSpan.Groups[1].Value.Trim();
            var aSec = System.Text.RegularExpressions.Regex.Match(iStdout, @"\((\d+(?:\.\d+)?)s,\s*\d+ frames?\)");
            if (aSec.Success) double.TryParse(aSec.Groups[1].Value, System.Globalization.NumberStyles.Float,
                                              System.Globalization.CultureInfo.InvariantCulture, out aInfo.SpanSeconds);
            var aCur = System.Text.RegularExpressions.Regex.Match(iStdout, @"next-cursor\s*:\s*(\d+(?:\.\d+)?)");
            if (aCur.Success) double.TryParse(aCur.Groups[1].Value, System.Globalization.NumberStyles.Float,
                                              System.Globalization.CultureInfo.InvariantCulture, out aInfo.NextCursor);
            return aInfo;
        }

        // ===========================================================
        // 區塊：ScreenStream 狀態 —— **只認顯式欄位**
        // ⚠ 不用 frame 新鮮度推論（Plan §2.2 的血證）
        // ===========================================================
        /// <summary>讀 _config.json 的感官開關 —— 開著就自動供給，呼叫端不必傳旗標。</summary>
        static (bool ocr, bool stt) ReadSensorFlags()
        {
            try
            {
                string aCfg = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", "_config.json");
                var aJd = JsonData.ParseJson(File.ReadAllText(aCfg, Encoding.UTF8));
                bool aOcr = aJd != null && aJd.Contains("ocr_enabled") && (bool)aJd["ocr_enabled"];
                bool aStt = aJd != null && aJd.Contains("stt_enabled") && (bool)aJd["stt_enabled"];
                return (aOcr, aStt);
            }
            catch { return (false, false); }
        }

        static bool IsRecordingEnabled(out string oNote)
        {
            oNote = "";
            try
            {
                string aCfg = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", "_config.json");
                if (!File.Exists(aCfg)) { oNote = $"找不到 {aCfg}（視為未錄影）"; return false; }
                var aJd = JsonData.ParseJson(File.ReadAllText(aCfg, Encoding.UTF8));
                bool aEn = aJd != null && aJd.Contains("enabled") && (bool)aJd["enabled"];
                oNote = $"`{aCfg}` enabled={aEn.ToString().ToLowerInvariant()}";
                return aEn;
            }
            catch (Exception e) { oNote = $"讀 _config.json 失敗：{e.Message}（視為未錄影）"; return false; }
        }

        static void AppendRetentionLine(StringBuilder ioR)
        {
            try
            {
                string aCfg = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", "_config.json");
                var aJd = JsonData.ParseJson(File.ReadAllText(aCfg, Encoding.UTF8));
                int aMax = aJd.Contains("max_frames") ? int.Parse(aJd["max_frames"].ToString()) : 0;
                int aFps = aJd.Contains("fps") ? int.Parse(aJd["fps"].ToString()) : 1;
                if (aFps <= 0) aFps = 1;
                ioR.AppendLine($"- 保存期   : {aMax / aFps}s（{aMax} frames / {aFps} fps —— **讀自 _config.json，不寫死**）");
            }
            catch { ioR.AppendLine("- 保存期   : (讀取失敗)"); }
        }

        // ===========================================================
        // 區塊：Library work 解析 —— media_id 是共享鍵（Plan §4）
        // ===========================================================
        static string WorksRoot() => Path.Combine(UCL_AgentCommandsPath.DataRoot, "BookNotes", "Library", "works");

        static bool WorkExists(string iSlug)
        {
            try { return Directory.Exists(Path.Combine(WorksRoot(), iSlug)); } catch { return false; }
        }

        static List<string> ListExistingWorks()
        {
            var aList = new List<string>();
            try
            {
                string aRoot = WorksRoot();
                if (!Directory.Exists(aRoot)) return aList;
                foreach (var d in Directory.GetDirectories(aRoot)) aList.Add(Path.GetFileName(d));
                aList.Sort(StringComparer.Ordinal);
            }
            catch { }
            return aList;
        }

        // ===========================================================
        // 區塊：路徑 / IO helpers（與 Cmd_FreeTime 同慣例）
        // ===========================================================
        static string SessionPath(string iPersona)
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "StreamWatch", "sessions", $"{iPersona}.json");

        static string MontageOutPath(string iPersona)
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", $"_montage_{iPersona}.jpg");

        /// <summary>⚠ 只能在主執行緒呼叫（CorePath 走 AssetDatabase）。</summary>
        static string ResolveMontageScript()
        {
            try
            {
                string aCoreRel = UCL_EditorPath.CorePath;
                if (string.IsNullOrEmpty(aCoreRel)) return "";
                string aPath = Path.GetFullPath(Path.Combine(
                    UCL_RepoPath.UnityProjectRoot, aCoreRel, "Tools~/AgentCommands/screenstream_montage.py"));
                return File.Exists(aPath) ? aPath.Replace('\\', '/') : "";
            }
            catch { return ""; }
        }

        static JsonData LoadSession(string iPersona)
        {
            try
            {
                string aP = SessionPath(iPersona);
                return File.Exists(aP) ? JsonData.ParseJson(File.ReadAllText(aP, Encoding.UTF8)) : null;
            }
            catch { return null; }
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
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "ChatTavern", "baton", "letters",
                            iPersona, $"_streamwatch_{iStep}.md");

        static void WritePayload(string iPath, string iContent)
        {
            AtomicWrite(iPath, iContent);
            UCL_AgentCommandRunner.ReportOutputFile(iPath);
        }

        static void Blocked(StringBuilder ioR, string iPath, string iReason, string iExit)
        {
            ioR.AppendLine("## blocked");
            ioR.AppendLine($"- reason: {iReason}");
            ioR.AppendLine($"- exit: {iExit}");
            WritePayload(iPath, ioR.ToString());
        }

        static async UniTask<int> TavernPost(string iPersona, string iBody, string iSubtag, CancellationToken iToken)
        {
            try
            {
                var aLock = UCL_AwakeningService.ReadLock(iPersona);
                var aArgs = new Dictionary<string, string>
                {
                    { "op", "post" }, { "room", "tavern" }, { "persona", iPersona }, { "body", iBody },
                    { "meta", $"{{\"tag\":\"stream-watch\",\"subtag\":\"{iSubtag}\",\"category\":\"chat\"}}" },
                };
                if (aLock != null && !string.IsNullOrEmpty(aLock.session_token)) aArgs["session_token"] = aLock.session_token;
                ChatTavern.Cmd_Tavern.LastPostSeq = 0;
                await new ChatTavern.Cmd_Tavern().ExecuteAsync(aArgs, iToken);
                return ChatTavern.Cmd_Tavern.LastPostSeq;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[StreamWatch] 酒館發文失敗（不擋 session）：{e.Message}");
                return 0;
            }
        }

        static bool TryParseUntil(string iUntil, DateTime iNow, out DateTime oUntil, out string oError)
        {
            oUntil = default; oError = "";
            if (string.IsNullOrEmpty(iUntil)) { oError = "until 未提供"; return false; }
            var aParts = iUntil.Split(':');
            if (aParts.Length != 2 || !int.TryParse(aParts[0], out int aH) || !int.TryParse(aParts[1], out int aM)
                || aH < 0 || aH > 23 || aM < 0 || aM > 59)
            { oError = $"until 格式錯誤 '{iUntil}'（需 HH:mm）"; return false; }
            var aT = new DateTime(iNow.Year, iNow.Month, iNow.Day, aH, aM, 0);
            if (aT <= iNow) aT = aT.AddDays(1);            // 深夜跨日
            oUntil = aT;
            return true;
        }

        static string ReadStr(JsonData iJd, string iKey) => iJd != null && iJd.Contains(iKey) ? iJd[iKey].ToString() : "";
        static int ReadInt(JsonData iJd, string iKey) { try { return iJd != null && iJd.Contains(iKey) ? int.Parse(iJd[iKey].ToString()) : 0; } catch { return 0; } }
        static double ReadDouble(JsonData iJd, string iKey) { try { return iJd != null && iJd.Contains(iKey) ? double.Parse(iJd[iKey].ToString(), System.Globalization.CultureInfo.InvariantCulture) : 0; } catch { return 0; } }
        static bool ReadBool(JsonData iJd, string iKey) { try { return iJd != null && iJd.Contains(iKey) && (bool)iJd[iKey]; } catch { return false; } }

        static DateTime? ParseIsoLocal(string iIso)
        {
            if (string.IsNullOrEmpty(iIso)) return null;
            if (DateTime.TryParse(iIso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var aUtc)) return aUtc.ToLocalTime();
            return null;
        }

        static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }
}
#endif
