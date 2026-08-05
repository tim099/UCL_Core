// 2026-05-18 (gura T19 BuildPlayerCheck fix): 整檔包 #if UNITY_EDITOR — 用 UCL_AgentSkillManagerPage
// (該 type 自己包 #if UNITY_EDITOR), Player Build 找不到 → CS0103. 本 page IMGUI editor-only, 沒 Player 用途.
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;


namespace UCL.Core.EditorLib.Page
{
    public class UCL_EditorMenuPage : UCL_CommonEditorPage
    {
        public override string WindowName => "UCL_EditorMenu";
        protected override bool ShowCloseButton => false;
        protected override bool ShowBackButton => false;

        // 區塊職責：Page 選擇器下拉的快取
        // 物理意義：反射收集所有 UCL_CommonEditorPage 子類中 ShowInPageMenu==true 者；
        //          快取以避免每幀掃 assembly。Domain reload 後 static 會重建，自動更新。
        // 數值影響：s_PagePickerEntries 為 (顯示名, Type) tuple；s_PagePickerLabels 為下拉用 List
        static List<(string label, Type type)> s_PagePickerEntries;
        static List<string> s_PagePickerLabels;
        int m_PagePickerSelected = 0;
        // PopupSearchCache 內部狀態容器（per-page；同 page 多個 popup 用 key 區分）
        readonly UCL_ObjectDictionary m_PagePickerDic = new UCL_ObjectDictionary();

        // ===========================================================
        // 外部可注入「首次繪製」鉤子：本頁第一次跑 ContentOnGUI 時會 invoke 一次然後清除。
        // 物理意義：給 UCL_WelcomePage.OpenAndShow() 之類的入口在開啟 EditorMenu 視窗後
        //          自動 push 一個子頁（例如歡迎頁）— 因為視窗的 controller 是私有的，
        //          無法在 ShowMenu 之後直接 Create<T> 到正確的 controller，
        //          只能等本頁的 ContentOnGUI 實際在它的 controller 內執行時才知道 CurrentRenderIns。
        // 數值影響：被呼叫一次後立刻清為 null，避免重複 push。
        // ===========================================================
        internal static Action<UCL_GUIPageController> s_OnFirstDraw;
        bool m_FirstDrawHandled = false;

        // 區塊職責：第一次顯示時自動 push AgentSkillManagerPage 到頂的 once-flag
        // 物理意義：除了 UCL_WelcomePage 之外，使用者也可能直接從選單 / 其他入口進到 EditorMenu，
        //          此時若 Skill 機制尚未確認過 → 同樣強制曝光一次。重複曝光由
        //          UCL_AgentSkillManagerPage.MaybeAutoPopupOnWelcome 內部的 EditorPrefs 版本旗標把關。
        // 數值影響：本 page 物件存活期間只觸發一次；EditorPrefs 控制是否真的彈出
        bool m_DidAutoPopupCheck = false;

        //UCL.Core.UCL_ObjectDictionary m_Dic = new UCL.Core.UCL_ObjectDictionary();

