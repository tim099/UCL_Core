// 區塊職責：共享圖書館（AgentCommands/Books/）簿冊的唯一 schema 實作者 —— 捐贈簿 / 打賞簿 / 出版登記。
// 物理意義：Books/ 放 agent 寫或捐的書全文（<slug>/NNN.txt）；每本書的捐贈登記是
//          <slug>/_donation.json（per-book 檔即事實源，T-BOOKS-STORAGE Phase B）；
//          打賞是 tips/<stamp>_<persona>_<tipid>.json（per-entry append-only，Phase A）。
//          本類是這兩種簿冊唯一允許的讀寫入口（Cmd_Books 與任何頁面都呼叫這裡）——
//          兩個寫入端各自理解 schema 就是 facts/aliases 兩形狀病的土壤。
// 數值影響：⤷ **三個動錢的動作已於 2026-09-18 整段搬進 `SCP.Core.Books.SCP_BooksOps`**（TASK-0166 ①）——
//          本層只剩薄殼與讀取層。錢走 `UCL_BooksGateway` → `UCL_TreasuryLedger.Pay`
//          （主動消費**自動先扣酒館券**，不足的才扣 token）；券走 `UCL_VoucherAuthority`。
//          Publish 不動錢（寫作是勞動產出非消費，tokens=0）。
//          ⚠ 舊註解寫「直呼 Ledger」「Tip 發雙券（UCL_CanvasVoucherLedger / UCL_TavernVoucherLedger）」——
//          那兩句現在都不成立了（兩個 ledger 自己也是薄殼）。
//
// 設計決策（2026-08-07 Tim 拍板「實作全在 C#，Python 只透過 Cmd 操作」）：
//   · 取代 library.py 經濟六件 —— 那邊的 debit 本來就 spawn run_cmd 回到 C#，
//     這裡直呼 Ledger 之後，Python 版「跨層驗證 ledger」的防禦碼整段消失
//     （in-process 拿到 entry 物件，沒有 stdout 可不可信的問題）。
//   · Debit 帶 idempotencyKey（donate=book、tip=tip_id）—— 重試不重扣。
//   · Publish **不從**舊 BookNotes/<slug>/book.json 推導身分：首次發表需顯式 title + persona；
//     再版沿用 _donation.json 既有登記。⚠ 但發表**之後會回寫**那份檔的 status/publish_status
//     （TASK-0148）—— 「不依賴它判斷」與「不必同步它」是兩件事，而它們曾被寫成同一件。
//     🩸 舊註解寫「那個 store 已空」—— **那句話是錯的**（2026-09-06 實測：157 份 book.json、
//        活的 6 份、其中 3 本 status=writing、5 天內寫過 2 次）。錯的前提讓「不回寫」
//        看起來像一個想清楚的決定，於是沒有人回頭問它。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Books;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core.EditorLib.AgentCommands.Books
{
    public static class UCL_BooksIO
    {
        public const string Key_Book = "book";
        public const string Key_Title = "title";
        public const string Key_Donor = "donor";
        public const string Key_DonorPersona = "donor_persona";
        public const string Key_DonorAgent = "donor_agent";
        public const string Key_Tokens = "tokens";
        public const string Key_Source = "source";
        public const string Key_Chapters = "chapters";
        public const string Key_DonatedAt = "donated_at";
        public const string Key_PublishedAt = "published_at";
        public const string Key_Note = "note";

        // 打賞匯率（Tim 2026-06-11 拍板 1+1：1 token → 繪圖券 1 張 + 酒館券 1 張）
        public const int TipCanvasRate = 1;
        public const int TipTavernRate = 1;
        // 對齊 Treasury max_per_transfer 上限
        public const int TipMax = 1000;
        // 捐贈基礎價（Tim 可用 tokens 參數覆寫優惠價）
        public const int DonationBasePrice = 100;

        public static string BooksRoot => Path.Combine(UCL_RepoPath.AgentCommandsDir, "Books");
        public static string BookDir(string book) => Path.Combine(BooksRoot, book);
        public static string DonationPath(string book) => Path.Combine(BookDir(book), "_donation.json");

        // 舊草稿 store：`BookNotes/<slug>/book.json`（`origin` / `status` / `publish_status` 住在這裡）。
        // ⚠ 這**不是** `BookNotes/Library/`（那是新 store，另一套 schema、另一個鍵）。
        public static string BookNotesRoot => Path.Combine(UCL_RepoPath.AgentCommandsDir, "BookNotes");
        public static string BookNotesJsonPath(string book) => Path.Combine(BookNotesRoot, book, "book.json");
        public static string TipsDir => Path.Combine(BooksRoot, "tips");

        // ===========================================================
        // 讀取層
        // ===========================================================

        /// <summary>捐贈登記聚合：glob 各書 _donation.json（per-book 檔即事實源；壞檔略過但列進 warnings）。</summary>
        public static List<JsonData> LoadDonations(List<string> warnings = null)
        {
            var o = new List<JsonData>();
            // ⤷ **清單規則走 SCP**（TASK-0234 ①）：「掃哪個目錄、收哪些檔、依什麼排序」只住一份。
            //   ⛔ parse 沒有跟著搬 —— 本層回傳的 `JsonData` 還要被 Donate/Publish/Tip 拿去**改欄位**，
            //   那是 Editor 方言的東西；跨接縫傳 JSON 物件會逼下一個人二選一。
            foreach (string dir in SCP.Core.Books.SCP_BooksDonations.DonationDirs(UCL_RepoPath.AgentCommandsDir))
            {
                string p = Path.Combine(dir, "_donation.json");
                JsonData d = LoadJson(p, out string err);
                if (d == null)
                {
                    warnings?.Add($"`{Path.GetFileName(dir)}/_donation.json` 讀取失敗：{err}");
                    continue;
                }
                if (!d.Contains(Key_Book)) d[Key_Book] = Path.GetFileName(dir);   // 缺欄用資料夾名兜底
                o.Add(d);
            }
            return o;
        }

        /// <summary>打賞簿聚合：glob tips/*.json（檔名≈時間序；壞檔略過但列進 warnings）。</summary>
        public static List<JsonData> LoadTips(List<string> warnings = null)
        {
            var o = new List<JsonData>();
            // ⤷ 清單規則走 SCP（同 LoadDonations）
            foreach (string f in SCP.Core.Books.SCP_BooksDonations.TipFiles(UCL_RepoPath.AgentCommandsDir))
            {
                JsonData d = LoadJson(f, out string err);
                if (d == null) { warnings?.Add($"`tips/{Path.GetFileName(f)}` 讀取失敗：{err}"); continue; }
                o.Add(d);
            }
            return o;
        }

        // ===========================================================
        // 區塊職責：三個**會動錢的動作** —— ⤷ **薄殼**（TASK-0166 ①，2026-09-18）。
        // 物理意義：本體整段住 `SCP.Core.Books.SCP_BooksOps`，Editor 與 Senate CLI
        //          **共用同一份實作**；本層只負責把宿主能力（錢／券／log／續寫包）遞過去。
        //          ⇒ 驗收量的是「兩個入口讀到的是同一份」，⛔ 不是「兩邊都跑得動」。
        // 🩸 **今天才搬得動**：這三支原本綁死在 Editor 的 `UCL_TreasuryLedger` 上 ——
        //   搬出 Unity 就沒有錢可動。而 `senate cmd bank --arg op=pay` 今天上線
        //   （Tim 2026-09-18「金流可以全面改串新銀行了」）⇒ 錢有了跨宿主的入口。
        // ⚠ 簽名逐字不動 —— 呼叫端（`Cmd_Books`）不必跟著改，
        //   ⇒ 對拍時「輸入一樣」這件事在結構上成立，不必靠比對參數表。
        // ===========================================================
        static readonly SCP_IBooksGateway k_Gate = new UCL_BooksGateway();

        static string DataRoot => UCL_RepoPath.AgentCommandsDir;

        public static string Donate(string book, string donorBank, string donorPersona, string donorAgent,
                                    int tokens, string note, out string broadcastBody, out string error)
            => SCP_BooksOps.Donate(DataRoot, k_Gate, book, donorBank, donorPersona, donorAgent,
                                   tokens, note, out broadcastBody, out error);

        public static string Publish(string book, string donorBank, string authorPersona, string donorAgent,
                                     string title, string note, out string broadcastBody, out string error)
            => SCP_BooksOps.Publish(DataRoot, k_Gate, book, donorBank, authorPersona, donorAgent,
                                    title, note, out broadcastBody, out error);

        public static string Tip(string book, string tipperBank, string tipperPersona, string tipperAgent,
                                 int tokens, string note, out string broadcastBody, out string error)
            => SCP_BooksOps.Tip(DataRoot, k_Gate, book, tipperBank, tipperPersona, tipperAgent,
                                tokens, note, out broadcastBody, out error);

        public static string RetryPendingTips()
            => SCP_BooksOps.RetryPendingTips(DataRoot, k_Gate);

        // ===========================================================
        // 報表（donations / tips 的人讀輸出 —— 與 Python 版同構：原創/捐贈分組 + 打賞累計）
        // ===========================================================
        // ⤷ **薄殼**（TASK-0234 ①）：版面與聚合邏輯整段住
        //   `SCP.Core.Books.SCP_BooksDonations` —— Editor 與 Senate CLI **共用同一份實作**。
        //   ⇒ 驗收量的是「兩個入口讀到的是同一份」（0166 ②），⛔ 不是「兩個實作碰巧一致」。
        //   ⚠ 版面逐字不可改：改措辭要連對拍一起搬走，不然下一個人會以為是資料變了。
        public static string RenderDonations()
            => SCP.Core.Books.SCP_BooksDonations.RenderDonations(UCL_RepoPath.AgentCommandsDir);

        public static string RenderTips(string bookFilter)
            => SCP.Core.Books.SCP_BooksDonations.RenderTips(UCL_RepoPath.AgentCommandsDir, bookFilter);

        // ===========================================================
        // 共用小工具（與 UCL_ReadingLibraryIO 同慣例）
        // ===========================================================
        static string Today() => DateTime.Now.ToString("yyyy-MM-dd");

        static string SafeSlug(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' ? c : '_');
            }
            string o = sb.ToString();
            return o.Length > 40 ? o.Substring(0, 40) : (o.Length == 0 ? "unknown" : o);
        }

        // internal：同 namespace 的 UCL_BooksShelf 要用（分類寫入走同一套讀寫，不另造一份）
        internal static JsonData LoadJson(string path, out string error)
        {
            error = null;
            if (!File.Exists(path)) { error = $"檔案不存在：{path}"; return null; }
            try
            {
                JsonData d = JsonData.ParseJson(File.ReadAllText(path, Encoding.UTF8));
                if (d == null || !d.IsObject) { error = $"不是 JSON 物件：{path}"; return null; }
                return d;
            }
            catch (Exception e)
            {
                error = $"JSON 解析失敗（{path}）：{e.Message}";
                return null;
            }
        }

        // 區塊職責：寫 JSON 到 Books store。
        // 物理意義：**正典格式由 SCP_Core 那支 writer 決定**（Tim 2026-09-11 拍板：
        //          同一件事有多份時以 SCP_Core 版（Senate CLI）為準）。
        //          型別邊界只有這一個點：`JsonData` → 文字 → `SCP_JsonData`
        //          （＝ `UCL_PersonaProfile:137` 那個手勢的反向，而那裡寫著
        //          「收成一個點的理由是**它可驗**」）。
        //          ⛔ 不在本層重寫一個「跟 SCP 同格式」的 formatter —— 兩份 formatter
        //          保持同步靠的是「記得注意」，而修法優先序裡那是最後一級
        //          （讓失敗不可能 ＞ 讓它當場喊 ＞ 記得注意）。
        //          走同一支 writer 之後，**格式漂移在結構上不可能發生**。
        // 數值影響：產物從「tab／冒號後無空格／無結尾換行」換成
        //          「2 空格／冒號後有空格／有結尾換行／CRLF」，與 `SCP_Cmd_Book` 的 838 份產物同形。
        //          🩸 改前這層與 `SCP_Cmd_Book` 兩支同時活著、寫同一批 `book.json`，
        //          而同一個檔在 git 上翻面過（TASK-0200）—— 那不是新舊檔並存，是來回振盪。
        // ⚠ 非 ASCII **不再需要** `UnescapeNonAscii` 那支補丁：`SCP_JsonWriter.WriteString`
        //          天生照原字寫 ⇒ 那支已隨本改動整支移除，⛔ 不留墓碑。
        internal static void SaveJson(string path, JsonData data)
        {
            var aScp = SCP.Core.Json.SCP_JsonData.Parse(data.ToJson());
            SCP.Core.Io.SCP_TextFile.WriteCrLf(
                path, SCP.Core.Json.SCP_JsonWriter.Write(aScp, iIndented: true, iIndent: "  ") + "\n");
        }
    }
}
#endif
