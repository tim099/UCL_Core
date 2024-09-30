using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UCL.Core;
using UCL.Core.EditorLib.Page;
using UCL.Core.LocalizeLib;
using UCL.Core.MathLib;
using UCL.Core.UI;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.Page
{
    public class UCL_CreateAssetPage<T> : UCL_EditorPage where T : class, UCLI_Asset, new()
    {
        static public UCL_CreateAssetPage<T> Create()
        {
            var aPage = new UCL_CreateAssetPage<T>();
            UCL_GUIPageController.CurrentRenderIns.Push(aPage);

            return aPage;
        }

        protected UCL.Core.UCL_ObjectDictionary m_DataDic = new UCL.Core.UCL_ObjectDictionary();
        protected UCLI_Asset m_Preview = null;
        protected string m_CreateDes = string.Empty;
        protected string m_TypeName = string.Empty;
        protected string m_TypeLocalizedName = string.Empty;
        protected string m_GroupID = string.Empty;
        protected string m_AssetID = string.Empty;
        List<string> m_AssetIDs = new List<string>();

        protected UCL_AssetCommonMeta m_Meta = null;
        protected UCL_Asset<T> m_Util = default;
        protected UCL_ModuleEntry m_Module = new UCL_ModuleEntry();
        public override string WindowName => $"UCL_CreateAssetPage({m_TypeName})";
        public override bool IsWindow => true;
        public override void Init(UCL.Core.UI.UCL_GUIPageController iGUIPageController)
        {

            base.Init(iGUIPageController);
            string aTypeName = typeof(T).Name;
            m_TypeName = aTypeName;
            m_TypeLocalizedName = UCL_LocalizeManager.Get(aTypeName);
            string aKey = $"Create_{aTypeName}";
            if (UCL_LocalizeManager.ContainsKey(aKey))
            {
                m_CreateDes = UCL_LocalizeManager.Get(aKey);
            }
            else
            {
                m_CreateDes = UCL_LocalizeManager.Get("CreateNew");
            }
            Util.OnEdit();

            m_Meta = Util.AssetMetaIns;
            var assetIDs = UCL_ModuleService.CurEditModule.ModuleEntry.GetAllAssetsID(typeof(T));
            m_Module.ID = UCL_ModuleService.CurEditModuleID;

            if (assetIDs.Count > 0)
            {
                m_AssetID = assetIDs[0];
            }
            else//try to find a module has assets
            {
                foreach (var aModule in UCL_ModuleService.Ins.LoadedModules)
                {
                    assetIDs = aModule.ModuleEntry.GetAllAssetsID(typeof(T));
                    if (assetIDs.Count > 0)
                    {
                        m_AssetID = assetIDs[0];
                        m_Module.ID = aModule.ID;
                        break;
                    }
                }
            }

            

            //Debug.LogError("m_CreateDes:" + m_CreateDes);
            OnResume();
        }
        public UCL_Asset<T> Util
        {
            get
            {
                if (m_Util == null)
                {
                    m_Util = UCLI_Asset.GetUtilByType(typeof(T)) as UCL_Asset<T>;
                }
                return m_Util;
            }

        }
        public override void OnResume()
        {
            m_Preview = null;
            SetGroupID(string.Empty);
        }
        public override void OnClose()
        {
            base.OnClose();
        }
        protected override void TopBarButtons()
        {
            GUILayout.Label(WindowName, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));



            //using (new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label($"Type : {m_TypeName}", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                if (GUILayout.Button(UCL_LocalizeManager.Get("Copy"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    GUIUtility.systemCopyBuffer = m_TypeName;
                }
            }

            //if (GUILayout.Button(UCL_LocalizeManager.Get("Open Folder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            //{
            //    System.Diagnostics.Process.Start("explorer.exe", @"c:\teste");
            //}
        }
        protected void SetGroupID(string iID)
        {
            UCL_Module module = UCL_ModuleService.Ins.GetLoadedModule(m_Module.ID);
            m_GroupID = iID;
            m_AssetIDs = module.ModuleEntry.GetAllAssetsID(typeof(T)).ToList();
            if (!m_AssetIDs.IsNullOrEmpty() && !string.IsNullOrEmpty(m_GroupID))
            {
                for (int i = m_AssetIDs.Count - 1; i >= 0; i--)
                {
                    var assetId = m_AssetIDs[i];
                    var asset = module.ModuleEntry.GetAsset<T>(assetId);
                    string groupID = asset.GroupID;
                    if (!string.IsNullOrEmpty(groupID) && groupID != m_GroupID)
                    {
                        m_AssetIDs.RemoveAt(i);
                    }
                }
            }
            if (!m_AssetIDs.IsNullOrEmpty())
            {
                m_AssetID = m_AssetIDs[0];
            }
            m_AssetIDs.Insert(0, string.Empty);
        }
        protected override void ContentOnGUI()
        {

            GUILayout.Label(UCL_LocalizeManager.Get("SelectAssetTemplateDes", m_TypeLocalizedName), UCL_GUIStyle.LabelStyle);
            using (var scope = new GUILayout.HorizontalScope())
            {
                //TODO 切換Module後要SetGroupID(string.Empty)
                GUILayout.Label("Module", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                string id = m_Module.ID;
                UCL_GUILayout.DrawObjectData(m_Module, new UCL_GUILayout.DrawObjectParams(m_DataDic.GetSubDic("Module"), "Module", true));
                if(m_Module.ID != id)
                {
                    SetGroupID(string.Empty);
                }
            }
            UCL_Module module = UCL_ModuleService.Ins.GetLoadedModule(m_Module.ID);

            using (var scope = new GUILayout.HorizontalScope())
            {
                GUILayout.Label("Group ID", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

                var meta = module.GetAssetMeta(typeof(T).Name);
                var groups = meta.m_Groups.Keys.ToList();
                groups.Insert(0, string.Empty);

                var newGroupID = UCL_GUILayout.PopupAuto(m_GroupID, groups, m_DataDic, "GroupID");
                if (m_GroupID != newGroupID)
                {
                    SetGroupID(newGroupID);
                }
            }
            using (var scope = new GUILayout.HorizontalScope())
            {
                GUILayout.Label("Asset ID", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));


                
                m_AssetID = UCL_GUILayout.PopupAuto(m_AssetID, m_AssetIDs, m_DataDic, "AssetID");
            }
            if(m_Preview != null)
            {
                if(m_Preview.ID != m_AssetID)
                {
                    m_Preview = null;
                }
            }

            if (!string.IsNullOrEmpty(m_AssetID) && m_Preview == null)
            {
                var aAsset = module.ModuleEntry.GetAsset<T>(m_AssetID);
                m_Preview = aAsset;
            }
            GUILayout.Space(20);
            bool createNew = false;
            if (GUILayout.Button(m_CreateDes, UCL_GUIStyle.ButtonStyle))//, GUILayout.ExpandWidth(false)
            {
                createNew = true;
            }
            GUILayout.Space(20);
            //m_Meta.OnGUI(m_DataDic.GetSubDic("Meta"));
            GUILayout.BeginHorizontal();
            //var aModule = UCL_ModuleService.CurEditModule;
            //var aIDs = Util.GetEditableIDs();//aModule.GetFolderPath;

            m_Preview?.Preview(m_DataDic.GetSubDic("Preview"), true);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (createNew)
            {
                Close();

                T asset = m_Preview as T;
                if (asset == null)
                {
                    asset = new T();
                }
                UCL_CommonEditPage.Create(asset);
            }
        }
    }
}

