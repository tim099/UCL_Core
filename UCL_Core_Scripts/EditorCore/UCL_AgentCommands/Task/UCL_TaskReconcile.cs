// 區塊職責：晚安的**收工閘**（PendingWrapups）與「顯式跳過」寫入（WriteSkip）—— Editor 端。
//
// 物理意義：晚安 check 的「Task 對帳」報告已搬進 SCP_Core `SCP_TaskReconcileReport`（TASK-0305）——
//   Senate 的 goodnight-check 與 Editor 的 GoodNight step=check 呼叫同一份；收工閘的判準本體也早已在
//   SCP_Core（`SCP_TaskReconcile.PendingWrapups`）。本檔只剩兩件只有 Editor 做得到的事：
//   把 SCP 判出的單讀成 UCL_TaskEntry，以及把跳過理由寫進單子時間線（單子寫入端目前只有 Editor 有）。
//
// ⚠ Senate 的 goodnight-sleep 在「收工閘帶 skip_reason」且 Editor 活著時會整步轉派到 Editor，
//   就是為了走到這裡的 WriteSkip；Editor 沒開時那一段被跳過並在回傳檔明說（Tim 2026-09-26）。
// 2026-08-24 summit（TASK-0004）；2026-09-26 報告部分移出（TASK-0305）
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;   // TASK-0163：WriteSkip 沒落盤時要出聲（Debug.LogError）

