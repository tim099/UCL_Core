// 區塊職責：酒館 CLI —— 把酒館裡「cmd …」的一句話變成 Editor 端的動作，並由酒保回覆結果。
// 物理意義：這是第四種酒保發言來源（前三種：keyword trigger / time rule / @點名），
//          而它是唯一一種**會改變 Editor 狀態、甚至動別人鍵盤**的。所以骨架是三道關卡：
//            ① 總開關 ② 白名單（精確比對）③ 需要時的二次確認
// 數值影響：指令本身的效果各自寫在 handler 裡；本檔只負責解析、授權、確認、回覆。
//
// 設計取捨：
//   · **比對一律轉小寫**（Tim 2026-08-19 指定），但**訊息內容必須用原文** ——
//     群發的 body 若走小寫化後的 token，英文訊息會被壓成全小寫而**沒有任何錯誤訊息**。
//     ⇒ UCL_BartenderCliContext 同時帶「小寫 token」與「原始整行」，比對用前者、內容用後者。
//   · **二次確認只守「拆掉護欄／對外送出」的方向。** `remote-window off` 不問，
//     `on permanent` 與 `msg`（會打進別人的視窗）要問。反過來設計會訓練人無腦按 Y。
//   · **確認回覆不以 `cmd` 開頭**（使用者只會打 `y`）⇒ 判斷必須帶發話者與落磁碟的 pending。
//   · **第二筆需確認的指令會被擋下，不覆蓋前一筆** —— 覆蓋的話那句 Y 會落在別的指令上。
//   · **群發目標在「執行時」才解析，不在確認時** —— 確認到執行之間有人上線／下線，
//     用確認當下的名單會送給已經不在的人、漏掉剛上線的人。確認訊息會把這件事講明。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;
using Cysharp.Threading.Tasks;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;

namespace UCL.Core.EditorLib.AgentCommands.Bartender
{
    public static class UCL_BartenderCliService
    {
        const string ArgPermanent = "permanent";
        const string TargetAll = "all";

        // ⚠ 指令表是**唯一真相** —— help 由它生成，不另外維護一份清單。
        //   兩份清單必漂，而漂掉的樣子是「help 列了一個不存在的指令」。
        //   2026-08-20 起指令表從 hardcode 改為設定檔（bartender/cli_commands/<id>.json，
        //   Tim 拍板）—— 每次用時重讀（CLI 訊息很稀疏，磁碟成本可忽略；
        //   快取反而要處理設定頁存檔後的失效問題）。
        static List<UCL_BartenderCliCommandConfig> LoadCommands()
            => UCL_BartenderCliCommandStore.LoadAll();

        /// <summary>指令這次呼叫要不要二次確認 —— 任一啟用中的行為要求即要。</summary>
        static bool CommandNeedsConfirm(UCL_BartenderCliCommandConfig iConfig, UCL_BartenderCliContext iCtx)
        {
            if (iConfig?.actions == null) return false;
            for (int i = 0; i < iConfig.actions.Count; i++)
            {
                var aAction = iConfig.actions[i];
                if (aAction == null || !aAction.IsEnable) continue;
                try { if (aAction.NeedsConfirm(iCtx)) return true; }
                catch (Exception e)
                {
                    // 判斷不了就當「要確認」—— fail-closed：多問一次的代價遠小於漏問一次
                    UnityEngine.Debug.LogWarning($"[BartenderCli] NeedsConfirm 例外（視同需要確認）：{e.Message}");
                    return true;
                }
            }
            return false;
        }

        /// <summary>確認訊息的「會發生什麼」—— 串接每個啟用中行為的 summary。</summary>
        static string CommandSummary(UCL_BartenderCliCommandConfig iConfig, UCL_BartenderCliContext iCtx)
        {
            if (iConfig?.actions == null) return "";
            var aParts = new List<string>();
            for (int i = 0; i < iConfig.actions.Count; i++)
            {
                var aAction = iConfig.actions[i];
                if (aAction == null || !aAction.IsEnable) continue;
                try
                {
                    string aPart = aAction.ConfirmSummary(iCtx);
                    if (!string.IsNullOrWhiteSpace(aPart)) aParts.Add(aPart);
                }
                catch (Exception e) { aParts.Add($"⚠ summary 產生失敗：{e.Message}"); }
            }
            return string.Join("\n\n", aParts);
        }

