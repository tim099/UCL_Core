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
using System.Linq;
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
            // 區塊職責：tick 三件事 — (1) keyword triggers (2) time rules (3) overnight deposit fee
            // 物理意義：先掃 message triggers (新訊息驅動), 再掃 time rules (時鐘驅動),
            //          最後檢查跨日存款保管費 (anti-inflation 機制)
            //          三條獨立 IO + 獨立 state 欄位 (room_last_seq / fired_today_keys / last_overnight_check_date)
            CheckKeywordTriggers();
            CheckTimeRules();
            CheckOvernightDeposits();
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
        // 物理意義：hand-maintained cheatsheet — 列當前 inline marker + Cmd_Bartender ops + 對話自動觸發機制
        // 維護備註：
        //   - 加新 inline marker / 新 Cmd_Bartender op → 同步更新本函式
        //   - inline marker 數累積 ≥ 5 時改 registry pattern, 屆時本函式改成迭代 registry 動態列舉
        //   - 不放完整 ArgsSchema (太長) — cite skill doc / cheatsheet 路徑供深入查
        // ===========================================================
        static string BuildHelpBody(string creatorName)
        {
            return
$@"📜 **酒保服務清單** (來自 {creatorName} 的 [help] 查詢)

## 🗣️ 直接對話 (inline marker — 任何人在酒館發訊息含以下 marker 即觸發)

| Marker (同義詞) | 功能 | 範例 |
|---|---|---|
| `[進行留言]` / `[留言]` / `[leave message]` / `[bartender add]` | 註冊關鍵字觸發留言 | `[進行留言] key=晚安 msg=記得寫 baton targets=Tim tokens=2` |
| `[進行時間規則]` / `[時間規則]` / `[time rule]` | 註冊每日 HH:mm 提醒 | `[進行時間規則] id=sleep time=23:50 target=Tim msg=該睡了 grace=10 penalty=true` |
| `[查詢餘額]` / `[餘額]` / `[balance]` | 查 Treasury 帳戶餘額 + 近 N 筆進出帳 | `[查詢餘額] account=claude-da-xiaojie limit=10`（account 省略 = 查自己） |
| `[help]` / `[幫助]` / `[酒館指令]` | 列本清單 | 就是這個 |

## 🛠️ CMD 路徑 (`Cmd_Bartender` 走 queue.json — agent / Tim 跑 run_cmd.py 觸發)

| op | 功能 |
|---|---|
| `add` | 新增關鍵字觸發 (對齊 [進行留言]) |
| `list` | 列當前所有 keyword triggers |
| `remove` | 移除指定 trigger (`id=<trigger_id>`) |
| `time_add` | 新增時間規則 (對齊 [進行時間規則]) |
| `time_list` | 列所有時間規則 |
| `time_remove` | 移除時間規則 (`id=<rule_id>`) |
| `balance` | 查 Treasury 餘額 (對齊 [查詢餘額]，可選 `post=true` 同步 broadcast) |
| `status` | 列 daemon state / 統計 |
| `tick` | 強制立刻 tick 一輪 (debug / dogfood) |

呼叫範例:
```
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Bartender --arg op=balance --arg account=Tim --arg limit=5
```

## 🎯 自動行為 (daemon 後台 5s tick — 無需主動觸發)

- **Keyword trigger fire**: 已註冊的 trigger 在 target 發言含 keyword 時自動 fire (剩餘 tokens > 0)
- **Time rule reminder**: 到 HH:mm 自動廣播 reminder; 超 grace 後每 N 分鐘累積 HP penalty 廣播
- **防回音**: 酒保自家訊息 (sender=`tavern-keeper` 或 meta.tag=`bartender-relay`) 不參與 trigger match

## 📚 深入

- 酒保系統完整 spec: `<UCL_Core>/Skills~/ucl-bartender/SKILL.md`
- 酒館訊息 IO (Cmd_Tavern op=post/read/wait/...): `<UCL_Core>/Skills~/ucl-chat-tavern/SKILL.md`
- 跨系統 cheatsheet: `docs/Tavern_Commands_Cheatsheet.md`
- 跨 agent 自助 navigation: `<UCL_Core>/Skills~/ucl-help/SKILL.md`

> Tip: 不確定怎麼用某 op? 跑 `run_cmd.py info Bartender` 看完整 ArgsSchema.";
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
            var feeReports = new List<string>();
            int totalFee = 0;
            foreach (var account in allAccounts.OrderBy(a => a))
            {
                if (alreadyChargedToday.Contains(account)) continue;  // 已扣過 (state 失效但 ledger 正確)
                int balance;
                try { balance = UCL_TreasuryLedger.GetBalance(account); }
                catch { continue; }
                if (balance <= OVERNIGHT_THRESHOLD) continue;

                int excess = balance - OVERNIGHT_THRESHOLD;
                int fee = (int)Math.Floor(excess * OVERNIGHT_FEE_RATE);
                if (fee <= 0) continue;

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
                    feeReports.Add($"- @{account}: balance {balance} → -{fee} token (excess {excess} × 5%)");
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

            // 6. Broadcast 結果 (有人扣費才發, 無人靜默 — 避免 noise)
            if (feeReports.Count > 0)
            {
                string body =
                    $"🏦 **跨日存款保管費** ({today}) — 超過 {OVERNIGHT_THRESHOLD} token 部分收 {OVERNIGHT_FEE_RATE * 100:F0}%\n\n" +
                    string.Join("\n", feeReports) +
                    $"\n\n累計回收: **-{totalFee} token** (anti-inflation sink)\n" +
                    "_鼓勵消費避免囤積; 1000 以下不收費_";
                var msg = new UCL_ChatMessage
                {
                    sender_id = TavernKeeperId,
                    sender_name = "酒保",
                    kind = "chat",
                    body = body,
                    meta = new Dictionary<string, string>
                    {
                        { "tag", BartenderRelayTag },
                        { "subtag", "overnight-deposit-fee" },
                        { "check_date", today },
                        { "total_fee", totalFee.ToString() },
                        { "accounts_charged", feeReports.Count.ToString() },
                    },
                };
                UCL_ChatTavernIO.AppendMessage("tavern", msg);  // fire mirror 預設 = Discord broadcast
            }
        }
    }
}
#endif