namespace UCL.Core.EditorLib.AgentCommands.TaskMgmt
{
    public static class UCL_TaskReconcile
    {
        // ===========================================================
        // 區塊職責：晚安的**收工閘** —— 本次醒來後有動靜（含別人在單上留言）、還開著、我是參與者，
        //   而**最後一次收工之後又有動靜**（或從沒收過工）的單。
        //
        // 物理意義（Tim 2026-08-24 補的洞）：跨多日接回真正會斷的地方不是「忘了寫記憶」，
        //   是**單子還開著、狀態還是 in_progress，而沒有人知道停在哪一步**。
        //   ⇒ 所以閘的判準是「**本次醒來後有動靜**」而不是「有沒有記憶」：
        //     今天沒碰的單不該擋我下線（那是別天的事）。
        //
        // ⚠ 判定「今天收工過了」的唯一依據是**該單時間線裡今天的 `wrapup` 事件** ——
        //   不另存一份「收過工的清單」（那就是第二個真相源，而它會漂）。
        //   ⚠ `last_wrapup_at` 不算第二真相源：它由 `op=wrapup` 在同一次寫入裡落下，
        //     而讀不到時**回頭問時間線**（見 `LastWrapupUtc`）—— 兩者永遠指向同一個事件。
        //
        // 🩸 血證 2026-08-25（summit 自驗 TASK-0019 那格「跨夜沒驗」）——
        //   舊版用 `DateTime.UtcNow.ToString("yyyy-MM-dd")` 當「今天」，並拿它去**字串比對** UTC 時間戳。
        //   ⇒ 換日發生在 **UTC 午夜**，而本地是 +08 ⇒ **邊界落在本地早上 08:00**，
        //     也就是這個團隊每天開工的時間（08-25 那天 summit 08:16、basecamp 08:17 早安）。
        //   實測（探針 TASK-0029，同一張單只改 `updated_at` 一格）：
        //     `2026-08-25T01:00Z`（本地 09:00）⇒ 🛑 擋下並點名；
        //     `2026-08-24T23:50Z`（**本地同一天 07:50**）⇒ ✅ **靜默放行，一個字都沒說**。
        //   📌 而我當初記在見叢的猜測是錯的：我寫「午夜前後語意模糊」——
        //     **跨本地午夜反而安全**（那時 UTC 還在同一天），危險的是跨本地早上八點。
        //     ⇒ 一般形：**「今天」是人的概念，而人講的今天是本地日；
        //       拿 UTC 日去代表它，等於把換日點搬到一個沒有人會注意的時刻。**
        //
        // ⚠ 2026-08-25 Tim 拍板：**系統面一律 UTC，只有顯示面用本地時間。**
        //   ⇒ 於是「本地日」那個修法不能留（閘是系統面）。而單純換回 UTC 日會**把上面那隻放回去**。
        //   📌 兩個都不對，代表問題不在「用哪套曆」，在**這裡根本不該用曆**：
        //     這道閘真正要問的從來不是「今天」，是「**我這次上線之後**」——
        //     goodnight 依定義就是一段 session 的結尾，而 session 的起點寫在
        //     `_session/_persona_<name>.json` 的 `locked_at`（本來就是 UTC）。
        //   ⇒ 現行：兩個述詞都改成**跟 `locked_at` 比大小的純 UTC 時間戳比較** ——
        //     零日曆、零時區轉換，比「一律 UTC 日期」更嚴格地符合拍板。
        //     附帶好處：跨夜 session（wake#62 那種）本來就該把整段都算進來，而日曆做不到。
        // ⚠ 拿不到 lock（例如沒登入就跑）⇒ 退回 **UTC 日**（拍板的預設），不退回本地日。
        // ===========================================================
        public static List<UCL_TaskEntry> PendingWrapups(string iPersona)
        {
            var aOut = new List<UCL_TaskEntry>();
            // ⚠ **判準本體已移進 SCP_Core**（`SCP_TaskReconcile.PendingWrapups`，2026-08-31 Tim 拍板）。
            //   本檔不再自己判 —— 兩份各算一次的症狀是「後台頁說 2 張、CLI 說 3 張」而**兩邊都不報錯**。
            // 📌 而 session 起點仍由**本檔**算並傳進去（不用 SCP 那支自己讀 lock 的版本）：
            //   這邊走 `UCL_AwakeningService.ReadLock` 的 typed model，SCP 那邊是手撈 JSON 欄位 ——
            //   後者解析失敗會降級成「UTC 今天 00:00」，那是**把閘放寬**。
            //   ⇒ 有 typed model 可用的那一側就該用它；SCP 那條路留給沒有 model 的 CLI（那裡失敗會出聲）。
            // 📌 型別留 UCL：SCP 決定**哪幾張**，本檔用自己的 loader 把那幾張讀成 `UCL_TaskEntry`
            //   ⇒ 零轉換器（一個 25 欄的 converter 就是下一個會漂的地方），呼叫端簽章一個字沒動。
            DateTime aSince = SessionStartUtc(iPersona);
            var aRoot = new SCP.Core.Paths.SCP_DataRoot(UCL_AgentCommandsPath.DataRoot);
            foreach (var aPicked in SCP.Core.Tasks.SCP_TaskReconcile.PendingWrapups(
                         aRoot, iPersona, aSince, m => UnityEngine.Debug.LogWarning(m)))
            {
                var e = UCL_TaskIO.Find(aPicked.index);
                // 判準說它在、而本檔的 loader 讀不到 ⇒ **那是兩份 parser 分岔的訊號**，要出聲。
                if (e == null)
                {
                    UnityEngine.Debug.LogError($"[Task] 收工閘：SCP 判定 TASK-{aPicked.index:0000} 命中，"
                        + "而 UCL_TaskIO.Find 讀不到那張單 —— 兩側 parser 對同一個磁碟給出不同答案，這格要人看");
                    continue;
                }
                aOut.Add(e);
            }
            return aOut;
        }

