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
            oReason = Reason(aBlocker);
            oExit = Exit(iPersona, aBlocker);
            return false;
        }

        /// <summary>「誰的哪一場、到幾點」—— 讀的人要能一眼判斷要等多久。</summary>
        static string Reason(SCP_ActivitySession iBlocker)
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
        static string Exit(string iPersona, SCP_ActivitySession iBlocker)
        {
            if (iBlocker.kind == SCP_ActivitySessionKind.FreeTime)
            {
                return $"先收掉那場：senate ucmd run FreeTime --persona {iPersona} --arg step=end --arg reason=<一句>";
            }
            if (iBlocker.kind == SCP_ActivitySessionKind.StreamWatch)
            {
                string aUntil = string.IsNullOrEmpty(iBlocker.until_local) ? "它到期" : iBlocker.until_local;
                return $"等 {aUntil}（觀影沒有 step=end —— 到期或 Tim 停錄影時 Cmd 會自己收工並結算）；"
                     + $"要提前收就請 Tim 停錄影";
            }
            // 未登記的 kind：**說出它是誰**，不要退回一句「請自行處理」。
            return $"先收掉那場（kind=`{iBlocker.kind}`，本守衛沒有它的收工指令）；"
                 + $"查現況：senate cmd sessions --arg op=show --arg target_persona={iPersona}";
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
