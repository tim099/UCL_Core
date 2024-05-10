// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/22 2024 13:52
using System;
using System.Collections;
using System.Collections.Generic;
using UCL.Core;
using UCL.Core.EditorLib.Page;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.Page
{
    public class UCL_ModuleServiceEditPage : UCL_CommonEditorPage
    {

        public override string WindowName => UCL_LocalizeManager.Get("UCL_ModuleServiceEditPage");

        static public UCL_ModuleServiceEditPage Create() => UCL_EditorPage.Create<UCL_ModuleServiceEditPage>();
        UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();
        public UCL_ModuleServiceEditPage()
        {

        }
        ~UCL_ModuleServiceEditPage()
        {

        }

        protected override void ContentOnGUI()
        {
            if (!UCL_ModuleService.Initialized)
            {
                return;
            }
            UCL_ModuleService.Ins.OnGUI(m_DataDic.GetSubDic("ModuleService"));
            //GUILayout.Label("Test", UCL_GUIStyle.LabelStyle);

        }
    }
}
