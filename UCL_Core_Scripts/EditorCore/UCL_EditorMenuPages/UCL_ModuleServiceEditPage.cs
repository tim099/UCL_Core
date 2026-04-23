// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/22 2024 13:52
using Cysharp.Threading.Tasks;
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
    /// <summary>
    /// [職責] 提供 UCL 模組服務的編輯介面，允許使用者管理模組清單、安裝狀態與版本。
    /// </summary>
    [HelpURL("ucl_core:Docs~/UCL_EditorPage/ModuleInstallation_CN.md")]
    public class UCL_ModuleServiceEditPage : UCL_CommonEditorPage
    {

        public override string WindowName => UCL_LocalizeManager.Get("UCL_ModuleServiceEditPage");

        public static UCL_ModuleServiceEditPage Create()
        {
            return UCL_EditorPage.Create<UCL_ModuleServiceEditPage>();
        }
        UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();
        public UCL_ModuleServiceEditPage()
        {

        }
        ~UCL_ModuleServiceEditPage()
        {

        }
        public override void Init(UCL_GUIPageController iGUIPageController)
        {
            if (!UCL_ModuleService.Initialized)
            {
                UCL_ModuleService.WaitUntilInitialized(default).Forget();
            }

            base.Init(iGUIPageController);
            //UCL_ModuleService.Ins.SetState(UCL_ModuleService.State.Main);
        }
        //public override void OnResume()
        //{
        //    base.OnResume();
        //    UCL_ModuleService.Ins.SetState(UCL_ModuleService.State.Main);
        //}
        public void ResumeState()
        {
            UCL_ModuleService.Ins.ResumeState();
        }
        protected override void ContentOnGUI()
        {
            if (!UCL_ModuleService.Initialized)
            {
                
                GUILayout.Label($"!UCL_ModuleService.Initialized", UCL_GUIStyle.LabelStyle);
                return;
            }
            UCL_ModuleService.Ins.OnGUI(m_DataDic.GetSubDic("ModuleService"));
            //GUILayout.Label("Test", UCL_GUIStyle.LabelStyle);

        }
    }
}
