// 區塊職責：**各 kind 開場的唯一入口** —— 把 `SCP_ActivitySessionStore.TryStart` 包成
//           「成功 ／ 被擋（附原因＋處理方式）」兩態，讓每個 kind 的開場路徑只寫一次判斷。
// 物理意義：🩸 TASK-0056 的活體（2026-09-05，basecamp）：一場進行中的 StreamWatch 擺在檔位上，
//          跑 FreeTime `step=start` ⇒ **那場觀影不見了**，而 FreeTime 回 Success、還發了開場宣告。
//          成因是各 kind 的開場守衛各自呼叫 `Load(自己那個 kind)` —— 它 filter kind
//          ⇒ 別 kind 的場在它眼裡是 `null` ⇒ 守衛放行 ⇒ `Save` 覆蓋掉那個檔位。
//          ⇒ 判斷收在**寫入端**（`TryStart` 先查再寫，`FindRunning` 不 filter kind），
//          而本檔只負責把「被擋下時要說什麼」寫成一份，不讓兩個 kind 各寫一套措辭。
// 數值影響：成功時＝一次 `Save`（與舊路徑逐位元組相同）；被擋時**一個位元組都不寫**。
//          跨 process 沒有鎖 —— 兩個人同一毫秒開場仍可能都通過（本層不宣稱它是原子的）。
//
// ⚠ 措辭照 D-1 拍板：**祈使句、指令直接附上、不解釋代價**。
//   「同 kind 疊開」不歸這裡管（各 kind 既有守衛自己擋，本層只擋跨 kind）——
//   兩條是正交的軸，混在一起會讓其中一條的失效被另一條的通過掩蓋。
#if UNITY_EDITOR
using System;
using SCP.Core.Session;

namespace UCL.Core.EditorLib.AgentCommands
{
    /// <summary>
    /// 開場守衛：這個人此刻在別的 kind 的場裡嗎？在就不寫，並給出原因與出口。
    /// <code>
    /// if (!UCL_SessionStartGuard.TryStart(aPersona, aSession, SCP_ActivitySessionKind.FreeTime,
    ///                                     out string aReason, out string aExit))
    /// {
    ///     // 寫 blocked 回傳檔（reason / exit 直接用），非零退出
    /// }
    /// </code>
    /// </summary>
    public static class UCL_SessionStartGuard
    {
        /// <summary>
        /// 開場（跨 kind 守衛 ＋ 落檔）。回 <c>true</c> ＝ 已寫入；<c>false</c> ＝ 被擋且**沒有寫**。
        /// </summary>
        /// <param name="oReason">被擋的原因（誰的哪一場、到幾點）。成功時為空字串。</param>
        /// <param name="oExit">處理方式（可直接複製執行的指令，或「等到期」）。成功時為空字串。</param>
        public static bool TryStart(string iPersona, SCP_ActivitySession iSession, string iKind,
                                    out string oReason, out string oExit)
        {
            oReason = "";
            oExit = "";
            if (SCP_ActivitySessionStore.TryStart(UCL_AgentCommandsPath.ScpDataRoot, iPersona, iSession,
                                                  iKind, DateTime.Now, out SCP_ActivitySession aBlocker))
            {
                return true;
            }
            // ⚠ TryStart 回 false 有兩種：被別 kind 擋下（aBlocker 有值），或 Save 自己失敗（null）。
            //   兩者**不可同形** —— 前者是正常守衛，後者是磁碟出事，而後者靜靜當成「被擋」
            //   會讓人去收一場根本不存在的場。
            if (aBlocker == null)
            {
                oReason = "session 寫入失敗（不是被別的場擋下）—— 路徑或磁碟有問題";
                oExit = "跑 senate cmd sessions --arg op=list 看目錄狀態，並確認資料根可寫";
                return false;
            }
            // ⚠ 兩條軸擋下來的東西**不同形**，而 TryStart 只回一個 `oBlockedBy`：
            //   軸1（每人一場）⇒ 那場是**我自己的**；軸2（全域互斥，TASK-0058）⇒ 那場是**別人的**。
            //   ⛔ 用同一句話講會給出相反的處理方式（關自己的場 vs 等別人）。
            //   ⇒ 判準用 `persona` 欄，不猜 —— 它就寫在那份 session 檔裡。
            bool aMine = string.Equals(aBlocker.persona, iPersona, StringComparison.Ordinal);
            oReason = aMine ? ReasonMine(aBlocker) : ReasonOther(aBlocker);
            oExit = aMine ? ExitMine(iPersona, aBlocker) : ExitOther(aBlocker);
            return false;
        }

        /// <summary>擋你的是**別人**持有的場（全域互斥那條軸）—— 主詞要換人，出口也要換。</summary>
        static string ReasonOther(SCP_ActivitySession iBlocker)
        {
            string aWho = string.IsNullOrEmpty(iBlocker.persona) ? "(session 檔沒寫 persona)" : "@" + iBlocker.persona;
            string aUntil = string.IsNullOrEmpty(iBlocker.until_local) ? "未寫截止時刻" : "至 " + iBlocker.until_local;
            return $"**{aWho}** 正在 **{KindLabel(iBlocker.kind)}**（`{iBlocker.session_id}`，{aUntil}）"
                 + $" —— 這種場全域同時只能一個人";
        }

