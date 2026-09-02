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
        public string priority = "normal";           // high / normal / low（task_create 指定，未指定預設 normal）
        public string group_id;                      // T37 — 邏輯關聯多 task 的 group ID（同 group 全 done 觸發 group_complete event）

        public string status = "pending";            // pending / claimed / in_progress / done / released
        public string owner;                         // claim 後填
        public string lease_until;                   // ISO 8601 UTC
        public string last_progress_summary;
        public string last_progress_at;
        public string created_at;                    // ISO 8601 UTC（task_create event ts）
        public int created_seq;
        public int last_event_seq;
        public int reject_count;                     // 被 reject 退回次數（Phase B 用，本 MVP 預留）

        // 衍生欄位（reducer 第二輪算）：
        public int downstream_weight;                // transitive 下游阻擋的任務數
        public double age_days;                      // 從 created_at 到 now 的天數
        public int age_factor;                       // ceil(age_days / 7)，每 7 天加 1 級優先度
        public bool is_stale;                        // lease_until < now → true
        public List<UCL_QuestEvent> lifecycle = new();  // 此 task 的所有 events，依 seq 排序（task_state op 用）
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

        /// <summary>idempotency 去重：T38 改 walk events/ per-msg dir 看每檔內容；簡易 substring 比對。</summary>
        public static bool HasIdempotencyKey(string roomId, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            // T38: walk events/<date>/*.json，逐檔 substring 比對
            string root = UCL_ChatTavernIO_PerMsgFile.GetEventsRoot(roomId);
            if (!Directory.Exists(root)) return false;
            string needle = $"\"idempotency_key\":\"{key}\"";
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
                {
                    string content = File.ReadAllText(f, Encoding.UTF8);
                    if (content.Contains(needle)) return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Quest] HasIdempotencyKey walk fail: {ex.Message}");
            }
            return false;
        }

        // R6 — 鏡像抑制旗標。Cmd_Tavern.ExecuteAsync 在邊界設置（`quiet=true` arg 觸發），
        //   讓 9 個 AppendEvent call site 不必各自傳參。預設 false（鏡像 on）。
        // 物理意義：static 是因為 Editor 端 Cmd 序列執行（沒並行），生命週期跟 ExecuteAsync 同步即夠
        // 數值影響：true 時 AppendEvent 跳過 MirrorEventToChat；events.jsonl 寫入不變
        public static bool MirrorSuppressed = false;

        /// <summary>append 一筆 event；自動分配 seq + ts；冪等（同 idempotency_key 重複則不寫直接回 -1）。
        /// R6 起：成功 append 後預設自動 mirror 到 messages.jsonl 當 system 訊息（互動感+工作紀錄）；
        /// MirrorSuppressed=true 跳過鏡像；房 meta `disable_quest_mirror=true` 也跳過。</summary>
        public static int AppendEvent(string roomId, UCL_QuestEvent e)
        {
            UCL_ChatTavernIO.EnsureRoomDir(roomId);
            if (!string.IsNullOrEmpty(e.idempotency_key) && HasIdempotencyKey(roomId, e.idempotency_key))
            {
                Debug.Log($"[Quest] idempotency_key 命中，跳過 append: key={e.idempotency_key}");
                return -1;
            }
            // T38: 委派 PerMsgFile.WriteEventFile — 寫單檔 per event；seq 改 reader 動態 derive
            UCL_ChatTavernIO_PerMsgFile.WriteEventFile(roomId, e);
            // 算 derived seq 回傳 caller（兼容既有 op 行為）
            int derivedSeq = LoadAllEvents(roomId).Count;
            e.seq = derivedSeq;
            // 寫 _events_seq.txt 給 caller cache 用
            try
            {
                File.WriteAllText(GetEventsSeqPath(roomId), derivedSeq.ToString(), new UTF8Encoding(false));
            }
            catch { /* fail swallow */ }
            if (!MirrorSuppressed)
            {
                try { MirrorEventToChat(roomId, e); }
                catch (Exception ex) { Debug.LogWarning($"[Quest] MirrorEventToChat 失敗（event 已寫，僅鏡像跳過）：{ex.Message}"); }
            }
            return derivedSeq;
        }

        // ===========================================================
        // R6 — task lifecycle 鏡像到聊天室
        // 區塊職責：每筆關鍵 event 自動寫 system message 到 messages.jsonl，
        //          讓 agent / Tim 在對話流自然看到「夥伴正在動 / 完成了」
        // 物理意義：sender_id="_quest_system"（底線開頭，不會跟真實 agent 撞）；
        //          meta 帶 event_type / task_id / event_seq → 反指 events.jsonl 雙向 trace
        // 數值影響：messages.jsonl 多一筆訊息（無 schema 變動）；不影響 events.jsonl
        // 安全：失敗 throw 由 caller 接（catch 退化 warning，不破 events 寫入）
        // ===========================================================

        const string QuestSystemSenderId = "_quest_system";
        const string QuestSystemSenderName = "Quest";
        const int MirrorBodyMaxLen = 1000;   // body 過長截斷（reason / plan / summary 失控時防爆）；R6.1 放寬到 1000 給「詳細個性化」內容

        /// <summary>對單筆 event 寫一筆 system message 到聊天室；body=null 時表示此 event 不鏡像（如 task_progress 沒 summary）。</summary>
        public static void MirrorEventToChat(string roomId, UCL_QuestEvent e)
        {
            // room meta opt-out
            var room = UCL_ChatTavernIO.LoadRoomMeta(roomId);
            if (room != null && room.disable_quest_mirror) return;

            string body = BuildMirrorBody(roomId, e);
            if (string.IsNullOrEmpty(body)) return;   // null = 此 event 不鏡像

            // body 過長截 + … 後綴（完整內容仍在 events.jsonl）
            if (body.Length > MirrorBodyMaxLen) body = body.Substring(0, MirrorBodyMaxLen - 1) + "…";

            var msg = new UCL_ChatMessage
            {
                sender_id = QuestSystemSenderId,
                sender_name = QuestSystemSenderName,
                kind = "system",
                body = body,
                meta = new Dictionary<string, string>
                {
                    { "event_type", e.type ?? "" },
                    { "task_id", e.task_id ?? "" },
                    { "event_seq", e.seq.ToString() },
                },
            };
            UCL_ChatTavernIO.AppendMessage(roomId, msg);
        }

        /// <summary>按 event type 渲染 system message body。回 null = 不鏡像。</summary>
        static string BuildMirrorBody(string roomId, UCL_QuestEvent e)
        {
            string actor = string.IsNullOrEmpty(e.actor) ? "?" : e.actor;
            string tid = e.task_id ?? "?";
            // 多數 type 需要 task title 才好讀；用 lazy lookup（只在需要時 ComputeTaskStates）
            string LookupTitle()
            {
                try
                {
                    var states = ComputeTaskStates(roomId);
                    if (states.TryGetValue(tid, out var st)) return st.title ?? tid;
                }
                catch { }
                return tid;
            }

            switch (e.type)
            {
                case "task_create":
                    {
                        string title = e.data != null && e.data.TryGetValue("title", out var t) ? t : tid;
                        string priority = e.data != null && e.data.TryGetValue("priority", out var p) ? p : "normal";
                        return $"🆕 {actor} 建任務 `{tid}` — {title}（priority={priority}）";
                    }
                case "task_claim":
                    {
                        string lease = e.data != null && e.data.TryGetValue("lease_until", out var lu) ? lu : "?";
                        string plan = e.data != null && e.data.TryGetValue("plan", out var pl) ? pl : "";
                        // R6.1 — plan 帶就 append 多行；個性化「開始時詳細說明規劃」
                        string head = $"🔒 {actor} 認領 `{tid}`（lease until {lease}）";
                        return string.IsNullOrEmpty(plan) ? head : head + "\n📋 規劃：" + plan;
                    }
                case "task_progress":
                    {
                        // 無 summary 視為「lease 自動展期」之類的純技術事件，不鏡像避免吵
                        string sm = e.data != null && e.data.TryGetValue("summary", out var s) ? s : "";
                        if (string.IsNullOrEmpty(sm)) return null;
                        return $"📈 {actor} 進度更新 `{tid}` — {sm}";
                    }
                case "task_review_request":
                    return $"🔍 {actor} 提交 `{tid}` 給審查";
                case "task_done":
                    {
                        string title = LookupTitle();
                        string summary = e.data != null && e.data.TryGetValue("summary", out var sm) ? sm : "";
                        // R6.1 — summary 帶就 append 多行；鼓勵 actor 用傲嬌語氣詳述工作內容
                        string head = $"✅ {actor} 完成 `{tid}` — {title}";
                        return string.IsNullOrEmpty(summary) ? head : head + "\n💁 " + summary;
                    }
                case "task_reject":
                    {
                        string reason = e.data != null && e.data.TryGetValue("reason", out var r) ? r : "";
                        return string.IsNullOrEmpty(reason)
                            ? $"↩ {actor} 退回 `{tid}`"
                            : $"↩ {actor} 退回 `{tid}` — {reason}";
                    }
                case "task_reopen":
                    {
                        string reason = e.data != null && e.data.TryGetValue("reason", out var r) ? r : "";
                        return string.IsNullOrEmpty(reason)
                            ? $"♻ {actor} 重開 `{tid}`"
                            : $"♻ {actor} 重開 `{tid}` — {reason}";
                    }
                case "task_release":
                    {
                        string reason = e.data != null && e.data.TryGetValue("reason", out var r) ? r : "";
                        return string.IsNullOrEmpty(reason)
                            ? $"🛗 {actor} 放棄 `{tid}`"
                            : $"🛗 {actor} 放棄 `{tid}` — {reason}";
                    }
                case "task_force_reclaim":
                    {
                        string prev = e.data != null && e.data.TryGetValue("previous_owner", out var po) ? po : "?";
                        string reason = e.data != null && e.data.TryGetValue("reason", out var r) ? r : "";
                        return $"⚡ {actor} 接管 `{tid}`（原 owner: {prev}{(string.IsNullOrEmpty(reason) ? "" : "，原因：" + reason)}）";
                    }
                case "group_complete":
                    {
                        // T37 — Quest Group 全 done 觸發；提醒 group owner 寫 friendly summary（不替 agent 寫）
                        string members = e.data != null && e.data.TryGetValue("members", out var mb) ? mb : "?";
                        string trigger = e.data != null && e.data.TryGetValue("trigger_task_id", out var tg) ? tg : "?";
                        return
                            $"🎉 Quest group `{tid}` 全部 task 完成！\n" +
                            $"members: {members}\n" +
                            $"trigger: `{trigger}` by {actor}\n" +
                            $"→ 該 @{actor} 寫 group summary 進 #tavern 主廳了（friendly 同事 standup 風格）";
                    }
                default:
                    return null;   // 未知 type 不鏡像
            }
        }

        /// <summary>讀 events 全部 — T38 委派 PerMsgFile.LoadAllEvents（walk per-event dir + ts sort + derive seq）。</summary>
        public static List<UCL_QuestEvent> LoadAllEvents(string roomId)
        {
            // T38: 走 per-msg file reader（不再讀 events.jsonl）
            return UCL_ChatTavernIO_PerMsgFile.LoadAllEvents(roomId);
        }

        // T38: 既有 jsonl reader 邏輯保留為 dead code stub（R.11 cleanup 時刪）
        static List<UCL_QuestEvent> _LoadAllEvents_Legacy_Jsonl(string roomId)
        {
            var list = new List<UCL_QuestEvent>();
            string p = GetEventsPath(roomId);
            if (!File.Exists(p)) return list;
            string raw = File.ReadAllText(p, Encoding.UTF8);
            int idx = 0;
            while (idx < raw.Length)
            {
                int nl = raw.IndexOf('\n', idx);
                if (nl < 0) break;
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

        /// <summary>
        /// T19 — Stale lease 自動回收（per Antigravity P8 + Tim Round 30）。
        /// 物理意義：claimed task 若 lease 過期且 owner 未 progress / done → 視為 owner 不在，
        ///          自動 task_release（system actor）退回 ready，防多 agent 死鎖永久鎖死。
        /// 數值影響：寫 task_release event 到 events.jsonl（actor=_quest_system，reason 標 auto），留 audit trail。
        ///          idempotency_key 防同一 stale lease 被多次 release（key=auto-release:&lt;task_id&gt;:&lt;lease_until&gt;）。
        /// 觸發：task_list / task_next / task_state 等查詢 op 開頭 lazy 跑（不影響其他 op 性能；查詢本就要 ComputeTaskStates）。
        /// </summary>
        public static int AutoRecoverStaleLeases(string roomId)
        {
            var states = ComputeTaskStatesNoRecover(roomId);
            int recovered = 0;
            foreach (var st in states.Values)
            {
                // 只看 claimed + is_stale；in_progress 也算（task_progress 後若再 stale 同樣處理）
                if (!st.is_stale) continue;
                if (st.status != "claimed" && st.status != "in_progress") continue;
                if (string.IsNullOrEmpty(st.lease_until)) continue;
                // idempotency: 同一 task + 同一 lease_until 只 release 一次
                string idemKey = $"auto-release:{st.id}:{st.lease_until}";
                if (HasIdempotencyKey(roomId, idemKey)) continue;
                var ev = new UCL_QuestEvent
                {
                    type = "task_release",
                    task_id = st.id,
                    actor = QuestSystemSenderId,
                    idempotency_key = idemKey,
                    data = new Dictionary<string, string>
                    {
                        { "reason", $"auto: lease expired at {st.lease_until} — owner {st.owner ?? "?"} 未 progress / done，系統釋放回 ready 防死鎖（T19）" },
                        { "auto_recovery", "true" },
                        { "expired_lease_until", st.lease_until },
                        { "previous_owner", st.owner ?? "" },
                    },
                };
                int seq = AppendEvent(roomId, ev);
                if (seq > 0)
                {
                    recovered++;
                    Debug.Log($"[Quest T19] auto-released stale lease：task={st.id} prev_owner={st.owner} lease_until={st.lease_until}");
                }
            }
            return recovered;
        }

        /// <summary>內部：算 states 但不觸發 stale recovery（避免遞迴）。</summary>
        static Dictionary<string, UCL_QuestTaskState> ComputeTaskStatesNoRecover(string roomId)
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
                st.lifecycle.Add(e);
                ApplyEvent(st, e);
            }
            ComputeDerivedFields(states);
            return states;
        }

        /// <summary>從 events.jsonl 重放算出 room 內所有 task 的當前狀態（含衍生欄位 downstream_weight / age / is_stale）。</summary>
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
                st.lifecycle.Add(e);
                ApplyEvent(st, e);
            }
            // 第二輪：算衍生欄位
            ComputeDerivedFields(states);
            return states;
        }

        static void ApplyEvent(UCL_QuestTaskState st, UCL_QuestEvent e)
        {
            switch (e.type)
            {
                case "task_create":
                    st.status = "pending";
                    st.created_seq = e.seq;
                    st.created_at = e.ts;
                    if (e.data != null)
                    {
                        if (e.data.TryGetValue("title", out var t)) st.title = t;
                        if (e.data.TryGetValue("role", out var r)) st.role = r;
                        if (e.data.TryGetValue("suggested_owner", out var so)) st.suggested_owner = so;
                        if (e.data.TryGetValue("priority", out var pr)) st.priority = pr;
                        if (e.data.TryGetValue("group_id", out var gid)) st.group_id = gid;   // T37 Quest Group
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
                case "task_release":
                    // 主動放棄：status 退回 pending，清 owner / lease，但 reason 留 lifecycle
                    st.status = "pending";
                    st.owner = null;
                    st.lease_until = null;
                    break;
                case "task_review_request":
                    // owner 提交 review；status: in_progress → review
                    st.status = "review";
                    break;
                case "task_reject":
                    // reviewer 退回；reject_count++；status: review → in_progress（owner 不換）
                    st.reject_count++;
                    st.status = "in_progress";
                    break;
                case "task_reopen":
                    // 已 done 的 task 被發現有問題；status: done → in_progress
                    // owner 沿用上次（不換人）；MVP 用，免走完整 review 流程
                    st.status = "in_progress";
                    break;
                case "task_force_reclaim":
                    // 強制接管（原 owner stale）：owner ← 新 claimer; status ← claimed; lease 重設
                    // 物理意義：lease 過期未展期視為原 owner 不在，新人接手繼續做
                    st.status = "claimed";
                    st.owner = e.actor;
                    if (e.data != null && e.data.TryGetValue("lease_until", out var luF)) st.lease_until = luF;
                    break;
                // 之後加 task_block / task_unblock / task_split 等
            }
        }

        /// <summary>算衍生欄位 — downstream_weight / age_days / age_factor / is_stale。</summary>
        static void ComputeDerivedFields(Dictionary<string, UCL_QuestTaskState> states)
        {
            DateTime now = DateTime.UtcNow;
            // age + is_stale
            foreach (var st in states.Values)
            {
                if (DateTime.TryParse(st.created_at, out var ca))
                    st.age_days = (now - ca.ToUniversalTime()).TotalDays;
                st.age_factor = (int)Math.Ceiling(st.age_days / 7.0);
                if (!string.IsNullOrEmpty(st.lease_until) && DateTime.TryParse(st.lease_until, out var lu))
                {
                    st.is_stale = lu.ToUniversalTime() < now && st.status != "done";
                }
            }
            // downstream_weight: BFS 從每個 task 出發，算阻擋多少下游
            foreach (var src in states.Values)
            {
                var visited = new HashSet<string>();
                var queue = new Queue<string>();
                foreach (var kv in states)
                {
                    if (kv.Value.depends_on.Contains(src.id)) queue.Enqueue(kv.Key);
                }
                while (queue.Count > 0)
                {
                    string t = queue.Dequeue();
                    if (!visited.Add(t)) continue;
                    foreach (var kv in states)
                    {
                        if (kv.Value.depends_on.Contains(t) && !visited.Contains(kv.Key)) queue.Enqueue(kv.Key);
                    }
                }
                src.downstream_weight = visited.Count;
            }
        }

        /// <summary>Cycle detection — 新 task X 的 depends_on 不能 transitive 到自己。</summary>
        public static bool HasCycle(string roomId, string newTaskId, List<string> dependsOn)
        {
            if (dependsOn == null || dependsOn.Count == 0) return false;
            var states = ComputeTaskStates(roomId);
            // DFS：從每個 dep 出發，看能不能走到 newTaskId
            foreach (var dep in dependsOn)
            {
                if (dep == newTaskId) return true; // 自己依賴自己
                var visited = new HashSet<string>();
                if (DfsReaches(dep, newTaskId, states, visited)) return true;
            }
            return false;
        }

        static bool DfsReaches(string from, string target, Dictionary<string, UCL_QuestTaskState> states, HashSet<string> visited)
        {
            if (from == target) return true;
            if (!visited.Add(from)) return false;
            if (!states.TryGetValue(from, out var st)) return false;
            // 反向：找誰依賴 from（即 from 阻擋 X => 走到 X）
            // 但 cycle 檢查要走 forward direction：from depends_on 的 → 它的 depends_on...
            foreach (var d in st.depends_on)
            {
                if (DfsReaches(d, target, states, visited)) return true;
            }
            return false;
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
        // 區塊職責：衍生快照重生 — quest.md + checklist.md
        // 物理意義：每筆改 events 的 op 結尾自動跑；events 是 truth，cache 隨時可重生
        // 數值影響：< 5ms per call (events <100 + serialize markdown)
        // ===========================================================

        public static string GetQuestDashboardPath(string roomId)
            => Path.Combine(UCL_ChatTavernIO.GetRoomDir(roomId), "quest.md");
        public static string GetChecklistPath(string roomId)
            => Path.Combine(UCL_ChatTavernIO.GetRoomDir(roomId), "checklist.md");

        /// <summary>重生 quest.md (full DAG dashboard) + checklist.md (簡潔勾選表)。</summary>
        public static void RebuildSnapshots(string roomId)
        {
            try
            {
                var states = ComputeTaskStates(roomId);
                WriteQuestDashboard(roomId, states);
                WriteChecklist(roomId, states);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Quest] RebuildSnapshots failed: {ex.Message}");
            }
        }

        static void WriteQuestDashboard(string roomId, Dictionary<string, UCL_QuestTaskState> states)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# 🏛 Quest Dashboard — {roomId}");
            sb.AppendLine();
            sb.AppendLine($"_衍生 cache，由 events.jsonl 重生；最後更新 {NowUtcIsoLocal()}_");
            sb.AppendLine();
            int done = 0, ready = 0, claimed = 0, inprog = 0, pending = 0, stale = 0;
            foreach (var s in states.Values)
            {
                if (s.is_stale) stale++;
                switch (s.status)
                {
                    case "done": done++; break;
                    case "claimed": claimed++; break;
                    case "in_progress": inprog++; break;
                    case "pending":
                        if (IsReady(s, states)) ready++; else pending++;
                        break;
                }
            }
            sb.AppendLine($"## 統計");
            sb.AppendLine($"- 總 task: {states.Count} | done: {done} | in_progress: {inprog} | claimed: {claimed} | ready: {ready} | pending(blocked): {pending} | **stale: {stale}**");
            sb.AppendLine();
            sb.AppendLine("## Tasks");
            sb.AppendLine();
            sb.AppendLine("| ID | Status | Owner | Role | Priority | DownW | Age | Deps | Last Progress |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            // 排序：status sort key (in_progress=0 / claimed=1 / ready=2 / pending=3 / done=4)，再 priority desc，再 downstream desc
            var sorted = new List<UCL_QuestTaskState>(states.Values);
            sorted.Sort((a, b) =>
            {
                int sa = StatusSortKey(a, states), sb2 = StatusSortKey(b, states);
                if (sa != sb2) return sa - sb2;
                int pa = PriorityScore(a), pb = PriorityScore(b);
                if (pa != pb) return pb - pa;
                return b.downstream_weight - a.downstream_weight;
            });
            foreach (var s in sorted)
            {
                string es = s.status == "pending" && IsReady(s, states) ? "ready" : s.status;
                if (s.is_stale) es += " (stale)";
                string deps = s.depends_on.Count > 0 ? string.Join(",", s.depends_on) : "-";
                string lp = string.IsNullOrEmpty(s.last_progress_summary) ? "-" : Truncate(s.last_progress_summary, 40);
                sb.AppendLine($"| `{s.id}` | {es} | {s.owner ?? "-"} | {s.role ?? "-"} | {s.priority} | {s.downstream_weight} | {s.age_days:F1}d | {deps} | {lp} |");
            }
            sb.AppendLine();
            File.WriteAllText(GetQuestDashboardPath(roomId), sb.ToString(), new UTF8Encoding(false));
        }

        static void WriteChecklist(string roomId, Dictionary<string, UCL_QuestTaskState> states)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# ✅ Checklist — {roomId}");
            sb.AppendLine();
            sb.AppendLine($"_衍生 cache；最後更新 {NowUtcIsoLocal()}_");
            sb.AppendLine();
            // 拓樸排序近似：先建好的（created_seq 小）在前
            var sorted = new List<UCL_QuestTaskState>(states.Values);
            sorted.Sort((a, b) => a.created_seq - b.created_seq);
            foreach (var s in sorted)
            {
                string mark;
                switch (s.status)
                {
                    case "done": mark = "✅"; break;
                    case "in_progress": mark = "🚧"; break;
                    case "claimed": mark = "🔒"; break;
                    case "pending": mark = IsReady(s, states) ? "🟢" : "⏳"; break;
                    default: mark = "⚪"; break;
                }
                if (s.is_stale) mark = "🔴" + mark;
                string owner = string.IsNullOrEmpty(s.owner) ? "" : $" (owner: {s.owner})";
                sb.AppendLine($"- {mark} **{s.id}** {s.title}{owner}");
            }
            File.WriteAllText(GetChecklistPath(roomId), sb.ToString(), new UTF8Encoding(false));
        }

        static int StatusSortKey(UCL_QuestTaskState s, Dictionary<string, UCL_QuestTaskState> all)
        {
            switch (s.status)
            {
                case "in_progress": return 0;
                case "claimed": return 1;
                case "pending": return IsReady(s, all) ? 2 : 3;
                case "done": return 4;
                default: return 5;
            }
        }

        public static int PriorityScore(UCL_QuestTaskState s)
        {
            int baseScore = s.priority switch { "high" => 100, "low" => 0, _ => 50 };
            return baseScore + s.age_factor; // 每 7 天加 1 級（饑餓緩解）
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max - 3) + "...";
        }

        static string NowUtcIsoLocal() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");

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

        /// <summary>append 一筆 inbox entry（任務 unblock / handoff 等通知）。
        /// T21 — 寫入後若 entry 數 > InboxCapMax → 自動 trim 舊的搬到 _archive.md，留最新 InboxCapKeep。</summary>
        // 版面沿革（**兩層，別只讀一層**）：
        //   2026-07-29 Tim 拍板精簡：時間併進標題列、**拿掉**獨立的 `_at` 行 —— 每條省一行，
        //     掃 inbox 時同螢幕能多看幾條；時間改本機時區（跟 Editor log / Discord 對得起來）。
        //   2026-08-15 basecamp：`_at` 行**加回來**，與標題列的本地時間並存。
        //     ⚠ 加回來不是推翻那次精簡的判斷 —— 精簡當時沒有任何東西讀那一行，它確實只是版面。
        //     是後來通知水位要換成時間戳，才發現**那次拿掉的正好是唯一可當判準的欄位**：
        //     標題列那個是本地時區、秒精度、可再生的**投影**，跨房不可比。
        //     ⇒ 現在兩者職責分開：標題列給人看、`_at` 給機器判。
        // 條目仍以 "## [seq=" 起首 —— tavern_catchup.read_inbox_entries 與
        // inbox_ack.count_mentions 都錨定這個 prefix，不可改。
        public static void AppendInbox(string roomId, string agentId, int eventSeq, string title, string body)
        {
            EnsureInboxDir(roomId);
            string aPath = GetInboxPath(roomId, agentId);
            var sb = new StringBuilder();
            if (!File.Exists(aPath))
            {
                sb.AppendLine($"> 📥 **{agentId}** 的 inbox — 新到最舊由上往下 append。時間為**本機時區**。");
                sb.AppendLine($"> 處理完跑 `inbox_ack.py` 歸檔；要看被截斷的全文跑 `tavern_query.py seq <N> --full`。");
            }
            sb.AppendLine();
            // 時區顯式標註（crest-001 QA 2026-07-29）：inbox 內新舊格式會並存一段時間，
            // 舊條目是 UTC 的 `_at ...Z_`、新條目是本機時間 — 不標偏移量就會有人把 14:47 當 UTC 讀。
            sb.AppendLine($"## [seq={eventSeq}] {title} ({System.DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zz})");
            // 區塊職責：權威時間戳 —— 通知已讀水位唯一可比較的欄位。
            // 物理意義：標題列那個 `(… +08)` 是**本地時區、秒精度、可再生**的投影，不可當判準；
            //          這一行是 UTC 毫秒，跨房可比。⇒ 兩者並存：一個給人看，一個給機器判。
            // 數值影響：seq 是 **per-room 編號**（tavern 15000+ vs 側房 109），拿它當跨房水位
            //          會讓側房的 @ 永遠算不出「新的」—— 那是永久靜默不是延遲
            //          （2026-08-15 實測 163 筆看不見的 @，六個 persona 全中）。
            // ⚠ 這一行**曾經存在**，2026-07-29 的版面精簡把它併進標題列時拿掉了；
            //   那次精簡拿掉的正好是唯一能當水位的欄位，代價七個月後才現形。加回來而不是另發明格式，
            //   是因為 tavern_catchup 的跳過清單早就認得 `_at `，wake_brief / inbox_ack 只讀標題列
            //   ⇒ 三個 parser 一支都不用改。既有條目由 inbox_ts_backfill.py 回填（681 筆）。
            sb.AppendLine($"_at {System.DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}_");
            if (!string.IsNullOrEmpty(body)) { sb.AppendLine(); sb.AppendLine(body); }
            File.AppendAllText(aPath, sb.ToString(), new UTF8Encoding(false));
            // T21 — append 後 lazy trim
            try { TrimInboxIfOverCap(roomId, agentId); }
            catch (Exception ex) { Debug.LogWarning($"[Quest T21] inbox trim 失敗（容忍，append 已完成）：{ex.Message}"); }
        }

        // ===========================================================
        // T21 — Inbox cap=50 自動 trim + archive（per Antigravity P4 / Tim Round 30）
        // 物理意義：inbox 累積爆量 → re-enter 看 inbox 時 prompt 被舊待辦塞滿；
        //          保留最新 InboxCapKeep 條，舊的搬到 inbox/<id>_archive.md 永久保存（append-only）。
        // 數值影響：寫 archive + 重寫 inbox（保留 truncated marker + 最新 N 條）；不丟資料。
        // 切點：以 "## " 行為單位 split entry（既有 AppendInbox 寫法都用此 prefix）。
        // ===========================================================
        const int InboxCapMax = 50;     // 觸發 trim 門檻
        const int InboxCapKeep = 50;    // trim 後保留最新 N 條

        // ═══════════════════════════════════════════════════════════
        // 區塊職責：inbox 條目的**年齡**上限 —— 數量之外的第二把尺（Tim 2026-09-02 拍板）。
        // 物理意義：cap=50 是「數量」的閘，而它在目前流量下**恰好**約等於一週 ——
        //          恰好不是設計。低頻／已退場的收件匣流量小，於是 50 條可以躺 111 天
        //          （實測：`claude-da-xiaojie` 38/50 超過 7 天、最舊 116 天）。
        //          太久以前的 @ 已經失去意義，卻照樣佔著「待處理」那個數字。
        // 數值影響：搬到 `<id>_archive.md`（append-only）⇒ **不丟資料、可逆**；
        //          inbox 只留最近 InboxMaxAgeDays 天。
        // ⚠ 這支是 append 時的 lazy trim ⇒ **沒有人 append 的死信箱不會自己瘦**
        //   （側房 18 個、合計 61 筆，最後訊息日全在 2026-05）。那半要靠掃描式清理，
        //   不在本函式的射程內 —— 寫在這裡是為了讓下一個人不必自己去發現。
        // ═══════════════════════════════════════════════════════════
        public const int InboxMaxAgeDays = 7;

        /// <summary>這筆 inbox 條目是不是「太舊」。⚠ 拿不到時戳一律當**舊**。</summary>
        /// <remarks>
        /// 判準來源是條目裡的 `_at &lt;ISO UTC&gt;_` 行 —— 那是唯一跨房可比的權威時戳
        /// （標題列的 `(… +08)` 是本地時區投影，不可當判準）。
        /// 🩸 為什麼「拿不到＝舊」是安全的：inbox 條目**只有** AppendInbox 一支在寫，
        ///    而它那行是無條件寫的 ⇒ 任何新條目必有 `_at`。缺它的只有 2026-08-15
        ///    回填之前的舊格式殘留（實測：缺 `_at` 的條目最新一筆是 2026-08-14，
        ///    而 2026-08-17 之後的條目 100% 有）。
        ///    反過來設（拿不到當新）的後果是那些條目**永遠**留在窗內 —— 窗等於沒開，
        ///    而且不會有任何一行字說出來。
        /// </remarks>
        public static bool IsInboxEntryStale(string entry, System.DateTime nowUtc, int maxAgeDays = InboxMaxAgeDays)
        {
            var m = System.Text.RegularExpressions.Regex.Match(entry, @"(?m)^_at (\S+?)Z?_\s*$");
            if (!m.Success) return true;
            if (!System.DateTime.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var ts)) return true;
            return (nowUtc - ts).TotalDays > maxAgeDays;
        }

        static void TrimInboxIfOverCap(string roomId, string agentId)
        {
            string inboxPath = GetInboxPath(roomId, agentId);
            if (!File.Exists(inboxPath)) return;
            string raw = File.ReadAllText(inboxPath, Encoding.UTF8);
            if (string.IsNullOrEmpty(raw)) return;
            // 切 entry：每個以 "## " 開頭（行首）
            var entries = SplitInboxEntries(raw);
            if (entries.Count == 0) return;

            // ① 數量閘：超過 InboxCapMax 才動，保留最新 InboxCapKeep 條
            int countCut = entries.Count > InboxCapMax ? entries.Count - InboxCapKeep : 0;

            // ② 年齡閘：從最舊往新掃，連續的「太舊」前綴一律歸檔（條目是 append 序 ⇒ 前綴即最舊）
            //   entries[0] 可能是檔頭（不以 "## " 開頭）—— 它跟著前綴走，但**不能自己驅動**歸檔，
            //   否則沒有任何舊條目時每次 append 都在搬那行 header。
            int ageCut = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (!entries[i].StartsWith("## ")) { ageCut = i + 1; continue; }
                if (!IsInboxEntryStale(entries[i], System.DateTime.UtcNow)) break;
                ageCut = i + 1;
            }
            if (ageCut > 0 && !entries[ageCut - 1].StartsWith("## ")) ageCut = 0;   // 只有 header ⇒ 不算

            int archiveCount = System.Math.Max(countCut, ageCut);
            if (archiveCount <= 0) return;
            string why = countCut >= ageCut
                ? (ageCut > 0 ? $"數量 >{InboxCapMax} 且有 >{InboxMaxAgeDays} 天的" : $"數量 >{InboxCapMax}")
                : $">{InboxMaxAgeDays} 天";
            var archive = new StringBuilder();
            for (int i = 0; i < archiveCount; i++) archive.Append(entries[i]);
            // append archive (preserve order)
            string archivePath = inboxPath.Replace(".md", "_archive.md");
            File.AppendAllText(archivePath, archive.ToString(), new UTF8Encoding(false));

            // rewrite inbox：truncated marker + 最新 InboxCapKeep
            var newInbox = new StringBuilder();
            newInbox.AppendLine($"> ⚠ **inbox truncated** — {archiveCount} 條較舊待辦已歸檔到 `{Path.GetFileName(archivePath)}`（規則：{why}；{UCL_ChatTavernIO.NowUtcIso()}）");
            newInbox.AppendLine();
            for (int i = archiveCount; i < entries.Count; i++) newInbox.Append(entries[i]);
            File.WriteAllText(inboxPath, newInbox.ToString(), new UTF8Encoding(false));
            Debug.Log($"[Quest T21] inbox trim {agentId}@{roomId}: archived {archiveCount} entries（{why}）→ {Path.GetFileName(archivePath)}; 剩 {entries.Count - archiveCount} 條");
        }

        /// <summary>把 inbox 內容切成 entries — 每個 entry 以 line "## " 為起點，包含到下個 "## " 之前。</summary>
        static List<string> SplitInboxEntries(string raw)
        {
            var entries = new List<string>();
            using var sr = new StringReader(raw);
            string line;
            var current = new StringBuilder();
            // header（檔案開頭 to 第一個 "## "）視為 entry 0；之後每個 "## " 起新 entry
            bool seenHeader = false;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.StartsWith("## "))
                {
                    if (current.Length > 0)
                    {
                        entries.Add(current.ToString());
                        current.Clear();
                    }
                    seenHeader = true;
                }
                current.AppendLine(line);
            }
            if (current.Length > 0) entries.Add(current.ToString());
            // 若沒看過 "## " — 整檔當 entry 0（header only），不 trim
            if (!seenHeader) return new List<string>();
            return entries;
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