        /// <summary>依序執行指令的全部啟用中行為；單一行為失敗不擋後面的，但一定出現在回覆裡。</summary>
        static string RunCommand(UCL_BartenderCliCommandConfig iConfig, UCL_BartenderCliContext iCtx)
        {
            if (iConfig?.actions == null || iConfig.actions.Count == 0)
                return $"⚠ 指令 `{iConfig?.id}` 沒有任何行為 —— 去設定頁（酒保管理 → 酒館 CLI → 指令設定）加一個。";
            var aParts = new List<string>();
            int aRan = 0;
            for (int i = 0; i < iConfig.actions.Count; i++)
            {
                var aAction = iConfig.actions[i];
                if (aAction == null || !aAction.IsEnable) continue;
                aRan++;
                try
                {
                    string aResult = aAction.Execute(iCtx);
                    if (!string.IsNullOrWhiteSpace(aResult)) aParts.Add(aResult);
                }
                catch (Exception e)
                {
                    // 失敗一定要出聲並帶原因 —— 沉默會讓人重打，而重打不會改變結果
                    UnityEngine.Debug.LogWarning($"[BartenderCli] `{iConfig.id}` 行為 {aAction.GetType().Name} 執行失敗：{e}");
                    aParts.Add($"❌ 行為 `{aAction.GetType().Name}` 執行失敗：{e.Message}");
                }
            }
            if (aRan == 0) return $"⚠ 指令 `{iConfig.id}` 的行為全被停用 —— 什麼都沒做。";
            return string.Join("\n\n", aParts);
        }

        // ===========================================================
        // 入口
        // ===========================================================
        public static bool IsCliMessage(UCL_ChatMessage iMsg, out UCL_BartenderCliSettings oSettings)
        {
            oSettings = null;
            if (iMsg == null) return false;
            string aBody = (iMsg.body ?? "").Trim();
            if (aBody.Length == 0) return false;

            var aSettings = UCL_BartenderCliIO.Load();
            if (!aSettings.enabled) return false;

            if (BodyHasCliPrefix(aBody, aSettings))
            {
                oSettings = aSettings;
                return true;
            }
            if (ParseYesNo(aBody).HasValue)
            {
                // 只有「這個人真的有 pending」才算確認回覆 —— 否則酒館裡任何一句 "y" 都會被吃掉
                var aState = UCL_BartenderCliIO.LoadState();
                if (FindPending(aState, iMsg) != null) { oSettings = aSettings; return true; }
            }
            return false;
        }

        /// <summary>
        /// 這段文字是不是一句 CLI 指令（prefix 命中且 CLI 總開關開著）。
        /// 給**寫入層**用（Cmd_Tavern post / Discord inbound）：判定為指令的訊息會被打上
        /// `tag=cli-cmd`，讓 glossary auto-attach 等後續流程分流跳過 ——
        /// 攔在寫入端而不是在讀取端剝除，附掛就不會發生而不是發生後再擦
        /// （2026-08-19 詞典污染血證：附掛區塊變成指令的一部分，群發把整本詞典打進別人輸入框）。
        /// ⚠ 只看 prefix，不驗白名單 —— 白名單是「能不能執行」，這裡問的是「這句話是不是指令」。
        /// </summary>
        public static bool LooksLikeCliCommand(string iBody)
        {
            string aBody = (iBody ?? "").Trim();
            if (aBody.Length == 0) return false;
            var aSettings = UCL_BartenderCliIO.Load();
            if (!aSettings.enabled) return false;
            return BodyHasCliPrefix(aBody, aSettings);
        }

        static bool BodyHasCliPrefix(string iTrimmedBody, UCL_BartenderCliSettings iSettings)
        {
            int aSpace = iTrimmedBody.IndexOfAny(s_Sep);
            string aFirst = aSpace < 0 ? iTrimmedBody : iTrimmedBody.Substring(0, aSpace);
            return string.Equals(aFirst, iSettings.prefix ?? "cmd", StringComparison.OrdinalIgnoreCase);
        }

