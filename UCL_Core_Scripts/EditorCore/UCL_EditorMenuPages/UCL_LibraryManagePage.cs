
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using UCL.Core.EditorLib.AgentCommands.ReadingLibrary;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.Page
{
    // 區塊職責：圖書館管理 UI — 列出共享圖書館的所有書籍 + 外部漫畫庫 + 捐贈者 + 推薦書單，並提供新增/捐贈/書籤等操作
    // 物理意義：閱讀資料落各專案 repo root（per-project）：
    //          - AgentCommands/BookNotes/<slug>/book.json   每本書 metadata + 進度 + 人物/卷/標籤/書評
    //          - AgentCommands/BookNotes/_recommended/<slug>.json  推薦書單（T-split：一 rec 一檔；舊單檔 _recommended.json 自動 migrate 成本資料夾）
    //          - AgentCommands/Books/_donations.json         捐贈索引（誰付 token 認領了哪本書）
    //          - 外部漫畫庫（D:\commic 等）：透過 UCL_ProjectEditorPrefs 儲存路徑，支援本機漫畫探索
    //          工具 library.py 在 UCL_Core（跨專案共用），Page 直讀 JSON 顯示，變更操作走 process spawn 跑 library.py
    // 數值影響：UI 顯示純 read。Add/Donate/Bookmark 按鈕觸發外部 python process，改 book.json / _donations.json
    //
    // 設計理由 (Tim 2026-05-26 派 task)：
    //   原生 library.py 只有 CLI 介面，Tim / agent 想一眼看「圖書館裡有哪些書、誰捐的、進度到哪」沒有可視化介面。
    //   本 page 補可視化清單 + 常用操作 GUI fallback，結構對齊 UCL_LoginStatusPage（讀 per-project 資料 + spawn UCL_Core 工具）。
    [HelpURL("ucl_core:Docs~/{lang}/Mechanics/Reading_Library.md")]
    public class UCL_LibraryManagePage : UCL_CommonEditorPage
    {
        public static UCL_LibraryManagePage Create()
        {
            var page = new UCL_LibraryManagePage();
            UCL_GUIPageController.CurrentRenderIns.Push(page);
            return page;
        }

        // Process 註冊中心的 tag（硬規則：每顆外部 Process 都要登記）。
        const string PROC_TAG_PY = "library_py";

        public override string WindowName => UCL_CodeLocalize.Get("LibraryManage.Title");
        public override bool ShowInPageMenu => true;

        // 區塊職責：書籍 entry — 對齊**新 Library** 的 media（media.json + 其 work.json + readers/）
        // 物理意義：一筆 = 一個 media（一部作品的一種媒材，例如「迷宮飯的漫畫版」）。
        //          書名與作者住 work.json（跨媒材共用），media.json 只有 id / work_id / media_kind，
        //          所以本結構是兩個檔 join 出來的視圖，不是任何單一檔的鏡射。
        //          舊的 BookNotes/<slug>/book.json 欄位（進度 / 書籤 / 人物 / arc / 卷 / 書評）
        //          一律不再進來 —— 那些現在住 readers/<persona>/ 底下，是閱讀心得頁的職責。
        public class BookEntry
        {
            public string Id = "";            // media_id
            public string WorkId = "";
            public string MediaKind = "";     // comic / film / novel …
            public string Title = "";         // 取自 work.json；join 不到則退回 media_id
            public string TitleOriginal = "";
            public string Author = "";
            public List<string> Readers = new List<string>();   // readers/<persona> 目錄名
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

        // 區塊職責：推薦書單 entry — 對齊 BookNotes/_recommended/<slug>.json 一 rec 一檔 schema（T-split 2026-07-20）
        public class RecommendEntry
        {
            public string Title = "";
            public string Author = "";
            public string Status = "";
            public string BookId = "";       // 若已建檔則指向 book slug，否則空
            public string Synopsis = "";
            public string AddedDate = "";    // 排序用（對齊 library.py 的 added_date 再 title 序）
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

        // 區塊職責：外部漫畫庫（External Comics）state
        // 物理意義：m_ComicRootPath = 當前設定的漫畫根目錄；
        //          m_ExternalComics = 掃描到的外部漫畫系列清單；
        //          m_SelectedComicSeriesSlug = 當前選中的漫畫系列 slug；
        //          m_ComicDisplayOptions = 下拉選單顯示字串清單
        string m_ComicRootPath = "";
        string m_ComicRootPathInput = "";
        List<UCL_ReadingLibraryIO.ExternalComicSeries> m_ExternalComics = new List<UCL_ReadingLibraryIO.ExternalComicSeries>();
        string m_SelectedComicSeriesSlug = "";
        List<string> m_ComicDisplayOptions = new List<string>();

        // 區塊職責：捐贈表單 state
        // 物理意義：Tim 輸入要捐的書 slug + 捐贈者 bank id + token 數，按「捐贈」後 spawn library.py donate
        string m_DonateBook = "";
        string m_DonateDonor = "claude-da-xiaojie";
        string m_DonateTokens = "100";
        string m_DonatePersona = "";

        // 區塊職責：BookNotes 下拉選單 state（對齊 UCL_AffinitySystemPage 的 persona picker 模式）
        // 物理意義：m_SelectedBookId = 當前選中的 BookNotes id；m_BookPickerDic 給 PopupSearchCache 暫存搜尋 state
        //          m_BookDisplayOptions = 下拉顯示字串清單（「title (id)」），index 對齊 m_Books 排序後順序
        string m_SelectedBookId = "";
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();
        // 區塊職責：各 section 的折疊狀態 — **刻意跟 m_Dic 分開**（比照 UCL_ControlPanelPage）
        // 物理意義：折疊是使用者的 UI 偏好（該長存）；PopupSearchCache 是衍生資料（選項變了該失效）。
        // 血證（2026-07-29 Tim QA, UCL_ChatTavernAdminPage）：兩者共用同一個 dictionary 時，
        //          資料重載路徑上的 dic.Clear() 會把折疊值一併清掉 → 下一幀退回 iDefaultValue，
        //          症狀是「按某個開關就自動展開、而且收不起來」，看起來像 key 撞名，實際是共用快取被清。
        //          本頁的 m_Dic 目前沒有 Clear 路徑，但 LoadData() 已經在 Clear 一堆集合了 ——
        //          哪天有人順手加一行 m_Dic.Clear() 就會踩中，先分開比事後查便宜。
        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();
        List<string> m_BookDisplayOptions = new List<string>();

        // 區塊職責：全文書庫（Books/）下拉選單 state
        // 物理意義：m_FullBooks = Books/*/ 掃到的全文書；m_SelectedFullBook = 當前選中 slug；
        //          m_FullBookDisplayOptions = 下拉顯示「title (slug)」
        List<BookFullEntry> m_FullBooks = new List<BookFullEntry>();
        string m_SelectedFullBook = "";
        List<string> m_FullBookDisplayOptions = new List<string>();

        string m_AgentCommandsDir = "";
        string m_BookNotesDir = "";
        string m_LibraryDir = "";
        string m_BooksDir = "";
        string m_UCLCorePath = "";

        public override void Init(UCL_GUIPageController p_Controller)
        {
            base.Init(p_Controller);
            // 區塊：路徑解析
            // 物理意義：BookNotes / Books 落 per-project repo root；library.py 在 UCL_Core 給 process spawn
            m_AgentCommandsDir = UCL_RepoPath.AgentCommandsDir;
            m_BookNotesDir = Path.Combine(m_AgentCommandsDir, "BookNotes");
            // 新 Library 根 —— 書籍索引的事實源（舊的 BookNotes/<slug>/ 已空）
            m_LibraryDir = Path.Combine(m_BookNotesDir, "Library");
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
        /// 物理意義：scan BookNotes/*/book.json + 讀 Books/_donations.json + BookNotes/_recommended/*.json（退回舊單檔）
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

            // 區塊：scan 新 Library —— works/<work-id>/work.json + media/<media-id>/media.json
            // 物理意義：**舊的 BookNotes/<slug>/book.json 已經完全不存在了**（2026-08-07 實測：
            //          BookNotes/ 底下只剩 Archive / Library / _migration 三個目錄，零本 book.json）。
            //          原本這裡掃的是那個空掉的 store，所以清單永遠是空的、數量永遠是 0 ——
            //          而它不會報錯，只會安靜地顯示「找不到」。改成掃新 Library。
            //          資料模型：work（作品，跨媒材共用書名/作者）→ media（媒材，comic/film/…）
            //          → readers/<persona>（每位讀者一份進度）。本頁列到 media 這一層，
            //          因為閱讀心得是掛在 media 上的，而 work 只是它的書名來源。
            // 數值影響：純唯讀。缺 work.json 的 media 仍會列出（書名退回 media_id），
            //          不因為 join 不到就整筆吞掉 —— 吞掉的話清單少一本沒人看得出來。
            var workById = new Dictionary<string, JsonData>();
            string worksDir = Path.Combine(m_LibraryDir, "works");
            if (Directory.Exists(worksDir))
            {
                foreach (var dir in Directory.GetDirectories(worksDir))
                {
                    string workJson = Path.Combine(dir, "work.json");
                    if (!File.Exists(workJson)) continue;
                    try
                    {
                        var jd = JsonData.ParseJson(File.ReadAllText(workJson));
                        if (jd != null && jd.IsObject) workById[Path.GetFileName(dir)] = jd;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[LibraryManage] 讀取失敗 {workJson}: {e.Message}");
                    }
                }
            }

            string mediaRoot = Path.Combine(m_LibraryDir, "media");
            if (Directory.Exists(mediaRoot))
            {
                foreach (var dir in Directory.GetDirectories(mediaRoot))
                {
                    string mediaId = Path.GetFileName(dir);
                    string mediaJson = Path.Combine(dir, "media.json");
                    var entry = new BookEntry { Id = mediaId, Title = mediaId };
                    try
                    {
                        if (File.Exists(mediaJson))
                        {
                            var md = JsonData.ParseJson(File.ReadAllText(mediaJson));
                            if (md != null && md.IsObject)
                            {
                                entry.WorkId = md.GetString("work_id", "");
                                entry.MediaKind = md.GetString("media_kind", "");
                            }
                        }
                        // 書名 / 作者來自 work.json —— media.json 本身只有 id 與種類，不重複存書名
                        if (!string.IsNullOrEmpty(entry.WorkId) && workById.TryGetValue(entry.WorkId, out var wd))
                        {
                            entry.Title = wd.GetString("title", mediaId);
                            entry.TitleOriginal = wd.GetString("title_original", "");
                            entry.Author = wd.GetString("author", "");
                        }
                        // 讀者清單：readers/<persona>/ 一位一個目錄
                        string readersDir = Path.Combine(dir, "readers");
                        if (Directory.Exists(readersDir))
                        {
                            foreach (var rd in Directory.GetDirectories(readersDir))
                                entry.Readers.Add(Path.GetFileName(rd));
                            entry.Readers.Sort(StringComparer.OrdinalIgnoreCase);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[LibraryManage] 讀取失敗 {mediaJson}: {e.Message}");
                    }
                    m_Books.Add(entry);
                }
                m_Books.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
                m_BookDisplayOptions.Clear();
                foreach (var b in m_Books)
                {
                    string kind = string.IsNullOrEmpty(b.MediaKind) ? "" : $" ‧ {b.MediaKind}";
                    m_BookDisplayOptions.Add($"{b.Title}{kind} ({b.Id})");
                }
                if (string.IsNullOrEmpty(m_SelectedBookId) && m_Books.Count > 0)
                    m_SelectedBookId = m_Books[0].Id;
            }

            // 區塊：讀推薦書單（T-split 2026-07-20：優先 _recommended/ 資料夾一 rec 一檔；退回舊單檔）
            // 物理意義：想讀但未必建檔的書。library.py 是 write-owner（Page 的寫走 spawn library.py），Page 這裡只直讀顯示。
            try
            {
                // local：把一個 rec JsonData 物件收進 m_Recommends（資料夾/舊陣列共用）
                void AddRec(JsonData r)
                {
                    if (r == null || !r.IsObject) return;
                    string title = r.GetString("title", "");
                    if (string.IsNullOrEmpty(title)) return;
                    m_Recommends.Add(new RecommendEntry
                    {
                        Title = title,
                        Author = r.GetString("author", ""),
                        Status = r.GetString("status", ""),
                        BookId = r.GetString("book_id", ""),
                        Synopsis = r.GetString("synopsis", ""),
                        AddedDate = r.GetString("added_date", ""),
                    });
                }

                string recDir = Path.Combine(m_BookNotesDir, "_recommended");
                if (Directory.Exists(recDir))
                {
                    // 新格式：資料夾內一 rec 一檔
                    foreach (var fp in Directory.GetFiles(recDir, "*.json"))
                    {
                        try { AddRec(JsonData.ParseJson(File.ReadAllText(fp))); }
                        catch (Exception e) { Debug.LogWarning($"[LibraryManage] rec file load fail {Path.GetFileName(fp)}: {e.Message}"); }
                    }
                }
                else
                {
                    // legacy fallback：舊單檔 _recommended.json 的 recommendations[]（migration 前）
                    string recPath = Path.Combine(m_BookNotesDir, "_recommended.json");
                    if (File.Exists(recPath))
                    {
                        var jd = JsonData.ParseJson(File.ReadAllText(recPath));
                        if (jd != null && jd.IsObject && jd.Dic != null
                            && jd.Dic.TryGetValue("recommendations", out var arr) && arr != null && arr.IsArray)
                        {
                            for (int i = 0; i < arr.Count; i++) AddRec(arr[i]);
                        }
                    }
                }
                // 穩定排序對齊 library.py（added_date 再 title）
                m_Recommends.Sort((a, b) =>
                {
                    int c = string.CompareOrdinal(a.AddedDate, b.AddedDate);
                    return c != 0 ? c : string.CompareOrdinal(a.Title, b.Title);
                });
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

            // 區塊：外部漫畫庫掃描（唯讀快取，不每幀走目錄樹）
            m_ComicRootPath = UCL_ReadingLibraryIO.GetComicRoot();
            m_ComicRootPathInput = m_ComicRootPath;
            m_ExternalComics = UCL_ReadingLibraryIO.ScanExternalComics(m_ComicRootPath);
            m_ComicDisplayOptions.Clear();
            foreach (var c in m_ExternalComics)
            {
                string statusIcon = c.Status == UCL_ReadingLibraryIO.ComicMatchStatus.Synced ? "🟢"
                    : (c.Status == UCL_ReadingLibraryIO.ComicMatchStatus.MissingSource ? "🟡" : "⚪");
                string volInfo = c.Volumes.Count > 0 ? $" ‧ {c.Volumes.Count}卷 {c.TotalChapters}話" : " ‧ 0話";
                m_ComicDisplayOptions.Add($"{statusIcon} {c.SeriesName}{volInfo} ({c.MediaId})");
            }
            if ((string.IsNullOrEmpty(m_SelectedComicSeriesSlug) || m_ExternalComics.FindIndex(x => x.Slug == m_SelectedComicSeriesSlug) < 0)
                && m_ExternalComics.Count > 0)
            {
                m_SelectedComicSeriesSlug = m_ExternalComics[0].Slug;
            }
        }

        // 區塊職責：頁面主體 — 各項目分類為可折疊 section（Tim 2026-08-07 要求，比照 UCL_ControlPanelPage）
        // 物理意義：**關鍵操作一律畫在折疊外層 header**，收合後仍可一鍵操作；折疊內只放清單與低頻明細。
        //          預設開合依使用頻率：外部漫畫庫/書籍索引/全文書庫預設展開，捐贈/表單/推薦預設收合。
        protected override void ContentOnGUI()
        {
            DrawExternalComicsSection();
            GUILayout.Space(8);
            DrawBookNotesSection();
            GUILayout.Space(8);
            DrawBooksFullSection();
            GUILayout.Space(8);
            DrawDonations();
            GUILayout.Space(8);
            DrawDonateForm();
            GUILayout.Space(8);
            DrawRecommendations();
        }

        // 區塊職責：外部漫畫庫區塊 — 設定本機漫畫根目錄（例如 D:\commic）+ 探索漫畫作品 + 挑選閱讀
        // 物理意義：路徑儲存於 UCL_ProjectEditorPrefs（不上 git、per-project 隔離），
        //          同步輸出 .comic_root.local 快照給 Python 唯讀消費。
        //          掃描快取只在 LoadData() / 重新整理時執行，OnGUI 零開銷。
        void DrawExternalComicsSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "ExternalComicsFold", 21, iDefaultValue: true);
                    GUILayout.Label(string.Format("<b>🎨 外部漫畫庫 (External Comics)</b>  ({0})", m_ExternalComics.Count),
                        UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("🔄 重新掃描", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        LoadData();
                    }
                    if (!string.IsNullOrEmpty(m_ComicRootPath) && Directory.Exists(m_ComicRootPath))
                    {
                        if (GUILayout.Button("📂 開啟漫畫根目錄", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            OpenInExplorer(m_ComicRootPath);
                        }
                    }
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                // ── 路徑設定列 ──
                using (new GUILayout.HorizontalScope("box"))
                {
                    GUILayout.Label("漫畫庫路徑:", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_ComicRootPathInput = GUILayout.TextField(m_ComicRootPathInput, UCL_GUIStyle.TextFieldStyle);

                    if (GUILayout.Button("📁 瀏覽", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
#if UNITY_EDITOR
                        string defaultDir = Directory.Exists(m_ComicRootPathInput) ? m_ComicRootPathInput : "";
                        string selected = UnityEditor.EditorUtility.OpenFolderPanel("選擇外部漫畫庫目錄", defaultDir, "");
                        if (!string.IsNullOrEmpty(selected))
                        {
                            m_ComicRootPathInput = selected;
                            UCL_ReadingLibraryIO.SetComicRoot(selected);
                            LoadData();
                        }
#endif
                    }

                    if (m_ComicRootPathInput != m_ComicRootPath)
                    {
                        if (GUILayout.Button("💾 套用", UCL_GUIStyle.GetButtonStyle(new Color(0.7f, 1f, 0.7f)), GUILayout.ExpandWidth(false)))
                        {
                            UCL_ReadingLibraryIO.SetComicRoot(m_ComicRootPathInput);
                            LoadData();
                        }
                    }

                    if (!string.IsNullOrEmpty(m_ComicRootPath))
                    {
                        if (GUILayout.Button("❌ 清除", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            m_ComicRootPathInput = "";
                            UCL_ReadingLibraryIO.SetComicRoot("");
                            LoadData();
                        }
                    }
                }

                if (string.IsNullOrEmpty(m_ComicRootPath))
                {
                    GUILayout.Label("<color=#aaaaaa><i>（尚未設定外部漫畫庫路徑，可於上方輸入框或點擊「📁 瀏覽」指定如 D:\\commic 之目錄；設定僅儲存於本機 EditorPrefs 不上 Git）</i></color>", UCL_GUIStyle.LabelStyle);
                    return;
                }

                if (!Directory.Exists(m_ComicRootPath))
                {
                    GUILayout.Label($"<color=#ffaa44>⚠ 目錄不存在或未掛載：{m_ComicRootPath}</color>", UCL_GUIStyle.LabelStyle);
                    return;
                }

                if (m_ExternalComics.Count == 0)
                {
                    GUILayout.Label($"（目錄內未找到任何漫畫子資料夾：{m_ComicRootPath}）", UCL_GUIStyle.LabelStyle);
                    return;
                }

                // ── 漫畫下拉選單列 ──
                using (new GUILayout.HorizontalScope("box"))
                {
                    GUILayout.Label("挑選漫畫:", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    int curIdx = m_ExternalComics.FindIndex(x => x.Slug == m_SelectedComicSeriesSlug);
                    if (curIdx < 0) curIdx = 0;
                    int newIdx = UCL_GUILayout.PopupSearchCache(curIdx, m_ComicDisplayOptions, m_Dic.GetSubDic("ComicPicker"), "ExternalComicPicker");
                    if (newIdx >= 0 && newIdx < m_ExternalComics.Count) m_SelectedComicSeriesSlug = m_ExternalComics[newIdx].Slug;
                    GUILayout.FlexibleSpace();
                }

                // ── 選中漫畫詳細面板 ──
                var comic = m_ExternalComics.Find(x => x.Slug == m_SelectedComicSeriesSlug);
                if (comic == null) return;

                using (new GUILayout.VerticalScope("box"))
                {
                    // 標題列
                    string statusBadge = comic.Status switch
                    {
                        UCL_ReadingLibraryIO.ComicMatchStatus.Synced => "<color=#44ff88>[🟢 已在 Library 建檔]</color>",
                        UCL_ReadingLibraryIO.ComicMatchStatus.MissingSource => "<color=#ffcc00>[🟡 來源失聯 (Missing Source)]</color>",
                        _ => "<color=#aaaaaa>[⚪ 未建檔 (Unregistered)]</color>"
                    };

                    GUILayout.Label($"<b><size=15>{comic.SeriesName}</size></b>  {statusBadge}　<size=11>({comic.MediaId})</size>", UCL_GUIStyle.LabelStyle);
                    if (!string.IsNullOrEmpty(comic.RegisteredTitle) && comic.RegisteredTitle != comic.SeriesName)
                    {
                        GUILayout.Label($"<i>Library 書名：{comic.RegisteredTitle}</i>", UCL_GUIStyle.LabelStyle);
                    }

                    GUILayout.Label($"<b>卷數</b>: {comic.Volumes.Count} 卷　|　<b>總話數</b>: {comic.TotalChapters} 話　|　<b>總圖片</b>: {comic.TotalPages} 張", UCL_GUIStyle.LabelStyle);

                    // 卷數清單展開
                    if (comic.Volumes.Count > 0)
                    {
                        GUILayout.Space(4);
                        GUILayout.Label("<b>卷話明細：</b>", UCL_GUIStyle.LabelStyle);
                        foreach (var v in comic.Volumes)
                        {
                            string chRange = v.Chapters.Count > 0 ? $"{v.Chapters[0]} ~ {v.Chapters[v.Chapters.Count - 1]} ({v.Chapters.Count} 話)" : "0 話";
                            using (new GUILayout.HorizontalScope())
                            {
                                GUILayout.Label($"  • <b>Vol.{v.VolumeLabel}</b> ({v.FolderName})：{chRange}，共 {v.PageCount} 頁", UCL_GUIStyle.LabelStyle);
                                if (GUILayout.Button("📂 開啟該卷", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                                {
                                    OpenInExplorer(v.FolderPath);
                                }
                                GUILayout.FlexibleSpace();
                            }
                        }
                    }

                    GUILayout.Space(6);

                    // 操作按鈕列
                    using (new GUILayout.HorizontalScope())
                    {
                        if (comic.Volumes.Count > 0 && Directory.Exists(comic.Volumes[0].FolderPath))
                        {
                            if (GUILayout.Button("📂 開啟漫畫資料夾", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            {
                                OpenInExplorer(comic.Volumes[0].FolderPath);
                            }
                        }

                        if (comic.Status == UCL_ReadingLibraryIO.ComicMatchStatus.Synced)
                        {
                            if (GUILayout.Button("📖 開啟閱讀心得頁",
                                UCL_GUIStyle.GetButtonStyle(new Color(0.75f, 0.95f, 0.75f)), GUILayout.ExpandWidth(false)))
                            {
                                UCL_ReadingNotesManagePage.CreateForTitle(string.IsNullOrEmpty(comic.RegisteredTitle) ? comic.SeriesName : comic.RegisteredTitle);
                            }
                        }
                        else if (comic.Status == UCL_ReadingLibraryIO.ComicMatchStatus.Unregistered)
                        {
                            if (GUILayout.Button("📥 初始化 Library Media",
                                UCL_GUIStyle.GetButtonStyle(new Color(0.6f, 0.85f, 1f)), GUILayout.ExpandWidth(false)))
                            {
                                string initLog = UCL_ReadingLibraryIO.MediaInit(
                                    comic.Slug, comic.MediaId, "comic", "apex-one",
                                    comic.SeriesName, "", "", 5,
                                    null, null, out string initErr);
                                if (!string.IsNullOrEmpty(initErr))
                                {
                                    Debug.LogError($"[LibraryManage] MediaInit failed: {initErr}");
                                }
                                else
                                {
                                    Debug.Log($"[LibraryManage] MediaInit success: {initLog}");
                                    LoadData();
                                }
                            }
                        }
                        else if (comic.Status == UCL_ReadingLibraryIO.ComicMatchStatus.MissingSource)
                        {
                            if (GUILayout.Button("📖 檢視既有閱讀心得",
                                UCL_GUIStyle.GetButtonStyle(new Color(1f, 0.9f, 0.6f)), GUILayout.ExpandWidth(false)))
                            {
                                UCL_ReadingNotesManagePage.CreateForTitle(comic.RegisteredTitle);
                            }
                        }

                        GUILayout.FlexibleSpace();
                    }
                }
            }
        }


        // 區塊職責：書籍索引區塊 — 下拉選一本書 → 導覽到該書的閱讀心得頁 / 資料夾。
        // 物理意義：**本區塊不再顯示 BookNotes 的閱讀內容**（進度 / 書籤 / 人物 / arc / 卷 / 書評）。
        //          那份 store 已廢棄（Tim 2026-08-07），閱讀心得的唯一入口改為 UCL_ReadingNotesManagePage；
        //          本頁只保留「有哪些書」與「怎麼過去」，避免同一份心得在兩個頁面各有一套顯示與判讀。
        //          留下的資料夾 / book.json 按鈕是**人工遷移**用的（要找得回 Archive 對應筆記），
        //          它們開的是檔案總管，不是在本頁重新詮釋內容。
        // 注意命名：本區塊掃的是 BookNotes 目錄（舊筆記索引），非 Books（全文書庫，在 AgentCommands/Books）。
        void DrawBookNotesSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                // header：折疊鈕 + 標題 + **關鍵操作提到折疊外層**（收合後仍能開閱讀心得頁）
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "BookIndexFold", 21, iDefaultValue: true);
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.BookNotes.HeaderFmt"), m_Books.Count),
                        UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.OpenReadingNotes"),
                            UCL_GUIStyle.GetButtonStyle(new Color(0.75f, 0.95f, 0.75f)), GUILayout.ExpandWidth(false)))
                    {
                        UCL_ReadingNotesManagePage.Create();
                    }
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                if (m_Books.Count == 0)
                {
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.BookNotes.EmptyFmt"), m_BookNotesDir), UCL_GUIStyle.LabelStyle);
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

                // 區塊：取選中的書，畫「身分 + 導覽」面板
                var b = m_Books.Find(x => x.Id == m_SelectedBookId);
                if (b == null) return;
                DrawBookNavPanel(b);
            }
        }

        // 區塊職責：單一書籍的「身分 + 導覽」面板 —— 取代原本的 BookNotes 明細面板。
        // 物理意義：只回答兩件事 —— **這是哪本書**、**要去哪裡看它的心得**。
        //          原本這裡展開的進度 / 書籤 / 人物 / arc / 卷 / 書評全部移除：那些欄位讀的是已廢棄的
        //          BookNotes store（Tim 2026-08-07 拍板），留著會讓同一份心得有兩個顯示來源，
        //          而兩邊遲早不一致 —— 到時候看的人無從判斷哪一份才是現況。
        // 數值影響：純唯讀導覽。開頁按鈕以**書名**帶入 UCL_ReadingNotesManagePage 並自動搜尋；
        //          資料夾 / book.json 按鈕開檔案總管，供人工遷移時對照原件。
        void DrawBookNavPanel(BookEntry b)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                // 標題列：書名 ‧ 媒材種類（media_id）
                string kind = string.IsNullOrEmpty(b.MediaKind)
                    ? ""
                    : $"  <color=#88ccff>{b.MediaKind}</color>";
                GUILayout.Label($"<b><size=15>{b.Title}</size></b>{kind}　({b.Id})", UCL_GUIStyle.LabelStyle);
                if (!string.IsNullOrEmpty(b.TitleOriginal))
                    GUILayout.Label($"<i>{b.TitleOriginal}</i>", UCL_GUIStyle.LabelStyle);
                if (!string.IsNullOrEmpty(b.Author))
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Detail.AuthorFmt"), b.Author), UCL_GUIStyle.LabelStyle);

                // 讀者清單 —— 只列「有誰讀過」，進度與心得一律去閱讀心得頁看，本頁不複述。
                if (b.Readers.Count > 0)
                {
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Detail.ReadersFmt"),
                        b.Readers.Count, string.Join(", ", b.Readers)), UCL_GUIStyle.LabelStyle);
                }

                GUILayout.Space(4);

                // 區塊：導覽按鈕列
                // 物理意義：📖 閱讀心得 = 帶書名開 UCL_ReadingNotesManagePage（該頁跨 Archive 與新 Library 比對標題）；
                //          資料夾 / media.json 開檔案總管與檔案，不在本頁重新詮釋內容。
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.OpenReadingNotesForBook"),
                            UCL_GUIStyle.GetButtonStyle(new Color(0.75f, 0.95f, 0.75f)), GUILayout.ExpandWidth(false)))
                    {
                        // 以書名而非 id 定位：閱讀心得頁是跨 Archive 與 Library 比對 metadata 標題的，
                        // 而 Archive 那側用的是舊 slug，兩邊只有書名對得起來。
                        UCL_ReadingNotesManagePage.CreateForTitle(b.Title);
                    }
                    if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.NotesFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        OpenInExplorer(Path.Combine(m_LibraryDir, "media", b.Id));
                    if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.OpenJson"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        OpenFile(Path.Combine(m_LibraryDir, "media", b.Id, "media.json"));
                    GUILayout.FlexibleSpace();
                }
            }
        }

        // 區塊職責：全文書庫（Books/）區塊 — 下拉選一本全文書 + 顯示捐贈者/資訊 + 跳轉編輯 Page 按鈕
        // 物理意義：跟 BookNotes（筆記）區分，本區塊操作的是 AgentCommands/Books/ 的實際全文。
        //          「✏ 編輯書籍」按鈕 new 一個 UCL_BookEditPage 設好 slug 後 Push（跳轉到章節編輯 prototype）。
        void DrawBooksFullSection()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                // header：折疊鈕 + 標題 + **關鍵操作（開全文資料夾）提到折疊外層**
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "BooksFullFold", 21, iDefaultValue: true);
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Books.HeaderFmt"), m_FullBooks.Count),
                        UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.FullTextFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        OpenInExplorer(m_BooksDir);
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                if (m_FullBooks.Count == 0)
                {
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Books.EmptyFmt"), m_BooksDir), UCL_GUIStyle.LabelStyle);
                    return;
                }

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
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "DonationsFold", 21, iDefaultValue: false);
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Donations.HeaderFmt"), m_Donations.Count),
                        UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

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


        // 區塊職責：捐贈表單 — spawn library.py donate（會扣 token，故確認後再跑）
        void DrawDonateForm()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "DonateFormFold", 21, iDefaultValue: false);
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Donate.Title"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Field.Book"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_DonateBook = GUILayout.TextField(m_DonateBook, UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Field.Donor"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    m_DonateDonor = GUILayout.TextField(m_DonateDonor, UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                }
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Field.DonorPersona"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    m_DonatePersona = GUILayout.TextField(m_DonatePersona, UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(180)));
                    GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Field.DonorTokens"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    m_DonateTokens = GUILayout.TextField(m_DonateTokens, UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                    if (GUILayout.Button(UCL_CodeLocalize.Get("LibraryManage.Btn.Donate"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        DoDonate();
                    }
                }
                GUILayout.Label(UCL_CodeLocalize.Get("LibraryManage.Donate.Hint"), UCL_GUIStyle.LabelStyle);
            }
        }


        // 區塊職責：推薦書單（預設收合，點開展看）
        // 物理意義：折疊改走 m_FoldDic —— 原本用自己的 bool 欄位，與其他 section 兩套寫法；
        //          統一成同一個 idiom，之後加 section 的人只會看到一種樣板。
        void DrawRecommendations()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool aShow;
                using (new GUILayout.HorizontalScope())
                {
                    aShow = UCL_GUILayout.Toggle(m_FoldDic, "RecommendsFold", 21, iDefaultValue: false);
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("LibraryManage.Recommend.HeaderFmt"), m_Recommends.Count),
                        UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!aShow) return;

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
                    // 硬規則：每顆外部 Process 都要登記（Coding_Standards.md「外部 Process」）。
                    // using 宣告 → 正常結束與例外路徑都會反登記，成對性由語言保證。
                    using var procScope_ = UCL_ProcessRegistryService.RegisterScope(
                        p, PROC_TAG_PY, "library.py", nameof(UCL_LibraryManagePage));
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
