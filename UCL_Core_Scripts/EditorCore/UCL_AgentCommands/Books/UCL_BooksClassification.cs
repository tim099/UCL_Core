// 區塊職責：分類三軸（origin／kind／series）的 **Editor 薄殼** ＋ 系列註冊表 `Books/_series.json` 的讀寫。
// 物理意義：推導規則與顯示字串**已經搬到** `SCP.Core.Books.SCP_BooksClassification`（TASK-0234 ①）——
//          本檔剩下的職責只有兩件：把 `JsonData` 的欄位值讀出來餵給它、以及 registry 的檔案 IO。
// 數值影響：零。本次搬遷是**等值移動**，改前／改後 `op=donations|shelf|series` 三支輸出逐位元組相同
//          （唯一差異是回傳檔裡的 `cmd_id` 註解行 —— 那是每次執行都會變的）。
//
// 🩸 為什麼列舉沒有留一份在這裡：原本的 `UCL_BookOrigin` / `UCL_BookKind` 已刪除，
//   全庫 8 個引用點改指 `SCP_BookOrigin` / `SCP_BookKind`。
//   留兩份「長得一樣的列舉」看起來比較安全，實際上是造一個**不會編譯錯的漂移點**：
//   任何一邊多一個成員，另一邊不會紅，而對應表會安靜地少一格。
//
// 🩸 為什麼 `LoadSeries` / `SaveSeries` **沒有**跟著搬（basecamp 2026-09-17，寫在 TASK-0234 上）：
//   寫入端會改變檔案形狀（非 ASCII 逃脫與否、縮排版面），同族前科是 BUG-6 ——
//   registry 被兩個序列化器輪流整檔重寫，逐鍵相同、逐位元組不同，而整批翻紅時沒有一層會喊。
//   ⇒ 那一格要自己帶「寫出來的檔逐位元組對拍」才准動，不搭這一批的便車。
//
// 相容策略：**read-through lazy migration，不做雙寫**（規則本體見 SCP 那份的檔頭）。
//   ⚠ **2026-09-18 更正（TASK-0166 ⑤ 全消費端對帳）**：本段原本寫
//   「`source` 欄位仍然照舊寫出，因為 python 端（library.py）還在讀它」——
//   **那個前提已經死了**：`library.py` 早已整支退場（79 行 exit-2 stub，零功能零副作用）。
//   現況是 `source` **不再新寫出**（2026-09-04 起，`SCP_BooksOps` 也一樣）；
//   舊檔留著的 `source` 照讀不動（`DeriveOrigin` 仍認它），只是不再新增。
//   🩸 留這句話當記號：**一條理由的前提消失之後，那條理由讀起來完全沒變** ——
//     而它會讓下一個人以為「不能拿掉」是一個還活著的限制。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Books;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.AgentCommands.Books
{
    // ===========================================================
    // 區塊職責：系列註冊表的一筆 —— 這裡是 **JSON model**（欄位名即 JSON 鍵名）。
    // 物理意義：對應 `Books/_series.json` 的一個元素。刻意用小寫底線（與 `_donation.json` 同一套慣例，
    //          且 python 端讀得懂）。⛔ 不要改成 PascalCase 去對齊 SCP 那個計算用的 view ——
    //          那會靜默改變 wire format。
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

    public static class UCL_BooksClassification
    {
        // 常數**轉發**而不是各留一份字面值 —— 兩份字面值不一致時，讀錯欄位的症狀是「這本書沒分類」，
        // 跟「這本書真的還沒分類」完全同形。
        public const string Key_Origin = SCP_BooksClassification.Key_Origin;
        public const string Key_Kind = SCP_BooksClassification.Key_Kind;
        public const string Key_Series = SCP_BooksClassification.Key_Series;
        public const string Key_Volume = SCP_BooksClassification.Key_Volume;

        public const string HistorySlugPrefix = SCP_BooksClassification.HistorySlugPrefix;
        public const string SeriesTavernHistory = SCP_BooksClassification.SeriesTavernHistory;
        public const string WatchSlugPrefix = SCP_BooksClassification.WatchSlugPrefix;

        public static string SeriesRegistryPath => Path.Combine(UCL_BooksIO.BooksRoot, "_series.json");

        // -------- 字串 <-> 列舉：⤷ 薄殼（TASK-0234 ①）--------

        public static string ToKey(SCP_BookOrigin v) => SCP_BooksClassification.ToKey(v);

        public static string ToKey(SCP_BookKind v) => SCP_BooksClassification.ToKey(v);

        public static bool TryParseKind(string s, out SCP_BookKind kind)
            => SCP_BooksClassification.TryParseKind(s, out kind);

        public static string AllKindKeys => SCP_BooksClassification.AllKindKeys;

        public static string KindLabel(SCP_BookKind k) => SCP_BooksClassification.KindLabel(k);

        // -------- read-through 推導：⤷ 薄殼；本層只負責把欄位值從 JsonData 讀出來 --------

        /// <summary>取這本書的 origin（規則住 <see cref="SCP_BooksClassification.DeriveOrigin"/>）。</summary>
        public static SCP_BookOrigin DeriveOrigin(JsonData d, string slug)
            => SCP_BooksClassification.DeriveOrigin(
                d.GetString(Key_Origin, ""), d.GetString(UCL_BooksIO.Key_Source, ""));

        /// <summary>取這本書的 kind（規則住 <see cref="SCP_BooksClassification.DeriveKind"/>）。</summary>
        public static SCP_BookKind DeriveKind(JsonData d, string slug)
            => SCP_BooksClassification.DeriveKind(
                d.GetString(Key_Kind, ""), d.GetString(UCL_BooksIO.Key_Source, ""),
                slug, d.GetString(Key_Origin, ""));

        /// <summary>系列 id（規則住 <see cref="SCP_BooksClassification.DeriveSeries"/>）。</summary>
        public static string DeriveSeries(JsonData d, string slug)
            => SCP_BooksClassification.DeriveSeries(d.GetString(Key_Series, ""), slug);

        /// <summary>冊次；0＝未指定（顯示時退回用 slug 排序，酒館史的 `history-YYYY-MM-DD` 天生就排得對）。</summary>
        public static int DeriveVolume(JsonData d) => d.GetInt(Key_Volume, 0);

        /// <summary>把推導出來的三軸寫實進 entry（任何一次寫入都呼叫它 —— 推導只做一次）。</summary>
        public static void Stamp(JsonData entry, string slug, SCP_BookOrigin origin, SCP_BookKind kind,
                                 string series, int volume)
        {
            entry[Key_Origin] = ToKey(origin);
            entry[Key_Kind] = ToKey(kind);
            entry[Key_Series] = series ?? "";
            entry[Key_Volume] = volume;
        }

        // -------- 系列註冊表（IO 留在本層；查詢走 SCP）--------

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

        /// <summary>
        /// 系列的顯示路徑：`世界觀 › 三部曲` —— ⤷ 薄殼，走訪與防環規則住
        /// <see cref="SCP_BooksClassification.SeriesPath"/>；本層只把 JSON model 投影成計算用的 view。
        /// </summary>
        public static string SeriesPath(UCL_BookSeriesRegistry reg, string id)
            => SCP_BooksClassification.SeriesPath(Project(reg), id);

        /// <summary>JSON model → SCP 計算用 view。⛔ 逐筆照抄，不在這裡過濾或排序（那會是第二套規則）。</summary>
        static List<SCP_BookSeriesEntry> Project(UCL_BookSeriesRegistry reg)
        {
            var aOut = new List<SCP_BookSeriesEntry>();
            if (reg?.series == null) return aOut;
            foreach (var e in reg.series)
            {
                if (e == null) continue;
                aOut.Add(new SCP_BookSeriesEntry
                {
                    Id = e.id ?? "",
                    Title = e.title ?? "",
                    Parent = e.parent ?? "",
                    Note = e.note ?? "",
                });
            }
            return aOut;
        }
    }
}
#endif
