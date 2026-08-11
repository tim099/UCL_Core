
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 05/06 2026
//
// 區塊職責：UCL_Core 的歡迎頁 — 第一次安裝 / 大版本升級時自動彈出，介紹本框架核心功能與快速入口。
// 物理意義：跨專案共用的 UCL_Core 套件可能被新人專案/新人開發者第一次接觸，
//          如果沒有引導，使用者要自己翻 Docs~ 才知道有哪些功能。
//          本頁集中呈現主要功能（Modules / Localize / Agent Commands / Editor Pages）+ 文件連結，
//          並附「重設首次彈出」按鈕方便日後重新體驗。
// 數值影響：唯讀 GUI；使用者按鈕可開啟其他 Page，或寫 EditorPrefs 控制自動彈出狀態。
#if UNITY_EDITOR
using System;                             // for Action（FeatureCard 簽名）
using System.Linq;                        // for .Select / .ToList（語言下拉組裝顯示名）
using UCL.Core.Game;                      // for UCL_LocalizeService（語言切換）
using UCL.Core.LocalizeLib;               // for UCL_CodeLocalize.Get(...)
using UCL.Core.Page;                      // for UCL_ModuleServiceEditPage（住在 UCL.Core.Page）
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// UCL_Core 的歡迎/總覽頁。
    ///
    /// 三種出現方式：
    /// <list type="number">
    ///   <item>第一次安裝自動彈出（由 <see cref="UCL_WelcomeAutoOpen"/> 透過 [InitializeOnLoad] 偵測）</item>
    ///   <item>使用者從 EditorMenu 主頁點 Welcome 按鈕</item>
    ///   <item>選單 <c>UCL → Welcome</c></item>
    /// </list>
    ///
    /// 控制 EditorPrefs：
    /// <list type="bullet">
    ///   <item><c>UCL_Core.Welcome.ShownVersion</c> — 已展示的版本號（預設空）</item>
    ///   <item><c>UCL_Core.Welcome.AutoOpenDisabled</c> — 使用者主動關閉自動彈出</item>
    /// </list>
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_WelcomePage.md")]
    public class UCL_WelcomePage : UCL_CommonEditorPage
    {
        public override string WindowName => "UCL_Core Welcome";
        public override bool ShowInPageMenu => false;
        /// <summary>當前 Welcome 內容版本 — 若 EditorPrefs 內紀錄的版本不同，會自動彈出一次。</summary>
        public const string CurrentVersion = "1";

        // EditorPrefs 是 per-machine（HKCU\...\Unity Editor 5.x 全域），跨專案共用會導致
        // A 專案看過 Welcome 後 B 專案不再自動彈。把 key 加上 Application.dataPath hash 後綴
        // 達成 per-project namespace。dataPath 在專案存活期間穩定；移動專案資料夾會視為新安裝
        // 重新彈一次（可接受）。

        static string s_PrefKey_ShownVersion;
        static string s_PrefKey_AutoOpenDisabled;

        /// <summary>專案指紋 — 走共用解析點 <see cref="UCL_RepoPath.ProjectFingerprint"/>
        /// （本檔原本有一份逐字相同的私有副本，2026-08-11 收攏；演算法未變，既有 key 不失效）。</summary>
        static string ProjectFingerprint => UCL_RepoPath.ProjectFingerprint;

        /// <summary>EditorPrefs key — 已展示版本（首次安裝時為空）。Per-project namespaced。</summary>
        public static string PrefKey_ShownVersion =>
            s_PrefKey_ShownVersion ??= $"UCL_Core.Welcome.ShownVersion@{ProjectFingerprint}";

        /// <summary>EditorPrefs key — 使用者主動關閉自動彈出（即使新版也不再彈）。Per-project namespaced。</summary>
        public static string PrefKey_AutoOpenDisabled =>
            s_PrefKey_AutoOpenDisabled ??= $"UCL_Core.Welcome.AutoOpenDisabled@{ProjectFingerprint}";

        // 區塊職責：第一次顯示時自動 push AgentSkillManagerPage 到頂的 once-flag
        // 物理意義：強制使用者第一次開 Welcome 時看到 Skill 安裝頁
        // 數值影響：本 page 物件存活期間只觸發一次；EditorPrefs 控制是否真的彈
        bool m_DidAutoPopupCheck;

        // PopupSearchCache 需要的容器 — 給語言下拉選單用
        readonly UCL_ObjectDictionary m_LangDic = new UCL_ObjectDictionary();

        // 區塊職責：長文字 Label 的 wordWrap 樣式（lazy 建一次後重用）
        // 物理意義：UCL_GUIStyle.LabelStyle 預設不換行 → 一行很長的描述 / hint 會撐爆視窗寬度，
        //          配合 IMGUI 的 auto-expand 把整頁拉寬到不舒服。給內文 Label 套上 wordWrap=true
        //          + ExpandWidth=true（HorizontalScope 內的水平捲動才會收歛）。
        // 數值影響：純樣式快取，無副作用
        GUIStyle m_WrapLabelStyle;
        GUIStyle WrapLabelStyle
        {
            get
            {
                if (m_WrapLabelStyle == null)
                {
                    m_WrapLabelStyle = new GUIStyle(UCL_GUIStyle.LabelStyle)
                    {
                        wordWrap = true,
                    };
                }
                return m_WrapLabelStyle;
            }
        }

        // 區塊備註：原本的內嵌文件搜尋已搬出至 UCL_DocSearchPage；
        //          本頁只保留一顆「🔍 文件搜尋」按鈕跳轉過去。

        /// <summary>從外部建立一份 Welcome 頁（不開新視窗）。</summary>
        public static UCL_WelcomePage Create()
        {
            return UCL_EditorPage.Create<UCL_WelcomePage>();
        }

        // ===========================================================
        // 區塊職責：對外的「開啟 EditorMenu 並導航到 Welcome」靜態入口
        // 物理意義：自動彈出與「UCL/Welcome」選單兩種觸發都走同一條路，避免邏輯歧異
        // 數值影響：可能開啟一個 Editor 視窗、push 一個 page
        // ===========================================================
        [MenuItem("UCL/Welcome")]
        public static void OpenAndShow()
        {
            // 1) 設置「下次 EditorMenuPage 第一次繪製時」的鉤子，要求 push WelcomePage
            UCL_EditorMenuPage.s_OnFirstDraw = (controller) =>
            {
                UCL_EditorPage.Create<UCL_WelcomePage>(controller);
            };

            // 2) 開啟 EditorMenu 視窗 — 用 ExecuteMenuItem 觸發 [MenuItem("UCL/Menu")]
            //    避免直接 reference UCL_MenuWindow（住在 UCL_CoreEditor asm，從 UCL_Core
            //    asm 看不到，會跨 asmdef 編譯失敗）
            EditorApplication.ExecuteMenuItem("UCL/Menu");

            // 3) 標記已展示當前版本，避免下次重啟 Editor 又彈一次
            EditorPrefs.SetString(PrefKey_ShownVersion, CurrentVersion);
        }

        protected override void ContentOnGUI()
        {
            // 區塊職責：首幀檢查是否要自動 push AgentSkillManagerPage
            // 物理意義：第一次開 Welcome、使用者尚未確認過 Skill 機制 → 強制曝光
            // 數值影響：push 一頁到 controller stack；本 page 物件存活期間只跑一次
            // 設計取捨：放在 ContentOnGUI 而不是 Init — Init 時 p_Controller 可能還沒就位；
            //          首幀繪製時 controller 已穩定，且只 push 一次後設旗標避免重入
            if (!m_DidAutoPopupCheck)
            {
                m_DidAutoPopupCheck = true;
                if (p_Controller != null)
                {
                    UCL_AgentSkillManagerPage.MaybeAutoPopupOnWelcome(p_Controller);
                }
            }

            // 不要在這裡再開 BeginScrollView — UCL_EditorPage.OnGUI 已包外層 ScrollViewScope；
            // 巢狀 ScrollView 在 Unity 2021 IMGUI 會拋 InvalidCastException（Unity 6 才會被內部靜默 recover）。
            DrawHeader();
            GUILayout.Space(4); // 呼叫 Layout 空白元件，帶入高度參數 4 以拉開標題與語言列的間距
            DrawLanguageBar();
            GUILayout.Space(4); // 呼叫 Layout 空白元件，帶入高度參數 4 以拉開語言列與指令表列的間距
            DrawCommandTableBar(); // 呼叫繪製指令對照表入口列，將指令預覽按鈕繪製於語言列正下方
            GUILayout.Space(8); // 呼叫 Layout 空白元件，帶入高度參數 8 以拉開指令表列與簡介區塊的間距
            DrawIntro();
            GUILayout.Space(8);
            DrawSearchEntry();
            GUILayout.Space(8);
            DrawFeatureGrid();
            GUILayout.Space(8);
            DrawSkillEntry();
            GUILayout.Space(8);
            DrawDocsLinks();
            GUILayout.Space(8);
            DrawFooter();
        }

        // ===========================================================
        // 區塊：頂部標題列
        // ===========================================================
        void DrawHeader()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                var titleStyle = new GUIStyle(UCL_GUIStyle.LabelStyle)
                {
                    fontSize = 22,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                };
                GUILayout.Label(UCL_CodeLocalize.Get("Welcome.Title"), titleStyle);

                var sub = new GUIStyle(UCL_GUIStyle.LabelStyle)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Italic,
                };
                GUILayout.Label(string.Format(UCL_CodeLocalize.Get("Welcome.Subtitle"), CurrentVersion), sub);
            }
        }

        // ===========================================================
        // 區塊：語言切換列 — 直接在 Welcome 頁切，不用跳到 LocalizeEditPage
        // 物理意義：使用者最常做的本地化操作就是「切語言看翻譯效果」，把它放在最顯眼的地方
        //          （跟其他 UCL_Core/EOV Editor 頁連動 — 切完後所有走 UCL_LocalizeManager
        //          / UCL_CodeLocalize 的字串都會更新）
        // 數值影響：UCL_LocalizeService.SetLanguage 寫 PlayerPrefs + 觸發 reload + raise
        //          OnLanguageChanged event
        // ===========================================================
        void DrawLanguageBar()
        {
            using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("Welcome.Lang.Label"),
                    UCL_GUIStyle.LabelStyle, GUILayout.Width(80));

                var allIds = UCL_LanguageCodeAsset.Util.GetAllIDs(true);
                if (allIds == null || allIds.Count == 0)
                {
                    GUILayout.Label(UCL_CodeLocalize.Get("Welcome.Lang.NoneAvailable"),
                        WrapLabelStyle);
                    return;
                }

                string curLang = UCL_LocalizeService.CurLang;
                int curIdx = allIds.IndexOf(curLang);
                if (curIdx < 0) curIdx = 0;

                // 顯示「en — English」這種「ID — DisplayName」組合
                var displayOptions = allIds
                    .Select(id =>
                    {
                        var asset = UCL_LanguageCodeAsset.Util.GetData(id);
                        string name = asset != null && !string.IsNullOrEmpty(asset.LanguageName) ? asset.LanguageName : id;
                        return name == id ? id : $"{id} — {name}";
                    })
                    .ToList();

                // 區塊職責：限制 picker 寬度避免撐爆水平 layout，把「進階編輯…」推到視窗外
                // 物理意義：PopupSearchCache 預設依文字寬度自動撐開，被選項裡的長字會
                //          連帶把後面的元素擠出可視範圍
                // 數值影響：picker 固定 240px 寬，超出顯示 "..."；之後的「進階編輯…」按鈕
                //          能保證可見
                using (new GUILayout.HorizontalScope(GUILayout.Width(240)))
                {
                    int newIdx = UCL_GUILayout.PopupSearchCache(curIdx, displayOptions, m_LangDic, "WelcomeLangPicker");
                    if (newIdx != curIdx && newIdx >= 0 && newIdx < allIds.Count)
                    {
                        string newLang = allIds[newIdx];
                        UCL_LocalizeService.SetLanguage(newLang);
                        Debug.Log($"[UCL_Welcome] Language switched: {curLang} → {newLang}");
                    }
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(UCL_CodeLocalize.Get("Welcome.Lang.OpenEditor"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    UCL_EditorPage.Create<UCL_LocalizeEditPage>();
                }
            }
        }

        // ===========================================================
        // 區塊職責：繪製指令對照表入口列 — 提供一鍵跳轉/預覽指令表的按鈕。
        // 物理意義：提供使用者快速在 Editor 內預覽 AI Agent 口語指令對照手冊的快捷管道，不需要自行尋找文件。
        // 數值影響：點擊時會解析 URL 路徑並建立一個內嵌 MarkdownViewerPage 用於渲染。
        // ===========================================================
        void DrawCommandTableBar()
        {
            // 建立水平佈局容器，並套用 "box" 樣式以凸顯該功能區域
            using (new GUILayout.HorizontalScope("box"))
            {
                // 區塊職責：繪製「📄 預覽指令表」按鈕，並於點擊時載入對應語言的 CommandTable.md
                // 物理意義：仿照 UCL_DocSearchPage 的預覽按鈕，在 Editor 內直接開啟 MarkdownViewer 進行預覽
                // 數值影響：當點擊按鈕 (回傳 true) 時，解析相對路徑並將其絕對化後傳入 UCL_MarkdownViewerPage
                if (GUILayout.Button(UCL_CodeLocalize.Get("Welcome.CommandTable.Btn"),
                    UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    // 取得當前的語系代碼（例如 "zh-Hant"、"en" 等）用於動態路徑拼接
                    string curLang = UCL_LocalizeService.CurLang;

                    // 組合出相對於 git root 的 CommandTable.md 路徑，用於 UI 與 Open 轉址比對
                    string relPath = $"CardGame/Assets/UCL/UCL_Core/Docs~/{curLang}/CommandTable.md";

                    // 藉由 UCL_URL 將多語系 prefix 轉譯成實體的本地絕對路徑，若該語系缺檔則自動 fallback 到 en
                    string absPath = UCL_URL.ResolveURL("ucl_core:Docs~/{lang}/CommandTable.md");

                    // 呼叫 MarkdownViewer 靜態方法在 Editor 內直接打開該文件頁面，並 push 到 controller stack
                    UCL_MarkdownViewerPage.Create(relPath, absPath);
                }

                // 區塊職責：繪製前導標籤
                // 物理意義：視覺提示該列為指令表功能入口
                GUILayout.Label(UCL_CodeLocalize.Get("Welcome.CommandTable.Label"),
                    UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

                // 區塊職責：顯示對應的功能說明文字
                // 物理意義：向使用者說明指令對照表的作用
                // 數值影響：佔用扣除標籤與按鈕後的剩餘寬度，並啟用 wordWrap 自動換行以適應寬度
                GUILayout.Label(UCL_CodeLocalize.Get("Welcome.CommandTable.Desc"), WrapLabelStyle);

                // 在說明文字與按鈕之間插入彈性空白，使得按鈕自動靠右對齊
                GUILayout.FlexibleSpace();


            }
        }

        // ===========================================================
        // 區塊：簡介
        // ===========================================================
        void DrawIntro()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("Welcome.IntroTitle"), UCL_GUIStyle.LabelStyle);
                GUILayout.Label(UCL_CodeLocalize.Get("Welcome.IntroBody"), WrapLabelStyle);
            }
        }

        // ===========================================================
        // 區塊：文件搜尋入口 — 點按鈕 push UCL_DocSearchPage 到當前 controller
        // 物理意義：搜尋功能複雜（進階選項 / 結果列表 / 同義詞檔路徑），獨立成 Page 比塞在 Welcome 裡乾淨
        // 數值影響：純 push page，搜尋邏輯都在 UCL_DocSearchPage 內
        // ===========================================================
        void DrawSearchEntry()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("Welcome.Search.Title"), UCL_GUIStyle.LabelStyle);
                GUILayout.Label(UCL_CodeLocalize.Get("Welcome.Search.Hint"), WrapLabelStyle);
                if (GUILayout.Button(UCL_CodeLocalize.Get("Welcome.Search.OpenPageButton"),
                    UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                {
                    UCL_DocSearchPage.Create();
                }
            }
        }

        // ===========================================================
        // 區塊：核心功能 — 每張「卡片」一個按鈕，點下去開對應頁面或文件
        // 物理意義：把「先讀文件再用」改成「點按鈕直達對應 Editor 頁」
        // ===========================================================
        void DrawFeatureGrid()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("Welcome.FeaturesTitle"), UCL_GUIStyle.LabelStyle);

                FeatureCard(
                    UCL_CodeLocalize.Get("Welcome.Asset.Title"),
                    UCL_CodeLocalize.Get("Welcome.Asset.Desc"),
                    UCL_CodeLocalize.Get("Welcome.Asset.Btn"),
                    () => { UCL_ModuleServiceEditPage.Create(); },
                    "Docs~/{lang}/Architecture/Polymorphism_In_UCL.md");

                FeatureCard(
                    UCL_CodeLocalize.Get("Welcome.Localize.Title"),
                    UCL_CodeLocalize.Get("Welcome.Localize.Desc"),
                    UCL_CodeLocalize.Get("Welcome.Localize.Btn"),
                    () => { UCL_EditorPage.Create<UCL_LocalizeEditPage>(); },
                    "Docs~/{lang}/UCL_Localize/UCL_LocalizeEditPage.md");

                FeatureCard(
                    UCL_CodeLocalize.Get("Welcome.Agent.Title"),
                    UCL_CodeLocalize.Get("Welcome.Agent.Desc"),
                    UCL_CodeLocalize.Get("Welcome.Agent.Btn"),
                    () => { UCL_AgentCommandsPage.Create(); },
                    "Docs~/{lang}/API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md");

                FeatureCard(
                    UCL_CodeLocalize.Get("Welcome.Editor.Title"),
                    UCL_CodeLocalize.Get("Welcome.Editor.Desc"),
                    UCL_CodeLocalize.Get("Welcome.Editor.Btn"),
                    () => { UCL_EditorPage.Create<UCL_PlayerPrefsEditPage>(); },
                    "Docs~/{lang}/UCL_EditorPage/UCL_EditorMenuPage.md");
            }
        }

        // 區塊職責：渲染單張功能卡片（標題 + 描述 + 主按鈕 +（可選）文件連結）
        // 物理意義：避免每張卡片重複寫 layout，集中於一處便於修改視覺
        // 設計取捨：原本 primary 用 (string label, Action onClick) tuple 傳，但 Unity 的 C# 編譯器
        //          對 tuple 內 lambda 推斷成 Action 還是 Func<T> 處理不夠寬容（會吐 CS1503）；
        //          改成平鋪參數 → 推斷無歧義。
        void FeatureCard(string title, string desc, string primaryLabel, Action primaryAction, string docPath)
        {
            using (new GUILayout.VerticalScope("box"))
            {
                var titleStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { fontStyle = FontStyle.Bold };
                GUILayout.Label(title, titleStyle);
                GUILayout.Label(desc, WrapLabelStyle);
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(primaryLabel, UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                    {
                        primaryAction?.Invoke();
                    }
                    if (!string.IsNullOrEmpty(docPath))
                    {
                        if (GUILayout.Button(UCL_CodeLocalize.Get("Welcome.DocBtn"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                        {
                            Application.OpenURL(UCL_URL.ResolveURL("ucl_core:" + docPath));
                        }
                    }
                }
            }
        }

        // ===========================================================
        // 區塊：Agent Skill Manager 入口（按鈕）
        // 物理意義：Skill 安裝 / 狀態管理已搬到獨立的 UCL_AgentSkillManagerPage；
        //          本 Welcome 頁只放一個入口按鈕，第一次開 Welcome 時會自動 push
        //          管理頁強制曝光（見 ContentOnGUI 開頭的 m_DidAutoPopupCheck 區塊）。
        // 數值影響：純 push page，無副作用；管理頁本身才會跑 install_skills.py。
        // ===========================================================
        void DrawSkillEntry()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                var titleStyle = new GUIStyle(UCL_GUIStyle.LabelStyle) { fontStyle = FontStyle.Bold };
                GUILayout.Label(UCL_CodeLocalize.Get("Welcome.SkillEntry.Title"), titleStyle);
                GUILayout.Label(UCL_CodeLocalize.Get("Welcome.SkillEntry.Desc"), WrapLabelStyle);

                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(UCL_CodeLocalize.Get("Welcome.SkillEntry.OpenBtn"),
                        UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.ExpandWidth(false)))
                    {
                        UCL_AgentSkillManagerPage.Create();
                    }
                }
            }
        }

        // 區塊職責：(已搬走) 安裝邏輯改放 UCL_AgentSkillManagerPage
        // ===========================================================


        // ===========================================================
        // 區塊：文件總入口
        // ===========================================================
        void DrawDocsLinks()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                GUILayout.Label(UCL_CodeLocalize.Get("Welcome.DocsTitle"), UCL_GUIStyle.LabelStyle);
                LinkRow(UCL_CodeLocalize.Get("Welcome.Docs.Index"),
                    "ucl_core:Docs~/{lang}/index.md");
                LinkRow(UCL_CodeLocalize.Get("Welcome.Docs.Architecture"),
                    "ucl_core:Docs~/{lang}/API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md");
                LinkRow(UCL_CodeLocalize.Get("Welcome.Docs.CreateCmd"),
                    "ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md");
                LinkRow(UCL_CodeLocalize.Get("Welcome.Docs.Validate"),
                    "ucl_core:Docs~/{lang}/Workflows/Validate_UCL_Asset_Workflow.md");
                // 區塊備註：CompileError_Diagnose_Workflow 是「最需要查錯誤時 Cmd 系統反而
                //          因 compile error 載不進來」雞生蛋情境的解法（standalone Python 工具
                //          + Tracker JSON），新人特別容易踩到，放進總入口顯眼處
                LinkRow(UCL_CodeLocalize.Get("Welcome.Docs.CompileError"),
                    "ucl_core:Docs~/{lang}/Workflows/CompileError_Diagnose_Workflow.md");
            }
        }

        void LinkRow(string label, string url)
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label("  ・", UCL_GUIStyle.LabelStyle, GUILayout.Width(20));
                if (GUILayout.Button(label, UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    Application.OpenURL(UCL_URL.ResolveURL(url));
                }
            }
        }

        // ===========================================================
        // 區塊：腳註 + 自動彈出開關
        // 物理意義：使用者可以選「以後不要自動彈」或「重設為下次再彈」
        // 數值影響：寫 EditorPrefs（per-user / per-machine）
        // ===========================================================
        void DrawFooter()
        {
            using (new GUILayout.VerticalScope("box"))
            {
                bool prevDisabled = EditorPrefs.GetBool(PrefKey_AutoOpenDisabled, false);
                bool newDisabled = GUILayout.Toggle(prevDisabled,
                    UCL_CodeLocalize.Get("Welcome.AutoOpenToggle"),
                    WrapLabelStyle);
                if (newDisabled != prevDisabled)
                {
                    EditorPrefs.SetBool(PrefKey_AutoOpenDisabled, newDisabled);
                }

                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(UCL_CodeLocalize.Get("Welcome.ResetButton"),
                        UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        EditorPrefs.DeleteKey(PrefKey_ShownVersion);
                        EditorPrefs.SetBool(PrefKey_AutoOpenDisabled, false);
                        Debug.Log(UCL_CodeLocalize.Get("Welcome.ResetLog"));
                    }
                    GUILayout.FlexibleSpace();
                    string shownVer = EditorPrefs.GetString(PrefKey_ShownVersion,
                        UCL_CodeLocalize.Get("Welcome.NotSet"));
                    GUILayout.Label(string.Format(UCL_CodeLocalize.Get("Welcome.PrefKeyLabel"), shownVer),
                        UCL_GUIStyle.LabelStyle);
                }
            }
        }
    }
}
#endif
