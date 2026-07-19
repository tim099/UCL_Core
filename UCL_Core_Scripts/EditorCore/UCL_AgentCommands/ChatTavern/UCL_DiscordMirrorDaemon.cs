// 區塊職責：Discord Mirror 常駐背景 daemon（Unity Editor 內）— tavern→Discord 同步的 C# native 版本骨架
// 物理意義：取代現行「每筆 AppendMessage fire-and-forget spawn python notify_discord.py」的模型。
//          2026-07-19 實錄：舊模型在 zombie lock 下累積 3578 隻 python → Unity 編譯 OOM。改常駐 daemon
//          （[InitializeOnLoad] + EditorApplication.update poll）從源頭消滅 per-message spawn 那一族病理。
// 設計取捨（對齊已 ship 的 UCL_BartenderDaemon）：
//   - 用 EditorApplication.update（非 delayCall）— 持續 tick，不靠單次 schedule
//   - poll model（非 FileSystemWatcher）— Editor 內 watcher 易掉事件 + domain reload 邊界問題
//   - tick 內 fail-safe try-catch — 任何 exception 不擋 Editor 主迴圈
//   - enable gate 預設 OFF — 開發期（T2~T6）daemon 不真的送，避免跟「仍活著的 python 路徑」雙送；
//     cutover（T7）才把 python spawn 關掉 + 本 daemon 打開
// 數值影響：CHECK_INTERVAL = 1s（比 Bartender 的 5s 密，因 mirror 即時性要求高，但 tick 內只做輕量掃描）
// 分階段：T2=本骨架（lifecycle + tick + enable gate）；T3=C# WebhookClient（POST + 429）；
//        T4=單寫者 cursor + seen-set 落 disk + per-webhook 去重 + 真正掃描送出。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// Discord Mirror native daemon（Phase B）— domain reload 時自動啟動、Editor 關閉時自動停止。
    /// hook EditorApplication.update → 定期 tick → （T4 起）掃 tavern 新訊息 broadcast 到 Discord webhook。
    /// 本檔為 T2 骨架：只建 lifecycle + tick 迴圈 + enable gate，尚未真正送任何訊息。
    /// </summary>
    [InitializeOnLoad]
    public static class UCL_DiscordMirrorDaemon
    {
        // 區塊職責：tick 間隔 + enable 開關持久化 key
        // 物理意義：CHECK_INTERVAL 1s 是 mirror 即時性與 IO load 的折衷（tick 內僅輕量增量掃描）
        // 數值影響：EnabledPrefKey 走 EditorPrefs（per-machine 持久，跨 domain reload 不失）— cutover 開關
        const double CHECK_INTERVAL_SECONDS = 1.0;
        const string EnabledPrefKey = "UCL_DiscordMirrorDaemon.Enabled";

        // 區塊職責：tick 節流游標 + 心跳一次性旗標
        // 物理意義：s_LastCheckTime 記上次 TickInternal 的 timeSinceStartup；未滿間隔的 update 直接跳過
        // 數值影響：s_HeartbeatLogged 讓「daemon 已在跑」只 log 一次，避免洗 console（domain reload 後重置）
        static double s_LastCheckTime = 0;
        static bool s_HeartbeatLogged = false;

        // ===========================================================
        // Static ctor — Editor 啟動 / domain reload 時自動執行
        // 物理意義：[InitializeOnLoad] 保證 assembly load 時跑一次 ctor，掛上 update tick
        // 數值影響：ctor 內不動 IO（避免 first-load 卡 Editor 啟動）；state rehydrate 留 T4 第一次 tick lazy 做
        // ===========================================================
        static UCL_DiscordMirrorDaemon()
        {
            try
            {
                EditorApplication.update += Tick;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DiscordMirror] daemon init fail: {e.Message}");
            }
        }

        /// <summary>
        /// daemon 是否啟用（cutover 開關）。預設 OFF — 開發期避免跟現行 python mirror 路徑雙送。
        /// 走 EditorPrefs 持久化，跨 domain reload / Editor 重啟不失。
        /// </summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPrefKey, false);
            set
            {
                EditorPrefs.SetBool(EnabledPrefKey, value);
                s_HeartbeatLogged = false;   // 重置心跳，讓 toggle 後能再看到一次「alive」log
                Debug.Log($"[DiscordMirror] Enabled = {value}");
            }
        }

        /// <summary>選單手動 toggle enable — 給 T2~T6 開發期驗證 daemon tick 用（cutover 前 python 仍為 live owner）。</summary>
        [MenuItem("UCL/Discord Mirror/Toggle Native Daemon (dev)")]
        static void ToggleEnabledMenu()
        {
            Enabled = !Enabled;
        }

        // ===========================================================
        // 區塊：mirror_owner 原子切換開關（疑慮5 · basecamp 拍板 · T4）
        // 物理意義：cutover 用單一 config flag mirror_owner: "python" | "native"。python spawn 閘讀它 →
        //          native 就不 spawn；daemon live 送出路徑也讀它 → 只有 native 才真送。原子切換零雙送窗，
        //          cutover = 翻一行、可 revert（對比「同時開兩條各自判斷」會有 race 雙送窗）。
        // 數值影響：帶 5s 快取 — 避免 AppendMessage hot path 每筆 post 都讀檔；預設 "python"（安全側：
        //          讀不到 config / 解析失敗都 fallback python，維持既有行為，不會誤把 ownership 交給未就緒的 native）。
        // ===========================================================
        const double OWNER_CACHE_TTL_SEC = 5.0;
        static string s_CachedOwner = null;
        static double s_OwnerCacheTime = -999;

        /// <summary>當前 mirror ownership（"python" | "native"）— 讀 notify_config.json 的 mirror_owner，帶 5s 快取。</summary>
        public static string MirrorOwner
        {
            get
            {
                double now = EditorApplication.timeSinceStartup;
                if (s_CachedOwner != null && now - s_OwnerCacheTime < OWNER_CACHE_TTL_SEC)
                {
                    return s_CachedOwner;
                }
                string owner = "python";   // 安全側預設
                try
                {
                    string path = System.IO.Path.Combine(UCL_RepoPath.AgentCommandsDir, "PromptQueue", "notify_config.json");
                    if (System.IO.File.Exists(path))
                    {
                        var jd = UCL.Core.JsonLib.JsonData.ParseJson(System.IO.File.ReadAllText(path));
                        if (jd != null && jd.IsObject)
                        {
                            string v = jd.GetString("mirror_owner", "python");
                            if (v == "native" || v == "python") owner = v;
                        }
                    }
                }
                catch { owner = "python"; }   // 任何失敗 → 安全側 python
                s_CachedOwner = owner;
                s_OwnerCacheTime = now;
                return owner;
            }
        }

        /// <summary>native daemon 是否為當前 live owner（mirror_owner == "native"）。python spawn 閘 + daemon 送出路徑共用。</summary>
        public static bool IsNativeOwner => MirrorOwner == "native";

        // ===========================================================
        // 區塊：update tick — 節流 + fail-safe 包裹
        // 物理意義：EditorApplication.update 一秒約 60+ 次；節流成每 CHECK_INTERVAL 秒最多進一次 TickInternal
        // 數值影響：!Enabled 直接 return（0 成本）；exception 只 warn 不 throw，絕不擋 Editor 主迴圈
        // ===========================================================
        static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - s_LastCheckTime < CHECK_INTERVAL_SECONDS) return;
            s_LastCheckTime = now;

            try
            {
                TickInternal();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DiscordMirror] tick fail（不擋主迴圈）: {e.Message}");
            }
        }

        /// <summary>強制立刻 tick（給 Cmd / 測試手動觸發；AdminPage 手動觸發 native owner 下改呼叫此，T6.5）。</summary>
        public static void ForceTick()
        {
            try { TickInternal(); }
            catch (Exception e) { Debug.LogWarning($"[DiscordMirror] ForceTick fail: {e.Message}"); }
        }

        // ===========================================================
        // 區塊：poll 送出 in-flight 基礎設施（basecamp 拍板 · tick 輪詢 isDone，不賭 await resume）
        // 物理意義：StartPost 發出的 UnityWebRequest 掛進 s_InFlight；每 tick DrainInFlight 檢查 isDone、
        //          完成在**主緒**判讀結果 → 2xx 才 RecordSent + Save（天然單寫者，cursor 只在主緒推進）。
        // 數值影響：MAX_INFLIGHT 限並發送出防 burst；recordOnSuccess=false 的是 smoke（只 log 不動 state）。
        // ===========================================================
        const int MAX_INFLIGHT = 6;

        class MirrorInFlight
        {
            public UnityWebRequest req;
            public string room;
            public string webhookId;
            public string uuid;
            public string ts;
            public bool recordOnSuccess;   // true=正式送(成功推 cursor)；false=smoke(只 log)
        }

        static readonly List<MirrorInFlight> s_InFlight = new List<MirrorInFlight>();

        // 區塊職責：每 tick 收割已完成的送出請求
        // 物理意義：2xx→RecordSent+ClearBackoff；429→SetBackoff；其他 fail→不推 cursor（下輪重送，可見非隱形）
        // 數值影響：任一 cursor 變動 → Save() 立即落 disk（seen-set 活過 reload — kiara 要求）
        static void DrainInFlight()
        {
            if (s_InFlight.Count == 0) return;
            bool anyStateChange = false;
            for (int i = s_InFlight.Count - 1; i >= 0; i--)
            {
                var f = s_InFlight[i];
                if (f.req == null) { s_InFlight.RemoveAt(i); continue; }
                if (!f.req.isDone) continue;

                var res = UCL_DiscordWebhookClient.InterpretResult(f.req);
                if (res.ok)
                {
                    if (f.recordOnSuccess)
                    {
                        UCL_DiscordMirrorState.RecordSent(f.room, f.webhookId, f.uuid, f.ts);
                        UCL_DiscordMirrorState.ClearBackoff(f.room, f.webhookId);
                        anyStateChange = true;
                    }
                    Debug.Log($"[DiscordMirror] ✓ sent {f.room}/{f.uuid} → webhook {f.webhookId} (HTTP {res.statusCode})");
                }
                else if (res.isRateLimited)
                {
                    if (f.recordOnSuccess) { UCL_DiscordMirrorState.SetBackoff(f.room, f.webhookId, res.retryAfterSeconds); anyStateChange = true; }
                    Debug.LogWarning($"[DiscordMirror] 429 {f.webhookId} → backoff {res.retryAfterSeconds:F1}s（cursor 不推進）");
                }
                else
                {
                    Debug.LogWarning($"[DiscordMirror] send fail {f.room}/{f.uuid} → {f.webhookId}: {res.error}（cursor 不推進，下輪重送）");
                }

                f.req.Dispose();
                s_InFlight.RemoveAt(i);
            }
            if (anyStateChange) UCL_DiscordMirrorState.Save();
        }

        /// <summary>
        /// Smoke test：發一則到指定 webhook，走跟正式一樣的 poll 路徑（StartPost→tick DrainInFlight 判讀），
        /// 但 recordOnSuccess=false（只 log 不動 state / 不寫 canonical 檔）。驗 edit-mode 下 poll resume + 真送達。
        /// 由 Cmd_MirrorSmoke 呼叫；webhook URL 由 Cmd 從 git-ignored 檔讀，不經 arg（避免 secret 進 log）。
        /// </summary>
        public static void SmokeTest(string url, string content)
        {
            var req = UCL_DiscordWebhookClient.StartPost(url, content, "mirror-smoke", null, null);
            if (req == null) { Debug.LogWarning("[DiscordMirror] smoke: malformed webhook url"); return; }
            s_InFlight.Add(new MirrorInFlight
            {
                req = req,
                room = "_smoke",
                webhookId = UCL_DiscordWebhookClient.ExtractWebhookId(url),
                uuid = "smoke",
                ts = "",
                recordOnSuccess = false,
            });
            Debug.Log("[DiscordMirror] smoke: request 發出，等 tick 輪詢 isDone（驗 edit-mode poll resume）");
        }

        // ===========================================================
        // 區塊：tick 主體 — 先 drain in-flight（永遠），再依 owner 互鎖決定是否 scan 送出
        // 物理意義：DrainInFlight 永遠跑（收割 smoke / 正式送出）；Scan 只在 native owner 時跑（mirror_owner 硬互鎖）
        // 數值影響：owner=python 時完全不 scan（不送真房）→ 跟 python 路徑零雙送
        // ===========================================================
        static void TickInternal()
        {
            DrainInFlight();

            // mirror_owner 硬互鎖：只有 native owner 才掃描送出真房訊息（basecamp 拍板）
            if (!IsNativeOwner) return;

            if (!s_HeartbeatLogged)
            {
                Debug.Log("[DiscordMirror] native daemon 為 live owner，開始掃描送出");
                s_HeartbeatLogged = true;
            }
            Scan();
        }

        // 區塊職責：mirror 目標 config（tavern_mirror 區塊子集）— Tim 拍板 class + JsonLib 讀取
        // 物理意義：只取 daemon 掃描要用的 rooms / webhook_urls / kinds；其餘 tavern_mirror 欄位 LoadDataFromJson 自動略過
        class MirrorConfigBlock
        {
            public List<string> rooms = new List<string>();
            public List<string> webhook_urls = new List<string>();
            public List<string> kinds = new List<string>();
        }

        const int SCAN_TAIL_N = 30;                 // 每 room 每 tick 掃最近 N 筆（ShouldSend 的 ts 窗會濾掉已送）
        const double CONFIG_CACHE_TTL_SEC = 5.0;
        static MirrorConfigBlock s_CachedConfig = null;
        static double s_ConfigCacheTime = -999;

        // 區塊職責：讀 notify_config.json 的 tavern_mirror 區塊（class + JsonLib，5s 快取）
        static MirrorConfigBlock ReadConfig()
        {
            double now = EditorApplication.timeSinceStartup;
            if (s_CachedConfig != null && now - s_ConfigCacheTime < CONFIG_CACHE_TTL_SEC) return s_CachedConfig;

            MirrorConfigBlock cfg = null;
            try
            {
                string path = System.IO.Path.Combine(UCL_RepoPath.AgentCommandsDir, "PromptQueue", "notify_config.json");
                if (System.IO.File.Exists(path))
                {
                    var jd = UCL.Core.JsonLib.JsonData.ParseJson(System.IO.File.ReadAllText(path));
                    if (jd != null && jd.IsObject && jd.Contains("tavern_mirror"))
                    {
                        var tm = jd["tavern_mirror"];
                        if (tm != null && tm.IsObject)
                            cfg = UCL.Core.JsonLib.JsonConvert.LoadDataFromJson<MirrorConfigBlock>(tm, UCL.Core.JsonLib.JsonConvert.SaveMode.Normal);
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning($"[DiscordMirror] config read fail: {e.Message}"); }

            if (cfg == null) cfg = new MirrorConfigBlock();
            if (cfg.rooms == null) cfg.rooms = new List<string>();
            if (cfg.webhook_urls == null) cfg.webhook_urls = new List<string>();
            if (cfg.kinds == null || cfg.kinds.Count == 0) cfg.kinds = new List<string> { "chat" };
            s_CachedConfig = cfg;
            s_ConfigCacheTime = now;
            return cfg;
        }

        // 已在 in-flight 就別重送同一 (room, webhook, uuid)
        static bool IsInFlight(string room, string webhookId, string uuid)
        {
            for (int i = 0; i < s_InFlight.Count; i++)
            {
                var f = s_InFlight[i];
                if (f.room == room && f.webhookId == webhookId && f.uuid == uuid) return true;
            }
            return false;
        }

        // echo 排除：discord_inbound relay 寫回 tavern 的訊息不再 mirror 回 Discord（防迴圈）— 對齊 python config
        static bool IsEcho(UCL_ChatMessage msg)
        {
            if (msg.meta != null && msg.meta.TryGetValue("source", out var src) && src == "discord") return true;
            if (!string.IsNullOrEmpty(msg.sender_id) && msg.sender_id.StartsWith("discord:")) return true;
            return false;
        }

        // 區塊職責：掃 config rooms × webhooks，對每則「該送且未在途」的訊息 StartPost（掛 in-flight，成功推 cursor）
        // 物理意義：Tail 取近訊 → kind 過濾 + echo 排除 → per-webhook ShouldSend（有界窗去重）→ StartPost
        // 數值影響：並發受 MAX_INFLIGHT 限（滿了本輪 return，下 tick 續）；content/username 走 MVP（body + DisplayName），
        //          rich identity/avatar 留 T5。cursor 只在 DrainInFlight 拿到 2xx 後推進（單寫者）。
        static void Scan()
        {
            var cfg = ReadConfig();
            if (cfg.rooms.Count == 0 || cfg.webhook_urls.Count == 0) return;

            foreach (var room in cfg.rooms)
            {
                if (string.IsNullOrEmpty(room)) continue;
                List<UCL_ChatMessage> msgs;
                try { msgs = UCL_ChatTavernIO.Tail(room, SCAN_TAIL_N); }
                catch { continue; }
                if (msgs == null) continue;

                foreach (var msg in msgs)
                {
                    if (msg == null || string.IsNullOrEmpty(msg.uuid)) continue;
                    string kind = string.IsNullOrEmpty(msg.kind) ? "chat" : msg.kind;
                    if (!cfg.kinds.Contains(kind)) continue;
                    if (IsEcho(msg)) continue;

                    foreach (var url in cfg.webhook_urls)
                    {
                        string wid = UCL_DiscordWebhookClient.ExtractWebhookId(url);
                        if (wid == null) continue;
                        if (!UCL_DiscordMirrorState.ShouldSend(room, wid, msg.uuid, msg.ts)) continue;
                        if (IsInFlight(room, wid, msg.uuid)) continue;
                        if (s_InFlight.Count >= MAX_INFLIGHT) return;   // 並發上限 → 下輪 tick 續送

                        // T5：解析 persona 頭像 + 清洗 username + @-mention rewrite（對齊 python 顯示）
                        string username = UCL_DiscordIdentityResolver.ResolveUsername(msg.sender_id, msg.sender_name, msg.DisplayName);
                        string avatarUrl = UCL_DiscordIdentityResolver.ResolveAvatarUrl(msg.sender_persona, msg.sender_avatar_sprite, msg.sender_id);
                        string content = UCL_DiscordIdentityResolver.RewriteMentions(msg.body ?? "");
                        var req = UCL_DiscordWebhookClient.StartPost(url, content, username, avatarUrl, null);
                        if (req != null)
                            s_InFlight.Add(new MirrorInFlight
                            {
                                req = req, room = room, webhookId = wid, uuid = msg.uuid, ts = msg.ts, recordOnSuccess = true,
                            });
                    }
                }
            }
        }
    }
}
#endif