        public static void Handle(UCL_ChatMessage iMsg, string iRoomId, UCL_BartenderCliSettings iSettings)
        {
            var aSettings = iSettings ?? UCL_BartenderCliIO.Load();
            // 🩸 一定要先剝掉自動附掛區塊（Cmd_Glossary 的「本回提到的新詞」等）。
            //   實測 2026-08-19：`cmd msg kiara <訊息>` 因為提到 persona 名，發文後被附上整段新詞區塊，
            //   而那段變成了指令的一部分 ⇒ 群發會把**整本詞典打進對方輸入框並按 Enter**。
            //   剝除走既有的那一支（inline 註冊早就踩過同一隻），不在這裡另造一套 marker 清單。
            string aRaw = UCL_BartenderInlineParser.StripAutoAttachedBlocks((iMsg.body ?? "").Trim());
            string aWho = DescribeSender(iMsg);

            // ── ① 確認回覆優先（"y" 不以 prefix 開頭）──
            bool? aAnswer = ParseYesNo(aRaw);
            var aState = UCL_BartenderCliIO.LoadState();
            var aPending = FindPending(aState, iMsg);
            if (aAnswer.HasValue && aPending != null)
            {
                aState.pending.Remove(aPending);
                UCL_BartenderCliIO.SaveState(aState);

                if (aPending.IsExpired(aSettings.confirm_timeout_seconds))
                {
                    Post($"⌛ {aWho} 的確認已逾期（超過 {aSettings.confirm_timeout_seconds} 秒），"
                       + $"沒有執行 `{aPending.command_line}`。要的話重打一次。", iMsg, iRoomId, "cli-expired");
                    return;
                }
                if (!aAnswer.Value)
                {
                    Post($"🚫 已取消：`{aPending.command_line}`（{aWho} 回答否）。什麼都沒動。",
                        iMsg, iRoomId, "cli-cancelled");
                    return;
                }
                // 用**當初那一行**重跑，不用「y」這一行
                // ⚠ 上下文用**當初那一行**重建，不是用「y」那一行 ——
                //   否則 msg 的訊息內容會變成空的（而它會「成功」地送出一句空訊息）。
                var aCtx = BuildContext(aPending.command_line, aSettings, iMsg, iRoomId);
                var aCmd = FindByLine(aPending.command_line, aSettings);
                if (aCmd == null)
                {
                    Post($"⚠ 確認通過但指令已不存在：`{aPending.command_line}`。什麼都沒動。",
                        iMsg, iRoomId, "cli-error");
                    return;
                }
                string aResult = RunCommand(aCmd, aCtx);
                Post($"✅ 已確認並執行：`{aPending.command_line}`\n\n{aResult}", iMsg, iRoomId, "cli-done");
                return;
            }

            // ── ② 一般指令 ──
            var aContext = BuildContext(aRaw, aSettings, iMsg, iRoomId);
            string aName = NameOf(aRaw, aSettings);
            if (string.IsNullOrEmpty(aName))
            {
                Post($"🔧 用法：`{aSettings.prefix} <指令>`。\n\n{BuildHelp()}", iMsg, iRoomId, "cli-help");
                return;
            }

            // 白名單 —— 精確比對，空清單擋光
            if (!aSettings.IsAuthorized(iMsg.sender_id, iMsg.sender_name, iMsg.sender_persona))
            {
                Post($"⛔ {aWho} 不在 CLI 白名單裡，沒有執行。\n"
                   + $"（白名單目前 {(aSettings.users?.Count ?? 0)} 人；"
                   + "要加人走 酒保管理頁 → 🔧 酒館 CLI，或改 `bartender/cli_settings.json`。"
                   + "⚠ 空清單是全部擋光，不是全部放行。）", iMsg, iRoomId, "cli-denied");
                return;
            }

            var aCommand = Find(aName);
            if (aCommand == null)
            {
                Post($"❓ 沒有這個指令：`{aName}`\n\n{BuildHelp()}", iMsg, iRoomId, "cli-unknown");
                return;
            }

            if (CommandNeedsConfirm(aCommand, aContext))
            {
                var aExisting = FindPendingByKey(aState, RequesterKey(iMsg));
                if (aExisting != null && !aExisting.IsExpired(aSettings.confirm_timeout_seconds))
                {
                    Post($"⏳ {aWho} 還有一筆待確認：`{aExisting.command_line}`。\n"
                       + "先回 **Y／N** 處理掉它，再下新指令（我不覆蓋前一筆 —— "
                       + "覆蓋的話你那句 Y 會落在你以為的另一個指令上）。", iMsg, iRoomId, "cli-busy");
                    return;
                }
                if (aExisting != null) aState.pending.Remove(aExisting);

                string aSummary = CommandSummary(aCommand, aContext);
                aState.pending.Add(new UCL_BartenderCliPending
                {
                    command_line = aRaw,
                    requester_key = RequesterKey(iMsg),
                    requester_id = iMsg.sender_id ?? "",
                    room_id = iRoomId,
                    source_seq = iMsg.seq,
                    created_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    summary = aSummary,
                });
                UCL_BartenderCliIO.SaveState(aState);

                Post($"⚠ 這個指令需要二次確認。\n\n"
                   + $"**指令**：`{aRaw}`\n"
                   + $"**會發生什麼**：{(string.IsNullOrEmpty(aSummary) ? "（未提供說明）" : aSummary)}\n\n"
                   + $"{aWho} 請回 **Y**（執行）或 **N**（取消）。"
                   + $"{aSettings.confirm_timeout_seconds} 秒內沒回就作廢。", iMsg, iRoomId, "cli-confirm");
                return;
            }

            Post(RunCommand(aCommand, aContext), iMsg, iRoomId, "cli-done");
        }

