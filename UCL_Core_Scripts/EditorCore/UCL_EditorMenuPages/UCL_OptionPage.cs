
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/22 2024 13:48
using System.Collections;
using System.Collections.Generic;
using UCL.Core.LocalizeLib;
using UnityEngine;
using UCL.Core.EditorLib.Page;
using UCL.Core.UI;
using Cysharp.Threading.Tasks;

namespace UCL.Core.Page
{
    public class ButtonData
    {
        public ButtonData() { }
        public ButtonData(string iText, System.Action iClickAction = null, GUIStyle iStyle = null
            , bool iIsCloseOptionPageAfterClick = true)
        {
            m_Text = iText;
            m_ClickAction = iClickAction;
            m_Style = iStyle;
            m_IsCloseOptionPageAfterClick = iIsCloseOptionPageAfterClick;
        }
        public GUIStyle m_Style = null;
        public string m_Text = string.Empty;
        /// <summary>
        /// 是否在點選後關閉彈窗
        /// </summary>
        public bool m_IsCloseOptionPageAfterClick = true;
        public System.Action m_ClickAction = null;
    }
    /// <summary>
    /// 選項頁面
    /// 可以設定選項 標題 內文
    /// </summary>
    public class UCL_OptionPage : UCL_EditorPage
    {
        /// <summary>
        /// 確認刪除彈窗
        /// </summary>
        /// <param name="iDeleteTarget"></param>
        /// <param name="iConfirmAct"></param>
        /// <returns></returns>
        static public UCL_OptionPage ConfirmDelete(string iDeleteTarget, System.Action iConfirmAct)
        {
            string aTitle = string.Empty;
            if (UCL_LocalizeManager.ContainsKey(iDeleteTarget))
            {
                aTitle = UCL_LocalizeManager.Get("ConfirmDelete", UCL_LocalizeManager.GetID(iDeleteTarget));
            }
            else
            {
                aTitle = $"Delete {iDeleteTarget}?";
            }
            const int FontSize = 20;
            var aPage = new UCL_OptionPage(aTitle, string.Empty
                        , new ButtonData(UCL_LocalizeManager.Get("Delete"), () => {
                            iConfirmAct?.Invoke();
                        }, UCL_GUIStyle.GetButtonStyle(Color.red, FontSize))
                        , new ButtonData(UCL_LocalizeManager.Get("Cancel"), 
                        iStyle: UCL_GUIStyle.GetButtonStyle(Color.white, FontSize)));
            UCL.Core.UI.UCL_GUIPageController.CurrentRenderIns.Push(aPage);
            return aPage;
        }
        static public UCL_OptionPage Create(string iTitle, string iContext, params ButtonData[] iButtonDatas)
        {
            var aPage = new UCL_OptionPage(iTitle, iContext, iButtonDatas);
            UCL.Core.UI.UCL_GUIPageController.CurrentRenderIns.Push(aPage);
            return aPage;
        }
        static public async UniTask ShowAlertAsync(string iTitle, string iContext)
        {
            bool aIsEnd = false;
            UCL.Core.Page.UCL_OptionPage.Create(iTitle, iContext, new UCL.Core.Page.ButtonData("OK", () => aIsEnd = true));
            await UniTask.WaitUntil(() => aIsEnd);
        }

        //public override bool IsPopup => true;
        public override bool IsWindow => true;
        //public override string WindowName => UCL_LocalizeManager.Get("Alert");
        public string m_Title = string.Empty;
        public string m_Context = string.Empty;
        public ButtonData[] m_ButtonDatas = null;
        public UCL_OptionPage()
        {

        }
        public UCL_OptionPage(string iTitle, string iContext, params ButtonData[] iButtonDatas)
        {
            m_Title = iTitle;
            m_Context = iContext;
            m_ButtonDatas = iButtonDatas;
        }
        protected override void TopBar()
        {
            if(!string.IsNullOrEmpty(m_Title)) GUILayout.Label(m_Title, UCL.Core.UI.UCL_GUIStyle.GetLabelStyle(Color.white, 22));
        }
        protected override void ContentOnGUI()
        {
            if (!string.IsNullOrEmpty(m_Context)) GUILayout.Label(m_Context);
            if (!m_ButtonDatas.IsNullOrEmpty())
            {
                using (var aScope = new GUILayout.HorizontalScope("box"))
                {
                    for (int i = 0; i < m_ButtonDatas.Length; i++)
                    {
                        var aButtonData = m_ButtonDatas[i];
                        if (i > 0) GUILayout.Space(15);
                        bool aIsClicked = false;
                        if(aButtonData.m_Style != null)
                        {
                            aIsClicked = GUILayout.Button(aButtonData.m_Text, aButtonData.m_Style, GUILayout.MinWidth(60), GUILayout.ExpandWidth(false));
                        }
                        else
                        {
                            aIsClicked = GUILayout.Button(aButtonData.m_Text, UCL_GUIStyle.ButtonStyle, GUILayout.MinWidth(60), GUILayout.ExpandWidth(false));
                        }
                        if (aIsClicked)
                        {
                            if (aButtonData.m_IsCloseOptionPageAfterClick)
                            {
                                BackButtonClicked();
                            }
                            aButtonData?.m_ClickAction?.Invoke();
                        }
                    }
                }
            }

        }
    }
}