// 區塊職責：券系統（繪圖券 / 酒館券）的 C# 端共用底層 — ISO 時戳 / 短 uuid / 原子讀改寫。
// 物理意義：兩種券的 schema 與儲存布局不同（繪圖券 = per-persona 檔 balance+history；
//          酒館券 = agent_bonus_quota.json 巢狀 agents.<bank>.personas.<persona>.total_remaining+history），
//          但「產生審計時戳/uuid、原子 tmp+replace 寫檔、讀檔或 init 空檔後 mutate 再寫回」這層機制完全一致。
//          抽出共用，消除原本在 UCL_CanvasVoucherLedger / UCL_TavernVoucherLedger / UCL_BankAdminPage
//          三處各自複製的 IsoNow/ShortUuid/AtomicWrite（Tim 2026-07-24：券通用邏輯抽離共用）。
// 設計取捨：schema 差異（balance 欄名 / 巢狀路徑 / history entry 格式）刻意「不」強行統一 —
//          由各 ledger 在 MutateFile 的 mutate 委派內自行處理；共用層只管「機制」不管「結構」。
//          無 Editor 依賴（純 file IO），對齊 UCL_CanvasVoucherLedger / UCL_TreasuryLedger 慣例不加 #if guard。
using System;
using System.Globalization;
using System.IO;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.AgentCommands.Voucher
{
    /// <summary>
    /// 券 ledger 共用底層 — 時戳 / uuid / 原子讀改寫。繪圖券與酒館券 ledger 共用，schema 差異由 caller 委派處理。
    /// </summary>
    public static class UCL_VoucherLedgerCommon
    {
        /// <summary>ISO 8601 UTC + ms — 對齊 canvas.py / work_session.py / Treasury 的 ts 格式。</summary>
        public static string IsoNow() =>
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z";

        /// <summary>6-char hex uuid — 對齊既有券 history 的 uuid 格式。</summary>
        public static string ShortUuid() => Guid.NewGuid().ToString("N").Substring(0, 6);

        /// <summary>
        /// 原子讀改寫：讀檔（或 init 空）→ mutate 委派就地改 → 原子 tmp+replace 寫回。
        /// 兩種券的 grant/consume 共用此骨架；schema 差異在 mutate 內處理。
        /// </summary>
        public static void MutateFile(string path, Func<JsonData> initEmpty, Action<JsonData> mutate)
        {
            JsonData d = File.Exists(path)
                ? JsonData.ParseJson(File.ReadAllText(path))
                : (initEmpty != null ? initEmpty() : JsonData.ParseJson("{}"));
            if (d == null) d = JsonData.ParseJson("{}");
            mutate(d);
            AtomicWrite(path, d.ToJsonBeautify());
        }

        /// <summary>原子寫入：tmp + replace，避免半寫檔（斷電 / 併發讀）。</summary>
        public static void AtomicWrite(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
