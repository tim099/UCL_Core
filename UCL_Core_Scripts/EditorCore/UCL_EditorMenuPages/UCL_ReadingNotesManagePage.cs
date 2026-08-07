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
            DrawSearchPanel();
            GUILayout.Space(8);
            DrawResultsPanel();
        }

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
                        using (new GUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button("📂 開啟", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                                UCL_ExplorerUtil.Open(entry.Path, nameof(UCL_ReadingNotesManagePage));
                            GUILayout.Label($"<b>{entry.Kind}</b>　{entry.Title}\n{entry.Detail}\n{entry.Path}", WrapLabelStyle);
                        }
                        // 追回檢視：每位 reader 一顆鈕 —— 讀取與 Cmd_Library op=recall 走同一段服務層
                        //（Tim 2026-08-06 拍板「頁面讀取與 Cmd 走同一段」；這裡不自己 parse 任何 JSON）。
                        if (!string.IsNullOrEmpty(entry.MediaId) && entry.Readers.Count > 0)
                        {
                            using (new GUILayout.HorizontalScope())
                            {
                                GUILayout.Label("追回：", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                                foreach (string reader in entry.Readers)
                                {
                                    if (GUILayout.Button($"📖 {reader}", UCL_GUIStyle.ButtonStyle,
                                            GUILayout.ExpandWidth(false)))
                                    {
                                        LoadRecall(entry.MediaId, reader);
                                    }
                                }
                                GUILayout.FlexibleSpace();
                            }
                        }
                    }
                }
            }
            DrawRecallPanel();
        }

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

        void LoadRecall(string mediaId, string reader)
        {
            m_RecallMediaId = mediaId;
            m_RecallReader = reader;
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
                    bool full = GUILayout.Toggle(m_RecallFull, " round 全文", GUILayout.ExpandWidth(false));
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

            SearchArchive(query);
            SearchLibrary(query);
            m_LastStatus = $"「{query}」找到 {m_Results.Count} 個入口。Archive 結果只供人工確認與遷移，不會被新流程讀取。";
        }

        // Archive 是歷史原件：只讀每個 entry 的 book.json 標題來定位資料夾，絕不讀 chapters / characters。
        void SearchArchive(string query)
        {
            if (!Directory.Exists(m_ArchiveDir)) return;
            foreach (string dir in Directory.GetDirectories(m_ArchiveDir))
            {
                string metadataPath = Path.Combine(dir, "book.json");
                JsonData data = ReadObject(metadataPath);
                if (data == null) continue;
                string title = data.GetString("title", "");
                string original = data.GetString("title_original", "");
                if (!Matches(query, title, original, Path.GetFileName(dir))) continue;
                m_Results.Add(new SearchEntry
                {
                    Kind = "Archive（legacy，唯讀）",
                    Title = string.IsNullOrEmpty(title) ? Path.GetFileName(dir) : title,
                    Detail = $"slug: {Path.GetFileName(dir)}　原文: {original}",
                    Path = dir,
                });
            }
        }

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
