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

            // ── 終止判定（唯一的一處）─────────────────────────────
            if (aExpired || aRecordingOff)
            {
                string aReason = aRecordingOff ? "Tim 停止錄影（_config.json enabled=false）" : "到期";
                aR.AppendLine("## 收工判定");
                aR.AppendLine($"- 判定: **{aReason}**");
                aR.AppendLine($"- 依據: {(aRecordingOff ? aCfgNote : $"now={aNow:HH:mm:ss} >= ends_at={aEnd:HH:mm:ss}")}");
                aR.AppendLine($"- ⚠ 本判定只認**顯式狀態**（系統時鐘／`enabled` 欄位），不推論 frame 新鮮度。");
                aR.AppendLine();
                aR.AppendLine($"- 本場統計: cycles={ReadInt(aS, "cycles")}｜observations={ReadInt(aS, "observations")}｜接續點={(ReadBool(aS, "note_written") ? "已寫" : "**未寫**")}");
                aR.AppendLine();
                aR.AppendLine("## next");
                if (!ReadBool(aS, "note_written"))
                {
                    aR.AppendLine($"1. **先寫接續點**（下次續看要靠它接回進度）：");
                    aR.AppendLine($"   run_cmd.py run StreamWatch --arg step=note --arg persona={iPersona} --arg-file body=<接續點>");
                    aR.AppendLine("   內容至少要有：**看到哪／下次從哪接／人物與伏筆的當前狀態**。");
                    aR.AppendLine($"2. 再跑一次 step=cycle 完成結算。");
                }
                else
                {
                    aR.AppendLine("1. 接續點已寫 —— 再跑一次 step=cycle 完成結算。");
                }
                aR.AppendLine();
                aR.AppendLine("⚠ 結算與收播公告尚未實作（本次施工只到 start/cycle 的判定）—— session 仍為 active。");
                WritePayload(aPath, aR.ToString());
                Debug.Log($"[StreamWatch] step=cycle 判定收工（{aReason}）→ {aPath}");
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

            var (aOk, aStdout, aErr) = await RunMontageAsync(aScript, aCursor, aOutPath, iToken);
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
            bool aHasSub = File.Exists(aSubPath);

            aR.AppendLine("## 本輪素材");
            aR.AppendLine($"- 縮圖牆   : `{aOutPath}`　← 直接 Read");
            aR.AppendLine(aHasSub
                ? $"- 字幕     : `{aSubPath}`　← 直接 Read"
                : "- 字幕     : **本輪無字幕**（不是欄位不存在 —— 這一輪確實沒有）");
            aR.AppendLine($"- 涵蓋     : {aInfo.SpanText}");
            aR.AppendLine($"- 格數     : {aInfo.Tiles}　**每格 ≈{aInfo.PerTileSeconds:F0}s**　← 落後越多每格越粗，丟的是細節不是時間");
            AppendRetentionLine(aR);
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
            string iScript, double iCursor, string iOutPath, CancellationToken iToken)
        {
            try
            {
                var aArgs = new StringBuilder();
                aArgs.Append("\"").Append(iScript).Append("\" make");
                if (iCursor > 0) aArgs.Append(" --after-mtime ").Append(iCursor.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                aArgs.Append(" --max-tiles ").Append(MAX_TILES);
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