        // ===========================================================
        // 指令實作（行為的實體 —— 由 UCL_BartenderCliCommandConfig.cs 的 CliAction_* 包裝呼叫）
        // ===========================================================
        public static string RunRemoteWindow(string[] iArgs)
        {
            if (iArgs == null || iArgs.Length == 0)
            {
                return "⚠ `remote-window` 要帶 `on` 或 `off`。\n"
                     + "　`cmd remote-window on`（只開本次 session）\n"
                     + "　`cmd remote-window on permanent`（連永久開關一起開，需二次確認）\n"
                     + "　`cmd remote-window off`（同時關掉本次與永久）";
            }
            bool aOn = IsOnArgs(iArgs);
            bool aOff = iArgs[0] == "off" || iArgs[0] == "0" || iArgs[0] == "false";
            if (!aOn && !aOff) return $"⚠ 認不出 `{iArgs[0]}`；要 `on` 或 `off`。";

            if (aOn)
            {
                bool aPermanent = HasPermanentArg(iArgs);
                UCL_RemoteWindowControl.SetEnabled(true);
                if (aPermanent) UCL_RemoteWindowControl.PersistEnabled = true;
                // 回讀而不是回報我寫了什麼 —— 「我設定了」與「它現在是這樣」是兩件事
                return "▶ 遠端視窗協作 **已開啟**\n"
                     + $"- 本次 session：`{UCL_RemoteWindowControl.Enabled}`（回讀值）\n"
                     + $"- 永久開關：`{UCL_RemoteWindowControl.PersistEnabled}`（回讀值）"
                     + (aPermanent ? " ← 已一併開啟，重編／重啟會自動恢復"
                                   : " ← 沒動；domain reload 後本次開關會被重置（那是刻意的護欄）")
                     + "\n⚠ 其他護欄照舊未動：偵測使用者操作後暫停、閒置秒數、送出前前景驗證。";
            }

            UCL_RemoteWindowControl.SetEnabled(false);
            UCL_RemoteWindowControl.PersistEnabled = false;
            return "⏹ 遠端視窗協作 **已關閉**（本次 ＋ 永久一起關）\n"
                 + $"- 本次 session：`{UCL_RemoteWindowControl.Enabled}`（回讀值）\n"
                 + $"- 永久開關：`{UCL_RemoteWindowControl.PersistEnabled}`（回讀值）";
        }

        // 區塊職責：確認訊息裡「會發生什麼」—— 對群發來說，**訊息原文必須完整回顯**。
        // 物理意義：這個確認要擋的不只是「要不要送」，還有「送出去的是不是我想打的那句」。
        //          只寫「將群發給 3 人」的話，錯字與被小寫化的內容都看不出來。
        public static string BuildMsgSummary(UCL_BartenderCliContext iCtx)
        {
            string aTarget = iCtx.Args.Length > 0 ? iCtx.Args[0] : "";
            string aBody = iCtx.RawAfterArgs ?? "";
            if (string.IsNullOrEmpty(aTarget)) return "⚠ 沒有指定對象（要 `<persona>` 或 `all`）";
            if (string.IsNullOrWhiteSpace(aBody)) return "⚠ 沒有訊息內容";

            var aOnline = SafeListOnline();
            var aNames = new List<string>();
            for (int i = 0; i < aOnline.Count; i++) aNames.Add(aOnline[i].Persona);

            string aWhoLine = aTarget == TargetAll
                // ⚠ 不要在這裡加粗：外層那句已經是粗體，嵌套的 ** 會讓 markdown 在畫面上炸成星號
                ? $"所有在線 persona（此刻 {aOnline.Count} 人：{(aNames.Count > 0 ? string.Join("、", aNames) : "（沒人在線）")}）"
                : $"`{aTarget}`";

            return $"透過自動通知的遠端輸入，把下面這段訊息**打進 {aWhoLine} 的輸入框並按 Enter**：\n"
                 + $"```\n{aBody}\n```\n"
                 + "⚠ 收件名單在**執行時**才重新解析（確認到執行之間有人上下線的話，"
                 + "送的是執行那一刻的在線名單，不是現在這份）。\n"
                 + $"⚠ 遠端視窗協作目前：`{UCL_RemoteWindowControl.Enabled}`"
                 + (UCL_RemoteWindowControl.Enabled ? "" : " ← **關著，這樣執行會直接被拒**（先 `cmd remote-window on`）");
        }