        // ===========================================================
        // 區塊職責：本次 session 的起點（UTC）。
        // 物理意義：`locked_at` 是早安登入寫的，**它就是這段工作的起點**。
        // ⚠ 讀不到就退回「UTC 今天 00:00」—— 那是拍板的預設曆，**不是本地日**。
        //   （讀不到的情境：沒登入就跑閘、lock 被清掉。此時退回日曆是刻意的降級，不是 fail-open：
        //     它仍然會擋 UTC 今天動過的單，只是拿不到跨夜那一段。）
        // ===========================================================
        static DateTime SessionStartUtc(string iPersona)
        {
            try
            {
                var aLock = Awakening.UCL_AwakeningService.ReadLock(iPersona);
                if (aLock != null && !string.IsNullOrWhiteSpace(aLock.locked_at)
                    && DateTime.TryParse(aLock.locked_at, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal
                        | System.Globalization.DateTimeStyles.AssumeUniversal, out var aUtc))
                    return aUtc;
            }
            catch { /* 讀不到就走下面的降級 —— 降級是有曆的那個，不是沒有閘 */ }
            return DateTime.UtcNow.Date;
        }

        /// <summary>ISO 時間戳晚於 <paramref name="iSinceUtc"/> 嗎（純 UTC 比大小，不碰時區轉換）。</summary>
        static bool IsAfterUtc(string iIso, DateTime iSinceUtc)
        {
            if (string.IsNullOrWhiteSpace(iIso)) return false;
            return DateTime.TryParse(iIso, System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.AdjustToUniversal
                       | System.Globalization.DateTimeStyles.AssumeUniversal, out var aUtc)
                   && aUtc > iSinceUtc;
        }

        // ===========================================================
        // 區塊職責：解析時間戳的兩個述詞（都在 §PendingWrapups 上方那段血證的射程內）。
        // ⚠ 解析不出來一律回 **false** —— 對 `updated_at` 是「不擋」，對 `wrapup` 是「當成沒收工」，
        //   兩邊都倒向**擋下**那一側（而擋下有出口：`skip_reason`；放行沒有）。
        // ===========================================================

        // ===========================================================
        // 區塊職責：這張單**最後一次 `wrapup`** 的時戳（UTC）；沒有回 `DateTime.MinValue`。
        //
        // 來源順序（**刻意不是「缺值就擋」**，這格是我對驗收標準的一處偏離，已在單上標明）：
        //   ① frontmatter 的 `last_wrapup_at` —— `op=wrapup` 寫的，正常路徑走這條
        //   ② 讀不到就**回頭問時間線**（`wrapup` 事件本來就在那裡）
        //   ③ 兩邊都沒有 ⇒ 從來沒收過工 ⇒ 回 MinValue ⇒ 呼叫端**擋下**
        //
        // 為什麼不直接「缺值就擋」：本欄位是 2026-08-25 才加的，**所有既有單都缺值** ——
        //   一律擋的話，上線當晚每個人都會被自己收過工的舊單擋住，
        //   而那正是「修完立刻天天亮」。⇒ 時間線是同一件事的既有紀錄，問它就不必回填。
        // ⚠ 而「兩邊都沒有」仍然倒向擋下（擋下有 `skip_reason` 出口，放行沒有）——
        //   驗收標準要的那個方向沒有被放掉，只是把「缺值」拆成了「真的沒收過工」與「欄位還沒補」。
        // ===========================================================
        public static DateTime LastWrapupUtc(UCL_TaskEntry e)
        {
            if (e == null) return DateTime.MinValue;
            if (!string.IsNullOrWhiteSpace(e.last_wrapup_at)
                && DateTime.TryParse(e.last_wrapup_at, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal, out var aField))
                return aField;
            return LastWrapupFromTimeline(e.index);
        }

