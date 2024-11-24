
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 11/24 2024
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
    public class UCL_CreateNewModulePage : UCL_CommonEditorPage
    {

        public override string WindowName => UCL_LocalizeManager.Get("Create new module");

        public static UCL_CreateNewModulePage Create()
        {
            return UCL_EditorPage.Create<UCL_CreateNewModulePage>();
        }
        UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();
        protected string m_NewModuleName = "New Module";
        public UCL_CreateNewModulePage()
        {

        }
        public override void Init(UCL_GUIPageController iGUIPageController)
        {
            base.Init(iGUIPageController);
        }

        protected override void ContentOnGUI()
        {
            if (!UCL_ModuleService.Initialized)
            {
                return;
            }
            using (var aScope = new GUILayout.VerticalScope("box"))
            {
                var moduleService = UCL_ModuleService.Ins;
                if (GUILayout.Button(UCL_LocalizeManager.Get("Create new module"), UCL_GUIStyle.ButtonStyle))
                {
                    UCL_ModuleService.Ins.CreateNewModule(m_NewModuleName);
                    Close();
                }
                using (var aScope2 = new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_LocalizeManager.Get("Module ID"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    m_NewModuleName = GUILayout.TextField(m_NewModuleName, UCL_GUIStyle.TextFieldStyle);
                }
            }
            //UCL_ModuleService.Ins.OnGUI(m_DataDic.GetSubDic("ModuleService"));
            //GUILayout.Label("Test", UCL_GUIStyle.LabelStyle);

        }
    }
}