#if UNITY_EDITOR
// ===========================================================
// 區塊職責：「叮 / 醒來時的酒館 catch-up」—— 在線一覽 ＋ 未讀訊息 ＋ persona inbox，組成一份簡報。
// 物理意義：agent 被叮或剛醒來時，chat 端**不會**自動把同事的發言推過來。
//          沒有這一步的症狀不是「看到比較少」，是**看起來一切正常地漏掉別人的交接**。
// 為什麼是 static class 而不是寫在 Cmd 裡（Tim 2026-08-20 拍板）：
//          同一份組裝會被 Cmd_Tavern、早安流程、後台頁共用。放進 Cmd 的話第二個呼叫端
//          只能複製一份，而兩份對「誰在線 / 我還沒看過什麼」給出不同答案時，兩邊都不會報錯。
// 🩸 本層取代 `AgentCommands/Tools/tavern_catchup.py`（2026-08-20）。搬家的真正理由不是「比較乾淨」，
//          是**「已讀到哪」原本有三個寫入端**：C# `UCL_TavernCursor`、python `tavern_cmd.py`、
//          python `tavern_catchup.py`，各自 read-modify-write 同一份 `_inbox_cursor/<persona>.json`。
//          2026-08-16 觀影 sidecar 的兩隻游標 bug（游標從沒設過 ⇒ 從全庫最舊列起／
//          0 筆未讀仍前進 ⇒ 跳過同事整段發言）就是這個家族，而兩次「看起來都很正常」。
// 數值影響：**唯一的寫入是推進游標**（走 UCL_TavernCursor，不自己碰檔），以及落一份回傳檔。
//          不發訊息、不記帳、不動金流。
// ⚠ 順序不可反：**先組出簡報、再推游標**。反過來的話，回傳檔寫入失敗時訊息已被標成已讀
//          ⇒ 那批訊息永遠不會再出現在任何人的未讀裡，而且沒有錯誤訊息。
// ===========================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UCL.Core.EditorLib.AgentCommands.AwakenInit;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    public static class UCL_TavernCatchupService
    {
        public const int DefaultMinCount = 10;
        public const int DefaultInboxShow = 10;
        const int BODY_CLIP = 240;

        // ===========================================================
        // 區塊職責：組出 catchup 簡報並（可選）推進游標。
        // 數值影響：oAdvancedTo 是**真的寫進去**的游標值；沒有未讀時為 null 且不寫檔
        //          —— 「0 筆未讀」不推進是刻意的，推進會跳過還沒落盤的那一段。
        // ===========================================================
        public static string Build(
            string iPersona, string iRoom, int iMinCount, bool iQuietSystem, bool iIncludeSelf,
            int iInboxShow, bool iAdvanceCursor, out string oAdvancedTo, out int oUnreadCount)
        {
            oAdvancedTo = null;
            oUnreadCount = 0;
            string room = string.IsNullOrEmpty(iRoom) ? "tavern" : iRoom;
            int minCount = iMinCount <= 0 ? DefaultMinCount : iMinCount;
            var sb = new StringBuilder();

            string cursorBefore = UCL_TavernCursor.ReadCursor(iPersona);
            sb.AppendLine($"# 📬 叮 catchup — {iPersona}　`{room}`");
            sb.AppendLine();
            sb.AppendLine($"- 游標（本次之前）：{(string.IsNullOrEmpty(cursorBefore) ? "**（從未設過）** —— 下面會是最近 " + minCount + " 筆，不是全庫" : "`" + cursorBefore + "`")}");
            sb.AppendLine();

            AppendOnline(sb, iPersona);

            // ── 未讀 ──
            var unread = UCL_TavernCursor.ReadUnread(iPersona, room, out string newestTs, out bool truncated);
            var shown = new List<UCL_ChatMessage>();
            int hiddenSystem = 0, hiddenSelf = 0;
            foreach (var m in unread)
            {
                if (!iIncludeSelf && IsMine(m, iPersona)) { hiddenSelf++; continue; }
                if (iQuietSystem && IsSystem(m)) { hiddenSystem++; continue; }
                shown.Add(m);
            }
            oUnreadCount = shown.Count;

            // 未讀不足 minCount 時補最近的舊訊息 —— Tim 2026-05-28：「確保會讀到至少最近 10 筆
            // （無論是否提及自己），已看過的可排除」。⚠ 補進來的那幾筆要**標記**，
            // 否則「這是新的」與「這是補給你看的」在畫面上長得一樣。
            var backfill = new List<UCL_ChatMessage>();
            if (shown.Count < minCount)
            {
                var recent = UCL_ChatTavernIO.Tail(room, minCount * 3);
                for (int i = recent.Count - 1; i >= 0 && backfill.Count < (minCount - shown.Count); i--)
                {
                    var m = recent[i];
                    if (shown.Contains(m)) continue;
                    if (!iIncludeSelf && IsMine(m, iPersona)) continue;
                    if (iQuietSystem && IsSystem(m)) continue;
                    backfill.Insert(0, m);
                }
            }

            sb.AppendLine($"## 💬 未看訊息　**{shown.Count}** 筆"
                + (hiddenSelf > 0 ? $"（已排除自己 {hiddenSelf} 筆）" : "")
                + (hiddenSystem > 0 ? $"（已隱藏酒保系統廣播 {hiddenSystem} 筆 —— 打款／獎金可能在裡面，`quiet_system=0` 看得到）" : ""));
            if (truncated)
                sb.AppendLine("⚠ **未讀一次交付不完** —— 這批是**最舊的**那段；更新的還留在未讀裡，"
                    + "再跑一次 catchup 會接著給（不會遺失）。");
            sb.AppendLine();
            int clipNormal = UCL_ChatTavernSettings.MessageBodyClip;
            int clipMention = UCL_ChatTavernSettings.MessageBodyClipMentioned;
            foreach (var m in shown)
            {
                bool mentioned = MentionsMe(m, iPersona);
                AppendMsg(sb, m, mentioned ? "🔔 **@你**" : "", mentioned ? clipMention : clipNormal);
            }
            if (backfill.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"### 🔁 補足最近 {minCount} 筆（**這些是已看過的**，不是新訊息）");
                foreach (var m in backfill) AppendMsg(sb, m, "（已讀）", clipNormal);
            }

            AppendInbox(sb, iPersona, iInboxShow <= 0 ? DefaultInboxShow : iInboxShow);

            // ── 推游標（最後一步，且只有真的有未讀才推）──
            sb.AppendLine();
            if (!iAdvanceCursor)
            {
                sb.AppendLine("- 游標：**未推進**（`advance=0`）—— 這次讀到的下次還會再出現。");
            }
            else if (string.IsNullOrEmpty(newestTs))
            {
                // 🩸 2026-08-16：0 筆未讀仍前進 ⇒ 跳過了同事後來發言的區間，而回傳檔看起來正常。
                // 🩸 2026-09-03：積壓超過回捲上限時 ReadUnread 也回 null —— 那不是「沒訊息」，
                //    是「最舊的那則還沒到手」。兩者都不推，但**必須長得不一樣**。
                sb.AppendLine(truncated
                    ? "- 游標：**未推進**（積壓超過回捲上限 —— 最舊的未讀還沒進到窗口）"
                      + " ⇒ 推了就會永久跳過它們。請先消化積壓或調高 `BACKLOG_SCAN_CAP`。"
                    : "- 游標：**未推進**（本次 0 筆未讀）—— 沒有讀數就不該移動水位。");
            }
            else
            {
                UCL_TavernCursor.WriteCursor(iPersona, newestTs);
                string readBack = UCL_TavernCursor.ReadCursor(iPersona);
                oAdvancedTo = readBack;
                sb.AppendLine(readBack == newestTs
                    ? $"- ✓ 游標已推進到 `{readBack}`（寫入後讀回確認）"
                    : $"- ✗ 游標寫入後讀回不符：期望 `{newestTs}`、實際 `{readBack}` —— 下次會重讀這一段");
            }
            return sb.ToString();
        }

        // ===========================================================
        // 區塊職責：「未讀訊息」這一段的**唯一**呈現實作 —— 叮 / 自由時間換骰共用。
        // 物理意義：在此之前 catchup 與 `Cmd_FreeTime` 各有一份渲染，兩邊的截斷長度、
        //          排除規則、游標推進時機各自演化 ⇒ 同一批訊息在兩處長得不一樣，而兩邊都不報錯。
        // ⚠ 順序不可反：**先寫進 ioR、再推游標**。反過來的話回傳檔寫入失敗時訊息已被標成已讀
        //          ⇒ 那批永遠不再出現在任何人的未讀裡，且沒有錯誤訊息。
        // 數值影響：iBodyClip 是**顯示**截斷（不影響原文）；iAdvance=false 時完全不寫檔。
        // ===========================================================
        public static void AppendUnreadSection(
            StringBuilder ioR, string iPersona, string iRoom,
            bool iQuietSystem, bool iIncludeSelf, int iBodyClip, int iBodyClipMentioned, bool iAdvance,
            out List<UCL_ChatMessage> oShown, out string oNewestTs, out int oHiddenSelf, out int oHiddenSystem)
        {
            oShown = new List<UCL_ChatMessage>();
            oNewestTs = null;
            oHiddenSelf = 0;
            oHiddenSystem = 0;
            string room = string.IsNullOrEmpty(iRoom) ? "tavern" : iRoom;
            bool truncated = false;
            try
            {
                var unread = UCL_TavernCursor.ReadUnread(iPersona, room, out oNewestTs, out truncated);
                foreach (var m in unread)
                {
                    if (!iIncludeSelf && IsMine(m, iPersona)) { oHiddenSelf++; continue; }
                    if (iQuietSystem && IsSystem(m)) { oHiddenSystem++; continue; }
                    oShown.Add(m);
                }
            }
            catch (Exception e)
            {
                // 讀不到 ≠ 沒訊息 —— 空白會被讀成「今天很安靜」，那是兩件事。游標不推進。
                ioR.AppendLine($"## 🍺 酒館未讀：⚠ **讀取失敗**（{e.Message}）—— 這不代表沒人講話；游標未推進");
                oNewestTs = null;
                return;
            }

            ioR.AppendLine($"## 🍺 酒館未讀　**{oShown.Count}** 筆"
                + (oHiddenSelf > 0 ? $"（排除自己 {oHiddenSelf}）" : "")
                + (oHiddenSystem > 0 ? $"（隱藏酒保廣播 {oHiddenSystem}　`quiet_system=0` 可見）" : "")
                + (iAdvance ? "　—— **本段印出後即推進已讀游標**" : "　—— 本次**不推進**游標"));
            if (truncated)
                ioR.AppendLine("- ⚠ **一次交付不完** —— 這批是最舊的那段，更新的還留在未讀裡（不會遺失）。");
            if (oShown.Count == 0) ioR.AppendLine("- （沒有未讀）");
            foreach (var m in oShown)
            {
                bool mentioned = MentionsMe(m, iPersona);
                // 標記不是裝飾：不標的話「同一份清單裡有的長有的短」看起來像截斷壞掉，
                // 而其實是規則在生效 —— 讓規則在畫面上看得見，才不會有人回頭去查一個不存在的 bug。
                AppendMsg(ioR, m, mentioned ? "🔔 **@你**" : "",
                          mentioned ? iBodyClipMentioned : iBodyClip);
            }

            if (!iAdvance || string.IsNullOrEmpty(oNewestTs)) return;
            UCL_TavernCursor.WriteCursor(iPersona, oNewestTs);
            string back = UCL_TavernCursor.ReadCursor(iPersona);
            ioR.AppendLine(back == oNewestTs
                ? $"- ✓ 已讀游標推進到 `{back}`（寫入後讀回確認）"
                : $"- ✗ 游標寫入後讀回不符：期望 `{oNewestTs}`、實際 `{back}`");
        }

        // 判準：body 裡出現 `@<persona>`（不分大小寫）。
        // ⚠ 刻意**不**認顯示名或 agent 名 —— 那兩個會變（合一遷移剛改過一輪），
        //   而 persona 是這套系統裡唯一穩定的識別。漏認的代價是「該長的沒長」（看得到、要再撈一次），
        //   誤認的代價是「不相干的訊息佔滿版面」—— 前者便宜得多。
        static bool MentionsMe(UCL_ChatMessage m, string iPersona)
        {
            if (string.IsNullOrEmpty(iPersona)) return false;
            string body = m?.body ?? "";
            return body.IndexOf("@" + iPersona, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsMine(UCL_ChatMessage m, string iPersona)
            => !string.IsNullOrEmpty(iPersona)
               && string.Equals(m?.sender_persona, iPersona, StringComparison.OrdinalIgnoreCase);

        // 系統廣播＝酒保代發的自動訊息（打款／結算／公告）。判準走 sender_id，
        // 不看內容關鍵字 —— 關鍵字會把同事**談論**打款的訊息一起吃掉。
        static bool IsSystem(UCL_ChatMessage m)
        {
            string s = m?.sender_id ?? "";
            return string.Equals(s, "tavern-keeper", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "subconscious-daemon", StringComparison.OrdinalIgnoreCase);
        }

        static void AppendMsg(StringBuilder sb, UCL_ChatMessage m, string iMark, int iClip = BODY_CLIP)
        {
            string tag = (m?.meta != null && m.meta.TryGetValue("tag", out var tg) && !string.IsNullOrEmpty(tg))
                ? $" «{tg}»" : "";
            string name = string.IsNullOrEmpty(m?.sender_name) ? (m?.sender_id ?? "?") : m.sender_name;
            string persona = string.IsNullOrEmpty(m?.sender_persona) ? "" : "@" + m.sender_persona;
            sb.AppendLine($"- **[seq {m.seq}]** {LocalHm(m.ts)} **{name}{persona}**{tag} {iMark}");
            string body = (m?.body ?? "").Replace("\r", "").Replace("\n", " ⏎ ").Trim();
            if (iClip > 0 && body.Length > iClip) body = body.Substring(0, iClip) + $"…（全文 {body.Length} 字）";
            sb.AppendLine($"    {body}");
        }

        static string LocalHm(string iTs)
        {
            if (DateTime.TryParse(iTs, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
                return t.ToLocalTime().ToString("MM-dd HH:mm:ss");
            return "??:??:??";
        }

        // ===========================================================
        // 區塊職責：在線一覽。
        // ⚠ 空清單**不是**「沒有人在線」，是「查不到 lock」 —— 兩者必須長得不一樣，
        //   否則會有人拿空清單當「今天沒人」的證據去 @ 一個其實在線的人（或反之）。
        // ===========================================================
        static void AppendOnline(StringBuilder sb, string iMe)
        {
            List<UCL_PersonaLockInfo> locks;
            try { locks = UCL_ActivePersonaLocks.ListOnline(); }
            catch (Exception e)
            {
                sb.AppendLine($"## 🟢 在線：**讀取失敗** —— {e.Message}（空 ≠ 沒人）");
                sb.AppendLine();
                return;
            }
            sb.AppendLine($"## 🟢 在線（{locks.Count}）");
            if (locks.Count == 0)
                sb.AppendLine("- （查不到任何 lock）—— **空不代表沒人**，只代表這裡讀不到。");
            foreach (var l in locks)
            {
                string me = string.Equals(l.Persona, iMe, StringComparison.OrdinalIgnoreCase) ? "　← 你" : "";
                string status = string.IsNullOrEmpty(l.NowStatus) ? "" : $"　💬 {l.NowStatus}";
                sb.AppendLine($"- **{l.Persona}**（{l.Agent}）{status}{me}");
            }
            sb.AppendLine();
        }

        // ===========================================================
        // 區塊職責：persona 層 inbox 摘要（誰 @ 了我、還沒處理的）。
        // 物理意義：ack 的語意是「已處理」不是「已看過」⇒ 這裡只列，不代為歸檔。
        // ===========================================================
        static void AppendInbox(StringBuilder sb, string iPersona, int iShow)
        {
            string raw;
            try { raw = UCL_ChatTavernQuestIO.ReadInbox("tavern", iPersona); }
            catch (Exception e)
            {
                sb.AppendLine($"## 📥 inbox：讀取失敗 —— {e.Message}");
                return;
            }
            if (string.IsNullOrWhiteSpace(raw))
            {
                sb.AppendLine("## 📥 inbox：（空）");
                return;
            }
            var lines = raw.Replace("\r", "").Split('\n');
            // 條目的判準是**標題行 `## [seq=…]`**，不是「行首有 -」。
            // 🩸 首航踩到：用 `- ` 當判準會把 commit 訊息的 bullet 與身分卡欄位一起數進去
            //   ⇒ 報 42 筆而實際 44 筆條目，**而那個數字看起來完全正常**（判準⑤：名字比事實大）。
            var titles = new List<string>();
            var snippets = new List<string>();
            string curTitle = null;
            string curFirstBody = null;
            // 區塊職責：條目要能判年齡 ⇒ 連 `_at` 那一行一起收（原本它只在跳過清單裡當雜訊）。
            // 物理意義：`_at <ISO UTC>` 是唯一跨房可比的權威時戳；標題列的 `(… +08)` 是本地投影。
            var atLines = new List<string>();
            string curAt = null;
            foreach (var ln in lines)
            {
                if (ln.StartsWith("## [seq="))
                {
                    if (curTitle != null) { titles.Add(curTitle); snippets.Add(curFirstBody ?? ""); atLines.Add(curAt ?? ""); }
                    curTitle = ln.Substring(3).Trim();
                    curFirstBody = null;
                    curAt = null;
                }
                else if (curTitle != null && ln.TrimStart().StartsWith("_at "))
                {
                    curAt = ln.Trim();
                }
                else if (curTitle != null && curFirstBody == null)
                {
                    string t = ln.Trim();
                    if (t.Length > 0 && !t.StartsWith("_at ") && !t.StartsWith(">") && !t.StartsWith("---"))
                        curFirstBody = t.Length > 90 ? t.Substring(0, 90) + "…" : t;
                }
            }
            if (curTitle != null) { titles.Add(curTitle); snippets.Add(curFirstBody ?? ""); atLines.Add(curAt ?? ""); }

            // ═══════════════════════════════════════════════════════════
            // 區塊職責：只顯示 InboxMaxAgeDays 天內的條目（Tim 2026-09-02 拍板）。
            // 物理意義：太久以前的 @ 已經失去意義，但它照樣佔著「待處理」那個數字 ——
            //          實測全庫 732 筆有 425 筆超過 7 天（最舊 116 天）。
            // 數值影響：**折起來不是丟掉** —— 條目原封不動留在 inbox 檔裡，
            //          而且標題那行會把折起的筆數印出來。
            // 🩸 為什麼一定要印：只是不顯示 ＝ 把警報關掉。那些條目不會因為看不見就被處理掉，
            //    而「乾淨的 inbox」與「被藏起來的 inbox」在畫面上完全同形。
            // ⚠ 判準委派給 UCL_ChatTavernQuestIO.IsInboxEntryStale —— 同一把尺只能有一份實作；
            //    這裡自己再 parse 一次就是兩個會各自漂移的真相源。
            // ═══════════════════════════════════════════════════════════
            var nowUtc = System.DateTime.UtcNow;
            var fresh = new List<int>();
            int stale = 0;
            for (int i = 0; i < titles.Count; i++)
            {
                if (UCL_ChatTavernQuestIO.IsInboxEntryStale(atLines[i], nowUtc)) stale++;
                else fresh.Add(i);
            }

            sb.AppendLine($"## 📥 inbox（persona 層）　**{fresh.Count}** 筆待處理"
                + $"（{UCL_ChatTavernQuestIO.InboxMaxAgeDays} 天內）"
                + (fresh.Count > iShow ? $"，以下為最新 {iShow} 筆" : "")
                + (stale > 0 ? $"　·　⚠ 另有 **{stale}** 筆超過 {UCL_ChatTavernQuestIO.InboxMaxAgeDays} 天已折起（仍在 inbox 檔裡，未歸檔）" : ""));
            for (int k = Math.Max(0, fresh.Count - iShow); k < fresh.Count; k++)
            {
                int i = fresh[k];
                sb.AppendLine($"- {titles[i]}");
                if (!string.IsNullOrEmpty(snippets[i])) sb.AppendLine($"    ↳ {snippets[i]}");
            }
            sb.AppendLine();
            sb.AppendLine("　↳ 處理完才歸檔（ack ＝ **已處理**，不是已看過）：`inbox_ack.py --agent " + iPersona + "`");
        }
    }
}
#endif
