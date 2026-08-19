// 區塊職責：藏書架 —— 依**系列**呈現圖書館，以及設定某本書的分類（kind / series / volume）。
// 物理意義：`op=shelf` 是總覽（系列一行、幾冊）、`op=series` 是某一系列的書單（含 **book id 供閱讀**）、
//          `op=classify` 是唯一的分類寫入通道。
// 數值影響：shelf / series 唯讀；classify 只改 `_donation.json` 的三個分類欄位與 `_series.json`，**不動錢**。
//
// 設計決策（Tim 2026-08-19）：
//   · **沒有系列的書單獨列出 —— 等於一本一系列。** 這讓總覽只有一種列，不必分「系列區」與「散書區」，
//     而讀者要的資訊（有哪些東西可讀、各幾冊）在同一張表上讀得完。
//   · 系列可巢狀（世界觀 › 三部曲 › 冊），巢狀路徑由 `_series.json` 的 parent 串出來。
//   · 酒館史（`history-*`）天生同屬一個系列 —— 不必逐本 classify 就會歸位（見 DeriveSeries）。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;

namespace UCL.Core.EditorLib.AgentCommands.Books
{
    // ===========================================================
    // 區塊職責：一本書在架上的樣子（唯讀投影）。
    // 物理意義：由 `_donation.json` 讀出 + read-through 推導三軸，**不寫回**。
    // ===========================================================
    public class UCL_ShelfBook
    {
        public string book;          // slug ＝ 閱讀用的 id
        public string title;
        public string persona;       // 作者或捐贈者
        public int chapters;
        public string date;
        public UCL_BookOrigin origin;
        public UCL_BookKind kind;
        public string series;        // 空＝沒有系列（自成一系列）
        public int volume;           // 0＝未指定，排序退回 slug
    }

    public static class UCL_BooksShelf
    {
        /// <summary>讀出全部藏書的架上投影（壞檔列進 warnings，不靜默吞）。</summary>
        public static List<UCL_ShelfBook> LoadShelf(List<string> warnings = null)
        {
            var o = new List<UCL_ShelfBook>();
            foreach (var d in UCL_BooksIO.LoadDonations(warnings))
            {
                string slug = d.GetString(UCL_BooksIO.Key_Book, "?");
                o.Add(new UCL_ShelfBook
                {
                    book = slug,
                    title = d.GetString(UCL_BooksIO.Key_Title, slug),
                    persona = d.GetString(UCL_BooksIO.Key_DonorPersona, d.GetString(UCL_BooksIO.Key_Donor, "?")),
                    chapters = d.GetInt(UCL_BooksIO.Key_Chapters, 0),
                    date = d.GetString(UCL_BooksIO.Key_PublishedAt, d.GetString(UCL_BooksIO.Key_DonatedAt, "?")),
                    origin = UCL_BooksClassification.DeriveOrigin(d, slug),
                    kind = UCL_BooksClassification.DeriveKind(d, slug),
                    series = UCL_BooksClassification.DeriveSeries(d, slug),
                    volume = UCL_BooksClassification.DeriveVolume(d),
                });
            }
            return o;
        }

        /// <summary>同一系列內的排序：先 volume（0 排最後），再 slug —— `history-YYYY-MM-DD` 天生就排得對。</summary>
        static int CompareInSeries(UCL_ShelfBook a, UCL_ShelfBook b)
        {
            int va = a.volume <= 0 ? int.MaxValue : a.volume;
            int vb = b.volume <= 0 ? int.MaxValue : b.volume;
            if (va != vb) return va.CompareTo(vb);
            return string.CompareOrdinal(a.book, b.book);
        }

