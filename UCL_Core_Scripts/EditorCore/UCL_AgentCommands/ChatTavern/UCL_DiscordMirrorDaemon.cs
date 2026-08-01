// 區塊職責：Discord Mirror 常駐背景 daemon（Unity Editor 內）— tavern→Discord 同步的 C# native 版本骨架
// 物理意義：取代舊「每筆 AppendMessage fire-and-forget spawn python notify_discord.py」的模型
//          （2026-07-28 python 路徑已整條移除，本 daemon 為 Discord 唯一傳送者）。
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
        /// daemon 是否啟用。預設 OFF（Tim 2026-07-28 拍板維持顯式 opt-in）— 走 EditorPrefs 持久化，
        /// 跨 domain reload / Editor 重啟不失，每台機器各自決定要不要鏡像。
        /// ⚠ 2026-07-28 python mirror 路徑已整條移除 → 本開關關著 = Discord 完全靜音（無備援路徑）。
        /// 開關位置：選單 UCL/Discord Mirror/Toggle Mirror Daemon，或酒館 Admin 頁。
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

        /// <summary>選單手動 toggle enable — 暫停 / 恢復 Discord 鏡像（native 為唯一傳送者）。</summary>
        [MenuItem("UCL/Discord Mirror/Toggle Mirror Daemon")]
        static void ToggleEnabledMenu()
        {
            Enabled = !Enabled;
        }


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

            // 區塊職責：follow-up#1 長文 chunk 化 — 同一則訊息的多 chunk 順序送出（python parity：寧可多條不截斷）
            // 物理意義：Discord 訊息順序 = POST 完成順序 → chunk n+1 MUST 等 chunk n 2xx 才發（同 entry 續用）。
            //          cursor 只在「全部 chunk 都 2xx」後推進 — 中途 fail 不推 → 下輪整組重送
            //          （取捨與 python 相反：python any_chunk_ok 推 cursor = 後續 chunk 丟了不補；
            //          native 寧可重複前段 chunk 也不隱形漏尾段，對齊「fail 方向 = 可見非隱形」原則）。
            public string url;                       // webhook 原始 URL（發續 chunk 用）
            public string username;                  // identity 沿用（多 chunk 同身分）
            public string avatarUrl;
            public List<string> pendingContents;     // 尚未發出的續 chunk（null/空 = 單條或已到最後一條）
            public int partIndex = 1;                // 當前第幾條（log 用，1-based）
            public int partTotal = 1;
        }

        static readonly List<MirrorInFlight> s_InFlight = new List<MirrorInFlight>();

        // 區塊職責：每 tick 收割已完成的送出請求
        // 物理意義：2xx→RecordSent+ClearBackoff（連帶歸零 fail_streak / 熔斷）；
        //          429→SetBackoff（retry-after）；
        //          其他 fail→RecordFailure 分類：暫時性走指數退避 30s→1h、
        //          **永久性（4xx 非 429）直接熔斷停送**（2026-08-01 前是一律「下輪重送」，
        //          對 404 等於無限重試 + 每輪一則帶 stack trace 的警告）。
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
                    // follow-up#1：還有續 chunk → 不推 cursor，原 entry 換上下一條 request 續等
                    //（順序保證：前一 chunk 2xx 才發下一條；Discord 顯示序 = 完成序）
                    if (f.pendingContents != null && f.pendingContents.Count > 0)
                    {
                        string nextContent = f.pendingContents[0];
                        f.pendingContents.RemoveAt(0);
                        var nextReq = UCL_DiscordWebhookClient.StartPost(f.url, nextContent, f.username, f.avatarUrl, null);
                        f.req.Dispose();
                        if (nextReq != null)
                        {
                            f.req = nextReq;
                            f.partIndex++;
                            Debug.Log($"[DiscordMirror] … chunk {f.partIndex}/{f.partTotal} {f.room}/{f.uuid} → webhook {f.webhookId}（前段 msg={res.messageId ?? "?"}）");
                            continue;   // entry 續留 in-flight，等下一 chunk 完成
                        }
                        // StartPost 失敗（極端）→ 當 fail 處理：不推 cursor，下輪整組重送
                        Debug.LogWarning($"[DiscordMirror] chunk {f.partIndex + 1}/{f.partTotal} 發出失敗 {f.room}/{f.uuid}（cursor 不推進，下輪重送整組）");
                        s_InFlight.RemoveAt(i);
                        continue;
                    }

                    if (f.recordOnSuccess)
                    {
                        UCL_DiscordMirrorState.RecordSent(f.room, f.webhookId, f.uuid, f.ts);
                        UCL_DiscordMirrorState.ClearBackoff(f.room, f.webhookId);
                        anyStateChange = true;
                    }
                    // T8：msg=<id> 為 ?wait=true 的「真建立」憑據 — 驗收 harness 以此行 + state seen_uuids 做能證偽 diff
                    string parts = f.partTotal > 1 ? $", parts={f.partTotal}" : "";
                    Debug.Log($"[DiscordMirror] ✓ sent {f.room}/{f.uuid} → webhook {f.webhookId} (HTTP {res.statusCode}, msg={res.messageId ?? "?"}{parts})");
                }
                else if (res.isRateLimited)
                {
                    if (f.recordOnSuccess) { UCL_DiscordMirrorState.SetBackoff(f.room, f.webhookId, res.retryAfterSeconds); anyStateChange = true; }
                    Debug.LogWarning($"[DiscordMirror] 429 {f.webhookId} → backoff {res.retryAfterSeconds:F1}s（cursor 不推進）");
                }
                else
                {
                    // 失敗分類 + 退避 / 熔斷（Tim 2026-08-01）。原本這裡一律「下輪重送」，
                    // 而 404 永遠不會變 200 → 無限重試 + 每輪一則帶 stack trace 的警告。
                    // 詳見 UCL_DiscordMirrorState.RecordFailure 區塊註解。
                    var (streak, backoffSec, shouldLog) =
                        UCL_DiscordMirrorState.RecordFailure(f.room, f.webhookId, res.statusCode, res.error);
                    anyStateChange = true;
                    if (shouldLog)
                    {
                        if (UCL_DiscordMirrorState.IsPermanentFailure(res.statusCode))
                            Debug.LogWarning(
                                $"[DiscordMirror] ⛔ **熔斷** {f.room} → webhook {f.webhookId}：HTTP {res.statusCode} 是永久性錯誤"
                                + $"（webhook/頻道已不存在或無權限），**停止自動重送**。{f.uuid} 未送達。\n"
                                + $"   修好後走 AdminPage 重設游標或清除該 webhook 的熔斷狀態才會恢復 —— "
                                + $"刻意不靠時間自動恢復：沒人介入過就恢復，只會回到無限重試。");
                        else
                            Debug.LogWarning(
                                $"[DiscordMirror] send fail {f.room}/{f.uuid} → {f.webhookId}: {res.error}"
                                + $"（第 {streak} 次連續失敗 → 退避 {backoffSec:F0}s 後重試）");
                    }
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

            // Phase C：treasury（bank）通知 native 模組 — 在途 drain 永遠跑；掃描僅 native owner 時啟用
            //（2026-07-28 python notify_treasury_entries 已移除 → native 為 treasury 唯一傳送者）
            UCL_DiscordTreasuryMirror.Tick(Enabled);

            // mirror_owner 硬互鎖：只有 native owner 才掃描送出真房訊息（basecamp 拍板）

            if (!s_HeartbeatLogged)
            {
                Debug.Log("[DiscordMirror] native daemon 為 live owner，開始掃描送出");
                s_HeartbeatLogged = true;
            }
            Scan();
        }

        // 區塊職責：mirror 目標 config（tavern_mirror 區塊子集）— Tim 拍板 class + JsonLib 讀取
        // 物理意義：只取 daemon 掃描要用的欄位；其餘 tavern_mirror 欄位 LoadDataFromJson 自動略過。
        //          T6 擴充：category_routing / quest_routing / exclude_senders / title_template / body_max
        //          （python parity — 對齊 notify_discord.py notify_tavern_messages 的路由與顯示語意）
        class MirrorCategoryRoutingBlock
        {
            public bool enabled = false;                          // 總開關（config category_routing.enabled）
        }
        class MirrorQuestRoutingBlock
        {
            public bool enabled = false;                          // quest 分流開關
            public string sender_match_prefix = "_quest_system";  // sender 前綴命中 → 走 quest webhooks（python Layer 1）
            public List<string> webhook_urls = new List<string>();// quest 頻道 webhook（config 直列；env/file 解析走 ResolveScopeUrls）
            public string webhook_env_var = "";
            public string webhook_file = "";
        }
        class MirrorConfigBlock
        {
            public List<string> rooms = new List<string>();
            public List<string> webhook_urls = new List<string>();
            public List<string> kinds = new List<string>();
            public List<string> exclude_senders = new List<string>();          // 這些 sender 不 mirror（python parity）
            public string title_template = "**`{room}` · {sender_name}** · seq {seq}";  // Discord 顯示 header（python 同款）
            public int body_max = 1500;                                        // body 截斷上限（Discord 硬限 2000；python 走 chunk，native MVP 截斷）
            public MirrorCategoryRoutingBlock category_routing = new MirrorCategoryRoutingBlock();
            public MirrorQuestRoutingBlock quest_routing = new MirrorQuestRoutingBlock();
        }

        // ===========================================================
        // 區塊職責：T6 category routing — routing group 解析快取（5s，與 config 快取同節奏）
        // 物理意義：UCL_TavernCategoryRoutingAsset 的 GetAllIDs(true)+GetData(false) 每次都掃 disk；
        //          per-message per-tick 呼叫會變 IO 熱點 → daemon 端把「group + 解析後 URLs」快取成
        //          輕量 target list，每 5s 重建一次（新增/改 group asset 最慢 5s 生效）。
        // 數值影響：matching 在記憶體做（HashSet category 查找 O(1)）；URL 解析含 ENV > FILE > config
        //          優先序 + placeholder 過濾（REPLACE_ME/PLACEHOLDER 不嘗試送，對齊 python T46）。
        // ===========================================================
        class MirrorRoutingTarget
        {
            public string id;                                     // group ID（Discord header 的 routing tag 用）
            public bool exclusive;                                // m_Exclusive：命中即壟斷（main + 其他 additive 全跳過）
            public bool isDefault;                                // m_IsDefault：沒命中任何 category 時的 fallback
            public HashSet<string> categories;                    // lowercase category 集
            public List<string> urls;                             // 解析+過濾後的真 webhook URLs
        }
        static List<MirrorRoutingTarget> s_CachedRouting = null;
        static double s_RoutingCacheTime = -999;

        // placeholder URL 判定（Templates~ 樣板預設值）— 對齊 python _is_placeholder_url
        static bool IsPlaceholderUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return true;
            string upper = url.ToUpperInvariant();
            return upper.Contains("REPLACE_ME") || upper.Contains("PLACEHOLDER");
        }

        // 區塊職責：scope 級 webhook URL 解析 — ENV > FILE > config list（對齊 python _resolve_webhook_urls）
        // 物理意義：file 路徑相對 AgentCommands/PromptQueue（python webhook_dir=STATE_DIR 同基準），一行一 URL。
        // 數值影響：全部經 placeholder 過濾；解析失敗 fail swallow 回空（caller 視同無 URL）。
        static List<string> ResolveScopeUrls(string envVar, string file, List<string> configUrls)
        {
            var outUrls = new List<string>();
            try
            {
                if (!string.IsNullOrEmpty(envVar))
                {
                    string ev = Environment.GetEnvironmentVariable(envVar);
                    if (!string.IsNullOrEmpty(ev))
                    {
                        foreach (var u in ev.Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
                            if (!IsPlaceholderUrl(u.Trim())) outUrls.Add(u.Trim());
                        if (outUrls.Count > 0) return outUrls;
                    }
                }
                if (!string.IsNullOrEmpty(file))
                {
                    string path = System.IO.Path.Combine(UCL_RepoPath.AgentCommandsDir, "PromptQueue", file);
                    if (System.IO.File.Exists(path))
                    {
                        foreach (var line in System.IO.File.ReadAllLines(path))
                        {
                            string u = line.Trim();
                            if (!string.IsNullOrEmpty(u) && !IsPlaceholderUrl(u)) outUrls.Add(u);
                        }
                        if (outUrls.Count > 0) return outUrls;
                    }
                }
                if (configUrls != null)
                {
                    foreach (var u in configUrls)
                        if (!string.IsNullOrEmpty(u) && !IsPlaceholderUrl(u)) outUrls.Add(u.Trim());
                }
            }
            catch (Exception e) { Debug.LogWarning($"[DiscordMirror] webhook URL resolve fail: {e.Message}"); }
            return outUrls;
        }

        // 區塊職責：載入（快取）全部 enabled routing groups → MirrorRoutingTarget list
        static List<MirrorRoutingTarget> ReadRoutingTargets()
        {
            double now = EditorApplication.timeSinceStartup;
            if (s_CachedRouting != null && now - s_RoutingCacheTime < CONFIG_CACHE_TTL_SEC) return s_CachedRouting;

            var targets = new List<MirrorRoutingTarget>();
            try
            {
                var allIDs = new UCL_TavernCategoryRoutingAsset().GetAllIDs(true);
                if (allIDs != null)
                {
                    foreach (var id in allIDs)
                    {
                        if (string.IsNullOrEmpty(id)) continue;
                        UCL_TavernCategoryRoutingAsset g = null;
                        try { g = new UCL_TavernCategoryRoutingAsset().GetData(id, false); } catch { continue; }
                        if (g == null || !g.m_Enabled) continue;

                        var cats = new HashSet<string>();
                        if (g.m_Categories != null)
                            foreach (var c in g.m_Categories)
                                if (!string.IsNullOrEmpty(c)) cats.Add(c.Trim().ToLowerInvariant());

                        targets.Add(new MirrorRoutingTarget
                        {
                            id = g.ID,
                            exclusive = g.m_Exclusive,
                            isDefault = g.m_IsDefault,
                            categories = cats,
                            urls = ResolveScopeUrls(g.m_WebhookEnvVar, g.m_WebhookFile, g.m_WebhookUrls),
                        });
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning($"[DiscordMirror] routing groups load fail: {e.Message}"); }

            s_CachedRouting = targets;
            s_RoutingCacheTime = now;
            return targets;
        }

        const int SCAN_TAIL_N = 30;                 // 每 room 每 tick 的起始掃描窗（cursor-driven 回頁的第一頁大小）
        const int SCAN_PAGE_MAX = 4096;             // 回頁深度硬上限（防 cursor 極舊時無界掃描；超限印警告不沉默截斷）
        const double CONFIG_CACHE_TTL_SEC = 5.0;

        // ==== 缺口熔斷（Tim 2026-08-01 拍板：最小改動止血，根因排重構）====
        const int BURST_GUARD_MAX_BACKLOG = 30;             // 單房積壓超過此數 → 停送（門檻對齊 SCAN_TAIL_N：一頁掃得完的量才算正常）
        const double BURST_GUARD_WARN_INTERVAL_SEC = 60.0;  // 熔斷警告節流（daemon 每 CHECK_INTERVAL 秒一 tick，不節流會洗版）
        static readonly HashSet<string> s_AdminBurstAllowed = new HashSet<string>();   // 刻意重放豁免（AdminPage 套用 seq 時加入）
        static readonly Dictionary<string, double> s_BurstGuardLastWarn = new Dictionary<string, double>();

        // ==== tick 早退（Tim 2026-08-01 拍板）====
        // 物理意義：Scan 每秒一次，但「該房訊息數」與「該房 min ts_high」都沒動時，這一輪的掃描結果
        //          必然與上一輪逐位元相同 —— 純白工。積壓卡住時（熔斷 / dead webhook 釘住下界）這個白工
        //          會**永遠**每秒重做一次深回頁，正是 2026-08-01 Editor 卡頓的放大器。
        // 數值影響：指紋未變且未超過 FORCE_RESCAN_SEC → 整房跳過（連 CollectScanMessages 都不進）。
        // 邊界：backoff 到期會讓 ShouldSend 由 false 轉 true，**但不改變指紋** → 靠 FORCE_RESCAN_SEC
        //      兜底，最遲 30 秒一定會再掃一次。所以早退最多延遲送出 30s，不會漏送。
        const double FORCE_RESCAN_SEC = 30.0;
        static readonly Dictionary<string, string> s_RoomScanSig = new Dictionary<string, string>();
        static readonly Dictionary<string, double> s_RoomScanLastFull = new Dictionary<string, double>();

        /// <summary>該房這輪值不值得掃：訊息數或 min ts_high 有變、或距上次掃描超過 FORCE_RESCAN_SEC 才回 true。</summary>
        static bool RoomNeedsScan(string room, List<string> webhookIds)
        {
            double now = EditorApplication.timeSinceStartup;
            string sig;
            try
            {
                // CountMessages 已走目錄指紋快取（71 個 dir stat ≈ 0.86ms，非全房列舉 32ms）
                sig = UCL_ChatTavernIO.CountMessages(room) + "|" + UCL_DiscordMirrorState.GetMinTsHigh(room, webhookIds);
            }
            catch { return true; }   // 指紋算不出 → 照掃（安全側：寧可白工，不可漏送）

            bool changed = !s_RoomScanSig.TryGetValue(room, out var prev) || prev != sig;
            bool stale = !s_RoomScanLastFull.TryGetValue(room, out double last) || now - last >= FORCE_RESCAN_SEC;
            if (!changed && !stale) return false;

            s_RoomScanSig[room] = sig;
            s_RoomScanLastFull[room] = now;
            return true;
        }
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
            if (cfg.exclude_senders == null) cfg.exclude_senders = new List<string>();
            if (cfg.category_routing == null) cfg.category_routing = new MirrorCategoryRoutingBlock();
            if (cfg.quest_routing == null) cfg.quest_routing = new MirrorQuestRoutingBlock();
            if (string.IsNullOrEmpty(cfg.title_template)) cfg.title_template = "**`{room}` · {sender_name}** · seq {seq}";
            if (cfg.body_max <= 0) cfg.body_max = 1500;
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
        // 2026-07-29：判定改走 UCL_ChatTavernIO.IsExternalRelay（單一事實源 meta.source 的共用讀取點），
        //            本地不再各自比對字串 — 未來新增中繼來源（LINE / webhook…）時防迴圈自動跟上。
        static bool IsEcho(UCL_ChatMessage msg) => UCL_ChatTavernIO.IsExternalRelay(msg);

        // 區塊職責：Bug B fix — cursor-driven 掃描窗收集（取代固定 Tail(SCAN_TAIL_N)）
        // 物理意義：固定尾窗在「積壓 > N」（burst / 429 backoff 期間訊息照進 / daemon 離線恢復）時，
        //          第 N+1 筆以前的未送訊息永遠掉出掃描範圍 = silent drop（07-17 partial-drop 結構同族：
        //          進度真相在 cursor，掃描卻信固定窗）。本 fix 讓窗深度由 cursor 決定——從尾端起頁，
        //          倍增回頁到「最舊一筆的 ts 跨過該房全 webhook 最小 ts_high - W」為止，保證所有
        //          ShouldSend 可能判 true 的訊息都進得了掃描窗。
        // 數值影響：正常時（cursor 貼近尾端）就是一頁 Tail(SCAN_TAIL_N)，成本與舊版相同；積壓時自動
        //          加深，硬上限 SCAN_PAGE_MAX（超限印警告，本輪照送最舊優先，餘下下輪隨 cursor 推進續掃）。
        // 邊界：lower bound 拿不到（任一 cursor ts_high 空 = parse 異常）→ 退回固定窗（有界安全側）。
        static List<UCL_ChatMessage> CollectScanMessages(string room, List<string> webhookIds)
        {
            // 該房全 webhook 最小 ts_high - W = 回頁下界（跨過它 = 沒有更舊的可送訊息）
            string minTsHigh = UCL_DiscordMirrorState.GetMinTsHigh(room, webhookIds);
            DateTime? lowMark = null;
            var parsed = UCL_DiscordMirrorState.ParseIsoUtc(minTsHigh);
            if (parsed != null) lowMark = parsed.Value.AddSeconds(-UCL_DiscordMirrorState.DEDUP_WINDOW_SEC);

            int n = SCAN_TAIL_N;
            while (true)
            {
                var msgs = UCL_ChatTavernIO.Tail(room, n);   // 升冪（最舊→最新），Scan 依序送 = 最舊優先
                if (msgs == null || msgs.Count == 0) return msgs;
                if (msgs.Count < n) return msgs;             // 整房都拿到了，沒有更舊的
                if (lowMark == null) return msgs;            // 無下界資訊 → 維持固定窗（安全側，不做無界掃）

                // 最舊一筆已早於下界 → 窗已涵蓋所有可能未送的訊息
                var oldestTs = UCL_DiscordMirrorState.ParseIsoUtc(msgs[0].ts);
                if (oldestTs != null && oldestTs.Value < lowMark.Value) return msgs;

                if (n >= SCAN_PAGE_MAX)
                {
                    // 不沉默截斷：明示積壓超上限。本輪仍最舊優先送（cursor 隨 2xx 推進，下輪窗自動右移續掃）
                    Debug.LogWarning($"[DiscordMirror] Scan({room}) backlog exceeds SCAN_PAGE_MAX={SCAN_PAGE_MAX}，本輪處理最舊 {n} 筆，餘下下輪續掃");
                    return msgs;
                }
                n = Math.Min(n * 2, SCAN_PAGE_MAX);          // 倍增回頁，直到跨過下界或觸頂
            }
        }

        // 區塊職責：缺口熔斷 — 積壓超門檻時停送並示警，不讓 daemon 把整段歷史一口氣噴進 Discord
        // 物理意義：游標（ts_high）是「已送達外部的高水位」，但它跟訊息走同一個 git repo、卻不是同一筆 commit。
        //          pull 到比本機**舊**的 _tavern_state.json 就會把游標往回推（git 對這種檔案是 last-write-wins，
        //          而高水位標記的正確合併語意是 max()）→ daemon 忠實地把「游標之後」的整段歷史重送一次。
        //          2026-08-01 Tim 回報現場：本機同步到 seq 100、git 上已到 seq 200 → 一口氣連送 100 筆。
        //          **本熔斷不修根因**（根因是「載入時單調化」+「per-machine 欄位拆檔」，已排重構）——
        //          它只保證失敗會**吵**、不會變成 spam：外部可見的副作用先停住，等人來看一眼。
        // 數值影響：backlog > BURST_GUARD_MAX_BACKLOG → 該房本輪整個跳過（不送、也不推游標，維持可重試）；
        //          每 BURST_GUARD_WARN_INTERVAL_SEC 印一次警告。積壓降回門檻內自動放行，無需人工解鎖。
        // 邊界：① AdminPage「套用 seq」是**刻意**重放 → AdminSetRoomCursorToSeq 把該房加進 s_AdminBurstAllowed
        //          放行；追平後自動除名（下次 pull 造成的缺口仍照擋）。
        //      ② min ts_high 解析不出（全新無高水位）→ **不熔斷**：該情形 CollectScanMessages 已退回
        //          SCAN_TAIL_N(30) 固定窗，本來就送不出爆量，擋它反而讓新房永遠同步不了。
        //      ③ 熔斷狀態只在記憶體（Editor 重開即重新武裝）— 失效方向是「再擋一次」，安全側。
        static bool IsBurstGuardTripped(string room, List<UCL_ChatMessage> msgs, List<string> webhookIds)
        {
            // 與 CollectScanMessages 用同一組參數取下界 —— 兩邊對「積壓」的認定必須是同一個數，
            // 否則熔斷擋的量跟實際會送的量對不上（iSkipEmpty 差一個旗標就會分岔）。
            var minT = UCL_DiscordMirrorState.ParseIsoUtc(UCL_DiscordMirrorState.GetMinTsHigh(room, webhookIds));
            if (minT == null) return false;   // 邊界②

            int backlog = 0;
            for (int i = 0; i < msgs.Count; i++)
            {
                var t = UCL_DiscordMirrorState.ParseIsoUtc(msgs[i].ts);
                if (t != null && t.Value > minT.Value) backlog++;
            }

            if (backlog <= BURST_GUARD_MAX_BACKLOG)
            {
                s_AdminBurstAllowed.Remove(room);   // 追平 → 解除刻意重放豁免
                return false;
            }
            if (s_AdminBurstAllowed.Contains(room)) return false;   // 邊界①

            double now = EditorApplication.timeSinceStartup;
            if (!s_BurstGuardLastWarn.TryGetValue(room, out double last) || now - last >= BURST_GUARD_WARN_INTERVAL_SEC)
            {
                s_BurstGuardLastWarn[room] = now;
                Debug.LogWarning(
                    $"[DiscordMirror] 缺口熔斷：`{room}` 積壓 {backlog} 筆 > 門檻 {BURST_GUARD_MAX_BACKLOG}，"
                    + "本房 outbound 已暫停（不送、不推游標）。"
                    + "常見成因：git pull 到較舊的 _tavern_state.json 把游標往回推。"
                    + "確認後放行 → 控制台 → 酒館後台管理 → 🔗 Webhook 設定 → tavern_mirror → 對該房「套用 seq」"
                    + "（設 = 目前 max seq＝認定已送過、跳過不補；設小＝刻意重放該區間）。");
            }
            return true;
        }

        // 區塊職責：T6 — 單一訊息的路由目標判定（對齊 python notify_tavern_messages 的三層 precedence）
        // 物理意義：Layer1 quest sender 前綴 → 只走 quest webhooks；Layer2 category 命中 enabled groups
        //          （exclusive 命中 = 壟斷：main + 其他 additive 全跳過；否則 main + 命中群 additive）；
        //          沒命中 → default group additive；routing disabled → 純 main。
        // 數值影響：回傳 (url → routing tag) 映射；tag 進 Discord header 顯示（→ #main / → #<group>）。
        static Dictionary<string, string> ResolveMessageTargets(
            MirrorConfigBlock cfg, List<MirrorRoutingTarget> routing, List<string> mainUrls, UCL_ChatMessage msg)
        {
            var targets = new Dictionary<string, string>();   // url → tag（dict 天然去重同 URL）

            // Layer 1: quest sender 分流（python quest_routing）— 命中即壟斷，不進 main / category。
            // python parity：quest_routing_active = enabled 且解析得到 URL；解析不到 → 不啟用分流，
            // quest 訊息落回 Layer 2（跟 python 一致，不無聲丟棄）。
            if (cfg.quest_routing.enabled && !string.IsNullOrEmpty(cfg.quest_routing.sender_match_prefix)
                && !string.IsNullOrEmpty(msg.sender_id) && msg.sender_id.StartsWith(cfg.quest_routing.sender_match_prefix))
            {
                var questUrls = ResolveScopeUrls(cfg.quest_routing.webhook_env_var, cfg.quest_routing.webhook_file, cfg.quest_routing.webhook_urls);
                if (questUrls.Count > 0)
                {
                    foreach (var u in questUrls) targets[u] = "quest";
                    return targets;
                }
            }

            // Layer 2: category routing
            string category = "";
            if (msg.meta != null && msg.meta.TryGetValue("category", out var c) && c != null)
                category = c.Trim().ToLowerInvariant();

            var matched = new List<MirrorRoutingTarget>();
            bool hasExclusive = false;
            if (routing.Count > 0)
            {
                if (!string.IsNullOrEmpty(category))
                    foreach (var t in routing)
                        if (t.categories.Contains(category)) matched.Add(t);
                if (matched.Count == 0)
                {
                    // 沒命中 → 第一筆 default group（python parity：多 default 取第一筆）
                    foreach (var t in routing)
                        if (t.isDefault) { matched.Add(t); break; }
                }
                foreach (var t in matched)
                    if (t.exclusive) { hasExclusive = true; break; }
            }

            // main webhook — 只在沒命中 exclusive 時加入（T42「買一送一」修正的 native 對齊）
            if (!hasExclusive)
                foreach (var u in mainUrls) targets[u] = "main";

            foreach (var t in matched)
            {
                if (hasExclusive && !t.exclusive) continue;   // exclusive 模式下其他 additive 也跳過
                foreach (var u in t.urls) targets[u] = t.id;
            }
            return targets;
        }

        // 區塊職責：follow-up#1 — body 切 chunk（python _split_body_for_discord parity）
        // 物理意義：Tim 2026-05-13 拍板「寧可發多條也不要截斷」。優先在 \n 邊界切（讀感完整），
        //          單行超長才硬切。body_max 預設 1500（Discord 2000 硬限留 buffer 給 title/part marker）。
        static List<string> SplitBody(string body, int max)
        {
            var chunks = new List<string>();
            if (string.IsNullOrEmpty(body) || body.Length <= max) { chunks.Add(body ?? ""); return chunks; }
            int pos = 0;
            while (pos < body.Length)
            {
                int len = Math.Min(max, body.Length - pos);
                if (pos + len < body.Length)
                {
                    // 在切點往回找最近的換行 — 找到就切在換行後（換行歸前段）；整段無換行 → 硬切
                    int nl = body.LastIndexOf('\n', pos + len - 1, len);
                    if (nl > pos) len = nl - pos + 1;
                }
                chunks.Add(body.Substring(pos, len));
                pos += len;
            }
            return chunks;
        }

        // 區塊職責：Discord 顯示 content 組裝 — title header（room/sender/seq/routing tag）+ 多 chunk
        // 物理意義：多房 mirror 進同頻道時沒 header 分不清來源；超長 body 切 chunk 順序送
        //          （對齊 python：首條帶 title + part 1/N 標記，續條帶「seq X 續 part i/N」標記）。
        // 數值影響：回傳 list（≥1 條）；caller 首條 StartPost、餘下掛 pendingContents 逐條續發。
        static List<string> BuildContents(MirrorConfigBlock cfg, string room, UCL_ChatMessage msg, string username, string routingTag, string body)
        {
            string header = (cfg.title_template ?? "")
                .Replace("{room}", room)
                .Replace("{sender_name}", username ?? "")
                .Replace("{seq}", msg.seq.ToString());
            if (!string.IsNullOrEmpty(routingTag)) header += $" → #{routingTag}";

            var chunks = SplitBody(body, cfg.body_max);
            var contents = new List<string>();
            int total = chunks.Count;
            for (int i = 0; i < total; i++)
            {
                if (total == 1)
                    contents.Add(string.IsNullOrEmpty(header) ? chunks[i] : header + "\n" + chunks[i]);
                else if (i == 0)
                    contents.Add($"{header} _(part 1/{total})_\n{chunks[i]}");
                else
                    contents.Add($"_(seq {msg.seq} 續 part {i + 1}/{total})_\n{chunks[i]}");
            }
            return contents;
        }

        // 區塊職責：掃 config rooms，對每則訊息按 T6 路由送出 + 全 webhook cursor 鎖步推進
        // 物理意義：cursor-driven 收集掃描窗（Bug B）→ kind/echo/exclude_senders 過濾 → ResolveMessageTargets
        //          決定 (url→tag) → 目標 webhook 走 ShouldSend/StartPost；**非目標 webhook 直接記 processed**。
        // 數值影響：鎖步推進讓冷門頻道（很少收到自己 category 的 webhook）的 ts_high 不落後 → CollectScanMessages
        //          的 min(ts_high) 下界貼近尾端，掃描窗常態一頁；否則冷門 cursor 會拖著全房每 tick 深回頁。
        //          cursor 語意由「sent 高水位」擴為「processed 高水位」——目標送出仍等 2xx 才記（單寫者不變）。
        static void Scan()
        {
            var cfg = ReadConfig();
            if (cfg.rooms.Count == 0) return;

            var routing = cfg.category_routing.enabled ? ReadRoutingTargets() : new List<MirrorRoutingTarget>();
            var mainUrls = new List<string>();
            foreach (var u in cfg.webhook_urls)
                if (!string.IsNullOrEmpty(u) && !IsPlaceholderUrl(u)) mainUrls.Add(u);
            if (mainUrls.Count == 0 && routing.Count == 0) return;   // 完全無處可送

            // 房間可能觸及的全 webhook 聯集（cursor 下界 + 鎖步推進的範圍）：main + 全 routing groups + quest
            var allWids = new List<string>();
            var allWidSet = new HashSet<string>();
            void AddWid(string url)
            {
                string w = UCL_DiscordWebhookClient.ExtractWebhookId(url);
                if (w != null && allWidSet.Add(w)) allWids.Add(w);
            }
            foreach (var u in mainUrls) AddWid(u);
            foreach (var t in routing) foreach (var u in t.urls) AddWid(u);
            if (cfg.quest_routing.enabled)
                foreach (var u in ResolveScopeUrls(cfg.quest_routing.webhook_env_var, cfg.quest_routing.webhook_file, cfg.quest_routing.webhook_urls)) AddWid(u);

            bool anyStateChange = false;
            bool inflightFull = false;

            foreach (var room in cfg.rooms)
            {
                if (inflightFull) break;
                if (string.IsNullOrEmpty(room)) continue;
                // tick 早退：訊息數與 min ts_high 都沒動 → 這輪必然與上輪同結果，整房跳過
                if (!RoomNeedsScan(room, allWids)) continue;
                List<UCL_ChatMessage> msgs;
                try { msgs = CollectScanMessages(room, allWids); }
                catch { continue; }
                if (msgs == null) continue;
                // 缺口熔斷：積壓超門檻 → 本房整輪跳過（含鎖步推進，游標一格都不動 = 可重試）
                if (IsBurstGuardTripped(room, msgs, allWids)) continue;

                foreach (var msg in msgs)
                {
                    if (inflightFull) break;
                    if (msg == null || string.IsNullOrEmpty(msg.uuid)) continue;

                    // eligible 判定：kind 過濾 + echo 排除 + exclude_senders（python parity）
                    string kind = string.IsNullOrEmpty(msg.kind) ? "chat" : msg.kind;
                    bool eligible = cfg.kinds.Contains(kind) && !IsEcho(msg)
                        && !(msg.sender_id != null && cfg.exclude_senders.Contains(msg.sender_id));

                    var targets = eligible
                        ? ResolveMessageTargets(cfg, routing, mainUrls, msg)
                        : new Dictionary<string, string>();

                    // 目標 webhook id 集（給下方鎖步推進判「誰不是目標」）
                    var targetWids = new HashSet<string>();
                    foreach (var kv in targets)
                    {
                        string wid = UCL_DiscordWebhookClient.ExtractWebhookId(kv.Key);
                        if (wid != null) targetWids.Add(wid);
                    }

                    // 目標路徑：ShouldSend → StartPost 掛 in-flight（2xx 後 DrainInFlight 才記 cursor，單寫者）
                    foreach (var kv in targets)
                    {
                        string url = kv.Key;
                        string wid = UCL_DiscordWebhookClient.ExtractWebhookId(url);
                        if (wid == null) continue;
                        if (!UCL_DiscordMirrorState.ShouldSend(room, wid, msg.uuid, msg.ts)) continue;
                        if (IsInFlight(room, wid, msg.uuid)) continue;
                        if (s_InFlight.Count >= MAX_INFLIGHT) { inflightFull = true; break; }   // 下輪 tick 續送

                        // T5：解析 persona 頭像 + 清洗 username + @-mention rewrite（對齊 python 顯示）
                        // 帶 sender_persona → username 尾綴 "@<persona>"（python parity，Tim 2026-07-20 抓漏修正）
                        string username = UCL_DiscordIdentityResolver.ResolveUsername(msg.sender_id, msg.sender_name, msg.DisplayName, msg.sender_persona);
                        string avatarUrl = UCL_DiscordIdentityResolver.ResolveAvatarUrl(msg.sender_persona, msg.sender_avatar_sprite, msg.sender_id);
                        string body = UCL_DiscordIdentityResolver.RewriteMentions(msg.body ?? "");
                        // follow-up#1：超長 body 切 chunk — 首條即發，餘下掛 pendingContents 由 DrainInFlight 順序續發
                        var contents = BuildContents(cfg, room, msg, username, kv.Value, body);
                        var req = UCL_DiscordWebhookClient.StartPost(url, contents[0], username, avatarUrl, null);
                        if (req != null)
                            s_InFlight.Add(new MirrorInFlight
                            {
                                req = req, room = room, webhookId = wid, uuid = msg.uuid, ts = msg.ts, recordOnSuccess = true,
                                url = url, username = username, avatarUrl = avatarUrl,
                                pendingContents = contents.Count > 1 ? contents.GetRange(1, contents.Count - 1) : null,
                                partIndex = 1, partTotal = contents.Count,
                            });
                    }
                    if (inflightFull) break;

                    // 鎖步推進：這則訊息「不歸」的 webhook 直接記 processed（不送、不等 2xx）
                    // 沒有它，冷門 group webhook 的 ts_high 永遠停在上次收到自己 category 的時刻 →
                    // min(ts_high) 拖著整房每 tick 深回頁。ineligible 訊息 = 對全部 webhook 都記 processed。
                    foreach (var wid in allWids)
                    {
                        if (targetWids.Contains(wid)) continue;
                        if (!UCL_DiscordMirrorState.ShouldSend(room, wid, msg.uuid, msg.ts)) continue;
                        UCL_DiscordMirrorState.RecordSent(room, wid, msg.uuid, msg.ts);
                        anyStateChange = true;
                    }
                }
            }

            if (anyStateChange) UCL_DiscordMirrorState.Save();
        }

        // ===========================================================
        // 區塊：AdminPage 手動游標操作接點（T6.5 — 套用 seq / 追平 / 進度顯示的 native 端）
        // 物理意義：AdminPage 舊控件直改 python last_seen_seq（native 不讀 → 純誤導）。本區把「seq N 邊界」
        //          翻成 native 游標語意（ts_high + 窗內 seen），並提供 native 版「已同步/待同步」反推供顯示。
        // 數值影響：admin 動作屬手動低頻，直接 read messages（Tail）+ 全房 webhook 重設；不進 daemon hot path。
        //          seq = UCL_ChatTavernIO.Tail 的絕對檔序位（1-based），與 AdminPage 讀 _seq.txt 的 maxSeq 同義。
        // ===========================================================

        // 區塊職責：解析某房「會觸及的全部 webhook id」（main + 全 routing group + quest）— admin 重設/顯示的作用域
        // 物理意義：與 Scan 的 allWids 聯集同語意，但 admin 低頻故獨立重算（不動已驗證的 Scan hot path）。
        //          URL 解析走與 Scan 相同的 ENV>FILE>config 優先序 + placeholder 過濾，確保重設到的 webhook
        //          集合跟 daemon 實際會送的集合一致（否則會漏設某 webhook → 該 webhook 仍按舊游標重送）。
        // 數值影響：回傳去重後的 webhook id 清單；room 參數目前僅語意標記（URL 為 config 全域、非 per-room）。
        /// <summary>某房會觸及的全部 webhook id（main + routing groups + quest）— admin 重設/進度顯示的作用域。</summary>
        public static List<string> GetRoomWebhookIds(string room)
        {
            var wids = new List<string>();
            var set = new HashSet<string>();
            void Add(string url)
            {
                string w = UCL_DiscordWebhookClient.ExtractWebhookId(url);
                if (w != null && set.Add(w)) wids.Add(w);
            }
            try
            {
                var cfg = ReadConfig();
                foreach (var u in cfg.webhook_urls)
                    if (!string.IsNullOrEmpty(u) && !IsPlaceholderUrl(u)) Add(u);
                var routing = cfg.category_routing.enabled ? ReadRoutingTargets() : new List<MirrorRoutingTarget>();
                foreach (var t in routing) foreach (var u in t.urls) Add(u);
                if (cfg.quest_routing.enabled)
                    foreach (var u in ResolveScopeUrls(cfg.quest_routing.webhook_env_var, cfg.quest_routing.webhook_file, cfg.quest_routing.webhook_urls))
                        Add(u);
            }
            catch (Exception e) { Debug.LogWarning($"[DiscordMirror] GetRoomWebhookIds fail: {e.Message}"); }
            return wids;
        }

        // 區塊職責：把「seq N 為已處理邊界」翻成 native 游標並重設該房全 webhook（AdminPage「套用 seq / 追平」）
        // 物理意義：native 無 seq → 用 seq N 對應訊息的 ts 當 ts_high、把 [ts_high-W, ts_high] 窗內且 seq≤N 的
        //          uuid 灌進 seen_uuids。結果 ShouldSend 對 seq≤N 一律 false（跳過不送）、對 seq>N 一律 true
        //          （ts 更新 → 送 / 窗內未 seen → 送）= 等價舊 last_seen_seq 的「設 N 小=重放、N≥max=追平」。
        // 數值影響：iTargetSeq≤0 → 清空游標（全房重放）；否則讀尾窗 (maxSeq-N + WINDOW_MARGIN) 筆足以涵蓋
        //          seq N 及其前 W 秒窗（tavern 訊息密度下 400 筆遠超 120s；窗未滿頂多邊界多重送一次，
        //          對齊「fail 方向=可見重送非隱形漏」設計哲學，絕不靜默漏送）。回人類可讀狀態字串給 AdminPage log。
        // 邊界：往回調（N < 當前已同步）= 該區間訊息會重發到 Discord（外部可見）— Tim 2026-07-21 拍板保留此能力。
        const int ADMIN_WINDOW_MARGIN = 400;
        /// <summary>把某房游標重設到「seq iTargetSeq 為已處理邊界」（seq≤N 跳過、seq&gt;N 重送）。回狀態字串。</summary>
        public static string AdminSetRoomCursorToSeq(string room, int iTargetSeq, int iMaxSeq)
        {
            if (string.IsNullOrEmpty(room)) return "room 為空";
            var wids = GetRoomWebhookIds(room);
            if (wids.Count == 0) return $"{room}：config 無 configured webhook（無事可做）";

            // seq ≤ 0 = 全部重放：ts_high 設「遠古 sentinel」而非空字串（Tim 2026-07-21 實測踩到的坑）。
            // 為何不用空字串：daemon.CollectScanMessages 遇 ts_high 空 → GetMinTsHigh 回 ""→ lowMark=null →
            // 退回固定 SCAN_TAIL_N(30) 尾窗 → 房內 >30 筆時最舊區間掉出掃描窗永遠不重放（trpg-yachiyo 34 筆
            // 實測只重放 seq 5-34、seq 1-4 漏）。改設遠古 ts → 全房訊息都「晚於 ts_high」必送、且 lowMark
            // 有值讓 CollectScanMessages 倍增回頁到全房（受 daemon SCAN_PAGE_MAX=4096 上限，超過的最舊區間
            // 屬 daemon 既有有界掃描限制，不在本 admin 操作內補）。
            const string REPLAY_ALL_TS = "1970-01-01T00:00:00.000Z";
            if (iTargetSeq <= 0)
            {
                s_AdminBurstAllowed.Add(room);   // 刻意重放豁免（見下方 AdminSetRoomCursors 處註解）
                UCL_DiscordMirrorState.AdminSetRoomCursors(room, wids, REPLAY_ALL_TS, null);
                return $"{room}：游標設到最舊（seq 0 = 全房重放，ts_high={REPLAY_ALL_TS}；下輪 daemon 依序重送全部 eligible 訊息到 Discord，深度上限 {SCAN_PAGE_MAX} 筆）";
            }

            // 讀足夠尾窗：涵蓋 seq iTargetSeq 本身 + 其前 W 秒窗（供 seen 建構）
            int readN = Math.Max(1, iMaxSeq - iTargetSeq) + ADMIN_WINDOW_MARGIN;
            List<UCL_ChatMessage> msgs;
            try { msgs = UCL_ChatTavernIO.Tail(room, readN); }
            catch (Exception e) { return $"{room}：讀訊息失敗 {e.Message}"; }
            if (msgs == null || msgs.Count == 0) return $"{room}：無訊息可讀";

            // 找 target 訊息（seq == iTargetSeq，或 ≤ iTargetSeq 的最大 seq；iTargetSeq≥max → 最後一筆）
            UCL_ChatMessage target = null;
            foreach (var m in msgs) { if (m.seq <= iTargetSeq) target = m; else break; }
            if (target == null) return $"{room}：讀不到 seq ≤ {iTargetSeq} 的訊息（尾窗未涵蓋，可能 seq 過小）";
            string targetTs = target.ts;

            // 窗內 seen：seq ≤ iTargetSeq 且 ts 落在 [targetTs - W, targetTs] 內的 uuid→ts
            var seen = new Dictionary<string, string>();
            DateTime? tHigh = UCL_DiscordMirrorState.ParseIsoUtc(targetTs);
            foreach (var m in msgs)
            {
                if (m.seq > iTargetSeq) continue;
                if (string.IsNullOrEmpty(m.uuid) || string.IsNullOrEmpty(m.ts)) continue;
                var t = UCL_DiscordMirrorState.ParseIsoUtc(m.ts);
                if (tHigh != null && t != null && t.Value < tHigh.Value.AddSeconds(-UCL_DiscordMirrorState.DEDUP_WINDOW_SEC)) continue;
                seen[m.uuid] = m.ts;
            }

            // 人在 AdminPage 按了「套用 seq」＝ 刻意操作游標，後續積壓是預期的 → 豁免缺口熔斷
            // （IsBurstGuardTripped），追平後自動除名。不豁免的話 admin 重放會被自己的熔斷擋死。
            // 只在**真的動了游標**的路徑上加豁免 —— 上面那些 return（讀檔失敗 / 找不到 target）不加，
            // 否則一次失敗的 admin 操作會靜默解除該房的熔斷保護。
            s_AdminBurstAllowed.Add(room);
            UCL_DiscordMirrorState.AdminSetRoomCursors(room, wids, targetTs, seen);
            return $"{room}：游標設到 seq {iTargetSeq}（ts_high={targetTs}，seen {seen.Count} 筆 × {wids.Count} webhook；seq>{iTargetSeq} 下輪重送、≤ 跳過）";
        }

        // 區塊職責：native 版「已同步到 seq / 待同步筆數」反推（AdminPage 顯示 — 取代 python last_seen_seq）
        // 物理意義：native 游標是 per-webhook ts_high；顯示取「該房全 webhook 最小 ts_high」（最落後者 = 保守
        //          catch-up 真相），從尾端數「ts > 該 ts_high」的筆數 = 待同步；已同步 = maxSeq - 待同步。
        // 數值影響：從尾端漸進讀（64 起倍增），一遇到「ts ≤ min_ts_high」即停 → 已追平房只讀 ~64 檔（不整房掃）；
        //          積壓深時讀到 ADMIN_PROGRESS_CAP 上限止（oCapped=true，顯示標「≥」不假裝精確）。
        //          min_ts_high 空（全新無高水位）→ 全房待送（replay-all 狀態）。
        const int ADMIN_PROGRESS_CAP = 4096;
        /// <summary>native 進度反推：算某房「已同步到 seq / 待同步筆數」（min ts_high vs 尾端訊息 ts）。</summary>
        public static void GetRoomNativeProgress(string room, int iMaxSeq, out int oSyncedSeq, out int oPending, out bool oCapped)
        {
            oSyncedSeq = iMaxSeq; oPending = 0; oCapped = false;
            var wids = GetRoomWebhookIds(room);
            if (wids.Count == 0) return;   // 無 webhook = 無待送（視為已追平）

            // iSkipEmpty=true：跳過 ts_high 空的 webhook（backoff 卡住/舊 replay-all 殘留），
            // 否則單一空 cursor 會把整房顯示歸零（Tim 2026-07-21 實測：trpg-yachiyo 已同步卻顯示 0）
            string minTs = UCL_DiscordMirrorState.GetMinTsHigh(room, wids, iSkipEmpty: true);
            DateTime? minT = UCL_DiscordMirrorState.ParseIsoUtc(minTs);
            if (minT == null) { oPending = iMaxSeq; oSyncedSeq = 0; return; }   // 全 webhook 皆無高水位 = 全房待送

            // 從尾端漸進讀，數「ts > min_ts_high」的筆數；遇到 ≤ 邊界即停（已追平房極便宜）
            int n = 64;
            while (true)
            {
                List<UCL_ChatMessage> msgs;
                try { msgs = UCL_ChatTavernIO.Tail(room, n); }
                catch { break; }
                if (msgs == null || msgs.Count == 0) { oPending = 0; break; }

                int cnt = 0; bool foundBoundary = false;
                for (int i = msgs.Count - 1; i >= 0; i--)
                {
                    var t = UCL_DiscordMirrorState.ParseIsoUtc(msgs[i].ts);
                    if (t != null && t.Value > minT.Value) cnt++;
                    else { foundBoundary = true; break; }
                }
                // 找到邊界（有一筆 ts≤min）或整房讀完（msgs 不足 n）→ cnt 即精確待送數
                if (foundBoundary || msgs.Count < n) { oPending = cnt; break; }
                if (n >= ADMIN_PROGRESS_CAP) { oPending = cnt; oCapped = true; break; }   // 積壓超上限，標「≥」
                n = Math.Min(n * 2, ADMIN_PROGRESS_CAP);
            }
            oSyncedSeq = Math.Max(0, iMaxSeq - oPending);
        }
    }
}
#endif
