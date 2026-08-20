// 區塊職責：Discord → ChatTavern inbound 中繼 daemon（C# native，取代 discord_inbound_bot.py）。
// 物理意義：跟 UCL_DiscordMirrorDaemon 對偶 — 後者 outbound（酒館 → Discord webhook），本檔 inbound
//          （Discord 頻道 → 酒館訊息檔）。**雙進料管線**（2026-07-28 Tim 拍板補 gateway）：
//            ⚡ 主路 = gateway WebSocket 推送（UCL_DiscordGatewayClient）— 即時，且**讓 bot 顯示上線**
//               （上線綠點是 gateway 專屬功能，REST 再勤也永遠離線）。
//            🛡 安全網 = REST 慢速追平（60s 一輪）— 補 gateway 斷線 / INVALID_SESSION 空窗期漏掉的訊息
//               （gateway 只有 RESUME 成功才補送，session 失效就永久漏 = 舊 python bot 的洞）。
//          兩路共用同一份 last_message_id 游標與同一份 RelayOne（過濾 / 附件 / 寫入），故不會重送、不漂移。
//          當初選 REST 起手的理由仍然成立、也仍是安全網的價值所在：
//            ① Editor domain reload 頻繁會砍長連線；輪詢無狀態，游標存 last_message_id 即可無痛續傳
//               （與 outbound 的 ts_high 游標同構）。
//            ② 省掉 IDENTIFY / HEARTBEAT / RESUME / zlib / 斷線重連整套協議（500+ 行複雜度）。
//            ③ 延遲與舊 python 路徑同量級 — 後者每筆訊息還要 spawn run_cmd.py + 過 queue.json 等 watcher。
// 數值影響：每 POLL_INTERVAL_SECONDS 輪一個 channel（round-robin，同時只有一個 in-flight request）→
//          N 個頻道的實效延遲 ≈ N × interval；3 頻道 × 3s ≈ 9s 最壞。Discord rate limit 綽綽有餘。
// 設計取捨（2026-07-28 事故教訓）：寫入端走 in-process UCL_ChatTavernIO.AppendMessage，
//          **絕不 spawn 任何 subprocess** — 舊 python 每筆訊息 spawn 一隻 run_cmd.py，是那族併發失控的根。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// Discord → 酒館 inbound 中繼（REST 輪詢）。Editor 內單執行緒單寫者，無 subprocess、無長連線。
    /// 開關：UCL_ProjectEditorPrefs（per-project，預設 OFF）× `notify_config.json` 的 `tavern_inbound.enabled`（意圖）。
    /// 路由：`ChatTavern/discord_channel_routing.json` 的 mappings（enabled 列）。
    /// 游標：`_tavern_state.json` → `inbound.channels.&lt;channelId&gt;.last_message_id`。
    /// </summary>
    [InitializeOnLoad]
    public static class UCL_DiscordInboundDaemon
    {
        // 區塊職責：節流與端點常數
        // 物理意義：POLL_INTERVAL 是「每隔多久發下一個 channel 的 GET」；同時只有一個 in-flight request
        //          （序列化 = 天然限流 + 判讀簡單），故實效 per-channel 延遲 = N × interval。
        // 數值影響：3s × 3 頻道 ≈ 9s 最壞延遲；Discord 每路由桶約 5 req/5s，序列化後遠低於上限。
        // gateway 接手即時推送後，REST 降為「慢速追平安全網」：只補 gateway 斷線 / INVALID_SESSION
        // 空窗期漏掉的訊息（gateway 只有 RESUME 成功才補送，session 失效就永久漏 —— 舊 python bot 的洞）。
        const double POLL_INTERVAL_SECONDS = 60.0;
        const int REQUEST_TIMEOUT_SEC = 15;
        const int FETCH_LIMIT = 50;            // 單次最多取回幾筆（Discord 上限 100；50 夠一輪 burst）
        const string API_BASE = "https://discord.com/api/v10";
        // Discord 前面的 Cloudflare 會間歇性擋預設 UA（outbound 端已踩過）→ 明確帶自訂 UA
        const string USER_AGENT = "UCL_Core-DiscordInbound (Unity Editor)";
        const string EnabledPrefKey = "UCL_DiscordInboundDaemon.Enabled";
        const string TokenFileName = "discord_bot_token.txt";
        const string NotifyConfigFileName = "notify_config.json";
        const string KeyTavernInbound = "tavern_inbound";
        const string KeyUserWhitelist = "user_whitelist";
        const string KeyWhitelistEnabled = "enabled";
        const string KeyWhitelistUsers = "users";
        const string KeyUserId = "user_id";
        const string KeyDisplayName = "display_name";
        const string KeyProfile = "profile";
        /// <summary>Process 註冊中心 tag — AdminPage 顯示中繼器狀態時認這個字串（native 版沿用同 tag 語意）。</summary>
        public const string RelayTag = "discord_inbound";

        static double s_LastPollTime = 0;
        static int s_RoundRobinIdx = 0;          // 下一個要輪的 channel index（跨 tick 遞增）
        static UnityWebRequest s_InFlight;       // 同時只有一個；null = 空閒
        static InboundRoute s_InFlightRoute;     // 上述 request 對應的路由列
        static bool s_InFlightIsBaseline;        // 本次 request 是 baseline 探測（只學游標不轉發）
        static bool s_TokenMissingLogged;        // token 缺席只警告一次，不洗 console

        // 區塊職責：Discord inbound 白名單快取（notify_config.json → tavern_inbound.user_whitelist）。
        // 物理意義：頻道 routing 決定「哪個地方的訊息可進酒館」；此表再決定「該頻道裡的哪個 Discord 帳號可信」。
        // 數值影響：enabled=false（缺欄位亦同）不過濾；enabled=true 時未列 user_id 一律略過，名稱覆寫只影響酒館顯示。
        static readonly HashSet<string> s_WhitelistedUserIds = new HashSet<string>(StringComparer.Ordinal);
        static readonly Dictionary<string, string> s_WhitelistedDisplayNames = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly Dictionary<string, string> s_WhitelistedProfiles = new Dictionary<string, string>(StringComparer.Ordinal);
        static long s_WhitelistConfigMtime = -1;
        static bool s_WhitelistEnabled;

        /// <summary>本 session 已中繼筆數（AdminPage 顯示用；domain reload 歸零）。</summary>
        public static int RelayedThisSession { get; private set; }
        /// <summary>最近一次錯誤（空 = 無）。禁靜默失敗的 UI 出口。</summary>
        public static string LastError { get; private set; } = "";
        /// <summary>最近一次成功輪詢的 UTC 時刻（顯示「多久沒動」用）。</summary>
        public static string LastPollUtc { get; private set; } = "";

        static UCL_DiscordInboundDaemon()
        {
            try
            {
                EditorApplication.update += Tick;
                // domain reload / Editor 退出前主動斷 gateway → bot 立刻轉離線（不留殘影綠點）
                AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
                EditorApplication.quitting += OnEditorQuitting;
            }
            catch (Exception e) { Debug.LogWarning($"[DiscordInbound] init fail: {e.Message}"); }
        }

        static void OnBeforeAssemblyReload()
        {
            // reload 後 static 全歸零 → s_GatewayWanted 會是 false，下個 tick 依 IsLive 自動重連
            UCL_DiscordGatewayClient.Stop();
        }

        static void OnEditorQuitting() => UCL_DiscordGatewayClient.Stop();

        /// <summary>
        /// daemon 是否啟用。預設 OFF（對齊 outbound mirror 的顯式 opt-in 慣例）— EditorPrefs 持久化，
        /// per-project（UCL_ProjectEditorPrefs），跨 domain reload / Editor 重啟不失。實際是否運作還要 config 的 tavern_inbound.enabled 為 true。
        /// </summary>
        public static bool Enabled
        {
            get => UCL_ProjectEditorPrefs.GetBool(EnabledPrefKey, false);
            set
            {
                UCL_ProjectEditorPrefs.SetBool(EnabledPrefKey, value);
                s_TokenMissingLogged = false;   // 重新 toggle → 允許再警告一次 token 缺席
                Debug.Log($"[DiscordInbound] Enabled = {value}");
            }
        }

        [MenuItem("UCL/Discord Mirror/Toggle Inbound Daemon")]
        static void ToggleEnabledMenu() => Enabled = !Enabled;

        /// <summary>config 端意圖開關（`notify_config.json` → `tavern_inbound.enabled`）。缺欄位視為 off。</summary>
        public static bool ConfigEnabled
        {
            get
            {
                try
                {
                    string path = Path.Combine(UCL_RepoPath.AgentCommandsDir, "PromptQueue", "notify_config.json");
                    if (!File.Exists(path)) return false;
                    var jd = JsonData.ParseJson(File.ReadAllText(path));
                    if (jd == null || !jd.IsObject || !jd.Contains("tavern_inbound")) return false;
                    return jd["tavern_inbound"].GetBool("enabled", false);
                }
                catch { return false; }
            }
        }

        /// <summary>
        /// 兩道閘門都開才會真的運作（AdminPage / 控制台顯示用）：
        ///   ① 本 daemon 的 per-project 開關（UCL_ProjectEditorPrefs，預設 OFF）— 控制台 / 酒館後台 / 選單皆可切
        ///   ② config 意圖（notify_config.json → tavern_inbound.enabled）
        /// 註（Tim 2026-07-28 拍板）：**不掛**酒館系統總開關（UCL_ChatTavernSystemControl）—
        ///   那顆管的是酒館自動廣播與 Bartender 等背景程序，inbound 中繼是獨立關注點，各自開關。
        /// </summary>
        public static bool IsLive => Enabled && ConfigEnabled;

        // ===========================================================
        // 區塊：tick — 節流 + fail-safe 包裹（任何例外都不讓 Editor update 掛掉）
        // ===========================================================
        static void Tick()
        {
            try
            {
                // ① gateway 生命週期跟 IsLive 連動（Tim 2026-07-28 要求「開啟 inbound 時 bot 上線、關閉後下線」）
                SyncGateway();
                // ② gateway 推來的 MESSAGE_CREATE 在主緒 drain → 寫檔（單寫者不變量：背景緒只入列不寫檔）
                DrainGatewayDispatch();

                // in-flight 判讀不受節流限制 — 請求完成就該立刻處理，否則白等一個 interval
                if (s_InFlight != null)
                {
                    if (!s_InFlight.isDone) return;
                    DrainInFlight();
                    return;
                }
                if (!IsLive) return;
                double now = EditorApplication.timeSinceStartup;
                if (now - s_LastPollTime < POLL_INTERVAL_SECONDS) return;
                s_LastPollTime = now;
                StartNextPoll();
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Debug.LogWarning($"[DiscordInbound] tick exception: {e.Message}");
                DisposeInFlight();
            }
        }

        /// <summary>手動立即輪詢一輪（AdminPage「立即檢查」用）— 跳過節流。</summary>
        public static void ForcePoll()
        {
            try
            {
                if (!IsLive)
                {
                    Debug.LogWarning($"[DiscordInbound] 未啟用（daemon={Enabled} / config={ConfigEnabled}）— 不輪詢");
                    return;
                }
                if (s_InFlight != null) return;   // 已有 in-flight → 讓它先完成
                s_LastPollTime = EditorApplication.timeSinceStartup;
                StartNextPoll();
            }
            catch (Exception e) { Debug.LogWarning($"[DiscordInbound] force poll fail: {e.Message}"); }
        }

        // ===========================================================
        // 區塊：gateway 生命週期同步（presence 的唯一來源）
        // 物理意義：bot 的「上線綠點」只有 gateway session 活著時才亮 —— 所以 IsLive（daemon 開關 × config）
        //          一變 true 就連 gateway、一變 false 就主動 Close。主動 Close 很重要：不關的話 Discord
        //          要等心跳逾時才標離線，綠點會殘留數十秒，看起來像「關不掉」。
        // 數值影響：每 tick 只比對 bool，實際連線動作只在狀態轉換時發生（不會反覆連斷）。
        //          token 缺席時不連（連了也會被拒），並沿用既有的「只警告一次」節流。
        // ===========================================================
        static bool s_GatewayWanted;

        static void SyncGateway()
        {
            bool want = IsLive && HasToken;
            if (want == s_GatewayWanted) return;   // 狀態沒變 → 不動作
            s_GatewayWanted = want;
            if (want)
            {
                UCL_DiscordGatewayClient.Start(ResolveToken());
                Debug.Log("[DiscordInbound] inbound 啟用 → 連 gateway（bot 即將在 Discord 顯示上線）");
            }
            else
            {
                UCL_DiscordGatewayClient.Stop();
                Debug.Log("[DiscordInbound] inbound 關閉 → 斷 gateway（bot 轉離線）");
            }
        }

        // ===========================================================
        // 區塊：gateway MESSAGE_CREATE → 中繼（主緒）
        // 物理意義：payload 是完整 Discord message 物件，跟 REST 回傳的同 schema → 直接複用 RelayOne，
        //          兩條進料管線共用同一份過濾 / 附件下載 / 寫入邏輯（避免兩套實作漂移）。
        // 數值影響：channel_id 不在 routing 表 → 靜默丟棄（不是所有頻道都要中繼）；
        //          中繼成功後**推進同一份 last_message_id 游標** → REST 追平不會重送同一筆（去重收斂於單一游標）。
        // 邊界：單 tick 最多處理 MAX_DISPATCH_PER_TICK 筆，避免 burst 時附件下載把主緒卡住太久。
        // ===========================================================
        const int MAX_DISPATCH_PER_TICK = 5;

        static void DrainGatewayDispatch()
        {
            int handled = 0;
            while (handled < MAX_DISPATCH_PER_TICK && UCL_DiscordGatewayClient.TryDequeueDispatch(out string payload))
            {
                handled++;
                try
                {
                    var msg = JsonData.ParseJson(payload);
                    if (msg == null || !msg.IsObject) continue;
                    string chId = msg.GetString("channel_id", "");
                    if (string.IsNullOrEmpty(chId)) continue;

                    InboundRoute route = null;
                    foreach (var r in GetRoutes())
                        if (r.channelId == chId) { route = r; break; }
                    if (route == null) continue;   // 非 watched 頻道 → 丟棄

                    string reason = RelayOne(msg, route);
                    string msgId = msg.GetString("id", "");
                    if (reason == null)
                    {
                        RelayedThisSession++;
                        LastPollUtc = UtcNowIso();
                        Debug.Log($"[DiscordInbound] ⚡ gateway 即時中繼 — {route.label} → room={route.tavernRoom}（msg={msgId}）");
                    }
                    else
                    {
                        Debug.Log($"[DiscordInbound] gateway 事件略過［{reason}］（{route.label}, msg={msgId}）");
                    }
                    // 不論中繼或略過都推進游標 —— 該筆已處理過，REST 追平不必再撈
                    if (!string.IsNullOrEmpty(msgId)) AdvanceCursorIfNewer(route.channelId, msgId);
                }
                catch (Exception e)
                {
                    LastError = $"gateway 事件處理失敗: {e.Message}";
                    Debug.LogWarning($"[DiscordInbound] {LastError}");
                }
            }
        }

        /// <summary>只在 newId 比現有游標「更新」時才推進（snowflake 單調遞增 → 數值比大小即時間序）。</summary>
        static void AdvanceCursorIfNewer(string channelId, string newId)
        {
            try
            {
                string cur = LoadCursor(channelId);
                if (!string.IsNullOrEmpty(cur)
                    && ulong.TryParse(cur, out ulong curV) && ulong.TryParse(newId, out ulong newV)
                    && newV <= curV)
                    return;   // 舊事件（RESUME 補送重複等）→ 不倒退游標
                SaveCursor(channelId, newId);
            }
            catch { /* 游標推進失敗不致命：REST 追平會再撈一次（可見重送非隱形漏）*/ }
        }

        // ===========================================================
        // 區塊：路由表（channel → room）讀取 + mtime 快取
        // 物理意義：discord_channel_routing.json 是 single source of truth（CRUD 在
        //          UCL_DiscordChannelRoutingPage）。本 daemon 只讀 enabled 列。
        // 數值影響：mtime 沒變就用快取 — 每 tick 不重 parse；改完 routing 存檔後下一 tick 自動生效
        //          （比舊 python bot 需 kill 重啟好；bot 是 startup-time 讀）。
        // ===========================================================
        public class InboundRoute
        {
            public string channelId = "";
            public string tavernRoom = "";
            public string label = "";
            public string sourceClass = "external";
            public int priority = 0;
            public string guildId = "";
        }

        static List<InboundRoute> s_Routes = new List<InboundRoute>();
        static long s_RoutingMtime = -1;

        static string RoutingPath =>
            Path.Combine(UCL_AgentCommandsPath.DataRoot, "ChatTavern", "discord_channel_routing.json");

        static List<InboundRoute> GetRoutes()
        {
            try
            {
                string path = RoutingPath;
                if (!File.Exists(path)) { s_Routes.Clear(); s_RoutingMtime = -1; return s_Routes; }
                long mtime = new FileInfo(path).LastWriteTimeUtc.Ticks;
                if (mtime == s_RoutingMtime) return s_Routes;
                s_RoutingMtime = mtime;
                var list = new List<InboundRoute>();
                var jd = JsonData.ParseJson(File.ReadAllText(path));
                if (jd != null && jd.Contains("mappings") && jd["mappings"].IsArray)
                {
                    var maps = jd["mappings"];
                    for (int i = 0; i < maps.Count; i++)
                    {
                        var m = maps[i];
                        if (m == null || !m.GetBool("enabled", false)) continue;
                        string cid = m.GetString("channel_id", "");
                        string room = m.GetString("tavern_room", "");
                        if (string.IsNullOrEmpty(cid) || string.IsNullOrEmpty(room)) continue;
                        list.Add(new InboundRoute
                        {
                            channelId = cid,
                            tavernRoom = room,
                            label = m.GetString("label", ""),
                            sourceClass = m.GetString("source_class", "external"),
                            priority = m.GetInt("priority", 0),
                            guildId = m.GetString("guild_id", ""),
                        });
                    }
                }
                s_Routes = list;
            }
            catch (Exception e)
            {
                LastError = $"routing 讀取失敗: {e.Message}";
                Debug.LogWarning($"[DiscordInbound] {LastError}");
            }
            return s_Routes;
        }

        /// <summary>當前啟用的路由數（AdminPage 顯示用）。</summary>
        public static int ActiveRouteCount => GetRoutes().Count;

        // ===========================================================
        // 區塊：bot token 解析（ENV > _secrets/discord_bot_token.txt）
        // 物理意義：明文 .txt 由 UCL_SecretManagerPage 以 passphrase 從 .enc 安裝出來（gitignored）。
        //          與舊 python bot 讀同一份檔 → 遷移期兩者可並存（但別同時開，會雙寫）。
        // 數值影響：不快取內容於磁碟、不 log token；只在缺席時警告一次。
        // ===========================================================
        static string ResolveToken()
        {
            try
            {
                string env = Environment.GetEnvironmentVariable("DISCORD_INBOUND_BOT_TOKEN");
                if (!string.IsNullOrEmpty(env)) return env.Trim();
                string p = Path.Combine(UCL_AgentCommandsPath.DataRoot, "_secrets", TokenFileName);
                if (File.Exists(p))
                {
                    string v = File.ReadAllText(p).Trim();
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch (Exception e) { LastError = $"token 讀取失敗: {e.Message}"; }
            return "";
        }

        /// <summary>token 是否就緒（AdminPage 顯示用；不回傳內容）。</summary>
        public static bool HasToken => !string.IsNullOrEmpty(ResolveToken());

        /// <summary>
        /// 建立 List Guild Members REST request（供 Discord Settings page 匯入候選成員）。
        /// token 留在 daemon 內解析，呼叫端只負責送出與 dispose request，絕不接觸明文字串。
        /// </summary>
        public static bool TryCreateGuildMembersRequest(string guildId, string afterUserId,
            out UnityWebRequest request, out string error)
        {
            request = null;
            error = "";
            if (!ulong.TryParse(guildId, out _))
            {
                error = "Guild ID 必須是正整數 snowflake";
                return false;
            }
            string token = ResolveToken();
            if (string.IsNullOrEmpty(token))
            {
                error = "bot token 未就緒，請先以 Secret Manager 安裝";
                return false;
            }
            string suffix = string.IsNullOrEmpty(afterUserId) ? "" : "&after=" + afterUserId;
            request = UnityWebRequest.Get($"{API_BASE}/guilds/{guildId}/members?limit=1000{suffix}");
            request.timeout = REQUEST_TIMEOUT_SEC;
            request.SetRequestHeader("Authorization", "Bot " + token);
            request.SetRequestHeader("User-Agent", USER_AGENT);
            return true;
        }

        /// <summary>
        /// 建立單一 Discord channel 的 REST 驗證 request，供 routing 管理頁確認 channel ID 並取得顯示名稱。
        /// token 僅在 daemon 內解析；管理頁不接觸憑據。
        /// </summary>
        public static bool TryCreateChannelRequest(string channelId, out UnityWebRequest request, out string error)
        {
            request = null;
            error = "";
            if (!ulong.TryParse(channelId, out _))
            {
                error = "Channel ID 必須是正整數 snowflake";
                return false;
            }
            string token = ResolveToken();
            if (string.IsNullOrEmpty(token))
            {
                error = "bot token 未就緒，請先以 Secret Manager 安裝";
                return false;
            }
            request = UnityWebRequest.Get($"{API_BASE}/channels/{channelId}");
            request.timeout = REQUEST_TIMEOUT_SEC;
            request.SetRequestHeader("Authorization", "Bot " + token);
            request.SetRequestHeader("User-Agent", USER_AGENT);
            return true;
        }

        // ===========================================================
        // 區塊：游標 state（per-channel last_message_id）讀寫
        // 物理意義：Discord snowflake id 單調遞增 → `?after=<id>` 天然就是「比這筆新的」語意，
        //          比 ts 比較更可靠（同 ms 多筆也有序）。存共用 canonical `_tavern_state.json`，
        //          走 read-modify-write 只動 `inbound` 子樹（別的欄位原樣保留）。
        // 數值影響：每次成功中繼一批後寫一次（非每筆）→ 中途 Editor 掛掉最多重放該批（可見重送非隱形漏）。
        // ===========================================================
        static string StatePath => Path.Combine(UCL_RepoPath.AgentCommandsDir, "PromptQueue", "_tavern_state.json");

        static string LoadCursor(string channelId)
        {
            try
            {
                if (!File.Exists(StatePath)) return "";
                var jd = JsonData.ParseJson(File.ReadAllText(StatePath));
                if (jd == null || !jd.Contains("inbound")) return "";
                var ib = jd["inbound"];
                if (ib == null || !ib.Contains("channels")) return "";
                var ch = ib["channels"];
                if (ch == null || !ch.Contains(channelId)) return "";
                return ch[channelId].GetString("last_message_id", "");
            }
            catch { return ""; }
        }

        static void SaveCursor(string channelId, string lastMessageId)
        {
            try
            {
                var state = File.Exists(StatePath)
                    ? JsonData.ParseJson(File.ReadAllText(StatePath))
                    : JsonData.ParseJson("{}");
                if (state == null) return;
                if (!state.Contains("inbound")) state["inbound"] = JsonData.ParseJson("{}");
                var ib = state["inbound"];
                if (!ib.Contains("channels")) ib["channels"] = JsonData.ParseJson("{}");
                var ch = ib["channels"];
                if (!ch.Contains(channelId)) ch[channelId] = JsonData.ParseJson("{}");
                ch[channelId]["last_message_id"] = new JsonData(lastMessageId ?? "");
                ch[channelId]["updated_at"] = new JsonData(UtcNowIso());
                AtomicWrite(StatePath, state.ToJsonBeautify());
            }
            catch (Exception e)
            {
                LastError = $"游標寫入失敗: {e.Message}";
                Debug.LogWarning($"[DiscordInbound] {LastError}");
            }
        }

        static void AtomicWrite(string path, string content)
        {
            string tmp = path + ".inbound.tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        static string UtcNowIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
            System.Globalization.CultureInfo.InvariantCulture);

        // ===========================================================
        // 區塊：發出下一個輪詢（round-robin 選 channel）
        // 物理意義：無游標的頻道先走 baseline 探測（limit=1 取最新一筆當起點、**不轉發**）→
        //          避免首次啟用就把整個頻道歷史灌進酒館（對齊 outbound「首見不回放」慣例）。
        // ===========================================================
        static void StartNextPoll()
        {
            var routes = GetRoutes();
            if (routes.Count == 0) return;

            string token = ResolveToken();
            if (string.IsNullOrEmpty(token))
            {
                if (!s_TokenMissingLogged)
                {
                    s_TokenMissingLogged = true;
                    LastError = $"bot token 未就緒（_secrets/{TokenFileName} 缺席或空）— 請走 Secret Manager 安裝";
                    Debug.LogWarning($"[DiscordInbound] {LastError}");
                }
                return;
            }

            if (s_RoundRobinIdx >= routes.Count) s_RoundRobinIdx = 0;
            var route = routes[s_RoundRobinIdx];
            s_RoundRobinIdx++;

            string cursor = LoadCursor(route.channelId);
            bool baseline = string.IsNullOrEmpty(cursor);
            string url = baseline
                ? $"{API_BASE}/channels/{route.channelId}/messages?limit=1"
                : $"{API_BASE}/channels/{route.channelId}/messages?after={cursor}&limit={FETCH_LIMIT}";

            var req = new UnityWebRequest(url, "GET");
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", "Bot " + token);
            req.SetRequestHeader("User-Agent", USER_AGENT);
            req.timeout = REQUEST_TIMEOUT_SEC;
            req.SendWebRequest();

            s_InFlight = req;
            s_InFlightRoute = route;
            s_InFlightIsBaseline = baseline;
        }

        // ===========================================================
        // 區塊：判讀已完成的請求 → 中繼進酒館 → 推進游標
        // 物理意義：Discord 回頂層 JSON 陣列（新→舊）。中繼順序必須反轉成「舊→新」，否則酒館內
        //          時序顛倒。游標推進到本批最大 id（= 陣列第一筆，因為 Discord 由新到舊排）。
        // 數值影響：任何 HTTP 非 2xx → 不推進游標（下輪重試，可見非隱形漏）+ 記 LastError。
        // ===========================================================
        static void DrainInFlight()
        {
            var req = s_InFlight;
            var route = s_InFlightRoute;
            bool baseline = s_InFlightIsBaseline;
            try
            {
                if (req == null || route == null) return;
                long code = req.responseCode;
                if (req.result != UnityWebRequest.Result.Success || code < 200 || code >= 300)
                {
                    // 401/403 = token 或頻道權限問題（要人介入）；429 = rate limit（下輪自然重試）
                    LastError = $"GET channel {route.channelId} 失敗（HTTP {code}）: {req.error}";
                    Debug.LogWarning($"[DiscordInbound] {LastError}"
                        + (code == 401 || code == 403 ? " ← 檢查 bot token 與該頻道的讀取權限（需 View Channel + Read Message History）" : ""));
                    return;
                }

                LastPollUtc = UtcNowIso();
                LastError = "";
                string body = req.downloadHandler.text ?? "";
                // 包一層物件再 parse — 不依賴 parser 對「頂層陣列」的處理細節
                var wrapped = JsonData.ParseJson("{\"items\":" + (string.IsNullOrEmpty(body) ? "[]" : body) + "}");
                if (wrapped == null || !wrapped.Contains("items") || !wrapped["items"].IsArray) return;
                var items = wrapped["items"];
                if (items.Count == 0) return;

                // Discord 由新到舊 → 第 0 筆是最新；游標一律推進到最新 id
                string newestId = items[0].GetString("id", "");

                if (baseline)
                {
                    // 首見該頻道：只記起點、不轉發任何歷史訊息
                    if (!string.IsNullOrEmpty(newestId))
                    {
                        SaveCursor(route.channelId, newestId);
                        Debug.Log($"[DiscordInbound] baseline 建立 — channel {route.channelId}"
                                  + $"（{route.label}）游標起點 msg={newestId}，歷史不回放");
                    }
                    return;
                }

                // 反轉成舊→新後逐筆中繼
                // 區塊職責：略過原因必須逐筆可見（禁靜默失敗）
                // 物理意義：「略過」有兩種天差地別的成因 —— ① bot/webhook 訊息（防迴圈，正確行為）
                //          ② content 為空（可能是 MESSAGE_CONTENT privileged intent 未開 → REST 不回 content，
                //          那會讓**所有**真人訊息都被當空訊息吞掉 = 整條 inbound 靜默失效）。
                //          兩者都印成 `略過(<原因>)` 才分得出「運作正常」與「整條壞掉」。
                int relayed = 0;
                var skipReasons = new List<string>();
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    string reason = RelayOne(items[i], route);
                    if (reason == null) relayed++; else skipReasons.Add(reason);
                }
                int skipped = skipReasons.Count;
                if (!string.IsNullOrEmpty(newestId)) SaveCursor(route.channelId, newestId);
                RelayedThisSession += relayed;
                if (relayed > 0 || skipped > 0)
                {
                    string reasonSummary = "";
                    if (skipped > 0)
                    {
                        var counts = new Dictionary<string, int>();
                        foreach (var r in skipReasons) counts[r] = counts.TryGetValue(r, out int c) ? c + 1 : 1;
                        var parts = new List<string>();
                        foreach (var kv in counts) parts.Add($"{kv.Key}×{kv.Value}");
                        reasonSummary = "［" + string.Join(", ", parts) + "］";
                    }
                    Debug.Log($"[DiscordInbound] {route.label}({route.channelId}) → room={route.tavernRoom}："
                              + $"中繼 {relayed} 筆、略過 {skipped} 筆{reasonSummary}（游標 → {newestId}）");
                }
            }
            catch (Exception e)
            {
                LastError = $"判讀失敗: {e.Message}";
                Debug.LogWarning($"[DiscordInbound] {LastError}");
            }
            finally
            {
                DisposeInFlight();
            }
        }

        static void DisposeInFlight()
        {
            try { s_InFlight?.Dispose(); } catch { }
            s_InFlight = null;
            s_InFlightRoute = null;
            s_InFlightIsBaseline = false;
        }

        // ===========================================================
        // 區塊：單筆 Discord message → 酒館訊息
        // 物理意義：對齊舊 python bot 的欄位語意（sender_id=discord:<uid>、meta.source=discord 防迴圈、
        //          source_class / priority 給 waiter cycle 排序），讓下游消費者（waiter / mirror 排除）零改動。
        // 數值影響：回 null = 已寫入酒館；回字串 = 略過原因（診斷用，會進 log 統計）。
        //          寫入走 in-process AppendMessage（單寫者），**不 spawn 任何 process**。
        // ===========================================================
        static string RelayOne(JsonData msg, InboundRoute route)
        {
            try
            {
                if (msg == null || !msg.IsObject) return "malformed";

                var author = msg.Contains("author") ? msg["author"] : null;
                if (author == null) return "no-author";
                // 跳過所有 bot 發的訊息 — 含本 bot 自己與 outbound webhook 推回來的鏡像（防迴圈第一道）
                if (author.GetBool("bot", false)) return "bot-author";
                // webhook 訊息帶 webhook_id（即使 author.bot 缺席也擋掉）
                if (msg.Contains("webhook_id") && !string.IsNullOrEmpty(msg.GetString("webhook_id", ""))) return "webhook-msg";

                string msgId = msg.GetString("id", "");
                string content = (msg.GetString("content", "") ?? "").Trim();

                string uid = author.GetString("id", "");
                if (string.IsNullOrEmpty(uid)) return "no-author-id";
                if (!TryGetWhitelistedUserInfo(uid, out string approvedDisplayName, out string approvedProfile)) return "not-whitelisted";

                // 附件：下載落地 + refs 關聯本地路徑（對齊舊 python bot 慣例，見 DownloadAttachments）
                var refs = new List<UCL_ChatRef>();
                var attachmentNames = new List<string>();
                if (msg.Contains("attachments") && msg["attachments"].IsArray)
                    DownloadAttachments(msg["attachments"], msgId, refs, attachmentNames);
                // ⚠ content 空且無附件 → 略過。若**真人訊息**大量出現此原因，極可能是 Discord Developer Portal
                //   的 MESSAGE CONTENT INTENT 未開 → REST 回的 content 一律空字串（不是本 daemon 的 bug）。
                if (string.IsNullOrEmpty(content) && attachmentNames.Count == 0)
                    return msg.Contains("content") ? "empty-content(檢查 MESSAGE_CONTENT intent)" : "no-content-field";
                // body 空（純圖訊息）→ 附件清單當 placeholder（對齊 python _build_attachment_body_hint）；
                // 有 body 時不把附件塞進 body，靠 refs 關聯即可（不污染對話內容）
                if (string.IsNullOrEmpty(content))
                    content = $"[Discord 附件 {attachmentNames.Count} 個] " + string.Join(", ", attachmentNames);

                // 顯示名優先序：guild 暱稱（member.nick）> global_name > username > uid。
                // 白名單列若填 display_name，則以該列為明示的實名覆寫；未填仍保留 Discord 自己的帳號區別。
                string display = "";
                var member = msg.Contains("member") ? msg["member"] : null;
                if (member != null) display = member.GetString("nick", "");
                if (string.IsNullOrEmpty(display)) display = author.GetString("global_name", "");
                if (string.IsNullOrEmpty(display)) display = author.GetString("username", "");
                if (string.IsNullOrEmpty(display)) display = uid;
                if (!string.IsNullOrEmpty(approvedDisplayName)) display = approvedDisplayName;

                string senderId = "discord:" + uid;
                // identity 先 ensure — Cmd_Tavern / 渲染端靠 identities.json 找 display_name，
                // 沒註冊會退回顯示 "discord:<uid>"（不好看）
                // kind 用 "discord-user" —— identities.json 內既有 18 筆 Discord 來源身分（python 時代註冊）
                // 全是這個值，寫 "human" 會造成同源身分兩種 kind 的 schema 漂移（2026-07-28 實測抓到）。
                try { UCL_ChatTavernIO.GetOrCreateIdentity(senderId, display, "discord-user"); }
                catch (Exception e) { Debug.LogWarning($"[DiscordInbound] identity ensure 失敗（繼續中繼）: {e.Message}"); }

                var meta = new Dictionary<string, string>
                {
                    // 防迴圈雙標記：outbound mirror 認 meta.source=discord 跳過，不把訊息推回 Discord
                    { "source", "discord" },
                    { "discord_msg_id", msgId },
                    { "discord_channel_id", route.channelId },
                    { "source_class", route.sourceClass },
                    { "priority", route.priority.ToString() },
                    { "relay", "native" },   // 區分 C# native 中繼 vs 舊 python bot（遷移期辨識用）
                };
                if (!string.IsNullOrEmpty(route.guildId)) meta["discord_guild_id"] = route.guildId;
                if (!string.IsNullOrEmpty(route.label)) meta["channel_label"] = route.label;
                if (!string.IsNullOrEmpty(approvedProfile)) meta["discord_user_profile"] = approvedProfile;

                if (refs.Count > 0) meta["attachments"] = refs.Count.ToString();

                // CLI 指令判定（寫入層攔截，Tim 2026-08-20 拍板）：
                // Discord 進來的訊息若是給酒保 CLI 的指令（`cmd …`），在寫入端就打上 tag，
                // 讓後續流程（glossary / mention / mirror 等）能分流 —— 判定跟 Cmd_Tavern post 同一支，
                // 兩個寫入口用同一個判準，不各自發明。
                try
                {
                    if (Bartender.UCL_BartenderCliService.LooksLikeCliCommand(content))
                    {
                        meta["tag"] = "cli-cmd";
                        meta["cli_cmd"] = "true";
                    }
                }
                catch (Exception e) { Debug.LogWarning($"[DiscordInbound] CLI 指令判定失敗（視同一般訊息）: {e.Message}"); }

                var record = new UCL_ChatMessage
                {
                    sender_id = senderId,
                    sender_name = display,
                    kind = "chat",
                    body = content,
                    meta = meta,
                    // refs 帶「repo 相對本地路徑」→ agent 讀完訊息可直接 Read 該圖（本功能的目的）
                    refs = refs.Count > 0 ? refs : null,
                };
                UCL_ChatTavernIO.AppendMessage(route.tavernRoom, record);
                return null;   // null = 成功中繼
            }
            catch (Exception e)
            {
                LastError = $"單筆中繼失敗: {e.Message}";
                Debug.LogWarning($"[DiscordInbound] {LastError}");
                return "exception";
            }
        }

        // ===========================================================
        // 區塊：白名單讀取與判定（config mtime 快取）
        // 物理意義：AdminPage 存檔後，下一筆 inbound 自動吃到新名單；不須重啟 daemon 或重新連 Gateway。
        // 數值影響：每次檔案 mtime 改變才重新 parse；設定檔讀壞時 fail-closed（原先已啟用白名單仍不放行陌生帳號）。
        // ===========================================================
        static string NotifyConfigPath => Path.Combine(UCL_AgentCommandsPath.DataRoot, "PromptQueue", NotifyConfigFileName);

        static bool TryGetWhitelistedUserInfo(string userId, out string displayName, out string profile)
        {
            displayName = "";
            profile = "";
            RefreshWhitelistIfNeeded();
            if (!s_WhitelistEnabled) return true;
            if (!s_WhitelistedUserIds.Contains(userId)) return false;
            s_WhitelistedDisplayNames.TryGetValue(userId, out displayName);
            s_WhitelistedProfiles.TryGetValue(userId, out profile);
            return true;
        }

        static void RefreshWhitelistIfNeeded()
        {
            try
            {
                string path = NotifyConfigPath;
                if (!File.Exists(path))
                {
                    // 初始沒有設定檔 = 舊專案相容的「未啟用」；但運行中檔案消失不可把已啟用門禁打開。
                    if (s_WhitelistConfigMtime >= 0)
                    {
                        LastError = "白名單設定檔在運行中消失；保留上一份 allowlist（fail-closed）";
                        Debug.LogWarning($"[DiscordInbound] {LastError}");
                    }
                    return;
                }
                long mtime = new FileInfo(path).LastWriteTimeUtc.Ticks;
                if (mtime == s_WhitelistConfigMtime) return;

                // 先完成新快取再交換；讀檔失敗不能把既有 allowlist 清空成意外放行。
                var nextIds = new HashSet<string>(StringComparer.Ordinal);
                var nextNames = new Dictionary<string, string>(StringComparer.Ordinal);
                var nextProfiles = new Dictionary<string, string>(StringComparer.Ordinal);
                bool nextEnabled = false;
                if (mtime >= 0)
                {
                    var config = JsonData.ParseJson(File.ReadAllText(path));
                    var inbound = config != null && config.Contains(KeyTavernInbound) ? config[KeyTavernInbound] : null;
                    var whitelist = inbound != null && inbound.Contains(KeyUserWhitelist) ? inbound[KeyUserWhitelist] : null;
                    if (whitelist != null)
                    {
                        nextEnabled = whitelist.GetBool(KeyWhitelistEnabled, false);
                        var users = whitelist.Contains(KeyWhitelistUsers) ? whitelist[KeyWhitelistUsers] : null;
                        if (users != null && users.IsArray)
                        {
                            for (int i = 0; i < users.Count; i++)
                            {
                                var user = users[i];
                                string id = user?.GetString(KeyUserId, "") ?? "";
                                if (string.IsNullOrEmpty(id)) continue;
                                nextIds.Add(id);
                                string name = user.GetString(KeyDisplayName, "");
                                if (!string.IsNullOrWhiteSpace(name)) nextNames[id] = name.Trim();
                                string profile = user.GetString(KeyProfile, "");
                                if (!string.IsNullOrWhiteSpace(profile)) nextProfiles[id] = profile.Trim();
                            }
                        }
                    }
                }

                s_WhitelistedUserIds.Clear();
                s_WhitelistedUserIds.UnionWith(nextIds);
                s_WhitelistedDisplayNames.Clear();
                foreach (var pair in nextNames) s_WhitelistedDisplayNames[pair.Key] = pair.Value;
                s_WhitelistedProfiles.Clear();
                foreach (var pair in nextProfiles) s_WhitelistedProfiles[pair.Key] = pair.Value;
                s_WhitelistEnabled = nextEnabled;
                s_WhitelistConfigMtime = mtime;
            }
            catch (Exception e)
            {
                LastError = $"白名單讀取失敗: {e.Message}";
                Debug.LogWarning($"[DiscordInbound] {LastError}");
            }
        }

        // ===========================================================
        // 區塊：附件下載落地（對齊舊 python bot `_download_attachments` 的存放慣例）
        // 物理意義：Discord 附件 URL 帶簽章、會過期 → 必須落地保存，否則事後 agent 讀訊息點不開圖。
        //          存放位置 <DataRoot>/ChatTavern/media/discord/<yyyy-MM-dd>/<msgId>__<attId>__<safeName>
        //          （與 python 版同路徑同檔名格式 → 新舊資料混放不衝突、既有工具鏈不必改）。
        //          refs[].path 記「repo 相對路徑 + forward slash」= 酒館 refs 慣例，agent 可直接 Read 開圖。
        // 數值影響：同步下載（忙等到 isDone），單檔上限 MAX_ATTACHMENT_BYTES、等待上限
        //          MAX_ATTACHMENT_WAIT_SEC；任一附件失敗只 log warning，不擋其他附件、不擋訊息寫入
        //          （fail-soft，對齊 python 版：附件掛掉不能讓對話內容消失）。
        // 設計取捨：刻意用忙等而非非同步佇列 —— 附件通常 0~2 件、單件數百 KB，且 RelayOne 必須在回傳前
        //          把 refs 湊齊才能「一次寫檔」（維持單寫者不分兩段寫）。主緒短暫卡頓以等待上限封頂。
        // ===========================================================
        const int MAX_ATTACHMENT_BYTES = 24 * 1024 * 1024;   // 24MB — Discord 免費上限 25MB，留餘裕
        const double MAX_ATTACHMENT_WAIT_SEC = 30.0;
        static readonly char[] s_UnsafeFileChars = { '<', '>', ':', '\"', '/', '\\', '|', '?', '*', '=', ' ' };

        /// <summary>Discord attachment.filename → 本地安全檔名（對齊 python _sanitize_attachment_filename）。</summary>
        static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(c < 0x20 || Array.IndexOf(s_UnsafeFileChars, c) >= 0 ? '_' : c);
            string trimmed = sb.ToString().TrimStart('-', '.', '_');
            return string.IsNullOrEmpty(trimmed) ? "unnamed" : trimmed;
        }

        static void DownloadAttachments(JsonData atts, string msgId, List<UCL_ChatRef> refs, List<string> names)
        {
            string dateStr = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            string saveDir = Path.Combine(UCL_AgentCommandsPath.DataRoot, "ChatTavern", "media", "discord", dateStr);
            try { Directory.CreateDirectory(saveDir); }
            catch (Exception e)
            {
                Debug.LogWarning($"[DiscordInbound] 附件目錄建立失敗（附件跳過，訊息照寫）: {e.Message}");
                return;
            }

            for (int i = 0; i < atts.Count; i++)
            {
                var att = atts[i];
                if (att == null) continue;
                string fn = att.GetString("filename", "");
                string url = att.GetString("url", "");
                string attId = att.GetString("id", i.ToString());
                int size = att.GetInt("size", 0);
                string contentType = att.GetString("content_type", "");
                if (!string.IsNullOrEmpty(fn)) names.Add(fn);
                if (string.IsNullOrEmpty(url)) continue;
                if (size > MAX_ATTACHMENT_BYTES)
                {
                    Debug.LogWarning($"[DiscordInbound] 附件過大跳過下載（{size} bytes 超上限）: {fn}");
                    continue;
                }

                string safeName = SanitizeFileName(string.IsNullOrEmpty(fn) ? $"att_{attId}" : fn);
                string localPath = Path.Combine(saveDir, $"{msgId}__{attId}__{safeName}");
                try
                {
                    if (!File.Exists(localPath))   // 已存在（同筆重放）→ 不重抓
                    {
                        byte[] bytes = HttpGetBytes(url);
                        if (bytes == null || bytes.Length == 0)
                        {
                            Debug.LogWarning($"[DiscordInbound] 附件下載失敗（略過，訊息照寫）: {fn}");
                            continue;
                        }
                        File.WriteAllBytes(localPath, bytes);
                    }
                    string rel = MakeRepoRelative(localPath);
                    refs.Add(new UCL_ChatRef
                    {
                        path = rel,
                        label = string.IsNullOrEmpty(contentType) ? fn : $"{fn} ({contentType})",
                    });
                    Debug.Log($"[DiscordInbound]   附件已存: {fn} ({size}B) -> {rel}");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[DiscordInbound] 附件處理失敗（略過）{fn}: {e.Message}");
                }
            }
        }

        /// <summary>同步 GET bytes（忙等到 isDone / 逾時）。失敗回 null。</summary>
        static byte[] HttpGetBytes(string url)
        {
            UnityWebRequest req = null;
            try
            {
                req = UnityWebRequest.Get(url);
                req.timeout = REQUEST_TIMEOUT_SEC;
                req.SetRequestHeader("User-Agent", USER_AGENT);
                var op = req.SendWebRequest();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!op.isDone)
                {
                    if (sw.Elapsed.TotalSeconds > MAX_ATTACHMENT_WAIT_SEC) return null;
                    System.Threading.Thread.Sleep(20);   // 忙等但讓出 CPU（件數少，可接受）
                }
                if (req.result != UnityWebRequest.Result.Success) return null;
                return req.downloadHandler?.data;
            }
            catch { return null; }
            finally { try { req?.Dispose(); } catch { } }
        }

        /// <summary>絕對路徑 → repo 相對 + forward slash（酒館 refs 慣例）。不在 repo 內則原樣回。</summary>
        static string MakeRepoRelative(string absPath)
        {
            try
            {
                string root = UCL_RepoPath.RepoRoot.Replace('\\', '/').TrimEnd('/');
                string full = Path.GetFullPath(absPath).Replace('\\', '/');
                if (full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                    return full.Substring(root.Length + 1);
                return full;
            }
            catch { return absPath; }
        }

        // ===========================================================
        // 區塊：診斷 hook — 給 Cmd_Invoke reflection 驗「真實 runtime 狀態」用
        // 物理意義：compile 0 error 只證語法對，不證接線對。本方法把兩道閘門 / token / 路由 / 游標
        //          一次吐成字串，經 `run_cmd.py run Invoke --arg member=SelfTest` 取得 → Editor.log
        //          的 `[AgentCmd:Invoke] OK (System.String) = ...` 才是 ground truth。
        // 數值影響：純唯讀（不發請求、不寫檔）；token 只報「就緒/缺席」絕不吐內容。
        // ===========================================================
        public static string SelfTest()
        {
            var sb = new StringBuilder();
            sb.Append($"Enabled(pref)={Enabled}; ConfigEnabled={ConfigEnabled}; IsLive={IsLive}; ");
            sb.Append($"[gateway] {UCL_DiscordGatewayClient.SelfTest()} || ");
            sb.Append($"HasToken={HasToken}; RelayedThisSession={RelayedThisSession}; ");
            sb.Append($"LastPollUtc={(string.IsNullOrEmpty(LastPollUtc) ? "(none)" : LastPollUtc)}; ");
            sb.Append($"LastError={(string.IsNullOrEmpty(LastError) ? "(none)" : LastError)}; ");
            var routes = GetRoutes();
            sb.Append($"routes={routes.Count}");
            foreach (var r in routes)
            {
                string cur = LoadCursor(r.channelId);
                sb.Append($" | ch={r.channelId}({r.label})→{r.tavernRoom} [{r.sourceClass}/p{r.priority}] "
                          + $"cursor={(string.IsNullOrEmpty(cur) ? "(baseline pending)" : cur)}");
            }
            return sb.ToString();
        }

        // TODO(backlog)：① 附件下載落地 — 2026-07-28 已完成（見 DownloadAttachments）
        //                ② @mention 反向重寫（Discord <@uid> → 酒館 @persona，對偶 outbound 的 rewrite）
        //                ③ reply_to 對應（Discord message_reference → reply_to_uuid）
        //                ④ gateway 連線（讓 Discord 端 bot 顯示上線 + 即時推送；Tim 2026-07-28 拍板要做）
    }
}
#endif
