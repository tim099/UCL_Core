// 區塊職責：Discord Gateway (WebSocket) 協議客戶端 — 維持連線讓 bot 在 Discord 端「顯示上線」，
//            並即時接收 MESSAGE_CREATE 推送。
// 物理意義：bot 的上線綠點是 **gateway 專屬功能** — Discord 只在 bot 有活躍 gateway session（IDENTIFY
//          成功 + 持續心跳）時才標它上線。REST 輪詢再勤也永遠顯示離線，這是協議層的事實，不是 bug。
//          故本檔存在的第一目的是 presence（Tim 2026-07-28 要求「開啟 inbound 時 bot 上線、關閉後下線」），
//          第二目的才是即時推送（既然連線都在了，順便省掉輪詢延遲）。
// 數值影響：一條 WebSocket + 每 heartbeat_interval（Discord 給，通常 41.25s）一次心跳 ≈ 零成本。
//          斷線指數退避重連（1→2→4→…→30s 上限），RESUME 成功可補回斷線期間漏掉的事件。
// 執行緒模型（重要）：WebSocket 收訊跑在背景 Task；收到的 dispatch payload 丟進 ConcurrentQueue，
//          **一律由 Editor 主緒 drain 後才寫檔** — 維持「酒館寫入是單寫者、且在主緒」這個既有不變量，
//          不讓 file IO 散到背景緒（2026-07-28 併發事故的教訓：寫入路徑必須收斂成一條）。
#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// Discord Gateway v10 客戶端（Editor-only）。負責 presence（bot 上線顯示）+ MESSAGE_CREATE 即時推送。
    /// 用法：<see cref="Start"/> 起連線 → 主緒每 tick 呼 <see cref="TryDequeueDispatch"/> 取事件 → <see cref="Stop"/> 斷線。
    /// </summary>
    public static class UCL_DiscordGatewayClient
    {
        // 區塊職責：協議常數
        // 物理意義：intents 是「要訂閱哪些事件」的 bitmask；MESSAGE_CONTENT (1<<15) 是 **privileged intent**，
        //          必須在 Discord Developer Portal 手動開啟，否則 IDENTIFY 會被以 close code 4014 踢掉。
        //          GUILD_MEMBERS (1<<1) 讓 Discord Settings 可列 guild 成員，亦是 REST List Guild Members 的必要權限。
        //          兩者都必須在 Discord Developer Portal 手動開啟，否則 IDENTIFY 會被以 close code 4014 踢掉。
        // 數值影響：GUILDS(1) + GUILD_MEMBERS(2) + GUILD_MESSAGES(512) + MESSAGE_CONTENT(32768) = 33283。
        const int INTENT_GUILDS = 1 << 0;
        const int INTENT_GUILD_MEMBERS = 1 << 1;
        const int INTENT_GUILD_MESSAGES = 1 << 9;
        const int INTENT_MESSAGE_CONTENT = 1 << 15;
        const int INTENTS = INTENT_GUILDS | INTENT_GUILD_MEMBERS | INTENT_GUILD_MESSAGES | INTENT_MESSAGE_CONTENT;

        const string GATEWAY_URL = "wss://gateway.discord.gg/?v=10&encoding=json";
        const int RECEIVE_BUFFER_SIZE = 16 * 1024;
        const int MAX_MESSAGE_BYTES = 8 * 1024 * 1024;    // 單一 gateway frame 組裝上限（防惡意/異常巨量）
        const double RECONNECT_BASE_SEC = 1.0;
        const double RECONNECT_MAX_SEC = 30.0;

        // ===========================================================
        // 區塊：對外狀態（主緒讀；背景緒寫 → 用 volatile / Interlocked 保可見性）
        // ===========================================================
        static volatile bool s_Running;          // Start 之後、Stop 之前為 true（含重連中）
        static volatile bool s_Identified;       // 收到 READY = Discord 已認證 → bot 顯示上線
        static volatile string s_LastError = "";
        static volatile string s_SessionId = "";
        static volatile string s_BotUser = "";   // READY 帶回的 bot username（診斷用）
        static volatile int s_ReconnectCount;
        static long s_LastEventUnixMs;           // 最近收到任何 gateway 事件的時刻（Interlocked）

        static CancellationTokenSource s_Cts;
        static Task s_LoopTask;
        static readonly ConcurrentQueue<string> s_DispatchQueue = new ConcurrentQueue<string>();

        /// <summary>是否已認證（收到 READY）— 等價於「Discord 端 bot 顯示上線」。</summary>
        public static bool IsOnline => s_Running && s_Identified;
        /// <summary>Start 已呼叫且未 Stop（含斷線重連中）。</summary>
        public static bool IsRunning => s_Running;
        public static string LastError => s_LastError ?? "";
        public static string BotUser => s_BotUser ?? "";
        public static int ReconnectCount => s_ReconnectCount;
        /// <summary>待主緒處理的 MESSAGE_CREATE 事件數（積壓觀測用）。</summary>
        public static int PendingDispatchCount => s_DispatchQueue.Count;

        /// <summary>最近一次收到 gateway 事件距今秒數；-1 = 尚未收到任何事件。</summary>
        public static double SecondsSinceLastEvent
        {
            get
            {
                long t = Interlocked.Read(ref s_LastEventUnixMs);
                if (t <= 0) return -1;
                return (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - t) / 1000.0;
            }
        }

        // ===========================================================
        // 區塊：啟動 / 停止
        // 物理意義：Start 起一條背景 Task 跑「連線→IDENTIFY→收訊+心跳」迴圈，斷線自動退避重連；
        //          Stop 取消 token 並主動送 WebSocket Close → Discord 立刻把 bot 標離線
        //          （不主動關的話要等心跳逾時，綠點會殘留數十秒）。
        // 數值影響：重複 Start 是 no-op（已在跑就不重連）；Stop 不阻塞主緒等背景收尾。
        // ===========================================================
        public static void Start(string token)
        {
            if (s_Running) return;
            if (string.IsNullOrEmpty(token))
            {
                s_LastError = "token 為空 — 無法連 gateway";
                return;
            }
            s_Running = true;
            s_Identified = false;
            s_LastError = "";
            s_ReconnectCount = 0;
            s_Cts = new CancellationTokenSource();
            var ct = s_Cts.Token;
            string tk = token;
            s_LoopTask = Task.Run(() => RunLoopAsync(tk, ct));
            Debug.Log("[DiscordGateway] 連線啟動中…（bot 上線需 IDENTIFY 成功，通常 1-3 秒）");
        }

        public static void Stop()
        {
            if (!s_Running) return;
            s_Running = false;
            s_Identified = false;
            try { s_Cts?.Cancel(); } catch { }
            // 不 join 背景 Task（避免卡主緒）；WebSocket 的 Close 在迴圈的 finally 內送出
            Debug.Log("[DiscordGateway] 已要求斷線 → Discord 端 bot 轉離線");
        }

        /// <summary>主緒取一筆待處理的 MESSAGE_CREATE payload JSON（回 false = 佇列空）。</summary>
        public static bool TryDequeueDispatch(out string payloadJson) => s_DispatchQueue.TryDequeue(out payloadJson);

        // ===========================================================
        // 區塊：主迴圈 — 連線 / IDENTIFY(或 RESUME) / 收訊 / 心跳 / 退避重連
        // 物理意義：Discord gateway 生命週期 = HELLO(op10) 給心跳間隔 → IDENTIFY(op2) 或 RESUME(op6)
        //          → READY/RESUMED → 之後持續收 dispatch(op0) 並定期送 HEARTBEAT(op1) 換 ACK(op11)。
        //          op7 RECONNECT / op9 INVALID_SESSION / 任何斷線 → 退避後重連（可 RESUME 就 RESUME）。
        // 數值影響：能 RESUME 時 Discord 會補送斷線期間的事件（不漏訊息）；不能 RESUME 就重新 IDENTIFY
        //          （該段期間的訊息由 daemon 的 REST 追平輪詢補回 — 這是保留 REST 安全網的理由）。
        // ===========================================================
        static async Task RunLoopAsync(string token, CancellationToken ct)
        {
            double backoff = RECONNECT_BASE_SEC;
            string resumeUrl = null;
            int lastSeq = 0;

            while (!ct.IsCancellationRequested && s_Running)
            {
                ClientWebSocket ws = null;
                bool canResume = !string.IsNullOrEmpty(s_SessionId) && !string.IsNullOrEmpty(resumeUrl);
                try
                {
                    ws = new ClientWebSocket();
                    string url = canResume ? resumeUrl + "/?v=10&encoding=json" : GATEWAY_URL;
                    await ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);

                    // ── HELLO(op10)：取心跳間隔 ──
                    string hello = await ReceiveMessageAsync(ws, ct).ConfigureAwait(false);
                    int heartbeatMs = 41250;
                    var helloD = Node(ParseSafe(hello), "d");
                    if (helloD != null) heartbeatMs = Math.Max(5000, IntOf(helloD, "heartbeat_interval", 41250));

                    // ── IDENTIFY(op2) 或 RESUME(op6) ──
                    if (canResume)
                    {
                        await SendAsync(ws, BuildResume(token, s_SessionId, lastSeq), ct).ConfigureAwait(false);
                        Debug.Log($"[DiscordGateway] RESUME 送出（session={Short(s_SessionId)}, seq={lastSeq}）");
                    }
                    else
                    {
                        await SendAsync(ws, BuildIdentify(token), ct).ConfigureAwait(false);
                        Debug.Log($"[DiscordGateway] IDENTIFY 送出（intents={INTENTS}）");
                    }

                    // ── 心跳背景任務 ──
                    using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var hbTask = HeartbeatLoopAsync(ws, heartbeatMs, () => lastSeq, hbCts.Token);

                    // ── 收訊迴圈 ──
                    while (!ct.IsCancellationRequested && s_Running && ws.State == WebSocketState.Open)
                    {
                        string raw = await ReceiveMessageAsync(ws, ct).ConfigureAwait(false);
                        if (raw == null) break;   // 對方關閉
                        Interlocked.Exchange(ref s_LastEventUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                        var jd = ParseSafe(raw);
                        if (jd == null) continue;
                        int op = IntOf(jd, "op", -1);
                        // "s" 對非 dispatch 事件是 JSON null（見 Node/IntOf 區塊血證）→ 必走 null-safe
                        int sq = IntOf(jd, "s", 0);
                        if (sq > 0) lastSeq = sq;

                        if (op == 0)   // dispatch
                        {
                            string t = StrOf(jd, "t", "");
                            // 診斷：IDENTIFY 後尚未 READY 期間，把收到的 dispatch 名稱印出來。
                            // 2026-07-28 血證：t 解析錯（見 StrOf）導致 READY 被靜默丟棄、連線看似正常卻永不上線，
                            // 查了兩輪才定位 → 這行讓「收到了但沒認出來」立刻現形。
                            if (!s_Identified) Debug.Log($"[DiscordGateway] dispatch 收到（尚未 READY）: t={(string.IsNullOrEmpty(t) ? "(空!)" : t)}");
                            if (t == "READY")
                            {
                                s_Identified = true;
                                backoff = RECONNECT_BASE_SEC;   // 連上就重置退避
                                var d = Node(jd, "d");
                                if (d != null)
                                {
                                    s_SessionId = StrOf(d, "session_id", "");
                                    resumeUrl = StrOf(d, "resume_gateway_url", null);
                                    s_BotUser = StrOf(Node(d, "user"), "username", s_BotUser);
                                }
                                Debug.Log($"[DiscordGateway] ✓ READY — bot「{s_BotUser}」已在 Discord 顯示上線"
                                          + $"（session={Short(s_SessionId)}）");
                            }
                            else if (t == "RESUMED")
                            {
                                s_Identified = true;
                                backoff = RECONNECT_BASE_SEC;
                                Debug.Log("[DiscordGateway] ✓ RESUMED — 斷線期間的事件已由 Discord 補送");
                            }
                            else if (t == "MESSAGE_CREATE")
                            {
                                // 只入列，不在背景緒寫檔（主緒 drain 後才寫 → 單寫者不變量）
                                var md = Node(jd, "d");
                                if (md != null) s_DispatchQueue.Enqueue(md.ToJson());
                            }
                        }
                        else if (op == 1)   // Discord 要求立刻心跳
                        {
                            await SendAsync(ws, "{\"op\":1,\"d\":" + (lastSeq > 0 ? lastSeq.ToString() : "null") + "}", ct).ConfigureAwait(false);
                        }
                        else if (op == 7)   // RECONNECT
                        {
                            Debug.Log("[DiscordGateway] 收到 RECONNECT(op7) → 重連（嘗試 RESUME）");
                            break;
                        }
                        else if (op == -1)   // IntOf 解析不出 op → payload 或解析器有問題，別靜默吞
                        {
                            Debug.LogWarning($"[DiscordGateway] 收到無法解析 op 的 payload（前 120 字）: "
                                             + raw.Substring(0, Math.Min(120, raw.Length)));
                        }
                        else if (op == 9)   // INVALID_SESSION
                        {
                            bool resumable = BoolOf(jd, "d", false);
                            Debug.LogWarning($"[DiscordGateway] INVALID_SESSION(op9, resumable={resumable})"
                                             + " → 清 session 重新 IDENTIFY（該空窗的訊息由 REST 追平補回）");
                            if (!resumable) { s_SessionId = ""; resumeUrl = null; lastSeq = 0; }
                            break;
                        }
                    }

                    hbCts.Cancel();
                    try { await hbTask.ConfigureAwait(false); } catch { }
                }
                catch (OperationCanceledException) { /* Stop() 正常路徑 */ }
                catch (Exception e)
                {
                    s_LastError = e.Message;
                    // 4014 = Disallowed intents（privileged intent 沒開）→ 這是要人介入的組態問題，不是暫時故障
                    if (e.Message.Contains("4014") || (ws != null && ws.CloseStatusDescription != null
                        && ws.CloseStatusDescription.Contains("intent")))
                    {
                        s_LastError = "IDENTIFY 被拒（close 4014 Disallowed intents）— 請到 Discord Developer Portal "
                                      + "→ Bot → Privileged Gateway Intents 開啟 MESSAGE CONTENT INTENT";
                        Debug.LogError($"[DiscordGateway] {s_LastError}");
                    }
                    else
                    {
                        // 印完整堆疊 — 只印 e.Message 的話遇到 NRE 這種「訊息無資訊量」的例外就無從定位
                        // （2026-07-28 實測：斷線路徑吞了一個 NRE，只看 message 完全查不出在哪行）
                        Debug.LogWarning($"[DiscordGateway] 連線異常: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                    }
                }
                finally
                {
                    s_Identified = false;
                    if (ws != null)
                    {
                        try
                        {
                            if (ws.State == WebSocketState.Open)
                            {
                                // 主動 Close → Discord 立刻標離線（不等心跳逾時，綠點不殘留）
                                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token)
                                        .ConfigureAwait(false);
                            }
                        }
                        catch { }
                        try { ws.Dispose(); } catch { }
                    }
                }

                if (!s_Running || ct.IsCancellationRequested) break;

                // 退避重連
                s_ReconnectCount++;
                Debug.Log($"[DiscordGateway] {backoff:0.#}s 後重連（第 {s_ReconnectCount} 次）");
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct).ConfigureAwait(false); }
                catch { break; }
                backoff = Math.Min(RECONNECT_MAX_SEC, backoff * 2);
            }

            s_Identified = false;
            Debug.Log("[DiscordGateway] 迴圈結束（已離線）");
        }

        // 心跳：每 interval 送 op1 帶最後 seq。漏心跳 Discord 會斷線 → 由外層退避重連接手。
        static async Task HeartbeatLoopAsync(ClientWebSocket ws, int intervalMs, Func<int> lastSeq, CancellationToken ct)
        {
            try
            {
                // 首次心跳加 jitter（Discord 建議，避免所有 client 同時心跳）
                // ⚠ 必須用 System.Random 而非 UnityEngine.Random —— 後者是**主緒限定** API，在背景緒呼叫會拋
                //   "get_value can only be called from the main thread"，心跳迴圈當場死掉 → Discord 約
                //   1.25×interval 後踢連線 → bot 綠點閃爍。（2026-07-28 實測踩到，log 有留證。）
                double jitter = new System.Random(Environment.TickCount).NextDouble();
                await Task.Delay((int)(intervalMs * jitter), ct).ConfigureAwait(false);
                int beat = 0;
                string lastPresence = null;
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    int sq = lastSeq();
                    await SendAsync(ws, "{\"op\":1,\"d\":" + (sq > 0 ? sq.ToString() : "null") + "}", ct).ConfigureAwait(false);

                    // presence 刷新「誰在線」（Tim 2026-08-01）—— 搭心跳的順風車，不另開迴圈。
                    // ⚠ 不是每次心跳都送：Discord 對 presence 更新有 rate limit（每分鐘 5 次），
                    //   而且人來人往沒那麼頻繁。另外**只在名單真的變了才送** ——
                    //   送一則內容一模一樣的更新，對 Discord 是流量、對讀 log 的人是雜訊。
                    if (++beat % PRESENCE_REFRESH_EVERY_N_HEARTBEATS == 0)
                    {
                        try
                        {
                            string now = PresenceText();
                            if (now != lastPresence)
                            {
                                await SendAsync(ws, BuildPresenceUpdate(), ct).ConfigureAwait(false);
                                lastPresence = now;
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception pe)
                        {
                            // presence 只是門面 —— 更新失敗絕不能拖垮心跳，
                            // 心跳斷了 Discord 約 1.25×interval 就踢連線（2026-07-28 踩過）
                            Debug.LogWarning($"[DiscordGateway] presence 更新略過: {pe.Message}");
                        }
                    }

                    await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Debug.LogWarning($"[DiscordGateway] 心跳中斷: {e.Message}"); }
        }

        // ===========================================================
        // 區塊：WebSocket 收發 helper
        // 物理意義：gateway frame 可能分片（EndOfMessage=false）→ 必須組裝完整才 parse。
        // ===========================================================
        static async Task SendAsync(ClientWebSocket ws, string json, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }

        static async Task<string> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buf = new byte[RECEIVE_BUFFER_SIZE];
            using var ms = new System.IO.MemoryStream();
            while (true)
            {
                var res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                if (res.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, res.Count);
                if (ms.Length > MAX_MESSAGE_BYTES)
                    throw new Exception($"gateway frame 超過 {MAX_MESSAGE_BYTES} bytes — 放棄本連線");
                if (res.EndOfMessage) break;
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        // ===========================================================
        // 區塊：payload 組裝
        // 物理意義：IDENTIFY 的 presence 欄位讓 bot 上線時直接帶「正在做什麼」，比純綠點更好辨識。
        // ===========================================================
        static string BuildIdentify(string token)
        {
            var sb = new StringBuilder();
            sb.Append("{\"op\":2,\"d\":{");
            sb.Append("\"token\":\"").Append(EscapeJson(token)).Append("\",");
            sb.Append("\"intents\":").Append(INTENTS).Append(',');
            sb.Append("\"properties\":{\"os\":\"windows\",\"browser\":\"UCL_Core\",\"device\":\"UnityEditor\"},");
            sb.Append("\"presence\":").Append(BuildPresenceObject()).Append('}');
            sb.Append('}');
            return sb.ToString();
        }

        // ===========================================================
        // 區塊：presence 顯示「誰在線」（Tim 2026-08-01）
        // 物理意義：原本固定顯示「ChatTavern ⇄ Discord」—— 那句話一年到頭都一樣，
        //          等於只告訴你「橋還活著」。Tim 要的是**現在有誰醒著**（basecamp, kaguya, gura）
        //          —— 從 Discord 那端一眼看出這邊有沒有人、有誰。
        // 資料源：AgentCommands/_session/_persona_*.json 的未過期 lock，
        //        判準與 Cmd_LoginStatus 一致（expires_at < now → expired），不另立第二套規則。
        // 數值影響：presence 在 IDENTIFY 帶一次，之後由 heartbeat 迴圈**每 N 次心跳刷新一次**
        //          （op 3 Presence Update）。不是每次心跳都送 —— Discord 對 presence 更新有
        //          rate limit（每分鐘 5 次），而且人來人往沒那麼頻繁。
        // 邊界：讀不到 / 沒人在線 → 回退成原本那句。**空不等於沒人**，可能只是 lock 讀不到；
        //      顯示「無人在線」會是一句可能為假的斷言，顯示橋名則永遠為真。
        // ===========================================================
        const string PRESENCE_FALLBACK = "ChatTavern ⇄ Discord";
        const int PRESENCE_REFRESH_EVERY_N_HEARTBEATS = 5;   // 心跳約 41s → 約 3.5 分鐘刷一次

        /// <summary>讀目前未過期的 persona lock，回排序後的名單；讀不到回空 list。
        /// 走 UCL_ActivePersonaLocks 唯一掃描實作（它已處理壞檔略過與排序），不自己掃。</summary>
        static List<string> OnlinePersonas()
        {
            var names = new List<string>();
            try
            {
                foreach (var l in UCL.Core.EditorLib.AgentCommands.UCL_ActivePersonaLocks.ListOnline())
                    if (!names.Contains(l.Persona)) names.Add(l.Persona);
            }
            catch { /* 讀不到就回空 → 上層回退成橋名 */ }
            return names;
        }

        /// <summary>presence 的 activity 名稱：有人在線列名字，否則回退橋名。</summary>
        static string PresenceText()
        {
            var names = OnlinePersonas();
            if (names.Count == 0) return PRESENCE_FALLBACK;
            string joined = string.Join(", ", names);
            // Discord activity name 上限 128 字元 —— 超過就截並標數量，不讓 payload 被伺服器拒收
            if (joined.Length > 110)
                joined = joined.Substring(0, 107) + $"…（{names.Count} 人）";
            return joined;
        }

        static string BuildPresenceObject()
            => "{\"status\":\"online\",\"afk\":false,\"since\":0,\"activities\":[{\"name\":\""
               + EscapeJson(PresenceText()) + "\",\"type\":3}]}";

        /// <summary>op 3 Presence Update —— 連線中途刷新「誰在線」。</summary>
        static string BuildPresenceUpdate()
        {
            var names = OnlinePersonas();
            var sb = new StringBuilder();
            sb.Append("{\"op\":3,\"d\":{\"status\":\"online\",\"afk\":false,\"since\":null,");
            sb.Append("\"activities\":[{\"name\":\"").Append(EscapeJson(PresenceText())).Append("\",\"type\":3}]}}");
            return sb.ToString();
        }

        static string BuildResume(string token, string sessionId, int seq)
            => "{\"op\":6,\"d\":{\"token\":\"" + EscapeJson(token) + "\",\"session_id\":\""
               + EscapeJson(sessionId) + "\",\"seq\":" + (seq > 0 ? seq.ToString() : "null") + "}}";

        static string EscapeJson(string s)
            => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        // ===========================================================
        // 區塊：JSON null-safe 取值 — gateway payload 大量使用 JSON null
        // 物理意義：Discord 對非 dispatch 事件（op10 HELLO / **op11 HEARTBEAT_ACK** / op7 …）送 `"s": null`、
        //          有時 `"d": null`。此時 `Contains("s")` 為 **true**（key 存在）但值是 null →
        //          JsonData.GetInt(key, def) 內部解 null 直接 NullReferenceException。
        // 血證（2026-07-28）：原本用 `if (jd.Contains("s")) jd.GetInt("s", 0)`，連上後收到**第一個心跳 ACK**
        //          就拋 NRE → 收訊迴圈 break → 斷線重連 → bot 綠點閃爍（reconnects 一路累加）。
        //          「key 存在」≠「值可轉型」，這類 payload 一律走本 helper，不要直接 GetXxx。
        // 數值影響：任何異常 / null / 型別不符 → 回 default，絕不讓解析問題拆掉連線。
        // ===========================================================
        static JsonData Node(JsonData parent, string key)
        {
            try
            {
                if (parent == null || !parent.Contains(key)) return null;
                var n = parent[key];
                return (n == null || JsonData.IsNull(n)) ? null : n;
            }
            catch { return null; }
        }

        static int IntOf(JsonData parent, string key, int def)
        {
            var n = Node(parent, key);
            if (n == null) return def;
            try { return n.GetInt(def); } catch { return def; }
        }

        static string StrOf(JsonData parent, string key, string def)
        {
            var n = Node(parent, key);
            if (n == null) return def;
            // ⚠ 必須用 GetStringWithDefaultValue —— `n.GetString(def)` 會解析到多載
            //   `GetString(string iKey)`，把「預設值」當成 **key** 去查（JsonData 的已知 API 陷阱，
            //   UCL_ChatTavernAdminPage 也留過同款註解）。
            // 血證（2026-07-28）：原本寫 n.GetString(def) → dispatch 的 `t` 永遠取到空字串 →
            //   READY / MESSAGE_CREATE 全部匹配不到 → bot 永不標上線、訊息永不中繼，
            //   而且**沒有任何錯誤** — 連線活著、心跳正常，只是事件被靜默丟棄（最難查的失敗形狀）。
            try { return n.GetStringWithDefaultValue(def); } catch { return def; }
        }

        static bool BoolOf(JsonData parent, string key, bool def)
        {
            var n = Node(parent, key);
            if (n == null) return def;
            try { return n.GetBool(def); } catch { return def; }
        }

        static JsonData ParseSafe(string json)
        {
            try { return string.IsNullOrEmpty(json) ? null : JsonData.ParseJson(json); }
            catch { return null; }
        }

        static string Short(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length <= 8 ? s : s.Substring(0, 8) + "…");

        /// <summary>診斷字串（Cmd_Invoke / AdminPage 用）。不吐 token、session id 只露前 8 碼。</summary>
        public static string SelfTest()
            => $"IsRunning={IsRunning}; IsOnline={IsOnline}; BotUser={(string.IsNullOrEmpty(BotUser) ? "(none)" : BotUser)}; "
               + $"session={Short(s_SessionId)}; reconnects={ReconnectCount}; pendingDispatch={PendingDispatchCount}; "
               + $"secsSinceLastEvent={SecondsSinceLastEvent:0.#}; lastError={(string.IsNullOrEmpty(LastError) ? "(none)" : LastError)}";
    }
}
#endif