        public static string RunMsg(UCL_BartenderCliContext iCtx)
        {
            string aTarget = iCtx.Args.Length > 0 ? iCtx.Args[0] : "";
            string aBody = (iCtx.RawAfterArgs ?? "").Trim();
            if (string.IsNullOrEmpty(aTarget)) return "⚠ 用法：`cmd msg <persona|all> <訊息>`";
            if (string.IsNullOrWhiteSpace(aBody)) return "⚠ 沒有訊息內容，什麼都沒送。";
            if (!UCL_RemoteWindowControl.Enabled)
            {
                return "⛔ 遠端視窗協作沒開 —— 沒送任何人。\n"
                     + "先 `cmd remote-window on`（這是動別人鍵盤的總閘，我不繞過它）。";
            }

            var aOnline = SafeListOnline();
            var aTargets = new List<UCL_PersonaLockInfo>();
            if (aTarget == TargetAll)
            {
                aTargets.AddRange(aOnline);
            }
            else
            {
                for (int i = 0; i < aOnline.Count; i++)
                {
                    if (string.Equals(aOnline[i].Persona, aTarget, StringComparison.OrdinalIgnoreCase))
                        aTargets.Add(aOnline[i]);
                }
                if (aTargets.Count == 0)
                {
                    var aNames = new List<string>();
                    for (int i = 0; i < aOnline.Count; i++) aNames.Add(aOnline[i].Persona);
                    // 「不在線」與「打錯名字」要分得出來 ⇒ 把在線名單一起印出來
                    return $"⚠ `{aTarget}` 不在在線名單裡，沒送。\n"
                         + $"此刻在線 {aOnline.Count} 人：{(aNames.Count > 0 ? string.Join("、", aNames) : "（沒人）")}";
                }
            }
            if (aTargets.Count == 0) return "⚠ 沒有任何在線 persona，什麼都沒送。";

            // 實體輸入是 async 且每個目標要好幾秒 —— 不能在 daemon tick 裡等。
            // 先回一則「開始了」，逐一送完再回一則結果報告（兩則都留在酒館，事後查得到）。
            BroadcastAsync(aTargets, aBody, iCtx).Forget();
            var aList = new List<string>();
            for (int i = 0; i < aTargets.Count; i++) aList.Add(aTargets[i].Persona);
            return $"📤 開始群發給 {aTargets.Count} 人：{string.Join("、", aList)}\n"
                 + "（實體輸入要逐一定位視窗，送完我再回一則逐人結果）";
        }

        static async UniTaskVoid BroadcastAsync(List<UCL_PersonaLockInfo> iTargets, string iBody, UCL_BartenderCliContext iCtx)
        {
            var aLines = new List<string>();
            int aOk = 0;
            for (int i = 0; i < iTargets.Count; i++)
            {
                try
                {
                    var aRes = await UCL_RemoteNotifyService.DeliverTextTo(iTargets[i], iBody);
                    if (aRes.ok) aOk++;
                    aLines.Add((aRes.ok ? "✅ " : "❌ ") + aRes.detail);
                }
                catch (Exception e)
                {
                    // 一個人失敗不影響其他人，但**一定要出現在報告裡**
                    aLines.Add($"❌ {iTargets[i].Persona}：例外 {e.Message}");
                }
            }
            Post($"📬 群發結果：**{aOk}/{iTargets.Count} 成功**\n"
               + $"訊息：\n```\n{iBody}\n```\n"
               + string.Join("\n", aLines), iCtx.Src, iCtx.RoomId, "cli-broadcast");
        }

