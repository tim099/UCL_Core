
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.Page
{
    // 區塊職責：圖書館管理 UI — 列出共享圖書館的所有書籍 + 捐贈者 + 推薦書單，並提供新增/捐贈/書籤等操作
    // 物理意義：閱讀資料落各專案 repo root（per-project）：
    //          - AgentCommands/BookNotes/<slug>/book.json   每本書 metadata + 進度 + 人物/卷/標籤/書評
    //          - AgentCommands/BookNotes/_recommended.json   推薦書單
    //          - AgentCommands/Books/_donations.json         捐贈索引（誰付 token 認領了哪本書）
    //          工具 library.py 在 UCL_Core（跨專案共用），Page 直讀 JSON 顯示，變更操作走 process spawn 跑 library.py
    // 數值影響：UI 顯示純 read。Add/Donate/Bookmark 按鈕觸發外部 python process，改 book.json / _donations.json
    //
    // 設計理由 (Tim 2026-05-26 派 task)：
    //   原生 library.py 只有 CLI 介面，Tim / agent 想一眼看「圖書館裡有哪些書、誰捐的、進度到哪」沒有可視化介面。
    //   本 page 補可視化清單 + 常用操作 GUI fallback，結構對齊 UCL_LoginStatusPage（讀 per-project 資料 + spawn UCL_Core 工具）。
    [HelpURL("ucl_core:Docs~/{lang}/Mechanics/Reading_Library.md")]
    public class UCL_LibraryManagePage : UCL_CommonEditorPage
    {
        public override string WindowName => UCL_CodeLocalize.Get("LibraryManage.Title");
        public override bool ShowInPageMenu => true;

        // 區塊職責：書籍 entry 結構 — 對齊 BookNotes/<slug>/book.json schema
        // 物理意義：一檔一本書，含 id/標題/作者/讀者 persona/狀態/進度/人物與卷與標籤計數/捐贈狀態
        public class BookEntry
        {
            public string Id = "";
            public string Title = "";
            public string TitleOriginal = "";
            public string Author = "";
            public string ReaderPersona = "";
            public string Status = "";
            public int CurrentChapter = 0;
            public string LastRead = "";
            public string BookmarkNote = "";
            public int CharacterCount = 0;
            public int ArcCount = 0;
            public int VolumeCount = 0;
            public int ReviewCount = 0;
            public string Tags = "";          // tags[] join 成逗號字串供顯示
            // 詳細資訊（選取後展開顯示用 — LoadData 一次載齊，避免渲染時重讀檔）
            public List<string> Characters = new List<string>();   // 人物 id 清單
            public List<string> ArcLines = new List<string>();      // 「chapters — title」
            public List<string> VolumeLines = new List<string>();   // 「卷N title (ch X-Y) [status]」
            public List<string> ReviewLines = new List<string>();   // 「reviewer scope ★rating — pitch」
            // 捐贈狀態（join 自 _donations.json）
            public bool IsDonated = false;
            public string Donor = "";
            public string DonorPersona = "";
            public int DonorTokens = 0;
            public string DonatedAt = "";
            // 全文書庫存在性（Books/<id>/ 是否有實際全文 — 區分 BookNotes 筆記 vs Books 全文）
            public bool HasFullText = false;
        }

        // 區塊職責：捐贈 entry 結構 — 對齊 Books/_donations.json 的 donations[] schema
        public class DonationEntry
        {
            public string Book = "";
            public string Title = "";
            public string Donor = "";
            public string DonorPersona = "";
            public string DonorAgent = "";
            public int Tokens = 0;
            public int BasePrice = 0;
            public string DonatedAt = "";
            public string Note = "";
        }

        // 區塊職責：推薦書單 entry — 對齊 BookNotes/_recommended.json 的 recommendations[] schema
        public class RecommendEntry
        {
            public string Title = "";
            public string Author = "";
            public string Status = "";
            public string BookId = "";       // 若已建檔則指向 book slug，否則空
            public string Synopsis = "";
        }

        // 區塊職責：全文書庫 entry — 對齊 Books/<slug>/（NNN.txt 章節檔 + _donation.json）
        // 物理意義：跟 BookNotes（筆記）區分 — 這是「實際全文」。捐贈資訊讀各書 _donation.json
        public class BookFullEntry
        {
            public string Slug = "";
            public string Title = "";
            public int ChapterCount = 0;     // *.txt 檔數
            public bool IsDonated = false;
            public string Donor = "";
            public string DonorPersona = "";
            public int Tokens = 0;
            public int BasePrice = 0;
            public string DonatedAt = "";
            public string Note = "";
            public bool HasNotes = false;    // 是否有對應 BookNotes/<slug>/
        }

        // 區塊職責：快取資料
        // 物理意義：books 列 BookNotes 全部書，donations 列捐贈索引，recommends 列推薦書單
        List<BookEntry> m_Books = new List<BookEntry>();
        List<DonationEntry> m_Donations = new List<DonationEntry>();
        List<RecommendEntry> m_Recommends = new List<RecommendEntry>();

        // 區塊職責：新增書籍表單 state
        // 物理意義：Tim 輸入 id/標題/原文名/作者/讀者 persona，按「新增」後 spawn library.py add-book
        string m_NewBookId = "";
        string m_NewBookTitle = "";
        string m_NewBookTitleOriginal = "";
        string m_NewBookAuthor = "";
        string m_NewBookReaderPersona = "";

        // 區塊職責：捐贈表單 state
        // 物理意義：Tim 輸入要捐的書 slug + 捐贈者 bank id + token 數，按「捐贈」後 spawn library.py donate
        string m_DonateBook = "";
        string m_DonateDonor = "claude-da-xiaojie";
        string m_DonateTokens = "100";
        string m_DonatePersona = "";

        // 區塊職責：書籤表單 state
        // 物理意義：Tim 選書 + 輸入章節 + 心得，按「書籤」後 spawn library.py bookmark
        string m_BookmarkBook = "";
        string m_BookmarkChapter = "";
        string m_BookmarkNote = "";

        // 區塊職責：BookNotes 下拉選單 state（對齊 UCL_AffinitySystemPage 的 persona picker 模式）
        // 物理意義：m_SelectedBookId = 當前選中的 BookNotes id；m_BookPickerDic 給 PopupSearchCache 暫存搜尋 state
        //          m_BookDisplayOptions = 下拉顯示字串清單（「title (id)」），index 對齊 m_Books 排序後順序
        string m_SelectedBookId = "";
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();
        List<string> m_BookDisplayOptions = new List<string>();

        // 區塊職責：全文書庫（Books/）下拉選單 state
        // 物理意義：m_FullBooks = Books/*/ 掃到的全文書；m_SelectedFullBook = 當前選中 slug；
        //          m_FullBookDisplayOptions = 下拉顯示「title (slug)」
        List<BookFullEntry> m_FullBooks = new List<BookFullEntry>();
        string m_SelectedFullBook = "";
        List<string> m_FullBookDisplayOptions = new List<string>();

        // 區塊職責：scroll 位置 + 推薦展開開關
        //Vector2 m_DetailScroll = Vector2.zero;
        bool m_ShowRecommends = false;

        string m_AgentCommandsDir = "";
        string m_BookNotesDir = "";
        string m_BooksDir = "";
        string m_UCLCorePath = "";

        public override void Init(UCL_GUIPageController p_Controller)
        {
            base.Init(p_Controller);
            // 區塊：路徑解析
            // 物理意義：BookNotes / Books 落 per-project repo root；library.py 在 UCL_Core 給 process spawn
            m_AgentCommandsDir = UCL_RepoPath.AgentCommandsDir;
            m_BookNotesDir = Path.Combine(m_AgentCommandsDir, "BookNotes");
            m_BooksDir = Path.Combine(m_AgentCommandsDir, "Books");
            // 區塊：UCL_Core path 解析 — 走 UCL_EditorPath.CorePath（對齊 UCL_LoginStatusPage）
            string corePathRel = UCL_EditorPath.CorePath;
            if (!string.IsNullOrEmpty(corePathRel))
            {
                m_UCLCorePath = Path.GetFullPath(Path.Combine(UCL_RepoPath.UnityProjectRoot, corePathRel));
            }
            LoadData();
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.Refresh"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                LoadData();
            }
            if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.OpenBookNotes"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                OpenInExplorer(m_BookNotesDir);
            }
        }

        /// <summary>
        /// 區塊職責：載入並反序列化 books + donations + recommendations
        /// 物理意義：scan BookNotes/*/book.json + 讀 Books/_donations.json + BookNotes/_recommended.json
        /// 數值影響：更新 m_Books / m_Donations / m_Recommends，並把捐贈狀態 join 進對應 book
        /// </summary>
        void LoadData()
        {
            m_Books.Clear();
            m_Donations.Clear();
            m_Recommends.Clear();

            // 區塊：讀捐贈記錄（之後 join 進 book，故先載）
            // 物理意義：T-BOOKS-STORAGE Phase B — 從單一 Books/_donations.json 聚合檔改成 scan 各書
            //          <slug>/_donation.json（per-book 即 source of truth，避跨專案共享 submodule 併發寫聚合檔衝突）。
            //          根 _donations.json 已廢除；掃各書資料夾各讀一筆捐贈記錄。
            try
            {
                if (Directory.Exists(m_BooksDir))
                {
                    foreach (var bookDir in Directory.GetDirectories(m_BooksDir))
                    {
                        string dpath = Path.Combine(bookDir, "_donation.json");
                        if (!File.Exists(dpath)) continue;
                        try
                        {
                            var d = JsonData.ParseJson(File.ReadAllText(dpath));
                            if (d == null || !d.IsObject) continue;
                            string slug = Path.GetFileName(bookDir);
                            m_Donations.Add(new DonationEntry
                            {
                                Book = d.GetString("book", slug),
                                Title = d.GetString("title", ""),
                                Donor = d.GetString("donor", ""),
                                DonorPersona = d.GetString("donor_persona", ""),
                                DonorAgent = d.GetString("donor_agent", ""),
                                Tokens = d.GetInt("tokens", 0),
                                BasePrice = d.GetInt("base_price", 0),
                                DonatedAt = d.GetString("donated_at", ""),
                                Note = d.GetString("note", ""),
                            });
                        }
                        catch (Exception e2)
                        {
                            Debug.LogWarning($"[LibraryManage] Books/{Path.GetFileName(bookDir)}/_donation.json load failed: {e2.Message}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LibraryManage] donations scan failed: {e.Message}");
            }

            // 區塊：建立 book → donation 快速查表（join 用）
            var donationByBook = new Dictionary<string, DonationEntry>();
            foreach (var d in m_Donations)
            {
                if (!string.IsNullOrEmpty(d.Book)) donationByBook[d.Book] = d;
            }

            // 區塊：scan BookNotes/*/book.json
            // 物理意義：每個子目錄一本書，book.json 是 metadata 入口
            if (Directory.Exists(m_BookNotesDir))
            {
                foreach (var dir in Directory.GetDirectories(m_BookNotesDir))
                {
                    string bookJson = Path.Combine(dir, "book.json");
                    if (!File.Exists(bookJson)) continue;
                    try
                    {
                        var jd = JsonData.ParseJson(File.ReadAllText(bookJson));
                        if (jd == null || !jd.IsObject || jd.Dic == null) continue;

                        // 區塊：Id 一律取「資料夾名」當權威 slug（cross-layer 身份鐵律）
                        // 物理意義：library.py --book <slug> 解析的是 BookNotes/<資料夾名>/，資料夾名才是唯一鍵。
                        //          book.json 內的 "id" 欄可能漂移（如 farseer-trilogy_01/02/03 都誤寫成 "farseer-trilogy"），
                        //          信它會導致下拉重複 + 開錯資料夾 + library.py slug 對不上，故一律忽略改用資料夾名。
                        string folderSlug = Path.GetFileName(dir);
                        var entry = new BookEntry
                        {
                            Id = folderSlug,
                            Title = jd.GetString("title", ""),
                            TitleOriginal = jd.GetString("title_original", ""),
                            Author = jd.GetString("author", ""),
                            ReaderPersona = jd.GetString("reader_persona", ""),
                            Status = jd.GetString("status", ""),
                        };

                        // 區塊：progress 子物件 — current_chapter / last_read / bookmark_note
                        if (jd.Dic.TryGetValue("progress", out var prog) && prog != null && prog.IsObject)
                        {
                            entry.CurrentChapter = prog.GetInt("current_chapter", 0);
                            entry.LastRead = prog.GetString("last_read", "");
                            entry.BookmarkNote = prog.GetString("bookmark_note", "");
                        }

                        // 區塊：陣列計數 — characters / arcs / volumes / reviews 取數量供標頭顯示
                        entry.CharacterCount = CountArray(jd, "characters");
                        entry.ArcCount = CountArray(jd, "arcs");
                        entry.VolumeCount = CountArray(jd, "volumes");
                        entry.ReviewCount = CountArray(jd, "reviews");

                        // 區塊：詳細明細 — 選取後展開顯示，LoadData 一次載齊
                        // 物理意義：characters 是字串陣列(人物 id)；arcs/volumes/reviews 是物件陣列，各取關鍵欄位 join 成一行
                        if (jd.Dic.TryGetValue("characters", out var charsNode) && charsNode != null && charsNode.IsArray)
                        {
                            for (int i = 0; i < charsNode.Count; i++)
                                entry.Characters.Add(charsNode[i].GetString());
                        }
                        if (jd.Dic.TryGetValue("arcs", out var arcsNode) && arcsNode != null && arcsNode.IsArray)
                        {
                            for (int i = 0; i < arcsNode.Count; i++)
                            {
                                var a = arcsNode[i];
                                if (a == null || !a.IsObject) continue;
                                entry.ArcLines.Add($"ch {a.GetString("chapters", "?")} — {a.GetString("title", "")}");
                            }
                        }
                        if (jd.Dic.TryGetValue("volumes", out var volsNode) && volsNode != null && volsNode.IsArray)
                        {
                            for (int i = 0; i < volsNode.Count; i++)
                            {
                                var v = volsNode[i];
                                if (v == null || !v.IsObject) continue;
                                entry.VolumeLines.Add($"卷{v.GetInt("n", 0)} {v.GetString("title", "")} (ch {v.GetString("chapters", "?")}) [{v.GetString("status", "")}]");
                            }
                        }
                        if (jd.Dic.TryGetValue("reviews", out var revsNode) && revsNode != null && revsNode.IsArray)
                        {
                            for (int i = 0; i < revsNode.Count; i++)
                            {
                                var rv = revsNode[i];
                                if (rv == null || !rv.IsObject) continue;
                                entry.ReviewLines.Add($"{rv.GetString("reviewer", "?")} [{rv.GetString("scope", "")}] ★{rv.GetInt("rating", 0)} — {rv.GetString("pitch", "")}");
                            }
                        }

                        // 區塊：全文書庫存在性 — Books/<id>/ 有目錄才算有實際全文（區分 BookNotes 筆記 vs Books 全文）
                        entry.HasFullText = Directory.Exists(Path.Combine(m_BooksDir, entry.Id));

                        // 區塊：tags[] join 成逗號字串
                        if (jd.Dic.TryGetValue("tags", out var tagsNode) && tagsNode != null && tagsNode.IsArray)
                        {
                            var sb = new StringBuilder();
                            for (int i = 0; i < tagsNode.Count; i++)
                            {
                                if (i > 0) sb.Append(", ");
                                sb.Append(tagsNode[i].GetString());
                            }
                            entry.Tags = sb.ToString();
                        }

                        // 區塊：join 捐贈狀態
                        if (donationByBook.TryGetValue(entry.Id, out var dn))
                        {
                            entry.IsDonated = true;
                            entry.Donor = dn.Donor;
                            entry.DonorPersona = dn.DonorPersona;
                            entry.DonorTokens = dn.Tokens;
                            entry.DonatedAt = dn.DonatedAt;
                        }

                        m_Books.Add(entry);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[LibraryManage] parse {bookJson} failed: {e.Message}");
                    }
                }

                // 區塊職責：排序 — reading 狀態優先（活躍書置頂），其次按 last_read 降序，再按 title 升序保證確定性
                // 數值影響：不改資料，只變 UI 渲染順序，把「正在讀的書」放最上方利於觀察
                m_Books.Sort((a, b) =>
                {
                    bool aReading = string.Equals(a.Status, "reading", StringComparison.OrdinalIgnoreCase);
                    bool bReading = string.Equals(b.Status, "reading", StringComparison.OrdinalIgnoreCase);
                    if (aReading != bReading) return bReading.CompareTo(aReading);
                    int lastReadCompare = string.Compare(b.LastRead, a.LastRead, StringComparison.Ordinal);
                    if (lastReadCompare != 0) return lastReadCompare;
                    return string.Compare(a.Title, b.Title, StringComparison.Ordinal);
                });

                // 區塊：建下拉選單顯示清單 + 維持/重設選取
                // 物理意義：display 字串 = 「title (id)」，index 對齊 m_Books 排序後順序；
                //          若先前選取的 id 仍存在則保留，否則預設選第一本
                m_BookDisplayOptions.Clear();
                foreach (var b in m_Books)
                    m_BookDisplayOptions.Add(string.IsNullOrEmpty(b.Title) ? b.Id : $"{b.Title} ({b.Id})");
                if ((string.IsNullOrEmpty(m_SelectedBookId) || m_Books.FindIndex(x => x.Id == m_SelectedBookId) < 0)
                    && m_Books.Count > 0)
                {
                    m_SelectedBookId = m_Books[0].Id;
                }
            }

            // 區塊：讀推薦書單
            // 物理意義：BookNotes/_recommended.json 的 recommendations[] — 想讀但未必建檔的書
            try
            {
                string recPath = Path.Combine(m_BookNotesDir, "_recommended.json");
                if (File.Exists(recPath))
                {
                    var jd = JsonData.ParseJson(File.ReadAllText(recPath));
                    if (jd != null && jd.IsObject && jd.Dic != null
                        && jd.Dic.TryGetValue("recommendations", out var arr) && arr != null && arr.IsArray)
                    {
                        for (int i = 0; i < arr.Count; i++)
                        {
                            var r = arr[i];
                            if (r == null || !r.IsObject) continue;
                            m_Recommends.Add(new RecommendEntry
                            {
                                Title = r.GetString("title", ""),
                                Author = r.GetString("author", ""),
                                Status = r.GetString("status", ""),
                                BookId = r.GetString("book_id", ""),
                                Synopsis = r.GetString("synopsis", ""),
                            });
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LibraryManage] recommendations load failed: {e.Message}");
            }

            // 區塊：掃全文書庫 Books/*/（跟 BookNotes 區分 — 這是實際全文）
            // 物理意義：每個子目錄一本全文書，內含 NNN.txt 章節 + _donation.json（捐贈資訊）
            m_FullBooks.Clear();
            if (Directory.Exists(m_BooksDir))
            {
                foreach (var dir in Directory.GetDirectories(m_BooksDir))
                {
                    string slug = Path.GetFileName(dir);
                    var fe = new BookFullEntry
                    {
                        Slug = slug,
                        ChapterCount = Directory.GetFiles(dir, "*.txt").Length,
                        HasNotes = Directory.Exists(Path.Combine(m_BookNotesDir, slug)),
                    };
                    // 讀 per-book _donation.json 取標題 + 捐贈資訊
                    try
                    {
                        string dpath = Path.Combine(dir, "_donation.json");
                        if (File.Exists(dpath))
                        {
                            var dj = JsonData.ParseJson(File.ReadAllText(dpath));
                            if (dj != null && dj.IsObject && dj.Dic != null)
                            {
                                fe.Title = dj.GetString("title", "");
                                fe.Donor = dj.GetString("donor", "");
                                fe.DonorPersona = dj.GetString("donor_persona", "");
                                fe.Tokens = dj.GetInt("tokens", 0);
                                fe.BasePrice = dj.GetInt("base_price", 0);
                                fe.DonatedAt = dj.GetString("donated_at", "");
                                fe.Note = dj.GetString("note", "");
                                fe.IsDonated = !string.IsNullOrEmpty(fe.Donor);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[LibraryManage] Books/{slug}/_donation.json load failed: {e.Message}");
                    }
                    if (string.IsNullOrEmpty(fe.Title)) fe.Title = slug;
                    m_FullBooks.Add(fe);
                }
                m_FullBooks.Sort((a, b) => string.Compare(a.Slug, b.Slug, StringComparison.Ordinal));

                m_FullBookDisplayOptions.Clear();
                foreach (var fb in m_FullBooks)
                    m_FullBookDisplayOptions.Add($"{fb.Title} ({fb.Slug})");
                if ((string.IsNullOrEmpty(m_SelectedFullBook) || m_FullBooks.FindIndex(x => x.Slug == m_SelectedFullBook) < 0)
                    && m_FullBooks.Count > 0)
                {
                    m_SelectedFullBook = m_FullBooks[0].Slug;
                }
            }
        }

        // 區塊職責：安全計算某 key 對應陣列的元素數量
        // 物理意義：characters / arcs / volumes / reviews 都是陣列，缺欄位或非陣列時回 0
        static int CountArray(JsonData jd, string key)
        {
            if (jd != null && jd.IsObject && jd.Dic != null
                && jd.Dic.TryGetValue(key, out var node) && node != null && node.IsArray)
            {
                return node.Count;
            }
            return 0;
        }

        protected override void ContentOnGUI()
        {
            DrawBookNotesSection();
            GUILayout.Space(12);
            DrawBooksFullSection();
            GUILayout.Space(12);
            DrawDonations();
            GUILayout.Space(12);
            DrawAddBookForm();
            GUILayout.Space(8);
            DrawDonateForm();
            GUILayout.Space(8);
            DrawBookmarkForm();
            GUILayout.Space(12);
            DrawRecommendations();
        }

        // 區塊職責：BookNotes 區塊 — 下拉選單選一本筆記 + 顯示其詳細資訊
        // 物理意義：對齊 UCL_AffinitySystemPage 的「選 persona → 看 details」模式：上方 PopupSearchCache 選 BookNotes，
        //          下方完整展開該筆記（metadata / 進度 / 書籤 / 人物 / arc / 卷 / 書評 / 捐贈狀態）+ 操作按鈕。
        // 注意命名：本區塊讀的是 BookNotes（讀書筆記），非 Books（全文書庫，在 AgentCommands/Books）。
        void DrawBookNotesSection()
        {
            GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.BookNotes.HeaderFmt"), m_Books.Count), UCL_GUIStyle.LabelStyle);

            if (m_Books.Count == 0)
            {
                using (new GUILayout.VerticalScope("box"))
                {
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.BookNotes.EmptyFmt"), m_BookNotesDir), UCL_GUIStyle.LabelStyle);
                }
                return;
            }

            // 區塊：下拉選單列（對齊 Affinity 的 PopupSearchCache 用法）
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.BookNotes.SelectLabel"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                int curIdx = m_Books.FindIndex(x => x.Id == m_SelectedBookId);
                if (curIdx < 0) curIdx = 0;
                int newIdx = UCL_GUILayout.PopupSearchCache(curIdx, m_BookDisplayOptions, m_Dic.GetSubDic("BookPicker"), "BookNotesPicker");
                if (newIdx >= 0 && newIdx < m_Books.Count) m_SelectedBookId = m_Books[newIdx].Id;
                GUILayout.FlexibleSpace();
            }

            // 區塊：取選中的 BookNotes，畫詳細面板
            var b = m_Books.Find(x => x.Id == m_SelectedBookId);
            if (b == null) return;
            DrawBookNoteDetail(b, m_Dic.GetSubDic(b.Id));
        }

        // 區塊職責：單一 BookNotes 的詳細資訊面板
        // 物理意義：把選中筆記的所有欄位完整展開 — metadata + 進度 + 書籤 + 人物清單 + arc + 卷 + 書評 + 捐贈狀態，附操作按鈕
        // 數值影響：純 UI；操作按鈕 spawn library.py（show-book / resume / 開資料夾）
        void DrawBookNoteDetail(BookEntry b, UCL_ObjectDictionary dic)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool showDetail = false;
                //m_DetailScroll = GUILayout.BeginScrollView(m_DetailScroll, GUILayout.Height(UCL_GUIStyle.GetScaledSize(360)));
                using (new GUILayout.HorizontalScope())
                {
                    showDetail = UCL_GUILayout.Toggle(dic, "showDetail");
                    // 標題列：書名
                    string fullText = b.HasFullText
                        ? $"  <color=#66ff99>{UCL_CodeLocalize.Get("LibraryManage.Detail.HasFullText")}</color>"
                        : $"  <color=#ffaa66>{UCL_CodeLocalize.Get("LibraryManage.Detail.NotesOnly")}</color>";
                    GUILayout.Label($"<b><size=15>{b.Title}</size></b>{fullText}({b.Id})", UCL_GUIStyle.LabelStyle);
                }

                if (showDetail)
                {
                    // 原文名 + 作者
                    if (!string.IsNullOrEmpty(b.TitleOriginal))
                        GUILayout.Label($"<i>{b.TitleOriginal}</i>", UCL_GUIStyle.LabelStyle);
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Detail.AuthorFmt"), b.Author), UCL_GUIStyle.LabelStyle);

                    GUILayout.Space(4);

                    // 區塊職責：操作按鈕列 — 開資料夾 / 開檔 / spawn library.py 各唯讀 op
                    // 物理意義：📂 筆記資料夾 = BookNotes/<id>；📂 全文資料夾 = Books/<id>（僅有全文才顯示）；
                    //          📄 book.json = 直接開 metadata 檔；其餘 spawn library.py（概覽/續讀/arc/名詞/書評/卷別），輸出印 Console
                    using (new GUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.ShowBook"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            RunLibrary(new List<string> { $"\"{LibraryPyPath()}\"", "show-book", "--book", b.Id }, $"show-book {b.Id}");
                        if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.Resume"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            RunLibrary(new List<string> { $"\"{LibraryPyPath()}\"", "resume", "--book", b.Id }, $"resume {b.Id}");
                        if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.NotesFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            OpenInExplorer(Path.Combine(m_BookNotesDir, b.Id));
                        // 全文資料夾僅在 Books/<id>/ 存在時顯示（避免點了開不存在路徑）
                        if (b.HasFullText && GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.FullTextFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            OpenInExplorer(Path.Combine(m_BooksDir, b.Id));
                        if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.OpenJson"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            OpenFile(Path.Combine(m_BookNotesDir, b.Id, "book.json"));
                    }
                    using (new GUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.Arcs"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            RunLibrary(new List<string> { $"\"{LibraryPyPath()}\"", "arcs", "--book", b.Id, "--full" }, $"arcs {b.Id}");
                        if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.Terms"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            RunLibrary(new List<string> { $"\"{LibraryPyPath()}\"", "terms", "--book", b.Id }, $"terms {b.Id}");
                        if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.Volumes"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            RunLibrary(new List<string> { $"\"{LibraryPyPath()}\"", "volumes", "--book", b.Id }, $"volumes {b.Id}");
                        if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.Reviews"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            RunLibrary(new List<string> { $"\"{LibraryPyPath()}\"", "reviews", "--book", b.Id }, $"reviews {b.Id}");
                        GUILayout.FlexibleSpace();
                    }

                    GUILayout.Space(4);

                    // metadata 列：id / 狀態 / 讀者 / 標籤
                    GUILayout.Label($"<size=10>id: {b.Id}</size>", UCL_GUIStyle.LabelStyle);
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Books.StatusFmt"), b.Status), UCL_GUIStyle.LabelStyle);
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Books.ReaderFmt"), b.ReaderPersona), UCL_GUIStyle.LabelStyle);
                    if (!string.IsNullOrEmpty(b.Tags))
                        GUILayout.Label($"<color=#cccccc>🏷 {b.Tags}</color>", UCL_GUIStyle.LabelStyle);

                    // 進度 + 書籤
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Books.ProgressFmt"), b.CurrentChapter, TruncTs(b.LastRead)), UCL_GUIStyle.LabelStyle);
                    if (!string.IsNullOrEmpty(b.BookmarkNote))
                        GUILayout.Label($"<color=#dddddd>🔖 {b.BookmarkNote}</color>", UCL_GUIStyle.LabelStyle);

                    // 捐贈狀態
                    if (b.IsDonated)
                    {
                        string personaSuffix = string.IsNullOrEmpty(b.DonorPersona) ? "" : $" / {b.DonorPersona}";
                        GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Detail.DonatedFmt"), $"{b.Donor}{personaSuffix}", b.DonorTokens, b.DonatedAt), UCL_GUIStyle.LabelStyle);
                    }

                    // 摘要計數
                    GUILayout.Space(4);
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Books.CountsFmt"),
                        b.CharacterCount, b.ArcCount, b.VolumeCount, b.ReviewCount), UCL_GUIStyle.LabelStyle);

                    // 卷別
                    DrawDetailList(b.VolumeLines, "LibraryManage.Detail.VolumesLabel");
                    // arc 階段大綱
                    DrawDetailList(b.ArcLines, "LibraryManage.Detail.ArcsLabel");
                    // 書評
                    DrawDetailList(b.ReviewLines, "LibraryManage.Detail.ReviewsLabel");
                    // 人物清單（id 逗號 join，免一行一個太長）
                    if (b.Characters.Count > 0)
                    {
                        GUILayout.Space(4);
                        GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Detail.CharactersLabel"), b.Characters.Count), UCL_GUIStyle.LabelStyle);
                        GUILayout.Label($"<size=10><color=#cccccc>{string.Join(", ", b.Characters)}</color></size>", UCL_GUIStyle.LabelStyle);
                    }
                }


                //GUILayout.EndScrollView();
            }
        }

        // 區塊職責：通用「標題 + 條列」明細區塊（卷/arc/書評共用）
        // 物理意義：清單空就不畫；非空印一個 localize 標題 + 逐行條列
        void DrawDetailList(List<string> lines, string labelKey)
        {
            if (lines == null || lines.Count == 0) return;
            GUILayout.Space(4);
            GUILayout.Label(string.Format(UCL_CodeLocalize.Get(labelKey), lines.Count), UCL_GUIStyle.LabelStyle);
            foreach (var line in lines)
                GUILayout.Label($" • {line}", UCL_GUIStyle.LabelStyle);
        }

        // 區塊職責：全文書庫（Books/）區塊 — 下拉選一本全文書 + 顯示捐贈者/資訊 + 跳轉編輯 Page 按鈕
        // 物理意義：跟 BookNotes（筆記）區分，本區塊操作的是 AgentCommands/Books/ 的實際全文。
        //          「✏ 編輯書籍」按鈕 new 一個 UCL_BookEditPage 設好 slug 後 Push（跳轉到章節編輯 prototype）。
        void DrawBooksFullSection()
        {
            GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Books.HeaderFmt"), m_FullBooks.Count), UCL_GUIStyle.LabelStyle);

            if (m_FullBooks.Count == 0)
            {
                using (new GUILayout.VerticalScope("box"))
                {
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Books.EmptyFmt"), m_BooksDir), UCL_GUIStyle.LabelStyle);
                }
                return;
            }

            using (new GUILayout.VerticalScope("box"))
            {
                // 下拉選單列 + 編輯按鈕
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Books.SelectLabel"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(120)));
                    int curIdx = m_FullBooks.FindIndex(x => x.Slug == m_SelectedFullBook);
                    if (curIdx < 0) curIdx = 0;
                    int newIdx = UCL_GUILayout.PopupSearchCache(curIdx, m_FullBookDisplayOptions, m_Dic.GetSubDic("FullBookPicker"), "FullBookPicker");
                    if (newIdx >= 0 && newIdx < m_FullBooks.Count) m_SelectedFullBook = m_FullBooks[newIdx].Slug;
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.EditBook"), UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 0.85f, 1f)), GUILayout.ExpandWidth(false)))
                        OpenBookEditPage(m_SelectedFullBook);
                }

                var fb = m_FullBooks.Find(x => x.Slug == m_SelectedFullBook);
                if (fb == null) return;

                // 操作按鈕
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.FullTextFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        OpenInExplorer(Path.Combine(m_BooksDir, fb.Slug));
                    if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.EditBook"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        OpenBookEditPage(fb.Slug);
                    GUILayout.FlexibleSpace();
                }

                // 書籍資訊
                GUILayout.Label($"<b><size=14>{fb.Title}</size></b>  <size=10>({fb.Slug})</size>", UCL_GUIStyle.LabelStyle);
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Books.ChapterFmt"), fb.ChapterCount), UCL_GUIStyle.LabelStyle);
                // 捐贈者資訊
                if (fb.IsDonated)
                {
                    string personaSuffix = string.IsNullOrEmpty(fb.DonorPersona) ? "" : $" / {fb.DonorPersona}";
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Detail.DonatedFmt"), $"{fb.Donor}{personaSuffix}", fb.Tokens, fb.DonatedAt), UCL_GUIStyle.LabelStyle);
                    if (!string.IsNullOrEmpty(fb.Note))
                        GUILayout.Label($"<size=10><color=#dddddd>{fb.Note}</color></size>", UCL_GUIStyle.LabelStyle);
                }
                else
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Books.NotDonated"), UCL_GUIStyle.LabelStyle);
                }
                // 對應筆記提示
                GUILayout.Label(fb.HasNotes
                    ? UCL_CodeLocalize.Get("LibraryManage.Books.HasNotes")
                    : UCL_CodeLocalize.Get("LibraryManage.Books.NoNotes"), UCL_GUIStyle.LabelStyle);


            }
        }

        // 區塊職責：跳轉到書籍編輯 Page（prototype）
        // 物理意義：new UCL_BookEditPage → SetBook(slug, BooksDir) → Push（Push 內部呼叫 Init load 章節）
        void OpenBookEditPage(string slug)
        {
            if (string.IsNullOrEmpty(slug)) return;
            var page = new UCL_BookEditPage();
            page.SetBook(slug, m_BooksDir);
            UCL_GUIPageController.CurrentRenderIns.Push(page);
        }

        // 區塊職責：捐贈者清單 — 讀 _donations.json 顯示誰認領了哪本書、花多少 token
        void DrawDonations()
        {
            GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Donations.HeaderFmt"), m_Donations.Count), UCL_GUIStyle.LabelStyle);
            using (new GUILayout.VerticalScope("box"))
            {
                if (m_Donations.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Donations.Empty"), UCL_GUIStyle.LabelStyle);
                    return;
                }
                foreach (var d in m_Donations)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        using (new GUILayout.VerticalScope(GUILayout.Width(UCL_GUIStyle.GetScaledSize(240))))
                        {
                            GUILayout.Label($"<b>{d.Title}</b>", UCL_GUIStyle.LabelStyle);
                            GUILayout.Label($"<size=9>{d.Book}</size>", UCL_GUIStyle.LabelStyle);
                        }
                        using (new GUILayout.VerticalScope(GUILayout.Width(UCL_GUIStyle.GetScaledSize(220))))
                        {
                            GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Col.Donor"), UCL_GUIStyle.LabelStyle);
                            string personaSuffix = string.IsNullOrEmpty(d.DonorPersona) ? "" : $" / {d.DonorPersona}";
                            GUILayout.Label($"{d.Donor}{personaSuffix}", UCL_GUIStyle.LabelStyle);
                        }
                        using (new GUILayout.VerticalScope(GUILayout.Width(UCL_GUIStyle.GetScaledSize(120))))
                        {
                            GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Col.Tokens"), UCL_GUIStyle.LabelStyle);
                            GUILayout.Label($"{d.Tokens} / {d.BasePrice}", UCL_GUIStyle.LabelStyle);
                        }
                        using (new GUILayout.VerticalScope(GUILayout.Width(UCL_GUIStyle.GetScaledSize(110))))
                        {
                            GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Col.Date"), UCL_GUIStyle.LabelStyle);
                            GUILayout.Label(d.DonatedAt, UCL_GUIStyle.LabelStyle);
                        }
                        using (new GUILayout.VerticalScope())
                        {
                            if (!string.IsNullOrEmpty(d.Note))
                                GUILayout.Label($"<size=10><color=#dddddd>{TruncStr(d.Note, 60)}</color></size>", UCL_GUIStyle.LabelStyle);
                            GUILayout.FlexibleSpace();
                        }
                    }
                }
            }
        }

        // 區塊職責：新增書籍表單 — spawn library.py add-book
        // 物理意義：Tim 填 id/標題/原文名/作者/讀者 persona，建一本新書的 book.json
        void DrawAddBookForm()
        {
            GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.AddBook.Title"), UCL_GUIStyle.LabelStyle);
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Field.Id"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_NewBookId = GUILayout.TextField(m_NewBookId, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Field.Title"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    m_NewBookTitle = GUILayout.TextField(m_NewBookTitle, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Field.TitleOriginal"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_NewBookTitleOriginal = GUILayout.TextField(m_NewBookTitleOriginal, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Field.Author"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    m_NewBookAuthor = GUILayout.TextField(m_NewBookAuthor, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Field.ReaderPersona"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_NewBookReaderPersona = GUILayout.TextField(m_NewBookReaderPersona, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.AddBook"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        DoAddBook();
                    }
                }
                GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.AddBook.Hint"), UCL_GUIStyle.LabelStyle);
            }
        }

        // 區塊職責：捐贈表單 — spawn library.py donate（會扣 token，故確認後再跑）
        void DrawDonateForm()
        {
            GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Donate.Title"), UCL_GUIStyle.LabelStyle);
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Field.Book"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_DonateBook = GUILayout.TextField(m_DonateBook, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Field.Donor"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    m_DonateDonor = GUILayout.TextField(m_DonateDonor, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Field.DonorPersona"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_DonatePersona = GUILayout.TextField(m_DonatePersona, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Field.DonorTokens"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    m_DonateTokens = GUILayout.TextField(m_DonateTokens, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.Donate"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        DoDonate();
                    }
                }
                GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Donate.Hint"), UCL_GUIStyle.LabelStyle);
            }
        }

        // 區塊職責：書籤表單 — spawn library.py bookmark（記讀到哪 + 心得）
        void DrawBookmarkForm()
        {
            GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Bookmark.Title"), UCL_GUIStyle.LabelStyle);
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Field.Book"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_BookmarkBook = GUILayout.TextField(m_BookmarkBook, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Field.Chapter"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    m_BookmarkChapter = GUILayout.TextField(m_BookmarkChapter, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.Bookmark"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        DoBookmark();
                    }
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Field.BookmarkNote"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_BookmarkNote = GUILayout.TextField(m_BookmarkNote, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(true));
                }
            }
        }

        // 區塊職責：推薦書單（預設收合，點開展看）
        void DrawRecommendations()
        {
            m_ShowRecommends = GUILayout.Toggle(m_ShowRecommends,
                string.Format(UCL_CodeLocalize.Get("LibraryManage.Recommend.HeaderFmt"), m_Recommends.Count),
                UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
            if (!m_ShowRecommends) return;
            using (new GUILayout.VerticalScope("box"))
            {
                if (m_Recommends.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Recommend.Empty"), UCL_GUIStyle.LabelStyle);
                    return;
                }
                foreach (var r in m_Recommends)
                {
                    using (new GUILayout.VerticalScope("box"))
                    {
                        string builtBadge = string.IsNullOrEmpty(r.BookId) ? "" : $"  <color=#66ff99>✓ {r.BookId}</color>";
                        GUILayout.Label($"<b>{r.Title}</b>  <size=10>{r.Author}</size>  <color=#ffcc66>[{r.Status}]</color>{builtBadge}", UCL_GUIStyle.LabelStyle);
                        if (!string.IsNullOrEmpty(r.Synopsis))
                            GUILayout.Label($"<size=10><color=#dddddd>{TruncStr(r.Synopsis, 140)}</color></size>", UCL_GUIStyle.LabelStyle);
                    }
                }
            }
        }

        // ==================== Process actions ====================

        // 區塊職責：spawn library.py add-book
        // 物理意義：建一本新書 book.json；id/title/author 為必填，缺則擋下
        void DoAddBook()
        {
            if (string.IsNullOrWhiteSpace(m_NewBookId) || string.IsNullOrWhiteSpace(m_NewBookTitle) || string.IsNullOrWhiteSpace(m_NewBookAuthor))
            {
                Debug.LogWarning("[LibraryManage] add-book: id / title / author 都不能空");
                return;
            }
            var args = new List<string>
            {
                $"\"{LibraryPyPath()}\"", "add-book",
                "--id", m_NewBookId.Trim(),
                "--title", $"\"{m_NewBookTitle.Trim()}\"",
                "--author", $"\"{m_NewBookAuthor.Trim()}\"",
            };
            if (!string.IsNullOrWhiteSpace(m_NewBookTitleOriginal))
            {
                args.Add("--title-original");
                args.Add($"\"{m_NewBookTitleOriginal.Trim()}\"");
            }
            if (!string.IsNullOrWhiteSpace(m_NewBookReaderPersona))
            {
                args.Add("--reader-persona");
                args.Add(m_NewBookReaderPersona.Trim());
            }
            RunLibrary(args, $"add-book {m_NewBookId}");
            LoadData();
        }

        // 區塊職責：彈窗確認後 spawn library.py donate
        // 物理意義：捐贈會扣 token（走 Cmd_Treasury debit），destructive，故先 popup 確認
        void DoDonate()
        {
            if (string.IsNullOrWhiteSpace(m_DonateBook) || string.IsNullOrWhiteSpace(m_DonateDonor))
            {
                Debug.LogWarning("[LibraryManage] donate: book / donor 都不能空");
                return;
            }
            string tokens = string.IsNullOrWhiteSpace(m_DonateTokens) ? "100" : m_DonateTokens.Trim();
            string body = string.Format(UCL_CodeLocalize.Get("LibraryManage.Dialog.Donate.BodyFmt"),
                m_DonateBook.Trim(), m_DonateDonor.Trim(), tokens);
            UCL.Core.Page.UCL_OptionPage.Create(
                UCL_CodeLocalize.Get("LibraryManage.Dialog.Donate.Title"),
                body,
                new ButtonData(UCL_CodeLocalize.Get("Cancel"), () => { }),
                new ButtonData(UCL_CodeLocalize.Get("LibraryManage.Btn.Donate"),
                    () =>
                    {
                        var args = new List<string>
                        {
                            $"\"{LibraryPyPath()}\"", "donate",
                            "--book", m_DonateBook.Trim(),
                            "--donor", m_DonateDonor.Trim(),
                            "--tokens", tokens,
                        };
                        if (!string.IsNullOrWhiteSpace(m_DonatePersona))
                        {
                            args.Add("--donor-persona");
                            args.Add(m_DonatePersona.Trim());
                        }
                        RunLibrary(args, $"donate {m_DonateBook}");
                        LoadData();
                    },
                    UCL.Core.UI.UCL_GUIStyle.GetButtonStyle(Color.red))
            );
        }

        // 區塊職責：spawn library.py bookmark
        // 物理意義：記讀到哪章 + 可選心得；book / chapter 必填
        void DoBookmark()
        {
            if (string.IsNullOrWhiteSpace(m_BookmarkBook) || string.IsNullOrWhiteSpace(m_BookmarkChapter))
            {
                Debug.LogWarning("[LibraryManage] bookmark: book / chapter 都不能空");
                return;
            }
            var args = new List<string>
            {
                $"\"{LibraryPyPath()}\"", "bookmark",
                "--book", m_BookmarkBook.Trim(),
                "--chapter", m_BookmarkChapter.Trim(),
            };
            if (!string.IsNullOrWhiteSpace(m_BookmarkNote))
            {
                args.Add("--note");
                args.Add($"\"{m_BookmarkNote.Trim()}\"");
            }
            RunLibrary(args, $"bookmark {m_BookmarkBook}");
            LoadData();
        }

        // 區塊職責：實際 spawn library.py subprocess（對齊 UCL_LoginStatusPage.RunAwakening 的 async 雙 stream 讀法）
        // 物理意義：async stdout + stderr 並行消費，避免 .NET Process redirect deadlock
        // 數值影響：執行結果印到 Unity Console；exit!=0 印 error。實際資料變更由 library.py 寫檔
        void RunLibrary(List<string> args, string opLabel)
        {
            string scriptPath = LibraryPyPath();
            if (!File.Exists(scriptPath))
            {
                Debug.LogError($"[LibraryManage] library.py 不存在: {scriptPath}");
                return;
            }
            try
            {
                var stdoutSb = new StringBuilder();
                var stderrSb = new StringBuilder();
                using (var p = new Process())
                {
                    p.StartInfo.FileName = "python";
                    p.StartInfo.Arguments = string.Join(" ", args);
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.RedirectStandardOutput = true;
                    p.StartInfo.RedirectStandardError = true;
                    p.StartInfo.CreateNoWindow = true;
                    p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                    p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                    p.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                    p.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutSb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrSb.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit(30000);
                    string stdout = stdoutSb.ToString();
                    string stderr = stderrSb.ToString();
                    if (!string.IsNullOrEmpty(stdout))
                        Debug.Log($"[LibraryManage:{opLabel}] stdout:\n{stdout}");
                    if (!string.IsNullOrEmpty(stderr))
                        Debug.LogWarning($"[LibraryManage:{opLabel}] stderr:\n{stderr}");
                    if (p.ExitCode != 0)
                        Debug.LogError($"[LibraryManage:{opLabel}] library.py exit={p.ExitCode}");
                    else
                        Debug.Log($"[LibraryManage:{opLabel}] ✓ 完成");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[LibraryManage:{opLabel}] spawn failed: {e.Message}");
            }
        }

        string LibraryPyPath()
        {
            return Path.Combine(m_UCLCorePath, "Tools~", "AgentCommands", "library.py");
        }

        // 區塊職責：在系統檔案總管開啟指定資料夾（跨平台 best-effort）
        void OpenInExplorer(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    Debug.LogWarning($"[LibraryManage] 路徑不存在: {path}");
                    return;
                }
                Application.OpenURL("file://" + path.Replace('\\', '/'));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LibraryManage] 開啟資料夾失敗: {e.Message}");
            }
        }

        // 區塊職責：用系統預設程式開啟單一檔案（如 book.json → 預設文字編輯器）
        void OpenFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Debug.LogWarning($"[LibraryManage] 檔案不存在: {path}");
                    return;
                }
                Application.OpenURL("file://" + path.Replace('\\', '/'));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LibraryManage] 開啟檔案失敗: {e.Message}");
            }
        }

        // ==================== Helpers ====================

        static string TruncTs(string ts)
        {
            if (string.IsNullOrEmpty(ts)) return "";
            return ts.Length > 19 ? ts.Substring(0, 19) : ts;
        }

        static string TruncStr(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > maxLen ? s.Substring(0, maxLen) + "…" : s;
        }
    }
}
