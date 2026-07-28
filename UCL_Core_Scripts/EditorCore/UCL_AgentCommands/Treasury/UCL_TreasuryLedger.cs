// 區塊職責：T40 Treasury Ledger Static API
// 物理意義：append-only ledger 三大 op：Credit / Debit / GetBalance；replay 算餘額
// 數值影響：所有寫操作走本 module；CMD / IMGUI 都是 thin wrapper（per Plan §3 三層架構）
// 安全：actor_signature 偵測 env_marker 防盜用；不主動 reject mismatch（log warning + audit）
// 修法 2026-05-11 (Tim QA TreasuryEnvMarker): caller-side detect thread-through CurrentCallerEnvMarker slot

// 2026-05-13 (Zeta): 去掉 #if UNITY_EDITOR guard — 純 file IO + crypto + replay 邏輯, 無 Editor 依賴.
// deps (RepoPath / TreasuryPaths) 已同步 strip guard.
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
        // 區塊職責: Caller env_marker thread-through slot (Tim 2026-05-11 QA Bug fix TreasuryEnvMarker)
        // 物理意義: caller-side (Python run_cmd.py) 抓得到 CLAUDECODE / ANTIGRAVITY_SESSION 等 env vars,
        //          長期 Unity Editor process 抓不到 — UCL_AgentCommandRunner 開 cmd 前從 args["_caller_env_marker"]
        //          設這個 slot, DetectEnvMarker 優先讀; 沒設 → fallback in-process detect (legacy 行為)
        // 數值影響: 整體 cmd 執行生命週期讀同一個值; finally clear 避免 cross-cmd leak
        public static string CurrentCallerEnvMarker { get; set; }

        // ==========================================================
        // 區塊職責：偵測 env_marker — Claude Code / Antigravity / Gemini / unknown
        // 物理意義：optional caller-passed slot 優先 (Phase 1 修法); fallback in-process env detect (legacy)
        // 數值影響：env_marker 寫進 ledger entry，事後 audit 查
        // ==========================================================
        public static string DetectEnvMarker()
        {
            // Phase 1 (Tim 2026-05-11 QA fix): 優先用 caller-side detect (Python 抓 env vars 傳進 args)
            if (!string.IsNullOrEmpty(CurrentCallerEnvMarker))
                return CurrentCallerEnvMarker;

            // Fallback: in-process detect (legacy, 直寫 queue.json 沒走 Python 時的容錯)
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDECODE")))
                return "claude-code";
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTIGRAVITY_SESSION"))
             || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTIGRAVITY_USER_ID")))
                return "antigravity";
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

            // 區塊職責：新 entry 直接餵 balance 快取（不等下次 GetBalance 列舉才發現）
            // 物理意義：entry 檔 immutable, 寫出當下即可安全計入；順手推進 watermark + 回寫 snapshot,
            //          讓「寫完就關 Editor」的場景下次冷啟動也能熱啟。
            lock (s_BalanceCacheLock)
            {
                if (s_ProcessedEntryPaths.Add(fullPath))
                {
                    ApplyEntryToCache_NoLock(entry);
                    string rel = fullPath.Substring(UCL_TreasuryPaths.GetLedgerRoot().Length).Replace('\\', '/');
                    if (string.CompareOrdinal(rel, s_MaxProcessedRelPath) > 0) s_MaxProcessedRelPath = rel;
                    SaveSnapshot_NoLock();
                }
            }

            if (sigMismatch)
            {
                Debug.LogWarning(
                    $"[Treasury] ⚠ signature_mismatch entry written: " +
                    $"account={accountId} claimed={claimedAgent} env={envMarker} " +
                    $"({typeStr} {amount} for {sourceKind}). " +
                    $"Tim 可走 audit 查 — 本筆已寫但留標記。");
            }

            Debug.Log($"[Treasury] {typeStr} {amount} → account={accountId} (balance: {balanceBefore} → {balanceAfter})");

            // Discord broadcast 不在此觸發 (2026-07-28 python 路徑移除)：ledger 是 append-only 事件流，
            // UCL_DiscordTreasuryMirror 以 cursor pull 撿走新 entry（冪等、漏送可補），寫入端零 spawn。
            return entry;
        }


        // ==========================================================
        // 區塊職責：agent_id ↔ env_marker 對應檢查 — 判定本筆 ledger entry 該不該被標 signature_mismatch
        // 物理意義：caller env 是 Python run_cmd.py 開的 Claude / Antigravity / Gemini env；
        //          claimedAgent 是寫進 ledger 的 sig_agent_id_claimed 欄位（誰自稱發了這筆）
        // 數值影響：return true → signature_mismatch=false (信任本筆)；return false → 標 mismatch + Discord ⚠
        // 邊界：
        //   - envMarker = "unknown" → 偵測不到 env (e.g. Editor 直寫 queue.json) 不算 mismatch
        //   - agentId 為空 → 沒 claim 不算 mismatch
        //   - agentId = "system" → **合法 wildcard** (Tim 2026-05-12 QA Bug fix TreasuryWorkPostSysMismatch)
        //     work_post / token_parse 等 Op_Post hook 走 internal auto-credit, 必須傳 callerAgentId="system"
        //     以區隔 user-initiated credit. "system" 跟任何 env 都不該被視為 mismatch — 否則所有 auto-credit
        //     都會被誤標 ⚠, 真正的 false positive 反被淹沒 (250+ 筆歷史 entries 已遭此污染)
        // ==========================================================
        static bool MatchesEnvMarker(string agentId, string envMarker)
        {
            if (envMarker == "unknown") return true;   // 偵測不到不算 mismatch（避免 false positive）
            if (string.IsNullOrEmpty(agentId)) return true;
            string lowerId = agentId.ToLowerInvariant();

            // T+ QA fix: "system" 是合法 internal caller (Op_Post auto-credit hooks 用)
            //            不該被誤判 mismatch — 跟任何 env_marker 都視為相容
            if (lowerId == "system") return true;

            switch (envMarker)
            {
                case "claude-code": return lowerId.Contains("claude");
                case "antigravity": return lowerId.Contains("antigravity") || lowerId.Contains("gemini");
                case "gemini":      return lowerId.Contains("gemini");
                default:            return true;
            }
        }

        // ==========================================================
        // 區塊職責：Balance 增量快取（path-keyed）+ 落盤 snapshot — GetBalance 效能修復
        // 物理意義：ledger entry 檔是 append-only / immutable（寫出後永不改寫）→
        //          每檔的餘額貢獻可以永久快取。原版 GetBalance 每次呼叫 LoadAllEntries
        //          全量 read+parse 整個 ledger（實測 10,000+ 檔 / 14MB）——ChatTavernPage
        //          每 2 秒刷餘額、每筆 post 的 auto-debit 又各叫 2 次，等於主執行緒
        //          反覆全帳本重放；冷碟 + 防毒逐檔掃描時第一次就是數十秒卡頓
        //          （2026-07-28 Tim 回報「初開 40 秒 + 開啟後嚴重卡頓」的根因）。
        // 修法（跟 ChatTavern PerMsgFile parse cache 同構）：
        //   1) 記憶體層：balances dict + 已處理檔案集合；每次 GetBalance 只「列舉路徑」
        //      （便宜, 不讀內容, 跨 process 新檔照樣看到），只對沒見過的新檔 read+parse。
        //   2) 落盤層：accounts/_balances.snapshot.txt 存（watermark 相對路徑 + 已處理檔數
        //      + 各帳戶餘額）。domain reload / Editor 重啟後冷啟動只需 parse「watermark 之後」
        //      的新檔——把一次性冷成本從全帳本壓到增量。
        // 正確性防線（外觀 OK ≠ 真的 OK 家族自檢）：
        //   - 餘額是加法交換律安全的（credit/debit 求和與順序無關），不依賴 sort。
        //   - snapshot 載入時驗證「≤ watermark 的現存檔數 == 記錄的檔數」，不符（手動刪檔 /
        //     git 操作動到舊 entry）→ 整組丟棄走全量重放，寧慢不錯。
        //   - 記憶體層偵測到「已處理數 > 現存檔數」（檔案消失）→ 同樣整組重建。
        //   - git merge 補進「舊日期」entry：記憶體層列舉自然涵蓋（不在已處理集合 → 會被 parse）；
        //     snapshot 層由 ≤watermark 檔數驗證擋下。
        //   - snapshot 讀不到 / 格式壞 → 靜默走全量重放（慢但正確, 永不錯帳）。
        // 數值影響：GetBalance 由 O(全帳本 read+parse) 降到 O(列舉) + O(新增檔)；
        //          無新 entry 時 0 次讀檔內容。
        // ==========================================================
        static readonly object s_BalanceCacheLock = new object();
        // 已處理的 entry 檔（full path）；含壞檔（parse 失敗也記，不重複讀不重複洗 log）
        static readonly HashSet<string> s_ProcessedEntryPaths = new HashSet<string>();
        // 各帳戶餘額 — key = accountId + "\n" + currency（\n 不可能出現在 id / currency 內）
        static readonly Dictionary<string, int> s_BalanceCache = new Dictionary<string, int>();
        // 已處理檔中字典序最大的 root-relative path — snapshot watermark 用
        static string s_MaxProcessedRelPath = "";
        // snapshot 是否已嘗試載入（每個 domain reload 生命週期只試一次）
        static bool s_SnapshotLoadAttempted = false;

        const string BalanceSnapshotFileName = "_balances.snapshot.txt";
        static string GetBalanceSnapshotPath()
            => Path.Combine(UCL_TreasuryPaths.GetAccountsRoot(), BalanceSnapshotFileName);

        static string BalanceKey(string accountId, string currency)
            => accountId + "\n" + (string.IsNullOrEmpty(currency) ? "tavern_token" : currency);

        /// <summary>清空 balance 快取（記憶體 + 落盤 snapshot）。
        /// 手動修改 / 刪除 ledger entry 檔後呼叫，強制下次 GetBalance 全量重放。</summary>
        public static void InvalidateBalanceCache()
        {
            lock (s_BalanceCacheLock)
            {
                s_ProcessedEntryPaths.Clear();
                s_BalanceCache.Clear();
                s_MaxProcessedRelPath = "";
                s_SnapshotLoadAttempted = true;   // 別再把剛失效的 snapshot 撿回來
                try
                {
                    string snap = GetBalanceSnapshotPath();
                    if (File.Exists(snap)) File.Delete(snap);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Treasury] InvalidateBalanceCache 刪 snapshot 失敗（不影響正確性, 下次載入會被驗證擋下）：{ex.Message}");
                }
            }
        }

        // 區塊職責：把單筆 entry 的餘額貢獻累進快取（呼叫端必須已持有 s_BalanceCacheLock）
        // 物理意義：credit 加 / debit 減；其他 type 忽略（與原版 GetBalance 邏輯一致）
        static void ApplyEntryToCache_NoLock(TreasuryLedgerEntry e)
        {
            if (e == null || string.IsNullOrEmpty(e.account_id)) return;
            string key = BalanceKey(e.account_id, e.currency);
            s_BalanceCache.TryGetValue(key, out int bal);
            if (e.type == "credit") bal += e.amount;
            else if (e.type == "debit") bal -= e.amount;
            else return;   // 未知 type 不動餘額也不建 key
            s_BalanceCache[key] = bal;
        }

        // 區塊職責：同步快取到磁碟現況（呼叫端必須已持有 s_BalanceCacheLock）
        // 物理意義：列舉 ledger 全部 entry 路徑 → 首次先試 snapshot 熱啟 → 只 parse 沒見過的新檔。
        // 數值影響：更新 s_BalanceCache / s_ProcessedEntryPaths / s_MaxProcessedRelPath；
        //          有新檔被 parse 時回寫 snapshot（下次冷啟動直接接力）。
        static void SyncBalanceCache_NoLock()
        {
            string root = UCL_TreasuryPaths.GetLedgerRoot();
            if (!Directory.Exists(root))
            {
                // ledger 根目錄不存在 = 沒有任何帳 — 對齊原版行為（全部餘額 0）
                s_ProcessedEntryPaths.Clear();
                s_BalanceCache.Clear();
                s_MaxProcessedRelPath = "";
                return;
            }

            string[] files = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories);

            // 冷啟動：先試 snapshot 熱啟（只有本 domain reload 週期第一次進來才試）
            if (!s_SnapshotLoadAttempted)
            {
                s_SnapshotLoadAttempted = true;
                TryLoadSnapshot_NoLock(root, files);
            }

            // 防線：檔案消失（歸檔 / 手動刪 / git 操作）→ 增量前提破裂 → 整組重建
            if (s_ProcessedEntryPaths.Count > files.Length)
            {
                Debug.LogWarning(
                    $"[Treasury] balance 快取偵測到 ledger 檔數縮水（cached={s_ProcessedEntryPaths.Count} > disk={files.Length}）" +
                    $"— 增量前提破裂, 整組重建（全量重放一次）。");
                s_ProcessedEntryPaths.Clear();
                s_BalanceCache.Clear();
                s_MaxProcessedRelPath = "";
            }

            int newProcessed = 0;
            foreach (var f in files)
            {
                if (!s_ProcessedEntryPaths.Add(f)) continue;   // 已處理（含壞檔）
                newProcessed++;
                try
                {
                    var entry = ParseEntry(File.ReadAllText(f, Encoding.UTF8));
                    ApplyEntryToCache_NoLock(entry);
                }
                catch (Exception ex)
                {
                    // 壞檔：已記入 processed 集合（不重複讀）；對齊原版 LoadAllEntries skip 行為
                    Debug.LogError($"[Treasury] balance 快取 skip malformed ledger entry {Path.GetFileName(f)}: {ex.Message}");
                }
                string rel = f.Substring(root.Length).Replace('\\', '/');
                if (string.CompareOrdinal(rel, s_MaxProcessedRelPath) > 0) s_MaxProcessedRelPath = rel;
            }

            // 有新檔被消化 → 回寫 snapshot（讓下次 domain reload / Editor 重啟直接熱啟）
            if (newProcessed > 0)
            {
                SaveSnapshot_NoLock();
            }
        }

        // 區塊職責：載入落盤 snapshot 並驗證（呼叫端必須已持有 s_BalanceCacheLock）
        // 物理意義：snapshot 宣稱「≤ watermark 的檔已全部算進 balances, 共 count 個」。
        //          載入前用現存檔案清單覆核該宣稱：≤ watermark 的現存檔數必須恰好等於 count，
        //          多了（merge 補舊檔）或少了（刪檔）都整組丟棄走全量重放 — 寧慢不錯。
        // 檔案格式（純文字, 行分隔, 免 JSON escape 麻煩）：
        //   line 1: watermark=<root-relative path>
        //   line 2: count=<int>
        //   line 3+: <accountId>\t<currency>\t<balance>
        static void TryLoadSnapshot_NoLock(string root, string[] files)
        {
            string snapPath = GetBalanceSnapshotPath();
            if (!File.Exists(snapPath)) return;

            string watermark = null;
            int count = -1;
            var balances = new List<(string key, int bal)>();
            try
            {
                foreach (var line in File.ReadAllLines(snapPath, Encoding.UTF8))
                {
                    if (line.StartsWith("watermark=", StringComparison.Ordinal))
                        watermark = line.Substring("watermark=".Length);
                    else if (line.StartsWith("count=", StringComparison.Ordinal))
                        int.TryParse(line.Substring("count=".Length), out count);
                    else if (line.Contains('\t'))
                    {
                        var parts = line.Split('\t');
                        if (parts.Length == 3 && int.TryParse(parts[2], out int bal))
                            balances.Add((BalanceKey(parts[0], parts[1]), bal));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Treasury] balance snapshot 讀取失敗 — 改走全量重放：{ex.Message}");
                return;
            }
            if (string.IsNullOrEmpty(watermark) || count < 0) return;   // 格式不完整 → 丟棄

            // 覆核宣稱：現存檔中 rel path ≤ watermark 的檔數必須 == count
            var covered = new List<string>(count);
            foreach (var f in files)
            {
                string rel = f.Substring(root.Length).Replace('\\', '/');
                if (string.CompareOrdinal(rel, watermark) <= 0) covered.Add(f);
            }
            if (covered.Count != count)
            {
                Debug.LogWarning(
                    $"[Treasury] balance snapshot 驗證失敗（≤watermark 現存檔數 {covered.Count} != 記錄 {count}）" +
                    $"— ledger 歷史被動過（刪檔 / merge 補檔）, 丟棄 snapshot 走全量重放。");
                return;
            }

            // 驗證通過 → 熱啟：watermark 內的檔標為已處理（不 parse）, 餘額直接繼承
            foreach (var f in covered) s_ProcessedEntryPaths.Add(f);
            foreach (var (key, bal) in balances) s_BalanceCache[key] = bal;
            s_MaxProcessedRelPath = watermark;
        }

        // 區塊職責：回寫落盤 snapshot（呼叫端必須已持有 s_BalanceCacheLock）
        // 物理意義：快取當下狀態 = 「≤ s_MaxProcessedRelPath 的 s_ProcessedEntryPaths.Count 個檔
        //          已全數計入 s_BalanceCache」— 寫壞了也只是下次驗證失敗走全量, 不會錯帳。
        static void SaveSnapshot_NoLock()
        {
            try
            {
                var sb = new StringBuilder(256 + s_BalanceCache.Count * 48);
                sb.Append("watermark=").Append(s_MaxProcessedRelPath).Append('\n');
                sb.Append("count=").Append(s_ProcessedEntryPaths.Count).Append('\n');
                foreach (var kv in s_BalanceCache)
                {
                    int nl = kv.Key.IndexOf('\n');
                    if (nl < 0) continue;   // 防禦：key 必為 account\ncurrency
                    sb.Append(kv.Key, 0, nl).Append('\t')
                      .Append(kv.Key, nl + 1, kv.Key.Length - nl - 1).Append('\t')
                      .Append(kv.Value).Append('\n');
                }
                Directory.CreateDirectory(UCL_TreasuryPaths.GetAccountsRoot());
                File.WriteAllText(GetBalanceSnapshotPath(), sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // snapshot 寫失敗不影響正確性（記憶體快取仍對）, 只是下次冷啟動慢
                Debug.LogWarning($"[Treasury] balance snapshot 寫入失敗（不影響本次餘額正確性）：{ex.Message}");
            }
        }

        // ==========================================================
        // 區塊職責：GetBalance — 增量快取版（語意等價原版全帳本 replay）
        // 物理意義：先同步快取到磁碟現況（只 parse 新檔）, 再 O(1) 查表。
        //          餘額 = credit 總和 - debit 總和, 加法交換律 → 與 parse 順序無關,
        //          與原版「sort by ts 後逐筆累加」結果完全一致。
        // 數值影響：純讀無副作用（除快取熱化 / snapshot 回寫）。
        // ==========================================================
        public static int GetBalance(string accountId, string currency = "tavern_token")
        {
            lock (s_BalanceCacheLock)
            {
                SyncBalanceCache_NoLock();
                return s_BalanceCache.TryGetValue(BalanceKey(accountId, currency), out int bal) ? bal : 0;
            }
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