        static List<UCL_PersonaLockInfo> SafeListOnline()
        {
            try
            {
                var aList = UCL_ActivePersonaLocks.ListOnline();
                return aList ?? new List<UCL_PersonaLockInfo>();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[BartenderCli] 讀在線名單失敗：{e.Message}");
                return new List<UCL_PersonaLockInfo>();
            }
        }

        // ===========================================================
        // 內部
        // ===========================================================
        public static bool IsOnArgs(string[] iArgs)
            => iArgs != null && iArgs.Length > 0
               && (iArgs[0] == "on" || iArgs[0] == "1" || iArgs[0] == "true" || iArgs[0] == "enable");

        public static bool HasPermanentArg(string[] iArgs)
        {
            if (iArgs == null) return false;
            for (int i = 0; i < iArgs.Length; i++)
            {
                // 同時接受 permanent / --permanent / perm / 永久 —— 打錯一個字母就靜默降級成
                // 「只開本次」是最難查的那種：使用者以為開了永久，重編後它是關的。
                string a = iArgs[i].TrimStart('-');
                if (a == ArgPermanent || a == "perm" || a == "永久") return true;
            }
            return false;
        }

        public static string BuildHelp()
        {
            var aConfigs = LoadCommands();
            var aSb = new StringBuilder();
            aSb.AppendLine("🔧 **酒館 CLI 可用指令**（不分大小寫；一律以 `cmd` 開頭）");
            aSb.AppendLine();
            for (int i = 0; i < aConfigs.Count; i++)
            {
                var c = aConfigs[i];
                if (c == null || !c.enabled) continue;
                aSb.AppendLine($"- `{(string.IsNullOrWhiteSpace(c.usage) ? "cmd " + c.id : c.usage)}`");
                if (!string.IsNullOrWhiteSpace(c.description)) aSb.AppendLine($"　　{c.description}");
            }
            aSb.AppendLine();
            aSb.AppendLine("⚠ 只有白名單成員可觸發；需要二次確認的指令會由我問一次 **Y／N**。");
            return aSb.ToString().TrimEnd();
        }

        static UCL_BartenderCliCommandConfig Find(string iLowerName)
        {
            if (string.IsNullOrEmpty(iLowerName)) return null;
            var aConfigs = LoadCommands();
            for (int i = 0; i < aConfigs.Count; i++)
            {
                if (aConfigs[i] == null || !aConfigs[i].enabled) continue;
                if (aConfigs[i].MatchKey == iLowerName) return aConfigs[i];
            }
            return null;
        }

        static UCL_BartenderCliCommandConfig FindByLine(string iRawLine, UCL_BartenderCliSettings iSettings)
            => Find(NameOf(iRawLine, iSettings));

        static readonly char[] s_Sep = { ' ', '\t', '\r', '\n', '　' };

        /// <summary>指令名（已小寫）；只打 prefix 時回空字串。</summary>
        static string NameOf(string iRaw, UCL_BartenderCliSettings iSettings)
        {
            var aTokens = LowerTokens(iRaw, iSettings);
            return aTokens.Count > 0 ? aTokens[0] : "";
        }

        static List<string> LowerTokens(string iRaw, UCL_BartenderCliSettings iSettings)
        {
            var aOut = new List<string>();
            string[] aParts = (iRaw ?? "").ToLowerInvariant().Split(s_Sep, StringSplitOptions.RemoveEmptyEntries);
            string aPrefix = (iSettings.prefix ?? "cmd").ToLowerInvariant();
            for (int i = 0; i < aParts.Length; i++)
            {
                if (i == 0 && aParts[i] == aPrefix) continue;
                aOut.Add(aParts[i]);
            }
            return aOut;
        }

