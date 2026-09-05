// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 09/05 2026
// 區塊職責：Coding session（改 C# 的施工場）的進場／改狀態／退出閘（TASK-0058 A1）。
// 物理意義：改 C# 這件事在此之前沒有任何「誰正在做」的資料 —— 於是編譯紅燈是誰造成的
//          只能靠人肉歸因（血證：2026-08-26 basecamp 驗 TASK-0051 時 ErrorLog 混入
//          summit TASK-0052 施工中的三筆紅）。本 Cmd 讓那件事變成一筆可查的場。
// 數值影響：寫 sessions/<persona>.json（唯一寫入端仍是 SCP_ActivitySessionStore）
//          ＋ 順手投影到 persona lock 的 now_status（唯一寫入通道 Awakening.UCL_AwakeningService.UpdateNowStatus）。
//          退出閘只**讀** .compile_status.json，不寫。
//
// ⚠ 射程（A1）：**Unity 側**。Senate 那側的進場入口與 build.sh 退出閘是 A2，本檔沒有。
//   ⛔ 因此任何回報都不准寫「Coding session 已上線」，只能寫「Unity 側已上線，Senate 側未納入」。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// Coding session —— 改 C# 前進場、改完退出。**全域同時至多一人**。
    ///
    /// <para>典型用法：</para>
    /// <code>
    /// # 進場（status 必填 —— 一句話說在改什麼）
    /// senate ucmd run Coding --persona summit --arg step=start --arg status="TASK-0058 加 Coding kind"
    ///
    /// # 場中改狀態
    /// senate ucmd run Coding --persona summit --arg step=status --arg status="改退出閘"
    ///
    /// # 退出（先過編譯閘）
    /// senate ucmd run Coding --persona summit --arg step=end
    /// </code>
    /// </summary>
    public class Cmd_Coding : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Coding";

        public override string ShortDescription =>
            "Coding session：改 C# 前進場（全域至多一人）／場中改狀態／退出前過編譯閘。";

        public override string ArgsSchema =>
            "step=start|status|end（必填） | persona=<名字>（必填，不猜身分） | " +
            "status=<一句話在改什麼>（step=start/status 必填） | " +
            "force=1（step=end 專用：跳過編譯閘，需同時給 reason） | reason=<為什麼要 force>";

        public override string ExampleArgs => "step=start persona=summit status=TASK-0058 加 Coding kind";

        public override string HelpURL =>
            "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Coding.md";

        // 這一句在三個地方要一字不差（進場成功／狀態更新／退出）——抽成常數，
        // 免得 A2 落地時只改到其中兩處，留下一句宣稱射程比事實大的話。
        const string kScopeCaveat = "⚠ 射程：**Unity 側已上線，Senate 側未納入**（A2 未做）——"
                                    + "在 Senate 那側改 .cs 目前不會被本場擋下，也不會擋下本場。";

        // ===========================================================
        // 區塊職責：把本 kind 的**宿主行為**登記進 `UCL_SessionKindHost`（補收工那條路要用）。
        // 物理意義：kind 的**名字**住 SCP_Core（兩個宿主共用），而「收工指令叫什麼／殘留要不要補結算」
        //          是 Editor 才有的東西 ⇒ 名字在共用層、行為在宿主，各一份真相源。
        //          ⛔ 登記在**這個檔**，不是回頭去改 `Cmd_SessionClose`——
        //          那支 2026-09-05（`092dd940`）才剛把 if 鏈換成登記表，回頭改它等於把那筆退回去。
        // 數值影響：純登記，零 IO。漏了不會報錯 —— 補收工照關，然後印「⚠ 沒有人登記過它」。
        //
        // ⚠ `SettleResidueAsync = null` 是**顯式答案**不是佔位：Coding 沒有金流。
        //   該有卻填 null 的後果是「補收工只翻三欄 ⇒ 酬勞蒸發，而回傳檔說『登記為不需要結算』」。
        // ⚠ `HasStepEnd = true` 是這次把 `op=exit` 改名成 `step=end` **之後**才為真的 ——
        //   改名的理由正是這一格：`Cmd_SessionClose` 印的出口是 `--arg step=end`，
        //   我若保留 `op=exit`，那行對本 kind 就是一條指向不存在指令的指路牌。
        // ===========================================================
        [UnityEditor.InitializeOnLoadMethod]
        static void RegisterSessionKind()
            => UCL_SessionKindHost.Register(new UCL_SessionKindEntry
            {
                Kind = SCP.Core.Session.SCP_ActivitySessionKind.Coding,
                CmdName = "Coding",
                HasStepEnd = true,
                SettleResidueAsync = null,   // null ＝ 這個 kind 真的不用結算（沒有金流），不是「還沒接」
            });

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.Yield();

            string aStep = GetArg(args, "step", "").Trim().ToLowerInvariant();
            string aPersona = GetArg(args, "persona", "").Trim();
            if (string.IsNullOrEmpty(aPersona))
            {
                // 不猜「現在是誰」—— 猜錯會用別人的名字佔住一個全域獨佔的場。
                throw new Exception("[Coding] 需要 --arg persona=<名字>（不猜身分）");
            }
            if (aStep != "start" && aStep != "status" && aStep != "end")
            {
                throw new Exception($"[Coding] step 必為 start|status|end（got '{aStep}'）");
            }

            // ⚠ 回傳檔路徑**只在這裡算一次**，然後傳給每個 step 用。
            //   🩸 2026-09-05 改名 op→step 時，被擋下的訊息裡還留著硬編碼的舊 step 值
            //   ⇒ 它指向 `coding_exit.md`，而 finally 寫的是 `coding_end.md`。**同一天第三次**
            //      「兩個字面必須一致，而沒有任何結構保證它們一致」。
            //   ⇒ 修法不是「改名時記得兩邊都改」，是讓第二份字面不存在。
            string aPayload = UCL_LettersPath.CmdPayload(aPersona, "coding", aStep);

            var aR = new StringBuilder();
            aR.AppendLine($"# Coding step={aStep} persona={aPersona}"
                          + $"  ts=`{DateTime.Now:yyyy-MM-dd HH:mm:sszzz}`（本地時間）");
            aR.AppendLine();

            // ⚠ finally 不是防禦性寫法，是**這支 Cmd 的主要輸出路徑**：
            //   被擋下（進場撞互斥／退出撞編譯閘）時我們 throw 好讓 CLI 非零退出，
            //   而**擋下的原因與出口就在 aR 裡** ⇒ 寫檔若排在 throw 之後，
            //   那條「原因與出口見回傳檔：<路徑>」就指向一個從來沒被寫出來的檔。
            //   🩸 實測過：2026-09-05 第一版正是這樣，Template 搶場被擋、exit=2 正確，
            //      而 letters/Template/cmd/coding_start.md **不存在** —— 訊息完全正確地指向空氣。
            //   ⇒ 修法不是「記得先寫」（那是原則，會忘），是把寫檔搬進 finally（那是結構）。
            try
            {
                switch (aStep)
                {
                    case "start": DoStart(args, aPersona, aR, aPayload); break;
                    case "status": DoStatus(args, aPersona, aR); break;
                    default: DoExit(args, aPersona, aR, aPayload); break;
                }
            }
            finally
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(aPayload));
                    File.WriteAllText(aPayload, aR.ToString(), new UTF8Encoding(false));
                    Debug.Log($"[Coding] step={aStep} persona={aPersona} → {aPayload}");
                    UCL_AgentCommandRunner.ReportOutputFile(args, aPayload);
                }
                catch (Exception e)
                {
                    // ⛔ 落檔失敗不得冒充主動作失敗（也不得吃掉正在往上飛的那個例外）。
                    Debug.LogWarning($"[Coding] 回傳檔寫入失敗 {aPayload}：{e.Message}");
                }
            }
        }

        // ===========================================================
        // 區塊職責：進場 —— 兩條互斥軸都過才寫檔。
        // 物理意義：判斷收在 `SCP_ActivitySessionStore.TryStart`（軸1 每人一場／軸2 全域至多一人），
        //          而**被擋下要說什麼**收在 `UCL_SessionStartGuard`。本函式兩者都不自己做 ——
        //          自己判＝第三份判準，自己組措辭＝第二份措辭，而兩者都不會在漂掉時報錯。
        // 數值影響：成功寫一筆 sessions/<persona>.json；被擋則一個位元組都不寫。
        // ⚠ end_ts 刻意留空 —— 施工場沒有預定時長，退場靠顯式 step=end。
        // ===========================================================
        static void DoStart(Dictionary<string, string> args, string iPersona, StringBuilder ioR, string iPayload)
        {
            string aStatus = GetArg(args, "status", "").Trim();
            if (string.IsNullOrEmpty(aStatus))
            {
                // 驗收第二格：status 必填。空的 status 會讓擋下訊息說不出「他在改什麼」，
                // 而那正是本單要解的那件事（紅燈是誰造成的）。
                throw new Exception("[Coding] step=start 需要 --arg status=<一句話在改什麼>（進場必填）");
            }

            var aSession = new UCL_CodingSession
            {
                persona = iPersona,
                kind = SCP.Core.Session.SCP_ActivitySessionKind.Coding,
                session_id = "coding-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + iPersona,
                start_ts = SCP.Core.Session.SCP_ActivitySession.NowIso(),
                end_ts = "",                    // 無預定時長 —— IsRunningAt 於是只信 active
                until_local = "",               // ⚠ 一律留空 —— 說明不塞資料欄，見 UCL_CodingSession 的 remarks
                active = true,
                status = aStatus,
                status_updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:sszzz"),
            };

            // ⚠ 走 `UCL_SessionStartGuard` 而**不是**直接呼叫 `Store.TryStart`（2026-09-05 改，@basecamp 提案）。
            //   🩸 直呼的代價不是重複幾行 code，是**措辭有兩份**：guard 那份處理了軸1／軸2 的主詞分流
            //   （`eafe501e`），而只要 Coding 直呼，**軸2 唯一的消費端就不在 guard 上**
            //   ⇒ 她那段是對的但走不到，我這段會跑但是第二份。兩份都活、一樣對、沒人知道自己站在哪一個。
            //   ⇒ 收斂成一份，而且是**會被跑到的那一份**。本檔只負責鋪陳，不再自己組原因與出口。
            if (!UCL_SessionStartGuard.TryStart(iPersona, aSession,
                    SCP.Core.Session.SCP_ActivitySessionKind.Coding,
                    out string aBlockReason, out string aBlockExit))
            {
                ioR.AppendLine("## ⛔ 進場被擋 —— 沒有開場");
                ioR.AppendLine();
                ioR.AppendLine($"- 原因：{aBlockReason}");
                ioR.AppendLine($"- 處理方式：{aBlockExit}");
                UCL_AgentCommandRunner.ReportOutputValue(args, "started", "0");
                // ⛔ 非零退出 —— 驗收第三格明寫 blocked 要非零，否則腳本會把「被擋」讀成「開好了」。
                throw new Exception("[Coding] 進場被擋 —— 原因與出口見回傳檔：" + iPayload);
            }

            Awakening.UCL_AwakeningService.UpdateNowStatus(iPersona, "🛠 Coding：" + aStatus);
            UCL_AgentCommandRunner.ReportOutputValue(args, "started", "1");
            UCL_AgentCommandRunner.ReportOutputValue(args, "session_id", aSession.session_id);

            ioR.AppendLine("## ✅ 進場");
            ioR.AppendLine($"- session_id: `{aSession.session_id}`");
            ioR.AppendLine($"- 在改什麼: **{aStatus}**");
            ioR.AppendLine($"- 開場: {aSession.start_ts}（無預定時長）");
            ioR.AppendLine($"- lock now_status 已同步（顯示端：`senate cmd sessions` / catchup 在線清單）");
            ioR.AppendLine();
            ioR.AppendLine(kScopeCaveat);
            ioR.AppendLine();
            ioR.AppendLine("## next");
            ioR.AppendLine($"- 改狀態：`senate ucmd run Coding --persona {iPersona} --arg step=status --arg status=<一句話>`");
            ioR.AppendLine($"- 退出（會先過編譯閘）：`senate ucmd run Coding --persona {iPersona} --arg step=end`");
        }

        // ===========================================================
        // 區塊職責：場中更新「在改什麼」。
        // 物理意義：一場施工會跨好幾個檔，而擋下別人時報的是**現在**在改什麼 ——
        //          停在進場那一句的話，第二個人拿到的是一句過期的話（而它讀起來完全正常）。
        // 數值影響：只動 status / status_updated 兩欄，其餘欄位原樣寫回（走 store 的 MergeOntoRaw）。
        // ===========================================================
        static void DoStatus(Dictionary<string, string> args, string iPersona, StringBuilder ioR)
        {
            string aStatus = GetArg(args, "status", "").Trim();
            if (string.IsNullOrEmpty(aStatus))
            {
                throw new Exception("[Coding] step=status 需要 --arg status=<一句話在改什麼>");
            }

            UCL_CodingSession aSession = LoadRunning(iPersona);
            if (aSession == null)
            {
                throw new Exception($"[Coding] {iPersona} 現在沒有進行中的 Coding 場 —— "
                                    + $"先進場：senate ucmd run Coding --persona {iPersona} --arg step=start --arg status=<一句話>");
            }

            aSession.status = aStatus;
            aSession.status_updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:sszzz");
            SCP.Core.Session.SCP_ActivitySessionStore.Save(
                UCL_AgentCommandsPath.ScpDataRoot, iPersona, aSession,
                SCP.Core.Session.SCP_ActivitySessionKind.Coding);
            Awakening.UCL_AwakeningService.UpdateNowStatus(iPersona, "🛠 Coding：" + aStatus);

            ioR.AppendLine("## ✅ 狀態已更新");
            ioR.AppendLine($"- 在改什麼: **{aStatus}**");
            ioR.AppendLine($"- session_id: `{aSession.session_id}`");
            ioR.AppendLine();
            ioR.AppendLine(kScopeCaveat);
        }

        // ===========================================================
        // 區塊職責：退出閘 —— 編譯沒過就不放行。
        // 物理意義：獨佔的意義在於「交回去的時候是乾淨的」。放行一個紅燈狀態等於把紅留給下一個人，
        //          而他會先懷疑自己（那正是本單動機血證的形狀）。
        // 數值影響：讀 .compile_status.json（tracker 寫的），不寫它。放行才翻 session 三欄。
        //
        // ⚠ 本閘只有 **tracker** 這一欄。ErrorLog 那一欄的實作**只活在 check_compile.py**，
        //   本閘沒有它 ⇒ 「只跑到 Editor ErrorLog 的錯」會通過本閘
        //   （2026-08-14 實測過同一時刻 tracker 說 0、ErrorLog 有 CS0117）。
        //   ⛔ 不在這裡重寫一份 ErrorLog 解析 —— 那會變成第二把尺，而兩把尺不一致時沒有人會發現。
        //   ⇒ 缺的那一欄**顯式印出來**，讓它會出聲，並列為 A1 的已知缺口交 QA 判。
        // ===========================================================
        static void DoExit(Dictionary<string, string> args, string iPersona, StringBuilder ioR, string iPayload)
        {
            UCL_CodingSession aSession = LoadRunning(iPersona);
            if (aSession == null)
            {
                throw new Exception($"[Coding] {iPersona} 現在沒有進行中的 Coding 場（沒有東西可以退出）");
            }

            bool aForce = string.Equals(GetArg(args, "force", "0").Trim(), "1", StringComparison.Ordinal);
            string aReason = GetArg(args, "reason", "").Trim();

            string aGateWhy;
            bool aGreen = CheckTrackerGate(aSession, out aGateWhy);

            ioR.AppendLine("## 編譯閘（兩欄分開報 —— 不壓成一句「通過」）");
            ioR.AppendLine();
            ioR.AppendLine("| 欄 | 尺 | 結果 |");
            ioR.AppendLine("|---|---|---|");
            ioR.AppendLine($"| tracker | `.compile_status.json` | {(aGreen ? "🟢 綠" : "🔴 紅")}　{aGateWhy} |");
            ioR.AppendLine("| ErrorLog | `check_compile.py` 的第二來源 | ⚪ **本閘未量** —— 見下 |");
            ioR.AppendLine();
            ioR.AppendLine("> ⚠ ErrorLog 那一欄的實作只活在 `check_compile.py`，本閘沒有它。");
            ioR.AppendLine("> ⇒ **只跑到 Editor ErrorLog 的錯會通過本閘**（2026-08-14 實測：同一時刻 tracker 說 0、ErrorLog 有錯）。");
            ioR.AppendLine("> 要那一欄請自己跑：`python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only`");
            ioR.AppendLine();

            if (!aGreen && !aForce)
            {
                ioR.AppendLine("## ⛔ 擋下 —— 沒有放行");
                ioR.AppendLine();
                ioR.AppendLine($"- 原因：{aGateWhy}");
                ioR.AppendLine($"- 這一場在改：**{aSession.status}**（{aSession.status_updated}）");
                ioR.AppendLine();
                ioR.AppendLine("### 兩個出口");
                ioR.AppendLine();
                ioR.AppendLine("**① 修完再退**（建議）");
                ioR.AppendLine("```bash");
                ioR.AppendLine("python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only");
                ioR.AppendLine("```");
                ioR.AppendLine("修完後重跑：");
                ioR.AppendLine("```bash");
                ioR.AppendLine($"senate ucmd run Coding --persona {iPersona} --arg step=end");
                ioR.AppendLine("```");
                ioR.AppendLine();
                ioR.AppendLine("**② 顯式 force**（會留名在本場，理由必填）");
                ioR.AppendLine("```bash");
                ioR.AppendLine($"senate ucmd run Coding --persona {iPersona} --arg step=end --arg force=1 --arg reason=<為什麼>");
                ioR.AppendLine("```");
                UCL_AgentCommandRunner.ReportOutputValue(args, "exited", "0");
                throw new Exception("[Coding] 退出被編譯閘擋下 —— 出口見回傳檔：" + iPayload);
            }

            if (!aGreen && aForce && string.IsNullOrEmpty(aReason))
            {
                // force 沒有理由就是一個不留痕跡的繞道 —— 那比不設閘更糟（它看起來有閘）。
                throw new Exception("[Coding] force=1 需要同時給 --arg reason=<為什麼要跳過編譯閘>");
            }

            string aEndReason = aGreen ? "step=end（編譯 tracker 綠）"
                                       : "step=end（force：" + aReason + "）";
            if (!aGreen) aSession.force_reason = aReason;

            SCP.Core.Session.SCP_ActivitySessionStore.Close(
                UCL_AgentCommandsPath.ScpDataRoot, iPersona, aSession, aEndReason);
            Awakening.UCL_AwakeningService.UpdateNowStatus(iPersona, "");

            UCL_AgentCommandRunner.ReportOutputValue(args, "exited", "1");
            UCL_AgentCommandRunner.ReportOutputValue(args, "forced", aGreen ? "0" : "1");

            ioR.AppendLine(aGreen ? "## ✅ 已退出（閘通過）" : "## ⚠ 已退出（**force** —— 編譯閘沒過）");
            ioR.AppendLine($"- session_id: `{aSession.session_id}`");
            ioR.AppendLine($"- end_reason: {aEndReason}");
            if (!aGreen) ioR.AppendLine($"- 🩸 force 理由留在本場的 `force_reason` 欄：{aReason}");
            ioR.AppendLine("- lock now_status 已清空");
            ioR.AppendLine();
            ioR.AppendLine(kScopeCaveat);
        }

        // ===========================================================
        // 區塊職責：tracker 那一欄的判定 —— 三個條件，任何一個不成立就是紅。
        // 物理意義：③ 那一格（時戳要晚於開場）是這裡最容易漏掉的一條：
        //          `.compile_status.json` 是**上一趟 compile 的結果**，不是「專案現在的狀態」。
        //          🩸 沒有 ③ 的話，改完 code 不 recompile 就 exit 會拿到一份**開場前**的綠燈放行 ——
        //          而那份綠燈完全真實、格式正確、數字合理。
        // 數值影響：純讀檔判斷。
        // ===========================================================
        static bool CheckTrackerGate(UCL_CodingSession iSession, out string oWhy)
        {
            string aPath = UCL_CompileErrorTracker.GetOutputPath();
            if (!File.Exists(aPath))
            {
                oWhy = $"找不到 `{aPath}` —— tracker 從沒跑過（先觸發一次 compile）";
                return false;
            }

            JsonData aJson;
            try { aJson = JsonData.ParseJson(File.ReadAllText(aPath)); }
            catch (Exception e) { oWhy = "讀不動 `.compile_status.json`：" + e.Message; return false; }
            if (aJson == null) { oWhy = "`.compile_status.json` parse 回 null"; return false; }

            if (aJson.GetBool("in_progress", false))
            {
                oWhy = "compile 還在跑（`in_progress=true`）—— 結果尚未定案";
                return false;
            }

            // ③ 這份讀數是不是**本場開場之後**量的。
            string aTs = aJson.GetString("timestamp", "");
            DateTime aStamp;
            if (!DateTime.TryParse(aTs, out aStamp))
            {
                oWhy = $"tracker 的 `timestamp` 解析不出來（'{aTs}'）—— 無法判斷它是不是本場之後量的";
                return false;
            }
            DateTime? aStart = SCP.Core.Session.SCP_ActivitySession.ParseIsoToLocal(iSession.start_ts);
            if (aStart.HasValue && aStamp < aStart.Value)
            {
                oWhy = $"tracker 的讀數是 **開場前** 量的（tracker {aStamp:yyyy-MM-dd HH:mm:ss} < 開場 {aStart.Value:yyyy-MM-dd HH:mm:ss}）"
                       + " —— 它沒有涵蓋本場改的東西，先重新 compile";
                return false;
            }

            int aErrors = aJson.GetInt("total_errors", -1);
            if (aErrors < 0) { oWhy = "讀不到 `total_errors`"; return false; }
            if (aErrors > 0)
            {
                oWhy = $"**{aErrors}** 個編譯錯誤（tracker {aStamp:HH:mm:ss}）";
                return false;
            }

            // ⚠ 射程要寫在讀數裡，不是只寫在註解裡：本閘比的是「tracker ≥ **開場時刻**」，
            //   而 `check_compile.py` 比的是「tracker ≥ **最後一次改檔的 mtime**」——**它比本閘嚴**。
            //   🩸 差別會咬人的情況：開場 → 改檔 → **不 recompile** → 退場。
            //      那時 tracker 仍晚於開場（因為開場前剛編過）⇒ **本閘放行，而那份綠燈不涵蓋你剛改的東西。**
            //   ⛔ 不在這裡重做一份「掃工作區 mtime」—— 那是第二把尺（同 ErrorLog 那欄的理由）。
            //   ⇒ 把差異印出來，讓它會出聲。
            oWhy = $"0 errors，讀數量於 {aStamp:yyyy-MM-dd HH:mm:ss}（**晚於開場**；"
                   + "⚠ 本閘只比到開場時刻，開場後改了檔又沒 recompile 的話它看不到 —— "
                   + "要比到「最後一次改檔」請跑 check_compile.py，它會印 STALE）";
            return true;
        }

        /// <summary>讀本人**進行中**的 Coding 場；沒有回 null（已收工的不算）。</summary>
        static UCL_CodingSession LoadRunning(string iPersona)
        {
            var aSession = SCP.Core.Session.SCP_ActivitySessionStore.Load<UCL_CodingSession>(
                UCL_AgentCommandsPath.ScpDataRoot, iPersona,
                SCP.Core.Session.SCP_ActivitySessionKind.Coding);
            if (aSession == null) return null;
            return aSession.IsRunningAt(DateTime.Now, out _) ? aSession : null;
        }

    }
}
#endif
