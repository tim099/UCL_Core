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
  target=<id>           提醒對象 (e.g. Tim)
  msg=<reminder>        提醒訊息
  grace=<min>           寬限分鐘, 預設 10
  penalty=<true/false>  啟用 HP penalty, 預設 false
  penalty_interval=<min> penalty 重複間隔, 預設 5

[time_list]   列時間規則

[time_remove] 移除時間規則
  id=<rule_id>

[status]      列 daemon 統計 + state file 概況

[tick]        強制立刻 tick (測試 / dogfood 用)";

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
                    case "add":         Op_Add(args); break;
                    case "list":        Op_List(args); break;
                    case "remove":      Op_Remove(args); break;
                    case "time_add":    Op_TimeAdd(args); break;
                    case "time_list":   Op_TimeList(args); break;
                    case "time_remove": Op_TimeRemove(args); break;
                    case "status":      Op_Status(args); break;
                    case "tick":        Op_Tick(args); break;
                    default:
                        WriteLastOp($"❌ 未知 op='{op}', 支援: add / list / remove / time_add / time_list / time_remove / status / tick");
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
            string target = GetArg(args, "target", "");
            string msg = GetArg(args, "msg", "");
            int grace = ParseInt(GetArg(args, "grace", "10"), 10);
            bool penalty = GetArg(args, "penalty", "false").ToLowerInvariant() == "true";
            int penaltyInterval = ParseInt(GetArg(args, "penalty_interval", "5"), 5);
            string penaltyTarget = GetArg(args, "penalty_target", target);
            string room = GetArg(args, "room", "tavern");

            if (string.IsNullOrEmpty(id)) { WriteLastOp("❌ time_add 缺 id"); return; }
            if (string.IsNullOrEmpty(time)) { WriteLastOp("❌ time_add 缺 time (HH:mm)"); return; }
            if (string.IsNullOrEmpty(msg)) { WriteLastOp("❌ time_add 缺 msg"); return; }

            // 走 shared register helper
            UCL_BartenderIO.RegisterTimeRule(
                id, time, target, msg, grace, penalty, penaltyInterval, penaltyTarget, room);

            WriteLastOp(
                $"✅ Time rule `{id}` 新增/覆寫\n\n" +
                $"- 時間: {time} (local)\n" +
                $"- 對象: {target}\n" +
                $"- 寬限: {grace} 分鐘\n" +
                $"- penalty: {(penalty ? $"啟用 (每 {penaltyInterval} min 廣播一次累積 HP loss)" : "停用")}\n" +
                $"- room: {room}\n" +
                $"- msg: {Truncate(msg, 100)}");
        }

        void Op_TimeList(Dictionary<string, string> args)
        {
            var data = UCL_BartenderIO.LoadTimeRules();
            if (data.rules.Count == 0) { WriteLastOp("📭 目前沒有任何時間規則."); return; }
            var sb = new StringBuilder();
            sb.AppendLine($"# ⏰ Bartender Time Rules ({data.rules.Count} 筆)\n");
            sb.AppendLine("| id | time | target | grace | penalty | room | enabled |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var r in data.rules)
            {
                if (r == null) continue;
                sb.AppendLine($"| `{r.id}` | {r.time_hhmm} | {r.target_id} | {r.grace_minutes}min | {(r.penalty_enabled ? $"✓ /{r.penalty_interval_minutes}min" : "—")} | {r.target_room} | {r.enabled} |");
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
