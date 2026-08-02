// 區塊職責：Treasury（bank）ledger → Discord 通知的 C# native 模組（Phase C 首棒，Tim 2026-07-19 拍板「不管原本 .py 流程、C# 端重新做」）
// 物理意義：取代 python notify_treasury_entries 的 pull adapter — ledger 本身是 append-only 事件流
//          （Treasury/ledger/<date>/<ts>_<uuid>__<type>.json，relkey ordinal 天然有序），cursor + 冪等重試。
//          python 版病史：只在 mirror run 內順跑（cutover 後 run 不再被觸發 → 兩天 125 筆積壓）、
//          20 筆/run 上限、intermittent print 遺失難以體檢。native 版直接掛 daemon tick，沒有「要被誰想起來才跑」的問題。
// 數值影響：cursor 沿用 _treasury_state.json {last_seen}（T6.6 拆檔後的獨立小檔；native 接管後 python 端
//          自查閘跳過 = 單寫者不變）。逐筆序列送出（同一時刻最多一筆 in-flight）→ 天然限速不打 429；
//          any_ok 語意（多 webhook 任一送達即推進，dead webhook 不擋帳，對齊 python）。
// 邊界：cursor 空 → baseline 到最新不回放歷史；__audit 檔靜默推進（include_audit=true 可開）；
//       壞檔跳過並推進（不卡死）；全 webhook fail → cursor 保留下輪重試。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace UCL.Core.EditorLib.AgentCommands.ChatTavern
{
    public static class UCL_DiscordTreasuryMirror
    {
        // ===========================================================
        // 區塊職責：config（treasury_mirror 區塊）— class + JsonLib 讀取，5s 快取（同 daemon 慣例）
        // ===========================================================
        class TreasuryConfigBlock
        {
            public bool enabled = false;
            public List<string> webhook_urls = new List<string>();
            public List<string> disabled_webhook_urls = new List<string>(); // 保留 URL 但不投遞（Admin toggle）
            public string webhook_file = "";
            public string webhook_env_var = "";
            public bool include_audit = false;
        }
        const double CONFIG_CACHE_TTL_SEC = 5.0;
        static TreasuryConfigBlock s_CachedConfig = null;
        static double s_ConfigCacheTime = -999;

        static TreasuryConfigBlock ReadConfig()
        {
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (s_CachedConfig != null && now - s_ConfigCacheTime < CONFIG_CACHE_TTL_SEC) return s_CachedConfig;
            TreasuryConfigBlock cfg = null;
            try
            {
                string path = Path.Combine(UCL_RepoPath.AgentCommandsDir, "PromptQueue", "notify_config.json");
                if (File.Exists(path))
                {
                    var jd = UCL.Core.JsonLib.JsonData.ParseJson(File.ReadAllText(path));
                    if (jd != null && jd.IsObject && jd.Contains("treasury_mirror"))
                    {
                        var tm = jd["treasury_mirror"];
                        if (tm != null && tm.IsObject)
                            cfg = UCL.Core.JsonLib.JsonConvert.LoadDataFromJson<TreasuryConfigBlock>(tm, UCL.Core.JsonLib.JsonConvert.SaveMode.Normal);
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning($"[TreasuryMirror] config read fail: {e.Message}"); }
            if (cfg == null) cfg = new TreasuryConfigBlock();
            if (cfg.webhook_urls == null) cfg.webhook_urls = new List<string>();
            if (cfg.disabled_webhook_urls == null) cfg.disabled_webhook_urls = new List<string>();
            s_CachedConfig = cfg;
            s_ConfigCacheTime = now;
            return cfg;
        }

        // ===========================================================
        // 區塊職責：cursor state（_treasury_state.json {last_seen}）— read-modify-write 保未知欄位 + atomic write
        // 物理意義：T6.6 拆出的獨立小檔；native 接管後 python notify_treasury_entries 自查閘跳過 → 單寫者。
        // ===========================================================
        static string StatePath => Path.Combine(UCL_RepoPath.AgentCommandsDir, "PromptQueue", "_treasury_state.json");

        static string LoadCursor()
        {
            try
            {
                if (!File.Exists(StatePath)) return "";
                var jd = UCL.Core.JsonLib.JsonData.ParseJson(File.ReadAllText(StatePath));
                if (jd != null && jd.IsObject) return jd.GetString("last_seen", "");
            }
            catch (Exception e) { Debug.LogWarning($"[TreasuryMirror] state load fail: {e.Message}"); }
            return "";
        }

        static void SaveCursor(string relkey)
        {
            try
            {
                UCL.Core.JsonLib.JsonData state = null;
                if (File.Exists(StatePath)) state = UCL.Core.JsonLib.JsonData.ParseJson(File.ReadAllText(StatePath));
                if (state == null || !state.IsObject) state = UCL.Core.JsonLib.JsonData.ParseJson("{}");
                state["last_seen"] = relkey;
                string tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, state.ToJsonBeautify());
                if (File.Exists(StatePath)) File.Delete(StatePath);
                File.Move(tmp, StatePath);
            }
            catch (Exception e) { Debug.LogWarning($"[TreasuryMirror] state save fail: {e.Message}"); }
        }

        // ===========================================================
        // 區塊職責：ledger 掃描 — cursor 之後的 relkey 升冪清單（date dir 名 < cursor date 的整夾 cheap prune）
        // ===========================================================
        static string LedgerRoot => Path.Combine(UCL_RepoPath.AgentCommandsDir, "Treasury", "ledger");

        static List<string> CollectPending(string cursor)
        {
            var pending = new List<string>();
            if (!Directory.Exists(LedgerRoot)) return pending;
            string cursorDate = string.IsNullOrEmpty(cursor) ? "" : cursor.Split('/')[0];
            var dateDirs = Directory.GetDirectories(LedgerRoot);
            Array.Sort(dateDirs, StringComparer.Ordinal);
            foreach (var ddir in dateDirs)
            {
                string dname = Path.GetFileName(ddir);
                if (!string.IsNullOrEmpty(cursorDate) && string.CompareOrdinal(dname, cursorDate) < 0) continue;
                var files = Directory.GetFiles(ddir, "*.json");
                Array.Sort(files, StringComparer.Ordinal);
                foreach (var f in files)
                {
                    string relkey = dname + "/" + Path.GetFileName(f);
                    if (string.CompareOrdinal(relkey, cursor) > 0) pending.Add(relkey);
                }
            }
            return pending;
        }

        // ===========================================================
        // 區塊職責：單筆序列送出狀態機 — 同一時刻最多一筆 entry、一個 webhook 在途
        // 物理意義：順序 = 帳目時序（Discord 顯示序跟 ledger 一致）；天然限速；429 → 等 retryAfter 再試同 URL。
        // ===========================================================
        static string s_CurrentRelkey = null;          // 正在送的 entry（null = 閒置，下 tick 掃下一筆）
        static List<string> s_CurrentUrls = null;      // 該 entry 還沒送的 webhook URLs
        static bool s_CurrentAnyOk = false;            // any_ok 累計（任一 webhook 送達即推進 cursor）
        static UnityWebRequest s_Req = null;           // 在途 request（單槽）
        static double s_BackoffUntil = -999;           // 429 退避到期（EditorApplication.timeSinceStartup 基準）

        /// <summary>daemon tick 進入點 — iActive =（daemon Enabled 且 mirror_owner=native）。在途 drain 永遠跑。</summary>
        public static void Tick(bool iActive)
        {
            double now = UnityEditor.EditorApplication.timeSinceStartup;

            // ---- drain：在途 request 完成判讀 ----
            if (s_Req != null)
            {
                if (!s_Req.isDone) return;
                var res = UCL_DiscordWebhookClient.InterpretResult(s_Req);
                s_Req.Dispose(); s_Req = null;
                if (res.ok)
                {
                    s_CurrentAnyOk = true;
                    Debug.Log($"[TreasuryMirror] ✓ {s_CurrentRelkey} → webhook {res.webhookId} (HTTP {res.statusCode}, msg={res.messageId ?? "?"})");
                }
                else if (res.isRateLimited)
                {
                    s_BackoffUntil = now + Math.Max(1.0, res.retryAfterSeconds);
                    // 429：把剛送的 URL 塞回隊頭，退避到期後重試（不算 fail、不丟 URL）
                    if (!string.IsNullOrEmpty(s_InFlightUrl) && s_CurrentUrls != null)
                        s_CurrentUrls.Insert(0, s_InFlightUrl);
                    Debug.LogWarning($"[TreasuryMirror] 429 → backoff {res.retryAfterSeconds:F1}s");
                }
                else
                {
                    Debug.LogWarning($"[TreasuryMirror] fail {s_CurrentRelkey} → {res.webhookId}: {res.error}");
                }
            }

            if (now < s_BackoffUntil) return;

            // ---- 當前 entry 還有下一個 webhook → 續發 ----
            if (s_CurrentRelkey != null && s_CurrentUrls != null && s_CurrentUrls.Count > 0)
            {
                SendNextUrl();
                return;
            }

            // ---- 當前 entry 全 webhook 跑完 → 結帳 ----
            if (s_CurrentRelkey != null)
            {
                if (s_CurrentAnyOk)
                {
                    SaveCursor(s_CurrentRelkey);   // any_ok → 推進（dead webhook 不擋帳，對齊 python）
                }
                else
                {
                    // 全滅 → cursor 保留，等下輪重試（可見重送非隱形漏）；退避 30s 免空轉刷 log
                    s_BackoffUntil = now + 30.0;
                    Debug.LogWarning($"[TreasuryMirror] {s_CurrentRelkey} 全 webhook fail — cursor 保留，30s 後重試");
                }
                s_CurrentRelkey = null; s_CurrentUrls = null; s_CurrentAnyOk = false;
                return;   // 下 tick 再掃下一筆（每 tick 最多結一筆，節奏平滑）
            }

            // ---- 閒置 → 掃下一筆 ----
            if (!iActive) return;
            var cfg = ReadConfig();
            if (!cfg.enabled) return;

            string cursor = LoadCursor();
            var pending = CollectPending(cursor);
            if (pending.Count == 0) return;

            // baseline：cursor 空 → 直接錨到最新，不回放歷史（對齊 python 首用行為）
            if (string.IsNullOrEmpty(cursor))
            {
                SaveCursor(pending[pending.Count - 1]);
                Debug.Log($"[TreasuryMirror] baseline established（略過 {pending.Count} 筆歷史）");
                return;
            }

            string relkey = pending[0];

            // __audit 檔（0 金額稽核記錄）預設不播 → 靜默推進
            if (relkey.EndsWith("__audit.json") && !cfg.include_audit)
            {
                SaveCursor(relkey);
                return;
            }

            // 讀 entry + 組 embed；壞檔 → log 跳過並推進（不卡死）
            string embedJson;
            try
            {
                string full = Path.Combine(LedgerRoot, relkey.Replace('/', Path.DirectorySeparatorChar));
                var entry = UCL.Core.JsonLib.JsonData.ParseJson(File.ReadAllText(full));
                if (entry == null || !entry.IsObject) throw new Exception("not a JSON object");
                embedJson = BuildEmbedJson(entry);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TreasuryMirror] entry parse fail @ {relkey}: {e.Message} — 跳過並推進 cursor");
                SaveCursor(relkey);
                return;
            }

            // 解析 webhook（ENV > FILE > config；placeholder 過濾）— 借 daemon 的 scope resolver 語意，
            // 此處自解（模組自足）：treasury 慣例上只用 config 直列，file/env 留擴充。
            var urls = ResolveUrls(cfg);
            if (urls.Count == 0) { Debug.LogWarning("[TreasuryMirror] 無可用 webhook URL"); return; }

            s_CurrentRelkey = relkey;
            s_CurrentUrls = urls;
            s_CurrentAnyOk = false;
            s_PendingEmbedJson = embedJson;
            SendNextUrl();
        }

        static string s_PendingEmbedJson = null;   // 當前 entry 的 embed JSON（各 webhook 共用）
        static string s_InFlightUrl = null;        // 剛發出的 URL（429 塞回隊頭用）

        static void SendNextUrl()
        {
            if (s_CurrentUrls == null || s_CurrentUrls.Count == 0) return;
            string url = s_CurrentUrls[0];
            s_CurrentUrls.RemoveAt(0);
            s_InFlightUrl = url;
            s_Req = UCL_DiscordWebhookClient.StartPost(url, "", null, null, s_PendingEmbedJson);
            if (s_Req == null)
                Debug.LogWarning($"[TreasuryMirror] malformed webhook url（跳過）");
        }

        static List<string> ResolveUrls(TreasuryConfigBlock cfg)
        {
            var urls = new List<string>();
            try
            {
                if (!string.IsNullOrEmpty(cfg.webhook_env_var))
                {
                    string ev = Environment.GetEnvironmentVariable(cfg.webhook_env_var);
                    if (!string.IsNullOrEmpty(ev))
                        foreach (var u in ev.Split(new[] { '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
                            urls.Add(u.Trim());
                    if (urls.Count > 0) return urls;
                }
                if (!string.IsNullOrEmpty(cfg.webhook_file))
                {
                    string p = Path.Combine(UCL_RepoPath.AgentCommandsDir, "PromptQueue", cfg.webhook_file);
                    if (File.Exists(p))
                    {
                        foreach (var line in File.ReadAllLines(p))
                            if (!string.IsNullOrEmpty(line.Trim())) urls.Add(line.Trim());
                        if (urls.Count > 0) return urls;
                    }
                }
                foreach (var u in cfg.webhook_urls)
                    if (!string.IsNullOrEmpty(u)) urls.Add(u.Trim());
            }
            catch (Exception e) { Debug.LogWarning($"[TreasuryMirror] url resolve fail: {e.Message}"); }
            if (cfg.disabled_webhook_urls.Count == 0) return urls;
            var disabled = new HashSet<string>(cfg.disabled_webhook_urls, StringComparer.Ordinal);
            urls.RemoveAll(u => disabled.Contains(u));
            return urls;
        }

        // ===========================================================
        // 區塊職責：ledger entry → Discord embed JSON（port python notify_treasury._build_embed）
        // 物理意義：Tim 必要欄位 — 變動金額（title）/ 原因（description）/ 總額（balance fields）；
        //          進帳綠、出帳紅、其他黃；footer 帶 ts + uuid 可回查 ledger 檔。
        // ===========================================================
        const int COLOR_CREDIT = 0x2ECC71;
        const int COLOR_DEBIT = 0xE74C3C;
        const int COLOR_OTHER = 0xF1C40F;

        static string BuildEmbedJson(UCL.Core.JsonLib.JsonData entry)
        {
            string type = entry.GetString("type", "?");
            int amount = entry.GetInt("amount", 0);
            string currency = entry.GetString("currency", "tavern_token");
            string account = entry.GetString("account_id", "?");
            string srcKind = entry.GetString("source_kind", entry.GetString("use_kind", "?"));
            string srcRef = entry.GetString("source_ref", entry.GetString("use_ref", ""));
            string srcDesc = entry.GetString("source_description", entry.GetString("description", ""));
            string ts = entry.GetString("ts", "");
            string uuid = entry.GetString("uuid", "");
            string sigClaimed = entry.GetString("sig_agent_id_claimed", "?");
            string sigEnv = entry.GetString("sig_env_marker", "?");
            bool sigMismatch = entry.GetBool("signature_mismatch", false);

            string title; int color;
            if (type == "credit") { title = $"↑ 進帳 +{amount} {currency}"; color = COLOR_CREDIT; }
            else if (type == "debit") { title = $"↓ 出帳 -{amount} {currency}"; color = COLOR_DEBIT; }
            else { title = $"◇ {type} {amount} {currency}"; color = COLOR_OTHER; }

            string desc = srcDesc ?? "";
            if (sigMismatch) desc += (desc.Length > 0 ? "\n" : "") + "⚠ signature_mismatch — 簽章與帳戶不符，請查核";

            var sb = new StringBuilder();
            sb.Append("[{");
            sb.Append("\"title\":\"").Append(E(title)).Append("\",");
            if (!string.IsNullOrEmpty(desc)) sb.Append("\"description\":\"").Append(E(desc)).Append("\",");
            sb.Append("\"color\":").Append(color).Append(",");
            sb.Append("\"fields\":[");
            sb.Append(Field("account", account, true)).Append(",");
            sb.Append(Field("source", string.IsNullOrEmpty(srcRef) ? srcKind : $"{srcKind} · {srcRef}", true));
            // balance 欄位僅在 entry 內存在時附上（native 不做 python 的帳戶檔 fallback 推算）
            if (entry.Contains("balance_before") && entry.Contains("balance_after"))
            {
                sb.Append(",").Append(Field("balance", $"{entry.GetInt("balance_before", 0)} → {entry.GetInt("balance_after", 0)}", true));
            }
            sb.Append(",").Append(Field("actor", $"{sigClaimed} ({sigEnv})", true));
            sb.Append("],");
            sb.Append("\"footer\":{\"text\":\"").Append(E($"{ts} · {uuid}")).Append("\"}");
            sb.Append("}]");
            return sb.ToString();
        }

        static string Field(string name, string value, bool inline)
        {
            return $"{{\"name\":\"{E(name)}\",\"value\":\"{E(value)}\",\"inline\":{(inline ? "true" : "false")}}}";
        }

        // JSON 字串 escape（對齊 WebhookClient.BuildPayloadJson 的手動 escape 策略，免 JsonLib 依賴）
        static string E(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
#endif
