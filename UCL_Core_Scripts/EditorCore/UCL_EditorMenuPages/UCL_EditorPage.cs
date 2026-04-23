using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;
namespace UCL.Core.EditorLib.Page
{
    /// <summary>
    /// Base class of Editor Pages
    /// </summary>
    public class UCL_EditorPage : UCL.Core.UI.UCL_GUIPage
    {
        #region static
        static public T Create<T>(UCL.Core.UI.UCL_GUIPageController iController = null) where T : UCL_EditorPage, new()
        {
            var aPage = new T();
            if (iController == null) iController = UCL.Core.UI.UCL_GUIPageController.CurrentRenderIns;
            iController.Push(aPage);
            return aPage;
        }
        #endregion

        /// <summary>
        /// if ShowCloseButton is true, Show Close button on Top bar
        /// 是否顯示關閉按鈕(會直接關閉所有頁面)
        /// </summary>
        virtual protected bool ShowCloseButton => true;
        /// <summary>
        /// if ShowBackButton is true, Show Back button on Top bar
        /// 是否顯示返回鈕
        /// </summary>
        virtual protected bool ShowBackButton => (!ShowCloseButton || p_Controller.Pages.Count > 1);

        /// <summary>
        /// [物理意義] 快取當前頁面的 HelpURL 字串，避免重複反射讀取。
        /// </summary>
        protected string m_PageHelpURL = null;

        protected Vector2 m_GUIScrollPos = Vector2.zero;
        protected Vector2 m_GUIScrollPos2 = Vector2.zero;
        /// <summary>
        /// Action trigger after clicked Back Button
        /// 返回鍵點下後執行的行為(預設為回上一頁)
        /// </summary>
        virtual protected void BackButtonClicked()
        {
            p_Controller.Pop();
        }
        /// <summary>
        /// Action trigger after clicked Close Button
        /// 返回鍵點下後執行的行為(預設為回上一頁)
        /// </summary>
        virtual protected void CloseButtonClicked()
        {
            p_Controller.PopAll();
        }

        /// <summary>
        /// [職責] 獲取並快取類別定義中的 HelpURLAttribute。
        /// [計算邏輯] 透過反射讀取類別屬性，若無則快取為空字串。
        /// </summary>
        protected string GetPageHelpURL()
        {
            if (m_PageHelpURL == null)
            {
                var aHelpURLAttr = System.Attribute.GetCustomAttribute(GetType(), typeof(HelpURLAttribute)) as HelpURLAttribute;
                m_PageHelpURL = aHelpURLAttr != null ? aHelpURLAttr.URL : string.Empty;
            }
            return m_PageHelpURL;
        }

        /// <summary>
        /// Draw TopBar
        /// 繪製上方按鈕列
        /// </summary>
        virtual protected void TopBar()
        {
            int aAction = 0;//0 none 1 back 2 close
            using (var aScope = new GUILayout.HorizontalScope("box"))
            {
                if (ShowBackButton)
                {
                    if (GUILayout.Button(UCL.Core.LocalizeLib.UCL_LocalizeManager.Get("Back"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        aAction = 1;
                    }
                }

                if (ShowCloseButton)//Close all pages
                {
                    if (GUILayout.Button(UCL.Core.LocalizeLib.UCL_LocalizeManager.Get("Close"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        aAction = 2;
                    }
                }

                // [職責] 在 TopBarButtons 區域的第一個位置繪製 Help 按鈕。
                // [物理意義] 讓使用者能快速開啟該功能頁面的說明文件。
                string aHelpURL = GetPageHelpURL();
                if (!string.IsNullOrEmpty(aHelpURL))
                {
                    UCL_GUILayout.DrawHelpButton(aHelpURL);
                }

                try
                {
                    TopBarButtons();
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                }
                
                GUILayout.FlexibleSpace();
            }
            switch (aAction)
            {
                case 1:
                    {
                        BackButtonClicked();
                        break;
                    }
                case 2:
                    {
                        CloseButtonClicked();
                        break;
                    }
            }
        }
        virtual protected void TopBarButtons()
        {

        }
        /// <summary>
        /// 繪製內容
        /// Draw content
        /// </summary>
        virtual protected void ContentOnGUI()
        {

        }
        public override void OnGUI()
        {
            GUILayout.BeginVertical();
            try
            {
                TopBar();
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
            using (var aScope = new GUILayout.ScrollViewScope(m_GUIScrollPos2))
            {
                m_GUIScrollPos2 = aScope.scrollPosition;
                try
                {
                    ContentOnGUI();
                }
                catch(System.Exception e)
                {
                    Debug.LogException(e);
                }
            }
            GUILayout.EndVertical();
        }

    }
}