        /// <summary>時間線裡**最後一筆** `wrapup` 的時戳；沒有回 <c>DateTime.MinValue</c>。</summary>
        static DateTime LastWrapupFromTimeline(int iIndex)
        {
            var aOut = DateTime.MinValue;
            try
            {
                string aPath = UCL_TaskIO.TaskPath(iIndex);
                if (!File.Exists(aPath)) return aOut;
                foreach (var aLine in File.ReadAllLines(aPath, Encoding.UTF8))
                {
                    string aTrim = aLine.TrimStart();
                    if (!aTrim.StartsWith("- ", StringComparison.Ordinal)) continue;
                    if (aLine.IndexOf("`wrapup`", StringComparison.Ordinal) < 0) continue;
                    string aStamp = aTrim.Substring(2).TrimStart();
                    int aCut = aStamp.IndexOfAny(new[] { ' ', '\t', '　' });
                    if (aCut > 0) aStamp = aStamp.Substring(0, aCut);
                    if (DateTime.TryParse(aStamp, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AdjustToUniversal
                            | System.Globalization.DateTimeStyles.AssumeUniversal, out var aUtc)
                        && aUtc > aOut) aOut = aUtc;
                }
            }
            catch { /* 讀不到就當沒有 —— 呼叫端會擋下，而擋下有出口 */ }
            return aOut;
        }

        // ⛔ `HasWrapupSince(index, since)` 已於 2026-08-25（TASK-0036）移除。
        //   它問的是「**有沒有**收過工」，而閘要問的是「**最後一次**收工之後有沒有又動過」——
        //   兩者在「10:00 收工、11:00 又改了」那格給相反答案，而舊的那個會**放行**。
        //   ⚠ 刻意不留著：一支長得像判準的死函式，下一個人會拿它去重造同一隻。
        //   要最後一次收工時戳請用 `LastWrapupUtc(entry)`。
        /// <summary>
        /// 顯式跳過收工閘：把理由寫進**那張單的時間線**。
        /// <para>⚠ 不是寫進 log —— **跳過要留在別人看得到的地方**（basecamp 拍板：
        /// 可跳過但留名，比不可跳過更持久；硬擋會讓人去找繞過的方法，而繞過一次那道閘就永久失效）。</para>
        /// </summary>
        public static bool WriteSkip(UCL_TaskEntry e, string iPersona, string iReason)
        {
            if (e == null) return false;
            string aNow = UCL_TaskIO.NowUtc();
            // ⭐ TASK-0163：本函式就是那個「`⛔ [RMW-END]` 前哨貼不進來」的位置 ——
            //   它把 `e` **當參數收**，於是 READ 發生在更上游（`Cmd_GoodNight` sleep 那一步
            //   的 `foreach` 之前就把清單載好了），這裡沒有一個地方放得下那個標記。
            //   ⇒ 走 `Mutate` 之後跨度由型別決定：拿 index 進去、在鎖內重讀，
            //   呼叫端傳進來的 `e` 降級成「提示」（只用它的 index）。
            //   📌 這一格是本次遷移的**原型**：前哨表達不出來的形狀，換成入口就消失了。
            bool aWrote = UCL_TaskIO.Mutate(e.index, m =>
            {
                UCL_TaskIO.Touch(m, aNow);
                return UCL_TaskWrite.Line($"{aNow}　`wrapup-skip`　{iPersona} 顯式跳過收工："
                    + iReason.Replace("\r", " ").Replace("\n", " "));
            });
            // ⚠ 回 false ＝ 鎖內重讀時那張單不在了 ⇒ **跳過紀錄沒有落盤**。
            //   ⛔ 不吞掉：這個函式存在的理由就是「跳過要留在別人看得到的地方」，
            //   而一個沒寫成的跳過紀錄，跟「他根本沒跳過」長得一樣。
            if (!aWrote)
                Debug.LogError($"[TaskReconcile] TASK-{e.index} 的 `wrapup-skip` **沒有落盤**"
                    + "（鎖內重讀時那張單不在了：被刪或被搬）⇒ 跳過的理由沒有留在單上，"
                    + "而「跳過但留名」正是這道閘的設計。⇒ 去看那張單是不是剛被刪掉。");
            return aWrote;
        }

        static List<string> RolesOrReporter(UCL_TaskEntry e, string iPersona)
        {
            var aRoles = e.RolesOf(iPersona);
            if (aRoles.Count > 0) return aRoles.Select(r => r.ToString()).ToList();
            return new List<string> { "reporter（開單人，未列參與者）" };
        }
    }
}
#endif
