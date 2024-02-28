using System.Collections;
using System.Collections.Generic;
using UCL.Core.ServiceLib;
using UCL.Core.UI;
using UnityEngine;


namespace UCL.Core.EditorLib.Page
{
    public class UCL_DebugLogPage : UCL.Core.EditorLib.Page.UCL_EditorPage
    {
        #region static
        static public UCL_DebugLogPage Create() => UCL_EditorPage.Create<UCL_DebugLogPage>();
        #endregion

        public override bool IsWindow => true;
        public override string WindowName => $"UCL_DebugLogPage";
        protected override bool ShowCloseButton => false;

        UCL.Core.UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();
        //public void Init()
        //{
        //    UCL_DebugLogService.Init();
        //}
        public override void Init(UCL_GUIPageController iGUIPageController)
        {
            base.Init(iGUIPageController);
            UCL_DebugLogService.Init();
        }
        public override void OnClose()
        {
            base.OnClose();
        }
        protected override void ContentOnGUI()
        {
            //GUILayout.Box("Console", UI.UCL_GUIStyle.BoxStyle);
            UCL_DebugLogService.OnGUI(m_DataDic.GetSubDic("DebugLog"));
        }
    }
}