        /// <summary>別人持有時的出口：**等或去問他**，⛔ 不要叫人去收別人的場。</summary>
        static string ExitOther(SCP_ActivitySession iBlocker)
        {
            string aWho = string.IsNullOrEmpty(iBlocker.persona) ? "持有者" : "@" + iBlocker.persona;
            string aUntil = string.IsNullOrEmpty(iBlocker.until_local) ? "他收工" : iBlocker.until_local;
            string aOut = $"等 {aUntil}，或去酒館 {aWho} 問他還要多久；查現況：senate cmd sessions --arg op=list";

            // 🩸 2026-09-05（@summit）：原本到上一行為止。而讀到它的人下一句一定會問
            //    「那他要怎麼收？」—— 沒有那格的話，他會去猜一個指令，或去跑 `sessions --arg op=close`
            //    （那支只收**殘留**，進行中的場它會擋，於是他得到第二個看不懂的錯誤）。
            //    ⇒ 附上指令原文，但**主詞標死是持有者**：⛔ 這條不是給你跑的。
            //    指令名走登記表（`CmdNameOf` 沒登記就照實回 kind 本身，不編字）。
            UCL_SessionKindEntry aEntry = UCL_SessionKindHost.For(iBlocker.kind);
            if (aEntry != null && aEntry.HasStepEnd && !string.IsNullOrEmpty(iBlocker.persona))
            {
                aOut += $"；⚠ 他自己的退出指令（**由 {aWho} 跑，不是你跑**）："
                      + $"senate ucmd run {aEntry.CmdName} --persona {iBlocker.persona} --arg step=end";
            }
            // ⛔ 而這一句對**全域互斥**的 kind 特別要緊：被擋的人手上沒有別的路，
            //    最順手的「解法」就是換一個 persona 名再開一場 —— 那是製造分身，不是解法。
            if (SCP_ActivitySessionKind.IsGlobalExclusive(iBlocker.kind))
            {
                aOut += "；⛔ 不要為了繞過而改用別的 persona 名進場";
            }
            return aOut;
        }

        /// <summary>「誰的哪一場、到幾點」—— 讀的人要能一眼判斷要等多久。</summary>
        static string ReasonMine(SCP_ActivitySession iBlocker)
        {
            string aUntil = string.IsNullOrEmpty(iBlocker.until_local) ? "未寫截止時刻" : "至 " + iBlocker.until_local;
            return $"你已經在另一種 session 裡：**{KindLabel(iBlocker.kind)}**"
                 + $"（`{iBlocker.session_id}`，{aUntil}）—— 一人同時只能在一種場";
        }

        /// <summary>
        /// 處理方式 —— **每個 kind 的收工路徑不同形**，所以這裡逐 kind 給，不給一句通用的廢話。
        /// </summary>
        /// <remarks>
        /// ⚠ `senate cmd sessions --arg op=close` **只收殘留**（那支自己的說明就寫著），
        /// 進行中的場要走該 kind 自己的收工步驟 —— 那裡才有收工公告與結算。
        /// ⚠ StreamWatch **沒有 `step=end`**：它到期或 Tim 停錄影時由 Cmd 自己宣布收工。
        /// ⇒ 那一格的誠實出口是「等它到期」，不是編一個不存在的指令出來。
        /// </remarks>
        static string ExitMine(string iPersona, SCP_ActivitySession iBlocker)
        {
            // 🩸 2026-09-05（@summit 在 0058 QA 撿到）：這裡本來是 `FreeTime` / `StreamWatch` 各一條 `if` ——
            //   **那正是我同一天在 `Cmd_SessionClose` 剛消滅的形狀，而這條路上還有第二份。**
            //   ⇒ 新增一種 kind 要回頭改**這裡**，而漏改不報錯：它會退到 fallback 說
            //   「本守衛沒有它的收工指令」，而那句話跟「這個 kind 真的沒有收工指令」同形。
            // ⇒ 改走登記表（`UCL_SessionKindHost`）—— 它已經有這裡要的兩格。
            UCL_SessionKindEntry aEntry = UCL_SessionKindHost.For(iBlocker.kind);
            if (aEntry == null)
            {
                // 沒登記：**說出它是誰，並附已登記清單**（@summit 的版本有這一半，我改寫時弄丟了）。
                string aKnown = string.Join("／", UCL_SessionKindHost.RegisteredKinds());
                return $"先收掉那場（kind=`{iBlocker.kind}`，**沒有人登記過它**"
                     + (aKnown.Length == 0 ? "" : $"；已登記：{aKnown}") + "）；"
                     + $"查現況：senate cmd sessions --arg op=show --arg target_persona={iPersona}";
            }
            if (!aEntry.HasStepEnd)
            {
                // ⚠ 沒有 `step=end` 的 kind（觀影）：誠實出口是「等它到期」，
                //   ⛔ 不編一個不存在的指令 —— 那是 @summit 今天改名 `op=exit`→`step=end` 治的同一隻。
                string aUntil = string.IsNullOrEmpty(iBlocker.until_local) ? "它到期" : iBlocker.until_local;
                // ⚠ `EarlyEndHint` 是 kind 專屬的處置知識 —— 有的話一定要附上，
                //   否則讀的人只知道「等」，不知道還有一條真的能提前收的路。
                string aWait = $"等 {aUntil}（`{aEntry.CmdName}` 沒有 step=end —— 到期或宿主停止時 Cmd 會自己收工並結算）";
                if (aEntry.EarlyEndHint.Length > 0) aWait += "；" + aEntry.EarlyEndHint;
                return aWait;
            }
            return $"先收掉那場：senate ucmd run {aEntry.CmdName} --persona {iPersona} --arg step=end --arg reason=<一句>";
        }

        /// <summary>kind 的人看得懂的名字。⚠ 認不得就**原樣印**，不要印「未知」（那會把 kind 名吃掉）。</summary>
        static string KindLabel(string iKind)
        {
            if (iKind == SCP_ActivitySessionKind.FreeTime) return "自由時間";
            if (iKind == SCP_ActivitySessionKind.StreamWatch) return "觀影";
            return string.IsNullOrEmpty(iKind) ? "(kind 欄是空的)" : iKind;
        }
    }
}
#endif