        /// <summary>把書分組成「系列」——**沒有系列的書自成一組**（Tim：等於一本一系列）。</summary>
        public static List<KeyValuePair<string, List<UCL_ShelfBook>>> GroupBySeries(List<UCL_ShelfBook> books)
        {
            var map = new Dictionary<string, List<UCL_ShelfBook>>();
            var order = new List<string>();
            foreach (var b in books)
            {
                // 單本系列用 `#<slug>` 當內部鍵，跟真正的 series id 分得開（不會撞名）
                string key = string.IsNullOrEmpty(b.series) ? "#" + b.book : b.series;
                if (!map.TryGetValue(key, out var lst))
                {
                    lst = new List<UCL_ShelfBook>();
                    map[key] = lst;
                    order.Add(key);
                }
                lst.Add(b);
            }
            var o = new List<KeyValuePair<string, List<UCL_ShelfBook>>>();
            foreach (var k in order)
            {
                map[k].Sort(CompareInSeries);
                o.Add(new KeyValuePair<string, List<UCL_ShelfBook>>(k, map[k]));
            }
            // 多冊的系列排前面（那是「系列」這個概念真正有用的地方），其次照名稱
            o.Sort((x, y) =>
            {
                bool mx = x.Value.Count > 1, my = y.Value.Count > 1;
                if (mx != my) return my.CompareTo(mx);
                if (mx && x.Value.Count != y.Value.Count) return y.Value.Count.CompareTo(x.Value.Count);
                return string.CompareOrdinal(x.Key, y.Key);
            });
            return o;
        }

        // ===========================================================
        // op=shelf —— 藏書總覽：一列一個系列（單書亦然），標明幾冊
        // ===========================================================
        public static string RenderShelf(string kindFilter)
        {
            var warnings = new List<string>();
            var books = LoadShelf(warnings);
            var reg = UCL_BooksClassification.LoadSeries(out string regErr);

            if (!string.IsNullOrEmpty(kindFilter))
            {
                if (!UCL_BooksClassification.TryParseKind(kindFilter, out var kf))
                    return $"❌ 未知 kind：{kindFilter}（可用：{UCL_BooksClassification.AllKindKeys}）";
                books = books.FindAll(b => b.kind == kf);
            }

            var sb = new StringBuilder();
            if (books.Count == 0)
            {
                sb.AppendLine("（架上沒有符合條件的書）");
                return sb.ToString();
            }
            var groups = GroupBySeries(books);
            int multi = groups.FindAll(g => g.Value.Count > 1).Count;
            // 壞檔數要出現在數字旁邊 —— 「共 N 本」靜默吸收讀不到的列，跟「讀空目錄不報錯」同族
            string failNote = warnings.Count > 0 ? $"，另有 {warnings.Count} 筆讀取失敗 ⚠ 見文末" : "";
            sb.AppendLine($"📚 藏書架 — 共 {books.Count} 本／{groups.Count} 個系列"
                          + $"（其中 {multi} 個是多冊系列，其餘為單本自成一系列）{failNote}");
            sb.AppendLine();

            foreach (var g in groups)
            {
                var first = g.Value[0];
                bool single = g.Value.Count == 1 && string.IsNullOrEmpty(first.series);
                string name = single
                    ? $"《{first.title}》"
                    : UCL_BooksClassification.SeriesPath(reg, g.Key);
                sb.AppendLine($"- {UCL_BooksClassification.KindLabel(first.kind)}　**{name}**"
                              + $"（目前共 {g.Value.Count} 冊）");
                if (!single)
                    sb.AppendLine($"    查書單：`run Books --arg op=series --arg series={g.Key}`");
                else
                    sb.AppendLine($"    id `{first.book}`　{first.persona}／{first.chapters} 章／{first.date}");
            }

            if (!string.IsNullOrEmpty(regErr)) sb.AppendLine($"\n⚠ {regErr}");
            if (warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("⚠ 讀取失敗：");
                foreach (var w in warnings) sb.AppendLine($"- {w}");
            }
            return sb.ToString();
        }

