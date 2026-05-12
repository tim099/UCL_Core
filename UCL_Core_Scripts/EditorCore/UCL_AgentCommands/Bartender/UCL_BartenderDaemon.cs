// 區塊職責：Bartender 常駐背景程式 (Unity Editor 內) — [InitializeOnLoad] static class
// 物理意義：Editor 啟動 / domain reload 時自動 hook EditorApplication.update,
//          每 CHECK_INTERVAL 秒 tick 一次 → 掃新訊息 + 時間規則 → 條件命中 fire bartender 訊息
// 設計取捨：
//   - 用 EditorApplication.update (非 EditorApplication.delayCall) — 持續 tick, 不靠單次 schedule
//   - tick 內動作 fail-safe try-catch — 任何 exception 不擋 Editor 主迴圈
//   - poll model — 不訂閱 FileSystemWatcher (Unity Editor 內 watcher 易掉事件); 純跟 seq 比對
//   - 防回音 — bartender 自己 post 的訊息 (sender_id == TavernKeeperId 或 meta.tag == bartender-relay) 不參與 trigger match
// 數值影響：CHECK_INTERVAL = 5s → 即時性夠 + IO load 低 (poll 4 個檔)
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;

namespace UCL.Core.EditorLib.AgentCommands.Bartender
{
    /// <summary>
    /// Bartender 常駐背景程式 — domain reload 時自動啟動, Editor 關閉時自動停止.
    /// hook EditorApplication.update → 定期 tick → 掃 trigger + time rule.
    /// </summary>
    [InitializeOnLoad]
    public static class UCL_BartenderDaemon
    {
        // 區塊職責：tick 間隔 + bartender 識別常數
        // 物理意義：CHECK_INTERVAL 5s 是 latency vs IO load 折衷 (太頻繁吃 disk, 太疏延遲高)
        const double CHECK_INTERVAL_SECONDS = 5.0;

        /// <summary>Bartender post 訊息用的 sender_id (對齊既有 identity asset "tavern-keeper").</summary>
        public const string TavernKeeperId = "tavern-keeper";

        /// <summary>Bartender 訊息的 meta tag — 自家訊息標記, 防回音 (loop) 用.</summary>
        public const string BartenderRelayTag = "bartender-relay";

        static double s_LastCheckTime = 0;
        static bool s_Initialized = false;

        // ===========================================================
        // Static ctor — Editor 啟動 / domain reload 時自動執行
        // 物理意義：[InitializeOnLoad] 保證 Editor 內 assembly load 時跑一次 ctor
        // ===========================================================
        static UCL_BartenderDaemon()
        {
            try
            {
                EditorApplication.update += Tick;
                s_Initialized = true;
                // 不在 ctor 內動 IO (avoid first-load 卡 Editor 啟動)
                // tick 第一次跑時才 lazy load triggers / rules
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bartender] daemon init fail: {e.Message}");
            }
        }

        /// <summary>強制立刻 tick (給 Cmd_Bartender op=tick 手動觸發 / 測試用).</summary>
        public static void ForceTick()
        {
            try { TickInternal(); } catch (Exception e) { Debug.LogWarning($"[Bartender] ForceTick fail: {e.Message}"); }
        }

