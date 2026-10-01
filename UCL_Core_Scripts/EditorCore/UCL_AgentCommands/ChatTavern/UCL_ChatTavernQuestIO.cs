// UCL Chat Tavern — Quest 事件的**讀取**（`events_since` 用）
// 物理意義：酒館任務板（task_* 寫入、reducer、inbox、快照、聊天鏡像）已於 2026-10-01 整組移除（TASK-0364，Tim 拍板）。
//          留下的只有「讀既有事件檔」這一格 —— `Cmd_Tavern op=events_since` 還讀它；既有 `rooms/<room>/events/` 資料留作紀錄。
// ⛔ 本檔不再有寫入端：要恢復寫事件之前，先問那個需求現在是不是 `senate cmd task` 已經做了。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// 一筆 Quest event（events.jsonl 一行一筆）。
    /// 物理意義：append-only 狀態變更日誌；reducer 重放出 task 當前狀態。
    /// </summary>
    public class UCL_QuestEvent
    {
        public int seq;                              // 房間內 quest 事件序號（單調遞增）
        public string ts;                            // ISO 8601 UTC
        public string actor;                         // 觸發者 identity_id
        public string idempotency_key;               // 客戶端自生 uuid，去重用
        public string type;                          // task_create / task_claim / task_progress / task_done / ...
        public string task_id;                       // 該事件作用的 task_id
        public Dictionary<string, string> data;      // type-specific payload（key/val 都字串，輕量）
    }

    /// <summary>Quest 事件的讀取入口（per-event 檔，T38 起的格式）。</summary>
    public static class UCL_ChatTavernQuestIO
    {
        /// <summary>讀 events 全部 — T38 委派 PerMsgFile.LoadAllEvents（walk per-event dir + ts sort + derive seq）。</summary>
        public static List<UCL_QuestEvent> LoadAllEvents(string roomId)
        {
            // T38: 走 per-msg file reader（不再讀 events.jsonl）
            return UCL_ChatTavernIO_PerMsgFile.LoadAllEvents(roomId);
        }

        /// <summary>
        /// 找 <c>"data"</c> 那個值的左大括號位置（回傳 <c>{</c> 的 index；找不到回 -1）。
        /// <para>⚠ 對 key 與 <c>:</c>、<c>:</c> 與 <c>{</c> 之間的**任意空白**不敏感（TASK-0288）。</para>
        /// </summary>
        static int IndexOfDataBrace(string line)
        {
            if (string.IsNullOrEmpty(line)) return -1;
            int k = line.IndexOf("\"data\"");
            if (k < 0) return -1;
            int p = k + "\"data\"".Length;
            while (p < line.Length && char.IsWhiteSpace(line[p])) p++;
            if (p >= line.Length || line[p] != ':') return -1;
            p++;
            while (p < line.Length && char.IsWhiteSpace(line[p])) p++;
            if (p >= line.Length || line[p] != '{') return -1;
            return p;
        }

        public static UCL_QuestEvent ParseEvent(string line)
        {
            // MVP：不做完整 JSON parser；只認預期欄位順序（自家 SerializeEvent 寫出來的）。
            //   非自家寫的 line（手改）可能解析失敗 → 回 null + warning（已被 LoadAllEvents 捕捉）
            var e = new UCL_QuestEvent();
            e.seq = ExtractInt(line, "\"seq\":");
            e.ts = ExtractStr(line, "\"ts\":");
            e.actor = ExtractStr(line, "\"actor\":");
            e.idempotency_key = ExtractStr(line, "\"idempotency_key\":");
            e.type = ExtractStr(line, "\"type\":");
            e.task_id = ExtractStr(line, "\"task_id\":");
            // data 欄位 (optional) — 抽 "data" : { ... } 整段，再 split key/val
            // 🩸 TASK-0288：這裡原本是 IndexOf("\"data\":{") —— 認死「冒號後**緊接**大括號」。
            //   而全庫 668 個 quest 事件檔裡有 **200 檔**寫的是 `"data": {`（多一個空格，另一個寫入端）
            //   ⇒ 找不到 ⇒ data **整包被丟掉**；而 data 是 optional ⇒ **沒有任何一層會叫**，
            //     失效樣子是「那些欄位剛好都是空的」（room `tavern-entry-latency` 的 Role 欄 28 筆全是 `-`）。
            //   ⛔ 修法不是多比對一個帶空格的字面 —— 那只把洞縮小一格，
            //     下一個寫入端換成換行或 tab 就又漏，而它一樣不會叫。
            //   📌 成因可追：本函式檔頭自己寫著「只認自家 SerializeEvent 寫出來的」——
            //     那句話在寫的當下是誠實的，病在**後來多了第二個寫入端**，而那句話沒有跟著失效。
            int dataIdx = IndexOfDataBrace(line);
            if (dataIdx >= 0)
            {
                int start = dataIdx + 1;
                int depth = 1, end = start;
                while (end < line.Length && depth > 0)
                {
                    char c = line[end];
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                    if (depth > 0) end++;
                }
                string inner = line.Substring(start, end - start);
                e.data = ParseSimpleStringDict(inner);
            }
            return e;
        }

        static int ExtractInt(string line, string key)
        {
            int i = line.IndexOf(key);
            if (i < 0) return 0;
            int s = i + key.Length;
            int j = s;
            while (j < line.Length && (char.IsDigit(line[j]) || line[j] == '-')) j++;
            int.TryParse(line.Substring(s, j - s), out var v);
            return v;
        }

        static string ExtractStr(string line, string key)
        {
            int i = line.IndexOf(key);
            if (i < 0) return "";
            int s = line.IndexOf('"', i + key.Length);
            if (s < 0) return "";
            int e = s + 1;
            var sb = new StringBuilder();
            while (e < line.Length)
            {
                char c = line[e];
                if (c == '\\' && e + 1 < line.Length) { sb.Append(UnescapeChar(line[e + 1])); e += 2; continue; }
                if (c == '"') break;
                sb.Append(c); e++;
            }
            return sb.ToString();
        }

        static char UnescapeChar(char c)
        {
            switch (c)
            {
                case 'n': return '\n';
                case 'r': return '\r';
                case 't': return '\t';
                case '"': return '"';
                case '\\': return '\\';
                default: return c;
            }
        }

        static Dictionary<string, string> ParseSimpleStringDict(string inner)
        {
            // 預期格式："k1":"v1","k2":"v2"  — 不處理巢狀 / 數字值（MVP 限 string 值）
            var d = new Dictionary<string, string>();
            int i = 0;
            while (i < inner.Length)
            {
                if (inner[i] != '"') { i++; continue; }
                // key
                i++;
                var sb = new StringBuilder();
                while (i < inner.Length && inner[i] != '"')
                {
                    if (inner[i] == '\\' && i + 1 < inner.Length) { sb.Append(UnescapeChar(inner[i + 1])); i += 2; continue; }
                    sb.Append(inner[i]); i++;
                }
                string key = sb.ToString();
                i++; // skip closing "
                while (i < inner.Length && inner[i] != '"') i++; // skip : and whitespace until value-start "
                if (i >= inner.Length) break;
                i++; // skip opening " of value
                var sb2 = new StringBuilder();
                while (i < inner.Length && inner[i] != '"')
                {
                    if (inner[i] == '\\' && i + 1 < inner.Length) { sb2.Append(UnescapeChar(inner[i + 1])); i += 2; continue; }
                    sb2.Append(inner[i]); i++;
                }
                d[key] = sb2.ToString();
                i++; // skip closing "
                while (i < inner.Length && inner[i] != ',') i++;
                if (i < inner.Length) i++;
            }
            return d;
        }
    }
}
#endif
