using System;
using System.Collections;
using System.Collections.Generic;
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
#endif
                if (GUILayout.Button("PlayerPrefs", UCL_GUIStyle.ButtonStyle))
                {
                    UCL_EditorPage.Create<UCL_PlayerPrefsEditPage>();
                }
            }
        }
    }
}