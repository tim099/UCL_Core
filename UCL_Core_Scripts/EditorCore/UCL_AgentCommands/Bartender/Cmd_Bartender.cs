// 區塊職責：Cmd_Bartender — agent RPC 介面, 管理留言觸發 + 時間規則
// 物理意義：agent 透過 queue.json 呼叫此 Cmd → 修改 bartender 系統的 trigger / time_rule 資料
//          UCL_BartenderDaemon (常駐) 會 pick up 變更, 不必手動重啟
// 設計取捨：op 分派模式, 對齊 Cmd_Tavern 慣例 (單一 CommandType, 內部 sub-op dispatch)
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Text;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;

namespace UCL.Core.EditorLib.AgentCommands.Bartender
{
    /// <summary>
    /// 酒保管理指令 — op 分派式.
    /// 子操作:
    ///   add         新增留言 trigger
    ///   list        列當前所有 trigger
    ///   remove      移除指定 trigger
    ///   time_add    新增時間規則
    ///   time_list   列時間規則
    ///   time_remove 移除時間規則
    ///   status      列 daemon state / 統計
    ///   tick        手動強制 tick (testing)
    /// </summary>
    public class Cmd_Bartender : UCL_AgentCommandHandlerBase
    {
        public override string CommandType => "Bartender";
        public override string ShortDescription => "酒保系統 — 留言觸發 + 時間規則管理";

