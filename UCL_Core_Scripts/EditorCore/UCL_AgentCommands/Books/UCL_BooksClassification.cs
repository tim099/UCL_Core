// 區塊職責：共享圖書館的**分類系統** —— 來源（origin）／種類（kind）／系列（series）三軸，
//          外加系列註冊表 `Books/_series.json` 的 typed model 與讀寫。
// 物理意義：把原本擠在 `_donation.json` 單一 `source` 欄位裡的兩件事拆開，並補上「這本書屬於哪個系列、第幾冊」。
// 數值影響：origin 決定**權限與帳務標籤**（publish 能不能覆寫、打賞的受益人叫作者還是捐贈者）；
//          kind 與 series 只影響**展示與檢索**，不動錢。
//
// 🩸 為什麼要拆（2026-08-19 meadow 實測）：
//   舊 `source` 同時扛兩役 —— 既是「這是什麼書」（authored / watch-log / 空=捐贈），
//   又是「能不能被 publish 覆寫」的閘（`source != "authored"` 就拒絕）。
//   於是 `watch-apocalypse-hotel`（source=watch-log）**永遠無法再版**，
//   而且在捐贈簿上被列進「📖 捐贈調入」—— 那本是 summit 自己寫的。
//   一個符號被要求同時扮演兩種語意，而消費端只認一種 ⇒ 修好一邊等於永久廢掉另一邊
//   （glossary: `一符二役` / one-symbol-two-duties）。
//
// 相容策略：**read-through lazy migration，不做雙寫**。
//   舊檔沒有 origin/kind 時由 `source` + slug 前綴推導（見 DeriveOrigin / DeriveKind）；
//   任何一次寫入（publish / donate / classify）會把推導結果**寫實**，之後不再推導。
//   `source` 欄位**仍然照舊寫出**，因為 python 端（library.py）還在讀它 ——
//   要拿掉得先改那邊，而在那之前拿掉等於靜默改變 wire format。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.AgentCommands.Books
{
    // ===========================================================
    // 區塊職責：來源 —— **誰把這本弄進圖書館的**（權限與帳務軸）。
    // 物理意義：只有兩種，因為只有兩條入庫路徑：自己寫（publish）或付錢調入（donate）。
    // 數值影響：publish 只准覆寫 Authored 的書；tip 的受益人標籤由它決定。
    //          ⚠ 這一軸**不准**再長出第三個值 —— 想加的東西應該加在 kind。
    // ===========================================================
    public enum UCL_BookOrigin
    {
        /// <summary>館內自己寫的（含觀影實錄、酒館史等編纂產物 —— 它們也是自己產的）。</summary>
        Authored,
        /// <summary>付 token 調入的外部作品。</summary>
        Donated,
    }

    // ===========================================================
    // 區塊職責：種類 —— **這是什麼書**（展示與檢索軸）。
    // 物理意義：純分類，可以長。新增種類時只要補這裡與 KindLabel，不影響任何權限判斷。
    // 數值影響：只影響捐贈簿的分組與排序。
    // ===========================================================
    public enum UCL_BookKind
    {
        /// <summary>原創著作（心得書、小說、散文集）。</summary>
        Original,
        /// <summary>外部作品（實體書 / 既有作品，付 token 調入）。</summary>
        External,
        /// <summary>觀影實錄（StreamWatch 收工匯出，酒館 seq 原文照收）。</summary>
        WatchLog,
        /// <summary>酒館史（某一天的酒館，Phase A 匯出 + Phase B 人工編纂）。</summary>
        TavernHistory,
    }

    // ===========================================================
    // 區塊職責：系列註冊表的一筆 —— 一個系列的身分。
    // 物理意義：對應 `Books/_series.json` 的一個元素。**欄位名即 JSON 鍵名**，
    //          刻意用小寫底線（與 `_donation.json` 同一套慣例，且 python 端讀得懂）。
    // 數值影響：純中繼資料。`parent` 讓系列可以巢狀（世界觀 > 三部曲 > 冊）。
    // ===========================================================
    public class UCL_BookSeriesEntry : UnityJsonSerializable
    {
        /// <summary>系列 id（kebab-case，書的 `series` 欄位指向它）。</summary>
        public string id = "";
        /// <summary>系列顯示名。</summary>
        public string title = "";
        /// <summary>上位系列 id；空＝頂層。例：`farseer-trilogy` 的 parent 是 `realm-of-the-elderlings`。</summary>
        public string parent = "";
        /// <summary>一句話說明（可空）。</summary>
        public string note = "";
    }

    public class UCL_BookSeriesRegistry : UnityJsonSerializable
    {
        public List<UCL_BookSeriesEntry> series = new List<UCL_BookSeriesEntry>();
    }

    // ===========================================================
    // 區塊職責：分類的推導、字串轉換與系列註冊表 IO。
    // 物理意義：所有「舊檔沒有新欄位時該算成什麼」的邏輯**只住在這裡一份** ——
    //          散到各消費端就會長出各自的推導，而它們不一致時兩邊都不會報錯。
    // ===========================================================
    public static class UCL_BooksClassification
    {
        public const string Key_Origin = "origin";
        public const string Key_Kind = "kind";
        public const string Key_Series = "series";
        public const string Key_Volume = "volume";

        /// <summary>酒館史的 slug 前綴（`history-<date>-<slug>`）—— 見 Tavern_History_Workflow.md。</summary>
        public const string HistorySlugPrefix = "history-";
        /// <summary>酒館史一律屬於同一個系列（Tim 2026-08-19：歷史書可以當成一整系列）。</summary>
        public const string SeriesTavernHistory = "tavern-history";
        /// <summary>觀影實錄的 slug 前綴（library.py export-watch 的產物）。</summary>
        public const string WatchSlugPrefix = "watch-";

        public static string SeriesRegistryPath => Path.Combine(UCL_BooksIO.BooksRoot, "_series.json");

        // -------- 字串 <-> 列舉（列舉一律用字串進出：JSON 有 python 讀取端，序號跨語言沒有意義）--------

        public static string ToKey(UCL_BookOrigin v) => v == UCL_BookOrigin.Donated ? "donated" : "authored";

        public static string ToKey(UCL_BookKind v)
        {
            switch (v)
            {
                case UCL_BookKind.External: return "external";
                case UCL_BookKind.WatchLog: return "watch-log";
                case UCL_BookKind.TavernHistory: return "tavern-history";
                default: return "original";
            }
        }

        public static bool TryParseKind(string s, out UCL_BookKind kind)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "original": kind = UCL_BookKind.Original; return true;
                case "external": kind = UCL_BookKind.External; return true;
                case "watch-log": case "watchlog": kind = UCL_BookKind.WatchLog; return true;
                case "tavern-history": case "history": kind = UCL_BookKind.TavernHistory; return true;
                default: kind = UCL_BookKind.Original; return false;
            }
        }

        public static string AllKindKeys => "original|external|watch-log|tavern-history";

        public static string KindLabel(UCL_BookKind k)
        {
            switch (k)
            {
                case UCL_BookKind.External: return "📖 外部作品（付 token 調入）";
                case UCL_BookKind.WatchLog: return "📺 觀影實錄";
                case UCL_BookKind.TavernHistory: return "🏛 酒館史";
                default: return "✍ 原創著作";
            }
        }

        // -------- read-through 推導 --------

        /// <summary>
        /// 取這本書的 origin。有 `origin` 欄就用它；沒有才由 legacy `source` 推導。
        /// 物理意義：舊檔的 `source` 只有 "authored" 代表館內自產，其餘（含空字串、watch-log）語意混雜 ——
        ///          而 watch-log 明明是自產的，舊邏輯把它算成捐贈，這裡修正。
        /// </summary>
        public static UCL_BookOrigin DeriveOrigin(JsonData d, string slug)
        {
            string o = d.GetString(Key_Origin, "");
            if (!string.IsNullOrEmpty(o))
                return o == "donated" ? UCL_BookOrigin.Donated : UCL_BookOrigin.Authored;

            string src = d.GetString(UCL_BooksIO.Key_Source, "");
            // 空 source ＝ 舊的捐贈登記（donate 從來不寫 source）。
            if (string.IsNullOrEmpty(src)) return UCL_BookOrigin.Donated;
            // authored / watch-log / 任何館內流程產出的標記 ⇒ 都是自產。
            return UCL_BookOrigin.Authored;
        }

        /// <summary>
        /// 取這本書的 kind。有 `kind` 欄就用它；沒有才依序由 legacy `source` → slug 前綴 → origin 推導。
        /// 數值影響：slug 前綴是**最後一道**，因為它是慣例不是宣告 —— 但沒有它的話，
        ///          已經發表的酒館史與觀影實錄要等到有人手動 classify 才會歸位。
        /// </summary>
        public static UCL_BookKind DeriveKind(JsonData d, string slug)
        {
            if (TryParseKind(d.GetString(Key_Kind, ""), out var k)) return k;

            string src = d.GetString(UCL_BooksIO.Key_Source, "");
            if (src == "watch-log") return UCL_BookKind.WatchLog;
            if (src == "tavern-history") return UCL_BookKind.TavernHistory;

            slug = slug ?? "";
            if (slug.StartsWith(HistorySlugPrefix, StringComparison.Ordinal)) return UCL_BookKind.TavernHistory;
            if (slug.StartsWith(WatchSlugPrefix, StringComparison.Ordinal)) return UCL_BookKind.WatchLog;

            return DeriveOrigin(d, slug) == UCL_BookOrigin.Donated ? UCL_BookKind.External : UCL_BookKind.Original;
        }

        /// <summary>系列 id：有 `series` 欄就用它；沒有時酒館史自動歸 `tavern-history`（那是全系列的定義）。</summary>
        public static string DeriveSeries(JsonData d, string slug)
        {
            string s = d.GetString(Key_Series, "").Trim();
            if (!string.IsNullOrEmpty(s)) return s;
            if ((slug ?? "").StartsWith(HistorySlugPrefix, StringComparison.Ordinal)) return SeriesTavernHistory;
            return "";
        }

        /// <summary>冊次；0＝未指定（顯示時退回用 slug 排序，酒館史的 `history-YYYY-MM-DD` 天生就排得對）。</summary>
        public static int DeriveVolume(JsonData d) => d.GetInt(Key_Volume, 0);

        /// <summary>把推導出來的三軸寫實進 entry（任何一次寫入都呼叫它 —— 推導只做一次）。</summary>
        public static void Stamp(JsonData entry, string slug, UCL_BookOrigin origin, UCL_BookKind kind,
                                 string series, int volume)
        {
            entry[Key_Origin] = ToKey(origin);
            entry[Key_Kind] = ToKey(kind);
            entry[Key_Series] = series ?? "";
            entry[Key_Volume] = volume;
        }

        // -------- 系列註冊表 --------

        /// <summary>讀 `Books/_series.json`；不存在或壞檔回空表並回報原因（fail-soft 要出聲）。</summary>
        public static UCL_BookSeriesRegistry LoadSeries(out string error)
        {
            error = null;
            var reg = new UCL_BookSeriesRegistry();
            string p = SeriesRegistryPath;
            if (!File.Exists(p)) return reg;
            try
            {
                var json = JsonData.ParseJson(File.ReadAllText(p));
                reg.DeserializeFromJson(json);
                if (reg.series == null) reg.series = new List<UCL_BookSeriesEntry>();
            }
            catch (Exception e)
            {
                error = $"_series.json 讀取失敗：{e.Message}";
                return new UCL_BookSeriesRegistry();
            }
            return reg;
        }

        /// <summary>
        /// 寫 `_series.json`。**刻意走 UCL_BooksIO.SaveJson**（非 ASCII 還原成原生 UTF-8），
        /// 不用 ToJsonBeautify 直出 —— 同一個資料夾裡的 `_donation.json` 是原生中文，
        /// 而逃脫版在 git diff 與 python 讀取端都是另一種形狀。
        /// 🩸 同族前科：registry 家族被兩個序列化器輪流整檔重寫（BUG-6，2026-08-19 才收斂）。
        /// </summary>
        public static void SaveSeries(UCL_BookSeriesRegistry reg)
        {
            UCL_BooksIO.SaveJson(SeriesRegistryPath, reg.SerializeToJson());
        }

        public static UCL_BookSeriesEntry FindSeries(UCL_BookSeriesRegistry reg, string id)
        {
            if (reg?.series == null || string.IsNullOrEmpty(id)) return null;
            return reg.series.Find(s => s.id == id);
        }

        /// <summary>系列的顯示路徑：`世界觀 › 三部曲`（巢狀時逐層往上串，最多 4 層防環）。</summary>
        public static string SeriesPath(UCL_BookSeriesRegistry reg, string id)
        {
            var names = new List<string>();
            string cur = id;
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(cur); i++)
            {
                var e = FindSeries(reg, cur);
                if (e == null) { names.Insert(0, cur); break; }   // 未註冊就印 id 本身，不假裝它有名字
                names.Insert(0, string.IsNullOrEmpty(e.title) ? e.id : e.title);
                cur = e.parent;
            }
            return string.Join(" › ", names);
        }
    }
}
#endif
