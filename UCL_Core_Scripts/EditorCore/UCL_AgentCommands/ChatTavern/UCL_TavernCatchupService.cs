#if UNITY_EDITOR
// ===========================================================
// 區塊職責：自由時間換骰裡的「酒館未讀」那一段（AppendUnreadSection）。
// 物理意義：叮／早安的 catch-up 簡報（在線一覽＋未讀＋inbox）**已搬進 SCP_Core `SCP_TavernCatchup`**
//          （TASK-0303：Senate 的 morning-catchup 與 Editor 的 `Tavern op=catchup` 呼叫同一份）。
//          本檔只剩自由時間用的這一段 —— 它吃 Editor 的 UCL_ChatMessage，還沒搬。
// 🩸 **「已讀到哪」只准有一個寫入端**：游標是 read-modify-write，多個寫入端各自讀舊值再寫回
//          ⇒ 後寫的吃掉前一次的推進，失效樣子是「有幾則訊息再也不會出現在任何人的未讀裡」。
//          ⇒ 推游標一律走 UCL_TavernCursor.WriteCursor（它再轉呼叫 SCP_TavernCursor，帶跨 process 鎖）。
// ⚠ 順序不可反：**先寫進 ioR、再推游標**。反過來的話回傳檔寫入失敗時訊息已被標成已讀。
// 數值影響：唯一的寫入是推進游標。不發訊息、不記帳、不動金流。
// ===========================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    public static class UCL_TavernCatchupService
    {
        // ===========================================================
        // 區塊職責：自由時間換骰裡的「酒館未讀」段。
        // ⚠ 判準（排除自己／系統廣播、@ 自己用長截斷）與 SCP_TavernCatchup 同一套 —— 改一邊要改另一邊，
        //   直到這一段也搬進 SCP_Core 為止。
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
                // 標記不是裝飾：不標的話「同一份清單裡有的長有的短」看起來像截斷壞掉，而其實是規則在生效。
                AppendMsg(ioR, m, mentioned ? "🔔 **@你**" : "", mentioned ? iBodyClipMentioned : iBodyClip);
            }

            if (!iAdvance || string.IsNullOrEmpty(oNewestTs)) return;
            UCL_TavernCursor.WriteCursor(iPersona, oNewestTs);
            string back = UCL_TavernCursor.ReadCursor(iPersona);
            ioR.AppendLine(back == oNewestTs
                ? $"- ✓ 已讀游標推進到 `{back}`（寫入後讀回確認）"
                : $"- ✗ 游標寫入後讀回不符：期望 `{oNewestTs}`、實際 `{back}`");
        }

        // 判準：body 裡出現 `@<persona>`（不分大小寫）；刻意不認顯示名或 agent 名（那兩個會變）。
        static bool MentionsMe(UCL_ChatMessage m, string iPersona)
        {
            if (string.IsNullOrEmpty(iPersona)) return false;
            string body = m?.body ?? "";
            return body.IndexOf("@" + iPersona, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsMine(UCL_ChatMessage m, string iPersona)
            => !string.IsNullOrEmpty(iPersona)
               && string.Equals(m?.sender_persona, iPersona, StringComparison.OrdinalIgnoreCase);

        // 系統廣播＝酒保代發的自動訊息。判準走 sender_id，不看內容關鍵字（會吃掉同事**談論**打款的訊息）。
        static bool IsSystem(UCL_ChatMessage m)
        {
            string s = m?.sender_id ?? "";
            return string.Equals(s, "tavern-keeper", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "subconscious-daemon", StringComparison.OrdinalIgnoreCase);
        }

        static void AppendMsg(StringBuilder sb, UCL_ChatMessage m, string iMark, int iClip)
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
    }
}
#endif