        /// <summary>
        /// Draw Editor Munu
        /// </summary>
        protected override void ContentOnGUI()
        {
            // 區塊職責：第一次繪製時觸發外部鉤子（自動導航到指定子頁）
            // 物理意義：讓 UCL_WelcomePage.OpenAndShow() 等入口能在 EditorMenu 開好之後
            //          push 子頁；只跑一次避免每幀重複。
            // 數值影響：可能多 push 一個 page 到 CurrentRenderIns
            if (!m_FirstDrawHandled)
            {
                m_FirstDrawHandled = true;
                var hook = s_OnFirstDraw;
                if (hook != null)
                {
                    s_OnFirstDraw = null; // 用完即清，避免後續 EditorMenu 開啟也被影響
                    try { hook(UCL_GUIPageController.CurrentRenderIns); }
                    catch (Exception e) { Debug.LogWarning($"[UCL_EditorMenuPage] s_OnFirstDraw threw: {e.Message}"); }
                }
            }

            // 區塊職責：首幀檢查是否要自動 push AgentSkillManagerPage
            // 物理意義：使用者可能不經 Welcome 直接進 EditorMenu（從 UCL/Menu 選單 / 其他入口）；
            //          此時若 Skill 機制尚未確認過 → 比照 Welcome 自動曝光。
            //          MaybeAutoPopupOnWelcome 內部會比對 EditorPrefs 版本，已確認過則不會重複彈。
            // 數值影響：可能 push 一個 UCL_AgentSkillManagerPage 到 CurrentRenderIns；只跑一次。
            // 設計取捨：放在 ContentOnGUI 而不是 Init — Init 時 CurrentRenderIns 可能還沒就位
            if (!m_DidAutoPopupCheck)
            {
                m_DidAutoPopupCheck = true;
                var ctrl = UCL_GUIPageController.CurrentRenderIns;
                if (ctrl != null)
                {
                    UCL_AgentSkillManagerPage.MaybeAutoPopupOnWelcome(ctrl);
                }
            }

            using (var aScope = new GUILayout.VerticalScope("box"))//, GUILayout.MaxWidth(320)
            {
                using (var aScopeH = new GUILayout.HorizontalScope("box"))
                {
                    var aStyleData = UCL_GUIStyle.CurStyleData;
                    UCL_GUIStyle.SetSizeOnGUI();
                }

#if UNITY_EDITOR
                // 區塊職責：歡迎頁入口（介紹 UCL_Core 主要功能、文件連結、首次彈出開關）
                // 物理意義：第一次安裝會自動彈出（[InitializeOnLoad] 偵測），這顆按鈕讓使用者
                //          也能隨時手動回顧。
                // 數值影響：push 一個 UCL_WelcomePage 到當前 controller。
                if (GUILayout.Button(UCL_CodeLocalize.Get("Welcome.MenuButton"), UCL_GUIStyle.GetButtonStyle(Color.cyan)))
                {
                    UCL_WelcomePage.Create();
                }

                // 區塊職責：工具集入口（Tim 2026-08-05 指派 — 取代原本直通文件搜尋頁的那顆按鈕）
                // 物理意義：工具型頁面（Git 攤平同步 / 文件搜尋 / 多語系編輯）收攏進 UCL_ToolBoxPage 一層。
                //          外層按鈕會隨功能無上限成長，收一層之後**新增工具只改 UCL_ToolBoxPage**，
                //          不必再動本檔。文件搜尋仍可從 Welcome 頁與工具集兩處進入，入口沒有變少。
                // 數值影響：push 一個 UCL_ToolBoxPage 到當前 controller。
                if (GUILayout.Button(UCL_CodeLocalize.Get("ToolBox.MenuButton"), UCL_GUIStyle.GetButtonStyle(Color.cyan)))
                {
                    UCL_ToolBoxPage.Create();
                }
#endif

                if (GUILayout.Button(UCL_LocalizeManager.Get("Edit Modules"), UCL_GUIStyle.ButtonStyle))
                {
                    UCL_ModuleServiceEditPage.Create();
                }

                if (GUILayout.Button(UCL_LocalizeManager.Get("UCL_LocalizeEditPage"), UCL_GUIStyle.ButtonStyle))
                {
                    UCL_EditorPage.Create<UCL_LocalizeEditPage>();
                }
#if UNITY_EDITOR
                // 區塊職責：提供按鈕以開啟 Agent Commands 頁面
                // 物理意義：快速導向 Agent Command 隊列與手動觸發管理面板
                // 數值影響：無
                if (GUILayout.Button(UCL_CodeLocalize.Get("Agent Commands"), UCL_GUIStyle.ButtonStyle))
                {
                    UCL_AgentCommandsPage.Create();
                }

                // 區塊職責：提供按鈕以開啟 Chat Tavern 頁面
                // 物理意義：快速導向酒館聊天室面板，以便人類開發者與 AI Agent 在同一個聊天室內進行對話與腦力激盪
                // 數值影響：無
                if (GUILayout.Button("Chat Tavern", UCL_GUIStyle.ButtonStyle))
                {
                    UCL_ChatTavernPage.Create();
                }

                // 區塊職責：提供按鈕以開啟控制台頁面
                // 物理意義：集中控制專案內各項重要設定 (目前第一塊：聊天酒館系統總開關)
                // 數值影響：push 一個 UCL_ControlPanelPage 到當前 controller
                if (GUILayout.Button("控制台", UCL_GUIStyle.ButtonStyle))
                {
                    UCL_ControlPanelPage.Create();
                }
#endif
                if (GUILayout.Button("UCL_LoginStatusPage", UCL_GUIStyle.ButtonStyle))
                {
                    UCL_EditorPage.Create<UCL_LoginStatusPage>();
                }

                // 區塊職責：「Page 選擇器」— 列出所有 ShowInPageMenu==true 的 UCL_CommonEditorPage 子類
                // 物理意義：給臨時新增 / 不想為每頁加按鈕的場景一個共用入口；
                //          左側 Open 按鈕，右側可搜尋下拉，選好後按 Open 開啟對應 Page
                // 數值影響：Open 時 push 對應 Page 到 controller；Refresh 重掃 assembly 重建快取
                DrawPagePickerDropdown();
            }
        }