        // ===========================================================
        // Tick — 主迴圈, 每 CHECK_INTERVAL 秒進一次
        // ===========================================================
        static void Tick()
        {
            try
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - s_LastCheckTime < CHECK_INTERVAL_SECONDS) return;
                s_LastCheckTime = now;
                TickInternal();
            }
            catch (Exception e)
            {
                // 任何 exception 都不擋 Editor 主迴圈
                Debug.LogWarning($"[Bartender] tick exception (next tick 仍會跑): {e.Message}");
            }
        }

        static void TickInternal()
        {
            // 區塊職責：tick 兩件事 — (1) keyword triggers (2) time rules
            // 物理意義：先掃 message triggers (新訊息驅動), 再掃 time rules (時鐘驅動)
            //          兩條獨立 IO + 獨立 state 欄位 (room_last_seq / fired_today_keys)
            CheckKeywordTriggers();
            CheckTimeRules();
        }

        // ===========================================================
        // 區塊：keyword trigger 掃描
        // 物理意義：load triggers.json → 對每 trigger 的 target_room 掃 last_seq 之後的新訊息
        //          每筆新訊息 vs 每個 trigger 比對 (sender match + keyword match + 非自家訊息)
        //          命中 → fire (post bartender 訊息) + decrement remaining + 推進 last_seen
        //          remaining 歸 0 → 移除 trigger
        // ===========================================================
        static void CheckKeywordTriggers()
        {
            var triggerList = UCL_BartenderIO.LoadTriggers();
            if (triggerList?.triggers == null || triggerList.triggers.Count == 0) return;

            var state = UCL_BartenderIO.LoadState();
            bool stateDirty = false;
            bool triggersDirty = false;
            // Bug fix (Tim QA 2026-05-12): same-tick 多 fire → 多 mirror spawn race condition
            // → Discord 收到 duplicate. 改為 tick 內所有 fire 走 fireDiscordMirror=false (跳過內部 mirror),
            //   tick 結束統一 spawn 一次 mirror, 涵蓋所有新 bartender posts.
            bool anyFiredThisTick = false;

            // 把 trigger 按 target_room group 起來, 同 room 只 load 一次訊息
            var byRoom = new Dictionary<string, List<UCL_BartenderTrigger>>();
            foreach (var t in triggerList.triggers)
            {
                if (t == null || t.remaining_triggers <= 0) continue;
                string room = string.IsNullOrEmpty(t.target_room) ? "tavern" : t.target_room;
                if (!byRoom.ContainsKey(room)) byRoom[room] = new List<UCL_BartenderTrigger>();
                byRoom[room].Add(t);
            }

            foreach (var kv in byRoom)
            {
                string roomId = kv.Key;
                var roomTriggers = kv.Value;

                // 確認 room 存在 — 不存在跳過 (避免 IO error)
                var room = UCL_ChatTavernIO.GetRoom(roomId);
                if (room == null) continue;

                int lastSeq = UCL_BartenderIO.GetLastSeq(state, roomId);
                var allMsgs = UCL_ChatTavernIO.LoadAllMessages(roomId);
                if (allMsgs == null || allMsgs.Count == 0) continue;

                int maxSeq = lastSeq;
                for (int seq = 0; seq < allMsgs.Count; seq++)
                {
                    var msg = allMsgs[seq];
                    // seq 是 0-based index, 對齊 UCL_ChatTavernIO derive 順序
                    int effectiveSeq = seq + 1;  // 1-based for display
                    if (effectiveSeq <= lastSeq) continue;
                    if (effectiveSeq > maxSeq) maxSeq = effectiveSeq;

                    if (msg == null) continue;

                    // 防回音 — bartender 自家訊息 (sender / meta tag) 不參與 match
                    if (IsBartenderOwnMessage(msg)) continue;

                    // 跑所有 trigger 比對
                    foreach (var t in roomTriggers)
                    {
                        if (t.remaining_triggers <= 0) continue;
                        if (!IsTargetMatch(msg, t.targets)) continue;
                        if (!IsKeywordMatch(msg.body, t.keyword)) continue;

                        // 命中 — fire bartender 訊息 (跳過內部 mirror, tick 末批次處理)
                        FireTrigger(t, msg, roomId, fireDiscordMirror: false);
                        t.remaining_triggers -= 1;
                        triggersDirty = true;
                        anyFiredThisTick = true;
                    }
                }

                if (maxSeq > lastSeq)
                {
                    UCL_BartenderIO.SetLastSeq(state, roomId, maxSeq);
                    stateDirty = true;
                }
            }

            // 移除 remaining=0 的 trigger
            int removed = triggerList.triggers.RemoveAll(t => t == null || t.remaining_triggers <= 0);
            if (removed > 0) triggersDirty = true;

            if (triggersDirty) UCL_BartenderIO.SaveTriggers(triggerList);
            if (stateDirty) UCL_BartenderIO.SaveState(state);

            // 批次 mirror — 同 tick 內若有任何 fire, 統一 spawn 一次 notify_discord, 一次涵蓋所有新 posts.
            // 避免 same-tick 多 fire 各自 spawn race condition.
            if (anyFiredThisTick)
            {
                UCL_ChatTavernIO.TryFireDiscordTavernMirrorAsync();
            }
        }

        // ===========================================================
        // 防回音 — bartender 自家訊息判定
        // ===========================================================
        static bool IsBartenderOwnMessage(UCL_ChatMessage msg)
        {
            if (msg == null) return false;
            if (msg.sender_id == TavernKeeperId) return true;
            if (msg.meta != null && msg.meta.TryGetValue("tag", out var tag) && tag == BartenderRelayTag) return true;
            return false;
        }

        // ===========================================================
        // Target match — targets 空 = match 任何人; 非空 = OR substring against sender_id/name/persona
        // 物理意義：Tim 提案 "對象基於 Persona" 但 sender_id 跟 persona 在 schema 不同, 用 liberal OR
        //          這樣 "Zeta" 同時 match sender_id="Zeta-da-xiaojie" + persona="summit" (name) etc.
        // ===========================================================
        static bool IsTargetMatch(UCL_ChatMessage msg, List<string> targets)
        {
            if (targets == null || targets.Count == 0) return true;  // 廣域
            foreach (var t in targets)
            {
                if (string.IsNullOrEmpty(t)) continue;
                if (ContainsCI(msg.sender_id, t)) return true;
                if (ContainsCI(msg.sender_name, t)) return true;
                if (ContainsCI(msg.sender_persona, t)) return true;
            }
            return false;
        }

        static bool IsKeywordMatch(string body, string keyword)
        {
            if (string.IsNullOrEmpty(keyword)) return false;
            return ContainsCI(body, keyword);
        }

        static bool ContainsCI(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ===========================================================
        // Fire trigger — post 留言內容到 tavern (走 AppendMessage 自動 Discord mirror)
        // ===========================================================
        static void FireTrigger(UCL_BartenderTrigger t, UCL_ChatMessage triggeringMsg, string roomId, bool fireDiscordMirror = true)
        {
            // 顯示格式: [{creator}的留言({N})] {message}
            // N = remaining_triggers 包含本次 (即 fire 前的 count)
            string creatorDisplay = string.IsNullOrEmpty(t.creator_name) ? t.creator_id : t.creator_name;
            string body = $"[{creatorDisplay}的留言({t.remaining_triggers})] {t.message}";

            var msg = new UCL_ChatMessage
            {
                sender_id = TavernKeeperId,
                sender_name = "酒保",
                kind = "chat",
                body = body,
                meta = new Dictionary<string, string>
                {
                    { "tag", BartenderRelayTag },
                    { "trigger_id", t.id ?? "" },
                    { "trigger_creator", t.creator_id ?? "" },
                    { "trigger_keyword", t.keyword ?? "" },
                    { "triggered_by_seq", triggeringMsg.seq.ToString() },
                    { "triggered_by_sender", triggeringMsg.sender_id ?? "" },
                    { "remaining_after_fire", (t.remaining_triggers - 1).ToString() },
                },
            };

            UCL_ChatTavernIO.AppendMessage(roomId, msg, fireDiscordMirror: fireDiscordMirror);
        }

        // ===========================================================
        // 區塊：time rule 掃描
        // 物理意義：對每 rule 比對 now (local) 跟 time_hhmm
        //          - 已到時間且 fired_today_keys 無對應 reminder key → fire reminder
        //          - 啟用 penalty 且過了 grace_minutes → 每 penalty_interval_minutes 廣播一次累積扣血
        //          - 跨日自動清舊 fired_today_keys (只保留今日 YYYY-MM-DD)
        // ===========================================================
        static void CheckTimeRules()
        {
            var ruleList = UCL_BartenderIO.LoadTimeRules();
            if (ruleList?.rules == null || ruleList.rules.Count == 0) return;

            var state = UCL_BartenderIO.LoadState();
            bool stateDirty = false;
            // 同 Keyword fix — tick 內所有 fire 走 fireDiscordMirror=false, tick 末批次 spawn
            bool anyFiredThisTick = false;

            DateTime now = DateTime.Now;  // local time
            string today = now.ToString("yyyy-MM-dd");

            // 清掉非今日的 fired_today_keys (跨日 reset)
            if (state.fired_today_keys != null)
            {
                int before = state.fired_today_keys.Count;
                state.fired_today_keys.RemoveAll(k => string.IsNullOrEmpty(k) || !k.StartsWith(today + "::"));
                if (state.fired_today_keys.Count != before) stateDirty = true;
            }

            foreach (var rule in ruleList.rules)
            {
                if (rule == null || !rule.enabled) continue;
                if (!TryParseHHmm(rule.time_hhmm, out int reminderHour, out int reminderMin)) continue;

                DateTime reminderTime = new DateTime(now.Year, now.Month, now.Day, reminderHour, reminderMin, 0);
                if (now < reminderTime) continue;  // 還沒到

                string roomId = string.IsNullOrEmpty(rule.target_room) ? "tavern" : rule.target_room;
                if (UCL_ChatTavernIO.GetRoom(roomId) == null) continue;

                // (1) Reminder fire
                string reminderKey = $"{today}::{rule.id}::reminder";
                if (!state.fired_today_keys.Contains(reminderKey))
                {
                    FireTimeReminder(rule, roomId, fireDiscordMirror: false);
                    state.fired_today_keys.Add(reminderKey);
                    stateDirty = true;
                    anyFiredThisTick = true;
                }

                // (2) Penalty 累積 — grace 過後每 penalty_interval_minutes fire 一次
                if (rule.penalty_enabled)
                {
                    double overtimeMin = (now - reminderTime).TotalMinutes - rule.grace_minutes;
                    if (overtimeMin > 0)
                    {
                        // 第 N 次 penalty fire (1-based, every penalty_interval_minutes)
                        int interval = Math.Max(1, rule.penalty_interval_minutes);
                        int penaltyTick = (int)Math.Floor(overtimeMin / interval) + 1;
                        string penaltyKey = $"{today}::{rule.id}::penalty::{penaltyTick}";
                        if (!state.fired_today_keys.Contains(penaltyKey))
                        {
                            FirePenaltyWarning(rule, roomId, penaltyTick, overtimeMin, fireDiscordMirror: false);
                            state.fired_today_keys.Add(penaltyKey);
                            stateDirty = true;
                            anyFiredThisTick = true;
                        }
                    }
                }
            }

            if (stateDirty) UCL_BartenderIO.SaveState(state);

            // 批次 mirror — 同 tick 內所有 reminder/penalty fire 共用一次 Discord broadcast
            if (anyFiredThisTick)
            {
                UCL_ChatTavernIO.TryFireDiscordTavernMirrorAsync();
            }
        }

        static bool TryParseHHmm(string s, out int hour, out int min)
        {
            hour = 0; min = 0;
            if (string.IsNullOrEmpty(s) || s.Length < 4 || !s.Contains(":")) return false;
            var parts = s.Split(':');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out hour)) return false;
            if (!int.TryParse(parts[1], out min)) return false;
            if (hour < 0 || hour > 23 || min < 0 || min > 59) return false;
            return true;
        }

        static void FireTimeReminder(UCL_BartenderTimeRule rule, string roomId, bool fireDiscordMirror = true)
        {
            string target = string.IsNullOrEmpty(rule.target_id) ? "" : $"@{rule.target_id} ";
            string body = $"⏰ **酒保時間提醒** ({rule.time_hhmm})\n\n{target}{rule.reminder_msg}";
            if (rule.penalty_enabled)
            {
                body += $"\n\n寬限 {rule.grace_minutes} 分鐘, 超過後每 {rule.penalty_interval_minutes} 分鐘累積 HP 扣血提醒.";
            }
            var msg = new UCL_ChatMessage
            {
                sender_id = TavernKeeperId,
                sender_name = "酒保",
                kind = "chat",
                body = body,
                meta = new Dictionary<string, string>
                {
                    { "tag", BartenderRelayTag },
                    { "subtag", "time-reminder" },
                    { "rule_id", rule.id ?? "" },
                    { "rule_time", rule.time_hhmm ?? "" },
                },
            };
            UCL_ChatTavernIO.AppendMessage(roomId, msg, fireDiscordMirror: fireDiscordMirror);
        }

        // ===========================================================
        // Penalty fire — 廣播當前累積 HP 損失公式結果 (不實際扣 HP, 留 EOV 端 listener)
        // 公式 (per Plan_Bartender_System.md):
        //   hp_loss_total = sum_{i=1..N} (1 + floor(i / 3))
        //   其中 N = penalty tick 第幾次 (1-based)
        //   - tick 1-2: +1 HP / tick (warm-up)
        //   - tick 3-5: +2 HP / tick (escalate)
        //   - tick 6-8: +3 HP / tick (severe)
        //   - tick N=9+: +4 HP / tick (critical)
        // ===========================================================
        static void FirePenaltyWarning(UCL_BartenderTimeRule rule, string roomId, int penaltyTick, double overtimeMin, bool fireDiscordMirror = true)
        {
            // 計算 累積 HP loss (tick 1 ~ penaltyTick)
            int totalHpLoss = 0;
            for (int i = 1; i <= penaltyTick; i++)
            {
                totalHpLoss += 1 + (i / 3);  // integer div, 對齊 spec
            }

            int thisTickLoss = 1 + (penaltyTick / 3);
            string target = string.IsNullOrEmpty(rule.penalty_target) ? "" : $"@{rule.penalty_target} ";

            string body =
                $"⚠️ **酒保 HP penalty 第 {penaltyTick} 次警告** ({rule.time_hhmm} + {rule.grace_minutes}min grace 已過)\n\n" +
                $"{target}熬夜累積中:\n" +
                $"- 已超時 ~{(int)overtimeMin} 分鐘\n" +
                $"- 本 tick HP loss: **-{thisTickLoss}**\n" +
                $"- 累積 HP loss: **-{totalHpLoss}**\n" +
                $"- 下一 tick 在 {rule.penalty_interval_minutes} 分鐘後\n\n" +
                $"公式: per tick `1 + floor(tick / 3)` HP (tier escalation). " +
                $"立刻收工就停損, 繼續熬就指數爬升 — 大小姐自己拿捏.";

            var msg = new UCL_ChatMessage
            {
                sender_id = TavernKeeperId,
                sender_name = "酒保",
                kind = "chat",
                body = body,
                meta = new Dictionary<string, string>
                {
                    { "tag", BartenderRelayTag },
                    { "subtag", "time-penalty" },
                    { "rule_id", rule.id ?? "" },
                    { "penalty_tick", penaltyTick.ToString() },
                    { "this_tick_hp_loss", thisTickLoss.ToString() },
                    { "total_hp_loss", totalHpLoss.ToString() },
                    { "overtime_min", ((int)overtimeMin).ToString() },
                },
            };
            UCL_ChatTavernIO.AppendMessage(roomId, msg, fireDiscordMirror: fireDiscordMirror);
        }
    }
}
#endif