        // ===========================================================
        // op=series —— 不帶 series：列所有已註冊系列；帶 series：列該系列的書單（含閱讀用 id）
        // ===========================================================
        public static string RenderSeries(string seriesId)
        {
            var warnings = new List<string>();
            var books = LoadShelf(warnings);
            var reg = UCL_BooksClassification.LoadSeries(out string regErr);
            var sb = new StringBuilder();

            if (string.IsNullOrEmpty(seriesId))
            {
                var groups = GroupBySeries(books).FindAll(g => !g.Key.StartsWith("#", StringComparison.Ordinal));
                sb.AppendLine($"📚 已成系列的書（{groups.Count} 個）— 單本自成一系列的請走 `op=shelf`");
                sb.AppendLine();
                foreach (var g in groups)
                    sb.AppendLine($"- `{g.Key}` **{UCL_BooksClassification.SeriesPath(reg, g.Key)}**"
                                  + $"（目前共 {g.Value.Count} 冊）");
                // 註冊了但架上一本都沒有的系列也要列 —— 否則它跟「不存在」長得一樣
                if (reg.series != null)
                {
                    var empty = reg.series.FindAll(e => groups.Find(g => g.Key == e.id).Value == null);
                    if (empty.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("（已註冊但架上尚無書的系列）");
                        foreach (var e in empty) sb.AppendLine($"- `{e.id}` {e.title}（0 冊）");
                    }
                }
                if (!string.IsNullOrEmpty(regErr)) sb.AppendLine($"\n⚠ {regErr}");
                return sb.ToString();
            }

            var mine = books.FindAll(b => b.series == seriesId);
            mine.Sort(CompareInSeries);
            var entry = UCL_BooksClassification.FindSeries(reg, seriesId);
            if (mine.Count == 0 && entry == null)
                return $"❌ 沒有這個系列：`{seriesId}` —— 用 `op=series`（不帶參數）看有哪些。";

            sb.AppendLine($"📚 **{UCL_BooksClassification.SeriesPath(reg, seriesId)}**"
                          + $"（`{seriesId}`，目前共 {mine.Count} 冊）");
            if (entry != null && !string.IsNullOrEmpty(entry.note)) sb.AppendLine($"> {entry.note}");
            sb.AppendLine();
            if (mine.Count == 0)
            {
                // 空表格讀起來像「這個系列沒有書」—— 而上位系列的書其實掛在子系列上。
                // 明講「本層 0 冊」，讓讀者往下看子系列。
                sb.AppendLine("（本層直接掛 0 冊 —— 書可能掛在下面的子系列上）");
            }
            else
            {
                sb.AppendLine("| 冊 | id（閱讀用） | 書名 | 作者／捐贈者 | 章 | 日期 |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var b in mine)
                    sb.AppendLine($"| {(b.volume > 0 ? b.volume.ToString() : "—")} | `{b.book}` | 《{b.title}》 "
                                  + $"| {b.persona} | {b.chapters} | {b.date} |");
                sb.AppendLine();
                sb.AppendLine($"全文在 `AgentCommands/Books/<id>/`。");
            }

            // 子系列（巢狀）：列出來，否則上位系列看起來是空的
            if (reg.series != null)
            {
                var children = reg.series.FindAll(e => e.parent == seriesId);
                if (children.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("子系列：");
                    foreach (var c in children)
                        sb.AppendLine($"- `{c.id}` {c.title}"
                                      + $"（{books.FindAll(b => b.series == c.id).Count} 冊）");
                }
            }
            if (!string.IsNullOrEmpty(regErr)) sb.AppendLine($"\n⚠ {regErr}");
            return sb.ToString();
        }

        // ===========================================================
        // op=classify —— 唯一的分類寫入通道
        // 數值影響：只改 _donation.json 的 kind/series/volume（＋補寫 origin）與 _series.json；不動錢。
        // ===========================================================
        public static string Classify(string book, string kindRaw, string series, string volumeRaw,
                                      string seriesTitle, string parentSeries, string parentSeriesTitle,
                                      string seriesNote, out string error)
        {
            error = null;
            string dpath = UCL_BooksIO.DonationPath(book);
            if (!File.Exists(dpath))
            {
                error = $"《{book}》不在登記簿（{dpath} 不存在）—— 先 publish / donate 才能分類";
                return null;
            }
            JsonData d = UCL_BooksIO.LoadJson(dpath, out string err);
            if (d == null) { error = $"讀取 _donation.json 失敗：{err}"; return null; }

            var origin = UCL_BooksClassification.DeriveOrigin(d, book);
            var kind = UCL_BooksClassification.DeriveKind(d, book);
            if (!string.IsNullOrEmpty(kindRaw) && !UCL_BooksClassification.TryParseKind(kindRaw, out kind))
            {
                error = $"未知 kind：{kindRaw}（可用：{UCL_BooksClassification.AllKindKeys}）";
                return null;
            }

            string newSeries = UCL_BooksClassification.DeriveSeries(d, book);
            if (series != null) newSeries = series.Trim();   // 顯式傳空字串＝脫離系列

            int volume = UCL_BooksClassification.DeriveVolume(d);
            if (!string.IsNullOrEmpty(volumeRaw))
            {
                if (!int.TryParse(volumeRaw, out volume) || volume < 0)
                {
                    error = $"volume 須為 ≥0 的整數（傳入 {volumeRaw}）";
                    return null;
                }
            }

            // 系列註冊：沒註冊過就必須給 title —— 不給就擋。
            // 🩸 理由：自動用 id 當 title 的話，打錯字會長出一個「看起來正常的新系列」，
            //   而它跟真正的新系列在畫面上一模一樣。
            var reg = UCL_BooksClassification.LoadSeries(out string regErr);
            if (!string.IsNullOrEmpty(regErr)) { error = regErr; return null; }
            string regNote = "";
            if (!string.IsNullOrEmpty(newSeries))
            {
                var e = UCL_BooksClassification.FindSeries(reg, newSeries);
                if (e == null)
                {
                    if (string.IsNullOrEmpty(seriesTitle))
                    {
                        error = $"系列 `{newSeries}` 尚未註冊 —— 首次使用要帶 --arg series_title=<系列顯示名>"
                                + "（不自動用 id 當名字：打錯字會長出一個看起來正常的新系列）";
                        return null;
                    }
                    e = new UCL_BookSeriesEntry { id = newSeries, title = seriesTitle };
                    reg.series.Add(e);
                    regNote = $"（新註冊系列 `{newSeries}` = {seriesTitle}）";
                }
                else if (!string.IsNullOrEmpty(seriesTitle) && e.title != seriesTitle)
                {
                    e.title = seriesTitle;
                    regNote = $"（系列 `{newSeries}` 更名為 {seriesTitle}）";
                }
                if (parentSeries != null)
                {
                    string pid = parentSeries.Trim();
                    // 上位系列同樣要有名字才准掛 —— 否則巢狀路徑會印出一個裸 id，
                    // 而那跟「這個系列真的叫這個名字」在畫面上分不出來。
                    if (!string.IsNullOrEmpty(pid) && UCL_BooksClassification.FindSeries(reg, pid) == null)
                    {
                        if (string.IsNullOrEmpty(parentSeriesTitle))
                        {
                            error = $"上位系列 `{pid}` 尚未註冊 —— 要帶 --arg parent_series_title=<顯示名>";
                            return null;
                        }
                        reg.series.Add(new UCL_BookSeriesEntry { id = pid, title = parentSeriesTitle });
                    }
                    e.parent = pid;
                }
                if (!string.IsNullOrEmpty(seriesNote)) e.note = seriesNote;
                UCL_BooksClassification.SaveSeries(reg);
            }

            UCL_BooksClassification.Stamp(d, book, origin, kind, newSeries, volume);
            UCL_BooksIO.SaveJson(dpath, d);

            // 印 ✓ 不算數 —— 回讀落地的檔案再報
            JsonData back = UCL_BooksIO.LoadJson(dpath, out _);
            string sTxt = string.IsNullOrEmpty(newSeries)
                ? "（無系列，自成一系列）"
                : $"{UCL_BooksClassification.SeriesPath(reg, newSeries)}　第 {(volume > 0 ? volume.ToString() : "—")} 冊";
            return $"✅ 《{d.GetString(UCL_BooksIO.Key_Title, book)}》分類完成 {regNote}\n"
                   + $"- id：`{book}`\n"
                   + $"- origin：`{back.GetString(UCL_BooksClassification.Key_Origin, "?")}`"
                   + $"　kind：`{back.GetString(UCL_BooksClassification.Key_Kind, "?")}`\n"
                   + $"- 系列：{sTxt}\n"
                   + $"（回讀 `_donation.json` 確認，非記憶體值）";
        }
    }
}
#endif
