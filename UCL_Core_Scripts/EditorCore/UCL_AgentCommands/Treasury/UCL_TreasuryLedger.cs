// 區塊職責：T40 Treasury Ledger Static API
// 物理意義：append-only ledger 三大 op：Credit / Debit / GetBalance；replay 算餘額
// 數值影響：所有寫操作走本 module；CMD / IMGUI 都是 thin wrapper（per Plan §3 三層架構）
// 安全：actor_signature 偵測 env_marker 防盜用；不主動 reject mismatch（log warning + audit）

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Treasury
{
    public static class UCL_TreasuryLedger
    {
        // ==========================================================
        // 區塊職責：UUID6 生成 — 同 T38 PerMsgFile
        // ==========================================================
        static readonly RandomNumberGenerator s_Rng = RandomNumberGenerator.Create();
        static string GenerateUUID6()
        {
            byte[] buf = new byte[3];
            s_Rng.GetBytes(buf);
            return BitConverter.ToString(buf).Replace("-", "").ToLowerInvariant();
        }

        // ==========================================================
        // 區塊職責：偵測 env_marker — Claude Code / Antigravity / unknown
        // 物理意義：CMD server-side 從環境變數推測寫入者真實環境
        //          per Plan §2.6 — 不可信但提高難度（agent 要刻意 spoof）
        // 數值影響：env_marker 寫進 entry，事後 audit 查
        // ==========================================================
        public static string DetectEnvMarker()
        {
            // 1. Claude Code 的標誌環境變數
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDECODE")))
                return "claude-code";
            // 2. Antigravity（Gemini 系列）
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTIGRAVITY_SESSION"))
             || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTIGRAVITY_USER_ID")))
                return "antigravity";
            // 3. Gemini CLI
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
             || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_SESSION")))
                return "gemini";
            return "unknown";
        }

        // ==========================================================
        // 區塊職責：Credit — 進帳
        // 物理意義：寫一筆 credit entry；Tim grant / task_completion 等都走這
        // 數值影響：account 餘額 += amount；ledger 多一筆 entry
        // 邊界：amount 必 > 0；source_kind 必填
        // ==========================================================
        public static TreasuryLedgerEntry Credit(
            string accountId,
            int amount,
            string sourceKind,
            string sourceRef = null,
            string description = null,
            string callerAgentId = null,
            string cmdId = null)
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("accountId 必填");
            if (amount <= 0) throw new ArgumentException($"amount 必 > 0（傳入 {amount}）");
            if (string.IsNullOrEmpty(sourceKind)) throw new ArgumentException("sourceKind 必填");

            return WriteEntry(TreasuryEntryType.Credit, accountId, amount, sourceKind, sourceRef, description, callerAgentId, cmdId);
        }

        // ==========================================================
        // 區塊職責：Debit — 出帳
        // 物理意義：寫一筆 debit entry；tavern_post / bartender_drink 等
        // 數值影響：account 餘額 -= amount；餘額不足 → throw（per rules.json policies.negative_balance_allowed）
        // 安全：account_id 必須等於 callerAgentId（per Plan §2.5 帳戶隔離鐵律）
        //       例外：callerAgentId 為空 / "system" 視為系統內部呼叫，跳過驗
        // ==========================================================
        public static TreasuryLedgerEntry Debit(
            string accountId,
            int amount,
            string useKind,
            string useRef = null,
            string description = null,
            string callerAgentId = null,
            string cmdId = null)
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("accountId 必填");
            if (amount <= 0) throw new ArgumentException($"amount 必 > 0（傳入 {amount}）");
            if (string.IsNullOrEmpty(useKind)) throw new ArgumentException("useKind 必填");

            // 帳戶隔離鐵律：debit 時 caller 必須是自己 account
            if (!string.IsNullOrEmpty(callerAgentId) && callerAgentId != "system" && callerAgentId != accountId)
            {
                throw new InvalidOperationException(
                    $"[Treasury] 不可動用對方帳戶：callerAgentId={callerAgentId} 嘗試 debit accountId={accountId}");
            }

            // 餘額檢查
            int currentBalance = GetBalance(accountId);
            if (currentBalance < amount)
            {
                throw new InvalidOperationException(
                    $"[Treasury] {accountId} 餘額不足：當前 {currentBalance} < 請求 debit {amount}");
            }

            return WriteEntry(TreasuryEntryType.Debit, accountId, amount, useKind, useRef, description, callerAgentId, cmdId);
        }

        // ==========================================================
        // 區塊職責：寫 entry 共用底層
        // 物理意義：建 TreasuryLedgerEntry + 寫 .json 檔（沿用 T38 atomic per-file）
        // 數值影響：自動填 ts / uuid / sig_*；append entry 到 ledger/<date>/
        // ==========================================================
        static TreasuryLedgerEntry WriteEntry(
            TreasuryEntryType type,
            string accountId,
            int amount,
            string sourceKind,
            string sourceRef,
            string description,
            string callerAgentId,
            string cmdId)
        {
            UCL_TreasuryPaths.EnsureTreasuryDir();

            DateTime utcTime = DateTime.UtcNow;
            string uuid6 = GenerateUUID6();
            string typeStr = type == TreasuryEntryType.Credit ? "credit" : "debit";

            // balance before / after
            int balanceBefore = GetBalance(accountId);
            int balanceAfter = type == TreasuryEntryType.Credit
                ? balanceBefore + amount
                : balanceBefore - amount;

            // actor_signature
            string envMarker = DetectEnvMarker();
            string claimedAgent = string.IsNullOrEmpty(callerAgentId) ? accountId : callerAgentId;
            int processId = 0;
            try { processId = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { /* sandbox */ }

            // signature mismatch heuristic：
            //   claimedAgent 對應 envMarker 嗎？（claude-da-xiaojie ↔ claude-code / antigravity-da-xiaojie ↔ antigravity）
            bool sigMismatch = !MatchesEnvMarker(claimedAgent, envMarker);

            var entry = new TreasuryLedgerEntry
            {
                ts = utcTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                uuid = uuid6,
                type = typeStr,
                amount = amount,
                currency = "tavern_token",
                account_id = accountId,
                source_kind = sourceKind,
                source_ref = sourceRef ?? "",
                source_description = description ?? "",
                balance_before = balanceBefore,
                balance_after = balanceAfter,
                sig_agent_id_claimed = claimedAgent,
                sig_process_id = processId.ToString(),
                sig_env_marker = envMarker,
                sig_cmd_id = cmdId ?? "",
                signature_mismatch = sigMismatch,
            };

            // 寫檔
            string dateDir = UCL_TreasuryPaths.GetLedgerDateDir(utcTime);
            Directory.CreateDirectory(dateDir);
            string filename = UCL_TreasuryPaths.BuildEntryFileName(utcTime, uuid6, typeStr);
            string fullPath = Path.Combine(dateDir, filename);

            // 防撞檔（極罕見；UUID6 retry 10 次）
            int retry = 0;
            while (File.Exists(fullPath) && retry < 10)
            {
                uuid6 = GenerateUUID6();
                entry.uuid = uuid6;
                filename = UCL_TreasuryPaths.BuildEntryFileName(utcTime, uuid6, typeStr);
                fullPath = Path.Combine(dateDir, filename);
                retry++;
            }
            if (File.Exists(fullPath))
                throw new IOException($"[Treasury] 寫 entry 失敗 — 10 次 retry 仍撞檔：{fullPath}");

            File.WriteAllText(fullPath, SerializeEntry(entry), new UTF8Encoding(false));

            if (sigMismatch)
            {
                Debug.LogWarning(
                    $"[Treasury] ⚠ signature_mismatch entry written: " +
                    $"account={accountId} claimed={claimedAgent} env={envMarker} " +
                    $"({typeStr} {amount} for {sourceKind}). " +
                    $"Tim 可走 audit 查 — 本筆已寫但留標記。");
            }

            Debug.Log($"[Treasury] {typeStr} {amount} → account={accountId} (balance: {balanceBefore} → {balanceAfter})");

            // T41 — 寫完 entry fire-and-forget Discord broadcast
            // 物理意義：每筆 ledger 變動同步到 Discord 記帳頻道（embed: 變動金額 / 原因 / 總額）
            // 數值影響：fail swallow 不擋 ledger 主流程；spawn Python notify_treasury.py 跑 Discord POST
            try
            {
                FireDiscordBroadcastAsync(fullPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Treasury] FireDiscordBroadcast 失敗（ledger 已寫，僅 broadcast 跳過）：{ex.Message}");
            }

            return entry;
        }

        // ==========================================================
        // 區塊職責：T41 — fire-and-forget Discord broadcast
        // 物理意義：spawn Python notify_treasury.py --entry-file <path> 非阻塞跑
        // 數值影響：~1s 內 Discord 端收到 embed；C# 不等 process 完成
        // 邊界：notify_treasury.py 不存在 → silent skip（同 R6.6 TryFireDiscordTavernMirrorAsync 模式）
        // ==========================================================
        static void FireDiscordBroadcastAsync(string entryFilePath)
        {
            try
            {
                string scriptPath = Path.Combine(UCL_RepoPath.AgentCommandsDir, "PromptQueue/notify_treasury.py").Replace('\\', '/');
                if (!File.Exists(scriptPath)) return;   // notify 系統未安裝 silent skip

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"\"{scriptPath}\" --entry-file \"{entryFilePath}\" --quiet",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                };
                System.Diagnostics.Process.Start(psi);   // fire-and-forget；不 await
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Treasury] FireDiscordBroadcast spawn fail: {ex.Message}");
            }
        }

        // 簡單的 agent_id ↔ env_marker 對應檢查
        static bool MatchesEnvMarker(string agentId, string envMarker)
        {
            if (envMarker == "unknown") return true;   // 偵測不到不算 mismatch（避免 false positive）
            if (string.IsNullOrEmpty(agentId)) return true;
            string lowerId = agentId.ToLowerInvariant();
            switch (envMarker)
            {
                case "claude-code": return lowerId.Contains("claude");
                case "antigravity": return lowerId.Contains("antigravity") || lowerId.Contains("gemini");
                case "gemini":      return lowerId.Contains("gemini");
                default:            return true;
            }
        }

        // ==========================================================
        // 區塊職責：GetBalance — 對 account 跑 replay 算當下餘額
        // 物理意義：no mutable snapshot；reader 走全 ledger sort by ts
        // 數值影響：純讀無副作用
        // ==========================================================
        public static int GetBalance(string accountId, string currency = "tavern_token")
        {
            int balance = 0;
            foreach (var e in LoadAllEntries())
            {
                if (e.account_id != accountId) continue;
                if (e.currency != currency) continue;
                if (e.type == "credit") balance += e.amount;
                else if (e.type == "debit") balance -= e.amount;
            }
            return balance;
        }

        // ==========================================================
        // 區塊職責：Replay — 載入全部 ledger entries（sort by ts）
        // 物理意義：walk ledger/<date>/*.json + ordinal sort
        // 數值影響：純讀；caller 用來算 balance / audit
        // ==========================================================
        public static List<TreasuryLedgerEntry> LoadAllEntries()
        {
            var list = new List<TreasuryLedgerEntry>();
            string root = UCL_TreasuryPaths.GetLedgerRoot();
            if (!Directory.Exists(root)) return list;

            string[] files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);
            Array.Sort(files, (a, b) =>
            {
                string ra = a.Substring(root.Length).Replace('\\', '/');
                string rb = b.Substring(root.Length).Replace('\\', '/');
                return string.CompareOrdinal(ra, rb);
            });

            foreach (var f in files)
            {
                try
                {
                    var entry = ParseEntry(File.ReadAllText(f, Encoding.UTF8));
                    if (entry != null) list.Add(entry);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Treasury] Skipping malformed ledger entry {Path.GetFileName(f)}: {ex.Message}");
                }
            }
            return list;
        }

        // ==========================================================
        // 區塊職責：Audit — 給定 account 列其全 ledger entries
        // ==========================================================
        public static List<TreasuryLedgerEntry> Audit(string accountId, string sinceTs = null)
        {
            var all = LoadAllEntries();
            return all.Where(e =>
                e.account_id == accountId &&
                (string.IsNullOrEmpty(sinceTs) || string.CompareOrdinal(e.ts, sinceTs) > 0)
            ).ToList();
        }

        // ==========================================================
        // 區塊職責：JSON serialize / parse — 簡易手寫（同 ChatTavern 慣例）
        // ==========================================================
        public static string SerializeEntry(TreasuryLedgerEntry e)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"ts\":\"").Append(EscapeStr(e.ts)).Append("\"");
            sb.Append(",\"uuid\":\"").Append(EscapeStr(e.uuid)).Append("\"");
            sb.Append(",\"type\":\"").Append(EscapeStr(e.type)).Append("\"");
            sb.Append(",\"amount\":").Append(e.amount);
            sb.Append(",\"currency\":\"").Append(EscapeStr(e.currency)).Append("\"");
            sb.Append(",\"account_id\":\"").Append(EscapeStr(e.account_id)).Append("\"");
            sb.Append(",\"source_kind\":\"").Append(EscapeStr(e.source_kind)).Append("\"");
            sb.Append(",\"source_ref\":\"").Append(EscapeStr(e.source_ref)).Append("\"");
            sb.Append(",\"source_description\":\"").Append(EscapeStr(e.source_description)).Append("\"");
            sb.Append(",\"balance_before\":").Append(e.balance_before);
            sb.Append(",\"balance_after\":").Append(e.balance_after);
            sb.Append(",\"sig_agent_id_claimed\":\"").Append(EscapeStr(e.sig_agent_id_claimed)).Append("\"");
            sb.Append(",\"sig_process_id\":\"").Append(EscapeStr(e.sig_process_id)).Append("\"");
            sb.Append(",\"sig_env_marker\":\"").Append(EscapeStr(e.sig_env_marker)).Append("\"");
            sb.Append(",\"sig_cmd_id\":\"").Append(EscapeStr(e.sig_cmd_id)).Append("\"");
            sb.Append(",\"signature_mismatch\":").Append(e.signature_mismatch ? "true" : "false");
            sb.Append("}");
            return sb.ToString();
        }

        public static TreasuryLedgerEntry ParseEntry(string json)
        {
            // 簡易 parser — 用 regex 抽欄位（v1 prototype 可接受）
            var e = new TreasuryLedgerEntry();
            e.ts                    = ExtractStringField(json, "ts");
            e.uuid                  = ExtractStringField(json, "uuid");
            e.type                  = ExtractStringField(json, "type");
            e.amount                = ExtractIntField(json, "amount");
            e.currency              = ExtractStringField(json, "currency");
            e.account_id            = ExtractStringField(json, "account_id");
            e.source_kind           = ExtractStringField(json, "source_kind");
            e.source_ref            = ExtractStringField(json, "source_ref");
            e.source_description    = ExtractStringField(json, "source_description");
            e.balance_before        = ExtractIntField(json, "balance_before");
            e.balance_after         = ExtractIntField(json, "balance_after");
            e.sig_agent_id_claimed  = ExtractStringField(json, "sig_agent_id_claimed");
            e.sig_process_id        = ExtractStringField(json, "sig_process_id");
            e.sig_env_marker        = ExtractStringField(json, "sig_env_marker");
            e.sig_cmd_id            = ExtractStringField(json, "sig_cmd_id");
            e.signature_mismatch    = ExtractBoolField(json, "signature_mismatch");
            return e;
        }

        static string ExtractStringField(string json, string key)
        {
            string token = "\"" + key + "\":";
            int idx = json.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return "";
            int colon = idx + token.Length;
            // skip whitespace + opening quote
            while (colon < json.Length && (json[colon] == ' ' || json[colon] == '\t')) colon++;
            if (colon >= json.Length || json[colon] != '"') return "";
            int q1 = colon;
            int q2 = q1 + 1;
            while (q2 < json.Length && json[q2] != '"')
            {
                if (json[q2] == '\\' && q2 + 1 < json.Length) q2 += 2;
                else q2++;
            }
            if (q2 > q1) return UnescapeStr(json.Substring(q1 + 1, q2 - q1 - 1));
            return "";
        }

        static int ExtractIntField(string json, string key)
        {
            string token = "\"" + key + "\":";
            int idx = json.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return 0;
            int p = idx + token.Length;
            while (p < json.Length && (json[p] == ' ' || json[p] == '\t')) p++;
            int start = p;
            while (p < json.Length && (json[p] == '-' || (json[p] >= '0' && json[p] <= '9'))) p++;
            if (p == start) return 0;
            return int.TryParse(json.AsSpan(start, p - start), out var v) ? v : 0;
        }

        static bool ExtractBoolField(string json, string key)
        {
            string token = "\"" + key + "\":";
            int idx = json.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) return false;
            int p = idx + token.Length;
            while (p < json.Length && (json[p] == ' ' || json[p] == '\t')) p++;
            return p < json.Length && json[p] == 't';
        }

        static string EscapeStr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        static string UnescapeStr(string s)
        {
            if (string.IsNullOrEmpty(s) || !s.Contains('\\')) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char nx = s[++i];
                    switch (nx)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(nx); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
#endif
