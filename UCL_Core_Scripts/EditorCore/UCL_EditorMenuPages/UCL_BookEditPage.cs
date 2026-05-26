
using System;
using System.Collections.Generic;
using System.IO;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UCL.Core.EditorLib.Page
{
    // 區塊職責：書籍編輯 Page（簡易 Prototype）— 編輯 Books/<slug>/ 全文書庫的章節純文字檔
    // 物理意義：Books/<slug>/ 內每個 NNN.txt 是一章全文（000.txt 常為序）。本 Page 提供：
    //          選章節（下拉）→ TextArea 編輯內文 → 存檔；以及新增空章節（下一個流水號 NNN.txt）。
    // 數值影響：Save 直接覆寫對應 .txt；新增章節寫新檔。純檔案 IO，不走 library.py（library.py 管 BookNotes 筆記，不管全文）。
    //
    // 設計理由 (Tim 2026-05-26 派 task — 先做簡易 Prototype)：
    //   從 UCL_LibraryManagePage 的 Books 下拉選書後，按「編輯書籍」跳轉進來。MVP 範圍：增章節 / 改內文 / 存檔。
    //   進階（章節改名 / 刪除 / 拖曳排序 / 與 BookNotes 章節對應）列為後續迭代。
    //
    // 開啟方式（從 LibraryManagePage）：
    //   var p = new UCL_BookEditPage();
    //   p.SetBook(slug, booksDir);
    //   UCL_GUIPageController.CurrentRenderIns.Push(p);   // Push 內部會呼叫 Init
    public class UCL_BookEditPage : UCL_CommonEditorPage
    {
        public override string WindowName => string.Format(UCL_CodeLocalize.Get("BookEdit.TitleFmt"), m_BookSlug);
        // 子頁面 — 從 LibraryManagePage 帶 slug 開啟，不掛進頂層 Page 選單
        public override bool ShowInPageMenu => false;

        // 區塊職責：目標書籍 — slug + Books 根目錄（Push 前由 SetBook 設定）
        string m_BookSlug = "";
        string m_BooksDir = "";
        string m_BookDir = "";

        // 區塊職責：章節清單 state
        // 物理意義：m_Chapters = Books/<slug>/ 下所有 .txt 檔名（不含路徑，排序後）；m_SelectedChapter = 當前編輯的檔名
        List<string> m_Chapters = new List<string>();
        readonly UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();
        string m_SelectedChapter = "";

        // 區塊職責：編輯緩衝
        // 物理意義：m_EditBuffer = TextArea 當前內容；m_LoadedChapter = 緩衝對應的檔名（偵測切章節要重載）；
        //          m_Dirty = 內容是否被改過（未存）
        string m_EditBuffer = "";
        string m_LoadedChapter = "";
        bool m_Dirty = false;

        // 區塊職責：新增章節輸入 — 章節流水號（NNN）
        string m_NewChapterNum = "";

        // 區塊職責：Push 前由呼叫端設定目標書籍
        // 物理意義：Init 由 Push 觸發，故 slug 必須在 Push 前先設；Init 再依此 load 章節
        public void SetBook(string bookSlug, string booksDir)
        {
            m_BookSlug = bookSlug;
            m_BooksDir = booksDir;
        }

        public override void Init(UCL_GUIPageController p_Controller)
        {
            base.Init(p_Controller);
            // 區塊：解析書籍目錄 — 若 SetBook 沒帶 booksDir 則 fallback 走 UCL_RepoPath
            if (string.IsNullOrEmpty(m_BooksDir))
                m_BooksDir = Path.Combine(UCL_RepoPath.AgentCommandsDir, "Books");
            m_BookDir = Path.Combine(m_BooksDir, m_BookSlug);
            LoadChapters();
        }

        // 區塊職責：掃 Books/<slug>/ 下所有 .txt 章節檔
        // 物理意義：檔名排序（NNN.txt 字串排序剛好等於章節序）；預設選第一章
        void LoadChapters()
        {
            m_Chapters.Clear();
            if (!Directory.Exists(m_BookDir))
            {
                Debug.LogWarning($"[BookEdit] 書籍目錄不存在: {m_BookDir}");
                return;
            }
            foreach (var f in Directory.GetFiles(m_BookDir, "*.txt"))
                m_Chapters.Add(Path.GetFileName(f));
            m_Chapters.Sort(StringComparer.Ordinal);

            if ((string.IsNullOrEmpty(m_SelectedChapter) || !m_Chapters.Contains(m_SelectedChapter))
                && m_Chapters.Count > 0)
            {
                m_SelectedChapter = m_Chapters[0];
            }
        }

        // 區塊職責：把選中章節的內文載進編輯緩衝
        // 物理意義：只在「緩衝對應檔 != 當前選中檔」時重載，避免每幀洗掉使用者正在打的字
        void EnsureBufferLoaded()
        {
            if (m_SelectedChapter == m_LoadedChapter) return;
            try
            {
                string path = Path.Combine(m_BookDir, m_SelectedChapter);
                m_EditBuffer = File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch (Exception e)
            {
                Debug.LogError($"[BookEdit] 讀取章節失敗 {m_SelectedChapter}: {e.Message}");
                m_EditBuffer = "";
            }
            m_LoadedChapter = m_SelectedChapter;
            m_Dirty = false;
        }

        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            // 返回 LibraryManagePage（pop 自己）
            if (GUILayout.Button(UCL_CodeLocalize.Get("BookEdit.Btn.Back"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                if (p_Controller != null) p_Controller.Pop();
            }
            if (GUILayout.Button(UCL_CodeLocalize.Get("BookEdit.Btn.Reload"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                LoadChapters();
                m_LoadedChapter = "";   // 強制重載緩衝
            }
            // 存檔 / 還原 — 放上排（Tim 2026-05-26）；dirty 才可按存檔，存檔鈕亮綠
            GUI.enabled = m_Dirty;
            if (GUILayout.Button(UCL_CodeLocalize.Get("BookEdit.Btn.Save"), UCL_GUIStyle.GetButtonStyle(m_Dirty ? Color.green : Color.white), GUILayout.ExpandWidth(false)))
            {
                DoSave();
            }
            if (GUILayout.Button(UCL_CodeLocalize.Get("BookEdit.Btn.Revert"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                m_LoadedChapter = "";   // 強制重載 → 丟棄緩衝改動
            }
            GUI.enabled = true;
        }

        protected override void ContentOnGUI()
        {
            if (!Directory.Exists(m_BookDir))
            {
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("BookEdit.MissingFmt"), m_BookDir), UCL_GUIStyle.LabelStyle);
                return;
            }

            // 區塊：章節數 + 新增章節
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("BookEdit.ChaptersFmt"), m_Chapters.Count), UCL_GUIStyle.LabelStyle);
                bool addChapter = GUILayout.Button(UCL_CodeLocalize.Get("BookEdit.Btn.AddChapter"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
                
                GUILayout.Label(UCL_CodeLocalize.Get("BookEdit.NewChapterLabel"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                m_NewChapterNum = GUILayout.TextField(m_NewChapterNum, UCL_GUIStyle.TextFieldStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80)));
                GUILayout.FlexibleSpace();

                if (addChapter) DoAddChapter();
            }

            if (m_Chapters.Count == 0)
            {
                GUILayout.Label(UCL_CodeLocalize.Get("BookEdit.NoChapters"), UCL_GUIStyle.LabelStyle);
                return;
            }

            // 區塊：章節下拉選單（對齊 LibraryManagePage 的 PopupSearchCache 用法）
            using (new GUILayout.HorizontalScope("box"))
            {
                bool openFile = GUILayout.Button(UCL_CodeLocalize.Get("BookEdit.Btn.OpenTxt"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false));
                GUILayout.Label(UCL_CodeLocalize.Get("BookEdit.SelectChapter"), UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(100)));
                int curIdx = m_Chapters.IndexOf(m_SelectedChapter);
                if (curIdx < 0) curIdx = 0;
                int newIdx = UCL_GUILayout.PopupSearchCache(curIdx, m_Chapters, m_Dic.GetSubDic("ChapterPicker"), "ChapterPicker", GUILayout.Width(UCL_GUIStyle.GetScaledSize(200)));
                if (newIdx >= 0 && newIdx < m_Chapters.Count)
                {
                    string picked = m_Chapters[newIdx];
                    // 切章節前若有未存改動 → 提示（簡易：印 warning，不擋）
                    if (picked != m_SelectedChapter && m_Dirty)
                        Debug.LogWarning($"[BookEdit] 切換章節，未存的改動將遺失: {m_SelectedChapter}");
                    m_SelectedChapter = picked;
                }
                GUILayout.FlexibleSpace();
                if (openFile) OpenFile(Path.Combine(m_BookDir, m_SelectedChapter));
            }

            // 區塊：載入內文 + TextArea 編輯
            EnsureBufferLoaded();

            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("BookEdit.EditingFmt"), m_SelectedChapter, m_Dirty ? " *" : ""), UCL_GUIStyle.LabelStyle);
                GUILayout.FlexibleSpace();
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("BookEdit.CharCountFmt"), m_EditBuffer != null ? m_EditBuffer.Length : 0), UCL_GUIStyle.LabelStyle);
            }

            // 區塊：內文編輯 TextArea — 不自帶 scroll（Page 本身已有 scroll，避免雙層卷軸）
            // 物理意義：MinHeight 給個基本高度，內容超過時靠 Page 外層 scroll 撐開
            string newText = GUILayout.TextArea(m_EditBuffer, UCL_GUIStyle.TextAreaStyle, 
                GUILayout.Width(UCL_GUIStyle.GetScaledSize(680)),
                GUILayout.MinHeight(UCL_GUIStyle.GetScaledSize(400)));
            if (newText != m_EditBuffer)
            {
                m_EditBuffer = newText;
                m_Dirty = true;
            }
        }

        // 區塊職責：存檔 — 把編輯緩衝寫回當前章節 .txt
        // 物理意義：UTF-8 覆寫；成功後清 dirty flag
        void DoSave()
        {
            if (string.IsNullOrEmpty(m_SelectedChapter)) return;
            try
            {
                string path = Path.Combine(m_BookDir, m_SelectedChapter);
                File.WriteAllText(path, m_EditBuffer ?? "", new System.Text.UTF8Encoding(false));
                m_Dirty = false;
                Debug.Log($"[BookEdit] ✓ 已存檔: {path} ({(m_EditBuffer != null ? m_EditBuffer.Length : 0)} 字)");
            }
            catch (Exception e)
            {
                Debug.LogError($"[BookEdit] 存檔失敗 {m_SelectedChapter}: {e.Message}");
            }
        }

        // 區塊職責：新增章節 — 建一個空的 NNN.txt
        // 物理意義：若輸入框有指定流水號則用之（補零到 3 位），否則自動取「現有最大號 + 1」
        void DoAddChapter()
        {
            int num;
            if (!string.IsNullOrWhiteSpace(m_NewChapterNum) && int.TryParse(m_NewChapterNum.Trim(), out num))
            {
                // 採用使用者指定號
            }
            else
            {
                // 自動：掃現有檔名取最大數字 + 1
                int max = -1;
                foreach (var c in m_Chapters)
                {
                    string stem = Path.GetFileNameWithoutExtension(c);
                    if (int.TryParse(stem, out int n) && n > max) max = n;
                }
                num = max + 1;
            }
            string fileName = num.ToString("D3") + ".txt";
            string path = Path.Combine(m_BookDir, fileName);
            if (File.Exists(path))
            {
                Debug.LogWarning($"[BookEdit] 章節已存在，不覆寫: {fileName}");
                return;
            }
            try
            {
                File.WriteAllText(path, "", new System.Text.UTF8Encoding(false));
                Debug.Log($"[BookEdit] ✓ 新增章節: {path}");
                m_NewChapterNum = "";
                LoadChapters();
                m_SelectedChapter = fileName;
                m_LoadedChapter = "";   // 強制載入新空章節
            }
            catch (Exception e)
            {
                Debug.LogError($"[BookEdit] 新增章節失敗 {fileName}: {e.Message}");
            }
        }

        // 區塊職責：用系統預設程式開啟章節 .txt
        void OpenFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Debug.LogWarning($"[BookEdit] 檔案不存在: {path}");
                    return;
                }
                Application.OpenURL("file://" + path.Replace('\\', '/'));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BookEdit] 開啟檔案失敗: {e.Message}");
            }
        }
    }
}
