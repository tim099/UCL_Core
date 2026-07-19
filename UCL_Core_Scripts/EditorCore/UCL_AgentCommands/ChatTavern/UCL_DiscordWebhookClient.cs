// 區塊職責：Discord webhook POST 客戶端（C# native）— Discord Mirror Phase B T3
// 物理意義：取代 python _lib/discord_webhook.py 的 send_one。對單一 webhook URL 送一則訊息，回「per-URL 明細」
//          （非舊 any_ok 聚合 — any_ok 是多 webhook partial drop 的 bug 源，見 Plan_Discord_Mirror_MultiWebhook_Partial_Drop.md）。
// 設計取捨：
//   - 用 UnityWebRequest + await（對齊專案既有 UCL_StreamingAssets 樣板 + UniTask UnityWebRequestExtensions），
//     不用 System.Net.Http.HttpClient — 免 asmdef reference 風險，且 await 完成點回主緒 → 天然滿足 kiara
//     「單寫者 cursor」（cursor 由 daemon 在 await 後於主緒推進，無需背景執行緒 marshaling）。
//   - PostOnceAsync 只送「一次」，不內建 sleep-retry loop — 避免 edit-mode timer（UniTask.Delay 在 edit mode
//     不保證 tick）坑。429 只「解析 Retry-After 並回報」，實際退避重試由 T4 daemon 依 tick 排程 per-webhook backoff。
// 數值影響：webhook-id 取 URL 倒數第二段（id 穩定、token 會 rotate）當 per-webhook cursor key；malformed URL 直接回錯。
#if UNITY_EDITOR
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    /// <summary>
    /// 單次 webhook POST 的結果明細（per-URL，不聚合）。
    /// daemon 依此決定：ok → 推進該 webhook cursor；isRateLimited → 依 retryAfterSeconds 排程 backoff 重試；
    /// 其他 error → 不推進 cursor（下輪重送，fail 方向 = 可見重送而非隱形漏送）。
    /// </summary>
    public struct UCL_WebhookPostResult
    {
        public string webhookId;          // URL 倒數第二段（穩定 id，不含 token）；null = malformed URL
        public bool ok;                   // HTTP 2xx
        public string messageId;          // T8：?wait=true 回應內的 Discord message id（真建立憑據）；null = 未取得
        public long statusCode;           // HTTP status code（0 = 網路層未拿到 response）
        public bool isRateLimited;        // HTTP 429
        public double retryAfterSeconds;  // 429 時建議退避秒數（來自 Retry-After header）；非 429 為 0
        public string error;              // null = 成功；否則人類可讀錯誤字串
    }

    /// <summary>
    /// Discord webhook POST 客戶端（T3）。static、無狀態 — 純把「一則內容 POST 到一個 URL」封裝成 async 呼叫。
    /// 去重 / cursor / backoff 排程 / identity 解析都不在此，屬 daemon（T4+）職責。
    /// </summary>
    public static class UCL_DiscordWebhookClient
    {
        // 區塊職責：單次請求逾時 + 429 fallback 退避常數
        // 物理意義：REQUEST_TIMEOUT_SEC 給 UnityWebRequest.timeout；DEFAULT_RETRY_AFTER 是 429 沒帶 Retry-After 時的保守值
        const int REQUEST_TIMEOUT_SEC = 15;
        const double DEFAULT_RETRY_AFTER_SEC = 5.0;

        // 區塊職責：User-Agent header（2026-07-19 root-cause 修）
        // 物理意義：Discord API 要求帶 User-Agent；UnityWebRequest 預設 UA 會被 Discord 前面的 Cloudflare 間歇性
        //          挑戰 → 回誤導性 2xx 但訊息未送達（實測:urllib 預設 UA 收 403 error 1010、瀏覽器風 UA 收 200）。
        //          這是「HTTP 204 外觀 OK ≠ 真送達」的真兇。設一個 Cloudflare-friendly UA 根治。
        const string USER_AGENT = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) UCL-DiscordMirror/1.0";

        /// <summary>
        /// 從 webhook URL 抽穩定 id — Discord URL 形如 `.../webhooks/&lt;id&gt;/&lt;token&gt;`，取倒數第二段。
        /// 用途：per-webhook cursor key。不用完整 URL（含 token）當 key — token rotate 會讓 cursor 誤重置。
        /// malformed（段數不足 / 空）→ 回 null，caller 視為無效 URL 跳過。
        /// </summary>
        public static string ExtractWebhookId(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            string trimmed = url.TrimEnd('/');
            int tokenSlash = trimmed.LastIndexOf('/');
            if (tokenSlash <= 0) return null;                       // 無 token 段 → malformed
            string beforeToken = trimmed.Substring(0, tokenSlash);  // 砍掉 token 段
            int idSlash = beforeToken.LastIndexOf('/');
            if (idSlash < 0 || idSlash >= beforeToken.Length - 1) return null;  // 無 id 段 / id 空 → malformed
            string id = beforeToken.Substring(idSlash + 1);
            return string.IsNullOrEmpty(id) ? null : id;
        }

        /// <summary>
        /// POST 一次到單一 webhook URL。不做內部 retry —— 429 只解析 Retry-After 回報，退避重試由 daemon 排程。
        /// await 完成點在主緒（UnityWebRequest + UniTask awaiter），呼叫端可在 await 後安全於主緒推進 cursor。
        /// </summary>
        /// <param name="url">webhook 完整 URL（含 token）</param>
        /// <param name="content">訊息內容（Discord content 欄位；可空，若有 embeds）</param>
        /// <param name="username">webhook username override（可 null）</param>
        /// <param name="avatarUrl">webhook avatar_url override（可 null）</param>
        /// <param name="embedsJson">預先序列化好的 embeds JSON 陣列字串（可 null；由 T5 identity 層建）</param>
        /// <param name="ct">取消 token（進入時檢查；UnityWebRequest 主要靠 timeout 收斂）</param>
        public static async Task<UCL_WebhookPostResult> PostOnceAsync(
            string url, string content, string username, string avatarUrl, string embedsJson, CancellationToken ct)
        {
            var result = new UCL_WebhookPostResult { webhookId = ExtractWebhookId(url), ok = false };

            if (string.IsNullOrEmpty(url) || result.webhookId == null)
            {
                result.error = "malformed webhook url（抽不出 webhook-id）";
                return result;
            }
            if (ct.IsCancellationRequested)
            {
                result.error = "cancelled before send";
                return result;
            }

            string json = BuildPayloadJson(content, username, avatarUrl, embedsJson);
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

            UnityWebRequest req = null;
            try
            {
                req = new UnityWebRequest(url, "POST");
                req.uploadHandler = new UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("User-Agent", USER_AGENT);   // 防 Cloudflare 擋（假 2xx 未送達的真兇）
                req.timeout = REQUEST_TIMEOUT_SEC;

                // await 完成點回主緒（對齊 UCL_StreamingAssets 樣板）— 網路 IO 走 native 執行緒，不卡 Editor 主迴圈
                var op = req.SendWebRequest();
                await op;

                result.statusCode = req.responseCode;

                if (req.responseCode >= 200 && req.responseCode < 300)
                {
                    result.ok = true;
                    return result;
                }
                if (req.responseCode == 429)
                {
                    result.isRateLimited = true;
                    result.retryAfterSeconds = ParseRetryAfterSeconds(req);
                    result.error = $"429 rate limited (retry after {result.retryAfterSeconds:F1}s)";
                    return result;
                }
                result.error = $"HTTP {req.responseCode} (result={req.result})";
                return result;
            }
            catch (Exception e)
            {
                result.error = $"exception: {e.Message}";
                return result;
            }
            finally
            {
                req?.Dispose();
            }
        }

        // ===========================================================
        // 區塊：poll-friendly API（basecamp 拍板 · 不賭 await 在 edit-mode resume）
        // 物理意義：daemon 自己持有 UnityWebRequest、每 tick 輪詢 isDone → 確定性 100%，不依賴 editor
        //          SynchronizationContext pump。StartPost 只「發出」不 await；完成後 daemon 呼叫 InterpretResult。
        // 數值影響：跟 PostOnceAsync（await 版）共用 BuildPayloadJson / ExtractWebhookId / ParseRetryAfter，
        //          只換驅動方式；await 版保留當 fallback / 一次性 smoke。
        // ===========================================================

        /// <summary>
        /// 發出一次 POST（不 await）→ 回已 SendWebRequest 的 UnityWebRequest 供 daemon 輪詢 isDone。
        /// malformed URL 回 null。呼叫端負責在 isDone 後呼叫 InterpretResult 並 Dispose（或用 using）。
        /// </summary>
        // 區塊職責：T8 — webhook URL 加掛 ?wait=true（Discord 回 200 + message JSON 而非 204）
        // 物理意義：204 = 「接受」；wait=true 的 200 + message id = 「真的建立」。能證偽驗收的送達憑據，
        //          代價是 Discord 端同步等建立完成（單筆延遲略增，daemon poll 模型不在乎）。
        static string AppendWaitTrue(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (url.Contains("wait=true")) return url;
            return url + (url.Contains("?") ? "&wait=true" : "?wait=true");
        }

        // 區塊職責：從 JSON 文字抽頂層字串欄位（輕量、免 JsonLib 依賴）
        // 邊界：找第一個 "key":"value" — Discord message 物件 id 慣例序列化在最前；此值僅作
        //       診斷憑據（ok 判定不依賴它），極端欄位序異動最多憑據錯位、不影響 cursor 正確性。
        static string ExtractJsonStringField(string json, string key)
        {
            string token = "\"" + key + "\":\"";
            int i = json.IndexOf(token, StringComparison.Ordinal);
            if (i < 0) return null;
            int start = i + token.Length;
            int end = json.IndexOf('"', start);
            return end > start ? json.Substring(start, end - start) : null;
        }

        public static UnityWebRequest StartPost(string url, string content, string username, string avatarUrl, string embedsJson)
        {
            if (string.IsNullOrEmpty(url) || ExtractWebhookId(url) == null) return null;
            url = AppendWaitTrue(url);   // T8：送達憑據（message id）
            string json = BuildPayloadJson(content, username, avatarUrl, embedsJson);
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);
            var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("User-Agent", USER_AGENT);   // 防 Cloudflare 擋（假 2xx 未送達的真兇）
            req.timeout = REQUEST_TIMEOUT_SEC;
            req.SendWebRequest();   // 發出即回；完成靠 daemon tick 輪詢 req.isDone
            return req;
        }

        /// <summary>把一個「已完成（isDone）」的 UnityWebRequest 判讀成 per-URL 結果明細。呼叫前確認 req.isDone。</summary>
        public static UCL_WebhookPostResult InterpretResult(UnityWebRequest req)
        {
            var result = new UCL_WebhookPostResult { ok = false };
            if (req == null) { result.error = "null request"; return result; }
            result.webhookId = ExtractWebhookId(req.url);
            result.statusCode = req.responseCode;

            if (req.result == UnityWebRequest.Result.ConnectionError || req.result == UnityWebRequest.Result.DataProcessingError)
            {
                result.error = $"network/{req.result}: {req.error}";
                return result;
            }
            if (req.responseCode >= 200 && req.responseCode < 300)
            {
                result.ok = true;
                // T8：?wait=true 時 Discord 回 200 + message JSON — 抽 "id" 當「真建立」ground truth
                //（204 只代表接受非渲染確認；message id 是能證偽驗收的送達憑據）
                try
                {
                    string body = req.downloadHandler != null ? req.downloadHandler.text : null;
                    if (!string.IsNullOrEmpty(body)) result.messageId = ExtractJsonStringField(body, "id");
                }
                catch { /* 憑據抽取失敗不影響 ok 判定 */ }
                return result;
            }
            if (req.responseCode == 429)
            {
                result.isRateLimited = true;
                result.retryAfterSeconds = ParseRetryAfterSeconds(req);
                result.error = $"429 rate limited (retry after {result.retryAfterSeconds:F1}s)";
                return result;
            }
            result.error = $"HTTP {req.responseCode} (result={req.result})";
            return result;
        }

        // 區塊職責：解析 429 的 Retry-After header（秒）
        // 物理意義：Discord 429 帶 Retry-After（秒，可能小數）；沒帶或 parse 失敗 → 保守 DEFAULT_RETRY_AFTER_SEC
        // 數值影響：回傳秒數給 daemon 排 per-webhook backoff；不在此 sleep（避免 edit-mode timer 坑）
        static double ParseRetryAfterSeconds(UnityWebRequest req)
        {
            string h = req.GetResponseHeader("Retry-After");
            if (!string.IsNullOrEmpty(h) && double.TryParse(h, out double sec) && sec >= 0)
            {
                return sec;
            }
            return DEFAULT_RETRY_AFTER_SEC;
        }

        // 區塊職責：組 Discord webhook JSON payload —— {content, username?, avatar_url?, embeds?}
        // 物理意義：對齊 python send_one 的 body 結構；username/avatar_url 空則不 emit（走 webhook 預設）
        // 數值影響：字串手動 escape（避免引入 JsonLib 依賴）；embedsJson 為預先序列化好的原始陣列字串直接內嵌
        static string BuildPayloadJson(string content, string username, string avatarUrl, string embedsJson)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"content\":");
            AppendJsonString(sb, content ?? "");
            if (!string.IsNullOrEmpty(username))
            {
                sb.Append(",\"username\":");
                AppendJsonString(sb, username);
            }
            if (!string.IsNullOrEmpty(avatarUrl))
            {
                sb.Append(",\"avatar_url\":");
                AppendJsonString(sb, avatarUrl);
            }
            if (!string.IsNullOrEmpty(embedsJson))
            {
                // embedsJson 已是合法 JSON 陣列字串（由 T5 identity 層建），直接內嵌不再 escape
                sb.Append(",\"embeds\":");
                sb.Append(embedsJson);
            }
            sb.Append('}');
            return sb.ToString();
        }

        // 區塊職責：把字串包成合法 JSON string literal（含跳脫）
        // 物理意義：手寫最小 escaper —— 處理 " \ 與控制字元（\n \r \t \b \f + \u00XX）
        // 數值影響：非 ASCII 中文等直接輸出（UTF-8 body，Discord 接受），只跳脫必要控制字元
        static void AppendJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
#endif
