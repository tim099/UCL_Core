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
        // 物理意義：軸1（每人一場）與軸2（全域至多一人）都收在 SCP_ActivitySessionStore.TryStart，
        //          本函式**不自己判存在**（自己判＝第三份判準，而它會跟前兩份不一致且不報錯）。
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
                until_local = "（無預定時長，顯式 step=end 才收工）",
                active = true,
                status = aStatus,
                status_updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:sszzz"),
            };

            bool aOk = SCP.Core.Session.SCP_ActivitySessionStore.TryStart(
                UCL_AgentCommandsPath.ScpDataRoot, iPersona, aSession,
                SCP.Core.Session.SCP_ActivitySessionKind.Coding, DateTime.Now,
                out SCP.Core.Session.SCP_ActivitySession aBlockedBy);

            if (!aOk)
            {
                AppendBlocked(iPersona, aBlockedBy, ioR);
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

            oWhy = $"0 errors，讀數量於 {aStamp:yyyy-MM-dd HH:mm:ss}（晚於開場）";
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

        // ===========================================================
        // 區塊職責：被擋下時的回報 —— D-1 措辭原則：祈使句、指令直接附上、不解釋代價。
        // 物理意義：⚠ 擋下的原因有**兩種**且處理方式相反：
        //          (a) 我自己在別的場 ⇒ 去關自己那一場；
        //          (b) 別人正在 Coding ⇒ 等他，或去敲他。
        //          把兩者印成同一段話會讓人對著自己的場等別人。
        // 數值影響：純輸出。
        // ===========================================================
        static void AppendBlocked(string iPersona, SCP.Core.Session.SCP_ActivitySession iBlockedBy, StringBuilder ioR)
        {
            ioR.AppendLine("## ⛔ 進場被擋 —— 沒有開場");
            ioR.AppendLine();
            if (iBlockedBy == null)
            {
                // 走到這裡代表 TryStart 回 false 但沒說是誰擋的 ⇒ 寫檔失敗（磁碟／穿越型 persona）。
                ioR.AppendLine("- 原因：**寫入失敗**（不是被別人擋）—— 檢查 persona 名與 sessions 目錄權限。");
                return;
            }

            bool aSelf = string.Equals(iBlockedBy.persona, iPersona, StringComparison.Ordinal);
            if (aSelf)
            {
                ioR.AppendLine($"- 原因：**你自己**正在另一種場：`{iBlockedBy.kind}`（session_id `{iBlockedBy.session_id}`）");
                ioR.AppendLine();
                ioR.AppendLine("先把那一場收掉：");
                ioR.AppendLine("```bash");
                ioR.AppendLine($"senate cmd sessions --arg data_root=<資料根> --arg op=close --arg persona={iPersona}");
                ioR.AppendLine("```");
                return;
            }

            string aWhat = "";
            if (iBlockedBy.Raw != null) aWhat = iBlockedBy.Raw.GetString("status", "");
            if (string.IsNullOrEmpty(aWhat)) aWhat = "（那一場沒有寫 status）";

            ioR.AppendLine($"- 原因：**@{iBlockedBy.persona} 正在 Coding**（全域同時至多一人）");
            ioR.AppendLine($"- 他在改：**{aWhat}**");
            ioR.AppendLine($"- 從：{iBlockedBy.start_ts}　session_id `{iBlockedBy.session_id}`");
            ioR.AppendLine();
            ioR.AppendLine("### 三個出口");
            ioR.AppendLine();
            ioR.AppendLine("**① 等他退出**，然後重跑進場。查他還在不在：");
            ioR.AppendLine("```bash");
            ioR.AppendLine($"senate ucmd run SessionStatus --persona {iPersona} --arg persona={iBlockedBy.persona}");
            ioR.AppendLine("```");
            // ⚠ `senate cmd sessions --arg op=list` 也看得到這一場 —— 它走 LoadAll（不過濾 kind），
            //   2026-09-05 實測印：「summit Coding（未登記 —— 本層不當它是現行 session）🟢 進行中」，
            //   `running` 也算進去了。⇒ 那條路可以用來「看有沒有人在」。
            //   🩸 我第一版在這裡寫「它掃不到 Coding 場」——**那是錯的**，成因是我拿一份
            //      跑在開場前 20 秒的 list 當證據，把「當時那場還不存在」讀成「它掃不到那種場」。
            //   ⇒ A2 之前真正缺的不是**看得見**，是 `IsRegistered` 為 false：
            //      凡是以「已登記 kind」為條件的判斷（如 FindRunning）都不會把它算進去
            //      ⇒ 從 Senate 那側開場，不會被這一場擋下。
            ioR.AppendLine("　 （`senate cmd sessions --arg op=list` 也看得到這一場，會標「未登記」——");
            ioR.AppendLine("　 那是 A2 未做的正常標記，不是這一場有問題。）");
            ioR.AppendLine();
            ioR.AppendLine($"**② 去酒館敲 @{iBlockedBy.persona}**：");
            ioR.AppendLine("```bash");
            ioR.AppendLine($"senate ucmd run Tavern --persona {iPersona} --arg op=post --arg room=tavern \\");
            ioR.AppendLine($"    --arg body=\"@{iBlockedBy.persona} Coding 場借過一下，我要改 <什麼>\"");
            ioR.AppendLine("```");
            ioR.AppendLine();
            ioR.AppendLine($"**③ 持有者自己的退出指令**（⚠ 由 @{iBlockedBy.persona} 跑，不是你跑）：");
            ioR.AppendLine("```bash");
            ioR.AppendLine($"senate ucmd run Coding --persona {iBlockedBy.persona} --arg step=end");
            ioR.AppendLine("```");
            ioR.AppendLine();
            ioR.AppendLine($"⚠ 若 @{iBlockedBy.persona} 已經不在（場殘留）：");
            ioR.AppendLine("```bash");
            ioR.AppendLine($"senate cmd sessions --arg data_root=<資料根> --arg op=close --arg persona={iBlockedBy.persona}");
            ioR.AppendLine("```");
            ioR.AppendLine("　 ⚠ 這條走 Senate 那份，而 A2 未做 ⇒ 它把本 kind 標成「未登記」。");
            ioR.AppendLine("　 關完之後用上面的 `SessionStatus` 覆驗一次（**兩條路各問一次**，別只信關場那句成功）。");
            ioR.AppendLine("⛔ 不要為了繞過而改用別的 persona 名進場 —— 那是製造分身，不是解法。");
        }
    }
}
#endif
