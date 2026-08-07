// 區塊職責：閱讀心得入口頁 — 以作品名稱定位 Archive 與新 Library 的「可手動開啟」資料夾。
// 物理意義：遷移期間舊資料仍保留在 Archive；本頁只掃 metadata 產生入口，不讀章節正文、
//          不推論 Archive 的 media / reader / 合併關係，避免管理 UI 變成 legacy reader。
// 數值影響：純唯讀索引與檔案總管導覽；不改寫 Archive、Library 或 migration registry。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.EditorLib;
using UCL.Core.JsonLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 閱讀心得管理入口。用作品標題找出 legacy Archive 與新 Library 的 metadata entry，
    /// 讓人工遷移前能找到原件，但不讓新流程直接消費 legacy 內容。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/Plan/Plan_Library_Media_Migration.md")]
    public class UCL_ReadingNotesManagePage : UCL_CommonEditorPage
    {
        const string FoldSearch = "ReadingNotesSearchFold";
        const string FoldResults = "ReadingNotesResultsFold";

        sealed class SearchEntry
        {
            public string Kind = "";
            public string Title = "";
            public string Detail = "";
            public string Path = "";
            // 只有 Library 命中才有：追回檢視按鈕用（Archive 是 legacy 唯讀，沒有 reader root）
            public string MediaId = "";
            public List<string> Readers = new List<string>();
        }

        readonly UCL_ObjectDictionary m_FoldDic = new UCL_ObjectDictionary();
        readonly List<SearchEntry> m_Results = new List<SearchEntry>();
        string m_Query = "";
        string m_AgentCommandsDir = "";
        string m_ArchiveDir = "";
        string m_LibraryDir = "";
        string m_LastStatus = "輸入作品名稱後按搜尋。搜尋只讀 metadata；Archive 內容仍須人工整理後遷移。";

        public override string WindowName => "閱讀心得管理";
        public override bool ShowInPageMenu => true;

        public static UCL_ReadingNotesManagePage Create() => UCL_EditorPage.Create<UCL_ReadingNotesManagePage>();

        // 區塊職責：帶著書名開頁 —— 給外部頁（UCL_LibraryManagePage）「開啟對應該書的頁面」用。
        // 物理意義：接合鍵取**書名**而不是 id。BookNotes 用 slug（`arakawa`）、新 Library 用
        //          media_id（`comic-arakawa-under-the-bridge`），兩套命名對不起來；而本頁本來就是
        //          以 metadata 標題跨 Archive 與 Library 比對，書名是目前唯一兩邊都有的鍵。
        //          （若日後建了 slug ↔ media_id 對應表，這裡才有條件改成精確定位。）
        // 數值影響：純唯讀搜尋。書名為空則只開頁不搜尋 —— 維持「空手入頁」的原行為，
        //          不要因為呼叫端沒給書名就跑一次空字串搜尋（那會把全庫都撈出來）。
        public static UCL_ReadingNotesManagePage CreateForTitle(string iTitle)
        {
            var aPage = Create();
            if (!string.IsNullOrWhiteSpace(iTitle))
            {
                aPage.m_Query = iTitle.Trim();
                aPage.Search();
            }
            return aPage;
        }

        public override void Init(UCL_GUIPageController p_Controller)
        {
            base.Init(p_Controller);
            m_AgentCommandsDir = UCL_RepoPath.AgentCommandsDir;
            m_ArchiveDir = Path.Combine(m_AgentCommandsDir, "BookNotes", "Archive");
            m_LibraryDir = Path.Combine(m_AgentCommandsDir, "BookNotes", "Library");
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            if (GUILayout.Button("📂 開啟 BookNotes", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                UCL_ExplorerUtil.Open(Path.Combine(m_AgentCommandsDir, "BookNotes"), nameof(UCL_ReadingNotesManagePage));
        }

        protected override void ContentOnGUI()
        {
            DrawBrowsePanel();
            GUILayout.Space(8);
            DrawSearchPanel();
            GUILayout.Space(8);
            DrawResultsPanel();
        }

        // ===========================================================
        // 區塊職責：三層下拉瀏覽（Tim 2026-08-07）—— 媒材 kind → 該 kind 所有筆記 → 該筆記所有 persona。
        // 物理意義：搜尋是「知道書名找入口」，瀏覽是「不知道有什麼、逛」—— 兩條路互補。
        //          資料走 UCL_ReadingLibraryIO.ListMediaEntries（只讀 metadata），檢視按鈕
        //          重用同一個 LoadRecall / DrawRecallPanel —— 讀取永遠只有服務層那一段。
        // 數值影響：純唯讀；清單在開頁載一次，「🔄」手動重整（Library 寫入頻率低，不每幀掃碟）。
        // ===========================================================
        const string FoldBrowse = "ReadingNotesBrowseFold";
        List<AgentCommands.ReadingLibrary.UCL_ReadingLibraryIO.MediaEntry> m_MediaEntries;
        int m_BrowseKindSel = 0;
        int m_BrowseMediaSel = 0;
        int m_BrowseReaderSel = 0;

        void EnsureMediaEntries(bool forceReload = false)
        {
            if (m_MediaEntries == null || forceReload)
            {
                m_MediaEntries = AgentCommands.ReadingLibrary.UCL_ReadingLibraryIO.ListMediaEntries();
            }
        }

        void DrawBrowsePanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool show;
                using (new GUILayout.HorizontalScope())
                {
                    show = UCL_GUILayout.Toggle(m_FoldDic, FoldBrowse, 21, iDefaultValue: true);
                    if (GUILayout.Button("🔄", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        EnsureMediaEntries(forceReload: true);
                    }
                    GUILayout.Label("<b>🗂 全庫瀏覽</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

                    GUILayout.FlexibleSpace();
                }
                if (!show) return;
                EnsureMediaEntries();
                if (m_MediaEntries.Count == 0)
                {
                    GUILayout.Label("（Library 沒有任何 media）", DimLabelStyle);
                    return;
                }

                // ── 第一層：媒材 kind ──
                var kinds = new List<string>();
                foreach (var e in m_MediaEntries)
                {
                    string k = string.IsNullOrEmpty(e.MediaKind) ? "(未標)" : e.MediaKind;
                    if (!kinds.Contains(k)) kinds.Add(k);
                }
                kinds.Sort(StringComparer.Ordinal);
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("媒材", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    int kindNext = UCL_GUILayout.PopupSearchCache(
                        Mathf.Clamp(m_BrowseKindSel, 0, kinds.Count - 1), kinds, m_PickerDic, "BrowseKindPicker");
                    if (kindNext != m_BrowseKindSel)
                    {
                        m_BrowseKindSel = kindNext;
                        m_BrowseMediaSel = 0;    // 換 kind 就重置下游選擇 —— 舊 index 指向的是另一張清單
                        m_BrowseReaderSel = 0;
                    }
                    //GUILayout.FlexibleSpace(); 會擠壓下拉選單不好操作
                }
                string kind = kinds[Mathf.Clamp(m_BrowseKindSel, 0, kinds.Count - 1)];

                // ── 第二層：該 kind 底下的筆記（media）──
                var medias = m_MediaEntries.FindAll(
                    e => (string.IsNullOrEmpty(e.MediaKind) ? "(未標)" : e.MediaKind) == kind);
                if (medias.Count == 0)
                {
                    GUILayout.Label("（此媒材下沒有筆記）", DimLabelStyle);
                    return;
                }
                var mediaLabels = medias.ConvertAll(e => $"{e.Title}　({e.MediaId})");
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("筆記", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    int mediaNext = UCL_GUILayout.PopupSearchCache(
                        Mathf.Clamp(m_BrowseMediaSel, 0, medias.Count - 1), mediaLabels, m_PickerDic,
                        $"BrowseMediaPicker_{kind}");
                    if (mediaNext != m_BrowseMediaSel)
                    {
                        m_BrowseMediaSel = mediaNext;
                        m_BrowseReaderSel = 0;
                    }
                    //GUILayout.FlexibleSpace(); 會擠壓下拉選單不好操作
                }
                var media = medias[Mathf.Clamp(m_BrowseMediaSel, 0, medias.Count - 1)];

                // ── 第三層：該筆記底下的 persona 心得 ──
                if (media.Readers.Count == 0)
                {
                    GUILayout.Label("（這個 media 還沒有任何 reader root）", DimLabelStyle);
                    return;
                }
                using (new GUILayout.HorizontalScope())
                {
                    string reader = media.Readers[Mathf.Clamp(m_BrowseReaderSel, 0, media.Readers.Count - 1)];
                    bool isOpen = m_RecallHost == "browse" && m_RecallMediaId == media.MediaId
                                  && m_RecallReader == reader && !string.IsNullOrEmpty(m_RecallText);
                    //按鈕放在前方 避免版面不夠時跑到畫面外按不到
                    if (GUILayout.Button(isOpen ? "✕ 收合" : "📖 檢視心得", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        if (isOpen)
                        {
                            m_RecallText = "";
                            m_RecallMediaId = "";
                        }
                        else
                        {
                            m_RecallHost = "browse";
                            LoadRecall(media.MediaId, reader);
                        }
                    }


                    GUILayout.Label("persona", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    m_BrowseReaderSel = UCL_GUILayout.PopupSearchCache(
                        Mathf.Clamp(m_BrowseReaderSel, 0, media.Readers.Count - 1), media.Readers, m_PickerDic,
                        $"BrowseReaderPicker_{media.MediaId}");

                    //GUILayout.FlexibleSpace(); 會擠壓下拉選單不好操作
                }
                if (m_RecallHost == "browse" && m_RecallMediaId == media.MediaId
                    && !string.IsNullOrEmpty(m_RecallText))
                {
                    DrawRecallPanel();
                }
            }
        }

        // 檢視面板目前掛在哪個區塊（"browse" / "search"）—— 同一個 media 可能同時出現在
        // 瀏覽選擇與搜尋結果裡，不標 host 的話兩處會同時畫出同一份面板。
        string m_RecallHost = "";

        void DrawSearchPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool show;
                using (new GUILayout.HorizontalScope())
                {
                    show = UCL_GUILayout.Toggle(m_FoldDic, FoldSearch, 21, iDefaultValue: true);
                    GUILayout.Label("<b>🔎 作品入口搜尋</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    if (GUILayout.Button("搜尋", UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                        Search();
                    GUILayout.FlexibleSpace();
                }
                if (!show) return;

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("書名", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    m_Query = GUILayout.TextField(m_Query, UCL_GUIStyle.TextFieldStyle);
                }
                using (new GUILayout.HorizontalScope())
                {
                    // 已遷移 Archive 預設隱藏（Tim 2026-08-07）——「已遷移」的標記就是
                    // _migration/registry.json（Archive 不可修改，標記只能活在它外面）。
                    // 切換即重搜 —— 開關與清單對不上是最誤導人的畫面。
                    bool next = GUILayout.Toggle(m_ShowMigrated, " 顯示已遷移的 Archive（預設隱藏）",
                        GUILayout.ExpandWidth(false));
                    if (next != m_ShowMigrated)
                    {
                        m_ShowMigrated = next;
                        if (!string.IsNullOrEmpty((m_Query ?? "").Trim())) Search();
                    }
                    GUILayout.FlexibleSpace();
                }
                GUILayout.Label(m_LastStatus, UCL_GUIStyle.LabelStyle);
            }
        }

        void DrawResultsPanel()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool show;
                using (new GUILayout.HorizontalScope())
                {
                    show = UCL_GUILayout.Toggle(m_FoldDic, FoldResults, 21, iDefaultValue: true);
                    GUILayout.Label($"<b>📚 搜尋結果（{m_Results.Count}）</b>", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.FlexibleSpace();
                }
                if (!show) return;
                if (m_Results.Count == 0)
                {
                    GUILayout.Label("（尚未搜尋，或沒有以 metadata 標題命中的入口）", UCL_GUIStyle.LabelStyle);
                    return;
                }

                foreach (SearchEntry entry in m_Results)
                {
                    using (new GUILayout.VerticalScope("box"))
                    {
                        DrawEntryRecallRow(entry);
                        using (new GUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("📂 開啟", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                                UCL_ExplorerUtil.Open(entry.Path, nameof(UCL_ReadingNotesManagePage));
                            GUILayout.Label($"<b>{entry.Kind}</b>　{entry.Title}\n{entry.Detail}\n{entry.Path}", WrapLabelStyle);
                        }
                    }
                }
            }
        }

        // 區塊職責：單一結果列的追回控制列 + inline 檢視（2026-08-07 三方規格定案）。
        // 物理意義：
        //   · persona 用下拉（Tim：同書多讀者是常態）—— PopupSearchCache，選項為 0 時整列不畫
        //     （它對零選項會 LogError，這不是版面選擇）。
        //   · 檢視 **inline 展開在該列下方、互斥**（Sirius：從 LibraryManagePage 入口過來，
        //     work→media 一對多讓多結果是主路徑；比較行為要求「看的跟點的在一起」）。
        //   · Archive 列給 dim 提示不給鈕 —— 沒有 reader root 可讀；同名並排時「沒有鈕」
        //     要讀成狀態而不是故障。
        void DrawEntryRecallRow(SearchEntry entry)
        {
            if (string.IsNullOrEmpty(entry.MediaId))
            {
                // Archive（legacy）：說清楚為什麼不能追回，別讓人以為按鈕壞了
                GUILayout.Label("　（legacy —— 遷移到新格式後才可追回；遷移走 op=scan / migrate，本頁不代辦）",
                    DimLabelStyle);
                return;
            }
            if (entry.Readers.Count == 0)
            {
                GUILayout.Label("　（尚無任何 reader root —— 這個 media 還沒有人讀過）", DimLabelStyle);
                return;
            }
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("追回 persona：", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                m_ReaderSel.TryGetValue(entry.MediaId, out int cur);
                GUILayout.BeginHorizontal(GUILayout.MinWidth(UCL_GUIStyle.GetScaledSize(150)));
                int next = UCL_GUILayout.PopupSearchCache(Mathf.Clamp(cur, 0, entry.Readers.Count - 1), entry.Readers, m_PickerDic, $"RecallReaderPicker_{entry.MediaId}");
                GUILayout.EndHorizontal();
                if (next != cur) m_ReaderSel[entry.MediaId] = next;
                string reader = entry.Readers[Mathf.Clamp(next, 0, entry.Readers.Count - 1)];
                bool isOpen = m_RecallHost == "search" && m_RecallMediaId == entry.MediaId
                              && !string.IsNullOrEmpty(m_RecallText);
                if (GUILayout.Button(isOpen && m_RecallReader == reader ? "✕ 收合" : "📖 追回",
                        UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    if (isOpen && m_RecallReader == reader)
                    {
                        m_RecallText = "";
                        m_RecallMediaId = "";
                    }
                    else
                    {
                        m_RecallHost = "search";
                        LoadRecall(entry.MediaId, reader);   // 互斥：載入即把上一列（含瀏覽區）的展開換掉
                    }
                }
                GUILayout.FlexibleSpace();
            }
            // inline 展開：只有「目前展開的那一列」畫檢視面板（host 標記防瀏覽區同 media 重複畫）
            if (m_RecallHost == "search" && m_RecallMediaId == entry.MediaId
                && !string.IsNullOrEmpty(m_RecallText))
            {
                DrawRecallPanel();
            }
        }

        readonly Dictionary<string, int> m_ReaderSel = new Dictionary<string, int>();
        // PopupSearchCache 的內部狀態容器 —— 獨立於 m_FoldDic（折疊值與下拉快取共用一個
        // dict 的話，資料重載路徑的 Clear 會把折疊狀態一併吃掉）。
        readonly UCL_ObjectDictionary m_PickerDic = new UCL_ObjectDictionary();

        GUIStyle m_DimLabelStyle;
        GUIStyle DimLabelStyle => m_DimLabelStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
            wordWrap = true,
        };

        // ===========================================================
        // 區塊職責：追回檢視 —— 在頁內直接看 RenderRecall 的輸出（Tim QA 的主要對象）。
        // 物理意義：與 Cmd_Library op=recall 共用 UCL_ReadingLibraryIO.RenderRecall（唯一 schema
        //          實作者），本頁不長第二套讀取。「產生追回檔」= WriteRecallBrief，寫的也是
        //          Cmd 寫的那個檔（letters/<persona>/_reading_recall_<media>.md）。
        // 數值影響：檢視純讀；產檔會覆寫該 persona 的追回檔（機械產物，本來就每次重生成）。
        // ===========================================================
        string m_RecallMediaId = "";
        string m_RecallReader = "";
        string m_RecallText = "";
        bool m_RecallFull = false;   // 預設精簡（round 只列索引）—— 全文動輒數千行，QA 先看骨架
        Vector2 m_RecallScroll = Vector2.zero;
        // 最近一次「產生追回檔」寫出的路徑 —— 給「開啟檔案位置」鈕用（Tim 2026-08-07）。
        // 只在本次產檔成功後有值；換 media/reader 檢視就清掉，別讓按鈕開到上一本的檔。
        string m_LastBriefPath = "";

        void LoadRecall(string mediaId, string reader)
        {
            m_RecallMediaId = mediaId;
            m_RecallReader = reader;
            m_LastBriefPath = "";   // 換檢視對象就失效 —— 開檔鈕只指向「這次」產的檔
            string text = AgentCommands.ReadingLibrary.UCL_ReadingLibraryIO.RenderRecall(
                mediaId, reader, m_RecallFull, out string error);
            m_RecallText = text ?? $"✗ 追回讀取失敗：{error}";
            m_RecallScroll = Vector2.zero;
        }

        void DrawRecallPanel()
        {
            if (string.IsNullOrEmpty(m_RecallText)) return;
            GUILayout.Space(8);
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label($"<b>📖 追回檢視</b>　{m_RecallMediaId} / {m_RecallReader}",
                        UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

                    GUILayout.BeginHorizontal();
                    bool full = UCL_GUILayout.CheckBox(m_RecallFull);
                    GUILayout.Label("round 全文", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    GUILayout.EndHorizontal();

                    if (full != m_RecallFull)
                    {
                        m_RecallFull = full;
                        LoadRecall(m_RecallMediaId, m_RecallReader);   // 切換即重讀，別讓畫面跟開關對不上
                    }
                    if (GUILayout.Button("💾 產生追回檔", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        string path = AgentCommands.ReadingLibrary.UCL_ReadingLibraryIO.WriteRecallBrief(
                            m_RecallMediaId, m_RecallReader, true, out string error);
                        m_LastStatus = path != null ? $"✓ 追回檔已寫出：{path}" : $"✗ 追回檔寫出失敗：{error}";
                        m_LastBriefPath = path ?? "";
                    }
                    // 產檔成功後才出現 —— 開父夾並選中該檔（UCL_ExplorerUtil 對檔案路徑的既有行為）
                    if (!string.IsNullOrEmpty(m_LastBriefPath)
                        && GUILayout.Button("📂 開啟檔案位置", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        UCL_ExplorerUtil.Open(m_LastBriefPath, nameof(UCL_ReadingNotesManagePage));
                    }
                    if (GUILayout.Button("✕ 關閉", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_RecallText = "";
                        return;
                    }
                    GUILayout.FlexibleSpace();
                }
                using (var sv = new GUILayout.ScrollViewScope(m_RecallScroll,
                           GUILayout.MinHeight(UCL_GUIStyle.GetScaledSize(320))))
                {
                    m_RecallScroll = sv.scrollPosition;
                    // 唯讀 TextArea：QA 時要能選取比對，不是只能看
                    UnityEditor.EditorGUILayout.TextArea(m_RecallText, RecallTextStyle);
                }
            }
        }

        GUIStyle m_RecallTextStyle;
        GUIStyle RecallTextStyle => m_RecallTextStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle)
        {
            wordWrap = true,
            richText = false,
        };

        void Search()
        {
            m_Results.Clear();
            string query = (m_Query ?? "").Trim();
            if (string.IsNullOrEmpty(query))
            {
                m_LastStatus = "請先輸入作品名稱。";
                return;
            }

            // Library 先、Archive 後（Tim 2026-08-07）：新版才有追回等可操作功能，該站結果頂端；
            // Archive 是遷移參考，沉底 —— 順序即分層，不用額外的分組標題。
            SearchLibrary(query);
            int hiddenMigrated = SearchArchive(query);
            m_LastStatus = $"「{query}」找到 {m_Results.Count} 個入口" +
                           (hiddenMigrated > 0 ? $"（另隱藏 {hiddenMigrated} 筆已遷移 Archive —— 勾上方開關顯示）" : "") +
                           "。Archive 結果只供人工確認與遷移，不會被新流程讀取。";
        }

        // Archive 是歷史原件：只讀每個 entry 的 book.json 標題來定位資料夾，絕不讀 chapters / characters。
        // 回傳「因已遷移而隱藏」的命中數 —— 隱藏必須被計數顯示，靜默隱藏＝使用者以為資料不見了。
        int SearchArchive(string query)
        {
            if (!Directory.Exists(m_ArchiveDir)) return 0;
            // 已遷移標記的唯一事實源是 _migration/registry.json（Archive 不可修改）——
            // 每次搜尋載一次（不逐列載；registry 是小檔但迴圈裡重複 IO 沒有理由）
            var migrated = AgentCommands.ReadingLibrary.UCL_ReadingLibraryIO.LoadMigratedArchiveSlugs();
            int hidden = 0;
            foreach (string dir in Directory.GetDirectories(m_ArchiveDir))
            {
                string slug = Path.GetFileName(dir);
                string metadataPath = Path.Combine(dir, "book.json");
                JsonData data = ReadObject(metadataPath);
                if (data == null) continue;
                string title = data.GetString("title", "");
                string original = data.GetString("title_original", "");
                if (!Matches(query, title, original, slug)) continue;
                bool isMigrated = migrated.Contains(slug);
                if (!m_ShowMigrated && isMigrated) { hidden++; continue; }
                m_Results.Add(new SearchEntry
                {
                    Kind = isMigrated ? "Archive（legacy，唯讀 · ✅ 已遷移）" : "Archive（legacy，唯讀）",
                    Title = string.IsNullOrEmpty(title) ? slug : title,
                    Detail = $"slug: {slug}　原文: {original}",
                    Path = dir,
                });
            }
            return hidden;
        }

        bool m_ShowMigrated = false;

        // 新 schema：先索引 work 的標題，再把命中的 work 對應到 media folder。
        // JSON 在此是 schema 邊界的唯讀 projection；未表示的欄位不會被寫回或遺失。
        void SearchLibrary(string query)
        {
            string worksRoot = Path.Combine(m_LibraryDir, "works");
            string mediaRoot = Path.Combine(m_LibraryDir, "media");
            if (!Directory.Exists(worksRoot) || !Directory.Exists(mediaRoot)) return;

            var workTitles = new Dictionary<string, string>();
            foreach (string workDir in Directory.GetDirectories(worksRoot))
            {
                JsonData work = ReadObject(Path.Combine(workDir, "work.json"));
                if (work == null) continue;
                string workId = work.GetString("work_id", Path.GetFileName(workDir));
                workTitles[workId] = work.GetString("title", workId);
            }

            foreach (string mediaDir in Directory.GetDirectories(mediaRoot))
            {
                JsonData media = ReadObject(Path.Combine(mediaDir, "media.json"));
                if (media == null) continue;
                string workId = media.GetString("work_id", "");
                string title = workTitles.TryGetValue(workId, out string knownTitle) ? knownTitle : workId;
                string mediaId = media.GetString("media_id", Path.GetFileName(mediaDir));
                string mediaKind = media.GetString("media_kind", "unknown");
                if (!Matches(query, title, mediaId, workId)) continue;
                // readers 清單給追回檢視按鈕用 —— 只列目錄名，不在搜尋階段讀 reader.json
                var readers = new List<string>();
                string readersRoot = Path.Combine(mediaDir, "readers");
                if (Directory.Exists(readersRoot))
                {
                    foreach (string readerDir in Directory.GetDirectories(readersRoot))
                        readers.Add(Path.GetFileName(readerDir));
                    readers.Sort(StringComparer.OrdinalIgnoreCase);
                }
                m_Results.Add(new SearchEntry
                {
                    Kind = "Library（新 schema）",
                    Title = title,
                    Detail = $"media_id: {mediaId}　media_kind: {mediaKind}　work_id: {workId}",
                    Path = mediaDir,
                    MediaId = mediaId,
                    Readers = readers,
                });
            }
        }

        static JsonData ReadObject(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                JsonData data = JsonData.ParseJson(File.ReadAllText(path));
                return data != null && data.IsObject ? data : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ReadingNotesManage] metadata load failed ({path}): {e.Message}");
                return null;
            }
        }

        static bool Matches(string query, params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        GUIStyle m_WrapLabelStyle;
        GUIStyle WrapLabelStyle => m_WrapLabelStyle ??= new GUIStyle(UCL_GUIStyle.LabelStyle) { wordWrap = true };
    }
}
#endif
