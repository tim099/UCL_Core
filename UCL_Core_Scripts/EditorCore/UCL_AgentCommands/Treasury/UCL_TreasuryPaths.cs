// 區塊職責：T40 Treasury — 路徑常數 + helper（仿 T38 PerMsgFile 模板）
// 物理意義：Per-entry file ledger 結構：
//          AgentCommands/Treasury/ledger/<YYYY-MM-DD>/<HHMMSS>_<MMM>_<UUID6>__<type>.json
// 數值影響：純常數 / Path 函式無副作用；caller 用本 helper 不必 hardcode 路徑

// 2026-05-13 (Zeta): 去掉 #if UNITY_EDITOR guard — 純 path helper 無 Editor 依賴.
using System;
using System.IO;

namespace UCL.Core.EditorLib.AgentCommands.Treasury
{
    /// <summary>
    /// T40 Treasury 路徑常數 + helper.
    /// 對應 Python 端未來的 _lib/treasury_paths.py（v2 ship 時加）。
    /// </summary>
    public static class UCL_TreasuryPaths
    {
        public const string TreasuryDirRelative = "AgentCommands/Treasury";
        public const string LedgerDirName = "ledger";
        public const string AccountsDirName = "accounts";
        public const string RulesFile = "rules.json";
        // 區塊職責：請款單（payout request）存放目錄名
        // 物理意義：請款單是「還沒發生的錢」—— 它跟 ledger 是兩種東西：ledger 記已成事實的收付，
        //          請款單記「某 agent 主張該收一筆錢」。核准後才會生出對應的 ledger entry。
        //          兩者刻意分開存：混進 ledger 會讓「帳面餘額」包含未核准的主張，那是假帳。
        // 數值影響：路徑 = <Treasury>/requests/<YYYY-MM-DD>/<HHMMSS_fff>_<UUID6>__request.json
        public const string RequestsDirName = "requests";

        /// <summary>Treasury 根目錄 — 走可 override 的資料根 (UCL_AgentCommandsPath.DataRoot)。
        /// 2026-05-28 修正:原本用 UnityProjectRoot/.. 與其他子系統不一致 (nested layout 脆弱),
        /// 統一改走 ResolveData;預設模式 = RepoRoot/AgentCommands/Treasury,與舊 nested layout 結果相同。</summary>
        public static string GetTreasuryDir()
            => UCL_AgentCommandsPath.ResolveData(TreasuryDirRelative);

        /// <summary>ledger/ 根目錄</summary>
        public static string GetLedgerRoot()
            => Path.Combine(GetTreasuryDir(), LedgerDirName);

        /// <summary>accounts/ 根目錄（balance snapshot cache）</summary>
        public static string GetAccountsRoot()
            => Path.Combine(GetTreasuryDir(), AccountsDirName);

        /// <summary>rules.json 路徑</summary>
        public static string GetRulesPath()
            => Path.Combine(GetTreasuryDir(), RulesFile);

        /// <summary>per-day ledger 子目錄（per T38 風格按日分桶）</summary>
        public static string GetLedgerDateDir(DateTime utcDate)
            => Path.Combine(GetLedgerRoot(), utcDate.ToString("yyyy-MM-dd"));

        // ── 每日結帳（Daily Closing）2026-08-04 ──────────────────────────────
        // 物理意義：一個 UTC 日一份，內容是「**含**該日全部 entry 之後」的各帳戶餘額。
        //          餘額 = 最近一份結帳 + 該日之後的 entry，於是讀取成本從 O(全部歷史) 變成 O(今日)。
        // ⚠ 日期一律用 **UTC**，跟 ledger 日期夾同一套曆 —— 兩邊用不同曆會讓結帳邊界與檔案位置
        //   對不上，症狀是「餘額偶爾差一點，而且只在半夜出現」。
        public const string ClosingDirName = "closing";

        public static string GetClosingRoot()
            => Path.Combine(GetTreasuryDir(), ClosingDirName);

        /// <summary>某個 UTC 日的結帳檔路徑。</summary>
        public static string GetClosingPath(string utcDateKey)
            => Path.Combine(GetClosingRoot(), $"{utcDateKey}.json");

        /// <summary>UTC 日期字串（結帳檔與 ledger 日期夾共用的唯一格式）。</summary>
        public static string DateKey(DateTime utc) => utc.ToString("yyyy-MM-dd");

        /// <summary>建構 ledger entry 檔名 — <HHMMSS>_<MMM>_<UUID6>__<type>.json</summary>
        public static string BuildEntryFileName(DateTime utcTime, string uuid6, string entryType)
        {
            string safeType = string.IsNullOrEmpty(entryType) ? "entry" : entryType.Replace("/", "_").Replace("\\", "_");
            return $"{utcTime:HHmmss_fff}_{uuid6}__{safeType}.json";
        }

        /// <summary>per-account snapshot cache 路徑</summary>
        public static string GetAccountSnapshotPath(string accountId)
            => Path.Combine(GetAccountsRoot(), $"{accountId}.snapshot.json");

        /// <summary>請款單根目錄（<Treasury>/requests）。</summary>
        public static string GetRequestsRoot()
            => Path.Combine(GetTreasuryDir(), RequestsDirName);

        /// <summary>請款單的當日分桶目錄 — 與 ledger 同構（按日分桶，避免單一目錄千檔）。</summary>
        public static string GetRequestDateDir(DateTime utcDate)
            => Path.Combine(GetRequestsRoot(), utcDate.ToString("yyyy-MM-dd"));

        /// <summary>建構請款單檔名 — <HHMMSS_fff>_<UUID6>__request.json（沿用 ledger 的檔名形狀）。</summary>
        public static string BuildRequestFileName(DateTime utcTime, string uuid6)
            => $"{utcTime:HHmmss_fff}_{uuid6}__request.json";

        // ── 轉帳單（2026-08-04）——與請款單同構，另開目錄以免兩種單混在一起難分辨 ──
        public const string TransfersDirName = "transfer_requests";

        public static string GetTransferRequestsRoot()
            => Path.Combine(GetTreasuryDir(), TransfersDirName);

        public static string GetTransferRequestDateDir(DateTime utcDate)
            => Path.Combine(GetTransferRequestsRoot(), utcDate.ToString("yyyy-MM-dd"));

        public static string BuildTransferRequestFileName(DateTime utcTime, string uuid6)
            => $"{utcTime:HHmmss_fff}_{uuid6}__transfer.json";

        public static void EnsureTreasuryDir()
        {
            Directory.CreateDirectory(GetTreasuryDir());
            Directory.CreateDirectory(GetLedgerRoot());
            Directory.CreateDirectory(GetAccountsRoot());
        }
    }
}
