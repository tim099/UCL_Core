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
    public class UCL_SelectAssetPage<T> : UCL_EditorPage where T : class, UCLI_Asset, new()
    {
        static public UCL_SelectAssetPage<T> Create()
        {
            var aPage = new UCL_SelectAssetPage<T>();
            UCL_GUIPageController.CurrentRenderIns.Push(aPage);

            return aPage;
        }
        public const int MaxAssetPerPage = 20;

        protected UCL.Core.UCL_ObjectDictionary m_DataDic = new UCL.Core.UCL_ObjectDictionary();
        protected UCLI_Preview m_Preview = null;
        protected string m_PreviewID = string.Empty;
        protected string m_CreateDes = string.Empty;
        protected string m_TypeName = string.Empty;
        //protected int m_CurPage = 0;


        protected UCL_AssetCommonMeta m_Meta = null;
        protected UCL_Asset<T> m_Util = default;
        public override string WindowName => $"UCL_SelectAssetPage";//({m_TypeName})
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
                m_CreateDes = UCL_LocalizeManager.Get("CreateNew");
            }
            Util.OnEdit();

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
            //m_Preview = null;
            if(!string.IsNullOrEmpty(m_PreviewID))
            {
                m_Preview = Util.CreateData(m_PreviewID);
            }
            else
            {
                m_Preview = null;
            }
            
            Util.ClearAllCache();
            m_Meta = Util.AssetMetaIns;
            //Debug.LogError($"OnResume m_Meta:{m_Meta.m_FileMetas.ConcatString(iMeta => $"{iMeta.Key}:{iMeta.Value.m_Group}")}");
        }
        public override void OnClose()
        {
            base.OnClose();
            try
            {
                m_Meta.Save();
            }
            catch(System.Exception ex)
            {
                Debug.LogException(ex);
                Debug.LogError($"{GetType().FullName}.OnClose, Exception:{ex}");
            }
            
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
                UCL_CreateAssetPage<T>.Create();
                //UCL_CommonEditPage.Create(new T());
            }
