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
            "觀影模式 Cmd（step=capture/peek/start/join/cycle/observe/note）。**capture 開關錄影**（串 Page 同一條規則）；" +
            "**peek 不開場、不記帳，看一眼就走**（也是測試探針）；" +
            "start 鎖定媒材＋註冊看到幾點；cycle 自己合成縮圖牆並判定到期/中斷；" +
            "**沒有 end —— agent 不能自己結束 session**。";

        public override string ArgsSchema =>
            "step=capture|peek|start|join|cycle|observe|note (必填) | persona=<name> — **peek 以外全步驟必填**（peek 不帶則歸 _peek） | "
          + "on=1|0 — capture 必填（開/關錄影；串 UCL_ScreenStreamPage 同一條規則） | " +
            "seconds=<5..600> — peek 選填，看最近幾秒（預設 60） | raw=1 — peek 選填，不夾感官水位（看最新畫面，代價寫在回傳檔） | " +
            "until=<HH:mm 本地> — start 必填 | media=<work-slug> — start 選填（不給則由 Cmd 問；" +
            "**bilibili 一律 `bilibili-<up主 slug>` 並必帶 up=**） | " +
            "up=<up主名> / title=<影片標題> / desc=<影片介紹> / url=<網址> — start 選填（bilibili 場 up 必填） | " +
            "body=<內文> — observe/note 必填（長文走 --arg-file） | " +
            "回傳落檔 letters/<persona>/_streamwatch_<step>.md（路徑隨 run_cmd verdict 印出）";

        public override string ExampleArgs => "step=start;persona=Template;until=23:59";

        public override string HelpURL => "ucl_core:Docs~/zh-Hant/Plan/Plan_StreamWatch_Cmd.md";

        /// <summary>每輪縮圖牆的格數上限（Tim 2026-08-15：一輪約讀 12–16 張）。</summary>
        const int MAX_TILES = 16;

        /// <summary>每場 observation 計酬上限（Plan §6：沒有上限的按量計酬就是印鈔許可證）。</summary>
        const int OBSERVATION_CAP = 12;

        /// <summary>peek 沒帶 persona 時的回傳檔歸屬（不是 persona，是一個放檔案的地方）。</summary>
        const string PEEK_OWNER = "_peek";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            string aStep = GetArg(args, "step", "").Trim().ToLowerInvariant();
            string aPersona = GetArg(args, "persona", "").Trim();
            // peek 是**唯一不需要身分的 step**：它不開 session、不記帳、不發文，
            // 所以沒有「這筆算誰的」這個問題 —— 要求 persona 只會讓「還沒登入就想看一眼」卡住。
            if (string.IsNullOrEmpty(aPersona))
            {
                if (aStep == "peek") aPersona = PEEK_OWNER;
                else throw new Exception($"[StreamWatch] persona 必填。ArgsSchema: {ArgsSchema}");
            }

            switch (aStep)
            {
                case "peek": await StepPeek(aPersona, GetArg(args, "seconds", "").Trim(),
                                            GetArg(args, "raw", "").Trim(), token); return;
                case "capture": StepCapture(aPersona, GetArg(args, "on", "").Trim()); return;
                case "start": await StepStart(aPersona, GetArg(args, "until", "").Trim(),
                                              GetArg(args, "media", "").Trim(),
                                              new SourceMeta
                                              {
                                                  Up = GetArg(args, "up", "").Trim(),
                                                  VideoTitle = GetArg(args, "title", "").Trim(),
                                                  VideoDesc = GetArg(args, "desc", "").Trim(),
                                                  Url = GetArg(args, "url", "").Trim(),
                                              }, token); return;
                case "cycle": await StepCycle(aPersona, token); return;
                case "observe": await StepObserve(aPersona, GetArg(args, "body", ""), token); return;
                case "note": await StepNote(aPersona, GetArg(args, "body", ""), token); return;
                case "join": await StepJoin(aPersona, token); return;
                default:
                    throw new Exception($"[StreamWatch] step 必為 peek|start|join|cycle|observe|note（got '{aStep}'）。ArgsSchema: {ArgsSchema}");
            }
        }

        // ===========================================================
        // 區塊：step=capture — 開／關錄影（Tim 2026-08-15 追加，方便測試與自助開播）
        // 物理意義：**不自己寫 config** —— 串 `UCL_ScreenStreamPage.SetRecordingEnabled`，
        //          跟 GUI 那顆「▶ 開始錄影 / ⏹ 停止錄影」走同一條規則（戳時刻／連動 stt_enabled／
        //          發酒保公告／要求 daemon 同步）。⇒ 「Cmd 開的播」與「人開的播」在酒館裡長得一樣。
        // ⚠ 這正是本日反覆栽的那族的預防：同一件事**不要有第二個寫入端**。
        //   Cmd 若自己寫 `enabled`，就會出現「誰後寫誰贏、而誰後寫取決於呼叫順序」。
        // 邊界：已經是該狀態 ⇒ 明說「未動作」並印讀值，不假裝做了一次切換。
        // ===========================================================
        void StepCapture(string iPersona, string iOn)
        {
            string aPath = PayloadPath(iPersona, "capture");
            var aR = new StringBuilder();
            aR.AppendLine($"# StreamWatch step=capture persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            bool aOn;
            if (iOn == "1" || iOn.Equals("true", StringComparison.OrdinalIgnoreCase) || iOn == "on") aOn = true;
            else if (iOn == "0" || iOn.Equals("false", StringComparison.OrdinalIgnoreCase) || iOn == "off") aOn = false;
            else
            {
                Blocked(aR, aPath, $"on 必須是 1/0（true/false、on/off 亦可）—— 收到 '{iOn}'",
                        $"run_cmd.py run StreamWatch --arg step=capture --arg persona={iPersona} --arg on=1");
                throw new Exception($"[StreamWatch] step=capture blocked：on 參數無效（詳見 {aPath}）");
            }

            string aNote = UCL.Core.EditorLib.Page.UCL_ScreenStreamPage.SetRecordingEnabled(aOn, iPersona);
            aR.AppendLine("## 結果（讀回的事實）");
            aR.AppendLine($"- {aNote}");
            bool aNow = IsRecordingEnabled(out string aCfgNote);
            aR.AppendLine($"- 回讀   : {aCfgNote}　←　**寫完再讀一次，不是看回傳值**");
            AppendRetentionLine(aR);
            aR.AppendLine();
            aR.AppendLine("## next");
            aR.AppendLine(aOn
                ? $"1. 看一眼：run_cmd.py run StreamWatch --arg step=peek --arg seconds=60\n"
                + $"2. 正式開場：run_cmd.py run StreamWatch --arg step=start --arg persona={iPersona} --arg until=<HH:mm> --arg media=<work>"
                : "1. 已停止擷取。進行中的觀影 session 會在下一次 cycle 被判定為「Tim 停止錄影」並結算。");
            WritePayload(aPath, aR.ToString());
            Debug.Log($"[StreamWatch] step=capture on={aOn} → {aPath}");
        }

        // ===========================================================
        // 區塊：step=peek — **不開場、看一眼**（Tim 2026-08-15 追加）
        // 物理意義：觀影 session 是一個**承諾**（看到幾點、要寫心得、會結算），
        //          而「直播開著，我看一眼」與「我想測 montage/感官管線通不通」兩件事都**不該付那個承諾的代價**。
        //          ⇒ peek 走同一條取材與對帳程式碼，但：**不讀 session、不寫 session、不記帳、不發文**。
        //          共用取材程式碼是刻意的：測試探針若走另一條路，它綠了也不代表正式路徑會綠
        //          （2026-08-15 血證：手跑 montage 全綠，而 Cmd 那條在同一分鐘 exit=1）。
        // 數值影響：零 token。輸出落 `_montage_peek_<owner>.jpg` —— **與 session 的 `_montage_<persona>.jpg` 分開**，
        //          否則進行中的觀影場會被一次 peek 蓋掉素材（而它不會報錯）。
        // 邊界：seconds 5–600（預設 60）；raw=1 時不夾感官水位 ⇒ 看得到最新畫面，
        //      但尾端那幾格可能沒有字幕/語音 —— 這件事由對帳行**明說「未夾」**，不靠讀的人記得。
        // ===========================================================
        async UniTask StepPeek(string iOwner, string iSeconds, string iRaw, CancellationToken iToken)
        {
            string aPath = PayloadPath(iOwner, "peek");
            var aR = new StringBuilder();
            aR.AppendLine($"# StreamWatch step=peek owner={iOwner}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();
            aR.AppendLine("> **這不是一場觀影** —— 不開 session／不記帳／不發酒館／不動任何進行中的場次。");
            aR.AppendLine();

            int aSec = 60;
            if (!string.IsNullOrEmpty(iSeconds) && int.TryParse(iSeconds, out int aParsed)) aSec = aParsed;
            aSec = Math.Max(5, Math.Min(600, aSec));
            bool aRaw = iRaw == "1" || iRaw.Equals("true", StringComparison.OrdinalIgnoreCase);

            string aScript = ResolveMontageScript();
            if (string.IsNullOrEmpty(aScript))
            {
                Blocked(aR, aPath, "解析不到 screenstream_montage.py（CorePath 空或檔案不存在）",
                        "確認 UCL_Core 掛載位置與 Tools~/AgentCommands/screenstream_montage.py 是否存在");
                throw new Exception($"[StreamWatch] step=peek blocked：找不到縮圖牆工具（詳見 {aPath}）");
            }
            string aOutPath = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", $"_montage_peek_{iOwner}.jpg");

            // ⚠ 錄影關掉時**不擋** —— ring buffer 裡還有畫面，看一眼仍然有意義。
            //   但必須把 enabled 的**讀值印出來**：否則「直播沒開」與「這段剛好沒畫面」在輸出上同形。
            bool aLive = IsRecordingEnabled(out _);
            var (aOcrOn, aSttOn) = ReadSensorFlags();
            // 水位**兩條路都算**（raw 只是不拿它去夾）—— 這樣 raw 的回傳檔仍印得出
            // 「你放棄的是什麼」：少了幾秒的感官涵蓋，而不是一句沒有數字的「未夾」。
            double aWmValue = SensorWatermark(aOcrOn, aSttOn, out string aWmNote);
            double aWatermark = aRaw ? 0 : aWmValue;
            double aAfter = ToEpoch(DateTime.UtcNow) - aSec;

            DateTime aRunStart = DateTime.Now;
            var (aOk, aStdout, aErr) = await RunMontageAsync(aScript, aAfter, aWatermark, aOutPath, aOcrOn, aSttOn, iToken);
            string aBoth = (aStdout ?? "") + "\n" + (aErr ?? "");
            if (!aOk && (aBoth.Contains("無 frame 命中") || aBoth.Contains("OCR watermark 還沒趕上")))
            {
                aR.AppendLine("## 這段窗口沒有畫面（不是錯誤）");
                aR.AppendLine($"- 錄影中  : {(aLive ? "是" : "**否**（`_config.json` 的 `enabled=false`）—— buffer 只剩舊畫面")}");
                aR.AppendLine($"- 窗口    : 最近 {aSec}s（{FromEpochLocal(aAfter):HH:mm:ss} → 現在）");
                aR.AppendLine($"- 感官水位: {aWmNote}"
                            + (aWatermark > 0 ? $"　⇒ {FromEpochLocal(aWatermark):HH:mm:ss}（窗口尾端夾在這）" : "　（raw：未夾）"));
                aR.AppendLine();
                aR.AppendLine("## next");
                aR.AppendLine($"- 窗口拉長：--arg seconds=180");
                aR.AppendLine($"- 不等字幕/語音、直接看最新畫面：--arg raw=1（代價：尾端可能沒有感官資料）");
                WritePayload(aPath, aR.ToString());
                Debug.Log($"[StreamWatch] step=peek 無素材 → {aPath}");
                return;
            }
            if (!aOk)
            {
                aR.AppendLine("## blocked");
                aR.AppendLine($"- reason: 縮圖牆合成失敗 — {aErr}");
                if (!string.IsNullOrWhiteSpace(aStdout))
                    aR.AppendLine($"- stdout: {Truncate(aStdout.Trim(), 500)}");
                aR.AppendLine("- exit: 確認 ScreenStream 是否有 frame；重跑 step=peek");
                WritePayload(aPath, aR.ToString());
                throw new Exception($"[StreamWatch] step=peek blocked：montage 失敗（詳見 {aPath}）");
            }

            var aInfo = ParseMontageReport(aStdout);
            string aSubPath = Path.ChangeExtension(aOutPath, ".subtitles.md");
            bool aHasSub = false; string aSubNote = "";
            try
            {
                if (File.Exists(aSubPath))
                {
                    DateTime aM = File.GetLastWriteTime(aSubPath);
                    if (aM >= aRunStart.AddSeconds(-1)) aHasSub = true;
                    else aSubNote = $"（磁碟上有一份 {aM:MM-dd HH:mm} 的殘留檔 —— **不是這次的，已忽略**）";
                }
            }
            catch { }

            aR.AppendLine("## 看到什麼");
            aR.AppendLine($"- 縮圖牆   : `{aOutPath}`　← 直接 Read");
            aR.AppendLine(aHasSub
                ? $"- 字幕     : `{aSubPath}`　← 直接 Read（**這次產出**，mtime 已驗）"
                : $"- 字幕     : **這次無字幕**{aSubNote}");
            aR.AppendLine($"- 錄影中   : {(aLive ? "是" : "**否**（`enabled=false`）—— 以下畫面是 buffer 殘留，不是當下")}");
            aR.AppendLine($"- 涵蓋     : {aInfo.SpanText}（要求窗口：最近 {aSec}s）");
            aR.AppendLine($"- 格數     : {aInfo.Tiles}　**每格 ≈{aInfo.PerTileSeconds:F0}s**");
            AppendRetentionLine(aR);
            aR.AppendLine($"- 感官     : OCR {(aOcrOn ? "開" : "關")}／STT {(aSttOn ? "開" : "關")}（讀自 _config.json）");
            AppendSttLine(aR, aSttOn, aInfo);
            if (aRaw)
                aR.AppendLine($"- 窗口對帳 : **raw=1，刻意未夾** —— 看的是最新畫面；"
                            + (aWmValue > 0 && aInfo.NextCursor > 0
                               ? $"尾端 {FromEpochLocal(aInfo.NextCursor):HH:mm:ss} 超出感官水位 {FromEpochLocal(aWmValue):HH:mm:ss} 約 {(aInfo.NextCursor - aWmValue):F0}s ⇒ 那幾格的「沒字幕」不可信"
                               : $"感官水位：{aWmNote}"));
            else AppendClampAudit(aR, aInfo.NextCursor, aWatermark, aWmNote);
            aR.AppendLine();
            aR.AppendLine("## next");
            aR.AppendLine("- 這是一次性的一眼；**沒有下一步**，也沒有進度可接。要正式看請開場：");
            aR.AppendLine($"  run_cmd.py run StreamWatch --arg step=start --arg persona=<P> --arg until=<HH:mm> --arg media=<work>");
            WritePayload(aPath, aR.ToString());
            Debug.Log($"[StreamWatch] step=peek tiles={aInfo.Tiles} → {aPath}");
        }

        // ===========================================================
        // 區塊：step=start — 守衛 → media 鎖定 → session 註冊 → 開播公告
        // 物理意義：media_id 是**共享鍵**（Plan §4）：先查既有 work，命中就用；沒命中才建新的並回報；
        //          不確定就 blocked 問人 —— 憑印象取 slug 正是製造 work 分裂的那一步，
        //          而分裂之後既有 reader 的心得對新場次永遠隱形且不會有錯誤訊息。
        // ===========================================================
        /// <summary>
        /// 這一場的來源資訊 —— **場次層，不是 work 層**。
        /// 一個 up 主底下有很多支影片：up 主決定 work（誰在講），影片標題/介紹屬於這一場（今天講哪個案子）。
        /// 兩者混在同一層，就會變成「每支影片一個 work」或「所有影片一個 work」——今天兩種都踩過。
        /// </summary>
        struct SourceMeta
        {
            public string Up;           // up 主（bilibili 場必填 —— 它就是 work 的身分）
            public string VideoTitle;   // 這一場看的那支影片標題
            public string VideoDesc;    // 影片介紹（Tim 之後會隨影片一起給）
            public string Url;          // 影片網址（可回溯的原始出處）
        }

        async UniTask StepStart(string iPersona, string iUntil, string iMedia, SourceMeta iSrc, CancellationToken iToken)
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
                aR.AppendLine();
                aR.AppendLine("### bilibili 場（Tim 2026-08-15 拍板）");
                aR.AppendLine("- **鍵按 up 主分**：`media=bilibili-<up主 slug>` ＋ **必帶** `--arg up=<up主名>`");
                aR.AppendLine("- 影片標題／介紹／網址走 `--arg title= / --arg desc= / --arg url=` —— 那是**場次層**，不進 work 名");
                WritePayload(aPath, aR.ToString());
                throw new Exception($"[StreamWatch] step=start blocked：media 未指定（詳見 {aPath}）");
            }

            // 守衛⑤：bilibili 的鍵**按 up 主分**（Tim 2026-08-15 拍板）
            // 物理意義：up 主是「誰在講」，那是跨場不變的身分；影片是「今天講哪一個案子」，一場一個。
            //          ⇒ work 認 up 主。兩種錯法今天各踩過一次：
            //          `bilibili-stream`（**所有 bilibili 併成一個 work** —— 名字比事實大，判準⑤），
            //          以及「每支影片一個 work」（work 爆炸，跨場心得永遠對不上）。
            // ⚠ 這裡擋的是**泛名**，不是所有 bilibili 鍵 —— 擋掉 `bilibili` / `bilibili-stream` 這種
            //   「看起來有指到東西、其實誰都指」的鍵。
            if (iMedia.StartsWith("bilibili", StringComparison.OrdinalIgnoreCase))
            {
                string aTail = iMedia.Length > 8 ? iMedia.Substring(8).Trim('-', '_') : "";
                bool aGeneric = aTail.Length == 0 || aTail == "stream" || aTail == "video" || aTail == "live";
                if (aGeneric || string.IsNullOrEmpty(iSrc.Up))
                {
                    Blocked(aR, aPath,
                        aGeneric ? $"`{iMedia}` 是泛名 —— 它會把**所有 bilibili 影片併成同一個 work**"
                                 : $"bilibili 場必須帶 `--arg up=<up主名>` —— up 主就是這個 work 的身分",
                        $"改成 --arg media=bilibili-<up主 slug> --arg up=<up主名> "
                        + "[--arg title=<影片標題>] [--arg desc=<影片介紹>] [--arg url=<網址>]");
                    aR.AppendLine("> 一個 up 主 = 一個 work（跨場累積心得）；一支影片 = 一場（`title`/`desc`/`url` 記在場次上）。");
                    aR.AppendLine("> 🩸 `bilibili-stream` 是 2026-08-15 我自己取的，當天就被 Tim 打回：**名字比事實大**。");
                    WritePayload(aPath, aR.ToString());
                    throw new Exception($"[StreamWatch] step=start blocked：bilibili 鍵需按 up 主分（詳見 {aPath}）");
                }
            }

            bool aIsNewWork = !WorkExists(iMedia);
            // ⚠ **新 work 要真的建出來**（2026-08-15 實證的洞）：
            //   舊版只印一句「這是新 work」就過去了，從不落檔 ⇒ 下一場的「既有 work 清單」裡
            //   **永遠不會有自己開過的場**（昨天 `bilibili-stream` 開過場，今天清單上找不到它）。
            //   於是那份清單只證明「Library 有什麼」，不證明「觀影用過什麼」，而它的標題讓人以為是後者。
            string aWorkNote = aIsNewWork ? CreateWork(iMedia, iSrc) : "";

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
            // 場次層來源資訊（up 主在 work 那層已經有了，這裡記的是**這一場看的那支**）
            if (!string.IsNullOrEmpty(iSrc.Up)) aSession["up"] = new JsonData(iSrc.Up);
            if (!string.IsNullOrEmpty(iSrc.VideoTitle)) aSession["video_title"] = new JsonData(iSrc.VideoTitle);
            if (!string.IsNullOrEmpty(iSrc.VideoDesc)) aSession["video_desc"] = new JsonData(iSrc.VideoDesc);
            if (!string.IsNullOrEmpty(iSrc.Url)) aSession["source_url"] = new JsonData(iSrc.Url);
            aSession["note_written"] = new JsonData(false);
            aSession["active"] = new JsonData(true);
            aSession["settled_at"] = new JsonData("");
            aSession["end_reason"] = new JsonData("");
            AtomicWrite(SessionPath(iPersona), aSession.ToJsonBeautify());

            // 開播公告（記 start_seq —— 匯出區間的左端點，寫入當下就知道，不必事後回頭數）
            int aMinutes = (int)Math.Max(0, (aUntil - aNow).TotalMinutes);
            var aBody = new StringBuilder();
            aBody.AppendLine($"📺 [{iPersona} 大小姐] 開播觀影 — 看到 **{aUntil:HH:mm}**（約 {aMinutes} 分鐘）｜媒材 `{iMedia}`{(aIsNewWork ? " ⚠ **新 work**" : "")}");
            if (!string.IsNullOrEmpty(iSrc.Up)) aBody.AppendLine($"　UP 主：**{iSrc.Up}**");
            if (!string.IsNullOrEmpty(iSrc.VideoTitle)) aBody.AppendLine($"　本場：{iSrc.VideoTitle}");
            if (!string.IsNullOrEmpty(iSrc.VideoDesc)) aBody.AppendLine($"　簡介：{Truncate(iSrc.VideoDesc, 300)}");
            if (!string.IsNullOrEmpty(iSrc.Url)) aBody.AppendLine($"　出處：{iSrc.Url}");
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
            if (!string.IsNullOrEmpty(aWorkNote)) aR.AppendLine($"- work 建檔: {aWorkNote}");
            if (!string.IsNullOrEmpty(iSrc.Up)) aR.AppendLine($"- UP 主  : **{iSrc.Up}**（work 認這個；影片標題/介紹記在場次上）");
            if (!string.IsNullOrEmpty(iSrc.VideoTitle)) aR.AppendLine($"- 本場影片: {iSrc.VideoTitle}");
            if (!string.IsNullOrEmpty(iSrc.Url)) aR.AppendLine($"- 出處    : {iSrc.Url}");
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
            // ⚠ 首輪沒有 cursor 會踩到雞生蛋（2026-08-15 自由時間實跑抓到）：
            //   cursor=0 ⇒ 不傳 --after-mtime ⇒ 而 montage 的 --before-mtime 過濾與 next-cursor 回報
            //   **都在 after-mtime 那個分支裡**  ⇒ 夾子不生效、cursor 也永遠設不起來，
            //   於是每一輪都退回 `--last` 預設路徑 —— **而回傳檔照樣印「窗口尾端夾在這裡」**。
            //   （旗標被接受卻未套用，跟今天稍早抓到的 `--max-tiles` 在 --last 路徑 no-op 同形。）
            // ⇒ 首輪用 session 起始時刻當 cursor：語意上正確（從開播那一刻看起），
            //   且保證每一輪都走 loop 路徑。
            if (aCursor <= 0)
            {
                DateTime? aSt = ParseIsoLocal(ReadStr(aS, "start_ts"));
                if (aSt.HasValue) aCursor = ToEpoch(aSt.Value.ToUniversalTime());
            }

            if (string.IsNullOrEmpty(aScript))
            {
                Blocked(aR, aPath, "解析不到 screenstream_montage.py（CorePath 空或檔案不存在）",
                        "確認 UCL_Core 掛載位置與 Tools~/AgentCommands/screenstream_montage.py 是否存在");
                throw new Exception($"[StreamWatch] step=cycle blocked：找不到縮圖牆工具（詳見 {aPath}）");
            }

            var (aOcrOn, aSttOn) = ReadSensorFlags();
            double aWatermark = SensorWatermark(aOcrOn, aSttOn, out string aWmNote);
            DateTime aRunStart = DateTime.Now;
            var (aOk, aStdout, aErr) = await RunMontageAsync(aScript, aCursor, aWatermark, aOutPath, aOcrOn, aSttOn, iToken);
            // ⚠ **軟條件的訊息在 stdout，不在 stderr**（2026-08-15 實測：`--before-mtime` 夾出空窗口時
            //   montage `print("ERROR: 選擇條件下無 frame 命中")` ⇒ 走 stdout、exit=1，stderr 全空）。
            //   舊版只比對 stderr ⇒ **這條軟路徑一次都沒被執行過**，每輪都退成 blocked 拋例外，
            //   而「水位還沒追上」是觀影開場的常態（STT 落後 ~29s，剛 start 的第一輪必中）。
            //   同族：檢查寫了但永遠不觸發 —— 跟今天稍早那四隻「旗標被接受卻靜默不套用」同一支。
            // ⇒ 兩條流都比對；exit=2（OCR 水位還沒追上任何 frame）同屬軟條件，一起收。
            string aBoth = (aStdout ?? "") + "\n" + (aErr ?? "");
            if (!aOk && (aBoth.Contains("無 frame 命中") || aBoth.Contains("OCR watermark 還沒趕上")))
            {
                // 不是失敗，是**感官水位還沒追上上一輪的 cursor** —— 等一下再來就有了。
                aR.AppendLine("## 本輪無新素材（不是錯誤）");
                // 同 AppendClampAudit 的判準：印兩個讀數的比較，不印「尚未越過」這種宣告 ——
                // 沒有讀數撐著的話，這一行在「水位真的落後」與「cursor 被算錯」時長得一模一樣。
                aR.AppendLine($"- 上輪 cursor: {(aCursor > 0 ? FromEpochLocal(aCursor).ToString("HH:mm:ss") : "(無)")}");
                aR.AppendLine($"- 感官水位  : {aWmNote}"
                            + (aWatermark > 0 ? $"　⇒ {FromEpochLocal(aWatermark):HH:mm:ss}"
                                                + $"　←　落後 cursor {(aCursor - aWatermark):F0}s" : ""));
                aR.AppendLine("- 意思    : 畫面有，但**字幕/語音還沒辨識到那裡**。看已辨識完的段落是刻意的（Tim 2026-08-15）。");
                aR.AppendLine();
                aR.AppendLine("## next");
                aR.AppendLine($"1. 等 30–60 秒再跑一次 step=cycle（不必改任何參數）。");
                WritePayload(aPath, aR.ToString());
                Debug.Log($"[StreamWatch] step=cycle 感官水位未追上 → {aPath}");
                return;
            }
            if (!aOk)
            {
                aR.AppendLine("## blocked");
                aR.AppendLine($"- reason: 縮圖牆合成失敗 — {aErr}");
                // ⚠ 這支工具的錯誤訊息會走 stdout（見上），所以 stderr 空**不代表沒有原因**。
                //   實測那次回傳檔只印得出 `exit=1;` 後面空白 —— 診斷得再去手跑一次才看得到。
                if (!string.IsNullOrWhiteSpace(aStdout))
                    aR.AppendLine($"- stdout: {Truncate(aStdout.Trim(), 500)}");
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
            AppendSttLine(aR, aSttOn, aInfo);
            AppendClampAudit(aR, aInfo.NextCursor, aWatermark, aWmNote);
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
        // 區塊：step=join — 陪同觀眾（Plan §5）
        // 物理意義：companion 的正確性條件跟 primary **相反** —— primary 的 gap 是失敗，
        //          companion 的 gap 是正常（它挑段細看，不負責覆蓋）。
        //          ⇒ 它**繼承 primary 的 media_id**（一場一個鍵，唯一來源），
        //            並拿到 primary 至今的評論摘要 ＋ 酒館游標，一進場就在同一個劇情點上。
        // ⚠ media_id 不由 companion 自己解析：憑印象取 slug 正是製造 work 分裂的那一步。
        // ===========================================================
        async UniTask StepJoin(string iPersona, CancellationToken iToken)
        {
            string aPath = PayloadPath(iPersona, "join");
            var aR = new StringBuilder();
            aR.AppendLine($"# StreamWatch step=join persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            if (!UCL_AwakeningService.IsOnline(iPersona))
            {
                Blocked(aR, aPath, $"'{iPersona}' 不在線（無 session lock）",
                        $"先跑 run_cmd.py run GoodMorning --arg step=wake --arg persona={iPersona}");
                throw new Exception($"[StreamWatch] step=join blocked：persona 不在線（詳見 {aPath}）");
            }

            var aOwn = LoadSession(iPersona);
            if (aOwn != null && ReadBool(aOwn, "active"))
            {
                Blocked(aR, aPath, "你已經有進行中的觀影 session —— 不疊開",
                        "跑 step=cycle 繼續你自己那場");
                throw new Exception($"[StreamWatch] step=join blocked：已有 session（詳見 {aPath}）");
            }

            // 找一個進行中的 primary（不是自己）
            JsonData aPrimary = null; string aPrimaryPersona = "";
            try
            {
                string aDir = Path.Combine(UCL_AgentCommandsPath.DataRoot, "StreamWatch", "sessions");
                if (Directory.Exists(aDir))
                {
                    foreach (var f in Directory.GetFiles(aDir, "*.json"))
                    {
                        string aWho = Path.GetFileNameWithoutExtension(f);
                        if (aWho == iPersona) continue;
                        var aJd = JsonData.ParseJson(File.ReadAllText(f, Encoding.UTF8));
                        if (aJd == null || !ReadBool(aJd, "active")) continue;
                        if (ReadStr(aJd, "role") != "primary") continue;
                        aPrimary = aJd; aPrimaryPersona = aWho; break;
                    }
                }
            }
            catch { }

            if (aPrimary == null)
            {
                Blocked(aR, aPath, "找不到進行中的主觀影場",
                        $"自己開一場：run_cmd.py run StreamWatch --arg step=start --arg persona={iPersona} --arg until=<HH:mm> --arg media=<work>");
                throw new Exception($"[StreamWatch] step=join blocked：無 primary 場（詳見 {aPath}）");
            }

            string aMedia = ReadStr(aPrimary, "media_id");
            string aSessionId = $"sw-{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{iPersona}";
            var aS = new JsonData();
            aS["persona"] = new JsonData(iPersona);
            aS["session_id"] = new JsonData(aSessionId);
            aS["role"] = new JsonData("companion");
            aS["media_id"] = new JsonData(aMedia);            // ← 繼承，不自己解析
            aS["parent_session_id"] = new JsonData(ReadStr(aPrimary, "session_id"));
            aS["parent_persona"] = new JsonData(aPrimaryPersona);
            aS["start_ts"] = new JsonData(UCL_AwakeningService.NowIso());
            aS["end_ts"] = new JsonData(ReadStr(aPrimary, "end_ts"));   // 沿用 primary 的截止
            aS["until_local"] = new JsonData(ReadStr(aPrimary, "until_local"));
            aS["cursor_epoch"] = new JsonData(ReadDouble(aPrimary, "cursor_epoch"));
            aS["cycles"] = new JsonData(0);
            aS["observations"] = new JsonData(0);
            aS["start_seq"] = new JsonData(0);
            aS["end_seq"] = new JsonData(0);
            aS["note_written"] = new JsonData(false);
            aS["active"] = new JsonData(true);
            aS["settled_at"] = new JsonData("");
            aS["end_reason"] = new JsonData("");
            AtomicWrite(SessionPath(iPersona), aS.ToJsonBeautify());

            var aBody = new StringBuilder();
            aBody.AppendLine($"🍿 [{iPersona} 大小姐] 加入觀影 — 陪同 @{aPrimaryPersona} 的場｜媒材 `{aMedia}`");
            aBody.AppendLine();
            aBody.AppendLine("陪同觀眾**挑段細看**，主劇情由主觀影者在酒館帶 —— gap 對我是正常的，不是漏看。");
            int aSeq = await TavernPost(iPersona, aBody.ToString(), "watch-join", iToken);
            if (aSeq > 0) { aS["start_seq"] = new JsonData(aSeq); AtomicWrite(SessionPath(iPersona), aS.ToJsonBeautify()); }

            aR.AppendLine($"- session : `{aSessionId}`（role=**companion**）");
            aR.AppendLine($"- 陪同    : @{aPrimaryPersona}（{ReadStr(aPrimary, "session_id")}）");
            aR.AppendLine($"- media   : `{aMedia}`　←　**繼承 primary，不自己解析**（一場一個鍵）");
            aR.AppendLine($"- 截止    : {ReadStr(aPrimary, "until_local")}（沿用 primary）");
            aR.AppendLine($"- primary 進度: 已 {ReadInt(aPrimary, "cycles")} 輪／{ReadInt(aPrimary, "observations")} 筆評論");
            aR.AppendLine($"- 加入公告: {(aSeq > 0 ? $"seq **{aSeq}**" : "未發（best-effort）")}");
            aR.AppendLine();
            aR.AppendLine("## 你的不變式跟 primary **不一樣**");
            aR.AppendLine("- primary：連續覆蓋，gap ＝ 失敗");
            aR.AppendLine("- **你（companion）：自由取樣，gap ＝ 正常** —— 挑段細看，主劇情靠酒館追");
            aR.AppendLine();
            aR.AppendLine("## next");
            aR.AppendLine($"1. 取素材：run_cmd.py run StreamWatch --arg step=cycle --arg persona={iPersona}");
            aR.AppendLine($"2. 讀主觀影者的劇情線：run_cmd.py run Tavern --arg op=read --arg room=tavern --arg limit=20");
            aR.AppendLine($"3. 發評論：run_cmd.py run StreamWatch --arg step=observe --arg persona={iPersona} --arg-file body=<評論>");
            WritePayload(aPath, aR.ToString());
            Debug.Log($"[StreamWatch] step=join {iPersona} → 陪同 {aPrimaryPersona} media={aMedia}");
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
            // ⚠ 而中斷路徑上，`iNow` 是**發現時刻**不是停止時刻 —— 兩者差多久取決於 agent 多久才回來。
            //   🩸 2026-08-15 實測：錄影停於 21:10:02，我 21:16:33 才回來收 ⇒ 付到 ends_at 21:14（多付 1 token）。
            //   ⇒ 改讀寫入端戳的顯式欄位 `enabled_changed_at`；讀不到就退回發現時刻並**在回傳檔明說那是上限估計**
            //     （不得靜默用估計值當事實）。
            string aStopNote = "";
            DateTime aPaidUntil;
            if (iByInterrupt)
            {
                DateTime? aStopped = RecordingStoppedAt();
                bool aUsable = aStopped.HasValue && aStart.HasValue
                               && aStopped.Value >= aStart.Value && aStopped.Value <= iNow;
                if (aUsable)
                {
                    aPaidUntil = aStopped.Value;
                    aStopNote = $"（錄影停於 {aStopped.Value:HH:mm:ss}，**讀自 `enabled_changed_at`**；發現於 {iNow:HH:mm:ss}）";
                }
                else
                {
                    aPaidUntil = iNow;
                    aStopNote = aStopped.HasValue
                        ? $"（⚠ `enabled_changed_at`={aStopped.Value:HH:mm:ss} 落在本場之外，不採用 —— 付到發現時刻，**上限估計**）"
                        : "（⚠ 讀不到 `enabled_changed_at` —— 付到發現時刻，**這是上限估計不是實際停止時刻**）";
                }
            }
            else aPaidUntil = iEnd ?? iNow;
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
            if (!string.IsNullOrEmpty(aStopNote))
                ioR.AppendLine($"- 計費上限: 付到 {aPaidUntil:HH:mm:ss} {aStopNote}");
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

            // ⚠ **收工後補寫必須放行**（2026-08-15 實跑撞到）：cycle 的收工分支會印
            //   「本場未寫接續點 —— 要補：跑 step=note」，而**那一步剛好把 session 關掉了** ⇒
            //   舊版在這裡擋掉 ⇒ 它指的是一條它自己封死的路，接續點永遠補不上。
            //   （比靜默失效更難看的一種：**指路存在、而且會大聲失敗**。）
            // ⇒ 沒有 active session 時退回**最近一場已關閉的 session**補寫，並在發文與回傳檔
            //   雙邊標明「補寫」與那場的結束時刻 —— 不能讓補的接續點看起來像當場寫的。
            var aS = LoadSession(iPersona);
            if (aS == null)
            {
                Blocked(aR, aPath, "查無任何觀影 session（連已結束的都沒有）",
                        $"先跑 run_cmd.py run StreamWatch --arg step=start --arg persona={iPersona} --arg until=<HH:mm> --arg media=<work>");
                throw new Exception($"[StreamWatch] step=note blocked：無 session 檔（詳見 {aPath}）");
            }
            bool aLate = !ReadBool(aS, "active");
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
            // ⚠ 欄位名是 `settled_at`（SettleAsync 寫的），不是 `ended_at` ——
            //   讀錯欄位時 ReadStr 靜默回空，回傳檔就會印「結束時刻未記」這句**假話**。
            string aEnded = ReadStr(aS, "settled_at");
            var aBody = new StringBuilder();
            aBody.AppendLine($"📌 [{iPersona} 大小姐] 觀影接續點 — 媒材 `{aMedia}`"
                           + (aLate ? "　**（補寫：本場已於收工時結算）**" : ""));
            if (aLate && !string.IsNullOrEmpty(aEnded))
                aBody.AppendLine($"　　場次結束於 `{aEnded}` —— 這段文字寫在收工之後，不是當場記的。");
            aBody.AppendLine();
            aBody.AppendLine(iBody.TrimEnd());
            int aSeq = await TavernPost(iPersona, aBody.ToString(), "watch-note", iToken);

            aS["note_written"] = new JsonData(true);
            aS["note_seq"] = new JsonData(aSeq);
            if (aLate) aS["note_late"] = new JsonData(true);
            AtomicWrite(SessionPath(iPersona), aS.ToJsonBeautify());

            aR.AppendLine($"- 接續點已寫並發布: {(aSeq > 0 ? $"seq **{aSeq}**" : "發文失敗（**接續點仍已記進 session**）")}");
            aR.AppendLine($"- media: `{aMedia}`");
            if (aLate) aR.AppendLine($"- ⚠ **補寫**：本場已結算收工（{(string.IsNullOrEmpty(aEnded) ? "結束時刻未記" : aEnded)}）；已標 `note_late=true`，不冒充當場寫的");
            aR.AppendLine();
            aR.AppendLine("## next");
            if (aLate)
                aR.AppendLine("1. 本場已收工，這筆補寫就是最後一步 —— 沒有下一步。");
            else
            {
                aR.AppendLine($"1. 跑 run_cmd.py run StreamWatch --arg step=cycle --arg persona={iPersona}");
                aR.AppendLine("   —— 若已到期／Tim 已停錄影，那一步會完成收工；否則會繼續給下一輪素材。");
            }
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
            string iScript, double iCursor, double iBefore, string iOutPath, bool iOcr, bool iStt, CancellationToken iToken)
        {
            try
            {
                var aArgs = new StringBuilder();
                aArgs.Append("\"").Append(iScript).Append("\" make");
                if (iCursor > 0) aArgs.Append(" --after-mtime ").Append(iCursor.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                if (iBefore > 0) aArgs.Append(" --before-mtime ").Append(iBefore.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
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
            // STT 原句（montage 的 `stt :` 那行）—— **原樣搬，不由 C# 改寫**：
            // 「0 段」與「無 cache」是兩件事，而它們在 python 端已經分開了，
            // 這裡任何重新措辭都可能把那個區別磨平（2026-08-15 血證：本輪無語音／STT 沒接上同形）。
            public string SttRaw;
            public string SttWarn;
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
            // ⚠ `stt` 那行**可有可無**：沒有它不代表「本輪沒語音」，代表**這一輪根本沒跑 STT**。
            //   兩者在舊回傳檔上同形（都是不印），所以這裡分開帶，呼叫端才有辦法說出區別。
            var aStt = System.Text.RegularExpressions.Regex.Match(iStdout, @"^\s*stt\s*:\s*(.+)$",
                                                                 System.Text.RegularExpressions.RegexOptions.Multiline);
            if (aStt.Success) aInfo.SttRaw = aStt.Groups[1].Value.Trim();
            var aSttW = System.Text.RegularExpressions.Regex.Match(iStdout, @"^\s*(⚠ STT 段渲染失敗.*)$",
                                                                  System.Text.RegularExpressions.RegexOptions.Multiline);
            if (aSttW.Success) aInfo.SttWarn = aSttW.Groups[1].Value.Trim();
            return aInfo;
        }

        // ===========================================================
        // 區塊：ScreenStream 狀態 —— **只認顯式欄位**
        // ⚠ 不用 frame 新鮮度推論（Plan §2.2 的血證）
        // ===========================================================
        // ===========================================================
        // 區塊：感官水位 —— 看「已經辨識完的那一段」，不是「最新的那一段」（Tim 2026-08-15）
        // 物理意義：OCR / STT 是**落後於 frame** 的（2026-08-15 實測：OCR ~1s、STT ~29s）。
        //          窗口若追到最新幀，尾端那幾格必然沒有字幕與語音 —— 而 sidecar 只是**少那幾行**，
        //          於是「這一格沒有語音」與「這一格還沒被辨識」在輸出上**同形**
        //          （2026-08-15 首場實踩：我花了幾分鐘才分出 STT 是沒接上還是沒內容）。
        // ⇒ 把窗口尾端夾在 min(OCR 水位, STT 水位) 上：**看到的每一格都有完整感官資料。**
        // 數值影響：代價是畫面延遲數十秒 —— 觀影不是即時監控，看得清楚比看得即時重要。
        // 邊界：感官關閉時該項不參與取小；兩項都關 ⇒ 回 0（不夾，行為同從前）。
        // ===========================================================
        static double SensorWatermark(bool iOcr, bool iStt, out string oNote)
        {
            double aWm = 0; var aParts = new List<string>();
            try
            {
                string aRoot = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream");
                if (iOcr)
                {
                    var aDir = new DirectoryInfo(Path.Combine(aRoot, "ocr"));
                    if (aDir.Exists)
                    {
                        double aO = 0;
                        foreach (var f in aDir.GetFiles())
                        {
                            double t = ToEpoch(f.LastWriteTimeUtc);
                            if (t > aO) aO = t;
                        }
                        if (aO > 0) { aWm = (aWm <= 0 ? aO : Math.Min(aWm, aO)); aParts.Add($"OCR {FromEpochLocal(aO):HH:mm:ss}"); }
                    }
                }
                if (iStt)
                {
                    var aDir = new DirectoryInfo(Path.Combine(aRoot, "stt"));
                    if (aDir.Exists)
                    {
                        double aS2 = 0;
                        foreach (var f in aDir.GetFiles("stt_*.json"))
                        {
                            // 檔名帶 epoch 毫秒 —— 用檔名不用 mtime：那是**內容代表的時刻**，
                            // mtime 只是它被寫下的時刻，兩者在補寫/搬移時會分家。
                            string aStem = Path.GetFileNameWithoutExtension(f.Name);
                            int aUs = aStem.IndexOf('_');
                            if (aUs >= 0 && long.TryParse(aStem.Substring(aUs + 1), out long aMs))
                            {
                                double t = aMs / 1000.0;
                                if (t > aS2) aS2 = t;
                            }
                        }
                        if (aS2 > 0) { aWm = (aWm <= 0 ? aS2 : Math.Min(aWm, aS2)); aParts.Add($"STT {FromEpochLocal(aS2):HH:mm:ss}"); }
                    }
                }
            }
            catch { }
            oNote = aParts.Count > 0 ? string.Join("／", aParts) : "(無 cache，未夾尾端)";
            return aWm;
        }

        // ===========================================================
        // 區塊：窗口對帳 —— **印比較結果，不印意圖**（2026-08-15 血債 #4 的正解）
        // 物理意義：夾子（--before-mtime）有沒有生效，是一個**兩個讀數的比較**，
        //          而舊版印的是「窗口尾端夾在這裡」—— 那是一句宣告，它在夾子完全沒生效的那一輪
        //          （首輪 cursor=0 ⇒ 整個過濾分支沒進去）**照樣印得一模一樣**。
        //          ⇒ 沒做卻報告做了，而回傳檔是我唯一的事後證據。
        // 數值影響：純輸出；判定門檻放 1 秒（frame mtime 與水位取樣本就不同源，
        //          差在秒內不算沒夾）。壞要往吵的方向壞：讀不到就說讀不到，不填「大概有」。
        // 邊界：水位 ≤ 0 ＝ 沒夾（感官全關或無 cache）；窗口尾端 ≤ 0 ＝ montage 沒回報 next-cursor
        //      ⇒ 兩者都**明說無法對帳**，不得沉默通過。
        // ===========================================================
        static void AppendClampAudit(StringBuilder ioR, double iWindowEnd, double iWatermark, string iWmNote)
        {
            if (iWatermark <= 0)
            {
                ioR.AppendLine($"- 窗口對帳 : **未夾** —— 感官水位讀不到（{iWmNote}）⇒ 尾端可能落在還沒辨識完的段落");
                return;
            }
            if (iWindowEnd <= 0)
            {
                ioR.AppendLine($"- 窗口對帳 : ⚠ **無讀數** —— montage 沒回報 next-cursor，"
                             + $"無法確認夾子是否生效（水位 {FromEpochLocal(iWatermark):HH:mm:ss}）。**當成沒生效看待。**");
                return;
            }
            double aDiff = iWatermark - iWindowEnd;   // 正 = 尾端在水位之內（正確）
            ioR.AppendLine(aDiff >= -1.0
                ? $"- 窗口對帳 : 窗口尾端 {FromEpochLocal(iWindowEnd):HH:mm:ss} ≤ 水位 {FromEpochLocal(iWatermark):HH:mm:ss} ✅（夾子生效，餘裕 {aDiff:F0}s）"
                : $"- 窗口對帳 : ⚠ 窗口尾端 {FromEpochLocal(iWindowEnd):HH:mm:ss} **>** 水位 {FromEpochLocal(iWatermark):HH:mm:ss} ❌ "
                  + $"**夾子沒生效** —— 尾端那 {-aDiff:F0}s 沒有完整感官資料，那幾格的「沒字幕」不可信");
            ioR.AppendLine($"　　　　　　 （水位來源：{iWmNote}）");
        }

        // 區塊職責：STT 段數回報 —— 讓「本輪沒語音」與「STT 沒接上」不再同形（2026-08-15 見叢 ①）
        // ⚠ montage 的原句原樣搬（`0 段` / `無 cache …`）：那個區別是 python 端分出來的，
        //   在這裡重新措辭等於把它磨平。C# 只補「這一行不存在」那一格 —— 那是第三種狀態。
        static void AppendSttLine(StringBuilder ioR, bool iSttOn, MontageInfo iInfo)
        {
            if (!string.IsNullOrEmpty(iInfo.SttWarn)) { ioR.AppendLine($"- STT      : {iInfo.SttWarn}"); return; }
            if (!string.IsNullOrEmpty(iInfo.SttRaw)) { ioR.AppendLine($"- STT      : {iInfo.SttRaw}"); return; }
            ioR.AppendLine(iSttOn
                ? "- STT      : ⚠ **本輪沒有 STT 回報** —— config 說 STT 開著，而 montage 一行都沒印。"
                  + "這**不是**「沒有語音」，是這一輪的語音管線沒跑起來"
                : "- STT      : 關（`_config.json` 的 `stt_enabled=false`）—— 沒有語音段是預期的，不是故障");
        }

        static double ToEpoch(DateTime iUtc) => (iUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        static DateTime FromEpochLocal(double iEp) => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(iEp).ToLocalTime();

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

        /// <summary>
        /// 錄影最後一次 enabled 翻轉的時刻（寫入端戳的顯式欄位 `enabled_changed_at`）。
        /// <para>⚠ 它是「翻轉」時刻，不專指「停止」—— 呼叫端必須自己確認現在 enabled=false
        /// 且該時刻落在本場區間內才可採用；不可假設它一定是停止（開播也會戳）。</para>
        /// 讀不到回 null —— 由呼叫端決定怎麼說，不在這裡塞預設值當事實。
        /// </summary>
        static DateTime? RecordingStoppedAt()
        {
            try
            {
                string aCfg = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", "_config.json");
                if (!File.Exists(aCfg)) return null;
                var aJd = JsonData.ParseJson(File.ReadAllText(aCfg, Encoding.UTF8));
                if (aJd == null || !aJd.Contains("enabled_changed_at")) return null;
                return ParseIsoLocal(aJd["enabled_changed_at"].ToString());
            }
            catch { return null; }
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

        // ===========================================================
        // 區塊：保存期 —— **名目上限**與**現在真的有多少**是兩個數
        // 物理意義：名目＝`max_frames / fps`（兩者都讀後台設定 `_config.json`，不寫死 ——
        //          舊 skill 寫死 600s 而實際是 2400s，差四倍，還拿那個數去教間隔紀律）。
        //          但名目**不是上限，也不是下限** —— 它只是設定值的換算，兩個方向都會失準：
        //          ① 剛開播時 buffer 沒滿 ⇒ 實有遠小於名目（開播前 N 分鐘內「可回看 40 分鐘」是假的）
        //          ② buffer 滿了但**實際擷取速率低於設定 fps** ⇒ 同樣張數涵蓋更長時間，實有反而大於名目
        //             （2026-08-15 首次雙印就撞到：名目 2400s、實有 2472s / 2400 張）。
        //          ⇒ 名目回答「設定要留多久」，實有回答「現在真的回得去多久」。要後者就別看前者。
        // 數值影響：實有＝磁碟上最舊 frame 到現在的秒數（讀 mtime，不用檔案數推算 ——
        //          daemon 重啟／手動清檔都會讓「檔案數 × fps」跟真實時間分家）。
        // 邊界：讀不到就把**原因**印出來（壞要往吵的方向壞）；不得只印「讀取失敗」。
        // ===========================================================
        static void AppendRetentionLine(StringBuilder ioR)
        {
            try
            {
                string aCfg = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", "_config.json");
                var aJd = JsonData.ParseJson(File.ReadAllText(aCfg, Encoding.UTF8));
                int aMax = aJd.Contains("max_frames") ? int.Parse(aJd["max_frames"].ToString()) : 0;
                int aFps = aJd.Contains("fps") ? int.Parse(aJd["fps"].ToString()) : 1;
                if (aFps <= 0) aFps = 1;
                string aHave = ActualBufferSpan();
                ioR.AppendLine($"- 保存期   : 名目 {aMax / aFps}s（{aMax} frames / {aFps} fps，**讀自後台設定不寫死**）"
                             + (string.IsNullOrEmpty(aHave) ? "" : $"｜{aHave}"));
            }
            catch (Exception e) { ioR.AppendLine($"- 保存期   : ⚠ 讀取失敗（{e.Message}）—— **別把它當成沒有上限**"); }
        }

        /// <summary>磁碟上最舊 frame 到現在 ＝ 現在真的回得去多久。讀不到回空字串（由呼叫端決定怎麼說）。</summary>
        static string ActualBufferSpan()
        {
            try
            {
                var aDir = new DirectoryInfo(Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", "frames"));
                if (!aDir.Exists) return "";
                DateTime aOldest = DateTime.MaxValue; int aN = 0;
                foreach (var f in aDir.GetFiles("*.jpg"))
                {
                    aN++;
                    if (f.LastWriteTime < aOldest) aOldest = f.LastWriteTime;
                }
                if (aN == 0) return "**buffer 空**（磁碟上 0 張 frame）";
                return $"實有 {(DateTime.Now - aOldest).TotalSeconds:F0}s（{aN} 張，最舊 {aOldest:HH:mm:ss}）";
            }
            catch { return ""; }
        }

        // ===========================================================
        // 區塊：Library work 解析 —— media_id 是共享鍵（Plan §4）
        // ===========================================================
        static string WorksRoot() => Path.Combine(UCL_AgentCommandsPath.DataRoot, "BookNotes", "Library", "works");

        static bool WorkExists(string iSlug)
        {
            try { return Directory.Exists(Path.Combine(WorksRoot(), iSlug)); } catch { return false; }
        }

        // ===========================================================
        // 區塊：新 work 落檔 —— 開過的場要能被下一場查得到
        // 物理意義：`work.json` 是 Library 既有 schema（work_id / title / author / aliases / genre_tags），
        //          照它寫，不另創格式 —— 觀影與閱讀共用同一份 work 身分才叫共享鍵。
        // ⚠ fail-soft：建檔失敗**不擋開場**（看直播不該被檔案系統擋住），但要把失敗字串帶回回傳檔 ——
        //   靜默失敗的話，下一場又會看到「這是新 work」而永遠不知道為什麼建不起來。
        // 數值影響：只寫 work 身分層（up 主／別名）。**影片標題與介紹不寫進來** ——
        //   那是場次層，寫進 work 會讓 work 的 title 隨最後一場漂移。
        // ===========================================================
        static string CreateWork(string iSlug, SourceMeta iSrc)
        {
            try
            {
                string aDir = Path.Combine(WorksRoot(), iSlug);
                string aFile = Path.Combine(aDir, "work.json");
                if (File.Exists(aFile)) return $"已存在，未覆寫（`{aFile}`）";
                Directory.CreateDirectory(aDir);

                string aTitle = string.IsNullOrEmpty(iSrc.Up) ? iSlug : iSrc.Up;
                var aJd = new JsonData();
                aJd["work_id"] = new JsonData(iSlug);
                aJd["title"] = new JsonData(aTitle);
                aJd["author"] = new JsonData(string.IsNullOrEmpty(iSrc.Up) ? "" : iSrc.Up);
                var aAliases = new JsonData();
                if (!string.IsNullOrEmpty(iSrc.Up)) aAliases.Add(new JsonData(iSrc.Up));
                aJd["aliases"] = aAliases;
                var aTags = new JsonData();
                if (iSlug.StartsWith("bilibili", StringComparison.OrdinalIgnoreCase))
                {
                    aTags.Add(new JsonData("bilibili"));
                    aTags.Add(new JsonData("直播/影片頻道"));
                }
                aJd["genre_tags"] = aTags;
                aJd["schema_version"] = new JsonData(1);
                AtomicWrite(aFile, aJd.ToJsonBeautify());
                return $"已建立 `{aFile}`（title=`{aTitle}`）—— 下一場起這個鍵會出現在既有清單裡";
            }
            catch (Exception e)
            {
                // 壞要往吵的方向壞：不擋開場，但這句必須被印出來
                return $"⚠ **建檔失敗**（不擋開場，但下一場仍會被當成新 work）：{e.Message}";
            }
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