        // 區塊職責：組出一次呼叫的上下文，含「第 1 個 arg 之後的**原文**」。
        // 物理意義：`cmd msg all Free time until 23:50` 的訊息若走小寫 token，
        //          送出去會變成 `free time until 23:50` —— 而**沒有任何一層會報錯**。
        //          所以原文用「在原始字串上依序切掉 prefix / 指令名 / 第一個 arg」取得，
        //          不是把小寫 token 接回去（那接不回大小寫，也接不回換行）。
        static UCL_BartenderCliContext BuildContext(string iRaw, UCL_BartenderCliSettings iSettings,
            UCL_ChatMessage iMsg, string iRoomId)
        {
            var aLower = LowerTokens(iRaw, iSettings);
            string[] aArgs = aLower.Count > 1 ? aLower.GetRange(1, aLower.Count - 1).ToArray() : new string[0];

            // 依序吃掉：prefix（若有）→ 指令名 → 第 1 個 arg，剩下的就是原文內容
            string aRest = (iRaw ?? "").Trim();
            aRest = StripToken(aRest, out string aFirst);
            string aPrefix = (iSettings.prefix ?? "cmd");
            if (string.Equals(aFirst, aPrefix, StringComparison.OrdinalIgnoreCase))
            {
                aRest = StripToken(aRest, out _);        // 指令名
            }
            aRest = StripToken(aRest, out _);            // 第 1 個 arg（例如 all / <persona>）

            return new UCL_BartenderCliContext
            {
                Args = aArgs,
                RawLine = iRaw,
                RawAfterArgs = aRest,
                Src = iMsg,
                RoomId = iRoomId,
            };
        }

        /// <summary>切掉開頭第一個 token，回傳剩下的（已 TrimStart）；被切掉的放 oToken。</summary>
        static string StripToken(string iText, out string oToken)
        {
            string a = (iText ?? "").TrimStart(s_Sep);
            int aIdx = a.IndexOfAny(s_Sep);
            if (aIdx < 0) { oToken = a; return ""; }
            oToken = a.Substring(0, aIdx);
            return a.Substring(aIdx).TrimStart(s_Sep);
        }

        /// <summary>Y／N 解析。回 null ＝ 這句不是答覆。</summary>
        static bool? ParseYesNo(string iBody)
        {
            string a = (iBody ?? "").Trim().ToLowerInvariant().TrimEnd('.', '!', '。', '！');
            switch (a)
            {
                case "y": case "yes": case "是": case "好": case "確認": case "ok": return true;
                case "n": case "no": case "否": case "不": case "取消": return false;
                default: return null;
            }
        }

        static string RequesterKey(UCL_ChatMessage iMsg)
        {
            if (iMsg == null) return "";
            if (!string.IsNullOrWhiteSpace(iMsg.sender_persona)) return iMsg.sender_persona.Trim();
            if (!string.IsNullOrWhiteSpace(iMsg.sender_name)) return iMsg.sender_name.Trim();
            return (iMsg.sender_id ?? "").Trim();
        }

        static UCL_BartenderCliPending FindPending(UCL_BartenderCliState iState, UCL_ChatMessage iMsg)
            => FindPendingByKey(iState, RequesterKey(iMsg));

        static UCL_BartenderCliPending FindPendingByKey(UCL_BartenderCliState iState, string iKey)
        {
            if (iState == null || iState.pending == null || string.IsNullOrEmpty(iKey)) return null;
            for (int i = 0; i < iState.pending.Count; i++)
            {
                if (iState.pending[i] == null) continue;
                if (string.Equals(iState.pending[i].requester_key, iKey, StringComparison.OrdinalIgnoreCase))
                    return iState.pending[i];
            }
            return null;
        }

        static string DescribeSender(UCL_ChatMessage iMsg)
        {
            string aName = !string.IsNullOrWhiteSpace(iMsg.sender_name) ? iMsg.sender_name : iMsg.sender_id;
            return string.IsNullOrWhiteSpace(iMsg.sender_persona) ? $"@{aName}" : $"@{aName}（{iMsg.sender_persona}）";
        }

        // 發言形狀比照 mention service —— 一定要帶 BartenderRelayTag，
        // 否則 daemon 下一拍會把自己的回覆當成新訊息再處理一次（回音）。
        static void Post(string iBody, UCL_ChatMessage iSrc, string iRoomId, string iKind)
        {
            var aMsg = new UCL_ChatMessage
            {
                sender_id = UCL_BartenderDaemon.TavernKeeperId,
                sender_name = "酒保",
                kind = "chat",
                body = iBody,
                meta = new Dictionary<string, string>
                {
                    { "tag", UCL_BartenderDaemon.BartenderRelayTag },
                    { "reply_kind", iKind },
                    { "triggered_by_seq", iSrc != null ? iSrc.seq.ToString() : "0" },
                    { "triggered_by_sender", iSrc != null ? (iSrc.sender_id ?? "") : "" },
                },
            };
            UCL_ChatTavernIO.AppendMessage(iRoomId, aMsg);
        }
    }
}
#endif
