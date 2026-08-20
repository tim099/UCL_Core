#if UNITY_EDITOR
// ===========================================================
// 區塊職責：酒館訊息的**查詢與呈現**層 —— 唯一一份「怎麼撈、怎麼印」的實作。
// 物理意義：底下的 IO（`UCL_ChatTavernIO` / `UCL_ChatTavernIO_PerMsgFile`）負責「訊息在哪、怎麼讀」，
//          本層負責「篩哪些、排怎樣、印成什麼」。兩層刻意分開：IO 有快取與檔案格式的責任，
//          而呈現會常改，混在一起會讓改版面要動到讀檔路徑。
// 為什麼是 static class 而不是寫在 Cmd 裡（Tim 2026-08-20 拍板）：
//          Cmd 是**入口**不是**實作**。同一份查詢邏輯會被 Cmd_Tavern、後台頁、
//          catchup 服務共用；放進 Cmd 的話第二個呼叫端只能複製一份，
//          而兩份查詢對同一個房間給出不同答案時，兩邊都不會報錯。
// 🩸 本層取代 `AgentCommands/Tools/tavern_query.py`（2026-08-20）——
//          python 端原本自帶一份 per-message 走訪與一份顯示名稱解析，
//          後者在合一遷移後查的是已廢棄的 identities.json（見 BUG-25 家族）。
// 數值影響：**純讀**。不寫任何檔、不動游標、不碰金流。
// ===========================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    public static class UCL_TavernQueryService
    {
        // 掃描上限：跨房查詢時每房最多讀這麼多則。
        // 物理意義：酒館單房已有兩萬則以上，全 parse 會讓一次查詢跑數十秒。
        // ⚠ 命中上限時**必須在輸出裡說**（見 TruncationNote）——
        //   「掃到上限後停」與「就這麼多」在結果上長得一模一樣，而前者會關掉下一個人的搜尋。
        const int SCAN_PER_ROOM = 4000;

        public const int DefaultBodyClip = 200;

        // ===========================================================
        // 區塊職責：時間窗口字串（`24h` / `7d` / `90m`）→ UTC 起點。
        // 數值影響：回傳 null 代表「不限時間」；解析失敗一律當 null 並在輸出標明，
        //          不要靜默套用預設值（那會讓人以為自己的 --since 生效了）。
        // ===========================================================
        public static DateTime? ParseSince(string iSince, out string oNote)
        {
            oNote = null;
            string s = (iSince ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(s)) return null;
            var m = Regex.Match(s, @"^(\d+)\s*([smhd])$");
            if (!m.Success)
            {
                oNote = $"⚠ `since={iSince}` 解析不了（格式：`90m` / `24h` / `7d`）—— **本次未套用時間窗口**";
                return null;
            }
            int n = int.Parse(m.Groups[1].Value);
            switch (m.Groups[2].Value)
            {
                case "s": return DateTime.UtcNow.AddSeconds(-n);
                case "m": return DateTime.UtcNow.AddMinutes(-n);
                case "h": return DateTime.UtcNow.AddHours(-n);
                default: return DateTime.UtcNow.AddDays(-n);
            }
        }

        static DateTime ParseTs(string iTs)
        {
            if (DateTime.TryParse(iTs, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) return t;
            return DateTime.MinValue;
        }

        static bool InWindow(UCL_ChatMessage m, DateTime? iSince)
            => iSince == null || ParseTs(m?.ts).ToUniversalTime() >= iSince.Value;

        // 顯示名稱：**唯一真相源是帳戶資料**（Tim 2026-08-20；identities.json 已廢棄）。
        // 訊息落盤時已經算過一次，這裡優先用落盤值 —— 歷史訊息要保留它當時的署名，
        // 不該因為今天有人改了顯示名就整段 history 跟著變。
        static string SenderLabel(UCL_ChatMessage m)
        {
            string name = m?.sender_name;
            if (string.IsNullOrEmpty(name))
                name = Treasury.UCL_BankAccountProfileIO.GetDisplayName(m?.sender_id);
            if (string.IsNullOrEmpty(name)) name = m?.sender_id ?? "?";
            string persona = m?.sender_persona;
            return string.IsNullOrEmpty(persona) ? name : $"{name}@{persona}";
        }

        static string Clip(string iBody, int iMax)
        {
            string b = (iBody ?? "").Replace("\r", "").Replace("\n", " ⏎ ").Trim();
            if (iMax <= 0 || b.Length <= iMax) return b;
            return b.Substring(0, iMax) + $"…（截斷，全文 {b.Length} 字）";
        }

        static string LocalHm(string iTs)
        {
            var t = ParseTs(iTs);
            return t == DateTime.MinValue ? "??:??:??" : t.ToLocalTime().ToString("MM-dd HH:mm:ss");
        }

        static void AppendMsgLine(StringBuilder sb, UCL_ChatMessage m, string iRoom, int iClip)
        {
            string tag = (m?.meta != null && m.meta.TryGetValue("tag", out var tg) && !string.IsNullOrEmpty(tg))
                ? $" «{tg}»" : "";
            string room = string.IsNullOrEmpty(iRoom) ? "" : $" [{iRoom}]";
            sb.AppendLine($"- **[seq {m.seq}]** {LocalHm(m.ts)}{room} **{SenderLabel(m)}**{tag}");
            sb.AppendLine($"    {Clip(m.body, iClip)}");
        }

        // ⚠ 判準是「**有沒有某一房掃到頂**」，不是「總則數有沒有超過上限」。
        // 🩸 首航當場踩到：59 房加總 5014 則就觸發警告，而沒有任何一房接近 4000 ——
        //   一個永遠會亮的警告等於沒有警告，而它宣稱的「清單不完整」是假的（判準⑤：名字比事實大）。
        static string TruncationNote(List<string> iCappedRooms)
        {
            if (iCappedRooms == null || iCappedRooms.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"⚠ **這些房間掃到上限（每房 {SCAN_PER_ROOM} 則）**："
                + string.Join("、", iCappedRooms.ToArray())
                + " —— 更舊的訊息沒有進入本次比對。這份清單**不完整**，縮小 `since` 或指定 `room` 再查一次。");
            return sb.ToString();
        }

        // ===========================================================
        // 區塊職責：取一批要參與查詢的訊息（跨房或單房）。
        // 數值影響：oScanned 是實際讀進來的則數 —— 呈報口徑用，別省。
        // ===========================================================
        static List<KeyValuePair<string, UCL_ChatMessage>> Collect(
            string iRoom, DateTime? iSince, out int oScanned, out int oRoomCount, out List<string> oCapped)
        {
            var result = new List<KeyValuePair<string, UCL_ChatMessage>>();
            oScanned = 0;
            oCapped = new List<string>();
            var rooms = new List<string>();
            if (!string.IsNullOrEmpty(iRoom)) rooms.Add(iRoom);
            else rooms.AddRange(UCL_ChatTavernIO.EnumerateRoomIds());
            oRoomCount = rooms.Count;

            foreach (var r in rooms)
            {
                List<UCL_ChatMessage> batch;
                try { batch = UCL_ChatTavernIO.Tail(r, SCAN_PER_ROOM); }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogWarning($"[TavernQuery] 讀房間 `{r}` 失敗：{e.Message}");
                    continue;
                }
                oScanned += batch.Count;
                if (batch.Count >= SCAN_PER_ROOM) oCapped.Add($"`{r}`");
                foreach (var m in batch)
                    if (InWindow(m, iSince)) result.Add(new KeyValuePair<string, UCL_ChatMessage>(r, m));
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Value?.ts ?? "", b.Value?.ts ?? ""));
            return result;
        }

        // ===========================================================
        // op=query kind=rooms —— 房間清單 ＋ 最後活動
        // ===========================================================
        public static string Rooms(string iSince)
        {
            var since = ParseSince(iSince, out string note);
            var sb = new StringBuilder();
            sb.AppendLine($"# 🍻 酒館房間　窗口 `{iSince ?? "(不限)"}`");
            if (note != null) sb.AppendLine(note);
            sb.AppendLine();
            var ids = UCL_ChatTavernIO.EnumerateRoomIds();
            sb.AppendLine($"共 **{ids.Count}** 房：");
            foreach (var r in ids)
            {
                var tail = UCL_ChatTavernIO.Tail(r, 1);
                var last = tail.Count > 0 ? tail[0] : null;
                int total = UCL_ChatTavernIO.CountMessages(r);
                bool active = last != null && InWindow(last, since);
                sb.AppendLine(last == null
                    ? $"- `{r}`　{total} 則　（無訊息）"
                    : $"- {(active ? "🟢" : "⚪")} `{r}`　{total} 則　最後 {LocalHm(last.ts)}　{SenderLabel(last)}");
            }
            return sb.ToString();
        }

        // ===========================================================
        // op=query kind=tail —— 單房最後 N 則
        // ===========================================================
        public static string Tail(string iRoom, int iLimit, int iClip = DefaultBodyClip)
        {
            string room = string.IsNullOrEmpty(iRoom) ? "tavern" : iRoom;
            int limit = iLimit <= 0 ? 20 : iLimit;
            var msgs = UCL_ChatTavernIO.Tail(room, limit);
            var sb = new StringBuilder();
            sb.AppendLine($"# 🍻 `{room}` 最後 {msgs.Count} 則（要求 {limit}）");
            sb.AppendLine();
            foreach (var m in msgs) AppendMsgLine(sb, m, null, iClip);
            return sb.ToString();
        }

        // ===========================================================
        // op=query kind=search —— 關鍵字（預設不分大小寫）
        // ===========================================================
        public static string Search(string iKeyword, string iRoom, string iSince,
                                    bool iCaseSensitive, int iLimit, int iClip = DefaultBodyClip)
        {
            if (string.IsNullOrEmpty(iKeyword)) return "❌ search 需要 `keyword=`";
            var since = ParseSince(iSince, out string note);
            var all = Collect(iRoom, since, out int scanned, out int roomCount, out var capped);
            var cmp = iCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var hits = new List<KeyValuePair<string, UCL_ChatMessage>>();
            foreach (var kv in all)
                if ((kv.Value?.body ?? "").IndexOf(iKeyword, cmp) >= 0) hits.Add(kv);

            int limit = iLimit <= 0 ? 30 : iLimit;
            var sb = new StringBuilder();
            sb.AppendLine($"# 🔍 `{iKeyword}`　命中 **{hits.Count}**　"
                        + $"（掃 {scanned} 則／{roomCount} 房／窗口 `{iSince ?? "不限"}`"
                        + $"／{(iCaseSensitive ? "大小寫敏感" : "不分大小寫")}）");
            if (note != null) sb.AppendLine(note);
            sb.Append(TruncationNote(capped));
            if (hits.Count > limit)
                sb.AppendLine($"\n⚠ 只列最新 {limit} 筆（命中 {hits.Count}）—— 這是**顯示上限，不是命中數**。\n");
            sb.AppendLine();
            for (int i = Math.Max(0, hits.Count - limit); i < hits.Count; i++)
                AppendMsgLine(sb, hits[i].Value, hits[i].Key, iClip);
            return sb.ToString();
        }

        // ===========================================================
        // op=query kind=by_sender
        // ===========================================================
        public static string BySender(string iSenderId, string iSince, int iLimit, int iClip = DefaultBodyClip)
        {
            if (string.IsNullOrEmpty(iSenderId)) return "❌ by_sender 需要 `sender=`";
            var since = ParseSince(iSince, out string note);
            var all = Collect(null, since, out int scanned, out int roomCount, out var capped);
            var hits = new List<KeyValuePair<string, UCL_ChatMessage>>();
            foreach (var kv in all)
            {
                var m = kv.Value;
                bool hit = string.Equals(m?.sender_id, iSenderId, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(m?.sender_persona, iSenderId, StringComparison.OrdinalIgnoreCase);
                if (hit) hits.Add(kv);
            }
            int limit = iLimit <= 0 ? 20 : iLimit;
            var sb = new StringBuilder();
            sb.AppendLine($"# 👤 `{iSenderId}`　{hits.Count} 則"
                        + $"（掃 {scanned} 則／{roomCount} 房／窗口 `{iSince ?? "不限"}`）");
            sb.AppendLine("　比對 `sender_id` 與 `sender_persona` 兩欄 —— 合一之後兩者是不同的名字。");
            if (note != null) sb.AppendLine(note);
            sb.Append(TruncationNote(capped));
            sb.AppendLine();
            for (int i = Math.Max(0, hits.Count - limit); i < hits.Count; i++)
                AppendMsgLine(sb, hits[i].Value, hits[i].Key, iClip);
            return sb.ToString();
        }

        // ===========================================================
        // op=query kind=timeline —— 跨房時序流
        // ===========================================================
        public static string Timeline(string iSince, int iLimit, int iClip = DefaultBodyClip)
        {
            var since = ParseSince(iSince, out string note);
            var all = Collect(null, since, out int scanned, out int roomCount, out var capped);
            int limit = iLimit <= 0 ? 30 : iLimit;
            var sb = new StringBuilder();
            sb.AppendLine($"# ⏱ 跨房時序　{all.Count} 則（掃 {scanned}／{roomCount} 房／窗口 `{iSince ?? "不限"}`）");
            if (note != null) sb.AppendLine(note);
            sb.Append(TruncationNote(capped));
            sb.AppendLine();
            for (int i = Math.Max(0, all.Count - limit); i < all.Count; i++)
                AppendMsgLine(sb, all[i].Value, all[i].Key, iClip);
            return sb.ToString();
        }

        // ===========================================================
        // op=query kind=stats —— 訊息數統計（依房、依 sender）
        // ===========================================================
        public static string Stats(string iSince)
        {
            var since = ParseSince(iSince, out string note);
            var all = Collect(null, since, out int scanned, out int roomCount, out var capped);
            var byRoom = new Dictionary<string, int>();
            var bySender = new Dictionary<string, int>();
            foreach (var kv in all)
            {
                byRoom.TryGetValue(kv.Key, out int rc); byRoom[kv.Key] = rc + 1;
                string s = SenderLabel(kv.Value);
                bySender.TryGetValue(s, out int sc); bySender[s] = sc + 1;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"# 📊 統計　窗口 `{iSince ?? "不限"}`　共 **{all.Count}** 則"
                        + $"（掃 {scanned} 則／{roomCount} 房）");
            if (note != null) sb.AppendLine(note);
            sb.Append(TruncationNote(capped));
            sb.AppendLine("\n## 依房間");
            var rooms = new List<KeyValuePair<string, int>>(byRoom);
            rooms.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (var kv in rooms) sb.AppendLine($"- `{kv.Key}`　**{kv.Value}**");
            sb.AppendLine("\n## 依發話者");
            var senders = new List<KeyValuePair<string, int>>(bySender);
            senders.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (var kv in senders) sb.AppendLine($"- {kv.Key}　**{kv.Value}**");
            return sb.ToString();
        }

        // ===========================================================
        // op=query kind=seq —— 依 seq／範圍／篩選撈訊息（對齊 op=read 的 canonical seq）
        // 數值影響：seq 是**檔序位**，與 op=read／實錄匯出同一把尺。
        // ===========================================================
        public static string Seq(string iRoom, int iSeq, int iFrom, int iTo, int iLast,
                                 string iPersona, string iSender, string iTag, string iGrep,
                                 bool iFull)
        {
            string room = string.IsNullOrEmpty(iRoom) ? "tavern" : iRoom;
            List<UCL_ChatMessage> pool;
            string scope;
            if (iSeq > 0) { pool = UCL_ChatTavernIO.Range(room, iSeq, iSeq); scope = $"seq {iSeq}"; }
            else if (iFrom > 0 && iTo >= iFrom) { pool = UCL_ChatTavernIO.Range(room, iFrom, iTo); scope = $"seq {iFrom}-{iTo}"; }
            else { pool = UCL_ChatTavernIO.Tail(room, SCAN_PER_ROOM); scope = $"最後 {SCAN_PER_ROOM} 則內"; }

            Regex grep = null;
            if (!string.IsNullOrEmpty(iGrep))
            {
                try { grep = new Regex(iGrep, RegexOptions.IgnoreCase); }
                catch (Exception e) { return $"❌ grep regex 不合法：{e.Message}"; }
            }

            var hits = new List<UCL_ChatMessage>();
            foreach (var m in pool)
            {
                if (!string.IsNullOrEmpty(iPersona)
                    && !string.Equals(m?.sender_persona, iPersona, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(iSender)
                    && (m?.sender_id ?? "").IndexOf(iSender, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!string.IsNullOrEmpty(iTag))
                {
                    string tg = (m?.meta != null && m.meta.TryGetValue("tag", out var t)) ? t : "";
                    if ((tg ?? "").IndexOf(iTag, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                if (grep != null && !grep.IsMatch(m?.body ?? "")) continue;
                hits.Add(m);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# 🔢 `{room}` {scope}　篩後 **{hits.Count}** 則");
            var filters = new List<string>();
            if (!string.IsNullOrEmpty(iPersona)) filters.Add($"persona=`{iPersona}`");
            if (!string.IsNullOrEmpty(iSender)) filters.Add($"sender~`{iSender}`");
            if (!string.IsNullOrEmpty(iTag)) filters.Add($"tag~`{iTag}`");
            if (grep != null) filters.Add($"grep=`{iGrep}`");
            if (filters.Count > 0) sb.AppendLine("　篩選：" + string.Join("　", filters.ToArray()));
            int take = iLast > 0 ? iLast : hits.Count;
            if (take < hits.Count) sb.AppendLine($"　只列最後 {take} 筆（**顯示上限，不是命中數**）");
            sb.AppendLine();
            for (int i = Math.Max(0, hits.Count - take); i < hits.Count; i++)
                AppendMsgLine(sb, hits[i], null, iFull ? 0 : DefaultBodyClip);
            return sb.ToString();
        }
    }
}
#endif