#if UNITY_EDITOR
            if (GUILayout.Button(UCL_LocalizeManager.Get("RefreshData"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                Util.RefreshAllDatas();
            }
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
                GUILayout.Label($"[{UCL_ModuleService.CurEditModuleID}] {m_TypeName}", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
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
        static public void DrawSelectTargetList(
            UCLI_Asset iUtil,
            IList<string> iIDs, UCL.Core.UCL_ObjectDictionary iDic,
            System.Action<string> iEditAct, System.Action<string> iPreviewAct, System.Action<string> iDeleteAct,
            UCL_AssetCommonMeta iMeta = null)
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
                float scale = UCL_GUIStyle.CurStyleData.Scale;
                int aVerticalScopeWidth = Mathf.RoundToInt(scale * 380);

                int EditGroupWidth = Mathf.RoundToInt(scale * 150);
                bool editGroup = false;
                bool showDeleteButton = false;
                if (iMeta != null)
                {
                    editGroup = iMeta.m_EditGroup;
                    showDeleteButton = iMeta.ShowDeleteButton;
                }
                if (editGroup)
                {
                    aVerticalScopeWidth += (EditGroupWidth + Mathf.RoundToInt(scale * 10));
                }
                int buttonWidth = Mathf.RoundToInt(60 * scale);
                if (showDeleteButton)
                {
                    aVerticalScopeWidth += buttonWidth;
                }

                int aScrollWidth = aVerticalScopeWidth;// + Mathf.RoundToInt(scale * 50);

                if (iMeta != null)
                {
                    iIDs = iMeta.GetAllShowData(iUtil, iIDs);
                }
                if (aRegex != null)//根據輸入 過濾顯示的目標 Filter targets
                {
                    iIDs = iIDs.Where(iID => aRegex.IsMatch(iID.ToLower())).ToList();
                }
                int pageCount = 1;
                if (iIDs.Count > MaxAssetPerPage)
                {
                    pageCount = 1 + ((iIDs.Count - 1) / MaxAssetPerPage);
                }
                const int FontSize = 14;

                if (pageCount > 1)
                {
                    int curPage = iDic.GetData("CurPage", 0);
                    if (curPage >= pageCount)
                    {
                        curPage = pageCount - 1;
                    }

                    using (var scope = new GUILayout.HorizontalScope("box", GUILayout.Width(aScrollWidth)))
                    {
                        int state = 0;
                        int startIndex = curPage * MaxAssetPerPage;
                        int lastIndex = startIndex + MaxAssetPerPage;
                        if (lastIndex > iIDs.Count)
                        {
                            lastIndex = iIDs.Count;
                        }

                        float space = UCL_GUIStyle.GetScaledSize(10);

                        if (GUILayout.Button("<", UCL_GUIStyle.GetButtonStyle(Color.white, FontSize), GUILayout.ExpandWidth(false)))
                        {
                            state = -2;//first page
                        }
                        GUILayout.Space(space);
                        if (GUILayout.Button(UCL_LocalizeManager.Get("PrevPage"),
                            UCL_GUIStyle.GetButtonStyle(Color.white, FontSize), GUILayout.ExpandWidth(false)))
                        {
                            state = -1;//prev page
                        }
                        //GUILayout.Space(space);

                        GUILayout.Box($"{(curPage + 1)} / {pageCount}",
                            UCL_GUIStyle.BoxStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50)));//, GUILayout.ExpandWidth(false)
                        //GUILayout.Space(space);
                        if (GUILayout.Button(UCL_LocalizeManager.Get("NextPage"),
                            UCL_GUIStyle.GetButtonStyle(Color.white, FontSize), GUILayout.ExpandWidth(false)))
                        {
                            state = 1;//next page
                        }
                        GUILayout.Space(space);
                        if (GUILayout.Button(">", UCL_GUIStyle.GetButtonStyle(Color.white, FontSize), GUILayout.ExpandWidth(false)))
                        {
                            state = 2;//lase page
                        }
                        GUILayout.Space(space);
                        GUILayout.Label($"[{startIndex + 1} ~ {lastIndex}]", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

                        GUILayout.FlexibleSpace();

                        using (var scope2 = new GUILayout.HorizontalScope(GUILayout.Width(UCL_GUIStyle.GetScaledSize(100))))
                        {
                            //GUILayout.Label(UCL_LocalizeManager.Get("PageID"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            curPage = UCL_GUILayout.IntField(curPage + 1) - 1;
                            curPage = Mathf.Clamp(curPage, 0, pageCount - 1);
                        }

                        if (state != 0)
                        {
                            switch (state)
                            {
                                case -1:
                                    {
                                        if (curPage <= 0)
                                        {
                                            curPage = pageCount - 1;
                                        }
                                        else
                                        {
                                            curPage--;
                                        }
                                        break;
                                    }
                                case 1:
                                    {
                                        if (curPage >= pageCount - 1)
                                        {
                                            curPage = 0;
                                        }
                                        else
                                        {
                                            curPage++;
                                        }
                                        break;
                                    }
                                case -2:
                                    {
                                        curPage = 0;
                                        break;
                                    }
                                case 2:
                                    {
                                        curPage = pageCount - 1;
                                        break;
                                    }
                            }
                        }

                        iDic.SetData("CurPage", curPage);
                        iIDs = iIDs.ToList().SubList(startIndex, MaxAssetPerPage);
                    }

                }


                //GUILayout.BeginHorizontal();
                iDic.SetData("ScrollPos", GUILayout.BeginScrollView(iDic.GetData("ScrollPos", Vector2.zero), GUILayout.Width(aScrollWidth)));
                
                //using (var aScope = new GUILayout.VerticalScope("box", GUILayout.Width(aVerticalScopeWidth)))//
                {

                    string previewID = iDic.GetData("PreviewID", string.Empty);
                    for (int i = 0; i < iIDs.Count; i++)
                    {
                        string aID = iIDs[i];
                        //if (aRegex != null && !aRegex.IsMatch(aID.ToLower()))//根據輸入 過濾顯示的目標 Filter targets
                        //{
                        //    continue;
                        //}
                        var heightStyle = GUILayout.Height(35 * scale);
                        GUILayout.BeginHorizontal();
                        using (var aScope2 = new GUILayout.HorizontalScope("box", heightStyle))
                        {
                            string aDisplayName = UCL_LocalizeManager.GetID(aID);
                            

                            if (aRegex != null)//標記符合搜尋條件的部分
                            {
                                aDisplayName = aRegex.HightLight(aDisplayName, aSearchName, Color.red);
                            }
                            
                            if (showDeleteButton)
                            {
                                if (GUILayout.Button(UCL_LocalizeManager.Get("Delete"),
                                    UCL_GUIStyle.GetButtonStyle(Color.red, FontSize), heightStyle, GUILayout.Width(buttonWidth)))
                                {
                                    Page.UCL_OptionPage.ConfirmDelete(aID, () => iDeleteAct(aID));
                                }
                            }


                            GUILayout.Box(aDisplayName, UCL.Core.UI.UCL_GUIStyle.BoxStyle, heightStyle, GUILayout.Width(210 * scale));
                            if (GUILayout.Button(UCL_LocalizeManager.Get("Edit"), 
                                UCL_GUIStyle.GetButtonStyle(Color.white, FontSize), heightStyle, GUILayout.Width(buttonWidth)))
                            {
                                iEditAct(aID);
                                //RCG_EditItemPage.Create(RCG_ItemData.GetItemData(aID));
                            }
                            if (GUILayout.Button(UCL_LocalizeManager.Get("Preview"), 
                                UCL_GUIStyle.GetButtonStyle((previewID == aID)?Color.yellow : Color.white, FontSize)
                                , heightStyle, GUILayout.Width(buttonWidth)))
                            {
                                iDic.SetData("PreviewID", aID);
                                iPreviewAct(aID);
                            }
                            if (editGroup)//編輯分組
                            {
                                using (var aScope3 = new GUILayout.HorizontalScope("box", GUILayout.MinWidth(EditGroupWidth)))
                                {
                                    iMeta.OnGUI_ShowData(iUtil, aID, iDic.GetSubDic(aID), EditGroupWidth - Mathf.RoundToInt(scale * 5));
                                }
                            }
                            //GUILayout.Space(scale * 20);
                            //GUILayout.FlexibleSpace();
                        }

                        //GUILayout.FlexibleSpace();
                        //GUILayout.Space(scale * 10);
                        GUILayout.EndHorizontal();
                    }
                }
                if (iMeta != null)
                {
                    iMeta.OnGUIEnd();
                }

                GUILayout.EndScrollView();


                //GUILayout.EndHorizontal();
            }
        }
        virtual protected void DrawSelectTargets()
        {
            GUILayout.BeginHorizontal();
            var aModule = UCL_ModuleService.CurEditModule;
            var aIDs = Util.GetEditableIDs();//aModule.GetFolderPath;

            DrawSelectTargetList(Util,
                aIDs, m_DataDic.GetSubDic("SelectTarget"),
                (iID) => {
                    UCL_CommonEditPage.Create(Util.GetData(iID));
                },
                (iID) => {
                    m_PreviewID = iID;
                    m_Preview = Util.CreateData(iID);
                },
                (iID) => {
                    Util.Delete(iID);
                }, m_Meta);

            if (m_Preview != null)
            {
                var scrollPos = GUILayout.BeginScrollView(m_DataDic.GetData("ScrollPosPreview", Vector2.zero),
                    GUILayout.MinWidth(UCL_GUIStyle.GetScaledSize(310)));
                m_DataDic.SetData("ScrollPosPreview", scrollPos);

                //, GUILayout.MinWidth(UCL_GUIStyle.GetScaledSize(220))
                m_Preview?.Preview(m_DataDic.GetSubDic("Preview"), true);
                GUILayout.EndScrollView();
            }


            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
    }
}
