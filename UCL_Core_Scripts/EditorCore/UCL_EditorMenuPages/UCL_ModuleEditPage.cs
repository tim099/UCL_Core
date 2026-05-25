
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/23 2024
using Cysharp.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using UCL.Core.EditorLib.Page;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core.Page
{
    [HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_ModuleEditPage.md")]
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
        private UCL_ModulePath.PersistantPath.ModuleEntry ModuleConfig => m_CurEditModule.ModuleEntry;
        private string m_ID;
        public void Init(UCL_Module iModule)
        {
            m_CurEditModule = iModule;
            m_ID = iModule.ID;
        }

        public override void OnClose()
        {
            //UCL_ModuleService.Ins.ClearCurrentEditModule();
            UCL_ModuleService.Ins.SetState(UCL_ModuleService.State.Main);//回到主頁
            base.OnClose();
        }
        protected override void TopBarButtons()
        {
            base.TopBarButtons();
            GUILayout.Label($"[{m_CurEditModule.ID}]", UCL_GUIStyle.LabelStyle);
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (GUILayout.Button(UCL_LocalizeManager.Get("OpenModuleRootFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                UCL.Core.FileLib.WindowsLib.OpenExplorer(ModuleConfig.RootFolder);
            }
            if (GUILayout.Button(UCL_LocalizeManager.Get("OpenModuleInstallFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                UCL.Core.FileLib.WindowsLib.OpenExplorer(ModuleConfig.InstallFolder);
            }

#endif
#if UNITY_EDITOR
            if (GUILayout.Button("RefreshAllDatas(With Reflection)", UCL_GUIStyle.ButtonStyle))
            {
                UCLI_Asset.RefreshAllAssetsWithReflection();
            }
#endif
        }
        protected override void ContentOnGUI()
        {
            if (m_CurEditModule == null || m_CurEditModule.IsLoading)
            {
                return;
            }

            // 區塊職責：判定此模組是否唯讀(內建免安裝 StreamingReadOnly)
            // 物理意義：唯讀時禁用所有寫入動作(Save/Zip/Install/UnInstall)、改提供 Fork；可瀏覽資料供參考
            // 數值影響：唯讀僅 build + 免安裝模組成立；Editor / 一般 Runtime 模組 aReadOnly=false → 行為與原本一致
            bool aReadOnly = UCL_ModuleService.Ins.IsModuleReadOnly(m_CurEditModule.ID);

            if (aReadOnly)
            {
                // 唯讀橫幅 + Fork 鈕
                GUILayout.Box(UCL_CodeLocalize.Get("Module_ReadOnly_Banner"), UCL_GUIStyle.BoxStyle);
                using (var aScope = new GUILayout.HorizontalScope())
                {
                    // Fork 新 ID 輸入(預設 <原ID>_copy)；首次 seed 預設值進 dataDic，之後由 TextField(label,dataDic,key) 自行管理
                    if (string.IsNullOrEmpty(m_DataDic.GetData<string>("ForkID", string.Empty)))
                    {
                        m_DataDic.SetData("ForkID", $"{m_CurEditModule.ID}_copy");
                    }
                    GUILayout.Label(UCL_CodeLocalize.Get("Fork_NewID_Prompt"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    string aForkID = UCL_GUILayout.TextField(string.Empty, m_DataDic, "ForkID");
                    if (GUILayout.Button(UCL_CodeLocalize.Get("Fork_To_Editable"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        var aForked = UCL_ModuleService.Ins.ForkModule(m_CurEditModule.ID, aForkID);
                        if (aForked != null)
                        {
                            // Fork 成功 → 切到新可編輯模組
                            UCL_ModuleService.Ins.EditModule(aForkID);
                        }
                    }
                }
            }
            else
            {
                // 非唯讀：原本的寫入動作鈕
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
            }

            // 唯讀時用 GUI.enabled=false 包住內容瀏覽 → 可看不可改(第一層防呆；write guard 為第二層兜底)
            bool aPrevEnabled = GUI.enabled;
            if (aReadOnly) GUI.enabled = false;

            UCL_GUILayout.DrawObjectData(m_CurEditModule, m_DataDic.GetSubDic("CurEditModule"), "CurEditModule");

            m_CurEditModule.ContentOnGUI(m_DataDic.GetSubDic("Module"));
            //GUILayout.Label("Test", UCL_GUIStyle.LabelStyle);

            if (aReadOnly) GUI.enabled = aPrevEnabled;
        }
    }
}