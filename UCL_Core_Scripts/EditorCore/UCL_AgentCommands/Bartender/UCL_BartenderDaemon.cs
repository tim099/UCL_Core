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
using System.Text;
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
        // Process 註冊中心的 tag（硬規則：每顆外部 Process 都要登記）。
        const string PROC_TAG_PY = "bartender_tick_py";

        // 區塊職責：tick 間隔 + bartender 識別常數
        // 物理意義：CHECK_INTERVAL 5s 是 latency vs IO load 折衷 (太頻繁吃 disk, 太疏延遲高)
        const double CHECK_INTERVAL_SECONDS = 5.0;

        /// <summary>Bartender post 訊息用的 sender_id (對齊既有 identity asset "tavern-keeper").</summary>
        public const string TavernKeeperId = "tavern-keeper";

        /// <summary>Bartender 訊息的 meta tag — 自家訊息標記, 防回音 (loop) 用.</summary>
        public const string BartenderRelayTag = "bartender-relay";
        public static bool Initialized { get; private set; } = false;
        static double s_LastCheckTime = 0;
        const string TickStateIdle = "Idle";
        const string TickStateCheckKeywordTriggers = nameof(CheckKeywordTriggers);
        const string TickStateCheckTimeRules = nameof(CheckTimeRules);
        const string TickStateCheckOvernightDeposits = nameof(CheckOvernightDeposits);

        // ===========================================================
        // 區塊：Tick 進度可視化 + 可取消 (2026-07-26, Tim 反映 Editor 卡住 "Hold on..." 好幾分鐘看不出卡在哪)
        // 物理意義：EditorApplication.update 若在單一 callback 內跑太久, Unity 只會顯示籠統的
        //          "Waiting for user code in UCL_Core.dll to finish executing" — 無法得知卡在 daemon
        //          內哪個階段。這裡在真正可能耗時的階段 (item 數量夠多 / 呼叫外部 subprocess 同步等待)
        //          插入 EditorUtility.DisplayProgressBar，讓卡住時至少看得到目前是哪個階段 + 進度；
        //          可取消的階段 (subprocess 等待 / 大量訊息掃描) 額外支援使用者按 Cancel 中途放棄。
        // 設計取捨：
        //   - 只在「item 數量超過門檻」或「同步等待外部程序」時才顯示，避免每 5s 空轉 tick 也跳窗口洗畫面。
        //   - TickInternal 外層包 try/finally 保證離開時一定 ClearProgressBar (即使中途 exception 也不留殘影)。
        //   - DisplayCancelableProgressBar 每次呼叫都會逼 Editor 重繪一次 — 只在迴圈內「每 N 筆 / 每 100ms」
        //     呼叫一次，兼顧「使用者按 Cancel 後盡快反應」跟「不要因為過度呼叫本身拖慢迴圈」。
        // ===========================================================
        static bool s_ProgressBarShown = false;

        // ===========================================================
        // 區塊：Tick 相位計時器（2026-08-15，Tim 回報「初次啟動 Editor 卡三分鐘」）
        // 物理意義：進度條只在**人在場**時有用；`_tick_state.txt` 又會在 tick 正常結束時被改回 Idle。
        //          於是「昨天早上那次卡三分鐘卡在哪」事後無法回答 —— 這次能定位純靠人工拿
        //          stall gap 去對 closing 檔 mtime 跟廣播訊息時間，那是對帳不是機制。
        //          本計時器把每個相位的耗時記下來，tick 結束時交給 UCL_BartenderIO.AppendSlowTick 落盤。
        // 數值影響：一個 Stopwatch + 每相位一筆 struct；正常 tick 不落盤（門檻 3s）。
        // 設計取捨：
        //   - **量測與落盤分層**：這裡只量，寫檔一律走 IO 層，避免 daemon 又長出一套檔案格式。
        //   - note 欄位存「這個相位處理了幾個東西」（檔數 / 帳戶數）—— 只有時間沒有基數的話，
        //     下次看到「170 秒」還是不知道是單位成本高還是量大，**兩者的修法完全不同**。
        //   - static 單例：tick 在主執行緒序列化執行，不會有兩個 tick 同時在跑。
        // ===========================================================
        sealed class TickProfiler
        {
            readonly System.Diagnostics.Stopwatch m_Watch = System.Diagnostics.Stopwatch.StartNew();
            readonly List<(string name, double ms, string note)> m_Phases = new List<(string, double, string)>();
            double m_LastMarkMs = 0;

            /// <summary>結算「上一個 Mark 到現在」為一個相位。note 填基數（檔數 / 筆數），可空。</summary>
            public void Mark(string name, string note = null)
            {
                double now = m_Watch.Elapsed.TotalMilliseconds;
                m_Phases.Add((name, now - m_LastMarkMs, note));
                m_LastMarkMs = now;
            }

            public double TotalMs => m_Watch.Elapsed.TotalMilliseconds;

            public string ToJsonArray()
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var sb = new System.Text.StringBuilder("[");
                for (int i = 0; i < m_Phases.Count; i++)
                {
                    var p = m_Phases[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":\"").Append(UCL_BartenderIO.EscapeJsonString(p.name)).Append('"')
                      .Append(",\"ms\":").Append(p.ms.ToString("F1", inv));
                    if (!string.IsNullOrEmpty(p.note))
                        sb.Append(",\"note\":\"").Append(UCL_BartenderIO.EscapeJsonString(p.note)).Append('"');
                    sb.Append('}');
                }
                return sb.Append(']').ToString();
            }
        }

        /// <summary>本次 tick 的計時器；只在 TickInternal 期間非 null（其餘時間讀到 null 是正常的）。</summary>
        static TickProfiler s_Profiler;

        /// <summary>相位標記 —— s_Profiler 為 null 時安靜跳過，讓被量測的程式碼不必到處判 null。</summary>
        static void MarkPhase(string name, string note = null) => s_Profiler?.Mark(name, note);

        /// <summary>本次 tick 是否走了跨日結算那條重路徑 —— 台帳用它一眼分開「日常 tick 變慢」與「跨日結算慢」。</summary>
        static bool s_TickWasCrossDay = false;

        static void ShowProgress(string title, string info, float progress)
        {
            try
            {
                EditorUtility.DisplayProgressBar(title, info, progress);
                s_ProgressBarShown = true;
            }
            catch { /* IMGUI 例外不擋主流程 (e.g. 非 main thread 誤呼叫防禦) */ }
        }

        /// <summary>顯示可取消進度條；回傳 true = 使用者按了 Cancel。</summary>
        static bool ShowCancelableProgress(string title, string info, float progress)
        {
            try
            {
                s_ProgressBarShown = true;
                return EditorUtility.DisplayCancelableProgressBar(title, info, progress);
            }
            catch { return false; }
        }

        static void ClearProgress()
        {
            if (!s_ProgressBarShown) return;
            try { EditorUtility.ClearProgressBar(); } catch { }
            s_ProgressBarShown = false;
        }

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
                Initialized = true;
                // 不在 ctor 內動 IO (avoid first-load 卡 Editor 啟動)
                // tick 第一次跑時才 lazy load triggers / rules
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Bartender] daemon init fail: {e.Message}");
            }
        }

        // ===========================================================
        // 區塊職責：Editor 存活心跳 — 每 HEARTBEAT_INTERVAL_SECONDS 複寫一次 _heartbeat.txt
        //          （檔名以 UCL_BartenderIO.HeartbeatFile 為準，別在註解裡另寫一份會漂的副本）
        // 物理意義：讓「Editor 的 update 迴圈還在跑」變成磁碟上 stat 得到的事實。
        //          編譯 / domain reload 期間 EditorApplication.update 不跑 → 心跳自然停 →
        //          外部工具**不必送 Cmd 等 round-trip**（實測要 2-13 秒）就能判斷 Editor 忙不忙。
        // 數值影響：每 0.5s 最多一次「單檔單行複寫」（約 25 bytes）。
        // 設計取捨：0.5s（Tim 2026-08-04 定調）—— 判準是「大部分 compile 不會比這個快」，
        //          所以任何一次編譯都必然跨過至少一個心跳週期、必然被看見。
        //          刻意比業務 tick (CHECK_INTERVAL 5s) 密十倍：一次編譯只有 4-7 秒，
        //          用 5s 解析度會整個蓋不住 —— **測不到的訊號等於沒有**。
        // 邊界：心跳停止的充分條件不只編譯 —— domain reload / Editor 沒有焦點被降頻 /
        //      modal dialog / Editor 掛掉 / Editor 關閉都會停。**它證明「沒在 tick」，
        //      不證明「正在編譯」**；要斷定編譯還要配 .compile_status.json 一起看。
        // ===========================================================
        const double HEARTBEAT_INTERVAL_SECONDS = 0.5;
        static double s_LastHeartbeat = -999.0;

        static void BeatHeartbeat()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - s_LastHeartbeat < HEARTBEAT_INTERVAL_SECONDS) return;
            s_LastHeartbeat = now;
            UCL_BartenderIO.WriteHeartbeat();
        }

        /// <summary>強制立刻 tick (給 Cmd_Bartender op=tick 手動觸發 / 測試用).</summary>
        public static void ForceTick()
        {
            try { TickInternal(); } catch (Exception e) { Debug.LogWarning($"[Bartender] ForceTick fail: {e.Message}"); }
        }

        // ===========================================================
        // 區塊：系統重啟 handler — 聊天酒館系統由 OFF→ON 時 (或控制台手動重啟) fire
        // 物理意義：強制下個 tick 立即執行 (不等 5s 間隔)。
        // 數值影響：純記憶體 state 重置，不動檔案；下個 EditorApplication.update 進 TickInternal。
        // ===========================================================
        static void OnSystemRestart()
        {
            s_LastCheckTime = 0;                 // 下次 update 立即進 TickInternal (繞過 CHECK_INTERVAL 間隔)
            Debug.Log("[Bartender] 系統重啟 — 游標重置，下個 tick 立即運行");
        }

        // ===========================================================
        // Tick — 主迴圈, 每 CHECK_INTERVAL 秒進一次
        // ===========================================================
        static void Tick()
        {
            try
            {
                // ⚠ 心跳必須寫在**所有 early-return 之前**（2026-08-04）：
                //   心跳回答的是「Editor 的 update 迴圈還活著嗎」，跟酒館業務開關無關。
                //   若放在下面 IsEnabled 閘門之後，「Tim 把酒館系統關掉」會讓心跳停止，
                //   讀取端就會把它誤讀成「還在編譯」—— 用一個層級的狀態去回答另一個層級的問題，
                //   跟 in_progress 那隻誤判同構。
                BeatHeartbeat();

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
            // 註 (2026-07-29): 原本這裡還有 work session 自動開工 / 過期結算兩條 sweep，
            //                 因 script 路徑硬編碼在本專案永遠 miss（靜默失效）已移除，見下方區塊註解。
            // 全 tick 包 try/finally — 任何階段丟例外或使用者按 Cancel 提早 return，
            // 進度條都保證被清掉，不留殘影卡住 Editor UI。
            bool completed = false;
            var profiler = new TickProfiler();
            s_Profiler = profiler;
            s_TickWasCrossDay = false;
            try
            {
                UCL_BartenderIO.WriteTickState(TickStateCheckKeywordTriggers);
                CheckKeywordTriggers();
                profiler.Mark(TickStateCheckKeywordTriggers);

                UCL_BartenderIO.WriteTickState(TickStateCheckTimeRules);
                CheckTimeRules();
                profiler.Mark(TickStateCheckTimeRules);

                UCL_BartenderIO.WriteTickState(TickStateCheckOvernightDeposits);
                CheckOvernightDeposits();   // 內部自行 Mark 子相位（跨日那條路才是重的）
                completed = true;
            }
            finally
            {
                ClearProgress();
                // 僅在三段皆正常返回時改回 Idle；例外時保留最後進入的 state，
                // 才不會把失敗位置覆寫掉，讓外部診斷重新失去證據。
                if (completed) UCL_BartenderIO.WriteTickState(TickStateIdle);

                // 相位台帳 —— 放在 finally 內，例外中斷的慢 tick 也留得下分解。
                // ⚠ 但 Editor 被殺 / 當掉時這行不會執行（見 TickPhaseFile 的邊界註解）。
                s_Profiler = null;
                if (!completed) profiler.Mark("aborted");
                UCL_BartenderIO.AppendSlowTick(profiler.TotalMs, profiler.ToJsonArray(), s_TickWasCrossDay);
            }
        }

        // ===========================================================
        // 區塊：work session 自動化（2026-07-29 移除，Tim 拍板）
        // 移除的東西：
        //   - CheckWorkSessionStart / SpawnWorkSessionStart — 掃 tavern「上班…到 HH:mm」→ spawn
        //     work_session.py start（酒保宣布開工）
        //   - CheckOverdueWorkSessions — sweep work_sessions.json 過期 session → spawn work_session.py end
        // 為什麼移除：兩處都把 script 路徑寫死成 RepoRoot/CardGame/Assets/UCL/UCL_Core/...（且無 fallback），
        //            本專案 UCL_Core 掛在 Assets/Plugins/UCL_Core → File.Exists 永遠 false → 每次都
        //            「找不到, skip」。功能在本專案從未生效，屬 install-path 硬編碼失效（見 ucl-core-paths）。
        // 之後要復原怎麼做：改為 C# 端直接讀寫 <DataRoot>/ChatTavern/work_sessions.json，
        //            與 work_session.py CLI 共讀同一份（雙實作、同 awakening.py ↔ UCL_LoginStatusPage 先例），
        //            不要再 spawn python subprocess。
        // ===========================================================

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
            // 2026-07-28: Discord 鏡像改由 UCL_DiscordMirrorDaemon poll，寫入端不再觸發任何 spawn
            //   → 原「tick 內抑制 mirror、tick 末批次 spawn 一次」的協調機制連同 python 路徑一起移除。

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

            // 進度可視化門檻 (2026-07-26) — 只有「新訊息數夠多」才顯示進度條, 避免每 5s 空轉 tick
            // 也跳窗口洗畫面. 門檻抓 20: 正常單筆 post 觸發的 tick 遠低於此, 只有 domain-reload /
            // Editor 閒置很久後第一次 tick 面對大量新訊息時才會觸發.
            const int progressThreshold = 20;
            bool userCancelledScan = false;

            foreach (var kv in byRoom)
            {
                if (userCancelledScan) break;

                string roomId = kv.Key;
                var roomTriggers = kv.Value;

                // 確認 room 存在 — 不存在跳過 (避免 IO error)
                var room = UCL_ChatTavernIO.GetRoom(roomId);
                if (room == null) continue;

                int lastSeq = UCL_BartenderIO.GetLastSeq(state, roomId);
                // 只讀游標後的新訊息 (perf cache follow-up #1) — 不再全讀整房再跳過舊的.
                // msg.seq = 檔序位 (1-based), helper 已保證只回 > lastSeq 者.
                var newMsgs = UCL_ChatTavernIO.LoadMessagesAfterSeq(roomId, lastSeq);
                if (newMsgs == null || newMsgs.Count == 0) continue;

                bool showProgress = newMsgs.Count >= progressThreshold;
                int total = newMsgs.Count;

                int maxSeq = lastSeq;
                for (int i = 0; i < newMsgs.Count; i++)
                {
                    var msg = newMsgs[i];
                    if (msg == null) continue;
                    int effectiveSeq = msg.seq;
                    if (effectiveSeq > maxSeq) maxSeq = effectiveSeq;

                    // 防回音 — bartender 自家訊息 (sender / meta tag) 不參與 match
                    if (IsBartenderOwnMessage(msg)) continue;

                    // Inline registration 偵測 — 含 [進行留言] / [進行時間規則] 等 marker 的訊息
                    // 視為 "control message", 走 inline parse 註冊 + post 確認, 跳過 keyword match
                    // (避免 registration body 內含 keyword 自觸發新註冊的 trigger)
                    var kind = UCL_BartenderInlineParser.DetectKind(msg.body);
                    if (kind != UCL_BartenderInlineParser.InlineCommandKind.None)
                    {
                        bool registered = HandleInlineRegistration(kind, msg, roomId);
                        // 註冊訊息本身不參與 keyword trigger match (control msg)
                    }
                    else if (UCL_BartenderCliService.IsCliMessage(msg, out var cliSettings))
                    {
                        // 區塊職責：酒館 CLI（`cmd …`）與它的 Y／N 確認回覆。
                        // 物理意義：這是第四種發言來源，也是**唯一會改變 Editor 狀態**的一種
                        //          （前三種只是發話）。所以它自己帶三道關卡：總開關／白名單／二次確認。
                        // ⚠ 排在 mention 與 keyword trigger **之前**：一則訊息一種處理。
                        //   排在後面的話 `cmd remote-window on` 這句會同時被 keyword 比對到，
                        //   於是一個指令換來兩則發言，而看起來像 bug。
                        // ⚠ 確認回覆（使用者只打 `y`）**不以 prefix 開頭** ⇒ IsCliMessage 收整個 msg，
                        //   要靠發話者 ＋ 落磁碟的 pending 才判得出來；只看 body 字首會漏掉它。
                        UCL_BartenderCliService.Handle(msg, roomId, cliSettings);
                    }
                    else if (UCL_BartenderMentionService.IsMention(msg.body))
                    {
                        // 區塊職責：`@酒保` 被點名 → 交給 mention service（async，不在 tick 裡等）。
                        // 物理意義：這是第三種發言來源（前兩種是 keyword trigger 與 time rule），
                        //          而它沒有 trigger 的預算上限、也沒有 time rule 的每日一次，
                        //          所以節流長在 service 裡（冷卻 ＋ 每日上限 ＋ 已回 seq 落磁碟）。
                        // ⚠ 點名訊息**不再參與 keyword trigger 比對** —— 一則訊息一種處理，
                        //   否則同一句話會同時觸發罐頭 trigger 與 mention 回話（兩則發言、看起來像 bug）。
                        // ⚠ 生成期間 service 自己有 s_Running 閘：tick 每 5s 一次而生成可能數十秒，
                        //   沒有那道閘會疊起來（顯存與訊息都會爆）。
                        UCL_BartenderMentionService.HandleMentionAsync(msg, roomId);
                    }
                    else
                    {
                        // 跑所有 trigger 比對
                        foreach (var t in roomTriggers)
                        {
                            if (t.remaining_triggers <= 0) continue;
                            if (!IsTargetMatch(msg, t.targets)) continue;
                            if (!IsKeywordMatch(msg.body, t.keyword)) continue;

                            // 命中 — fire bartender 訊息 (跳過內部 mirror, tick 末批次處理)
                            FireTrigger(t, msg, roomId);
                            t.remaining_triggers -= 1;
                            triggersDirty = true;
                        }
                    }

                    // 本筆已完整處理（maxSeq 已含這筆）— 這裡才安全 break, 不會漏處理半筆訊息.
                    if (showProgress)
                    {
                        if (ShowCancelableProgress(
                                "酒保 — 掃描酒館訊息",
                                $"房間 '{roomId}': 第 {i + 1}/{total} 筆 (累積 lastSeq→{maxSeq})…",
                                (float)(i + 1) / total))
                        {
                            userCancelledScan = true;
                            Debug.Log($"[Bartender] 使用者取消訊息掃描 — 房間 '{roomId}' 只處理到第 {i + 1}/{total} 筆, " +
                                      $"游標仍推進到 seq={maxSeq}（未處理的訊息下個 tick 會繼續, 不會被跳過）。");
                            break;
                        }
                    }
                }

                if (maxSeq > lastSeq)
                {
                    UCL_BartenderIO.SetLastSeq(state, roomId, maxSeq);
                    stateDirty = true;
                }

                if (userCancelledScan) break;
            }

            // 移除 remaining=0 的 trigger
            int removed = triggerList.triggers.RemoveAll(t => t == null || t.remaining_triggers <= 0);
            if (removed > 0) triggersDirty = true;

            if (triggersDirty) UCL_BartenderIO.SaveTriggers(triggerList);
            if (stateDirty) UCL_BartenderIO.SaveState(state);

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
        //          register 完發 bartender 確認回應 (跟 fire trigger 同路徑).
        // 數值影響：register 成功才 return true
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
                // 區塊職責：使用者於 tavern 發 [查詢餘額] → C# 原生查 UCL_TreasuryLedger → 酒保 post 結果
                // 物理意義：account 未指定 → 預設查 sender 自己 (msg.sender_id)
                //          純 C# 查詢（記憶體快取 + ledger 檔），無外部 process ——
                //          為什麼不能用推導路徑的外部查詢，見 RunBalanceQuery 的血證註解
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
                        "格式: `[進行時間規則] id=<id> time=<HH:mm> msg=<提醒>`",
                        new Dictionary<string, string> { { "subtag", "inline-timerule-fail" } });
                    return true;
                }
                UCL_BartenderIO.RegisterTimeRule(
                    spec.id, spec.time_hhmm, spec.msg,
                    string.IsNullOrEmpty(spec.room) ? roomId : spec.room);
                PostBartenderConfirm(roomId,
                    $"✅ **inline 時間規則已註冊** by {creatorName}\n\n" +
                    $"- id: `{spec.id}`\n- time: {spec.time_hhmm} (local)\n" +
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
            UCL_ChatTavernIO.AppendMessage(roomId, msg);
        }

        static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

        // ===========================================================
        // 區塊職責：查 UCL_TreasuryLedger 產生餘額 + 近 N 筆進出帳的 markdown 報表（C# 原生）
        // 物理意義：read-only —— 餘額的唯一擁有者是 UCL_TreasuryLedger（增量快取 + snapshot），
        //          這裡只讀不算第二套。inline [查詢餘額] 與 Cmd_Bartender op=balance 共用本方法，
        //          於是「同一個餘額只有一套算法」。
        // 數值影響：回傳 markdown 直接貼回 tavern；失敗回 null + err 字串（不吞例外）。
        //          ⚠ 為什麼不可以改回「外部查詢 + 自己推導 repo root」：血證在 RunBalanceQuery 內
        //          （推導出去的路徑命中另一棵資料樹，回報了差 876 token 的假數字而不報錯）。
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
                // 2026-08-17：本方法原本 **spawn python balance_query.py**，已整段改為 C# 原生查詢。
                //
                // 🩸 為什麼非換不可（不是為了少一顆 process，是它一直在報錯的數字）：
                //   舊版用 `Path.Combine(Application.dataPath, "..", "..")` 推 repo root ——
                //   那假設 Unity 專案是 repo 的**子目錄**（EOV 的 CardGame/Assets）。
                //   扁平佈局的專案算出來是 **repo 的上一層**，而那裡剛好留著一整棵舊的
                //   AgentCommands（含 Tools/balance_query.py）。於是它**沒有報錯**，
                //   還成功回傳了一個看起來完全正常的數字 —— 實測 Myth 帳戶
                //   舊路徑回 453、真實帳本是 1329，**差 876 token，而酒館裡每個人查到的都是 453**。
                //   「找不到檔」至少會喊；「找到另一個宇宙的檔」不會。
                //
                // 換掉之後一次消滅四件事：路徑推導、外部 process（含註冊/逾時/取消/進度條）、
                // python 相依、以及「同一個餘額有兩套算法」。
                // UCL_TreasuryLedger 是餘額的唯一擁有者（增量快取 + snapshot），也比全掃快。
                int bal = Treasury.UCL_TreasuryLedger.GetBalance(account);
                // ⭐ 2026-09-22（TASK-0274）起餘額與明細**同一本帳**（都是新銀行）——
                //   此前明細來自凍結於 09-18 的舊帳本，兩者時代不同、加不回同一個數字。
                var entries = Treasury.UCL_TreasuryLedger.Audit(account);

                int credit = 0, debit = 0;
                foreach (var e in entries)
                {
                    if (e.type == "credit") credit += e.amount; else debit += e.amount;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"💰 **{account} 帳戶餘額**: `{bal}` tavern_token");
                sb.AppendLine($"📊 累計: +{credit} / -{debit}（共 {entries.Count} 筆，資料源＝新銀行 `Bank/`）"
                              + $"　⇒ 淨額 {credit - debit}"
                              + (credit - debit == bal ? "，與上面的餘額**相符**"
                                                       : $"，⚠ 與上面的餘額 {bal} **對不起來**（差 {bal - (credit - debit)}）"));

                if (limit > 0 && entries.Count > 0)
                {
                    entries.Sort((a, b) => string.CompareOrdinal(b.ts, a.ts));   // newest first
                    sb.AppendLine();
                    int n = Math.Min(limit, entries.Count);
                    sb.AppendLine($"**最近 {n} 筆進出帳** (newest first):");
                    for (int i = 0; i < n; i++)
                    {
                        var e = entries[i];
                        string arrow = e.type == "credit" ? "↑+" : "↓-";
                        string desc = Truncate(e.source_description ?? "", 80);
                        string refPart = string.IsNullOrEmpty(e.source_ref) ? "" : $" [ref: `{e.source_ref}`]";
                        sb.AppendLine($"- `{e.ts}` {arrow}{e.amount} `{e.source_kind}` → 餘額 {e.balance_after}"
                                      + (string.IsNullOrEmpty(desc) ? "" : $" — {desc}") + refPart);
                    }
                }
                return sb.ToString().TrimEnd();
            }
            catch (Exception e)
            {
                err = $"餘額查詢失敗: {e.Message}";
                return null;
            }
        }

        // ===========================================================
        // Fire trigger — post 留言內容到 tavern (走 AppendMessage 自動 Discord mirror)
        // ===========================================================
        static void FireTrigger(UCL_BartenderTrigger t, UCL_ChatMessage triggeringMsg, string roomId)
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

            UCL_ChatTavernIO.AppendMessage(roomId, msg);
        }

        // ===========================================================
        // 區塊：time rule 掃描
        // 物理意義：對每 rule 比對 now (local) 跟 time_hhmm
        //          - 已到時間且 fired_today_keys 無對應 reminder key → fire reminder
        //          - 跨日自動清舊 fired_today_keys (只保留今日 YYYY-MM-DD)
        //
        // T-Bartender-CatchupCollapse (2026-07-26, Tim 觀察「Editor 長時間沒跑，回來後全部一次補報」懷疑觸發):
        //   Daemon 每 CHECK_INTERVAL(5s) tick 一次，但若 Editor 被關閉/卡住/domain-reload 一段時間，
        //   下次 tick 時 now 可能已經跨過好幾個 rule 的 time_hhmm（例如 15 個「整點快照」rule 從 09:00~23:00
        //   逐時各一條 — Editor 關 6 小時重開，一次補發 6 筆提醒）。每筆 fire 都要 AppendMessage 一次落盤，
        //   在大房間 (1万+ 訊息檔) 上就是這次卡頓 (Hold on 4:53) 的放大器之一。
        //   修法：同一 tick 內若「到期但今天還沒發過 reminder」的 rule 數 > 1，視為 catch-up 補報情境 —
        //   只發「時間最新」的那一條（最貼近現在、資訊最有時效性），其餘視同已發（補標 fired_today_keys，
        //   不落盤、不佔位）並記一筆 Debug.Log 說明被併掉幾條。正常單條到期（count==1）行為完全不變。
        // ===========================================================
        static void CheckTimeRules()
        {
            var ruleList = UCL_BartenderIO.LoadTimeRules();
            if (ruleList?.rules == null || ruleList.rules.Count == 0) return;

            var state = UCL_BartenderIO.LoadState();
            bool stateDirty = false;

            DateTime now = DateTime.Now;  // local time
            string today = now.ToString("yyyy-MM-dd");

            // 清掉非今日的 fired_today_keys (跨日 reset)
            if (state.fired_today_keys != null)
            {
                int before = state.fired_today_keys.Count;
                state.fired_today_keys.RemoveAll(k => string.IsNullOrEmpty(k) || !k.StartsWith(today + "::"));
                if (state.fired_today_keys.Count != before) stateDirty = true;
            }

            // ---- Pass 1: 蒐集本 tick「到期且今天還沒發 reminder」的 rule (不在此 pass 真的 fire) ----
            var dueReminders = new List<(UCL_BartenderTimeRule rule, DateTime reminderTime, string roomId, string reminderKey)>();
            foreach (var rule in ruleList.rules)
            {
                if (rule == null || !rule.enabled) continue;
                if (!TryParseHHmm(rule.time_hhmm, out int reminderHour, out int reminderMin)) continue;

                DateTime reminderTime = new DateTime(now.Year, now.Month, now.Day, reminderHour, reminderMin, 0);
                if (now < reminderTime) continue;  // 還沒到

                string roomId = string.IsNullOrEmpty(rule.target_room) ? "tavern" : rule.target_room;
                if (UCL_ChatTavernIO.GetRoom(roomId) == null) continue;

                string reminderKey = $"{today}::{rule.id}::reminder";
                if (!state.fired_today_keys.Contains(reminderKey))
                {
                    dueReminders.Add((rule, reminderTime, roomId, reminderKey));
                }
            }

            // ---- Pass 2: 依 catch-up 併發規則實際 fire reminder ----
            // Bug fix (basecamp 拍磚 2026-07-27 seq 13741)：原本用「dueReminders.Count > 1」判定 catch-up，
            // 會誤殺「本來就同一分鐘設了兩條 rule」的合法情境（例如 23:00 整點快照 + 23:00 睡眠提醒）—
            // 這種情況 count 也 > 1，但兩條 reminderTime 完全相同，不是補報。改判定基準為「時間跨度」：
            // 最舊與最新到期時間差 > CATCHUP_SPAN_THRESHOLD_MINUTES 才視為補報 (Editor 閒置回來一次跨過
            // 好幾個時點)；同分鐘或跨度很小的並發到期，一律照常各自都發，不合併也不靜默丟。
            const double CATCHUP_SPAN_THRESHOLD_MINUTES = 5.0;
            dueReminders.Sort((a, b) => a.reminderTime.CompareTo(b.reminderTime));
            double spanMinutes = dueReminders.Count >= 2
                ? (dueReminders[dueReminders.Count - 1].reminderTime - dueReminders[0].reminderTime).TotalMinutes
                : 0.0;

            if (dueReminders.Count > 1 && spanMinutes > CATCHUP_SPAN_THRESHOLD_MINUTES)
            {
                // Catch-up 補報情境（時間跨度夠大，判定為 Editor 閒置回來的一次補齊）：
                // 只發時間最新的一條，其餘補標已發 — 這裡「補標已發」是指補進 state.fired_today_keys
                // 並隨 stateDirty 正常存檔 (UCL_BartenderIO.SaveState)，不是完全不落地；跳過的只是
                // 「不再發 tavern 訊息 / 不再呼叫 FireTimeReminder → AppendMessage」這件事本身。
                // 這點很重要：狀態有落盤，所以就算 Editor 這次也提早關掉，下次重開不會對同一批 reminder 重判到期。
                var latest = dueReminders[dueReminders.Count - 1];
                var skipped = dueReminders.GetRange(0, dueReminders.Count - 1);

                FireTimeReminder(latest.rule, latest.roomId);
                state.fired_today_keys.Add(latest.reminderKey);
                stateDirty = true;

                foreach (var s in skipped)
                {
                    state.fired_today_keys.Add(s.reminderKey);
                }
                stateDirty = true;
                Debug.Log($"[Bartender] catch-up 補報偵測：{dueReminders.Count} 條 time rule 同時到期, " +
                          $"時間跨度 {spanMinutes:F1} 分鐘 > 門檻 {CATCHUP_SPAN_THRESHOLD_MINUTES} " +
                          $"(疑似 Editor 閒置一段時間才回來 tick) — 只發最新一條 '{latest.rule.id}' " +
                          $"({latest.rule.time_hhmm})，其餘 {skipped.Count} 條 " +
                          $"[{string.Join(", ", skipped.ConvertAll(s => $"{s.rule.id}({s.rule.time_hhmm})"))}] " +
                          $"補標已發並存檔，但不發 tavern 訊息。");
            }
            else if (dueReminders.Count >= 1)
            {
                // 正常路徑 — 單條到期，或多條到期但時間跨度在門檻內 (視為合法並發，各自都發, 不合併)。
                // 行為與修改前 (dueReminders.Count==1 分支) 完全一致；並發但非 catch-up 時新增全發, 不誤殺。
                foreach (var only in dueReminders)
                {
                    FireTimeReminder(only.rule, only.roomId);
                    state.fired_today_keys.Add(only.reminderKey);
                }
                stateDirty = true;
            }

            if (stateDirty) UCL_BartenderIO.SaveState(state);

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

        static void FireTimeReminder(UCL_BartenderTimeRule rule, string roomId)
        {
            // 動態組裝的只剩標頭 —— 內文一律照 reminder_lines 的求值結果播出。
            // 要 @ 誰請寫在 reminder_lines 裡（那是內文的一部分，不該是另一個欄位）。
            string body = $"⏰ **酒保時間提醒** ({rule.time_hhmm})\n\n{rule.GetReminderBody()}";
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
            UCL_ChatTavernIO.AppendMessage(roomId, msg);
        }

        // ===========================================================
        // 區塊：跨日存款保管費的**觸發**（Anti-inflation, Tim 2026-05-13 拍板）
        // ⭐ TASK-0278（Tim 2026-09-22 拍板）：**扣繳的計算與落帳已整段搬到 Senate 端**
        //   （`SCP_Demurrage` ／ `senate cmd demurrage`）。⛔ 這裡不再算錢、不再寫帳本。
        //   ⇒ 政策（門檻／費率／央行／豁免）、算式、冪等鍵、廣播本文**全部只有那一份**，
        //     判準與血證寫在 `SCP_Demurrage` 檔頭，⛔ 本檔不重抄（抄兩份必漂，而漂掉時兩邊都不報錯）。
        // 本層只剩三件事：
        //   ① **判跨日**（state.last_overnight_check_date != 今天 UTC）＋ 首次啟動的 grace
        //   ② **派一次** `cmd demurrage op=run`（Server 是唯一寫入端）
        //   ③ **把 Cmd 組好的廣播本文貼上酒館**（酒館寫入端目前是 Editor，Server 沒有資格寫）
        // 觸發：daemon tick 每次跑, 但 state.last_overnight_check_date == today → skip.
        //       首次啟動 (state 為空) → init today, **不收費** (避免新裝立刻課稅).
        // ⚠ 射程：TASK-0278 ⑧ 明寫**本單不含「觸發也搬走」** —— 排程／常駐由誰跑是下一張單。
        // ===========================================================

        static void CheckOvernightDeposits()
        {
            var state = UCL_BartenderIO.LoadState();
            // 區塊職責：日期一律走 **UTC**（Tim 2026-08-04 拍板統一時區）
            //          規則本身（含 2026-08-25 全系統拍板與選擇判準）→ repo:docs/Glossary/utc-everywhere-local-display.md
            //          —— 本註解是史料出處，別只讀這裡（TASK-0046：拍板只活在這裡時咬過人）
            // 物理意義：ledger 日期夾用 UTC，本流程原本用 local（台灣 +8）——
            //          於是 local 00:00~08:00 產生的 entry 會落在**前一天**的 UTC 夾。
            //          兩套曆並存時，結帳邊界會跟檔案位置對不上，
            //          症狀是「餘額偶爾差一點，而且只在半夜出現」。
            // 數值影響：結算時點由 local 00:00 變成 local 08:00（= UTC 00:00）。
            //          `useRef` 判重 key 內嵌的日期也跟著變 UTC。
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");

            // First-run grace: state 沒紀錄 → init 成 today, 不收費
            if (string.IsNullOrEmpty(state.last_overnight_check_date))
            {
                state.last_overnight_check_date = today;
                UCL_BartenderIO.SaveState(state);
                return;
            }

            // ── UTC 遷移 grace（一次性）─────────────────────────────────────
            // 物理意義：state 裡存的可能是**遷移前的 local 日期**。若直接拿它跟 UTC today 比，
            //          在 local 00:00~08:00 之間會判定「跨日了」而重跑一輪；
            //          而判重用的 useRef 內嵌日期也不同（local D+1 vs UTC D）→ **重複扣款**。
            // 數值影響：遷移後第一次執行只寫 state、**不收費**，並落一個不可逆的 marker。
            //          代價是可能少收一天保管費 —— 相對於重複扣款，這方向明顯該選。
            //          **壞要往安全的方向壞。**
            // 設計取捨：不採「新舊 key 都查一次」的雙查期（gura 2026-08-04 提案，技術上更精確）——
            //          雙查是過渡期專用邏輯，必須在某天被移除，而「該移除卻沒人記得移除」
            //          的臨時碼在本 repo 是有血債的。一次性少收一天不留債。
            string graceMarker = Path.Combine(UCL_BartenderIO.GetBartenderDir(), "utc_migration_grace.marker");
            if (!File.Exists(graceMarker))
            {
                try
                {
                    Directory.CreateDirectory(UCL_BartenderIO.GetBartenderDir());
                    File.WriteAllText(graceMarker,
                        $"UTC 遷移 grace 已執行\nat_utc={DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}Z\n"
                        + $"prev_state_date={state.last_overnight_check_date}\nnew_state_date={today}\n"
                        + "本次刻意不收保管費 —— 避免 local→UTC 換算期間重複扣款。\n",
                        new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    // marker 寫不進去就**不要**跳過收費 —— 否則每輪都當成「還在 grace」而永遠不收費，
                    // 那是比少收一天嚴重得多的靜默失效。
                    Debug.LogWarning($"[Bartender] UTC grace marker 寫入失敗，本輪照常收費：{ex.Message}");
                    goto skipGrace;
                }
                state.last_overnight_check_date = today;
                UCL_BartenderIO.SaveState(state);
                Debug.Log($"[Bartender] UTC 遷移 grace — state 由 '{state.last_overnight_check_date}' 對齊 UTC {today}，本輪不收費。");
                return;
            }
            skipGrace:

            // 同一天已 check 過 → skip (短路, 避免每 5s 重跑)
            if (state.last_overnight_check_date == today) return;

            // 以下是**跨日重路徑** —— 一天只會走一次，而它就是初開 Editor 卡住的那一段。
            // 每個子相位都留下耗時與基數（檔數 / 帳戶數），讓下次不必靠人工對帳去夾區間。
            s_TickWasCrossDay = true;
            MarkPhase("overnight.enter");

            // ── 每日結帳（掛在保管費之前）────────────────────────────────────
            // 物理意義：跨日 tick 是唯一能確定「前一天已經寫完了」的時點，所以結帳掛在這裡。
            //          先關帳再收費：保管費本身要讀全部帳戶餘額，結帳讓那件事變便宜。
            // 數值影響：只寫 closing/*.json，不動任何餘額；失敗不擋收費（結帳是加速不是前提）。
            int closingWritten = 0;
            try
            {
                // 🩸 2026-09-22（TASK-0274）換了受詞：舊的 `UCL_TreasuryClosing` 結的是**凍結的**
                //   `Treasury/ledger`（09-18 起不再長）⇒ 它每天被叫、每天什麼都沒結，
                //   而畫面上跟「今天沒有要結的」一模一樣（closing/ 最後一份停在 2026-09-18 為證）。
                //   ⇒ 改打新帳本的 `SCP_BankClosing`。
                var closingProblems = new List<string>();
                closingWritten = SCP.Core.Bank.SCP_BankClosing.GenerateMissing(
                    UCL_TreasuryAuthority.BankRoot, out string closingSummary, closingProblems);
                if (closingWritten > 0) Debug.Log($"[Bartender] 每日結帳：{closingSummary}");
                if (closingProblems.Count > 0)
                    Debug.LogWarning($"[Bartender] 每日結帳有 {closingProblems.Count} 格讀不動：\n  · "
                                     + string.Join("\n  · ", closingProblems));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Bartender] 每日結帳失敗（不擋保管費）：{ex.Message}");
            }
            MarkPhase("overnight.closing", $"written={closingWritten}");

            // ===========================================================
            // 區塊職責：把這一輪的扣繳**整段派給 Senate Server**（TASK-0278）。
            // 物理意義：計算與落帳住在 `SCP_Demurrage`／`cmd demurrage`，⛔ 不在這裡。
            //          本層剩下的只有三件事：**判跨日**（上面）、**派一次**、**把廣播貼上酒館**。
            // 🩸 為什麼扣款不留在這裡：`SCP_BankLedger` 那把 debit 鎖只在 **同一個 process 內**有效，
            //   而它成立的前提是「只有 Server 在寫」。Editor 自己算完自己扣＝安靜地多一個寫入端。
            // ⚠ 為什麼廣播還在這裡：酒館寫入端目前是 **Editor**（`tavern.writer=editor`）
            //   ⇒ Server 那側沒有資格寫酒館。⛔ 不為了「搬乾淨」而在那邊偷開第二個酒館寫入端
            //   —— 那是 TASK-0106 正在收斂的那條線。⇒ 本文由 Cmd 組好寫進檔，這裡讀出來貼。
            // 數值影響：失敗**不推進 state** —— 下一個 tick 會再試一次，而重跑是冪等的。
            // ===========================================================
            string bodyFile = Path.Combine(UCL_BartenderIO.GetBartenderDir(), $"demurrage_{today}.md");
            var demurrageArgs = new List<string>
            {
                "op=run", "confirm=1", $"date={today}", $"body_out={bodyFile}",
            };
            System.Collections.Generic.Dictionary<string, string> demurrageValues;
            try
            {
                demurrageValues = UCL_TreasuryAuthority.RunSenateCmd("demurrage", demurrageArgs);
            }
            catch (Exception ex)
            {
                // ⛔ 不推進 state：這一輪**沒有發生**，下一個 tick 要再來一次。
                //   ⚠ 而它跟「已經扣過了」不同形 —— 那一種會走完並回冪等命中。
                Debug.LogWarning($"[Bartender] 跨日保管費派給 Server 失敗，**本輪沒有扣款**：{ex.Message}");
                MarkPhase("overnight.delegate", "FAILED");
                return;
            }
            demurrageValues.TryGetValue("total_fee", out string totalFeeText);
            demurrageValues.TryGetValue("accounts_charged", out string chargedText);
            MarkPhase("overnight.delegate", $"charged={chargedText} total_fee={totalFeeText}");

            // 5. 推進 state.last_overnight_check_date（即使無人扣費也推進，避免重跑）
            state.last_overnight_check_date = today;
            UCL_BartenderIO.SaveState(state);

            // 6. Broadcast —— 本文由 Cmd 組好（同一份判準、同一段文字），這裡只負責貼。
            //    ⚠ 讀不到本文就**不要自己編一段** ——「這一輪發生了什麼」只有 Cmd 知道，
            //      而一段編出來的公告會蓋掉真正的讀數。出聲，然後不貼。
            string body;
            try { body = File.ReadAllText(bodyFile); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Bartender] 保管費廣播本文讀不到（{bodyFile}）：{ex.Message}"
                                 + " —— ⚠ 錢**已經扣了**（見上面的讀數），只是這一則公告沒有發出去。");
                MarkPhase("overnight.broadcast", "MISSING_BODY");
                return;
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
                    { "subtag", Value(demurrageValues, "subtag", "overnight-deposit-fee") },
                    { "check_date", today },
                    { "total_fee", Value(demurrageValues, "meta_total_fee", "0") },
                    { "central_bank", Value(demurrageValues, "meta_central_bank", "") },
                    { "central_bank_income", Value(demurrageValues, "meta_central_bank_income", "0") },
                    { "accounts_charged", Value(demurrageValues, "meta_accounts_charged", "0") },
                    { "accounts_safe", Value(demurrageValues, "meta_accounts_safe", "0") },
                },
            };
            UCL_ChatTavernIO.AppendMessage("tavern", msg);  // 預設 fire mirror = Discord broadcast
            MarkPhase("overnight.broadcast", $"body_len={body.Length}");
        }

        /// <summary>從 Server 印的 `🔢` 值表取一格；沒有就回預設（⛔ 不丟例外 —— 錢已經動了）。</summary>
        static string Value(System.Collections.Generic.Dictionary<string, string> iValues,
                            string iKey, string iFallback)
            => iValues != null && iValues.TryGetValue(iKey, out string aV) && aV.Length > 0 ? aV : iFallback;
    }
}
#endif