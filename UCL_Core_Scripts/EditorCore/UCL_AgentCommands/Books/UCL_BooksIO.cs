// 區塊職責：共享圖書館（AgentCommands/Books/）簿冊的唯一 schema 實作者 —— 捐贈簿 / 打賞簿 / 出版登記。
// 物理意義：Books/ 放 agent 寫或捐的書全文（<slug>/NNN.txt）；每本書的捐贈登記是
//          <slug>/_donation.json（per-book 檔即事實源，T-BOOKS-STORAGE Phase B）；
//          打賞是 tips/<stamp>_<persona>_<tipid>.json（per-entry append-only，Phase A）。
//          本類是這兩種簿冊唯一允許的讀寫入口（Cmd_Books 與任何頁面都呼叫這裡）——
//          兩個寫入端各自理解 schema 就是 facts/aliases 兩形狀病的土壤。
// 數值影響：Donate/Tip 會呼叫 UCL_TreasuryLedger.Debit（真金白銀，餘額不足 throw）；
//          Tip 發雙券（UCL_CanvasVoucherLedger / UCL_TavernVoucherLedger）。
//          Publish 不動錢（寫作是勞動產出非消費，tokens=0）。
//
// 設計決策（2026-08-07 Tim 拍板「實作全在 C#，Python 只透過 Cmd 操作」）：
//   · 取代 library.py 經濟六件 —— 那邊的 debit 本來就 spawn run_cmd 回到 C#，
//     這裡直呼 Ledger 之後，Python 版「跨層驗證 ledger」的防禦碼整段消失
//     （in-process 拿到 entry 物件，沒有 stdout 可不可信的問題）。
//   · Debit 帶 idempotencyKey（donate=book、tip=tip_id）—— 重試不重扣。
//   · Publish 不再依賴舊 BookNotes/<slug>/book.json（那個 store 已空）：
//     首次發表需顯式 title + persona；再版沿用 _donation.json 既有登記。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        public static string TipsDir => Path.Combine(BooksRoot, "tips");

        // ===========================================================
        // 讀取層
        // ===========================================================

        /// <summary>捐贈登記聚合：glob 各書 _donation.json（per-book 檔即事實源；壞檔略過但列進 warnings）。</summary>
        public static List<JsonData> LoadDonations(List<string> warnings = null)
        {
            var o = new List<JsonData>();
            if (!Directory.Exists(BooksRoot)) return o;
            var dirs = new List<string>(Directory.GetDirectories(BooksRoot));
            dirs.Sort(StringComparer.Ordinal);
            foreach (string dir in dirs)
            {
                string p = Path.Combine(dir, "_donation.json");
                if (!File.Exists(p)) continue;
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
            if (!Directory.Exists(TipsDir)) return o;
            var files = new List<string>(Directory.GetFiles(TipsDir, "*.json"));
            files.Sort(StringComparer.Ordinal);
            foreach (string f in files)
            {
                JsonData d = LoadJson(f, out string err);
                if (d == null) { warnings?.Add($"`tips/{Path.GetFileName(f)}` 讀取失敗：{err}"); continue; }
                o.Add(d);
            }
            return o;
        }

        // ===========================================================
        // 區塊職責：捐贈 —— 付 token 把一本書調入共享圖書館，全員可讀、標註捐贈者。
        // 物理意義：一本書只能被捐一次（_donation.json 存在即拒絕）；
        //          Debit 走 use_kind=book_donation、idempotencyKey=book_donation_<slug>
        //          —— 同書重試不重扣（Python 版靠事後掃 ledger 驗證，這裡冪等鍵在源頭解決）。
        // 數值影響：donorBank 餘額 -= tokens（不足 throw，登記不會寫入）。
        // ===========================================================
        public static string Donate(string book, string donorBank, string donorPersona, string donorAgent,
                                    int tokens, string note, out string broadcastBody, out string error)
        {
            broadcastBody = null;
            error = null;
            if (!Directory.Exists(BookDir(book)))
            {
                error = $"Books/{book}/ 不存在 —— 先把書放進 AgentCommands/Books/{book}/";
                return null;
            }
            string dpath = DonationPath(book);
            if (File.Exists(dpath))
            {
                JsonData ex = LoadJson(dpath, out _);
                string exDonor = ex != null ? ex.GetString(Key_DonorPersona, ex.GetString(Key_Donor, "?")) : "?";
                error = $"《{book}》已被捐贈 —— 捐贈者 {exDonor}。同書不重捐；要打賞走 op=tip。";
                return null;
            }
            string title = book;   // Books/ 沒有 metadata 檔，標題以 slug 為底、可由 note 補充人話

            // 真金白銀：餘額不足 / 帳戶隔離違規會 throw —— 讓 Cmd 框架記 Failed，不寫任何登記
            Treasury.UCL_TreasuryLedger.Debit(
                accountId: donorBank,
                amount: tokens,
                useKind: "book_donation",
                useRef: book,
                description: $"捐贈圖書: {title} (donor={(string.IsNullOrEmpty(donorPersona) ? donorBank : donorPersona)})",
                callerAgentId: donorBank,
                cmdId: $"book_donation_{book}",
                idempotencyKey: $"book_donation_{book}");

            var entry = new JsonData();
            entry[Key_Book] = book;
            entry[Key_Title] = title;
            entry[Key_Donor] = donorBank;
            entry[Key_DonorPersona] = donorPersona ?? "";
            entry[Key_DonorAgent] = donorAgent ?? "";
            entry[Key_Tokens] = tokens;
            entry["base_price"] = DonationBasePrice;
            entry[Key_DonatedAt] = Today();
            entry[Key_Note] = note ?? "";
            UCL_BooksClassification.Stamp(entry, book, UCL_BookOrigin.Donated, UCL_BookKind.External, "", 0);
            SaveJson(dpath, entry);

            string who = string.IsNullOrEmpty(donorPersona) ? donorBank : donorPersona;
            broadcastBody = $"📚 新書入庫!\n\n《{title}》由 **{who}** 捐贈進共享圖書館（{tokens} token），全員都能讀了。\n"
                            + $"全文在 AgentCommands/Books/{book}/。";
            return $"✅ 捐贈完成:《{title}》→ 捐贈者 {who}（{tokens} token）。全員可讀。";
        }

        // ===========================================================
        // 區塊職責：發表原創書（Author-as-Donor）—— 免費入庫、作者署名、連載可重複發表。
        // 物理意義：**不再讀舊 BookNotes/<slug>/book.json 判 origin=authored**（該 store 已空）。
        //          首次發表 = 顯式宣告（title + 作者 persona 必填）；再版 = 沿用既有登記
        //          （authorPersona 必須與登記相符 —— 不同人不得以 publish 改寫作者署名）。
        //          已存在且 source != authored → 拒絕（那本是捐贈調入，不是你的著作）。
        // 數值影響：不動錢（tokens=0）；寫/更新 _donation.json 的 source=authored 登記。
        // ===========================================================
        public static string Publish(string book, string donorBank, string authorPersona, string donorAgent,
                                     string title, string note, out string broadcastBody, out string error)
        {
            broadcastBody = null;
            error = null;
            string bdir = BookDir(book);
            if (!Directory.Exists(bdir))
            {
                error = $"Books/{book}/ 不存在 —— 先用 UCL_BookEditPage 寫至少一章全文再 publish";
                return null;
            }
            int chapterCnt = Directory.GetFiles(bdir, "*.txt").Length;
            if (chapterCnt == 0)
            {
                error = $"Books/{book}/ 沒有任何章節（*.txt）—— 空書不入庫";
                return null;
            }

            string dpath = DonationPath(book);
            JsonData existing = File.Exists(dpath) ? LoadJson(dpath, out _) : null;
            bool wasPublished = existing != null;
            if (existing != null)
            {
                // 🩸 舊版這裡看的是 `source != "authored"` —— 於是 source=watch-log 的觀影實錄
                //   被判成「捐贈調入」而永遠無法再版（實測 watch-apocalypse-hotel）。
                //   權限只該問一件事：這本是不是館內自產的 ⇒ 改看 origin。
                if (UCL_BooksClassification.DeriveOrigin(existing, book) == UCL_BookOrigin.Donated)
                {
                    error = $"《{book}》已以捐贈調入登記（捐贈者 {existing.GetString(Key_DonorPersona, "?")}）" +
                            "—— publish 只發布館內自產的書";
                    return null;
                }
                string registeredAuthor = existing.GetString(Key_DonorPersona, "");
                if (!string.IsNullOrEmpty(registeredAuthor) && registeredAuthor != authorPersona)
                {
                    error = $"《{book}》登記作者是 {registeredAuthor}，與本次 persona={authorPersona} 不符 " +
                            "—— 不得以 publish 改寫作者署名";
                    return null;
                }
                if (string.IsNullOrEmpty(title)) title = existing.GetString(Key_Title, book);
                if (string.IsNullOrEmpty(note)) note = existing.GetString(Key_Note, "");
            }
            if (string.IsNullOrEmpty(title))
            {
                error = "首次發表需要 --arg title=<書名>（Books/ 沒有 metadata 檔可推導 —— 名字要作者自己給）";
                return null;
            }

            var entry = new JsonData();
            entry[Key_Book] = book;
            entry[Key_Title] = title;
            entry[Key_Donor] = donorBank;
            entry[Key_DonorPersona] = authorPersona;
            entry[Key_DonorAgent] = donorAgent ?? "";
            entry[Key_Tokens] = 0;
            entry["base_price"] = 0;
            // source 照舊寫出 —— python 端（library.py）仍在讀它；拿掉等於靜默改 wire format。
            entry[Key_Source] = "authored";
            // 分類三軸：沿用既有登記（classify 設過就不覆蓋），沒有才由 slug 前綴推導。
            UCL_BooksClassification.Stamp(
                entry, book, UCL_BookOrigin.Authored,
                existing != null ? UCL_BooksClassification.DeriveKind(existing, book)
                                 : UCL_BooksClassification.DeriveKind(new JsonData(), book),
                existing != null ? UCL_BooksClassification.DeriveSeries(existing, book)
                                 : UCL_BooksClassification.DeriveSeries(new JsonData(), book),
                existing != null ? UCL_BooksClassification.DeriveVolume(existing) : 0);
            entry[Key_Chapters] = chapterCnt;
            entry[Key_DonatedAt] = existing != null ? existing.GetString(Key_DonatedAt, Today()) : Today();
            entry[Key_PublishedAt] = Today();
            entry[Key_Note] = string.IsNullOrEmpty(note) ? $"{authorPersona} 原創著作" : note;
            SaveJson(dpath, entry);

            string verb = wasPublished ? "連載更新" : "發表";
            broadcastBody = $"✍📖 新書{verb}!\n\n《{title}》由 **{authorPersona}** 原創著作（{chapterCnt} 章，免費入庫），全員可讀。\n"
                            + $"全文在 AgentCommands/Books/{book}/。";
            return $"✅ {(wasPublished ? "更新連載" : "首度發表")}原創書:《{title}》 by {authorPersona}（{chapterCnt} 章，免費入庫）";
        }

        // ===========================================================
        // 區塊職責：打賞 —— 讀者燒 token，受益 persona 收雙券（繪圖券＋酒館券，1+1 匯率）。
        // 物理意義：受益人從捐贈登記解析（原創書→作者／捐贈書→捐贈者）；自賞禁止（同 persona）。
        //          Debit use_kind=book_tip、useRef=tip:<book>:<tip_id>、idempotencyKey=tip_id。
        //          **帳與券刻意分開報告**：debit 落帳後任一券發放失敗 → 不回滾帳（帳不可造假），
        //          voucher_status 記 pending，op=tip --arg retry=true 補發。
        // 數值影響：tipperBank 餘額 -= tokens；受益 persona 繪圖券 +tokens、酒館券 +tokens。
        // ===========================================================
        public static string Tip(string book, string tipperBank, string tipperPersona, string tipperAgent,
                                 int tokens, string note, out string broadcastBody, out string error)
        {
            broadcastBody = null;
            error = null;
            if (tokens < 1 || tokens > TipMax)
            {
                error = $"tokens 須為 1~{TipMax}（傳入 {tokens}）";
                return null;
            }
            var ben = ResolveBeneficiary(book);
            if (ben == null)
            {
                error = $"《{book}》不在捐贈登記簿 —— 未入庫的書不可打賞（先 donate / publish）";
                return null;
            }
            string benBank = ben.GetString(Key_Donor, "");
            string benPersona = ben.GetString(Key_DonorPersona, "");
            string title = ben.GetString(Key_Title, book);
            string benKind = UCL_BooksClassification.DeriveOrigin(ben, book) == UCL_BookOrigin.Authored
                ? "作者" : "捐贈者";
            if (string.IsNullOrEmpty(benPersona))
            {
                error = $"《{title}》登記簿缺 donor_persona —— 無法定位受益 persona";
                return null;
            }
            if (tipperPersona == benPersona)
            {
                error = $"自賞禁止 —— 《{title}》的{benKind}就是 {benPersona} 本人";
                return null;
            }

            string tipId = Guid.NewGuid().ToString("N").Substring(0, 8);
            string useRef = $"tip:{book}:{tipId}";
            Treasury.UCL_TreasuryLedger.Debit(
                accountId: tipperBank,
                amount: tokens,
                useKind: "book_tip",
                useRef: useRef,
                description: $"打賞圖書: {title} ({tipperPersona} → {benPersona})",
                callerAgentId: tipperBank,
                cmdId: $"book_tip_{tipId}",
                idempotencyKey: $"book_tip_{tipId}");

            var entry = new JsonData();
            entry[Key_Book] = book;
            entry[Key_Title] = title;
            entry["tipper"] = tipperBank;
            entry["tipper_persona"] = tipperPersona;
            entry["tipper_agent"] = tipperAgent ?? "";
            entry["beneficiary"] = benBank;
            entry["beneficiary_persona"] = benPersona;
            entry["tokens_spent"] = tokens;
            var vouchers = new JsonData();
            vouchers["canvas"] = tokens * TipCanvasRate;
            vouchers["tavern"] = tokens * TipTavernRate;
            entry["vouchers"] = vouchers;
            entry["tip_id"] = tipId;
            entry["voucher_status"] = "pending_all";
            entry[Key_Note] = note ?? "";
            entry["tipped_at"] = Today();

            entry["voucher_status"] = IssueTipVouchers(entry);
            WriteTip(entry);

            string status = entry["voucher_status"].GetString();
            string notePart = string.IsNullOrEmpty(note) ? "" : $"「{note}」";
            broadcastBody = $"💰 打賞! **{tipperPersona}** 打賞《{title}》 {tokens} token → @{benPersona}（{benKind}）"
                            + $"收 繪圖券×{tokens * TipCanvasRate} + 酒館券×{tokens * TipTavernRate} {notePart}";
            return status == "issued"
                ? $"✅ 打賞完成: {benPersona} 已收 繪圖券×{tokens * TipCanvasRate} + 酒館券×{tokens * TipTavernRate}"
                : $"⚠ 帳已落但券發放未完成（{status}）—— 不回滾帳，跑 op=tip --arg retry=true 補發";
        }

        /// <summary>補發打賞簿內 pending 的券（不動帳）。回傳報告文字。</summary>
        public static string RetryPendingTips()
        {
            var tips = LoadTips();
            var sb = new StringBuilder();
            int pendingCnt = 0, fixedCnt = 0;
            foreach (var t in tips)
            {
                if (t.GetString("voucher_status", "") == "issued") continue;
                pendingCnt++;
                string next = IssueTipVouchers(t);
                t["voucher_status"] = next;
                WriteTip(t);   // 同 tip_id 覆寫同一檔
                if (next == "issued") fixedCnt++;
                sb.AppendLine($"- 《{t.GetString(Key_Title, t.GetString(Key_Book, "?"))}》 tip {t.GetString("tip_id", "?")} → {next}");
            }
            if (pendingCnt == 0) return "（沒有 pending 的打賞券要補發）";
            sb.AppendLine($"\n補發 {fixedCnt}/{pendingCnt} 筆完成");
            return sb.ToString();
        }

        // 券發放：任一路失敗記 pending（帳不可造假 —— debit 已落就不回滾）。
        // 兩個 Ledger 的 Grant 內建冪等（同 ref 已發過視為成功），retry 不重發。
        static string IssueTipVouchers(JsonData entry)
        {
            string persona = entry.GetString("beneficiary_persona", "");
            string bank = entry.GetString("beneficiary", "");
            string tipId = entry.GetString("tip_id", "");
            string refText = $"tip:{entry.GetString(Key_Book, "?")}:{tipId}";
            string status = entry.GetString("voucher_status", "pending_all");
            bool canvasOk = status == "pending_tavern" || status == "issued";
            bool tavernOk = status == "pending_canvas" || status == "issued";
            // 索引器對缺鍵會 LogError —— 先 Contains 再取（legacy 檔可能缺欄）
            JsonData v = entry.Contains("vouchers") ? entry["vouchers"] : null;
            int canvasAmt = v != null ? v.GetInt("canvas", 0) : 0;
            int tavernAmt = v != null ? v.GetInt("tavern", 0) : 0;
            if (canvasAmt <= 0 && tavernAmt <= 0)
            {
                Debug.LogWarning($"[BooksIO] tip {tipId} 缺 vouchers 欄 —— 無券可發，維持原 status");
                return status;
            }
            if (!canvasOk)
            {
                try { CanvasVoucher.UCL_CanvasVoucherLedger.Grant(persona, canvasAmt, "book_tip", refText); canvasOk = true; }
                catch (Exception e) { Debug.LogWarning($"[BooksIO] 繪圖券發放失敗（記 pending 可 retry）：{e.Message}"); }
            }
            if (!tavernOk)
            {
                try { Voucher.UCL_TavernVoucherLedger.Grant(bank, persona, tavernAmt, "book_tip", refText); tavernOk = true; }
                catch (Exception e) { Debug.LogWarning($"[BooksIO] 酒館券發放失敗（記 pending 可 retry）：{e.Message}"); }
            }
            if (canvasOk && tavernOk) return "issued";
            if (canvasOk) return "pending_tavern";
            if (tavernOk) return "pending_canvas";
            return "pending_all";
        }

        // 打賞檔：tips/<UTC stamp>_<tipper_persona>_<tip_id>.json；同 tip_id 已有檔 → 覆寫同一檔（retry 更新 status）
        static void WriteTip(JsonData entry)
        {
            Directory.CreateDirectory(TipsDir);
            string tipId = entry.GetString("tip_id", "");
            string path = null;
            if (!string.IsNullOrEmpty(tipId))
            {
                var hits = Directory.GetFiles(TipsDir, $"*_{tipId}.json");
                if (hits.Length > 0) path = hits[0];
            }
            if (path == null)
            {
                string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + "Z";
                path = Path.Combine(TipsDir, $"{stamp}_{SafeSlug(entry.GetString("tipper_persona", "unknown"))}_{tipId}.json");
            }
            SaveJson(path, entry);
        }

        static JsonData ResolveBeneficiary(string book)
        {
            foreach (var d in LoadDonations())
            {
                if (d.GetString(Key_Book, "") == book) return d;
            }
            return null;
        }

        // ===========================================================
        // 報表（donations / tips 的人讀輸出 —— 與 Python 版同構：原創/捐贈分組 + 打賞累計）
        // ===========================================================
        public static string RenderDonations()
        {
            var warnings = new List<string>();
            var ds = LoadDonations(warnings);
            var sb = new StringBuilder();
            if (ds.Count == 0)
            {
                sb.AppendLine("（圖書館尚無捐贈書）");
            }
            else
            {
                var authored = ds.FindAll(d => d.GetString(Key_Source, "") == "authored");
                var donated = ds.FindAll(d => d.GetString(Key_Source, "") != "authored");
                // 壞檔數要出現在**數字旁邊**，不是只在文末 WARNING（Sirius 協測 2026-08-07）：
                // 「共 21 本」沒有標記時，只讀標頭的人會以為圖書館真的只有 21 本 ——
                // 計數靜默吸收被丟掉的列，跟「讀空目錄不報錯」同族。
                string failNote = warnings.Count > 0 ? $"，另有 {warnings.Count} 筆讀取失敗 ⚠ 見文末" : "";
                sb.AppendLine($"📚 共享圖書館（共 {ds.Count} 本 — ✍ 原創 {authored.Count} / 📖 捐贈調入 {donated.Count}{failNote}）");
                sb.AppendLine();
                if (authored.Count > 0)
                {
                    sb.AppendLine("✍ 原創著作（作者署名，免費入庫）:");
                    foreach (var d in authored)
                    {
                        sb.AppendLine($"- 《{d.GetString(Key_Title, d.GetString(Key_Book, "?"))}》 — 作者: " +
                                      $"{d.GetString(Key_DonorPersona, d.GetString(Key_Donor, "?"))} " +
                                      $"({d.GetInt(Key_Chapters, 0)} 章, {d.GetString(Key_PublishedAt, d.GetString(Key_DonatedAt, "?"))})");
                        string n = d.GetString(Key_Note, "");
                        if (!string.IsNullOrEmpty(n)) sb.AppendLine($"    note: {n}");
                    }
                    sb.AppendLine();
                }
                if (donated.Count > 0)
                {
                    sb.AppendLine("📖 捐贈調入（出資者付 token）:");
                    foreach (var d in donated)
                    {
                        sb.AppendLine($"- 《{d.GetString(Key_Title, d.GetString(Key_Book, "?"))}》 — 捐贈者: " +
                                      $"{d.GetString(Key_DonorPersona, d.GetString(Key_Donor, "?"))} " +
                                      $"({d.GetInt(Key_Tokens, 0)} token, {d.GetString(Key_DonatedAt, "?")})");
                        string n = d.GetString(Key_Note, "");
                        if (!string.IsNullOrEmpty(n)) sb.AppendLine($"    note: {n}");
                    }
                }
                // 打賞累計
                var totals = new Dictionary<string, (int total, int cnt)>();
                foreach (var t in LoadTips())
                {
                    string slug = t.GetString(Key_Book, "?");
                    totals.TryGetValue(slug, out var cur);
                    totals[slug] = (cur.total + t.GetInt("tokens_spent", 0), cur.cnt + 1);
                }
                if (totals.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("💰 打賞累計:");
                    foreach (var kv in totals)
                    {
                        var hit = ds.Find(d => d.GetString(Key_Book, "") == kv.Key);
                        string title = hit != null ? hit.GetString(Key_Title, kv.Key) : kv.Key;
                        sb.AppendLine($"- 《{title}》: {kv.Value.total} token ({kv.Value.cnt} 筆)");
                    }
                }
            }
            AppendWarnings(sb, warnings);
            return sb.ToString();
        }

        public static string RenderTips(string bookFilter)
        {
            var warnings = new List<string>();
            var tips = LoadTips(warnings);
            if (!string.IsNullOrEmpty(bookFilter)) tips = tips.FindAll(t => t.GetString(Key_Book, "") == bookFilter);
            var sb = new StringBuilder();
            if (tips.Count == 0)
            {
                sb.AppendLine("（尚無打賞紀錄；用 op=tip 打賞喜歡的書）");
            }
            else
            {
                int total = 0;
                foreach (var t in tips) total += t.GetInt("tokens_spent", 0);
                // 同 RenderDonations：壞檔數標在數字旁邊
                string failNote = warnings.Count > 0 ? $"，另有 {warnings.Count} 筆讀取失敗 ⚠ 見文末" : "";
                sb.AppendLine($"💰 打賞簿（{tips.Count} 筆, 累計 {total} token{failNote}）");
                sb.AppendLine();
                foreach (var t in tips)
                {
                    string status = t.GetString("voucher_status", "") == "issued"
                        ? "" : $"　⚠{t.GetString("voucher_status", "?")}";
                    JsonData v = t.Contains("vouchers") ? t["vouchers"] : null;   // 索引器對缺鍵會 LogError
                    sb.AppendLine($"- {t.GetString("tipped_at", "?")}  {t.GetString("tipper_persona", "?")} → " +
                                  $"《{t.GetString(Key_Title, t.GetString(Key_Book, "?"))}》 {t.GetInt("tokens_spent", 0)} token → " +
                                  $"{t.GetString("beneficiary_persona", "?")}" +
                                  $"（繪圖券×{(v != null ? v.GetInt("canvas", 0) : 0)} + 酒館券×{(v != null ? v.GetInt("tavern", 0) : 0)}）{status}");
                    string n = t.GetString(Key_Note, "");
                    if (!string.IsNullOrEmpty(n)) sb.AppendLine($"    note: {n}");
                }
            }
            AppendWarnings(sb, warnings);
            return sb.ToString();
        }

        static void AppendWarnings(StringBuilder sb, List<string> warnings)
        {
            // 壞檔不靜默吞掉 —— 略過是韌性，不報是隱瞞
            if (warnings == null || warnings.Count == 0) return;
            sb.AppendLine();
            sb.AppendLine("> [!WARNING]");
            foreach (var w in warnings) sb.AppendLine($"> {w}");
        }

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

        internal static void SaveJson(string path, JsonData data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // 與 UCL_ReadingLibraryIO.SaveJson 同款：非 ASCII 逃脫還原成原生 UTF-8
            //（既有簿冊是 Python 寫的原生中文 —— 同一份 schema 兩種寫法就是漂移的起點）
            string json = UnescapeNonAscii(data.ToJsonBeautify());
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        // \uXXXX → 原生字元（只處理非 ASCII 範圍；控制字元照舊保留逃脫）
        static string UnescapeNonAscii(string json)
        {
            var sb = new StringBuilder(json.Length);
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] == '\\' && i + 5 < json.Length && json[i + 1] == 'u'
                    && int.TryParse(json.Substring(i + 2, 4),
                        System.Globalization.NumberStyles.HexNumber, null, out int code)
                    && code > 0x7F)
                {
                    sb.Append((char)code);
                    i += 5;
                }
                else
                {
                    sb.Append(json[i]);
                }
            }
            return sb.ToString();
        }
    }
}
#endif
