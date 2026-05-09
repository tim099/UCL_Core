// 區塊職責：T40 Treasury — 路徑常數 + helper（仿 T38 PerMsgFile 模板）
// 物理意義：Per-entry file ledger 結構：
//          AgentCommands/Treasury/ledger/<YYYY-MM-DD>/<HHMMSS>_<MMM>_<UUID6>__<type>.json
// 數值影響：純常數 / Path 函式無副作用；caller 用本 helper 不必 hardcode 路徑

#if UNITY_EDITOR
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

        /// <summary>Treasury 根目錄 — repo-relative.</summary>
        public static string GetTreasuryDir()
            => Path.Combine(UCL_RepoPath.UnityProjectRoot, "..", TreasuryDirRelative).Replace('\\', '/');

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

        /// <summary>建構 ledger entry 檔名 — <HHMMSS>_<MMM>_<UUID6>__<type>.json</summary>
        public static string BuildEntryFileName(DateTime utcTime, string uuid6, string entryType)
        {
            string safeType = string.IsNullOrEmpty(entryType) ? "entry" : entryType.Replace("/", "_").Replace("\\", "_");
            return $"{utcTime:HHmmss_fff}_{uuid6}__{safeType}.json";
        }

        /// <summary>per-account snapshot cache 路徑</summary>
        public static string GetAccountSnapshotPath(string accountId)
            => Path.Combine(GetAccountsRoot(), $"{accountId}.snapshot.json");

        public static void EnsureTreasuryDir()
        {
            Directory.CreateDirectory(GetTreasuryDir());
            Directory.CreateDirectory(GetLedgerRoot());
            Directory.CreateDirectory(GetAccountsRoot());
        }
    }
}
#endif