        // ===========================================================
        // 區塊：Page 選擇器下拉（採 UCL_GUILayout.PopupSearchCache，自帶搜尋 + 內部快取）
        // ===========================================================
        void DrawPagePickerDropdown()
        {
            EnsurePagePickerCache();

            using (new GUILayout.HorizontalScope("box"))
            {
                bool hasEntries = s_PagePickerEntries != null && s_PagePickerEntries.Count > 0;

                // 左側：Open 按鈕
                GUI.enabled = hasEntries;
                if (GUILayout.Button("Open", UCL_GUIStyle.GetButtonStyle(Color.cyan), GUILayout.Width(60)))
                {
                    var entry = s_PagePickerEntries[m_PagePickerSelected];
                    try
                    {
                        UCL_EditorPage.CreateByType(entry.type);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[UCL_EditorMenuPage] 開啟 {entry.type.Name} 失敗：{e.Message}");
                    }
                }
                GUI.enabled = true;

                // 中間：可搜尋下拉（PopupSearchCache 內建模糊搜尋 + 子 dict 快取）
                if (!hasEntries)
                {
                    GUILayout.Label("(尚無 Page opt-in；override ShowInPageMenu => true 即可加入)",
                        UCL_GUIStyle.LabelStyle);
                }
                else
                {
                    if (m_PagePickerSelected < 0 || m_PagePickerSelected >= s_PagePickerEntries.Count)
                        m_PagePickerSelected = 0;
                    m_PagePickerSelected = UCL_GUILayout.PopupSearchCache(
                        m_PagePickerSelected, s_PagePickerLabels, m_PagePickerDic, "PagePicker");
                }

                // 右側：Reload — 重掃 assembly，找出新加 ShowInPageMenu=true 的 Page
                // 物理意義：用文字而非 unicode 符號（如 ⟳）避免 Editor 字型不支援導致顯示為□
                if (GUILayout.Button("↻", UCL_GUIStyle.ButtonStyle, GUILayout.Width(60)))
                {
                    s_PagePickerEntries = null; // 重掃
                    s_PagePickerLabels = null;
                }
            }
        }

        // 區塊職責：建立 / 重建 Page 選擇器的快取
        // 物理意義：反射掃所有已載入 assembly 中 UCL_CommonEditorPage 的非抽象子類；
        //          臨時 Activator.CreateInstance 讀 ShowInPageMenu（虛屬性必須有 instance）
        // 數值影響：建一次後快取至 static；建立失敗的 Type 跳過 + LogWarning（避免一個壞 Page 阻塞整列）
        static void EnsurePagePickerCache()
        {
            if (s_PagePickerEntries != null) return;
            var list = new List<(string label, Type type)>();
            var baseType = typeof(UCL_CommonEditorPage);

            foreach (var asm in AssemblyExtensions.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; } // 部分 dynamic assembly 抓不到 type 是常態
                foreach (var t in types)
                {
                    if (t.IsAbstract) continue;
                    if (!baseType.IsAssignableFrom(t)) continue;
                    if (t == baseType) continue;
                    if (t == typeof(UCL_EditorMenuPage)) continue; // 排除自己（避免遞迴開）

                    try
                    {
                        var inst = (UCL_CommonEditorPage)Activator.CreateInstance(t);
                        if (!inst.ShowInPageMenu) continue;
                        string label = string.IsNullOrEmpty(inst.WindowName) ? t.Name : inst.WindowName;
                        if (label != t.Name) label = $"{t.Name}({label})";
                        list.Add((label, t));
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[UCL_EditorMenuPage] 跳過無法 instantiate 的 Page：{t.FullName} — {e.Message}");
                    }
                }
            }
            list.Sort((a, b) => string.Compare(a.label, b.label, StringComparison.OrdinalIgnoreCase));
            s_PagePickerEntries = list;
            s_PagePickerLabels = list.Select(x => x.label).ToList();
        }
    }
}
#endif // UNITY_EDITOR