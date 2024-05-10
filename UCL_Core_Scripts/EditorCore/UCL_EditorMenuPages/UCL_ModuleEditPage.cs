
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/23 2024 18:25
using Cysharp.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using UCL.Core.EditorLib.Page;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.Page
{
    public class UCL_ModuleEditPage : UCL_CommonEditorPage
    {

        public override string WindowName => $"UCL_ModuleEditPage({m_ID})";//UCL_LocalizeManager.Get("UCL_ModuleEditPage");

        static public UCL_ModuleEditPage Create(UCL_Module iModule)
        {
            var aPage = UCL_EditorPage.Create<UCL_ModuleEditPage>();
            aPage.Init(iModule);
            return aPage;
        }
        private UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();
        private UCL_Module m_CurEditModule;
        private UCL_ModulePath.PersistantPath.ModuleConfig ModuleConfig => m_CurEditModule.ModuleConfig;
        private string m_ID;
        public void Init(UCL_Module iModule)
        {
            m_CurEditModule = iModule;
            m_ID = iModule.ID;
        }

        public override void OnClose()
        {
            UCL_ModuleService.Ins.ClearCurrentEditModule();
            base.OnClose();
        }
        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            GUILayout.Label($"ID: {m_CurEditModule.ID}", UCL_GUIStyle.LabelStyle);
#if UNITY_EDITOR_WIN
            if (GUILayout.Button("Open Folder", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                UCL.Core.FileLib.WindowsLib.OpenExplorer(ModuleConfig.RootFolder);
            }
            if (GUILayout.Button("Open Install Folder", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                UCL.Core.FileLib.WindowsLib.OpenExplorer(ModuleConfig.InstallFolder);
            }
#endif
        }
        protected override void ContentOnGUI()
        {
            if (m_CurEditModule == null || m_CurEditModule.IsLoading)
            {
                return;
            }
            using (var aScope = new GUILayout.HorizontalScope())
            {

                if (GUILayout.Button("Zip Module", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    ModuleConfig.ZipModule();
                }
                if (GUILayout.Button("Save Module", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    m_CurEditModule.Save();
                }
                if (GUILayout.Button("Load Module", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    m_CurEditModule.Load(m_CurEditModule.ID, m_CurEditModule.ModuleEditType);
                }
                if (GUILayout.Button("Install Module", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    //Debug.LogError("Install Module");
                    m_CurEditModule.Install().Forget();
                }
                if (GUILayout.Button("UnInstall Module", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    //Debug.LogError("Install Module");
                    m_CurEditModule.UnInstall();
                }
            }


            UCL_GUILayout.DrawObjectData(m_CurEditModule, m_DataDic.GetSubDic("CurEditModule"), "CurEditModule");

            m_CurEditModule.OnGUI(m_DataDic.GetSubDic("Module"));
            //GUILayout.Label("Test", UCL_GUIStyle.LabelStyle);

        }
    }
}