// UCL Chat Tavern — Quest Workflow IO 層 (MVP A)
// 在 Tavern 房間下加 events.jsonl + tasks/ + inbox/ 支援多階段任務協作
// 設計詳見 Docs~/zh-Hant/Workflows/Quest_Workflow.md
//
// MVP 範圍：6 個 op (task_create / task_claim / task_progress / task_done / task_list / inbox_read)
// MVP 簡化：depth=1（不 split）、lease 寫入但不 force_reclaim、衍生 quest.md 手動重生
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

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

    /// <summary>
    /// Reducer 算出來的 task 當前狀態（純衍生，不持久化）。
    /// </summary>
    public class UCL_QuestTaskState
    {
        public string id;
        public string title;
        public string role;                          // architect / programmer / art / translator / qa / ...
        public List<string> depends_on = new();
        public string suggested_owner;               // task_create 時可指定（誰最適合）

        public string status = "pending";            // pending / claimed / in_progress / done
        public string owner;                         // claim 後填
        public string lease_until;                   // ISO 8601 UTC
        public string last_progress_summary;
        public string last_progress_at;
        public int created_seq;
        public int last_event_seq;
    }

    /// <summary>
    /// Quest IO + reducer。負責：
    ///   - events.jsonl 讀寫（append-only + 行尾 `\n` 完整性檢查）
    ///   - idempotency 去重
    ///   - tasks/<id>.md 規格檔讀寫
    ///   - inbox/<agent>.md handoff queue
    ///   - 從 events 重放出每個 task 的當前狀態
    /// </summary>
    public static class UCL_ChatTavernQuestIO
    {
        public const string EventsFile = "events.jsonl";
        public const string EventsSeqFile = "_events_seq.txt";

        public static string GetEventsPath(string roomId)
            => Path.Combine(UCL_ChatTavernIO.GetRoomDir(roomId), EventsFile);
        public static string GetEventsSeqPath(string roomId)
            => Path.Combine(UCL_ChatTavernIO.GetRoomDir(roomId), EventsSeqFile);
        public static string GetTasksDir(string roomId)
            => Path.Combine(UCL_ChatTavernIO.GetRoomDir(roomId), "tasks");
        public static string GetTaskSpecPath(string roomId, string taskId)
            => Path.Combine(GetTasksDir(roomId), $"{taskId}.md");
        public static string GetInboxDir(string roomId)
            => Path.Combine(UCL_ChatTavernIO.GetRoomDir(roomId), "inbox");
        public static string GetInboxPath(string roomId, string agentId)
            => Path.Combine(GetInboxDir(roomId), $"{agentId}.md");

        // ===========================================================
        // 區塊職責：events.jsonl append + 完整性檢查
        // 物理意義：append-only audit log；行尾 `\n` 完整則該行有效
        // ===========================================================

        static int IncrementEventsSeq(string roomId)
        {
            UCL_ChatTavernIO.EnsureRoomDir(roomId);
            int cur = 0;
            string p = GetEventsSeqPath(roomId);
            if (File.Exists(p))
            {
                int.TryParse(File.ReadAllText(p, Encoding.UTF8).Trim(), out cur);
            }
            int next = cur + 1;
            File.WriteAllText(p, next.ToString(), new UTF8Encoding(false));
            return next;
        }

        /// <summary>idempotency 去重：events.jsonl 掃過一遍找有沒有同 key（MVP 階段 O(N) 夠用）。</summary>
        public static bool HasIdempotencyKey(string roomId, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            string p = GetEventsPath(roomId);
            if (!File.Exists(p)) return false;
            // 簡易 substring 比對：events 行 JSON 必含 "idempotency_key":"<key>"
            string needle = $"\"idempotency_key\":\"{key}\"";
            using var sr = new StreamReader(p, Encoding.UTF8);
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.Contains(needle)) return true;
            }
            return false;
        }

        /// <summary>append 一筆 event；自動分配 seq + ts；冪等（同 idempotency_key 重複則不寫直接回 -1）。</summary>
        public static int AppendEvent(string roomId, UCL_QuestEvent e)
        {
            UCL_ChatTavernIO.EnsureRoomDir(roomId);
            if (!string.IsNullOrEmpty(e.idempotency_key) && HasIdempotencyKey(roomId, e.idempotency_key))
            {
                Debug.Log($"[Quest] idempotency_key 命中，跳過 append: key={e.idempotency_key}");
                return -1;
            }
            e.seq = IncrementEventsSeq(roomId);
            if (string.IsNullOrEmpty(e.ts)) e.ts = UCL_ChatTavernIO.NowUtcIso();
            string line = SerializeEvent(e) + "\n";
            File.AppendAllText(GetEventsPath(roomId), line, new UTF8Encoding(false));
            return e.seq;
        }

        /// <summary>讀 events.jsonl 全部（partial-line 自動 skip）。</summary>
        public static List<UCL_QuestEvent> LoadAllEvents(string roomId)
        {
            var list = new List<UCL_QuestEvent>();
            string p = GetEventsPath(roomId);
            if (!File.Exists(p)) return list;
            // 先讀全文，按 \n 切分；最後一段若沒結尾 \n 視為 partial（crash mid-write） → 丟
            string raw = File.ReadAllText(p, Encoding.UTF8);
            int idx = 0;
            while (idx < raw.Length)
            {
                int nl = raw.IndexOf('\n', idx);
                if (nl < 0) break; // partial line at end → skip
                string line = raw.Substring(idx, nl - idx);
                idx = nl + 1;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var ev = ParseEvent(line);
                    if (ev != null) list.Add(ev);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Quest] skipping malformed event line: {ex.Message}");
                }
            }
            return list;
        }

        // ===========================================================
        // 區塊職責：reducer — 重放 events 算出每 task 當前狀態
        // 物理意義：events 是 truth；任何時刻可以重放；task state 純衍生不持久
        // ===========================================================

        /// <summary>從 events.jsonl 重放算出 room 內所有 task 的當前狀態。</summary>
        public static Dictionary<string, UCL_QuestTaskState> ComputeTaskStates(string roomId)
        {
            var states = new Dictionary<string, UCL_QuestTaskState>();
            var events = LoadAllEvents(roomId);
            foreach (var e in events)
            {
                if (string.IsNullOrEmpty(e.task_id)) continue;
                if (!states.TryGetValue(e.task_id, out var st))
                {
                    st = new UCL_QuestTaskState { id = e.task_id };
                    states[e.task_id] = st;
                }
                st.last_event_seq = e.seq;
                ApplyEvent(st, e);
            }
            return states;
        }

        static void ApplyEvent(UCL_QuestTaskState st, UCL_QuestEvent e)
        {
            switch (e.type)
            {
                case "task_create":
                    st.status = "pending";
                    st.created_seq = e.seq;
                    if (e.data != null)
                    {
                        if (e.data.TryGetValue("title", out var t)) st.title = t;
                        if (e.data.TryGetValue("role", out var r)) st.role = r;
                        if (e.data.TryGetValue("suggested_owner", out var so)) st.suggested_owner = so;
                        if (e.data.TryGetValue("depends_on", out var deps))
                        {
                            st.depends_on = string.IsNullOrEmpty(deps) ? new List<string>()
                                : new List<string>(deps.Split(','));
                        }
                    }
                    break;
                case "task_claim":
                    st.status = "claimed";
                    st.owner = e.actor;
                    if (e.data != null && e.data.TryGetValue("lease_until", out var lu)) st.lease_until = lu;
                    break;
                case "task_progress":
                    st.status = "in_progress";
                    st.last_progress_at = e.ts;
                    if (e.data != null && e.data.TryGetValue("summary", out var sm)) st.last_progress_summary = sm;
                    if (e.data != null && e.data.TryGetValue("lease_until", out var lu2)) st.lease_until = lu2;
                    break;
                case "task_done":
                    st.status = "done";
                    break;
                // MVP 之外的 event types (block / unblock / review / split) 在 Phase B+ 補
            }
        }

        /// <summary>判斷 task 是否 ready (status=pending 且所有 deps done)。</summary>
        public static bool IsReady(UCL_QuestTaskState st, Dictionary<string, UCL_QuestTaskState> all)
        {
            if (st.status != "pending") return false;
            foreach (var dep in st.depends_on)
            {
                if (!all.TryGetValue(dep, out var depSt) || depSt.status != "done") return false;
            }
            return true;
        }

        // ===========================================================
        // 區塊職責：tasks/<id>.md 任務規格檔（write-once + frontmatter）
        // 物理意義：任務「內容真相」— event 只放狀態指標，內文走獨立檔
        // ===========================================================

        public static void EnsureTasksDir(string roomId)
        {
            string d = GetTasksDir(roomId);
            if (!Directory.Exists(d)) Directory.CreateDirectory(d);
        }

        public static void WriteTaskSpec(string roomId, string taskId, string title, string role, List<string> dependsOn, string body)
        {
            EnsureTasksDir(roomId);
            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"task_id: {taskId}");
            sb.AppendLine($"title: {title ?? ""}");
            if (!string.IsNullOrEmpty(role)) sb.AppendLine($"role: {role}");
            if (dependsOn != null && dependsOn.Count > 0)
            {
                sb.AppendLine($"depends_on: [{string.Join(", ", dependsOn)}]");
            }
            sb.AppendLine($"created_at: {UCL_ChatTavernIO.NowUtcIso()}");
            sb.AppendLine("---");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(body)) sb.AppendLine(body);
            File.WriteAllText(GetTaskSpecPath(roomId, taskId), sb.ToString(), new UTF8Encoding(false));
        }

        public static string ReadTaskSpec(string roomId, string taskId)
        {
            string p = GetTaskSpecPath(roomId, taskId);
            if (!File.Exists(p)) return null;
            return File.ReadAllText(p, Encoding.UTF8);
        }

        // ===========================================================
        // 區塊職責：inbox/<agent>.md — 給特定 agent 的待處理事項
        // 物理意義：handoff 專用；agent re-enter 第一站
        // ===========================================================

        public static void EnsureInboxDir(string roomId)
        {
            string d = GetInboxDir(roomId);
            if (!Directory.Exists(d)) Directory.CreateDirectory(d);
        }

        /// <summary>append 一筆 inbox entry（任務 unblock / handoff 等通知）。</summary>
        public static void AppendInbox(string roomId, string agentId, int eventSeq, string title, string body)
        {
            EnsureInboxDir(roomId);
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine($"## [seq={eventSeq}] {title}");
            sb.AppendLine($"_at {UCL_ChatTavernIO.NowUtcIso()}_");
            if (!string.IsNullOrEmpty(body)) { sb.AppendLine(); sb.AppendLine(body); }
            File.AppendAllText(GetInboxPath(roomId, agentId), sb.ToString(), new UTF8Encoding(false));
        }

        public static string ReadInbox(string roomId, string agentId)
        {
            string p = GetInboxPath(roomId, agentId);
            if (!File.Exists(p)) return "(inbox 為空)";
            return File.ReadAllText(p, Encoding.UTF8);
        }

        /// <summary>task_done 後算下游 unblock 並寫 inbox（給 suggested_owner）。</summary>
        public static void NotifyDownstreamUnblock(string roomId, string completedTaskId, int triggerSeq)
        {
            var all = ComputeTaskStates(roomId);
            foreach (var kv in all)
            {
                var st = kv.Value;
                if (!st.depends_on.Contains(completedTaskId)) continue;
                if (!IsReady(st, all)) continue;
                string target = !string.IsNullOrEmpty(st.suggested_owner) ? st.suggested_owner : "any";
                if (target == "any") continue; // 沒指定 owner 就不寫 inbox（MVP）
                string title = $"{st.id} ready (deps {completedTaskId} done)";
                string body = $"spec: tasks/{st.id}.md\nsuggested_action: task_claim {st.id}";
                AppendInbox(roomId, target, triggerSeq, title, body);
            }
        }

        // ===========================================================
        // 區塊職責：Event JSON 序列化 / 解析（手寫 minimal — JsonUtility 不支援 Dict）
        // ===========================================================

        public static string SerializeEvent(UCL_QuestEvent e)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"seq\":").Append(e.seq);
            sb.Append(",\"ts\":\"").Append(EscapeStr(e.ts)).Append("\"");
            sb.Append(",\"actor\":\"").Append(EscapeStr(e.actor ?? "")).Append("\"");
            sb.Append(",\"idempotency_key\":\"").Append(EscapeStr(e.idempotency_key ?? "")).Append("\"");
            sb.Append(",\"type\":\"").Append(EscapeStr(e.type ?? "")).Append("\"");
            sb.Append(",\"task_id\":\"").Append(EscapeStr(e.task_id ?? "")).Append("\"");
            if (e.data != null && e.data.Count > 0)
            {
                sb.Append(",\"data\":{");
                bool first = true;
                foreach (var kv in e.data)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("\"").Append(EscapeStr(kv.Key)).Append("\":\"").Append(EscapeStr(kv.Value ?? "")).Append("\"");
                }
                sb.Append("}");
            }
            sb.Append("}");
            return sb.ToString();
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
            // data 欄位 (optional) — 簡易抽取 "data":{...} 整段，再 split key/val
            int dataIdx = line.IndexOf("\"data\":{");
            if (dataIdx >= 0)
            {
                int start = dataIdx + "\"data\":{".Length;
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

        static string EscapeStr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
#endif
