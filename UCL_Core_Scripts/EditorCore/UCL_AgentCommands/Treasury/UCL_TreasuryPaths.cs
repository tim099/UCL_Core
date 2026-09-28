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

        // 區塊職責：**新帳本根**（TASK-0274 遷移的落點）
        // 物理意義：2026-09-18 起金流權威是 `AgentCommands/Bank`；舊 `Treasury/` 只剩凍結歷史。
        //          單據（requests / transfer_requests）與設定此前**刻意留在舊路徑**，
        //          理由是「它們是單據不是帳」—— 而那讓系統停在半遷移狀態：
        //          🩸 2026-09-22 量到：刪舊目錄會一起刪掉還在用的請款單，
        //            而「凍結歷史」與「還活著的單據」在同一個資料夾底下**長得一模一樣**。
        // 數值影響：只換根，子目錄名與檔名形狀不動 ⇒ 搬檔就是 `git mv`。
        public const string BankDirRelative = "AgentCommands/Bank";

        public const string LedgerDirName = "ledger";
        public const string AccountsDirName = "accounts";
        public const string RulesFile = "rules.json";

        /// <summary>Treasury 根目錄 — 走可 override 的資料根 (UCL_AgentCommandsPath.DataRoot)。
        /// 2026-05-28 修正:原本用 UnityProjectRoot/.. 與其他子系統不一致 (nested layout 脆弱),
        /// 統一改走 ResolveData;預設模式 = RepoRoot/AgentCommands/Treasury,與舊 nested layout 結果相同。</summary>
        public static string GetTreasuryDir()
            => UCL_AgentCommandsPath.ResolveData(TreasuryDirRelative);

        /// <summary>新帳本根（`AgentCommands/Bank`）—— 單據與設定的落點，⛔ 不是凍結的 `Treasury/`。</summary>
        public static string GetBankDir()
            => UCL_AgentCommandsPath.ResolveData(BankDirRelative);

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

        // ⛔ 請款／轉帳單的路徑已隨開單一起搬到 Senate（`SCP_TreasuryRequests.PayoutDir`／`TransferDir`，TASK-0327）。

        public static void EnsureTreasuryDir()
        {
            Directory.CreateDirectory(GetTreasuryDir());
            Directory.CreateDirectory(GetLedgerRoot());
            Directory.CreateDirectory(GetAccountsRoot());
        }
    }
}