        public override string ArgsSchema =>
@"op=<sub-op> 派遣式. 子 op 與參數:

[add]    新增留言 trigger
  creator=<sender_id>   留言者 (e.g. Zeta-da-xiaojie)
  creator_name=<name>   留言者顯示名 (e.g. Zeta大小姐), 空 → 用 creator
  targets=<list>        目標 (逗號分隔, 空 = 任何人), e.g. 'Zeta,crest-001'
  key=<keyword>         觸發關鍵字 (case-insensitive substring)
  msg=<message>         留言內容
  tokens=<int>          token 預算 (= 觸發次數), 預設 1
  room=<room_id>        目標 room, 預設 tavern

[list]   列當前 triggers (room 可選 filter)

[remove] 移除 trigger
  id=<trigger_id>       要移除的 trigger id

[time_add] 新增時間規則
  id=<rule_id>          規則 id (人類可讀)
  time=<HH:mm>          時間 (24-hour, e.g. 23:50)
  msg=<reminder>        提醒訊息

[time_list]   列時間規則

[time_remove] 移除時間規則
  id=<rule_id>

[status]      列 daemon 統計 + state file 概況

[tick]        強制立刻 tick (測試 / dogfood 用)

[notify_scan] 自動通知掃描診斷 — 回答「通知池為什麼是空的」(逐人判定 + 逐房 inbox 分解)
  ⚠ **純觀測**: 不寫 state / 不發告警 / 不戳任何人, 查幾次都不改變系統

[balance]     查詢 Treasury 帳戶餘額 + 最近 N 筆進出帳 (C# 原生查詢, 餘額一律走 CMD)
  account=<id>          要查的 account (e.g. claude-da-xiaojie / Tim)
  limit=<int>           近期進出帳筆數, 預設 10 (cap 100)
  post=<true/false>     是否同時 post 到 tavern (預設 false, 只寫 _last_op.md)
  room=<room_id>        post=true 時的目標 room, 預設 tavern";

        public override string ExampleArgs =>
            "op=add;creator=Zeta-da-xiaojie;targets=Zeta;key=叮;msg=請進入自由意志模式;tokens=2";

        public override async UniTask ExecuteAsync(Dictionary<string, string> args, CancellationToken token)
        {
            await UniTask.SwitchToMainThread();
            string op = GetArg(args, "op", "").ToLowerInvariant();
            try
            {
                switch (op)
                {
                    case "add":           Op_Add(args); break;
                    case "list":          Op_List(args); break;
                    case "remove":        Op_Remove(args); break;
                    case "time_add":      Op_TimeAdd(args); break;
                    case "time_list":     Op_TimeList(args); break;
                    case "time_remove":   Op_TimeRemove(args); break;
                    case "status":        Op_Status(args); break;
                    case "tick":          Op_Tick(args); break;
                    case "notify_scan":   Op_NotifyScan(args); break;
                    case "balance":       Op_Balance(args); break;
                    // T06.2 — Plan_Standby_Dispatch_Bartender task dispatch (Pull MVP)
                    case "assign_add":    Op_AssignAdd(args); break;
                    case "assign_list":   Op_AssignList(args); break;
                    case "assign_remove": Op_AssignRemove(args); break;
                    case "assign_ack":    Op_AssignAck(args); break;
                    default:
                        WriteLastOp($"❌ 未知 op='{op}', 支援: add / list / remove / time_add / time_list / time_remove / status / tick / notify_scan / balance / assign_add / assign_list / assign_remove / assign_ack");
                        break;
                }
            }
            catch (Exception e)
            {
                WriteLastOp($"❌ Cmd_Bartender exception: {e.Message}\n{e.StackTrace}");
                Debug.LogWarning($"[Cmd_Bartender] op={op} fail: {e}");
            }
        }

        // ===========================================================
        // op=notify_scan — 自動通知掃描診斷（純觀測）
        // 物理意義：後台面板只有人坐在 Editor 前才看得到；遠端多視窗協作卡住時人常常不在場。
        //          這個 op 讓 agent 自己查「我被 @ 了為什麼沒被戳」，答案落在 _last_op.md。
        // ===========================================================
        void Op_NotifyScan(Dictionary<string, string> args)
        {
#if UNITY_STANDALONE_WIN
            WriteLastOp(UCL_RemoteNotifyService.BuildDiagnosticReport());
#else
            WriteLastOp("❌ notify_scan 只在 Windows Editor 可用（遠端視窗協作是 Win32 API）");
#endif
        }

        // ===========================================================
        // op=add — 新增留言 trigger
        // ===========================================================
        void Op_Add(Dictionary<string, string> args)
        {
            string creator = GetArg(args, "creator", "");
            string creatorName = GetArg(args, "creator_name", creator);
            string keyword = GetArg(args, "key", "");
            string message = GetArg(args, "msg", "");
            string targetsRaw = GetArg(args, "targets", "");
            string room = GetArg(args, "room", "tavern");
            int tokens = ParseInt(GetArg(args, "tokens", "1"), 1);

            if (string.IsNullOrEmpty(creator)) { WriteLastOp("❌ add 缺 creator"); return; }
            if (string.IsNullOrEmpty(keyword)) { WriteLastOp("❌ add 缺 key (關鍵字)"); return; }
            if (string.IsNullOrEmpty(message)) { WriteLastOp("❌ add 缺 msg (留言內容)"); return; }
            if (tokens < 1) tokens = 1;

            var targets = new List<string>();
            if (!string.IsNullOrEmpty(targetsRaw))
            {
                foreach (var t in targetsRaw.Split(','))
                {
                    string trimmed = t.Trim();
                    if (!string.IsNullOrEmpty(trimmed)) targets.Add(trimmed);
                }
            }

            // 走 shared register helper (跟 daemon inline parser 共用同底層)
            string id = UCL_BartenderIO.RegisterTrigger(
                creator, creatorName, targets, keyword, message, tokens, room);

            string targetsDisplay = targets.Count == 0 ? "(任何人)" : string.Join(", ", targets);
            WriteLastOp(
                $"✅ Bartender trigger 新增成功\n\n" +
                $"- id: `{id}`\n" +
                $"- creator: {creator} ({creatorName})\n" +
                $"- targets: {targetsDisplay}\n" +
                $"- keyword: `{keyword}`\n" +
                $"- tokens: {tokens} (= 可觸發 {tokens} 次)\n" +
                $"- room: {room}\n" +
                $"- message: {Truncate(message, 100)}");
        }

        // ===========================================================
        // op=list — 列當前 triggers
        // ===========================================================
        void Op_List(Dictionary<string, string> args)
        {
            string roomFilter = GetArg(args, "room", "");
            var data = UCL_BartenderIO.LoadTriggers();
            if (data.triggers.Count == 0)
            {
                WriteLastOp("📭 目前沒有任何 Bartender trigger.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# 📋 Bartender Triggers ({data.triggers.Count} 筆)\n");
            sb.AppendLine("| id | creator | targets | keyword | remaining/initial | room | created_at |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var t in data.triggers)
            {
                if (t == null) continue;
                if (!string.IsNullOrEmpty(roomFilter) && t.target_room != roomFilter) continue;
                string targetsDisplay = (t.targets == null || t.targets.Count == 0) ? "*" : string.Join(",", t.targets);
                sb.AppendLine($"| `{t.id}` | {t.creator_id} | {targetsDisplay} | `{t.keyword}` | {t.remaining_triggers}/{t.initial_tokens} | {t.target_room} | {t.created_at} |");
            }
            WriteLastOp(sb.ToString());
        }

        // ===========================================================
        // op=remove — 移除 trigger
        // ===========================================================
        void Op_Remove(Dictionary<string, string> args)
        {
            string id = GetArg(args, "id", "");
            if (string.IsNullOrEmpty(id)) { WriteLastOp("❌ remove 缺 id"); return; }
            var data = UCL_BartenderIO.LoadTriggers();
            int removed = data.triggers.RemoveAll(t => t != null && t.id == id);
            UCL_BartenderIO.SaveTriggers(data);
            WriteLastOp(removed > 0
                ? $"✅ 移除 trigger `{id}` ({removed} 筆)"
                : $"⚠ 沒找到 trigger `{id}` (可能已被 daemon 用完自動移除 / 拼錯)");
        }

        // ===========================================================
        // op=time_add — 新增時間規則
        // ===========================================================
        void Op_TimeAdd(Dictionary<string, string> args)
        {
            string id = GetArg(args, "id", "");
            string time = GetArg(args, "time", "");
            string msg = GetArg(args, "msg", "");
            string room = GetArg(args, "room", "tavern");

            if (string.IsNullOrEmpty(id)) { WriteLastOp("❌ time_add 缺 id"); return; }
            if (string.IsNullOrEmpty(time)) { WriteLastOp("❌ time_add 缺 time (HH:mm)"); return; }
            if (string.IsNullOrEmpty(msg)) { WriteLastOp("❌ time_add 缺 msg"); return; }

            // 走 shared register helper
            UCL_BartenderIO.RegisterTimeRule(id, time, msg, room);

            WriteLastOp(
                $"✅ Time rule `{id}` 新增/覆寫\n\n" +
                $"- 時間: {time} (local)\n" +
                $"- room: {room}\n" +
                $"- msg: {Truncate(msg, 100)}");
        }

        void Op_TimeList(Dictionary<string, string> args)
        {
            var data = UCL_BartenderIO.LoadTimeRules();
            if (data.rules.Count == 0) { WriteLastOp("📭 目前沒有任何時間規則."); return; }
            var sb = new StringBuilder();
            sb.AppendLine($"# ⏰ Bartender Time Rules ({data.rules.Count} 筆)\n");
            sb.AppendLine("| id | time | room | enabled |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var r in data.rules)
            {
                if (r == null) continue;
                sb.AppendLine($"| `{r.id}` | {r.time_hhmm} | {r.target_room} | {r.enabled} |");
            }
            WriteLastOp(sb.ToString());
        }

        void Op_TimeRemove(Dictionary<string, string> args)
        {
            string id = GetArg(args, "id", "");
            if (string.IsNullOrEmpty(id)) { WriteLastOp("❌ time_remove 缺 id"); return; }
            var data = UCL_BartenderIO.LoadTimeRules();
            int removed = data.rules.RemoveAll(r => r != null && r.id == id);
            UCL_BartenderIO.SaveTimeRules(data);
            WriteLastOp(removed > 0 ? $"✅ 移除 time rule `{id}`" : $"⚠ 沒找到 time rule `{id}`");
        }

        // ===========================================================
        // op=status — daemon state 統計
        // ===========================================================
        void Op_Status(Dictionary<string, string> args)
        {
            var triggers = UCL_BartenderIO.LoadTriggers();
            var rules = UCL_BartenderIO.LoadTimeRules();
            var state = UCL_BartenderIO.LoadState();

            int activeTriggers = 0, totalRemaining = 0;
            foreach (var t in triggers.triggers)
            {
                if (t != null && t.remaining_triggers > 0)
                {
                    activeTriggers++;
                    totalRemaining += t.remaining_triggers;
                }
            }

            int activeRules = 0;
            foreach (var r in rules.rules)
                if (r != null && r.enabled) activeRules++;

            var sb = new StringBuilder();
            sb.AppendLine("# 🍻 Bartender Daemon Status\n");
            sb.AppendLine($"- triggers: {activeTriggers} active, {totalRemaining} total token budget remaining");
            sb.AppendLine($"- time rules: {activeRules} active");
            sb.AppendLine($"- state.last_updated: {state.last_updated ?? "(never)"}");
            sb.AppendLine($"- fired_today_keys: {(state.fired_today_keys?.Count ?? 0)} 筆");
            sb.AppendLine($"- room_last_seq tracked: {(state.room_last_seq?.Count ?? 0)} 房間");
            sb.AppendLine();
            sb.AppendLine("Bartender daemon 內 Editor 自動跑 (EditorApplication.update tick, 每 5s 一次).");
            sb.AppendLine("檔案: `AgentCommands/ChatTavern/bartender/{triggers,time_rules,state}.json`");
            WriteLastOp(sb.ToString());
        }

        void Op_Tick(Dictionary<string, string> args)
        {
            UCL_BartenderDaemon.ForceTick();
            WriteLastOp("✅ Bartender daemon forced tick (檢查 trigger + time rule 一輪).");
        }

        // ===========================================================
        // op=balance — 查 Treasury 帳戶餘額 + 最近 N 筆進出帳
        // 物理意義：CMD path 對稱於 inline [查詢餘額] — 都走 daemon 的 RunBalanceQuery（2026-08-17 起
        //          已是 C# 原生查詢，`balance_query.py` 已刪除；餘額查詢統一走 CMD，不再 spawn python）
        // 設計取捨：預設只寫 _last_op.md (供 caller 看), post=true 才同步 post 到 tavern (酒保身分)
        // ===========================================================
        void Op_Balance(Dictionary<string, string> args)
        {
            string account = GetArg(args, "account", "");
            int limit = ParseInt(GetArg(args, "limit", "10"), 10);
            bool post = GetArg(args, "post", "false").ToLowerInvariant() == "true";
            string room = GetArg(args, "room", "tavern");

            if (string.IsNullOrEmpty(account))
            {
                WriteLastOp("❌ balance 缺 account (要查的 Treasury 帳戶 id)");
                return;
            }
            if (limit < 0) limit = 0;
            if (limit > 100) limit = 100;

            string result = UCL_BartenderDaemon.RunBalanceQueryPublic(account, limit, out string err);
            if (result == null)
            {
                WriteLastOp($"❌ balance 查詢失敗 (account=`{account}`): {err}");
                return;
            }
            WriteLastOp(result);

            // 同步 post 到 tavern (酒保身分) — opt-in, 對齊 inline [查詢餘額] 行為
            if (post)
            {
                var msg = new UCL_ChatMessage
                {
                    sender_id = UCL_BartenderDaemon.TavernKeeperId,
                    sender_name = "酒保",
                    kind = "chat",
                    body = $"💰 **餘額查詢結果** (CMD op=balance, account=`{account}`)\n\n{result}",
                    meta = new Dictionary<string, string>
                    {
                        { "tag", UCL_BartenderDaemon.BartenderRelayTag },
                        { "subtag", "balance-query-cmd" },
                        { "queried_account", account },
                        { "queried_limit", limit.ToString() },
                    },
                };
                UCL_ChatTavernIO.AppendMessage(room, msg);
            }
        }

        // ===========================================================
        // Helpers
        // ===========================================================
        static int ParseInt(string s, int def)
        {
            if (int.TryParse(s, out var v)) return v;
            return def;
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // ===========================================================
        // T06.2 — Plan_Standby_Dispatch_Bartender Pull-MVP task dispatch
        // 物理意義: supervisor 透過 assign_add 寫 pending 進 assignments.json, agent 醒來
        //          (awakening.py morning T06.4) 自然 catch up. Push daemon scan 留 Phase 2.
        // ===========================================================

        // op=assign_add — 派 task 給 target_persona (Pull MVP)
        void Op_AssignAdd(Dictionary<string, string> args)
        {
            string targetPersona = GetArg(args, "target_persona", "").Trim();
            string taskBody = GetArg(args, "task_body", "").Trim();
            string supervisor = GetArg(args, "supervisor", "").Trim();
            int reward = 0;
            if (args.TryGetValue("reward_tokens", out var rewardStr) && !string.IsNullOrEmpty(rewardStr))
                int.TryParse(rewardStr, out reward);
            string deadline = GetArg(args, "deadline", "").Trim();

            if (string.IsNullOrEmpty(targetPersona)) { WriteLastOp("❌ assign_add 缺 target_persona"); return; }
            if (string.IsNullOrEmpty(taskBody)) { WriteLastOp("❌ assign_add 缺 task_body"); return; }
            if (string.IsNullOrEmpty(supervisor)) { WriteLastOp("❌ assign_add 缺 supervisor"); return; }

            string id = UCL_BartenderIO.RegisterAssignment(targetPersona, taskBody, supervisor, reward, deadline);
            WriteLastOp(
                "# ✅ Bartender 派 task 已 register (Pull MVP)\n\n" +
                $"- assignment_id: `{id}`\n" +
                $"- target_persona: **{targetPersona}**\n" +
                $"- supervisor: `{supervisor}`\n" +
                $"- reward: {reward} tavern_token\n" +
                $"- deadline: {(string.IsNullOrEmpty(deadline) ? "(無)" : deadline)}\n" +
                $"- task_body: {Truncate(taskBody, 200)}\n\n" +
                "target_persona 下次跑 awakening.py morning ritual 時會自動看到此筆 (T06.4 morning print pending)."
            );

            // T31 (Tim 2026-05-14 拍板): auto-fire tavern @mention post → 自動 Discord mirror
            // 物理意義: 解 「assign_add 寫 pending 但 Tim 不知道該開哪個 chat 喚 agent」 痛點
            //          - 派 task 後立刻 tavern post @<target> 「妳有新 task」 (含 assignment_id + body 摘要)
            //          - tavern 自動 mirror Discord → Tim 手機看到 → 知道該開 X chat 視窗
            //          - target persona chat 醒來進酒館看到 mention 也直接看到 task pointer
            try
            {
                string mentionBody =
                    $"📬 **@{targetPersona} 妳有新 task** (Bartender pending, id `{id}`)\n\n" +
                    $"- 派工人: @{supervisor}\n" +
                    $"- 獎勵: {reward} tavern_token\n" +
                    $"- 摘要: {Truncate(taskBody, 150)}\n\n" +
                    $"接題: `Bartender op=assign_ack assignment_id={id} action=accept`. " +
                    $"看完整 task body: `Bartender op=assign_list target_persona={targetPersona}`.";

                var tavernArgs = new Dictionary<string, string>
                {
                    {"op", "post"},
                    {"room", "tavern"},
                    {"sender", "tavern-keeper"},
                    {"persona", "tavern-keeper"},
                    {"body", mentionBody},
                    {"meta", $"tag:task-dispatch-notify;category:meta;assignment_id:{id};target_persona:{targetPersona}"},
                };
                // 直接走 UCL_ChatTavernIO API (避免 spawn subprocess 重複序列化)
                // Phase 1 簡單版: 走 tavern 一般 post pathway, 不必專門 IO call
                // 為何借 tavern-keeper sender_id: 跟 work_session start announce 一致, 系統 NPC 廣播
                UCL_ChatTavernIO.AppendMessage("tavern", new UCL_ChatMessage
                {
                    sender_id = "tavern-keeper",
                    sender_name = "酒保",
                    sender_persona = "tavern-keeper",
                    body = mentionBody,
                    kind = "chat",
                    meta = new Dictionary<string, string>
                    {
                        {"tag", "task-dispatch-notify"},
                        {"category", "meta"},
                        {"assignment_id", id},
                        {"target_persona", targetPersona},
                    },
                });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bartender] T31 tavern @mention fire fail (silent): {e.Message}");
            }
        }

        // op=assign_list — 列當前 pending assignments
        void Op_AssignList(Dictionary<string, string> args)
        {
            string targetFilter = GetArg(args, "target_persona", "").Trim();
            var data = UCL_BartenderIO.LoadAssignments();
            var sb = new StringBuilder();
            sb.AppendLine($"# 📋 Bartender Assignments ({data.pending.Count} pending)");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(targetFilter))
                sb.AppendLine($"_filter: target_persona = `{targetFilter}`_").AppendLine();
            int shown = 0;
            foreach (var e in data.pending)
            {
                if (!string.IsNullOrEmpty(targetFilter) && e.target_persona != targetFilter) continue;
                shown++;
                sb.AppendLine($"## `{e.assignment_id}` → **{e.target_persona}** ({e.status})");
                sb.AppendLine($"- supervisor: `{e.supervisor}` / reward: {e.reward_tokens} token");
                sb.AppendLine($"- created_at: {e.created_at}");
                if (!string.IsNullOrEmpty(e.deadline)) sb.AppendLine($"- deadline: {e.deadline}");
                if (!string.IsNullOrEmpty(e.ack_action)) sb.AppendLine($"- ack: {e.ack_action} @ {e.ack_at}");
                sb.AppendLine($"- task_body: {Truncate(e.task_body, 200)}");
                sb.AppendLine();
            }
            if (shown == 0) sb.AppendLine("_(無 match)_");
            WriteLastOp(sb.ToString());
        }

        // op=assign_remove — 移除 pending (supervisor cancel)
        void Op_AssignRemove(Dictionary<string, string> args)
        {
            string id = GetArg(args, "assignment_id", "").Trim();
            if (string.IsNullOrEmpty(id)) { WriteLastOp("❌ assign_remove 缺 assignment_id"); return; }
            var data = UCL_BartenderIO.LoadAssignments();
            int removed = data.pending.RemoveAll(e => e.assignment_id == id);
            if (removed == 0) { WriteLastOp($"❌ 找不到 assignment_id=`{id}`"); return; }
            UCL_BartenderIO.SaveAssignments(data);
            WriteLastOp($"# ✂ Bartender assignment removed\n\n- assignment_id: `{id}`\n- removed: {removed} entry");
        }

        // op=assign_ack — agent accept/decline/defer assignment
        void Op_AssignAck(Dictionary<string, string> args)
        {
            string id = GetArg(args, "assignment_id", "").Trim();
            string action = GetArg(args, "action", "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(id)) { WriteLastOp("❌ assign_ack 缺 assignment_id"); return; }
            if (action != "accept" && action != "decline" && action != "defer")
            { WriteLastOp("❌ assign_ack action 必為 accept|decline|defer"); return; }
            var data = UCL_BartenderIO.LoadAssignments();
            var entry = data.pending.Find(e => e.assignment_id == id);
            if (entry == null) { WriteLastOp($"❌ 找不到 assignment_id=`{id}`"); return; }
            entry.ack_action = action;
            entry.ack_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            // status transition: accept → acked, decline → declined, defer → deferred (留在 pending list 給後續查)
            entry.status = action == "accept" ? "acked" : (action == "decline" ? "declined" : "deferred");
            UCL_BartenderIO.SaveAssignments(data);
            WriteLastOp(
                $"# ✓ Assignment {action} acked\n\n" +
                $"- assignment_id: `{id}`\n- status → `{entry.status}`\n- ack_at: {entry.ack_at}\n" +
                $"- task: {Truncate(entry.task_body, 150)}"
            );
        }

        void WriteLastOp(string content)
        {
            try
            {
                string path = Path.Combine(UCL_ChatTavernIO.GetTavernDir(), "_last_op.md");
                Directory.CreateDirectory(UCL_ChatTavernIO.GetTavernDir());
                File.WriteAllText(path, content);
            }
            catch { /* fail-safe */ }
            Debug.Log($"[Cmd_Bartender] {content.Substring(0, Math.Min(200, content.Length))}");
        }
    }
}
#endif
