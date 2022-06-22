using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UCL.Core.LocalizeLib;
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
        /// Draw TopBar
        /// 繪製上方按鈕列
        /// </summary>
        virtual protected void TopBar()
        {
            using (var aScope = new GUILayout.HorizontalScope("box"))
            {
                if (ShowBackButton)
                {
                    if (GUILayout.Button(UCL.Core.LocalizeLib.UCL_LocalizeManager.Get("Back"), GUILayout.ExpandWidth(false)))
                    {
                        BackButtonClicked();
                    }
                }

                if (ShowCloseButton)//Close all pages
                {
                    if (GUILayout.Button(UCL.Core.LocalizeLib.UCL_LocalizeManager.Get("Close"), GUILayout.ExpandWidth(false)))
                    {
                        CloseButtonClicked();
                    }
                }

                TopBarButtons();
                GUILayout.FlexibleSpace();
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
            {
                TopBar();
            }
            using (var aScope = new GUILayout.ScrollViewScope(m_GUIScrollPos2))
            {
                m_GUIScrollPos2 = aScope.scrollPosition;
                ContentOnGUI();
            }
            GUILayout.EndVertical();
        }

    }
}