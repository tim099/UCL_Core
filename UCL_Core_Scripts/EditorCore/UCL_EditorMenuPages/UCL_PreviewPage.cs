using System.Collections;
using System.Collections.Generic;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.StringExtensionMethods;
using UnityEngine;
using UCL.Core.EditorLib.Page;
using UCL.Core;
namespace UCL.Core.Page
{
    public class UCL_PreviewPage : UCL_EditorPage
    {
        protected override bool ShowCloseButton => false;
        static public UCL_PreviewPage Create(UCLI_Preview iData, bool iIsShowEditButton = false)
        {
            var aPage = new UCL_PreviewPage(iData, iIsShowEditButton);
            UCL.Core.UI.UCL_GUIPageController.CurrentRenderIns.Push(aPage);
            return aPage;
        }
        UCLI_Preview m_Data = null;
        bool m_IsShowEditButton = false;
        public override bool IsWindow => true;

        private UCL_ObjectDictionary m_DataDic = new();
        public UCL_PreviewPage() { }
        public UCL_PreviewPage(UCLI_Preview iData, bool iIsShowEditButton)
        {
            m_Data = iData;
            m_IsShowEditButton = iIsShowEditButton;
        }

        protected override void ContentOnGUI()
        {
            m_Data.Preview(m_DataDic, m_IsShowEditButton);
        }
    }
}

