// 區塊職責：跨專案文件搜尋頁 — 對 Docs/ 與 UCL_Core/Docs~/ 做即時模糊搜尋。
// 物理意義：跟 Cmd_SearchDocs 共用 UCL_DocSearchEngine 計分 / 同義詞展開邏輯，
//          差別在於以 IMGUI 呈現結果、提供進階控制（mode / limit / synonyms 路徑 /
//          includeArchived），且每筆結果可一鍵開啟檔案或在檔案管理員定位。
// 數值影響：cold scan 200+ 篇 markdown（SSD <200ms）；結果 cache 在 page 實例內，
//          重繪不會重掃；點「開啟」按鈕走 OS 預設 .md 檢視器。
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UCL.Core.EditorLib.AgentCommands;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// 文件搜尋頁。從 <see cref="UCL_WelcomePage"/> 的「🔍 文件搜尋」按鈕跳進來，
    /// 或之後可加 <c>UCL → Search Docs</c> 選單。
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_DocSearchPage.md")]
    public class UCL_DocSearchPage : UCL_CommonEditorPage
    {
        public override string WindowName => "UCL_DocSearch";

        // ==== 搜尋輸入暫存 ====
        // 區塊職責：保留使用者最近一次的查詢與結果，避免每幀重掃 200 篇 .md
        // 物理意義：搜尋只在按 Search / 按 Enter 時觸發；之後重繪只用 cache
        // 數值影響：m_Hits == null → 尚未搜過；count == 0 → 搜過但無命中
        string m_Query = "";
        List<UCL_DocSearchHit> m_Hits;
        int m_LastScannedCount;

        // ==== 進階選項 ====
        int m_Limit = 20;
        bool m_OrMode = false;
        bool m_IncludeArchived = false;
        string m_SynonymsPath = "Docs/_synonyms.txt";   // 預設位置，與 Cmd_SearchDocs 一致
        bool m_ShowAdvanced = false;

        Vector2 m_Scroll = Vector2.zero;

        // 區塊職責：snippet 顯示專用樣式 — 換行 + rich-text 高亮（&lt;color&gt;&lt;b&gt;）
        // 物理意義：Engine 端把命中字以 IMGUI rich-text tag 包起來，這裡保證 GUIStyle 能解析並換行
        // 數值影響：lazy 建立、整個 page 共用一份，避免每筆結果 row new 一個 GUIStyle
        GUIStyle m_SnippetStyle;
        GUIStyle SnippetStyle
        {
            get
            {
                if (m_SnippetStyle == null)
                {
                    m_SnippetStyle = new GUIStyle(UCL_GUIStyle.LabelStyle)
                    {
                        wordWrap = true,
                        richText = true,
                    };
                }
                return m_SnippetStyle;
            }
        }

        public static UCL_DocSearchPage Create()
        {
            return UCL_EditorPage.Create<UCL_DocSearchPage>();
        }

        // ===========================================================
        // 主流程
        // ===========================================================
        protected override void ContentOnGUI()
        {
            m_Scroll = GUILayout.BeginScrollView(m_Scroll);

            DrawSearchInput();
            GUILayout.Space(4);
            DrawAdvancedOptions();
            GUILayout.Space(4);
            DrawResults();

            GUILayout.EndScrollView();
        }

        // ===========================================================
        // 區塊：搜尋輸入列 — text field + Search 按鈕 + Enter 觸發
        // ===========================================================
        void DrawSearchInput()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("Welcome.Search.Title"), UCL_GUIStyle.LabelStyle);

                using (new GUILayout.HorizontalScope())
                {
                    bool clicked = GUILayout.Button(
                        UCL_CodeLocalize.Get("Welcome.Search.Button"),
                        UCL_GUIStyle.GetButtonStyle(Color.cyan),
                        GUILayout.ExpandWidth(false));

                    // 區塊職責：在 TextField 繪製「之前」snapshot Enter 鍵狀態，避免 TextField
                    //          自己 Use() 掉 KeyDown 事件後我們檢查不到。
                    // 物理意義：IMGUI 的 TextField 是 single-line，正常情況下會把 Return 當作
                    //          「結束輸入」消化掉；同一輪 OnGUI 內，我們事先記下 Return 是否
                    //          被按下，再配合「焦點是否在這個 control」決定要不要觸發搜尋。
                    Event ev = Event.current;
                    bool enterDownThisFrame = ev.type == EventType.KeyDown
                                              && (ev.keyCode == KeyCode.Return
                                                  || ev.keyCode == KeyCode.KeypadEnter);

                    GUI.SetNextControlName("UCL_DocSearch_Field");
                    string newQuery = GUILayout.TextField(m_Query ?? "", UCL_GUIStyle.TextFieldStyle);
                    m_Query = newQuery;

                    bool enterPressed = enterDownThisFrame
                                        && GUI.GetNameOfFocusedControl() == "UCL_DocSearch_Field";

                    if ((clicked || enterPressed) && !string.IsNullOrWhiteSpace(m_Query))
                    {
                        DoSearch(m_Query);
                        if (enterPressed) ev.Use();
                    }
                }
                GUILayout.Label(UCL_CodeLocalize.Get("Welcome.Search.Hint"), UCL_GUIStyle.LabelStyle);
            }
        }

        // ===========================================================
        // 區塊：進階選項（折疊）— mode / limit / synonyms 路徑 / includeArchived
        // 物理意義：簡單模式只看上面的 input；想做精細控制就展開這區塊
        // 數值影響：選項變更不會自動重搜；要按一次 Search 才生效
        // ===========================================================
        void DrawAdvancedOptions()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                m_ShowAdvanced = GUILayout.Toggle(m_ShowAdvanced,
                    UCL_CodeLocalize.Get("DocSearch.Advanced"));
                if (!m_ShowAdvanced) return;

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("DocSearch.Mode"),
                        UCL_GUIStyle.LabelStyle, GUILayout.Width(80));
                    if (GUILayout.Toggle(!m_OrMode, "AND", UCL_GUIStyle.ButtonStyle)) m_OrMode = false;
                    if (GUILayout.Toggle(m_OrMode, "OR", UCL_GUIStyle.ButtonStyle)) m_OrMode = true;
                }

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("DocSearch.Limit"),
                        UCL_GUIStyle.LabelStyle, GUILayout.Width(80));
                    m_Limit = (int)GUILayout.HorizontalSlider(m_Limit, 5, 100, GUILayout.Width(200));
                    GUILayout.Label(m_Limit.ToString(), UCL_GUIStyle.LabelStyle, GUILayout.Width(40));
                }

                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("DocSearch.SynonymsPath"),
                        UCL_GUIStyle.LabelStyle, GUILayout.Width(120));
                    m_SynonymsPath = GUILayout.TextField(m_SynonymsPath ?? "", UCL_GUIStyle.TextFieldStyle);
                }

                m_IncludeArchived = GUILayout.Toggle(m_IncludeArchived,
                    UCL_CodeLocalize.Get("DocSearch.IncludeArchived"));
            }
        }

        // ===========================================================
        // 區塊：結果列表
        // 物理意義：每筆 row = score + 標題 + 路徑 + 命中欄位 + 描述 + 兩顆動作按鈕
        //          (📂 Reveal 在檔案管理員打開所在資料夾、📖 Open 用 OS 預設應用打開檔案)
        // ===========================================================
        void DrawResults()
        {
            if (m_Hits == null) return;
            using (new GUILayout.VerticalScope("box"))
            {
                if (m_Hits.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("Welcome.Search.NoResults"), UCL_GUIStyle.LabelStyle);
                    return;
                }
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("Welcome.Search.ResultsCount"),
                        m_Hits.Count, m_LastScannedCount),
                    UCL_GUIStyle.LabelStyle);

                string gitRoot = UCL_DocCatalogScanner.GetGitRoot();
                for (int i = 0; i < m_Hits.Count; i++)
                {
                    DrawResultRow(i, m_Hits[i], gitRoot);
                }
            }
        }

        void DrawResultRow(int idx, UCL_DocSearchHit hit, string gitRoot)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                using (new GUILayout.HorizontalScope())
                {
                    // 區塊職責：把 git-root 相對路徑轉成「可開」的 URL
                    // 物理意義：UCL_Core 內的 Docs~ 走 UCL_URL.ResolveURL("ucl_core:...") prefix
                    //          與 FeatureCard 的「📖 文件」按鈕同一條已驗證可用的路徑；
                    //          其他位置（如 git-root/Docs/）沒註冊 prefix，退回 file:/// 絕對路徑。
                    //          Reveal 直接用 EditorUtility.RevealInFinder（跨平台）。
                    string rel = hit.Entry.RelativePath.Replace('\\', '/');
                    string abs = Path.Combine(gitRoot, rel).Replace('\\', '/');
                    if (GUILayout.Button(UCL_CodeLocalize.Get("DocSearch.Reveal"),
                        UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        EditorUtility.RevealInFinder(abs);
                    }
                    // 區塊職責：「📄 預覽」— 在 Editor 內以 IMGUI 直接渲染這份 .md（不離開 Unity 視窗）
                    // 物理意義：與右側「📖 Open」並存：Open 走 OS 預設應用、預覽走內嵌 page；兩條入口皆保留
                    // 數值影響：點擊後 Push 一頁 UCL_MarkdownViewerPage 到 GUIPageController，使用者按 Back 返回搜尋
                    if (GUILayout.Button("📄",
                        UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        UCL_MarkdownViewerPage.Create(rel, abs);
                    }
                    if (GUILayout.Button(UCL_CodeLocalize.Get("Welcome.Search.OpenButton"),
                        UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                    {
                        OpenDocByUrl(rel, abs);
                    }


                    GUILayout.Label($"#{idx + 1}", UCL_GUIStyle.LabelStyle, GUILayout.Width(40));
                    GUILayout.Label($"<color=#FFD37AFF>★ {hit.Score}</color>",
                        UCL_GUIStyle.LabelStyle, GUILayout.Width(60));
                    var titleStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { fontStyle = FontStyle.Bold };
                    GUILayout.Label(hit.Entry.Title ?? "(no title)", titleStyle);
                    GUILayout.FlexibleSpace();


                }
                GUILayout.Label($"  {hit.Entry.RelativePath}", UCL_GUIStyle.LabelStyle);
                if (!string.IsNullOrEmpty(hit.Entry.Description))
                {
                    GUILayout.Label($"  {hit.Entry.Description}", UCL_GUIStyle.LabelStyle);
                }
                // P1：最佳命中 section（H1~H6 標題 + 起始行號）— 引導使用者跳到具體段落
                if (!string.IsNullOrEmpty(hit.SectionTitle))
                {
                    string lineSuffix = hit.SectionStartLine > 0 ? $" (L{hit.SectionStartLine})" : "";
                    GUILayout.Label($"  <color=#9BD0FF>§ {hit.SectionTitle}</color>{lineSuffix}", SnippetStyle);
                }
                // P1：snippet preview — 圍繞首個命中的 ±N 字元上下文，命中字以 rich-text 高亮
                if (!string.IsNullOrEmpty(hit.Snippet))
                {
                    GUILayout.Label($"  {hit.Snippet}", SnippetStyle);
                }
                if (hit.MatchedFields != null && hit.MatchedFields.Count > 0)
                {
                    GUILayout.Label($"  Matched: {string.Join(", ", hit.MatchedFields)}", UCL_GUIStyle.LabelStyle);
                }
            }
        }

        // ===========================================================
        // 執行搜尋（cold scan + score）
        // ===========================================================
        void DoSearch(string query)
        {
            string gitRoot = UCL_DocCatalogScanner.GetGitRoot();
            var roots = new List<string> { "Docs", "CardGame/Assets/UCL/UCL_Core/Docs~" };
            var excludes = new List<string> { "node_modules", ".git", "_Drafts" };

            var entries = UCL_DocCatalogScanner.ScanRoots(roots, gitRoot, excludes,
                m_IncludeArchived, CancellationToken.None);
            m_LastScannedCount = entries.Count;

            var synonymGroups = UCL_DocSearchEngine.LoadSynonyms(gitRoot, m_SynonymsPath);
            // 區塊職責：用當前語系作為 preferredLang，讓對應語系的文件排前
            // 物理意義：UCL_LocalizeManager.s_LangName 是 4 語系切換中央狀態
            //          （"zh-Hant" / "en" / "ja" / "zh-Hans"），與 UCL_Core 多語系 Docs 路徑段 1:1 對應
            string preferredLang = UCL_LocalizeManager.s_LangName;
            // P1：改走 body-aware 變體 — 多讀一次每篇 .md、做 section 級計分、產 snippet
            // 物理意義：metadata 命中之外多吃 body，並抓最佳 section 的上下文片段給 UI 高亮顯示
            // 數值影響：每查詢多 ~200 次 ReadAllLines；SSD 上仍在數百 ms 量級，編輯器搜尋可接受
            m_Hits = UCL_DocSearchEngine.SearchSimpleWithBody(query, entries, gitRoot, synonymGroups,
                orMode: m_OrMode, limit: m_Limit, preferredLang: preferredLang);
        }

        // 區塊職責：把搜尋命中的 .md 檔轉成可開啟的 URL，再 Application.OpenURL
        // 物理意義：UCL_Core 內部文件（CardGame/Assets/UCL/UCL_Core/...）走 UCL_URL "ucl_core:" prefix —
        //          這條路徑跟 FeatureCard 的「📖 文件」按鈕共用，已驗證可在 Editor / Build 模式都正確開啟。
        //          其他位置（git-root 下的 Docs/ 等）沒註冊專用 prefix，退回 file:/// 絕對路徑。
        // 數值影響：純 OpenURL 呼叫，由 OS / browser / 預設應用接手
        internal static void OpenDocByUrl(string relPath, string absPath)
        {
            const string kUclCorePrefix = "CardGame/Assets/UCL/UCL_Core/";
            if (relPath.StartsWith(kUclCorePrefix, System.StringComparison.OrdinalIgnoreCase))
            {
                // 截掉 "CardGame/Assets/UCL/UCL_Core/" 前綴 → 得到 UCL_Core 內部相對路徑（如 "Docs~/zh-Hant/..."）
                string sub = relPath.Substring(kUclCorePrefix.Length);
                // UCL_URL 在 UCL.Core namespace，從 UCL.Core.EditorLib.Page 向外解析可見
                Application.OpenURL(UCL_URL.ResolveURL("ucl_core:" + sub));
            }
            else
            {
                // EOV Docs/ 等非 UCL_Core 文件：沒專用 prefix，走絕對路徑 file://
                Application.OpenURL("file:///" + absPath);
            }
        }
    }
}
#endif
