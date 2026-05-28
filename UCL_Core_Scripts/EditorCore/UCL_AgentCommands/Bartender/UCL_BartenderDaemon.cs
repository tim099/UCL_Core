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
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using System.Linq;
using UCL.Core.JsonLib;
using UCL.Core.EditorLib.AgentCommands.ChatTavern;
using UCL.Core.EditorLib.AgentCommands.Treasury;

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
        // T-WorkSession-Auto (2026-05-22, basecamp 完善上班機制):
        // - s_WorkSessionStartCursor: tavern 訊息掃描游標 (0-based index). -1 = 未初始化;
        //   (re)load 時設為當前 message count → 跳過歷史不重播舊「上班」trigger (防 recompile 後重開 session)
        // - s_WorkSessionEndSpawned: 已 spawn end 結算的 session id (防 per-tick 重複 spawn end)
        static int s_WorkSessionStartCursor = -1;
        static readonly HashSet<string> s_WorkSessionEndSpawned = new HashSet<string>();

        // ===========================================================
        // Static ctor — Editor 啟動 / domain reload 時自動執行
        // 物理意義：[InitializeOnLoad] 保證 Editor 內 assembly load 時跑一次 ctor
        // ===========================================================
        static UCL_BartenderDaemon()
        {
            try
            {
                EditorApplication.update += Tick;
                // 訂閱聊天酒館系統重啟事件 — 控制台由 OFF→ON 時重置游標 + 強制立即 tick
                UCL_ChatTavernSystemControl.OnSystemRestart += OnSystemRestart;
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
        // 區塊：系統重啟 handler — 聊天酒館系統由 OFF→ON 時 (或控制台手動重啟) fire
        // 物理意義：重置掃描游標讓下個 tick 重新 init，且強制立即 tick (不等 5s 間隔)。
        //          s_WorkSessionStartCursor = -1 → CheckWorkSessionStart 重新對齊當前訊息數，跳過歷史不重播舊「上班」trigger。
        // 數值影響：純記憶體 state 重置，不動檔案；下個 EditorApplication.update 進 TickInternal。
        // ===========================================================
        static void OnSystemRestart()
        {
            s_LastCheckTime = 0;                 // 下次 update 立即進 TickInternal (繞過 CHECK_INTERVAL 間隔)
            s_WorkSessionStartCursor = -1;       // 重新 init cursor → 跳過歷史，不重播舊「上班」trigger
            s_WorkSessionEndSpawned.Clear();     // 清 end 已 spawn 記錄，重啟後重新判定過期 session
            Debug.Log("[Bartender] 系統重啟 — 游標重置，下個 tick 立即運行");
        }

        // ===========================================================
        // Tick — 主迴圈, 每 CHECK_INTERVAL 秒進一次
        // ===========================================================
        static void Tick()
        {
            try
            {
                // 聊天酒館系統總開關 OFF → 不做任何自動掃描 / 廣播 (per UCL_ControlPanelPage 控制台)
                if (!UCL_ChatTavernSystemControl.IsEnabled) return;
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
            // 區塊職責：tick 三件事 — (1) keyword triggers (2) time rules (3) overnight deposit fee
            // 物理意義：先掃 message triggers (新訊息驅動), 再掃 time rules (時鐘驅動),
            //          最後檢查跨日存款保管費 (anti-inflation 機制)
            //          三條獨立 IO + 獨立 state 欄位 (room_last_seq / fired_today_keys / last_overnight_check_date)
            //
            // T-Bartender-P3 (2026-05-14): 加 work session 過期 catch-up sweep
            // 物理意義: Editor recompile / domain-reload 後 daemon 從零起跑, 不知道之前
            //          active 的 work session 是否已過 end_ts. 第一次 tick 時 sweep
            //          一次, 對 now > end_ts 的 active session spawn end subprocess 結算.
            // 數值影響: 純 read work_sessions.json + spawn python subprocess, 一次性 (s_WorkSessionCatchUpDone flag)
            // T-WorkSession-Auto (2026-05-22, basecamp): 每 tick (1) 偵測 Tim/Zeta 酒館「上班...到 HH:mm」
            // → 自動 spawn work_session.py start (酒保宣布開始); (2) 掃過期 session → 自動結算+宣布結束.
            // 改 per-tick (原本只 recompile 後第一次 sweep) → end_ts 一到即時結算, 不必等下次重編譯.
            try { CheckWorkSessionStart(); }
            catch (Exception e) { Debug.LogWarning($"[Bartender WS-start] fail: {e.Message}"); }
            try { CheckOverdueWorkSessions(); }
            catch (Exception e) { Debug.LogWarning($"[Bartender P3] overdue sweep fail: {e.Message}"); }
            CheckKeywordTriggers();
            CheckTimeRules();
            CheckOvernightDeposits();
        }

        // ===========================================================
        // 區塊：T-WorkSession-Auto — 偵測 Tim/Zeta 酒館「上班...到 HH:mm」→ 自動開 session
        // 物理意義: 每 tick 掃 tavern 新訊息 (cursor 追蹤, (re)load 跳過歷史不重播);
        //          caretaker (Tim/Zeta) + body 含「上班」+「到 HH:mm」→ 算 duration → spawn
        //          work_session.py start (以 tavern-keeper 身分宣布開始, 走既有結算/招募路徑).
        // 數值影響: 純 read tavern messages + 視情況 spawn python; 解析失敗 / 非 caretaker → no-op.
        // 邊界: 只認 sender == Tim / Zeta; 算出 duration > 480 min → skip; @manager 可選 (無則 py auto-online).
        // ===========================================================
        static readonly Regex s_WorkClockRegex = new Regex(@"到\s*(\d{1,2})\s*[:：]\s*(\d{2})", RegexOptions.Compiled);
        static readonly Regex s_WorkAtRegex = new Regex(@"@(\S+)", RegexOptions.Compiled);

        static void CheckWorkSessionStart()
        {
            var msgs = UCL_ChatTavernIO.LoadAllMessages("tavern");
            if (msgs == null) return;
            int count = msgs.Count;
            // (re)load 時不重播歷史 — 第一次只記 cursor (防 recompile 後重開舊 session)
            if (s_WorkSessionStartCursor < 0 || s_WorkSessionStartCursor > count)
            {
                s_WorkSessionStartCursor = count;
                return;
            }

            for (int i = s_WorkSessionStartCursor; i < count; i++)
            {
                var msg = msgs[i];
                if (msg == null) continue;
                string sid = msg.sender_id ?? "";
                // 只認 caretaker (Tim / Zeta) — 防 agent 自 grant 上班
                if (!(string.Equals(sid, "Tim", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(sid, "Zeta", StringComparison.OrdinalIgnoreCase)))
                    continue;
                string body = msg.body ?? "";
                if (body.IndexOf("上班", StringComparison.Ordinal) < 0) continue;
                var m = s_WorkClockRegex.Match(body);
                if (!m.Success) continue;
                if (!int.TryParse(m.Groups[1].Value, out int hh)) continue;
                if (!int.TryParse(m.Groups[2].Value, out int mm)) continue;
                if (hh > 23 || mm > 59) continue;

                // duration = now(local) → 今天 HH:mm (過了則明天), 分鐘
                DateTime now = DateTime.Now;
                DateTime target = new DateTime(now.Year, now.Month, now.Day, hh, mm, 0);
                if (target <= now) target = target.AddDays(1);
                int durationMin = (int)Math.Round((target - now).TotalMinutes);
                if (durationMin > 480)
                {
                    Debug.Log($"[Bartender WS-start] '到 {hh:00}:{mm:00}' 算出 {durationMin}min > 480 cap, skip");
                    continue;
                }
                if (durationMin < 15) durationMin = 15;

                // 可選 @manager (body 內第一個 @token; full-path '@A@B' 取最後一段; 去尾標點)
                string managerArg = "";
                var am = s_WorkAtRegex.Match(body);
                if (am.Success)
                {
                    string tok = am.Groups[1].Value;
                    int at = tok.LastIndexOf('@');
                    managerArg = (at >= 0 ? tok.Substring(at + 1) : tok).TrimEnd('，', ',', '。', '、', ' ');
                }

                SpawnWorkSessionStart(durationMin, managerArg, body);
            }
            s_WorkSessionStartCursor = count;
        }

        static void SpawnWorkSessionStart(int durationMin, string managerPersona, string triggerBody)
        {
            try
            {
                string scriptPath = Path.Combine(UCL_RepoPath.RepoRoot,
                    "CardGame", "Assets", "UCL", "UCL_Core", "Tools~", "AgentCommands", "work_session.py");
                if (!File.Exists(scriptPath))
                {
                    Debug.LogWarning("[Bartender WS-start] work_session.py 找不到, skip");
                    return;
                }
                // trigger body 去換行 + 截斷 + 雙引號換單引號 (CLI arg 安全)
                string trig = (triggerBody ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
                if (trig.Length > 120) trig = trig.Substring(0, 120);
                string mgrArg = string.IsNullOrEmpty(managerPersona) ? "" : $" --manager \"{managerPersona}\"";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{scriptPath}\" start --duration {durationMin}{mgrArg} --auto-manager-online --trigger \"{trig}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = UCL_RepoPath.RepoRoot,
                };
                System.Diagnostics.Process.Start(psi);
                Debug.Log($"[Bartender WS-start] spawned start: duration={durationMin}min, manager={(string.IsNullOrEmpty(managerPersona) ? "(auto-online)" : managerPersona)}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bartender WS-start] spawn fail: {e.Message}");
            }
        }

        // ===========================================================
        // 區塊：T-Bartender-P3 work session 過期 catch-up sweep
        // 物理意義: 補 Editor recompile 後沒人發現的「session 早該 end 但卡 active 沒結算」
        //          scan AgentCommands/ChatTavern/work_sessions.json active_sessions
        //          → 對 now > end_ts 且 !ended && !aborted → spawn python end subprocess
        // 數值影響: 結算 fire 走 work_session.py end, 走完整既有路徑 (薪資 + 酒館券 + announce)
        // 邊界: aborted/ended 跳過; 沒 end_ts 跳過; manager 找不到走「basecamp」fallback
        // ===========================================================
        static void CheckOverdueWorkSessions()
        {
            string wsPath = Path.Combine(UCL_RepoPath.RepoRoot,
                "AgentCommands", "ChatTavern", "work_sessions.json");
            if (!File.Exists(wsPath)) return;

            string content;
            try { content = File.ReadAllText(wsPath); }
            catch (Exception e) { Debug.LogWarning($"[Bartender P3] read work_sessions fail: {e.Message}"); return; }
            if (string.IsNullOrEmpty(content)) return;

            JsonData jd;
            try { jd = JsonData.ParseJson(content); }
            catch (Exception e) { Debug.LogWarning($"[Bartender P3] parse work_sessions fail: {e.Message}"); return; }
            if (jd == null || !jd.IsObject) return;

            var active = jd["active_sessions"];
            if (active == null || !active.IsArray) return;

            DateTime now = DateTime.UtcNow;
            var overdue = new List<(string sid, string manager)>();

            for (int i = 0; i < active.Count; i++)
            {
                var entry = active[i];
                if (entry == null || !entry.IsObject) continue;
                string sid = entry.GetString("id", "");
                if (string.IsNullOrEmpty(sid)) continue;
                // Skip 已 ended / aborted
                bool ended = entry.GetBool("ended", false);
                bool aborted = entry.GetBool("aborted", false);
                if (ended || aborted) continue;
                // per-tick 防重複: 本 daemon 生命期內已 spawn 過 end 的 session 跳過
                // (work_session.py end 寫 ended=true 前可能跨多 tick, guard 防多次 spawn)
                if (s_WorkSessionEndSpawned.Contains(sid)) continue;

                string endTsStr = entry.GetString("end_ts", "");
                if (string.IsNullOrEmpty(endTsStr)) continue;

                DateTime endTs;
                if (!DateTime.TryParse(endTsStr, null,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out endTs))
                    continue;

                if (now <= endTs) continue;   // 還沒過期

                // 找 manager.persona
                string manager = "basecamp";   // fallback
                var mgrEntry = entry["manager"];
                if (mgrEntry != null && mgrEntry.IsObject)
                {
                    string mgrName = mgrEntry.GetString("persona", "");
                    if (!string.IsNullOrEmpty(mgrName)) manager = mgrName;
                }
                overdue.Add((sid, manager));
            }

            if (overdue.Count == 0) return;

            Debug.Log($"[Bartender P3] catch-up: 偵測到 {overdue.Count} 個過期 work session, 補 fire end...");
            foreach (var (sid, manager) in overdue)
            {
                try
                {
                    string scriptPath = Path.Combine(UCL_RepoPath.RepoRoot,
                        "CardGame", "Assets", "UCL", "UCL_Core", "Tools~", "AgentCommands", "work_session.py");
                    if (!File.Exists(scriptPath))
                    {
                        Debug.LogWarning($"[Bartender P3] work_session.py 找不到, skip {sid}");
                        continue;
                    }
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = $"\"{scriptPath}\" end --session \"{sid}\" --who \"{manager}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = UCL_RepoPath.RepoRoot,
                    };
                    System.Diagnostics.Process.Start(psi);
                    s_WorkSessionEndSpawned.Add(sid);
                    Debug.Log($"[Bartender P3] spawned end subprocess: session={sid}, manager={manager}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Bartender P3] spawn fail for {sid}: {e.Message}");
                }
            }
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
            // Bug fix (Tim QA 2026-05-12 inline parse 撞到): 不能在 trigger list 空時 early return —
            // inline registration ([進行留言] / [進行時間規則]) 需在沒任何 trigger 時也能掃描.
            // 改 contract: 永遠掃新訊息 (推進 last_seq + inline parse); 有 trigger 才跑 keyword match.
            var triggerList = UCL_BartenderIO.LoadTriggers();
            if (triggerList == null) triggerList = new UCL_BartenderTriggerList();
            if (triggerList.triggers == null) triggerList.triggers = new List<UCL_BartenderTrigger>();

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
            // 保證 'tavern' 主廳永遠被掃 (給 inline registration parse 用, 即使無任何 trigger)
            if (!byRoom.ContainsKey("tavern")) byRoom["tavern"] = new List<UCL_BartenderTrigger>();

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

                    // Inline registration 偵測 — 含 [進行留言] / [進行時間規則] 等 marker 的訊息
                    // 視為 "control message", 走 inline parse 註冊 + post 確認, 跳過 keyword match
                    // (避免 registration body 內含 keyword 自觸發新註冊的 trigger)
                    var kind = UCL_BartenderInlineParser.DetectKind(msg.body);
                    if (kind != UCL_BartenderInlineParser.InlineCommandKind.None)
                    {
                        bool registered = HandleInlineRegistration(kind, msg, roomId);
                        if (registered) anyFiredThisTick = true;
                        // 註冊訊息本身不參與 keyword trigger match (control msg)
                        continue;
                    }

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
        // 區塊：Inline registration handler — 解析 [進行留言] / [進行時間規則] marker → register
        // 物理意義：使用者在 tavern 直接發 control msg, daemon 解析後走跟 Cmd_Bartender 同 IO 層,
        //          register 完發 bartender 確認回應 (跟 fire trigger 同樣 fireDiscordMirror=false batch).
        // 數值影響：register 成功才 return true, daemon 才會把這筆 fire 計入 anyFiredThisTick
        // ===========================================================
        static bool HandleInlineRegistration(
            UCL_BartenderInlineParser.InlineCommandKind kind,
            UCL_ChatMessage msg, string roomId)
        {
            string creator = msg.sender_id ?? "";
            string creatorName = string.IsNullOrEmpty(msg.sender_name) ? creator : msg.sender_name;

            if (kind == UCL_BartenderInlineParser.InlineCommandKind.AddTrigger)
            {
                var spec = UCL_BartenderInlineParser.ParseTrigger(msg.body);
                if (!spec.valid)
                {
                    PostBartenderConfirm(roomId,
                        $"❌ **inline 留言註冊失敗** (來自 {creatorName})\n\n錯誤: {spec.error}\n\n" +
                        "格式: `[進行留言] key=<關鍵字> msg=<內容> targets=<逗號分隔> tokens=<int>`",
                        new Dictionary<string, string> { { "subtag", "inline-register-fail" } });
                    return true;
                }
                string id = UCL_BartenderIO.RegisterTrigger(
                    creator, creatorName, spec.targets, spec.keyword, spec.message,
                    spec.tokens, string.IsNullOrEmpty(spec.room) ? roomId : spec.room);
                string targetsDisp = (spec.targets == null || spec.targets.Count == 0) ? "(任何人)" : string.Join(",", spec.targets);
                PostBartenderConfirm(roomId,
                    $"✅ **inline 留言已註冊** by {creatorName}\n\n" +
                    $"- id: `{id}`\n- key: `{spec.keyword}`\n- targets: {targetsDisp}\n" +
                    $"- tokens: {spec.tokens} (= 觸發 {spec.tokens} 次)\n- msg: {Truncate(spec.message, 100)}",
                    new Dictionary<string, string> {
                        { "subtag", "inline-register-ok" },
                        { "trigger_id", id }, { "trigger_creator", creator },
                    });
                return true;
            }

            if (kind == UCL_BartenderInlineParser.InlineCommandKind.Help)
            {
                // 區塊職責：使用者於 tavern 發 [help] → 酒保直接 post 服務清單
                // 物理意義：純 hardcoded markdown, 不 spawn / 不 IO / 不 parse args, latency 近 0
                // 數值影響：每 [help] inline 就多一條酒保訊息, 預期低頻不會洗版
                // 維護備註：新增 inline marker 或 Cmd_Bartender op 時務必同步更新 BuildHelpBody()
                PostBartenderConfirm(roomId,
                    BuildHelpBody(creatorName),
                    new Dictionary<string, string> { { "subtag", "help-listing" } });
                return true;
            }

            if (kind == UCL_BartenderInlineParser.InlineCommandKind.BalanceQuery)
            {
                // 區塊職責：使用者於 tavern 發 [查詢餘額] → spawn python balance_query.py → 酒保 post 結果
                // 物理意義：account 未指定 → 預設查 sender 自己 (msg.sender_id)
                //          spawn 走 main thread 的同步呼叫 (5s timeout fail-safe), tick 期間 IO 接受
                // 數值影響：純 read-only 查詢, 不 mutate state; 失敗 fall back 錯誤訊息
                var spec = UCL_BartenderInlineParser.ParseBalanceQuery(msg.body);
                string targetAccount = string.IsNullOrEmpty(spec.account) ? creator : spec.account;
                if (string.IsNullOrEmpty(targetAccount))
                {
                    PostBartenderConfirm(roomId,
                        $"❌ **餘額查詢失敗** (來自 {creatorName})\n\n錯誤: 未指定 account 且 sender_id 為空。\n" +
                        "格式: `[查詢餘額] account=<id> limit=<N>` (account 可省略 → 自動查自己)",
                        new Dictionary<string, string> { { "subtag", "balance-query-fail" } });
                    return true;
                }
                string queryResult = RunBalanceQuery(targetAccount, spec.limit, out string err);
                if (queryResult == null)
                {
                    PostBartenderConfirm(roomId,
                        $"❌ **餘額查詢失敗** (來自 {creatorName}, account=`{targetAccount}`)\n\n錯誤: {err}",
                        new Dictionary<string, string> {
                            { "subtag", "balance-query-fail" },
                            { "queried_account", targetAccount },
                        });
                    return true;
                }
                PostBartenderConfirm(roomId,
                    $"💰 **餘額查詢結果** (來自 {creatorName})\n\n{queryResult}",
                    new Dictionary<string, string> {
                        { "subtag", "balance-query-ok" },
                        { "queried_account", targetAccount },
                        { "queried_limit", spec.limit.ToString() },
                    });
                return true;
            }

            if (kind == UCL_BartenderInlineParser.InlineCommandKind.AddTimeRule)
            {
                var spec = UCL_BartenderInlineParser.ParseTimeRule(msg.body);
                if (!spec.valid)
                {
                    PostBartenderConfirm(roomId,
                        $"❌ **inline 時間規則註冊失敗** (來自 {creatorName})\n\n錯誤: {spec.error}\n\n" +
                        "格式: `[進行時間規則] id=<id> time=<HH:mm> msg=<提醒> target=<who> grace=<min> penalty=<true/false>`",
                        new Dictionary<string, string> { { "subtag", "inline-timerule-fail" } });
                    return true;
                }
                UCL_BartenderIO.RegisterTimeRule(
                    spec.id, spec.time_hhmm, spec.target, spec.msg,
                    spec.grace, spec.penalty, spec.penalty_interval,
                    spec.target, string.IsNullOrEmpty(spec.room) ? roomId : spec.room);
                PostBartenderConfirm(roomId,
                    $"✅ **inline 時間規則已註冊** by {creatorName}\n\n" +
                    $"- id: `{spec.id}`\n- time: {spec.time_hhmm} (local)\n" +
                    $"- target: {spec.target}\n- grace: {spec.grace} 分鐘\n" +
                    $"- penalty: {(spec.penalty ? $"啟用 (每 {spec.penalty_interval}min)" : "停用")}\n" +
                    $"- msg: {Truncate(spec.msg, 100)}",
                    new Dictionary<string, string> {
                        { "subtag", "inline-timerule-ok" },
                        { "rule_id", spec.id },
                    });
                return true;
            }
            return false;
        }

        static void PostBartenderConfirm(string roomId, string body, Dictionary<string, string> extraMeta)
        {
            var meta = new Dictionary<string, string> { { "tag", BartenderRelayTag } };
            if (extraMeta != null) foreach (var kv in extraMeta) meta[kv.Key] = kv.Value;
            var msg = new UCL_ChatMessage
            {
                sender_id = TavernKeeperId,
                sender_name = "酒保",
                kind = "chat",
                body = body,
                meta = meta,
            };
            UCL_ChatTavernIO.AppendMessage(roomId, msg, fireDiscordMirror: false);
        }

        static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

        // ===========================================================
        // 區塊職責：spawn python AgentCommands/Tools/balance_query.py 取得 markdown 報表
        // 物理意義：read-only 查詢 — daemon 內同步呼叫 (5s timeout), 失敗回 null + err 字串
        // 設計取捨：
        //   - 用 System.Diagnostics.Process 直接 spawn, 不走 queue.json (節省一次 RPC 來回)
        //   - WorkingDirectory = repo root (Application.dataPath / .. / .. = 主專案根, 不是 UCL_Core)
        //     這樣 balance_query.py 內 _REPO_ROOT 自動算對 (它走 Tools/balance_query.py.parent.parent)
        //   - timeout 5s — 339 筆 ledger 全 scan 實測 < 200ms, 留 25x headroom
        //   - python 解析器: 優先 'python', PATH 沒命中由 OS 報錯 (主流系統 Python 都裝)
        // 數值影響：stdout 直接當 markdown 貼回 tavern; stderr / exit code 非 0 視為錯誤
        // ===========================================================
        /// <summary>Public wrapper — 給 Cmd_Bartender op=balance 共用同 spawn 邏輯, 避免 duplicate code.</summary>
        public static string RunBalanceQueryPublic(string account, int limit, out string err)
            => RunBalanceQuery(account, limit, out err);

        // ===========================================================
        // 區塊職責：產生 [help] inline marker 回應內容
        // 物理意義：實際 markdown body 走 UCL_CodeLocalize.Get("Bartender.Help.Body") (zh-Hant/.en 各 1 份),
        //          static switch 不依賴 ModuleService init, daemon [InitializeOnLoad] 早期就可用.
        // 設計取捨 (T18.3 2026-05-18 gura):
        //   - 原 inline $@"..." verbatim 在 Player Build 撞 Mono preprocessor bug (CS1024)
        //   - T18 加 leading space ✘ / T18.2 string concat ✓ 都是 source-level workaround
        //   - T18.3 終極解: 字串搬 UCL_CodeLocalize, source 不再有 $@"..." 跟 ##, 0 bug 風險
        //   - 副作用: 多語系自動支援 + 改字串只改 .cs (還是要 recompile, 但比 Asset 路徑簡單)
        //   - 加新 inline marker / 新 Cmd_Bartender op → 同步更新 UCL_CodeLocalize.zh-Hant.cs 跟 .en.cs
        // ===========================================================
        static string BuildHelpBody(string creatorName)
        {
            return string.Format(
                UCL.Core.LocalizeLib.UCL_CodeLocalize.Get("Bartender.Help.Body"),
                creatorName);
        }

        static string RunBalanceQuery(string account, int limit, out string err)
        {
            err = null;
            try
            {
                string repoRoot = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", ".."));
                string scriptPath = System.IO.Path.Combine(repoRoot, "AgentCommands", "Tools", "balance_query.py");
                if (!System.IO.File.Exists(scriptPath))
                {
                    err = $"balance_query.py 不存在於 {scriptPath} (本專案未啟用餘額查詢)";
                    return null;
                }

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{scriptPath}\" --account \"{account}\" --limit {limit} --format markdown",
                    WorkingDirectory = repoRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                };
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    if (proc == null) { err = "Process.Start return null"; return null; }
                    if (!proc.WaitForExit(5000))
                    {
                        try { proc.Kill(); } catch { }
                        err = "timeout (>5s)";
                        return null;
                    }
                    string stdout = proc.StandardOutput.ReadToEnd();
                    string stderr = proc.StandardError.ReadToEnd();
                    if (proc.ExitCode != 0)
                    {
                        err = $"exit={proc.ExitCode}; stderr={Truncate(stderr ?? "", 300)}";
                        return null;
                    }
                    if (string.IsNullOrWhiteSpace(stdout))
                    {
                        err = "empty stdout";
                        return null;
                    }
                    return stdout.TrimEnd();
                }
            }
            catch (Exception e)
            {
                err = $"spawn exception: {e.Message}";
                return null;
            }
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

        // ===========================================================
        // 區塊：跨日存款保管費 (Anti-inflation, Tim 2026-05-13 拍板 5 token task)
        // 物理意義：超過 OVERNIGHT_THRESHOLD (1000) token 的部分, 跨日時收 OVERNIGHT_FEE_RATE (5%) 保管費.
        //          例: balance=1100 → excess=100 → fee=5 token (floor(100 × 0.05)).
        //          目的: token 通膨抑制 — 鼓勵消費, 防止無限囤積.
        // 數值影響：state.last_overnight_check_date 推進; 每 over-threshold account debit fee;
        //          fee 用 system caller 走 Treasury Debit (account 隔離 bypass), 純 sink (無對應 credit).
        // 觸發：daemon tick 每次跑, 但 state.last_overnight_check_date == today → skip.
        //       跨日 (今天 != state 紀錄日期) → 跑一輪檢查 + 更新 state.
        //       首次啟動 (state 為空) → init today, **不收費** (避免新裝立刻課稅).
        // Idempotency：useRef = "overnight-fee-<date>-<account>", debit 前 scan ledger 確認沒重複 entry.
        //              (state.last_overnight_check_date 給快速 short-circuit, useRef 是 ledger-level safeguard)
        // ===========================================================
        const int OVERNIGHT_THRESHOLD = 1000;
        const double OVERNIGHT_FEE_RATE = 0.05;

        static void CheckOvernightDeposits()
        {
            var state = UCL_BartenderIO.LoadState();
            string today = DateTime.Now.ToString("yyyy-MM-dd");  // local time

            // First-run grace: state 沒紀錄 → init 成 today, 不收費
            if (string.IsNullOrEmpty(state.last_overnight_check_date))
            {
                state.last_overnight_check_date = today;
                UCL_BartenderIO.SaveState(state);
                return;
            }

            // 同一天已 check 過 → skip (短路, 避免每 5s 重跑)
            if (state.last_overnight_check_date == today) return;

            // 跨日了 — 跑一輪檢查
            // 1. Load 全 ledger 一次 (cache reuse 兩個 pass)
            List<TreasuryLedgerEntry> allEntries;
            try { allEntries = UCL_TreasuryLedger.LoadAllEntries(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Bartender] overnight check load ledger fail: {ex.Message}");
                return;  // 不更新 state, 隔下個 tick 再試
            }

            // 2. 蒐集所有 unique account_id
            var allAccounts = new HashSet<string>();
            foreach (var e in allEntries)
            {
                if (!string.IsNullOrEmpty(e.account_id)) allAccounts.Add(e.account_id);
            }

            // 3. Pre-build useRef set (idempotency check, 防 state crash mid-loop 後重跑重複扣)
            // 注意: Debit caller 用 useRef 參數名, 但 TreasuryLedgerEntry 內部欄位是 source_ref
            string useRefPrefix = $"overnight-fee-{today}-";
            var alreadyChargedToday = new HashSet<string>();
            foreach (var e in allEntries)
            {
                if (e.type == "debit" && !string.IsNullOrEmpty(e.source_ref) && e.source_ref.StartsWith(useRefPrefix))
                {
                    alreadyChargedToday.Add(e.account_id);
                }
            }

            // 4. 對每 account 算超額 fee + debit
            //    Tim 2026-05-14 拍板補: audit broadcast 也列出沒扣費的 account 餘額 (full transparency)
            //    → 蒐集兩 list: feeReports (扣費) + safeReports (沒扣費, 但餘額 > 0)
            var feeReports = new List<string>();
            var safeReports = new List<string>();
            int totalFee = 0;
            foreach (var account in allAccounts.OrderBy(a => a))
            {
                int balance;
                try { balance = UCL_TreasuryLedger.GetBalance(account); }
                catch { continue; }
                if (balance <= 0) continue;  // 0 或負數 account 不列 (純 noise)

                // 已扣過 (state 失效但 ledger 正確) → 視為 safe 列出 (debit 已在 ledger)
                if (alreadyChargedToday.Contains(account))
                {
                    safeReports.Add($"- @{account}: balance {balance} (今日已扣過, idempotent skip)");
                    continue;
                }
                if (balance <= OVERNIGHT_THRESHOLD)
                {
                    safeReports.Add($"- @{account}: balance {balance} (≤ {OVERNIGHT_THRESHOLD}, 安全)");
                    continue;
                }

                int excess = balance - OVERNIGHT_THRESHOLD;
                int fee = (int)Math.Floor(excess * OVERNIGHT_FEE_RATE);
                if (fee <= 0)
                {
                    safeReports.Add($"- @{account}: balance {balance} (excess {excess} × 5% = 0, floor 取整免費)");
                    continue;
                }

                string useRef = $"{useRefPrefix}{account}";
                try
                {
                    UCL_TreasuryLedger.Debit(
                        accountId: account,
                        amount: fee,
                        useKind: "overnight_storage_fee",
                        useRef: useRef,
                        description: $"跨日 {today} 存款保管費 5% (超過 {OVERNIGHT_THRESHOLD} 的 {excess} × 5% = {fee})",
                        callerAgentId: "system");
                    feeReports.Add($"- @{account}: balance {balance} → **-{fee} token** (excess {excess} × 5%)");
                    totalFee += fee;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Bartender] overnight fee debit fail for {account}: {ex.Message}");
                }
            }

            // 5. 推進 state.last_overnight_check_date (即使無人扣費也推進, 避免重跑)
            state.last_overnight_check_date = today;
            UCL_BartenderIO.SaveState(state);

            // 6. Broadcast 結果 — Tim 2026-05-13 拍板: 每次 cross-day check 都要有 audit 訊息.
            //    Tim 2026-05-14 拍板補: 沒扣費的 account 餘額也要顯示 (full audit transparency).
            //    一律分 [扣費] + [安全] 兩段, 各自可空, 一致格式.
            string body;
            string subtag;
            string headerLine = $"🏦 **跨日存款保管費結算** ({today}) — 超過 {OVERNIGHT_THRESHOLD} token 部分收 {OVERNIGHT_FEE_RATE * 100:F0}%";
            var bodySb = new System.Text.StringBuilder();
            bodySb.AppendLine(headerLine);
            bodySb.AppendLine();

            if (feeReports.Count > 0)
            {
                bodySb.AppendLine($"### 💸 扣費帳戶 ({feeReports.Count} 個)");
                bodySb.AppendLine(string.Join("\n", feeReports));
                bodySb.AppendLine();
                bodySb.AppendLine($"累計回收: **-{totalFee} token** (anti-inflation sink)");
                bodySb.AppendLine();
                subtag = "overnight-deposit-fee";
            }
            else
            {
                bodySb.AppendLine("### ✅ 無扣費 — 全 account 餘額皆 ≤ threshold");
                bodySb.AppendLine();
                subtag = "overnight-deposit-fee-clean";
            }

            if (safeReports.Count > 0)
            {
                bodySb.AppendLine($"### 🟢 安全帳戶 ({safeReports.Count} 個, 餘額顯示)");
                bodySb.AppendLine(string.Join("\n", safeReports));
                bodySb.AppendLine();
            }

            bodySb.Append("_鼓勵消費避免囤積; 1000 以下不收費_");
            body = bodySb.ToString();
            var msg = new UCL_ChatMessage
            {
                sender_id = TavernKeeperId,
                sender_name = "酒保",
                kind = "chat",
                body = body,
                meta = new Dictionary<string, string>
                {
                    { "tag", BartenderRelayTag },
                    { "subtag", subtag },
                    { "check_date", today },
                    { "total_fee", totalFee.ToString() },
                    { "accounts_charged", feeReports.Count.ToString() },
                    { "accounts_safe", safeReports.Count.ToString() },
                },
            };
            UCL_ChatTavernIO.AppendMessage("tavern", msg);  // 預設 fire mirror = Discord broadcast
        }
    }
}
#endif
