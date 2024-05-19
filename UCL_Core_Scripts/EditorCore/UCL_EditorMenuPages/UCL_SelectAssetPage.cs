using System.Collections;
using System.Collections.Generic;
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
    public class UCL_SelectAssetPage<T> : UCL_EditorPage where T : class, UCLI_Asset, new()
    {
        static public UCL_SelectAssetPage<T> Create()
        {
            var aPage = new UCL_SelectAssetPage<T>();
            UCL_GUIPageController.CurrentRenderIns.Push(aPage);

            return aPage;
        }

        protected UCL.Core.UCL_ObjectDictionary m_DataDic = new UCL.Core.UCL_ObjectDictionary();
        protected UCLI_Preview m_Preview = null;
        protected string m_CreateDes = string.Empty;
        protected string m_TypeName = string.Empty;
        protected UCL_AssetMeta m_Meta = null;
        protected UCL_Asset<T> m_Util = default;
        public override string WindowName => $"UCL_SelectAssetPage({m_TypeName})";
        public override bool IsWindow => true;
        public override void Init(UCL.Core.UI.UCL_GUIPageController iGUIPageController)
        {


            base.Init(iGUIPageController);
            string aTypeName = typeof(T).Name;
            m_TypeName = aTypeName;
            string aKey = $"Create_{aTypeName}";
            if (UCL_LocalizeManager.ContainsKey(aKey))
            {
                m_CreateDes = UCL_LocalizeManager.Get(aKey);
            }
            else
            {
                m_CreateDes = UCL_LocalizeManager.Get("Create New");
            }
            m_Meta = Util.AssetMetaIns;
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

        }//RCG_CommonData<T>.Util as RCG_CommonData<T>;
        public override void OnResume()
        {
            m_Preview = null;
            Util.ClearAllCache();
            m_Meta = Util.AssetMetaIns;
            //Debug.LogError($"OnResume m_Meta:{m_Meta.m_FileMetas.ConcatString(iMeta => $"{iMeta.Key}:{iMeta.Value.m_Group}")}");
        }
        public override void OnClose()
        {
            base.OnClose();
            m_Meta.Save();
        }
        protected override void ContentOnGUI()
        {
            m_Meta.OnGUI(m_DataDic.GetSubDic("Meta"));
            DrawSelectTargets();
        }
        protected override void TopBarButtons()
        {
            GUILayout.Label(WindowName, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
            if (GUILayout.Button(m_CreateDes, UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                UCL_CommonEditPage.Create(new T());
            }
#if UNITY_EDITOR
            if (GUILayout.Button(UCL_LocalizeManager.Get("RefreshData"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                Util.RefreshAllDatas();
            }
            //if (GUILayout.Button(UCL_LocalizeManager.Get("RemoveMetas"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            //{
            //    var aPath = Util.StreamingAssetFolderPath;
            //    var aFiles = UCL.Core.FileLib.Lib.GetFiles(aPath, "*.meta");
            //    foreach (var aFile in aFiles)
            //    {
            //        System.IO.File.Delete(aFile);
            //    }
            //}
#endif
#if UNITY_STANDALONE_WIN
            if (GUILayout.Button(UCL_LocalizeManager.Get("OpenFolder"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                UCL.Core.FileLib.WindowsLib.OpenExplorer(Util.SaveFolderPath);
            }
#endif
            //using (new GUILayout.HorizontalScope("box"))
            {
                //GUILayout.Label("Type:", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                //GUILayout.Space(10);
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
        /// <summary>
        /// 繪製選取編輯目標的列表(目前 裝備 道具都用這個繪製)
        /// </summary>
        /// <param name="iIDs2">所有目標ID</param>
        /// <param name="iDic">緩存</param>
        /// <param name="iEditAct">點下編輯時呼叫</param>
        /// <param name="iPreviewAct">點下預覽時呼叫</param>
        /// <param name="iDeleteAct">點下刪除時呼叫</param>
        /// <param name="iFontSize"></param>
        static public void DrawSelectTargetList(IList<string> iIDs, UCL.Core.UCL_ObjectDictionary iDic,
            System.Action<string> iEditAct, System.Action<string> iPreviewAct, System.Action<string> iDeleteAct,
            UCL_AssetMeta iMeta = null,
            int iFontSize = 20)
        {
            using(var aScopeV = new GUILayout.VerticalScope("box"))
            {
                Regex aRegex = null;
                string aSearchName = UCL.Core.UI.UCL_GUILayout.TextField(UCL_LocalizeManager.Get("Search"), iDic, "SearchName");
                if (!string.IsNullOrEmpty(aSearchName))
                {
                    try
                    {
                        aRegex = new Regex(aSearchName.ToLower(), RegexOptions.Compiled);
                    }
                    catch (System.Exception iE)
                    {
                        Debug.LogException(iE);
                    }
                }
                float aScale = UCL_GUIStyle.CurStyleData.Scale;
                int aVerticalScopeWidth = Mathf.RoundToInt(aScale * 450);

                int EditGroupWidth = Mathf.RoundToInt(aScale * 150);
                bool aIsEditGroup = false;
                if (iMeta != null)
                {
                    aIsEditGroup = iMeta.m_EditGroup;
                }
                if (aIsEditGroup)
                {
                    aVerticalScopeWidth += (EditGroupWidth + Mathf.RoundToInt(aScale * 10));
                }
                int aScrollWidth = aVerticalScopeWidth + Mathf.RoundToInt(aScale * 40);
                GUILayout.BeginHorizontal();
                iDic.SetData("ScrollPos", GUILayout.BeginScrollView(iDic.GetData("ScrollPos", Vector2.zero), GUILayout.MinWidth(aScrollWidth)));

                using (var aScope = new GUILayout.VerticalScope("box"))//, GUILayout.MinWidth(aVerticalScopeWidth)
                {
                    if (iMeta != null)
                    {
                        iIDs = iMeta.GetAllShowData(iIDs);
                    }

                    for (int i = 0; i < iIDs.Count; i++)
                    {
                        string aID = iIDs[i];
                        //if(iMeta != null && !iMeta.CheckShowData(aID))
                        //{
                        //    continue;
                        //}
                        if (aRegex != null && !aRegex.IsMatch(aID.ToLower()))//根據輸入 過濾顯示的目標 Filter targets
                        {
                            continue;
                        }
                        GUILayout.BeginHorizontal();
                        using (var aScope2 = new GUILayout.HorizontalScope("box"))
                        {
                            string aDisplayName = aID;
                            if (aRegex != null)//標記符合搜尋條件的部分
                            {
                                aDisplayName = aRegex.HightLight(aDisplayName, aSearchName, Color.red);
                            }

                            if (UCL.Core.UI.UCL_GUILayout.ButtonAutoSize(UCL_LocalizeManager.Get("Delete"), iFontSize, Color.red, Color.white))
                            {
                                Page.UCL_OptionPage.ConfirmDelete(aID, () => iDeleteAct(aID));
                            }

                            GUILayout.Box(aDisplayName, UCL.Core.UI.UCL_GUIStyle.BoxStyle, GUILayout.MinWidth(160), GUILayout.MaxWidth(250));
                            if (UCL.Core.UI.UCL_GUILayout.ButtonAutoSize(UCL_LocalizeManager.Get("Edit"), iFontSize))
                            {
                                iEditAct(aID);
                                //RCG_EditItemPage.Create(RCG_ItemData.GetItemData(aID));
                            }
                            if (UCL.Core.UI.UCL_GUILayout.ButtonAutoSize(UCL_LocalizeManager.Get("Preview"), iFontSize))
                            {
                                iPreviewAct(aID);
                            }

                        }
                        if (aIsEditGroup)
                        {
                            using (var aScope2 = new GUILayout.HorizontalScope("box", GUILayout.MinWidth(EditGroupWidth)))
                            {
                                iMeta.OnGUI_ShowData(aID, iDic.GetSubDic(aID), EditGroupWidth - Mathf.RoundToInt(aScale * 5));
                            }
                        }
                        GUILayout.EndHorizontal();
                    }
                }
                if (iMeta != null)
                {
                    iMeta.OnGUIEnd();
                }
                GUILayout.EndScrollView();


                GUILayout.EndHorizontal();
            }
        }
        virtual protected void DrawSelectTargets()
        {
            GUILayout.BeginHorizontal();
            var aModule = UCL_ModuleService.CurEditModule;
            var aIDs = Util.GetEditableIDs();//aModule.GetFolderPath;

            DrawSelectTargetList(aIDs, m_DataDic.GetSubDic("SelectTarget"),
                (iID) => {
                    UCL_CommonEditPage.Create(Util.GetData(iID));
                },
                (iID) => {
                    m_Preview = Util.CreateData(iID);
                },
                (iID) => {
                    Util.Delete(iID);
                }, m_Meta);
            m_Preview?.Preview(m_DataDic.GetSubDic("Preview"), true);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
    }
}
