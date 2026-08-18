// 區塊職責：Cmd_StreamWatch — 觀影模式的 Cmd 入口（Plan_StreamWatch_Cmd.md，Tim 2026-08-15 拍板全部規格）。
//          同一支 Cmd 以 step 分步：start（守衛＋media 鎖定＋註冊 ends_at＋開播公告）→
//          cycle（取素材＋到期/中斷判定＋狀態相依 next）→ observe → note。
// 物理意義：**沒有 step=end** —— agent 不能自己結束 session；兩個終止（到期／Tim 停錄影）
//          都由 cycle 對系統時鐘與 _screenstream/_config.json 的 enabled 判定。
//          「自動」指的是**判斷自動**（Cmd 算好告訴你），不是觸發自動 —— 不新增任何常駐偵測。
// 數值影響：session state 落 <DataRoot>/StreamWatch/sessions/<persona>.json（C# 唯一寫入端）；
//          回傳檔 letters/<persona>/cmd/streamwatch_<step>.md（路徑經 ReportOutputFile 進 result outputs）。
// ⚠ 阻塞紀律（Tim 2026-08-15 指示 + WorkMemory/unitask-editor-async）：
//   縮圖牆是外部 process，**一律 await Task.Run 包起來**，不得在主執行緒輪詢 WaitForExit。
//   照抄 UCL_BartenderDaemon.RunBalanceQuery 會自動繼承它的同步性（那支因 out 參數不可能 async）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using UCL.Core.JsonLib;
using UCL.Core.EditorLib.AgentCommands.Treasury;
using UCL.Core.EditorLib.AgentCommands.ReadingLibrary;
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
            "step=prepare|capture|peek|start|catchup|join|cycle|observe|note|hotspot|claim (必填) | persona=<name> — **peek 以外全步驟必填**（peek 不帶則歸 _peek） | "
          + "title=<片名> ＋ episode=<第幾集> — **prepare 必填**（media_id 明示時 title 可省）；prepare 會查既有媒材 id、不發明 | "
          + "media_id=<既有媒材 id> — prepare 選填（命中 ≥2 筆或 0 筆時必填）／catchup 必填 | "
          + "reference_reader=<persona> — prepare 選填（接續基準；並列最多章時必填） | "
          + "catchup_map=\"0001=persona,0002=persona\" — prepare 選填（基準者缺的集數由主觀影者指定來源） | "
          + "start_recording=false — prepare 選填（預設會在未錄影時自動開播；先填節目名再開） | "
          + "on=1|0 — capture 必填（開/關錄影；串 UCL_ScreenStreamPage 同一條規則） | " +
            "seconds=<5..600> — peek 選填，看最近幾秒（預設 60） | raw=1 — peek 選填，不夾感官水位（看最新畫面，代價寫在回傳檔） | " +
            "until=<HH:mm 本地> — start 必填 | media=<work-slug> — start 選填（不給則由 Cmd 問；" +
            "**bilibili 一律 `bilibili-<up主 slug>` 並必帶 up=**） | " +
            "up=<up主名> / title=<影片標題> / desc=<影片介紹> / url=<網址> — start 選填（bilibili 場 up 必填） | " +
            "body=<內文> — observe/note 必填（長文走 --arg-file） | " +
            "回傳落檔 letters/<persona>/cmd/streamwatch_<step>.md（路徑隨 run_cmd verdict 印出）";

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
                case "peek": await StepPeek(args, aPersona, GetArg(args, "seconds", "").Trim(),
                                            GetArg(args, "raw", "").Trim(), token); return;
                case "capture": StepCapture(args, aPersona, GetArg(args, "on", "").Trim()); return;
                case "prepare": await StepPrepare(args, aPersona, token); return;
                case "catchup": StepCatchup(args, aPersona); return;
                case "start": await StepStart(args, aPersona, GetArg(args, "until", "").Trim(),
                                              GetArg(args, "media", "").Trim(),
                                              new SourceMeta
                                              {
                                                  Up = GetArg(args, "up", "").Trim(),
                                                  VideoTitle = GetArg(args, "title", "").Trim(),
                                                  VideoDesc = GetArg(args, "desc", "").Trim(),
                                                  Url = GetArg(args, "url", "").Trim(),
                                              }, token); return;
                case "cycle": await StepCycle(args, aPersona, token); return;
                case "observe": await StepObserve(args, aPersona, GetArg(args, "body", ""), token); return;
                case "note": await StepNote(args, aPersona, GetArg(args, "body", ""), token); return;
                case "join": await StepJoin(args, aPersona, token); return;
                case "hotspot": await StepHotspot(args, aPersona, GetArg(args, "from", "").Trim(),
                        GetArg(args, "to", "").Trim(), GetArg(args, "why", ""), token); return;
                case "claim": await StepClaim(args, aPersona, GetArg(args, "hotspot", "").Trim(), token); return;
                default:
                    throw new Exception($"[StreamWatch] step 必為 prepare|peek|capture|start|join|catchup|cycle|observe|note|hotspot|claim（got '{aStep}'）。ArgsSchema: {ArgsSchema}");
            }
        }

        // ===========================================================
        // 區塊：step=prepare — **主觀影者的準備階段**（Tim 2026-08-17 拍板）
        // 物理意義：開場前把「這場在看什麼」釘死成一個 id，讓開播公告／實錄章／閱讀心得三處指向同一個東西。
        //   ⚠ 這一步存在的理由是**漂移**：媒材 id 若由每個人各自打字，會長出
        //   `anim-apocalypse-hotel` / `apocalypse-hotel` / `apocalypse_hotel` 三個平行宇宙，
        //   而三邊各自都能運作、都不報錯（「找到另一個宇宙的檔」那一族）。
        // 硬規則三條：
        //   ① **id 不准發明** —— 先查閱讀庫既有媒材（id/別名/work），1 筆才用；0 筆要 --media_id 明示；
        //      ≥2 筆**停下來列清單**（猜一個等於替 Tim 選了平行宇宙）。
        //   ② **先填節目名再開錄影** —— 反序的話開播公告已經送出去，標題追不回（公告不可 amend）。
        //   ③ 準備完成才輪到陪同者進場（step=join 會檢查本檔）—— 這樣他們一進來 media_id 就已經是定值。
        // 數值影響：寫 `StreamWatch/prepared/<media_id>.json`（含 catchup_map），不動任何 session；
        //   零 token（準備不是觀影）。
        // ===========================================================
        static string PreparedPath(string iMediaId)
            => Path.Combine(UCL_AgentCommandsPath.DataRoot, "StreamWatch", "prepared", $"{iMediaId}.json");

        static JsonData LoadPrepared(string iMediaId)
        {
            try
            {
                string aP = PreparedPath(iMediaId);
                return File.Exists(aP) ? JsonData.ParseJson(File.ReadAllText(aP, Encoding.UTF8)) : null;
            }
            catch { return null; }
        }

        /// <summary>某 reader 已有哪些章（chapter id 昇冪）。讀目錄本身 —— 不推 reader.json 的 progress
        /// （progress 是「最後讀到哪」，不等於「哪幾章有心得」，兩者曾經不一致）。</summary>
        static List<string> ReaderChapters(string iMediaId, string iPersona)
        {
            var aOut = new List<string>();
            try
            {
                string aDir = Path.Combine(UCL_ReadingLibraryIO.ReaderRoot(iMediaId, iPersona), "chapters");
                if (!Directory.Exists(aDir)) return aOut;
                foreach (var d in Directory.GetDirectories(aDir))
                {
                    string aName = Path.GetFileName(d);
                    if (UCL_ReadingLibraryIO.IsValidChapterId(aName)) aOut.Add(aName);
                }
                aOut.Sort(StringComparer.Ordinal);
            }
            catch { }
            return aOut;
        }

        // ===========================================================
        // 區塊職責：把使用者給的那一個字串，解析成 (work_id, 閱讀庫 media_id)。
        // 物理意義：這個系統裡有**兩種鍵**長得很像 —— `apocalypse-hotel`(work) 與
        //   `anim-apocalypse-hotel`(media_id)。人（含我）會混用，而混用的後果是無聲的：
        //   把 media_id 當 work ⇒ works/ 底下生出一個假 work，兩邊各自都能寫、都不報錯。
        // 🩸 2026-08-17 實跑：prepare 的 next 印了 media_id，start 照著建了假 work（我自己踩的）。
        // 數值影響：純唯讀（讀 media.json / 掃 media 清單），不建立任何東西；
        //   ⚠ 一個 work 底下有多個 media 時**不自動選**（章號會落進哪份心得不能猜），只回說明。
        // 判準：能查出來的就不要問人；查不到才算新東西，而那一刻呼叫端要吵。
        // ===========================================================
        public static void ResolveWatchTarget(string iKey, out string oWorkId, out string oLibMediaId, out string oNote)
        {
            oWorkId = iKey; oLibMediaId = ""; oNote = "";
            if (string.IsNullOrEmpty(iKey)) { oNote = "（空鍵，不解析）"; return; }
            try
            {
                string aMediaJson = Path.Combine(UCL_ReadingLibraryIO.MediaRoot(iKey), "media.json");
                if (File.Exists(aMediaJson))
                {
                    var aMj = UCL_ReadingLibraryIO.LoadJson(aMediaJson, out string aErr);
                    string aWid = (aMj != null && aMj.Contains("work_id")) ? aMj["work_id"].GetString() : "";
                    if (!string.IsNullOrEmpty(aWid))
                    {
                        oWorkId = aWid; oLibMediaId = iKey;
                        oNote = $"`{iKey}` 是**既有 media_id** ⇒ 自動解析出 work `{aWid}`（讀自 media.json，沒有新建任何 work）";
                        return;
                    }
                }
                var aOwn = new List<string>();
                foreach (var m in UCL_ReadingLibraryIO.ListMediaEntries())
                    if ((m.WorkId ?? "") == iKey) aOwn.Add(m.MediaId);
                if (aOwn.Count == 1)
                {
                    oLibMediaId = aOwn[0];
                    oNote = $"`{iKey}` 是既有 work ⇒ 底下唯一的 media `{oLibMediaId}` 已自動填入（寫心得不必再打）";
                }
                else if (aOwn.Count > 1)
                    oNote = $"`{iKey}` 底下有 {aOwn.Count} 個 media（{string.Join(" / ", aOwn)}）—— **不自動選**，寫心得時要指定哪一個";
                else
                    oNote = $"`{iKey}` 在閱讀庫查不到對應 media ⇒ 視為新東西（呼叫端負責吵）";
            }
            catch (Exception e) { oNote = $"解析失敗（fail-soft，當成新東西）：{e.Message}"; }
        }

        /// <summary>給 Cmd_Invoke 用的可讀版 —— 一行字串，方便不開 session 就量解析結果。</summary>
        public static string ResolveWatchTargetDebug(string iKey)
        {
            ResolveWatchTarget(iKey, out string w, out string m, out string n);
            return $"key={iKey} → work={w} / library_media_id={(string.IsNullOrEmpty(m) ? "(none)" : m)} / note={n}";
        }

        /// <summary>解析媒材 id：查既有的，不發明。回傳命中清單（0/1/N 由呼叫端決定怎麼辦）。</summary>
        static List<string> ResolveMediaCandidates(string iQuery)
        {
            var aHit = new List<string>();
            if (string.IsNullOrEmpty(iQuery)) return aHit;
            string aQ = iQuery.Trim().ToLowerInvariant();
            try
            {
                foreach (var m in UCL_ReadingLibraryIO.ListMediaEntries())
                {
                    string aId = m.MediaId ?? "";
                    string aWork = m.WorkId ?? "";
                    string aTitle = m.Title ?? "";
                    if (aId.ToLowerInvariant() == aQ || aWork.ToLowerInvariant() == aQ)
                    { aHit.Add(aId); continue; }
                    if (!string.IsNullOrEmpty(aTitle) && aTitle.ToLowerInvariant().Contains(aQ))
                    { aHit.Add(aId); continue; }
                    if (aId.ToLowerInvariant().Contains(aQ) || aWork.ToLowerInvariant().Contains(aQ))
                        aHit.Add(aId);
                }
            }
            catch { }
            return aHit.Distinct().ToList();
        }

        async UniTask StepPrepare(Dictionary<string, string> iArgs, string iPersona, CancellationToken iToken)
        {
            string aPath = PayloadPath(iPersona, "prepare");
            var aR = new StringBuilder();
            aR.AppendLine($"# StreamWatch step=prepare persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();
            aR.AppendLine("> **這不是開場** —— 準備階段不開 session、不記帳。它只做一件事：");
            aR.AppendLine("> 把「這場在看什麼」釘成一個 id，並且**在陪同者進場之前**就配置好。");
            aR.AppendLine();

            string aTitleIn = GetArg(iArgs, "title", "").Trim();          // 片名（人打的，如「末日後酒店」）
            string aEpisodeIn = GetArg(iArgs, "episode", "").Trim();      // 集數（如 05）
            string aMediaArg = GetArg(iArgs, "media_id", "").Trim();
            string aRefReader = GetArg(iArgs, "reference_reader", "").Trim();
            string aCatchupMap = GetArg(iArgs, "catchup_map", "").Trim(); // 0001=summit,0002=gura
            string aSttPrompt = GetArg(iArgs, "stt_prompt", null);
            bool aStartRec = GetArg(iArgs, "start_recording", "true").Trim().ToLowerInvariant() != "false";

            if (string.IsNullOrEmpty(aTitleIn) && string.IsNullOrEmpty(aMediaArg))
            {
                Blocked(iArgs, aR, aPath, "title（片名）與 media_id 至少要有一個 —— 準備階段的產物就是那個 id，不能兩邊都空",
                        $"run_cmd.py run StreamWatch --arg step=prepare --arg persona={iPersona} --arg title=末日後酒店 --arg episode=05");
                throw new Exception($"[StreamWatch] step=prepare blocked：缺 title/media_id（詳見 {aPath}）");
            }
            if (string.IsNullOrEmpty(aEpisodeIn))
            {
                Blocked(iArgs, aR, aPath, "episode（本場看第幾集）必填 —— 補課地圖與章號都靠它算",
                        $"run_cmd.py run StreamWatch --arg step=prepare --arg persona={iPersona} --arg title={(string.IsNullOrEmpty(aTitleIn) ? "末日後酒店" : aTitleIn)} --arg episode=05");
                throw new Exception($"[StreamWatch] step=prepare blocked：缺 episode（詳見 {aPath}）");
            }
            if (!int.TryParse(aEpisodeIn.TrimStart('0').Length == 0 ? "0" : aEpisodeIn.TrimStart('0'), out int aEpisode) || aEpisode <= 0)
            {
                Blocked(iArgs, aR, aPath, $"episode 要是正整數（收到 '{aEpisodeIn}'）", "--arg episode=05");
                throw new Exception($"[StreamWatch] step=prepare blocked：episode 無效（詳見 {aPath}）");
            }
            string aChapterId = aEpisode.ToString("0000");

            // ── ① 解析媒材 id（查既有，不發明） ──────────────────────────
            aR.AppendLine("## ① 媒材 id（查既有，不發明）");
            string aMediaId = "";
            if (!string.IsNullOrEmpty(aMediaArg))
            {
                aMediaId = aMediaArg;
                bool aExists = Directory.Exists(UCL_ReadingLibraryIO.MediaRoot(aMediaId));
                aR.AppendLine($"- 明示 `media_id={aMediaId}`（{(aExists ? "閱讀庫**已存在**" : "⚠ 閱讀庫**尚不存在** —— 要建請走 `Cmd_Library op=media_init`，本步不代建")}）");
            }
            else
            {
                var aCand = ResolveMediaCandidates(aTitleIn);
                aR.AppendLine($"- 查詢字串: `{aTitleIn}` → 命中 **{aCand.Count}** 筆");
                foreach (var c in aCand) aR.AppendLine($"  - `{c}`");
                if (aCand.Count == 1) { aMediaId = aCand[0]; aR.AppendLine($"- ⇒ 採用 `{aMediaId}`（唯一命中）"); }
                else
                {
                    string aWhy = aCand.Count == 0
                        ? $"閱讀庫查不到「{aTitleIn}」—— 新作品請先 `Cmd_Library op=media_init`（媒材 id 由那邊生成），再帶 --arg media_id=<id> 回來"
                        : $"「{aTitleIn}」命中 {aCand.Count} 筆，**不猜** —— 用 --arg media_id=<上面其中一個> 指定";
                    Blocked(iArgs, aR, aPath, aWhy,
                            $"run_cmd.py run StreamWatch --arg step=prepare --arg persona={iPersona} --arg media_id=<id> --arg episode={aEpisodeIn}");
                    throw new Exception($"[StreamWatch] step=prepare blocked：媒材 id 未定（詳見 {aPath}）");
                }
            }

            // ── ② 閱讀庫現況（避免漂移的證據：誰已經寫過哪幾章） ──────────
            aR.AppendLine();
            aR.AppendLine("## ② 心得庫現況（**這就是防漂移的那一眼**）");
            var aReaders = new List<string>();
            try { aReaders = UCL_ReadingLibraryIO.ListReaders(aMediaId) ?? new List<string>(); } catch { }
            var aChaptersOf = new Dictionary<string, List<string>>();
            foreach (var r in aReaders) aChaptersOf[r] = ReaderChapters(aMediaId, r);
            if (aReaders.Count == 0)
                aR.AppendLine("- （這個媒材還沒有任何讀者紀錄 —— 本場就是第一筆）");
            foreach (var r in aReaders)
            {
                var ch = aChaptersOf[r];
                aR.AppendLine($"- `{r}`：{ch.Count} 章（{(ch.Count == 0 ? "—" : string.Join(" ", ch))}）"
                    + (ch.Contains(aChapterId) ? $"　⚠ **已有第 {aEpisode} 話心得**（本場是重看？那要開 r2，不是覆寫 r1）" : ""));
            }

            // ── ③ 接續基準 reader（給陪同者追進度用） ────────────────────
            aR.AppendLine();
            aR.AppendLine("## ③ 接續心得基準（reference_reader）");
            if (string.IsNullOrEmpty(aRefReader))
            {
                string aBest = ""; int aBestN = -1; bool aTie = false;
                foreach (var kv in aChaptersOf)
                {
                    if (kv.Value.Count > aBestN) { aBest = kv.Key; aBestN = kv.Value.Count; aTie = false; }
                    else if (kv.Value.Count == aBestN && aBestN >= 0) aTie = true;
                }
                if (string.IsNullOrEmpty(aBest)) aRefReader = iPersona;      // 沒人寫過 → 就是我
                else if (aTie)
                {
                    Blocked(iArgs, aR, aPath,
                            $"有多位讀者章數並列最多（{aBestN} 章）—— 基準要人挑，不由工具擲",
                            $"--arg reference_reader=<persona>");
                    throw new Exception($"[StreamWatch] step=prepare blocked：reference_reader 未定（詳見 {aPath}）");
                }
                else aRefReader = aBest;
                aR.AppendLine($"- 未指定 → 取章數最多者 `{aRefReader}`（{Math.Max(aBestN, 0)} 章）。要換用 `--arg reference_reader=<persona>`");
            }
            else aR.AppendLine($"- 明示 `{aRefReader}`");

            // ── ④ 補課地圖：第 1..episode-1 話各由誰的心得補 ──────────────
            // 規則（Tim）：預設取主觀影者/基準者自己的心得；**缺的那幾集由主觀影者指定用誰的**。
            aR.AppendLine();
            aR.AppendLine($"## ④ 補課地圖（第 1 – {Math.Max(aEpisode - 1, 0)} 話，給進度有缺的陪同者）");
            var aMapArg = new Dictionary<string, string>();
            foreach (var part in aCatchupMap.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length == 2) aMapArg[kv[0].Trim().PadLeft(4, '0')] = kv[1].Trim();
            }
            var aMap = new JsonData();
            var aUnfilled = new List<string>();
            for (int e = 1; e < aEpisode; e++)
            {
                string aCh = e.ToString("0000");
                string aSrc = "";
                if (aMapArg.TryGetValue(aCh, out string aFromArg)) aSrc = aFromArg;
                else if (aChaptersOf.TryGetValue(aRefReader, out var rc) && rc.Contains(aCh)) aSrc = aRefReader;
                else
                {
                    var aHas = aChaptersOf.Where(kv => kv.Value.Contains(aCh)).Select(kv => kv.Key).ToList();
                    if (aHas.Count == 1) aSrc = aHas[0];
                    else
                    {
                        aUnfilled.Add($"{aCh}（{(aHas.Count == 0 ? "**沒有任何人寫過**" : "候選: " + string.Join(" / ", aHas))}）");
                        continue;
                    }
                }
                bool aSrcHas = aChaptersOf.TryGetValue(aSrc, out var sc) && sc.Contains(aCh);
                aMap[aCh] = new JsonData(aSrc);
                aR.AppendLine($"- 第 {e} 話 → `{aSrc}`{(aSrcHas ? "" : "　⚠ **該 reader 其實沒有這章**（指定了也補不出內容）")}");
            }
            if (aEpisode == 1) aR.AppendLine("- （本場是第 1 話，沒有要補的）");
            if (aUnfilled.Count > 0)
            {
                aR.AppendLine();
                aR.AppendLine("⚠ **以下集數沒有預設來源，要主觀影者指定**（Tim 拍板：缺的由主觀影者決定用誰的心得補）：");
                foreach (var u in aUnfilled) aR.AppendLine($"- {u}");
                aR.AppendLine($"  ⇒ 補上：`--arg catchup_map=\"{aUnfilled[0].Substring(0, 4)}=<persona>,…\"`（可與本步其他參數一起重跑，prepare 可重入）");
            }

            // ── ⑤ 節目名 → 再開錄影（順序有意義） ────────────────────────
            aR.AppendLine();
            aR.AppendLine("## ⑤ 錄影（先填節目名，再開）");
            string aShowTitle = string.IsNullOrEmpty(aTitleIn) ? aMediaId : aTitleIn;
            string aShow = $"{aShowTitle} [{aEpisode:00}]";
            string aTitleNote = UCL.Core.EditorLib.Page.UCL_ScreenStreamPage.SetStreamTitle(aShow, aSttPrompt, iPersona);
            aR.AppendLine($"- {aTitleNote}");
            bool aRecOn = IsRecordingEnabled(out string aCfgNote);
            if (aRecOn) aR.AppendLine($"- 錄影：**已在錄** —— 未動作（{aCfgNote}）");
            else if (!aStartRec) aR.AppendLine($"- 錄影：未開，且 `start_recording=false` ⇒ 不代開（{aCfgNote}）");
            else
            {
                string aRecNote = UCL.Core.EditorLib.Page.UCL_ScreenStreamPage.SetRecordingEnabled(true, iPersona);
                aR.AppendLine($"- {aRecNote}");
                aR.AppendLine($"- 回讀：{(IsRecordingEnabled(out string aCfg2) ? "錄影中" : "**仍未錄影**")}（{aCfg2}）　←　寫完再讀，不採信回傳值");
            }

            // ── ⑥ 落檔（陪同者的 join / catchup 都讀這份） ────────────────
            var aP = new JsonData();
            aP["media_id"] = new JsonData(aMediaId);
            aP["episode"] = new JsonData(aEpisode);
            aP["chapter_id"] = new JsonData(aChapterId);
            aP["show_title"] = new JsonData(aShow);
            aP["prepared_by"] = new JsonData(iPersona);
            aP["prepared_at"] = new JsonData(UCL_AwakeningService.NowIso());
            aP["reference_reader"] = new JsonData(aRefReader);
            aP["catchup_map"] = aMap;
            aP["catchup_unfilled"] = UCL_ReadingLibraryIO.ToStringArray(aUnfilled.Select(u => u.Substring(0, 4)).ToList());
            AtomicWrite(PreparedPath(aMediaId), aP.ToJsonBeautify());
            aR.AppendLine();
            aR.AppendLine($"- 準備檔：`StreamWatch/prepared/{aMediaId}.json`（join / catchup 都讀這份）");

            // 公告：陪同者要知道「現在可以進場了、而且 id 已經定了」
            var aBody = new StringBuilder();
            aBody.AppendLine($"🎬 [{iPersona} 大小姐] 觀影準備完成 — **{aShow}**｜媒材 `{aMediaId}`");
            aBody.AppendLine();
            aBody.AppendLine($"- 章號：`{aChapterId}`（心得一律寫這個章號，**別各自打字，那是漂移的來源**）");
            aBody.AppendLine($"- 接續基準：`{aRefReader}`");
            if (aUnfilled.Count > 0) aBody.AppendLine($"- ⚠ 補課地圖尚缺：{string.Join(" ", aUnfilled.Select(u => u.Substring(0, 4)))}（我會指定來源）");
            aBody.AppendLine();
            aBody.AppendLine("陪同者現在可以進場了 —— 進度有缺的先跑 catchup 讀一份補課簡報：");
            aBody.AppendLine($"`run_cmd.py --persona <me> run StreamWatch --arg step=catchup --arg persona=<me> --arg media_id={aMediaId}`");
            aBody.AppendLine($"然後 `--arg step=join`（媒材與章號都已經配置好，不用自己填）。");
            int aSeq = await TavernPost(iArgs, iPersona, aBody.ToString(), "watch-prepare", iToken);

            aR.AppendLine($"- 公告：{(aSeq > 0 ? $"seq **{aSeq}**" : "未發（best-effort）")}");
            aR.AppendLine();
            // ⚠ `start` 的 `--arg media=` 吃的是 **work slug**（它會去 works/ 建檔），不是 media_id。
            // 🩸 2026-08-17 首次實跑就踩到：prepare 的 next 印了 `--arg media=anim-apocalypse-hotel`，
            //   start 照著把它當 work ⇒ 在 works/ 生出一個假 work（title 也是那串 id），
            //   而真正的 work 是 `apocalypse-hotel`。**準備階段本來就是為了防這件事，結果它自己指錯。**
            //   ⇒ 這裡改讀 media.json 的 work_id，指令印真正的 work slug。
            string aWorkId = "";
            try
            {
                var aMediaJson = UCL_ReadingLibraryIO.LoadJson(
                    Path.Combine(UCL_ReadingLibraryIO.MediaRoot(aMediaId), "media.json"), out string aMErr);
                if (aMediaJson != null && aMediaJson.Contains("work_id")) aWorkId = aMediaJson["work_id"].GetString();
            }
            catch { }
            aP["work_id"] = new JsonData(aWorkId);
            AtomicWrite(PreparedPath(aMediaId), aP.ToJsonBeautify());   // work_id 也要進準備檔

            aR.AppendLine("## next");
            aR.AppendLine($"1. **開場**：run_cmd.py run StreamWatch --arg step=start --arg persona={iPersona} --arg until=<HH:mm> "
                + $"--arg media={(string.IsNullOrEmpty(aWorkId) ? aMediaId : aWorkId)}"
                + (string.IsNullOrEmpty(aWorkId)
                    ? "　⚠ 讀不到 media.json 的 work_id，這裡退回 media_id —— **開場前先確認 works/ 底下是不是已有對應的 work**，否則會生出重複 work"
                    : $"　←　`media=` 吃的是 **work slug**（讀自 media.json 的 `work_id`），不是 media_id"));
            aR.AppendLine($"2. 陪同者：先 `step=catchup --arg media_id={aMediaId}`（缺集才需要），再 `step=join`");
            if (aUnfilled.Count > 0)
                aR.AppendLine($"3. ⚠ 補課地圖有缺 —— 重跑本步並帶 `--arg catchup_map=\"…\"` 補齊（prepare 可重入，會覆寫準備檔）");
            WritePayload(iArgs, aPath, aR.ToString());
            Debug.Log($"[StreamWatch] step=prepare media={aMediaId} ep={aEpisode} → {aPath}");
        }

        // ===========================================================
        // 區塊：step=catchup — 陪同者的**補課簡報**（Tim 2026-08-17：只輸入自己的 persona 就讀得回來）
        // 物理意義：形狀刻意抄早安 brief —— **一份檔案讀完就接上**，不要「去這五個資料夾各讀一份」。
        //   來源不是工具生成的摘要，是**別人親筆心得的全文**（誰的由 prepare 的 catchup_map 決定）。
        // 數值影響：純唯讀 + 寫一份 payload；零 token。缺的集數在檔內**逐條列明**，
        //   不靜默跳過 —— 「這集沒人寫過」與「我沒撈到」必須長得不一樣。
        // ===========================================================
        void StepCatchup(Dictionary<string, string> iArgs, string iPersona)
        {
            string aMediaId = GetArg(iArgs, "media_id", "").Trim();
            string aPath = PayloadPath(iPersona, "catchup");
            var aR = new StringBuilder();
            aR.AppendLine($"# StreamWatch step=catchup persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            if (string.IsNullOrEmpty(aMediaId))
            {
                Blocked(iArgs, aR, aPath, "media_id 必填（準備階段公告裡有）",
                        $"run_cmd.py run StreamWatch --arg step=catchup --arg persona={iPersona} --arg media_id=<id>");
                throw new Exception($"[StreamWatch] step=catchup blocked：缺 media_id（詳見 {aPath}）");
            }
            var aP = LoadPrepared(aMediaId);
            if (aP == null)
            {
                Blocked(iArgs, aR, aPath,
                        $"`{aMediaId}` 還沒有準備檔 —— 主觀影者要先跑 step=prepare（媒材 id／章號／補課地圖都在那一步定）",
                        "run_cmd.py run StreamWatch --arg step=prepare --arg persona=<主觀影者> --arg title=<片名> --arg episode=<N>");
                throw new Exception($"[StreamWatch] step=catchup blocked：無準備檔（詳見 {aPath}）");
            }

            int aEpisode = ReadInt(aP, "episode");
            string aRef = ReadStr(aP, "reference_reader");
            string aShow = ReadStr(aP, "show_title");
            var aMine = ReaderChapters(aMediaId, iPersona);
            aR.AppendLine($"> **{aShow}**｜媒材 `{aMediaId}`｜本場第 {aEpisode} 話｜接續基準 `{aRef}`");
            aR.AppendLine($"> 我（`{iPersona}`）已有的章：{(aMine.Count == 0 ? "**無**" : string.Join(" ", aMine))}");
            aR.AppendLine();

            var aGaps = new List<string>();
            for (int e = 1; e < aEpisode; e++)
            {
                string aCh = e.ToString("0000");
                if (!aMine.Contains(aCh)) aGaps.Add(aCh);
            }
            if (aGaps.Count == 0)
            {
                aR.AppendLine("## ✅ 沒有缺集");
                aR.AppendLine($"第 1 – {aEpisode - 1} 話我都有心得，直接進場即可。");
            }
            else
            {
                aR.AppendLine($"## 我缺 {aGaps.Count} 集：{string.Join(" ", aGaps)}");
                aR.AppendLine();
                aR.AppendLine("> 以下是**別人親筆的心得全文**（來源由主觀影者在 prepare 指定），不是工具生成的摘要。");
                aR.AppendLine("> 讀完就接得上；⚠ 但那是**他們看到的**，不是我看到的 —— 我自己的心得要寫成自己的觀察。");
                aR.AppendLine();
                var aMap = (aP.Contains("catchup_map") ? aP["catchup_map"] : null);
                foreach (var aCh in aGaps)
                {
                    string aSrc = (aMap != null && aMap.Contains(aCh)) ? aMap[aCh].GetString() : "";
                    aR.AppendLine($"### 第 {int.Parse(aCh)} 話（章 `{aCh}`）");
                    if (string.IsNullOrEmpty(aSrc))
                    {
                        aR.AppendLine("- ⚠ **補課地圖沒有這一集的來源** —— 請主觀影者重跑 prepare 帶 `--arg catchup_map=\"" + aCh + "=<persona>\"`。");
                        aR.AppendLine();
                        continue;
                    }
                    string aDir = Path.Combine(UCL_ReadingLibraryIO.ReaderRoot(aMediaId, aSrc), "chapters", aCh);
                    var aRounds = Directory.Exists(aDir)
                        ? Directory.GetFiles(aDir, "r*.md").OrderBy(f => f).ToList() : new List<string>();
                    if (aRounds.Count == 0)
                    {
                        aR.AppendLine($"- ⚠ 來源 `{aSrc}` 的第 {int.Parse(aCh)} 話**找不到心得檔**（目錄 {(Directory.Exists(aDir) ? "存在但沒有 r*.md" : "不存在")}）—— 這一集補不出來，請主觀影者改指定來源。");
                        aR.AppendLine();
                        continue;
                    }
                    string aUse = aRounds[aRounds.Count - 1];       // 取最後一輪（重看 r2 比 r1 新）
                    aR.AppendLine($"- 來源：`{aSrc}` / `{Path.GetFileName(aUse)}`"
                        + (aRounds.Count > 1 ? $"（該章共 {aRounds.Count} 輪，取最新）" : ""));
                    aR.AppendLine();
                    try { aR.AppendLine(File.ReadAllText(aUse, Encoding.UTF8).Trim()); }
                    catch (Exception e2) { aR.AppendLine($"⚠ 讀取失敗：{e2.Message}"); }
                    aR.AppendLine();
                }
            }

            // 基準者的接續點（下次從哪接）—— 進場前最後一眼
            try
            {
                var aReader = UCL_ReadingLibraryIO.LoadReader(aMediaId, aRef, out string aErr);
                if (aReader != null && aReader.Contains("progress"))
                {
                    var aProg = aReader["progress"];
                    aR.AppendLine($"## 接續點（`{aRef}` 的書籤）");
                    aR.AppendLine($"- 目前章：`{(aProg.Contains("current_chapter_id") ? aProg["current_chapter_id"].GetString() : "?")}`");
                    aR.AppendLine($"- 書籤：{(aProg.Contains("bookmark_note") ? aProg["bookmark_note"].GetString() : "（無）")}");
                    if (aReader.Contains("current_impression"))
                        aR.AppendLine($"- 當前看法：{aReader["current_impression"].GetString()}");
                    aR.AppendLine();
                }
            }
            catch { }

            aR.AppendLine("## next");
            aR.AppendLine($"1. 進場：run_cmd.py run StreamWatch --arg step=join --arg persona={iPersona}");
            aR.AppendLine($"2. 寫心得時**章號一律用 `{aEpisode:0000}`**、媒材 `{aMediaId}` —— 那是 prepare 釘死的，別各自打字。");
            WritePayload(iArgs, aPath, aR.ToString());
            Debug.Log($"[StreamWatch] step=catchup media={aMediaId} gaps={aGaps.Count} → {aPath}");
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
        void StepCapture(IDictionary<string, string> iArgs, string iPersona, string iOn)
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
                Blocked(iArgs, aR, aPath, $"on 必須是 1/0（true/false、on/off 亦可）—— 收到 '{iOn}'",
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
            WritePayload(iArgs, aPath, aR.ToString());
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
        async UniTask StepPeek(IDictionary<string, string> iArgs, string iOwner, string iSeconds, string iRaw, CancellationToken iToken)
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
                Blocked(iArgs, aR, aPath, "解析不到 screenstream_montage.py（CorePath 空或檔案不存在）",
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
            // peek 沒有 session ⇒ 沒有已讀游標；排除自己仍然照做（額度留給別人講的）。
            // since=0 ＝ 從頭算未讀，但 peek 只看一眼、不推進游標，所以不會污染任何場次的進度。
            var (aOk, aStdout, aErr) = await RunMontageAsync(aScript, aAfter, aWatermark, aOutPath, aOcrOn, aSttOn,
                                                            iOwner, 0, iToken);
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
                WritePayload(iArgs, aPath, aR.ToString());
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
                WritePayload(iArgs, aPath, aR.ToString());
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
            WritePayload(iArgs, aPath, aR.ToString());
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

        async UniTask StepStart(IDictionary<string, string> iArgs, string iPersona, string iUntil, string iMedia, SourceMeta iSrc, CancellationToken iToken)
        {
            string aPath = PayloadPath(iPersona, "start");
            var aR = new StringBuilder();
            aR.AppendLine($"# StreamWatch step=start persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            // 守衛①：必須在線
            if (!UCL_AwakeningService.IsOnline(iPersona))
            {
                Blocked(iArgs, aR, aPath, $"'{iPersona}' 不在線（無 session lock）",
                        $"先跑 run_cmd.py run GoodMorning --arg step=wake --arg persona={iPersona}");
                throw new Exception($"[StreamWatch] step=start blocked：persona 不在線（詳見 {aPath}）");
            }

            // 守衛②：until 必填且可解析
            DateTime aNow = DateTime.Now;
            if (!TryParseUntil(iUntil, aNow, out DateTime aUntil, out string aUntilErr))
            {
                Blocked(iArgs, aR, aPath, aUntilErr, "--arg until=<HH:mm 本地時刻>（例 until=23:30；深夜跨日自動判定）");
                throw new Exception($"[StreamWatch] step=start blocked：until 無效（詳見 {aPath}）");
            }

            // 守衛③：不疊開（到期殘留自動收掉 —— 沒跑 cycle 的人不該被卡死在沒有出口的房間）
            var aOld = LoadSession(iPersona);
            if (aOld != null && ReadBool(aOld, "active"))
            {
                DateTime? aOldEnd = ParseIsoLocal(ReadStr(aOld, "end_ts"));
                if (aOldEnd.HasValue && aNow <= aOldEnd.Value)
                {
                    Blocked(iArgs, aR, aPath, $"已有進行中的觀影 session（至 {aOldEnd.Value:HH:mm} 本地）—— 不疊開",
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
                WritePayload(iArgs, aPath, aR.ToString());
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
                    Blocked(iArgs, aR, aPath,
                        aGeneric ? $"`{iMedia}` 是泛名 —— 它會把**所有 bilibili 影片併成同一個 work**"
                                 : $"bilibili 場必須帶 `--arg up=<up主名>` —— up 主就是這個 work 的身分",
                        $"改成 --arg media=bilibili-<up主 slug> --arg up=<up主名> "
                        + "[--arg title=<影片標題>] [--arg desc=<影片介紹>] [--arg url=<網址>]");
                    aR.AppendLine("> 一個 up 主 = 一個 work（跨場累積心得）；一支影片 = 一場（`title`/`desc`/`url` 記在場次上）。");
                    aR.AppendLine("> 🩸 `bilibili-stream` 是 2026-08-15 我自己取的，當天就被 Tim 打回：**名字比事實大**。");
                    WritePayload(iArgs, aPath, aR.ToString());
                    throw new Exception($"[StreamWatch] step=start blocked：bilibili 鍵需按 up 主分（詳見 {aPath}）");
                }
            }

            // ===========================================================
            // 區塊職責：防呆解析 —— 使用者給的那個鍵，到底是 work、還是 media_id、還是真的新東西？
            // 🩸 2026-08-17 實跑血證（我自己踩的）：prepare 的 next 印了 `--arg media=anim-apocalypse-hotel`
            //   （那是 **media_id**），而 start 把它當 work slug ⇒ 在 works/ 生出一個假 work
            //   `anim-apocalypse-hotel`（title 也是那串 id），而真正的 work 是 `apocalypse-hotel`。
            //   **兩個 work 各自都能被寫入、都不報錯** —— 「找到另一個宇宙的檔」那一族。
            // 物理意義：能查出來的就不要問人。只有**查不到任何對應**時才算新建，而那一刻要吵。
            // 數值影響：解析結果寫進 session 的 work_id / library_media_id 兩個新欄位
            //   （不動既有 media_id 欄的語意 ⇒ cycle/join/settle 的既有讀取端不受影響）。
            // ===========================================================
            ResolveWatchTarget(iMedia, out string aResolvedWork, out string aLibMediaId, out string aResolveNote);
            bool aIsNewWork = !WorkExists(aResolvedWork);
            // ⚠ **新 work 要真的建出來**（2026-08-15 實證的洞）：
            //   舊版只印一句「這是新 work」就過去了，從不落檔 ⇒ 下一場的「既有 work 清單」裡
            //   **永遠不會有自己開過的場**（昨天 `bilibili-stream` 開過場，今天清單上找不到它）。
            //   於是那份清單只證明「Library 有什麼」，不證明「觀影用過什麼」，而它的標題讓人以為是後者。
            string aWorkNote = aIsNewWork ? CreateWork(aResolvedWork, iSrc) : "";

            // session 註冊（C# 唯一寫入端）
            string aSessionId = $"sw-{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{iPersona}";
            var aSession = new JsonData();
            aSession["persona"] = new JsonData(iPersona);
            aSession["session_id"] = new JsonData(aSessionId);
            aSession["role"] = new JsonData("primary");
            aSession["media_id"] = new JsonData(iMedia);
            aSession["work_id"] = new JsonData(aResolvedWork);          // 解析後的 work（可能與 media_id 不同）
            aSession["library_media_id"] = new JsonData(aLibMediaId);   // 寫心得要用的那個 id；空＝還沒有對應 media
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
            int aSeq = await TavernPost(iArgs, iPersona, aBody.ToString(), "watch-start", iToken);
            if (aSeq > 0)
            {
                aSession["start_seq"] = new JsonData(aSeq);
                AtomicWrite(SessionPath(iPersona), aSession.ToJsonBeautify());
            }

            // 回傳檔
            aR.AppendLine($"- session: `{aSessionId}`（state: `{SessionPath(iPersona)}`）");
            aR.AppendLine($"- media: `{iMedia}`{(aIsNewWork ? "　⚠ **這是新 work** —— 若這部片其實已存在於 Library，現在喊停比事後合併便宜" : "　✅ 命中既有 work")}");
            if (!string.IsNullOrEmpty(aResolveNote)) aR.AppendLine($"- 防呆解析: {aResolveNote}");
            if (aResolvedWork != iMedia) aR.AppendLine($"- work    : `{aResolvedWork}`　←　**沒有用妳給的字串當 work**（那是 media_id）");
            if (!string.IsNullOrEmpty(aLibMediaId)) aR.AppendLine($"- 寫心得用: `media_id={aLibMediaId}`（已解析，下面 next 的指令已填好）");
            if (!string.IsNullOrEmpty(aWorkNote)) aR.AppendLine($"- work 建檔: {aWorkNote}");
            if (!string.IsNullOrEmpty(iSrc.Up)) aR.AppendLine($"- UP 主  : **{iSrc.Up}**（work 認這個；影片標題/介紹記在場次上）");
            if (!string.IsNullOrEmpty(iSrc.VideoTitle)) aR.AppendLine($"- 本場影片: {iSrc.VideoTitle}");
            if (!string.IsNullOrEmpty(iSrc.Url)) aR.AppendLine($"- 出處    : {iSrc.Url}");
            aR.AppendLine($"- 看到: {aUntil:HH:mm}（約 {aMinutes} 分鐘）");
            aR.AppendLine($"- 開播公告: {(aSeq > 0 ? $"seq **{aSeq}**（匯出區間左端點）" : "未發（best-effort，不影響 session）")}");
            AppendRetentionLine(aR);
            aR.AppendLine();
            // 續看／續集的入口 —— 印在**開場那一步**，因為那是唯一還來得及追回的時刻（Tim 2026-08-16）
            aR.Append(ReaderProgressBlock(iMedia, iPersona));
            aR.AppendLine();
            aR.AppendLine("## next");
            aR.AppendLine($"1. **取素材**：run_cmd.py run StreamWatch --arg step=cycle --arg persona={iPersona}");
            aR.AppendLine("2. 依回傳檔給的**絕對路徑** Read 縮圖牆與字幕 → 寫觀戰評論");
            aR.AppendLine($"3. **發評論**：run_cmd.py run StreamWatch --arg step=observe --arg persona={iPersona} --arg-file body=<評論>");
            // ⚠ **next 只寫「往前」，不提收工**（Tim 2026-08-16 拍板）。
            //    原本這裡寫「收工不用你判斷 —— 到期時 cycle 會告訴你」，本意是防 agent 自行收手，
            //    🩸 結果反效果：basecamp 陪看第一輪讀到那句之後**就停了**（把「收工」放進視野，
            //    等於在指路的位置提供了一個停下來的選項）。反向提示會被當成選項，不會被當成禁令。
            //    ⇒ 收工由 cycle 在**真的到期時**宣布即可，不必事先預告。
            aR.AppendLine("4. 回到 1，繼續下一輪。");
            // 心得指令**自動填好**（Tim 2026-08-17）：能查出來的就不要問人 ——
            // 只有「這部片還沒有 media」時才需要人給 id，而那一刻它是真的新東西。
            if (!string.IsNullOrEmpty(aLibMediaId))
            {
                var aPrep2 = LoadPrepared(aLibMediaId);
                string aCh2 = aPrep2 != null ? ReadStr(aPrep2, "chapter_id") : "";
                aR.AppendLine($"5. 收工後寫心得（**id 已填好，不要自己打字**）："
                    + $"`run_cmd.py run Library --arg op=note_chapter --arg persona={iPersona} "
                    + $"--arg media_id={aLibMediaId} --arg chapter={(string.IsNullOrEmpty(aCh2) ? "<四位數話號>" : aCh2)} "
                    + "--arg title=<話名> --arg-file body=<心得>`"
                    + (string.IsNullOrEmpty(aCh2) ? "　（章號查不到 ⇒ 走過 step=prepare 就會自動帶）" : ""));
            }
            else
            {
                aR.AppendLine($"5. ⚠ 這部片在閱讀庫**還沒有 media** ⇒ 寫心得前先 `Cmd_Library op=media_init`"
                    + "（媒材 id 由那邊生成，這是唯一需要人取 id 的情況）。");
            }
            WritePayload(iArgs, aPath, aR.ToString());
            Debug.Log($"[StreamWatch] step=start 完成 session={aSessionId} media={iMedia} → {aPath}");
        }

        // ===========================================================
        // 區塊：step=cycle — 取素材 ＋ 到期/中斷判定 ＋ 狀態相依 next
        // 物理意義：本步是**唯一的終止判定點**。判定只認兩個顯式事實：系統時鐘、_config.json 的 enabled。
        // ⚠ 中斷判定**不推論 frame 新鮮度**：實測活樣本 enabled=false 而 994 張 frame 仍在磁碟上 ——
        //   「錄影停了」與「frame 沒變新」是兩件事，用後者推論會把 daemon 打嗝讀成中斷而誤殺 session。
        // ===========================================================
        async UniTask StepCycle(IDictionary<string, string> iArgs, string iPersona, CancellationToken iToken)
        {
            string aPath = PayloadPath(iPersona, "cycle");
            var aR = new StringBuilder();
            aR.AppendLine($"# StreamWatch step=cycle persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            var aS = LoadSession(iPersona);
            if (aS == null || !ReadBool(aS, "active"))
            {
                Blocked(iArgs, aR, aPath, "無進行中的觀影 session",
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
                    // ⚠ 接續點**走閱讀心得那條路**（Tim 2026-08-16）——「接續觀影跟接續閱讀走一樣的流程」。
                    //   不另建格式：Cmd_Library 的 media/reader/chapter 模型本來就是為分段觀看設計的
                    //   （它有 `time_range`「手動切段留下的事實」與 `display_number`）。
                    //   ⇒ StreamWatch 不重造第四套，只把 session 的事實預填進指令。
                    aR.AppendLine("⚠ **本場未寫接續點** —— 不擋結算，但下次續看接不回進度。");
                    aR.AppendLine("   **接續點＝閱讀心得**，走 Library（與接續閱讀同一條路，不是另一種格式）：");
                    // 🩸 舊版這裡印的是樣板 `--arg media_id=<anim|film|series>-<session 的 media_id>` ——
                    //   照著貼會組出 `anim-anim-apocalypse-hotel` 這種不存在的 id（我 2026-08-17 實跑撞到）。
                    //   ⇒ 改成**用 session 解析好的 library_media_id 與準備階段的章號直接填**；
                    //   查不到才退回要人填，並說清楚是哪一種情況。
                    string aLibId = ReadStr(aS, "library_media_id");
                    string aChId = "";
                    if (!string.IsNullOrEmpty(aLibId))
                    {
                        var aPrepS = LoadPrepared(aLibId);
                        if (aPrepS != null) aChId = ReadStr(aPrepS, "chapter_id");
                    }
                    string aMidArg = string.IsNullOrEmpty(aLibId) ? "<閱讀庫 media_id — 先跑 Cmd_Library op=media_init>" : aLibId;
                    string aChArg = string.IsNullOrEmpty(aChId) ? "<四位數話號>" : aChId;
                    aR.AppendLine($"   1. 心得：`run_cmd.py run Library --arg op=note_chapter --arg persona={iPersona} "
                        + $"--arg media_id={aMidArg} --arg chapter={aChArg} --arg title=<話名> --arg display_number=<第 N 話> "
                        + "--arg-file body=<心得>`"
                        + (string.IsNullOrEmpty(aLibId) ? "" : "　←　**id 已自動填**，不要自己打字"));
                    aR.AppendLine($"   2. 書籤：`run_cmd.py run Library --arg op=bookmark --arg persona={iPersona} "
                        + $"--arg media_id={aMidArg} --arg note=<下次從哪接> --arg impression=<當前看法>`");
                    aR.AppendLine("   3. 人物：`op=add_character` / `op=revise_view`（改觀要寫 `change_reason`）");
                    aR.AppendLine("   ⚠ **一話一 round，場次中斷續寫同一個 round**；`r2` 只留給真正的重看。");
                    aR.AppendLine("      （場次是我的切法，話數是作品的切法 —— round 認後者。）");
                    aR.AppendLine("   ⇒ 下次續看：`run_cmd.py run Library --arg op=recall --arg persona="
                        + iPersona + " --arg media_id=<同上>`");
                    aR.AppendLine();
                }

                await SettleAsync(iArgs, iPersona, aS, aByInterrupt, aNow, aEnd, aR, iToken);
                WritePayload(iArgs, aPath, aR.ToString());
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
                Blocked(iArgs, aR, aPath, "解析不到 screenstream_montage.py（CorePath 空或檔案不存在）",
                        "確認 UCL_Core 掛載位置與 Tools~/AgentCommands/screenstream_montage.py 是否存在");
                throw new Exception($"[StreamWatch] step=cycle blocked：找不到縮圖牆工具（詳見 {aPath}）");
            }

            var (aOcrOn, aSttOn) = ReadSensorFlags();
            double aWatermark = SensorWatermark(aOcrOn, aSttOn, out string aWmNote);
            DateTime aRunStart = DateTime.Now;
            // 酒館已讀游標：優先用 session 記的 `tavern_seq`；沒有就退回 `start_seq`（＝開播那則，本場起點）。
            // ⚠ 退回值**不可以是 -1** —— 那會讓 sidecar 從全庫最舊開始列，正是 2026-08-16 那隻的成因。
            int aTavernSince = aS != null ? ReadInt(aS, "tavern_seq") : 0;
            if (aTavernSince <= 0 && aS != null) aTavernSince = ReadInt(aS, "start_seq");
            var (aOk, aStdout, aErr) = await RunMontageAsync(aScript, aCursor, aWatermark, aOutPath, aOcrOn, aSttOn,
                                                            iPersona, Math.Max(0, aTavernSince), iToken);
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
                // ⚠ **無素材時同樣要印同場訊息** —— 這條是我 2026-08-16 自己寫了「每一段永遠存在」
                //   卻在同一次改動裡違反的那格：AppendSidecar 原本只掛在有素材那條路徑上。
                //   而「我這輪沒東西看」正是**最需要看別人講了什麼**的時刻（他的窗口跟我的不一樣）。
                //   ⇒ 兩條路徑都印；本輪無 sidecar，所以字幕/語音段會誠實地印「無」。
                // ⚠ 游標印**實際餵給 montage 的那個**（aTavernSince，已含 start_seq 退回），不是 session 原欄位：
                //   第一輪 tavern_seq 還沒寫，印原欄位會印出 seq=0，而那一輪真正用的是 start_seq（瑕疵③）。
                //   montageRan=false —— 這條路徑上 montage 提早收工，酒館段根本沒跑到（瑕疵②）。
                AppendSidecar(aR, Path.ChangeExtension(aOutPath, ".subtitles.md"), false,
                              iPersona, ParseTavernShown(aStdout), Math.Max(0, aTavernSince), false);
                AppendHotspots(aR, iPersona);   // 無素材時更該印 —— 沒東西看正是該去領熱點的時刻
                aR.AppendLine();
                aR.AppendLine("## next");
                aR.AppendLine($"1. 等 30–60 秒再跑一次 step=cycle（不必改任何參數）。");
                WritePayload(iArgs, aPath, aR.ToString());
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
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[StreamWatch] step=cycle blocked：montage 失敗（詳見 {aPath}）");
            }

            var aInfo = ParseMontageReport(aStdout);

            // 推進 cursor（用 report 的 next-cursor —— 不是 wall-clock，抖動下仍首尾嚴絲合縫）
            if (aInfo.NextCursor > 0)
            {
                aS["cursor_epoch"] = new JsonData(aInfo.NextCursor);
            }
            // 推進酒館已讀游標 —— montage 印 `tavern_max_seq=<N>`（本輪實際顯示到的最大 seq）。
            // ⚠ **不推進的後果不是「重複看到」，是「永遠看不到新的」**：未讀是從游標往後數、顯示有額度上限，
            //   游標卡住 ⇒ 每輪都從同一個舊起點重列同一批，同場的人後來講的話永遠排在額度外。
            //   （這正是 2026-08-16 那隻的第二半；只傳 --tavern-since-seq 而不推進，第二輪起就會復發。）
            // ⚠ 只前進不後退：max 保底，避免某輪 0 筆未讀時把游標打回去。
            // 🩸 2026-08-16 第二隻（長在第一隻的修法裡）：本輪 **0 筆未讀時也照樣推進游標** ⇒
            //   第一輪跑在同事發言之前、游標卻已跳到當前最大 seq ⇒ 他之後發的整段被永久跳過。
            //   症狀：sidecar 酒館段整段消失，而「沒人說話」與「游標跳過了說話的人」同形
            //   （我當時還替它編了個無害的理由，說那是 0 筆）。
            // ⇒ **沒讀到東西就沒有「已讀」到那裡**：shown==0 不推進。
            int aTavernShown = ParseTavernShown(aStdout);
            int aTavernMax = ParseTavernMaxSeq(aStdout);
            string aGlobalCursorTs = null;
            if (aTavernShown > 0 && aTavernMax > 0)
            {
                aS["tavern_seq"] = new JsonData(Math.Max(aTavernMax, ReadInt(aS, "tavern_seq")));
                // 區塊職責：本輪 sidecar 顯示過的訊息，一併消化**全域**已讀游標（Tim 2026-08-18 拍板）。
                // 物理意義：sidecar 水位是 per-session 的 seq（語意＝這場開始以來），
                //          叮／自由時間的游標是全域 ts —— 兩套原本互不相干。
                //          但**觀影期間顯示過的訊息確實已經進到眼裡**，不消化的話整場結束後
                //          未讀會累成一堵牆，然後下一次 catchup 一次倒出來（＝等於沒有人在讀）。
                // 數值影響：只在 shown>0 && maxSeq>0 時推（同上方兩隻血證的守衛：沒讀到就沒有已讀到那裡）；
                //          推進本身單調，只前進不後退。
                // ⚠ 不影響 sidecar 自己的顯示範圍 —— 它讀的是 tavern_seq，不是這個游標。
                aGlobalCursorTs = ChatTavern.UCL_TavernCursor.AdvanceToSeq(iPersona, "tavern", aTavernMax);
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
            // 單一入口：字幕／語音／同場訊息全部嵌進本檔（Tim 2026-08-16）
            // 游標印本輪實際餵進去的那個（aTavernSince），與 sidecar 標題的 `已讀 seq≤N` 同源 —— 見瑕疵①③。
            AppendSidecar(aR, aSubPath, aHasSub, iPersona, aTavernShown, Math.Max(0, aTavernSince), true);
            // 讓「全域游標有沒有被消化」可觀測 —— 靜默推進跟沒推進在回傳檔上會同形。
            aR.AppendLine(string.IsNullOrEmpty(aGlobalCursorTs)
                ? "- 全域已讀游標: **未推進**（本輪沒顯示同場訊息，或已在更前面 —— 沒讀到就沒有已讀到那裡）"
                : $"- 全域已讀游標: ✓ 推進到 `{aGlobalCursorTs}`（觀影期間顯示過＝已消化，叮不會再倒一次）");
            AppendHotspots(aR, iPersona);   // 熱點清單 —— 有沒有都印（零狀態必印）
            aR.AppendLine();
            aR.AppendLine("## next");
            aR.AppendLine($"1. Read 上面的縮圖牆{(aHasSub ? "與字幕" : "")}路徑");
            aR.AppendLine($"2. run_cmd.py run StreamWatch --arg step=observe --arg persona={iPersona} --arg-file body=<你的評論>");
            // ⚠ **next 只寫「往前」，不提收工**（Tim 2026-08-16 拍板）。
            //    原本這裡寫「收工不用你判斷 —— 到期時 cycle 會告訴你」，本意是防 agent 自行收手，
            //    🩸 結果反效果：basecamp 陪看第一輪讀到那句之後**就停了**（把「收工」放進視野，
            //    等於在指路的位置提供了一個停下來的選項）。反向提示會被當成選項，不會被當成禁令。
            //    ⇒ 收工由 cycle 在**真的到期時**宣布即可，不必事先預告。
            aR.AppendLine($"3. 之後再跑 step=cycle 繼續下一輪。");
            WritePayload(iArgs, aPath, aR.ToString());
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
        async UniTask StepJoin(IDictionary<string, string> iArgs, string iPersona, CancellationToken iToken)
        {
            string aPath = PayloadPath(iPersona, "join");
            var aR = new StringBuilder();
            aR.AppendLine($"# StreamWatch step=join persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            if (!UCL_AwakeningService.IsOnline(iPersona))
            {
                Blocked(iArgs, aR, aPath, $"'{iPersona}' 不在線（無 session lock）",
                        $"先跑 run_cmd.py run GoodMorning --arg step=wake --arg persona={iPersona}");
                throw new Exception($"[StreamWatch] step=join blocked：persona 不在線（詳見 {aPath}）");
            }

            var aOwn = LoadSession(iPersona);
            if (aOwn != null && ReadBool(aOwn, "active"))
            {
                Blocked(iArgs, aR, aPath, "你已經有進行中的觀影 session —— 不疊開",
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
                Blocked(iArgs, aR, aPath, "找不到進行中的主觀影場",
                        $"自己開一場：run_cmd.py run StreamWatch --arg step=start --arg persona={iPersona} --arg until=<HH:mm> --arg media=<work>");
                throw new Exception($"[StreamWatch] step=join blocked：無 primary 場（詳見 {aPath}）");
            }

            string aMedia = ReadStr(aPrimary, "media_id");

            // ⛔ 準備階段門檻（Tim 2026-08-17）：**準備完成才輪到陪同者進場。**
            // 物理意義：進場時 media_id / 章號 / 接續基準必須**已經是定值** ——
            //   否則每個人各自打字，就會長出 anim-apocalypse-hotel / apocalypse-hotel 兩個平行宇宙，
            //   而兩邊各自都能寫心得、都不報錯。
            // 邊界：這裡**不擋死**成無路可走 —— 缺準備檔就明說要主觀影者跑 prepare（一行指令），
            //   並把本場 media 帶進那行指令裡（不要求對方自己回想）。
            var aPrep = LoadPrepared(aMedia);
            if (aPrep == null)
            {
                Blocked(iArgs, aR, aPath,
                        $"`{aMedia}` 還沒有準備檔 —— 準備階段未完成，陪同者先不進場（章號與接續基準還沒定，現在寫心得就是漂移的起點）",
                        $"請主觀影者（`{aPrimaryPersona}`）先跑：run_cmd.py run StreamWatch --arg step=prepare "
                        + $"--arg persona={aPrimaryPersona} --arg media_id={aMedia} --arg episode=<第幾集>");
                throw new Exception($"[StreamWatch] step=join blocked：媒材 {aMedia} 無準備檔（詳見 {aPath}）");
            }
            string aPrepChapter = ReadStr(aPrep, "chapter_id");
            string aPrepRef = ReadStr(aPrep, "reference_reader");
            var aMyChapters = ReaderChapters(aMedia, iPersona);
            var aMyGaps = new List<string>();
            for (int e = 1; e < ReadInt(aPrep, "episode"); e++)
            {
                string aCh = e.ToString("0000");
                if (!aMyChapters.Contains(aCh)) aMyGaps.Add(aCh);
            }

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
            int aSeq = await TavernPost(iArgs, iPersona, aBody.ToString(), "watch-join", iToken);
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
            aR.AppendLine("## 準備階段給你的定值（**不要自己打字**，那是漂移的來源）");
            aR.AppendLine($"- 本場章號: `{aPrepChapter}`（第 {ReadInt(aPrep, "episode")} 話）／節目名 `{ReadStr(aPrep, "show_title")}`");
            aR.AppendLine($"- 接續基準: `{aPrepRef}`（由 `{ReadStr(aPrep, "prepared_by")}` 在 prepare 指定）");
            aR.AppendLine($"- 我的進度: 已有 {aMyChapters.Count} 章"
                + (aMyGaps.Count == 0 ? "，**本場之前的集數沒有缺**" : $"，⚠ **缺 {aMyGaps.Count} 集：{string.Join(" ", aMyGaps)}**"));
            if (aMyGaps.Count > 0)
                aR.AppendLine($"  ⇒ 補課簡報（一份讀完就接上）：run_cmd.py run StreamWatch --arg step=catchup --arg persona={iPersona} --arg media_id={aMedia}");
            aR.AppendLine();
            aR.AppendLine("## next");
            aR.AppendLine($"1. 取素材：run_cmd.py run StreamWatch --arg step=cycle --arg persona={iPersona}");
            aR.AppendLine($"2. 讀主觀影者的劇情線：run_cmd.py run Tavern --arg op=read --arg room=tavern --arg limit=20");
            aR.AppendLine($"3. 發評論：run_cmd.py run StreamWatch --arg step=observe --arg persona={iPersona} --arg-file body=<評論>");
            aR.AppendLine($"4. 寫心得時用 `--arg media_id={aMedia} --arg chapter={aPrepChapter}` —— 那兩個值準備階段已經釘死。");
            WritePayload(iArgs, aPath, aR.ToString());
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

        static async UniTask SettleAsync(IDictionary<string, string> iArgs, string iPersona, JsonData ioS, bool iByInterrupt,
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
            int aSeq = await TavernPost(iArgs, iPersona, aBody.ToString(), "watch-end", iToken);

            ioS["active"] = new JsonData(false);
            ioS["settled_at"] = new JsonData(UCL_AwakeningService.NowIso());
            ioS["end_reason"] = new JsonData(iByInterrupt ? "recording-stopped" : "expired");
            ioS["paid_minutes"] = new JsonData(aPaidMin);
            ioS["paid_total"] = new JsonData(aTotal);
            ioS["end_seq"] = new JsonData(aSeq);
            AtomicWrite(SessionPath(iPersona), ioS.ToJsonBeautify());
            AppendSessionLog(ioS, iPersona, aSeq, aPaidMin, aTotal);

            ioR.AppendLine($"- 本場統計: cycles={ReadInt(ioS, "cycles")}｜observations={aObs}｜在場 {aPaidMin} 分鐘");
            if (!string.IsNullOrEmpty(aStopNote))
                ioR.AppendLine($"- 計費上限: 付到 {aPaidUntil:HH:mm:ss} {aStopNote}");
            ioR.AppendLine($"- 結算    : {aPayNote}");
            ioR.AppendLine($"- 收播公告: {(aSeq > 0 ? $"seq **{aSeq}**" : "未發（best-effort）")}");
            ioR.AppendLine($"- 場次紀錄: seq **{ReadInt(ioS, "start_seq")} → {aSeq}**（匯出區間，`tavern` 房）");
            ioR.AppendLine($"- 實錄台帳: 已 append `StreamWatch/{SESSION_LOG_NAME}`（append-only；per-persona session 檔下一場就被覆寫，台帳不會）");
            ioR.AppendLine();
            ioR.AppendLine("## next");
            ioR.AppendLine("1. 本場已收工結算，session 已關閉。");
            ioR.AppendLine($"2. 要再看：run_cmd.py run StreamWatch --arg step=start --arg persona={iPersona} --arg until=<HH:mm> --arg media=<work>");
            // 實錄匯出：**不自動跑**。章 ≠ 場（重播、殘場、一話跨數場都發生過，001 章末就記了一次併章），
            // 而章名要親筆 ⇒ 這裡只把可直接貼的指令連同已量到的區間交出去，別讓它變成要人自己記得的事。
            ioR.AppendLine($"3. 本場實錄可匯出成章（章 ≠ 場：一話跨數場就把區間一起給）：");
            ioR.AppendLine($"   `python <UCL_Core>/Tools~/AgentCommands/library.py export-watch --media {aMedia} "
                + $"--seq-ranges {ReadInt(ioS, "start_seq")}-{aSeq} --title <章名> --work-title <作品 第N話> --sessions {aSessionId}`");
            ioR.AppendLine($"   （同一話的其它場次區間查 `StreamWatch/{SESSION_LOG_NAME}`；章名與併章判斷是人的事，工具不代取）");
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
        async UniTask StepObserve(IDictionary<string, string> iArgs, string iPersona, string iBody, CancellationToken iToken)
        {
            string aPath = PayloadPath(iPersona, "observe");
            var aR = new StringBuilder();
            aR.AppendLine($"# StreamWatch step=observe persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            var aS = LoadSession(iPersona);
            if (aS == null || !ReadBool(aS, "active"))
            {
                Blocked(iArgs, aR, aPath, "無進行中的觀影 session",
                        $"先跑 run_cmd.py run StreamWatch --arg step=start --arg persona={iPersona} --arg until=<HH:mm> --arg media=<work>");
                throw new Exception($"[StreamWatch] step=observe blocked：無 active session（詳見 {aPath}）");
            }
            if (string.IsNullOrWhiteSpace(iBody))
            {
                Blocked(iArgs, aR, aPath, "body 為空 —— 觀戰評論不能是空的",
                        $"--arg-file body=<檔案>（長文走檔案，不經 shell）");
                throw new Exception($"[StreamWatch] step=observe blocked：body 為空（詳見 {aPath}）");
            }
            // 守衛：沒有對應的取材紀錄 ⇒ 拒收（Plan §12 —— 不是靜靜算錢）
            int aLastTiles = ReadInt(aS, "last_tiles");
            if (ReadInt(aS, "cycles") <= 0 || aLastTiles <= 0)
            {
                Blocked(iArgs, aR, aPath, "本場尚無取材紀錄 —— 沒看過就沒有可記的觀察",
                        $"先跑 run_cmd.py run StreamWatch --arg step=cycle --arg persona={iPersona}");
                throw new Exception($"[StreamWatch] step=observe blocked：無取材紀錄（詳見 {aPath}）");
            }

            // ① 先發文
            double aSpan = ReadDouble(aS, "last_span_seconds");
            var aBody = new StringBuilder();
            aBody.AppendLine(iBody.TrimEnd());
            aBody.AppendLine();
            aBody.AppendLine($"— 本輪素材：{aLastTiles} 格／涵蓋 {aSpan:F0}s（**每格 ≈{(aLastTiles > 0 ? aSpan / aLastTiles : 0):F0}s**）｜媒材 `{ReadStr(aS, "media_id")}`");
            int aSeq = await TavernPost(iArgs, iPersona, aBody.ToString(), "watch-observe", iToken);

            // ② 後記帳（發文失敗就不記 —— 帳上不留沒人看過的評論）
            if (aSeq <= 0)
            {
                aR.AppendLine("## blocked");
                aR.AppendLine("- reason: 酒館發文失敗 ⇒ **不記帳**（先記後發會在帳上留一筆沒人看過的評論）");
                aR.AppendLine("- exit: 重跑 step=observe（評論內容請保留）");
                WritePayload(iArgs, aPath, aR.ToString());
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
            // ⚠ **next 只寫「往前」，不提收工**（Tim 2026-08-16 拍板）。
            //    原本這裡寫「收工不用你判斷 —— 到期時 cycle 會告訴你」，本意是防 agent 自行收手，
            //    🩸 結果反效果：basecamp 陪看第一輪讀到那句之後**就停了**（把「收工」放進視野，
            //    等於在指路的位置提供了一個停下來的選項）。反向提示會被當成選項，不會被當成禁令。
            //    ⇒ 收工由 cycle 在**真的到期時**宣布即可，不必事先預告。
            aR.AppendLine($"1. 繼續：run_cmd.py run StreamWatch --arg step=cycle --arg persona={iPersona}");
            WritePayload(iArgs, aPath, aR.ToString());
            Debug.Log($"[StreamWatch] step=observe seq={aSeq} obs={aObs} → {aPath}");
        }

        // ===========================================================
        // 區塊：step=note — 寫接續點（Tim 2026-08-15：心得必寫，理由是「下次續看無法追回進度」）
        // 物理意義：必寫的**不是「心得」，是「接續點」** —— 心得可以短、可以主觀，Cmd 管不了品質；
        //          接續點是結構化可檢查的：看到哪／下次從哪接／人物與伏筆狀態。
        // ⚠ Tim 拍板**不擋結算**（擋的失敗模式是 agent 消失 ⇒ 錢卡住而心得照樣沒寫）。
        //   所以本步只負責「寫得容易」，遺漏由收工通知與下一場 start 明列 —— **不擋，但也不安靜**。
        // ===========================================================
        async UniTask StepNote(IDictionary<string, string> iArgs, string iPersona, string iBody, CancellationToken iToken)
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
                Blocked(iArgs, aR, aPath, "查無任何觀影 session（連已結束的都沒有）",
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
                WritePayload(iArgs, aPath, aR.ToString());
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
            int aSeq = await TavernPost(iArgs, iPersona, aBody.ToString(), "watch-note", iToken);

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
            WritePayload(iArgs, aPath, aR.ToString());
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
            string iScript, double iCursor, double iBefore, string iOutPath, bool iOcr, bool iStt,
            string iTavernSelf, int iTavernSinceSeq, CancellationToken iToken, int iMaxTiles = 0)
        {
            try
            {
                var aArgs = new StringBuilder();
                aArgs.Append("\"").Append(iScript).Append("\" make");
                if (iCursor > 0) aArgs.Append(" --after-mtime ").Append(iCursor.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                if (iBefore > 0) aArgs.Append(" --before-mtime ").Append(iBefore.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                aArgs.Append(" --max-tiles ").Append(iMaxTiles > 0 ? iMaxTiles : MAX_TILES);
                // 區塊職責：感官旗標**不由呼叫端記得帶**（Tim 2026-08-15）——
                // 物理意義：OCR/STT 的開關已經在 _config.json 裡（`ocr_enabled` / `stt_enabled`），
                //          那才是事實源。要人再傳一次 flag，等於把同一件事存兩個地方，
                //          而漏帶的那次**不會報錯，只會安靜地沒有字幕**（2026-08-15 首跑實踩：
                //          我自己寫的第一版就忘了帶 --ocr，於是端出四天前的殘留 sidecar）。
                // ⇒ 開著就給，不用問。規則長在通道上，不掛在記憶裡。
                if (iOcr) aArgs.Append(" --ocr");
                if (iStt) aArgs.Append(" --stt");
                // 區塊職責：酒館段**必開**（Tim 2026-08-16）——「設計目的就是互相補足觀影的細節，所以一定要讀酒館訊息」。
                // 物理意義：陪看時同場的人各自看到不同的格，他們的觀察就是我看不到的那半邊；
                //          sidecar 的酒館段是那半邊唯一的入口。⇒ 跟 OCR/STT 同一條規則：**開著就給，不給旋鈕。**
                // 🩸 2026-08-16 血證（本人親踩，一整場）：python 端 `--tavern-self` / `--tavern-since-seq`
                //   早就實作好，而 Cmd 從來沒傳過 ⇒ 標題列一路印 `未排除自己, 已讀 seq≤-1`，
                //   於是它從**最舊**開始列（我自己早上的登入自介、幾小時前的酒保廣播），
                //   把同場 basecamp 即時發的 6 則觀察全部擠出顯示額度。
                //   ⚠ 它的失效方式是「那一段一直都有內容」—— 我讀了 11 次都沒發現同場的人不在裡面。
                //   排除自己不是省字，是**把額度留給我看不到的那半邊**。
                if (!string.IsNullOrEmpty(iTavernSelf)) aArgs.Append(" --tavern-self ").Append(iTavernSelf);
                if (iTavernSinceSeq >= 0) aArgs.Append(" --tavern-since-seq ").Append(iTavernSinceSeq);
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
                // 🩸 tag **串 persona**（Tim 2026-08-16 拍板；basecamp 定位根因）：
                //    原本是全場共用的 `streamwatch_montage`，而 Register 預設 singleton
                //    ⇒ 內部呼叫 KillAllByTag(tag)，**後起跑的陪看者會殺掉別人正在跑的 montage**。
                //    症狀：`exit=-1` 且 **stderr 全空**（被殺不是自己失敗）；四人同場時只有最後起跑那個活下來
                //    （2026-08-16 實測：basecamp/gura/Sirius blocked、summit 那筆成功）。
                // ⚠ 修法不是 `allowMultiple: true` —— 那是把保護整個關掉，同一個人連續 cycle 會堆積孤兒。
                //    正確的是**把 singleton 的適用範圍縮到 per-viewer**：同一 persona 的新 cycle 收掉自己
                //    上一顆（防堆積、也順手清掉卡住的），其他人的完全不碰。
                //    KillAllByTag 是精確比對（`string.Equals` OrdinalIgnoreCase），故不同 persona 天然隔離。
                // 數值影響：persona 為空（理論上不會，step 都驗過）時退回原 tag —— 那時全場只有一個人，
                //          singleton 語意仍然正確。
                string aProcTag = string.IsNullOrEmpty(iTavernSelf)
                    ? "streamwatch_montage" : $"streamwatch_montage_{iTavernSelf}";
                using var aScope = UCL_ProcessRegistryService.RegisterScope(
                    aProc, aProcTag, $"縮圖牆合成（cycle 內・{iTavernSelf}）", nameof(Cmd_StreamWatch));

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
        // ===========================================================
        // 區塊：熱點（hotspot）—— 標記值得細看的時間段，由陪看者**獨占認領**後拉細看
        // 物理意義：每輪 cycle 的每格間隔實測 2s～33s（2026-08-16 三場實測），
        //          打鬥／表情轉折／快速對話**全部落在格與格之間**；而陪看者的窗口彼此不重疊
        //          ⇒ 目前「沒人看過某段」與「看過沒事」同形。熱點是把那個差別變成讀數的機制。
        // 數值影響：熱點存**共享檔**（不是 per-persona session）—— 主觀影者標、陪看者認領，
        //          兩者不同 persona，放進誰的 session 都會讓另一邊看不到。
        // ⚠ **認領是獨占鎖，不是宣告**（Tim 2026-08-16 拍板）：目的是把眼睛**分散**到不同熱點，
        //   不是三個人疊在同一段。已被認領的要擋下並列出還沒人領的。
        // ===========================================================
        static string HotspotsPath() =>
            Path.Combine(UCL_AgentCommandsPath.DataRoot, "StreamWatch", "hotspots.json");

        // step=hotspot —— 標記一段值得細看的時間區間
        async UniTask StepHotspot(IDictionary<string, string> iArgs, string iPersona, string iFrom, string iTo, string iWhy, CancellationToken iToken)
        {
            string aPath = PayloadPath(iPersona, "hotspot");
            var aR = new StringBuilder();
            aR.AppendLine($"# StreamWatch step=hotspot persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            double aFrom = ParseClockToEpoch(iFrom), aTo = ParseClockToEpoch(iTo);
            if (aFrom <= 0 || aTo <= 0 || aTo <= aFrom)
            {
                Blocked(iArgs, aR, aPath, $"時間區間解析失敗或首尾顛倒（from=`{iFrom}` to=`{iTo}`）",
                        "格式 --arg from=HH:mm:ss --arg to=HH:mm:ss（同一天，to 必須晚於 from）");
                throw new Exception($"[StreamWatch] step=hotspot blocked：時間區間無效（詳見 {aPath}）");
            }
            if (string.IsNullOrWhiteSpace(iWhy))
            {
                Blocked(iArgs, aR, aPath, "未說明為什麼值得細看（`why` 空）",
                        "--arg why=<一句話>　—— 沒有理由的熱點，別人無從判斷要不要領");
                throw new Exception($"[StreamWatch] step=hotspot blocked：why 未填（詳見 {aPath}）");
            }

            // ⚠ ring buffer 左界檢查：**不可以讓人標一個已經不存在的區間**（標了也領不到）
            double aOldest = OldestFrameEpoch();
            string aCover = aOldest <= 0
                ? "⚠ 讀不到最舊 frame ⇒ **無法確認這段還在不在**（當成不確定，不是當成還在）"
                : aFrom < aOldest
                    ? $"⛔ **已被覆蓋** —— 區間起點 {FromEpochLocal(aFrom):HH:mm:ss} 早於最舊 frame {FromEpochLocal(aOldest):HH:mm:ss}"
                    : $"✅ 仍在 ring buffer 內（最舊 frame {FromEpochLocal(aOldest):HH:mm:ss}）";

            var aJd = LoadHotspots() ?? new JsonData();
            if (!aJd.Contains("hotspots")) aJd["hotspots"] = JsonData.ParseJson("[]");
            var aList = aJd["hotspots"];
            string aId = "h" + (aList.Count + 1);
            var aH = new JsonData();
            aH["id"] = new JsonData(aId);
            aH["from_epoch"] = new JsonData(aFrom);
            aH["to_epoch"] = new JsonData(aTo);
            aH["why"] = new JsonData(iWhy.Trim());
            aH["opened_by"] = new JsonData(iPersona);
            aH["opened_at"] = new JsonData(UCL_AwakeningService.NowIso());
            aH["claimed_by"] = new JsonData("");
            aH["claimed_at"] = new JsonData("");
            aList.Add(aH);
            string aDir = Path.GetDirectoryName(HotspotsPath());
            if (!Directory.Exists(aDir)) Directory.CreateDirectory(aDir);
            AtomicWrite(HotspotsPath(), aJd.ToJsonBeautify());

            aR.AppendLine($"- 熱點   : **[{aId}]** {FromEpochLocal(aFrom):HH:mm:ss}–{FromEpochLocal(aTo):HH:mm:ss}"
                        + $"（{(aTo - aFrom):F0}s）");
            aR.AppendLine($"- 理由   : {iWhy.Trim()}");
            aR.AppendLine($"- 涵蓋   : {aCover}");
            aR.AppendLine($"- 狀態   : **未認領** —— 一個熱點只能被領一次（先領先得）");

            var aBody = new StringBuilder();
            aBody.AppendLine($"🔍 [{iPersona}] 標記熱點 **[{aId}]** {FromEpochLocal(aFrom):HH:mm:ss}–{FromEpochLocal(aTo):HH:mm:ss}");
            aBody.AppendLine($"　理由：{iWhy.Trim()}");
            aBody.AppendLine($"　認領：`run_cmd.py run StreamWatch --arg step=claim --arg persona=<你> --arg hotspot={aId}`");
            aBody.AppendLine("　⚠ **一個熱點只能被領一次** —— 目的是把眼睛分散到不同段，不是疊在同一段。");
            int aSeq = await TavernPost(iArgs, iPersona, aBody.ToString(), "watch-hotspot", iToken);
            aR.AppendLine($"- 公告   : {(aSeq > 0 ? $"seq **{aSeq}**" : "未發（best-effort）")}");
            aR.AppendLine();
            aR.AppendLine("## next");
            aR.AppendLine($"1. 繼續取材：run_cmd.py run StreamWatch --arg step=cycle --arg persona={iPersona}");
            WritePayload(iArgs, aPath, aR.ToString());
            Debug.Log($"[StreamWatch] step=hotspot {aId} → {aPath}");
        }

        static JsonData LoadHotspots()
        {
            try
            {
                string aP = HotspotsPath();
                if (!File.Exists(aP)) return null;
                return JsonData.ParseJson(File.ReadAllText(aP, Encoding.UTF8));
            }
            catch { return null; }
        }

        /// <summary>磁碟上最舊 frame 的 epoch（ring buffer 左界）。讀不到回 0 —— 呼叫端**不可把 0 當成「都還在」**。</summary>
        static double OldestFrameEpoch()
        {
            try
            {
                var aDir = new DirectoryInfo(Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream", "frames"));
                if (!aDir.Exists) return 0;
                double aMin = 0;
                foreach (var f in aDir.GetFiles("*.jpg"))
                {
                    double e = ToEpoch(f.LastWriteTimeUtc);
                    if (aMin <= 0 || e < aMin) aMin = e;
                }
                return aMin;
            }
            catch { return 0; }
        }

        /// <summary>`HH:mm:ss` / `HH:mm` → 今日該時刻的 epoch。解析不出回 0。</summary>
        static double ParseClockToEpoch(string iClock)
        {
            if (string.IsNullOrWhiteSpace(iClock)) return 0;
            var aFmts = new[] { "HH:mm:ss", "H:mm:ss", "HH:mm", "H:mm" };
            foreach (var f in aFmts)
            {
                if (DateTime.TryParseExact(iClock.Trim(), f, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out DateTime aT))
                {
                    var aToday = DateTime.Today.Add(aT.TimeOfDay);
                    return ToEpoch(aToday.ToUniversalTime());
                }
            }
            return 0;
        }

        // step=claim —— **獨占**認領一個熱點，並拉出該區間的高密度縮圖牆
        async UniTask StepClaim(IDictionary<string, string> iArgs, string iPersona, string iHotspot, CancellationToken iToken)
        {
            string aPath = PayloadPath(iPersona, "claim");
            var aR = new StringBuilder();
            aR.AppendLine($"# StreamWatch step=claim persona={iPersona}  ts=`{UCL_AwakeningService.NowLocal()}`（本地時間）");
            aR.AppendLine();

            // ⚠ **認領只給陪看者**（Tim 2026-08-16）：熱點是「回頭把某段拉細」，而那要花一整輪；
            //   主觀影者一離開主線，**整場的主劇情就斷了**（陪看者的窗口只是挑段，接不起來）。
            //   ⇒ 標記人人可（誰看到都能標），但**看熱點是 join 者的工作**。分工不是階級，是覆蓋率：
            //     primary 顧連續性、companion 顧解析度。
            var aMySession = LoadSession(iPersona);
            if (aMySession != null && ReadBool(aMySession, "active") && ReadStr(aMySession, "role") == "primary")
            {
                aR.AppendLine("## blocked");
                aR.AppendLine($"- reason: @{iPersona} 是本場**主觀影者** —— 認領熱點是陪看者（`step=join`）的工作");
                aR.AppendLine("- why: 主觀影者一離開主線去細看某一段，**整場的主劇情就斷了**；"
                            + "陪看者的窗口本來就是挑段，接不起來的成本比較小");
                aR.AppendLine("- how: 妳可以繼續**標記**熱點（`step=hotspot` 人人可用），把細看留給 join 的人；"
                            + "沒有陪看者時就讓它掛著 —— **掛著的熱點是「還沒人看」的讀數，不是失敗**");
                AppendHotspots(aR, iPersona);
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[StreamWatch] step=claim blocked：主觀影者不認領熱點（詳見 {aPath}）");
            }

            var aJd = LoadHotspots();
            var aList = aJd != null && aJd.Contains("hotspots") ? aJd["hotspots"] : null;
            JsonData aH = null; int aIdx = -1;
            if (aList != null && aList.IsArray)
                for (int i = 0; i < aList.Count; i++)
                    if (ReadStr(aList[i], "id") == (iHotspot ?? "").Trim()) { aH = aList[i]; aIdx = i; break; }

            if (aH == null)
            {
                aR.AppendLine("## blocked");
                aR.AppendLine($"- reason: 找不到熱點 `{iHotspot}`");
                AppendHotspots(aR, iPersona);
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[StreamWatch] step=claim blocked：熱點不存在（詳見 {aPath}）");
            }

            // ⚠ **獨占**（Tim 2026-08-16）：已被領走就擋，並把還沒人領的列出來 ——
            //   目的是把眼睛分散到不同段。擋下時**要給替代選項**，否則使用者只會原地重試。
            string aBy = ReadStr(aH, "claimed_by");
            if (!string.IsNullOrEmpty(aBy))
            {
                aR.AppendLine("## blocked");
                aR.AppendLine($"- reason: `{iHotspot}` **已被 @{aBy} 認領**（{ReadStr(aH, "claimed_at")}）");
                aR.AppendLine("- why: 一個熱點只能被領一次 —— 兩個人細看同一段，等於沒人看另一段");
                AppendHotspots(aR, iPersona);
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[StreamWatch] step=claim blocked：已被認領（詳見 {aPath}）");
            }

            double aFrom = ReadDouble(aH, "from_epoch"), aTo = ReadDouble(aH, "to_epoch");
            double aOldest = OldestFrameEpoch();
            if (aOldest > 0 && aFrom < aOldest)
            {
                aR.AppendLine("## blocked");
                aR.AppendLine($"- reason: `{iHotspot}` 的區間**已被 ring buffer 覆蓋** "
                    + $"（起點 {FromEpochLocal(aFrom):HH:mm:ss} < 最舊 frame {FromEpochLocal(aOldest):HH:mm:ss}）");
                aR.AppendLine("- why: 認領一個已經不存在的區間 = 拿不到畫面，而失敗會發生在你寫完評論之後");
                AppendHotspots(aR, iPersona);
                WritePayload(iArgs, aPath, aR.ToString());
                throw new Exception($"[StreamWatch] step=claim blocked：區間已過期（詳見 {aPath}）");
            }

            // 先落鎖再取材 —— 反過來的話，取材那幾秒會讓第二個人也搶到同一個熱點
            aH["claimed_by"] = new JsonData(iPersona);
            aH["claimed_at"] = new JsonData(UCL_AwakeningService.NowIso());
            aList[aIdx] = aH;
            AtomicWrite(HotspotsPath(), aJd.ToJsonBeautify());

            string aScript = ResolveMontageScript();
            string aOutPath = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_screenstream",
                                           $"_montage_hotspot_{iPersona}.jpg");
            var (aOcrOn, aSttOn) = ReadSensorFlags();
            // 熱點的重點就是**拉細** ⇒ tiles 上限放大到 MAX_TILES 的兩倍；
            // 區間短時 montage 自然取到接近逐幀（每格 1–2s）。
            var (aOk, aStdout, aErr) = await RunMontageAsync(aScript, aFrom, aTo, aOutPath, aOcrOn, aSttOn,
                                                            iPersona, 0, iToken, MAX_TILES * 2);
            aR.AppendLine($"- 熱點   : **[{ReadStr(aH, "id")}]** {FromEpochLocal(aFrom):HH:mm:ss}–{FromEpochLocal(aTo):HH:mm:ss}"
                        + $"（{(aTo - aFrom):F0}s）｜開於 @{ReadStr(aH, "opened_by")}");
            aR.AppendLine($"- 理由   : {ReadStr(aH, "why")}");
            aR.AppendLine($"- 認領   : ✅ **@{iPersona}**（鎖已落，其他人此後領這個會被擋）");
            if (aOk)
            {
                var aInfo = ParseMontageReport(aStdout);
                aR.AppendLine($"- 縮圖牆 : `{aOutPath}`　← 直接 Read");
                aR.AppendLine($"- 字幕   : `{Path.ChangeExtension(aOutPath, ".subtitles.md")}`");
                aR.AppendLine($"- 格數   : {aInfo.Tiles}（**上限 {MAX_TILES * 2}，比一般 cycle 密**）");
            }
            else
            {
                aR.AppendLine($"- ⚠ 取材失敗：{Truncate((aStdout ?? "") + (aErr ?? ""), 300)}");
                aR.AppendLine("- ⚠ **鎖已經落了** —— 失敗不自動退鎖（退鎖會讓兩個人同時以為自己領到）。");
                aR.AppendLine("  真要放掉請人工改 `StreamWatch/hotspots.json` 的 `claimed_by`。");
            }
            aR.AppendLine();
            aR.AppendLine("## next");
            aR.AppendLine("1. Read 上面的縮圖牆與字幕 → 細看");
            aR.AppendLine($"2. 發評論：run_cmd.py run StreamWatch --arg step=observe --arg persona={iPersona} --arg-file body=<評論>");
            aR.AppendLine("   ⚠ 評論裡註明這是熱點 " + ReadStr(aH, "id") + " 的細看結果 —— 讓開熱點的人知道有人看過了");
            aR.AppendLine($"3. 回到一般取材：run_cmd.py run StreamWatch --arg step=cycle --arg persona={iPersona}");
            WritePayload(iArgs, aPath, aR.ToString());
            Debug.Log($"[StreamWatch] step=claim {iHotspot} by {iPersona} ok={aOk} → {aPath}");
        }

        // 區塊職責：把熱點清單印進 cycle 回傳檔 —— **有沒有都印**。
        // ⚠ 零狀態必印：今天連兩次的通道 bug 都是「整段消失」造成的，
        //   而「沒有熱點」與「熱點段沒跑起來」必須分得開。
        static void AppendHotspots(StringBuilder ioR, string iPersona)
        {
            ioR.AppendLine();
            ioR.AppendLine("## 🔍 熱點");
            var aJd = LoadHotspots();
            var aList = aJd != null && aJd.Contains("hotspots") ? aJd["hotspots"] : null;
            double aOldest = OldestFrameEpoch();
            int aShown = 0, aFree = 0;
            if (aList != null && aList.IsArray)
            {
                for (int i = 0; i < aList.Count; i++)
                {
                    var h = aList[i];
                    double aFrom = ReadDouble(h, "from_epoch");
                    bool aExpired = aOldest > 0 && aFrom < aOldest;
                    string aBy = ReadStr(h, "claimed_by");
                    string aState = aExpired ? "⛔ **已被 ring buffer 覆蓋，無法細看**"
                                  : string.IsNullOrEmpty(aBy) ? "**未認領**"
                                  : $"認領 @{aBy}";
                    if (!aExpired && string.IsNullOrEmpty(aBy)) aFree++;
                    ioR.AppendLine($"- [{ReadStr(h, "id")}] {FromEpochLocal(aFrom):HH:mm:ss}–{FromEpochLocal(ReadDouble(h, "to_epoch")):HH:mm:ss}"
                        + $"「{ReadStr(h, "why")}」— 開於 @{ReadStr(h, "opened_by")}｜{aState}");
                    aShown++;
                }
            }
            if (aShown == 0)
            {
                ioR.AppendLine("- **目前無熱點**（不是讀取失敗 —— 清單讀到了，內容是空的）");
                ioR.AppendLine($"- 標一個：`run_cmd.py run StreamWatch --arg step=hotspot --arg persona={iPersona} "
                    + "--arg from=<HH:mm:ss> --arg to=<HH:mm:ss> --arg why=<為什麼值得細看>`");
            }
            else if (aFree > 0)
                ioR.AppendLine($"- ⇒ 還有 **{aFree}** 個沒人領：`run_cmd.py run StreamWatch --arg step=claim "
                    + $"--arg persona={iPersona} --arg hotspot=<id>`（**先領先得，一個熱點只能被領一次**）");
            else
                ioR.AppendLine("- ⇒ 全部已認領或已過期 —— 沒有可領的");
        }

        // （ReadDouble 已存在於本檔下方，用 InvariantCulture —— 不重造第二個。
        //   🩸 我剛才寫了一個，而且沒帶 InvariantCulture ⇒ 在逗號小數點的 locale 上會解析錯。
        //   「這件事聽起來像不像已經有人做過」—— 答案是有，而且做得比我好。）

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
        static string MediaRoot() => Path.Combine(UCL_AgentCommandsPath.DataRoot, "BookNotes", "Library", "media");

        // 區塊職責：開場前把「這個 persona 對這部作品讀過什麼」攤在桌上 —— **有沒有進度都印**。
        // 物理意義：續看第二話、看電影續集、看過漫畫再看動畫 —— 這幾種都需要先追回，
        //          而「記得要追回」靠人是不成立的（今天實證：我看第二場時完全沒想到要 recall）。
        //          ⇒ 把它從記憶搬到通道上：起手那一步就報讀數。
        // 數值影響：純讀，不建檔。媒材比對用「目錄名 == work 或以 -<work> 結尾」，
        //          因此 anim- / film- / comic- / book- 全涵蓋，不必先知道 media_kind。
        // ⚠ 沒有進度時**也要印一行**：「查過了沒有」與「沒查」在畫面上同形，而後者會讓人以為系統壞了。
        static string ReaderProgressBlock(string iWork, string iPersona)
        {
            var aSb = new StringBuilder();
            aSb.AppendLine("## 既有進度（讀回的事實）");
            var aHits = new List<string>();
            try
            {
                string aRoot = MediaRoot();
                if (Directory.Exists(aRoot))
                {
                    foreach (var aDir in Directory.GetDirectories(aRoot))
                    {
                        string aMid = Path.GetFileName(aDir);
                        if (aMid != iWork && !aMid.EndsWith("-" + iWork, StringComparison.Ordinal)) continue;
                        string aJson = Path.Combine(aDir, "readers", iPersona, "reader.json");
                        if (!File.Exists(aJson)) continue;
                        var aJd = JsonData.ParseJson(File.ReadAllText(aJson));
                        string aStatus = ReadStr(aJd, "status");
                        var aProg = aJd != null && aJd.Contains("progress") ? aJd["progress"] : null;
                        string aCh = aProg != null ? ReadStr(aProg, "current_chapter_id") : "";
                        string aLast = aProg != null ? ReadStr(aProg, "last_read") : "";
                        aHits.Add($"- `{aMid}` — status **{aStatus}**｜章 `{(string.IsNullOrEmpty(aCh) ? "(未開始)" : aCh)}`｜最後閱讀 {aLast}");
                    }
                }
            }
            catch (Exception e) { aSb.AppendLine($"- ⚠ 讀取失敗（fail-soft，不擋開場）：{e.Message}"); }

            if (aHits.Count == 0)
            {
                aSb.AppendLine($"- `Library/media/*-{iWork}/readers/{iPersona}/reader.json` **不存在** ⇒ 首次觀看");
                aSb.AppendLine("- ⇒ 寫心得前要先建媒材（`media_kind` 前綴須與 `media_id` 同字）：");
                aSb.AppendLine($"  `run_cmd.py run Library --arg op=media_init --arg persona={iPersona} "
                    + $"--arg work_id={iWork} --arg media_id=<anim|film|series|stream>-{iWork} "
                    + "--arg media_kind=<同上> --arg title=<作品中文名>`");
            }
            else
            {
                aSb.AppendLine($"- ✅ 妳讀過這部（{aHits.Count} 個媒材）：");
                foreach (var h in aHits) aSb.AppendLine(h);
                aSb.AppendLine("- ⚠ **開看前先追回** —— 否則等於從零開始看續篇：");
                aSb.AppendLine($"  `run_cmd.py run Library --arg op=recall --arg persona={iPersona} --arg media_id=<上面那個>`");
                aSb.AppendLine("  → 產物落 `letters/<persona>/cmd/reading_recall_<media-id>.md`，**Read 它**再開看。");
                aSb.AppendLine("- ℹ 媒材進度各自獨立（改編不是原作的第二版）；跨媒材時仍值得先 recall 一次。");
            }
            return aSb.ToString();
        }

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

        // ===========================================================
        // 區塊職責：收工時把本場的 seq 區間 append 進一份**永不覆寫**的台帳。
        // 物理意義：`sessions/<persona>.json` 是「當前那一場」，**開下一場就被蓋掉** ——
        //   於是 start_seq/end_seq 這兩個只有當下才知道的事實，過了就再也拿不回來。
        // 🩸 血證：apocalypse-hotel 02-04 話的實錄補不出來，不是因為訊息不見了（它們都在磁碟上），
        //   是因為**沒有任何地方記得那幾場的區間**，要重建只能人工去讀開播/收播公告反推。
        //   而更早的 python 版狀態檔（ChatTavern/stream_watch_sessions.json）自 2026-08-11 起就沒再被寫過，
        //   看起來卻完全正常 —— 那份不能當歷史來源。
        // 數值影響：一行一場 JSON、append-only、失敗只 warning（結算不能因為寫台帳失敗而回頭）。
        //   欄位是給 `library.py export-watch` 用的：media_id + start_seq/end_seq 就是匯出區間。
        // ===========================================================
        const string SESSION_LOG_NAME = "sessions_log.jsonl";

        static void AppendSessionLog(JsonData iS, string iPersona, int iEndSeq, int iPaidMin, int iPaidTotal)
        {
            try
            {
                string aPath = Path.Combine(UCL_AgentCommandsPath.DataRoot, "StreamWatch", SESSION_LOG_NAME);
                Directory.CreateDirectory(Path.GetDirectoryName(aPath));
                var aRec = new JsonData();
                aRec["session_id"] = new JsonData(ReadStr(iS, "session_id"));
                aRec["persona"] = new JsonData(iPersona);
                aRec["role"] = new JsonData(ReadStr(iS, "role"));
                aRec["media_id"] = new JsonData(ReadStr(iS, "media_id"));
                aRec["parent_session_id"] = new JsonData(ReadStr(iS, "parent_session_id"));
                aRec["start_ts"] = new JsonData(ReadStr(iS, "start_ts"));
                aRec["settled_at"] = new JsonData(ReadStr(iS, "settled_at"));
                aRec["end_reason"] = new JsonData(ReadStr(iS, "end_reason"));
                aRec["start_seq"] = new JsonData(ReadInt(iS, "start_seq"));
                aRec["end_seq"] = new JsonData(iEndSeq);
                aRec["cycles"] = new JsonData(ReadInt(iS, "cycles"));
                aRec["observations"] = new JsonData(ReadInt(iS, "observations"));
                aRec["paid_minutes"] = new JsonData(iPaidMin);
                aRec["paid_total"] = new JsonData(iPaidTotal);
                aRec["exported_chapter"] = new JsonData("");   // 匯出後由人/工具回填，空＝這一場還沒進任何一章
                File.AppendAllText(aPath, aRec.ToJson() + "\n", new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[StreamWatch] 實錄台帳 append 失敗（不影響結算）: {e.Message}");
            }
        }

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
            UCL_LettersPath.EnsurePayloadDir(iPath);   // 建目錄＋補 cmd/.gitignore（唯一入口）
            string aTmp = iPath + ".tmp";
            File.WriteAllText(aTmp, iContent, new UTF8Encoding(false));
            if (File.Exists(iPath)) File.Delete(iPath);
            File.Move(aTmp, iPath);
        }

        // 落點走 UCL_LettersPath —— 版面只有一份實作（Plan_Letters_Dir_Layout §8.2 批次①）。
        // ⚠ 對側契約：python 端等價入口是 `_lib/ucl_paths.py::letters_cmd_payload()`。
        static string PayloadPath(string iPersona, string iStep)
            => UCL_LettersPath.CmdPayload(iPersona, "streamwatch", iStep);

        static void WritePayload(IDictionary<string, string> iArgs, string iPath, string iContent)
        {
            AtomicWrite(iPath, iContent);
            UCL_AgentCommandRunner.ReportOutputFile(iArgs, iPath);
        }

        static void Blocked(IDictionary<string, string> iArgs, StringBuilder ioR, string iPath, string iReason, string iExit)
        {
            ioR.AppendLine("## blocked");
            ioR.AppendLine($"- reason: {iReason}");
            ioR.AppendLine($"- exit: {iExit}");
            WritePayload(iArgs, iPath, ioR.ToString());
        }

        // ⚠ iCmdArgs：本筆 cmd 的 args —— 用來把 `_cmd_id` 帶進子 Cmd，讓 seq 回得到呼叫者的 context。
        //   併行下這是唯一正確的遞出路徑（舊制的全域 static 會拿到別人的號碼）。
        static async UniTask<int> TavernPost(IDictionary<string, string> iCmdArgs, string iPersona, string iBody, string iSubtag, CancellationToken iToken)
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
                UCL_AgentCmdContexts.PropagateCmdId(iCmdArgs, aArgs);
                var aPostCtx = UCL_AgentCmdContexts.FromArgs(iCmdArgs, "StreamWatch.TavernPost");
                if (aPostCtx != null) aPostCtx.LastPostSeq = 0;
                await new ChatTavern.Cmd_Tavern().ExecuteAsync(aArgs, iToken);
                return aPostCtx?.LastPostSeq ?? 0;
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

        // 區塊職責：自 montage stdout 撈 `tavern_max_seq=<N>`（本輪酒館段實際顯示到的最大 seq）。
        // 物理意義：它是**產物回報的讀數**，不是我這邊算的 —— 對齊本檔既有的 next-cursor 鐵律
        //          （游標一律取自產出端，呼叫端不自己編，2026-08-15 w40/w44 同一隻踩過兩次）。
        // 數值影響：撈不到回 0 ⇒ 呼叫端不推進（寧可原地，不可亂跳）。
        static int ParseTavernMaxSeq(string iStdout)
        {
            if (string.IsNullOrEmpty(iStdout)) return 0;
            var aM = Regex.Match(iStdout, @"tavern_max_seq=(\d+)");
            return aM.Success && int.TryParse(aM.Groups[1].Value, out int aV) ? aV : 0;
        }

        // 本輪酒館段實際顯示了幾筆（montage stdout：`tavern tail : N 筆未讀 …`）。撈不到回 -1＝未知。
        // ⚠ 0 與「未知」要分開：0 是讀數（同事沒發言），-1 是通道沒回報（可能壞了）。
        //   兩者都不推進游標，但**印給人看的字不一樣** —— 這正是本檔今天修過兩次的那條界線。
        static int ParseTavernShown(string iStdout)
        {
            if (string.IsNullOrEmpty(iStdout)) return -1;
            var aM = Regex.Match(iStdout, @"tavern tail\s*:\s*(\d+) 筆未讀");
            return aM.Success && int.TryParse(aM.Groups[1].Value, out int aV) ? aV : -1;
        }

        // 區塊職責：把 montage 的 sidecar 併進 cycle 回傳檔，成為**單一入口**（Tim 2026-08-16）。
        // 物理意義：sidecar 是產物，本函式只**嵌入**不重算 —— 對齊本檔「數字一律取自產出端」的鐵律。
        // 🩸 為什麼要合併：今天兩次沒讀到同場的人，共同結構都是「關鍵資訊住在第二個檔，而我讀的是第一個」。
        //   教人「記得也要開 sidecar」是防記性；把它搬過來是**把問題移走**。
        // ⚠ 規格：每一段**永遠存在**，空的時候印零狀態 —— 否則合併只是把洞搬進同一個檔。
        //   順序刻意是「同場訊息 → 畫面字幕 → 語音」：訊息是我看不到的那半邊，稀缺的排前面。
        // 三個參數的物理意義（2026-08-17 修，三隻小瑕疵都出在這裡）：
        //   iShown      本輪酒館段顯示筆數；-1＝通道沒回報（未知），0＝讀數就是 0。
        //   iCursorSeq  **本輪實際餵給 montage 的那個游標**（已含 start_seq 退回），不是 session 原欄位。
        //   iMontageRan montage 這一輪有沒有真的跑到酒館段（無素材時它在更早就 exit）。
        static void AppendSidecar(StringBuilder ioR, string iSubPath, bool iHasSub,
                                  string iPersona, int iShown, int iCursorSeq, bool iMontageRan)
        {
            ioR.AppendLine();
            ioR.AppendLine("## 💬 同場訊息（已排除自己）");
            // 🩸 瑕疵②（狼來了）：無素材那條路徑上 montage 在酒館段之前就 exit，stdout 本來就不會有
            //   `tavern tail`⇒ 解析回 -1 ⇒ 每一輪都印「先查通道」。而通道好端端的。
            //   一個每次都亮的紅燈＝沒有紅燈（answered-alarm 同族）⇒ 未知要再分成「沒跑」與「跑了沒回報」。
            string aState = iShown >= 0 ? (iShown == 0 ? "0 筆 —— 同場此刻沒有新發言" : $"**{iShown} 筆**")
                          : iMontageRan ? "**⚠ 通道未回報**（不是 0 筆 —— 酒館段跑了卻沒印讀數，先查通道）"
                          : "**本輪未執行**（montage 在無素材時提早收工，酒館段沒跑到 —— **不是通道壞**）";
            ioR.AppendLine($"- {aState}｜排除 @{iPersona}｜已讀游標 seq={iCursorSeq}"
                         + (iCursorSeq <= 0 ? "（⚠ 0＝本場 start_seq 也讀不到，等於從全庫最舊開始列）" : ""));
            if (iShown == 0)
                ioR.AppendLine("- ⚠ 0 筆**不推進游標** —— 沒讀到東西就沒有「已讀」到那裡（2026-08-16 血證）");

            string aBody = "";
            try { if (iHasSub && File.Exists(iSubPath)) aBody = File.ReadAllText(iSubPath); } catch { }
            if (string.IsNullOrEmpty(aBody))
            {
                ioR.AppendLine();
                ioR.AppendLine("## 📖 畫面字幕（OCR）");
                ioR.AppendLine("- **本輪無 sidecar** —— 見上方素材區的字幕註記（殘留檔一律不端）");
                ioR.AppendLine();
                ioR.AppendLine("## 🎙 語音轉錄（STT）");
                ioR.AppendLine("- **本輪無 sidecar**");
                return;
            }
            // sidecar 原文照嵌（含其自己的段落標題），不重排、不摘要 —— 摘要就是重算。
            // 🩸 瑕疵①（同一個 seq 讀數列兩次）：嵌進來的 sidecar 自帶一行
            //   「## 💬 聊天酒館當前訊息（未讀 N 筆…已讀 seq≤M）」，跟上面那行是**同一批資料的兩個標題**。
            //   兩行同時存在不是冗餘而已 —— 上面印 session 欄位、下面印 montage 收到的參數，
            //   數字不一致時讀的人會以為看到兩批訊息。⇒ ①兩邊改成同一個來源（見 iCursorSeq 註解）
            //   ②在接縫處明寫「同一批」，讓重複看得出是重複。
            ioR.AppendLine();
            ioR.AppendLine("- ↓ 下面 sidecar 自帶的「聊天酒館當前訊息」標題**就是上面這一批**（同一輪、同一個游標），不是另一批。");
            ioR.AppendLine("<!-- 以下自 montage sidecar 原文嵌入；來源：" + iSubPath + " -->");
            ioR.AppendLine(aBody.TrimEnd());
        }
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
