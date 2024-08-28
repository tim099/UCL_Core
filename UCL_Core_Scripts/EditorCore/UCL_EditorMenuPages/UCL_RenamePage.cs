
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 12/27 2023 10:59
using System.Collections;
using System.Collections.Generic;
using UCL.Core.LocalizeLib;
using UnityEngine;
using UCL.Core.EditorLib.Page;
using UCL.Core.UI;
using UCL.Core;
using UCL.Core.JsonLib;

namespace RCG.Page
{

    public class UCL_RenamePage : UCL_EditorPage
    {
        public class RenameData
        {
            /// <summary>
            /// origin ID
            /// </summary>
            public string m_ID;

            /// <summary>
            /// new ID
            /// </summary>
            public string m_NewID;

            /// <summary>
            /// 對應的Type
            /// </summary>
            public System.Type m_Type;

        }
        #region static
        static public UCL_RenamePage Create(UCLI_Asset iTarget)
        {
            var aPage = new UCL_RenamePage(iTarget);
            UCL.Core.UI.UCL_GUIPageController.CurrentRenderIns.Push(aPage);
            return aPage;
        }
        #endregion



        public override bool IsWindow => true;
        //public override string WindowName => UCL_LocalizeManager.Get("Alert");
        public UCLI_Asset m_Target = null;
        public string m_NewID = string.Empty;
        private UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();
        public UCL_RenamePage()
        {

        }
        public UCL_RenamePage(UCLI_Asset iTarget)
        {
            m_Target = iTarget;
            m_NewID = m_Target.ID;
        }
        protected override void TopBar()
        {
            GUILayout.Label($"{UCL_LocalizeManager.Get("Rename")} : {m_Target.ID}", UCL.Core.UI.UCL_GUIStyle.LabelStyle);
            //base.TopBar();
        }
        //protected override void TopBarButtons()
        //{
        //    GUILayout.Label($"{UCL_LocalizeManager.Get("Rename")} : {m_Target.ID}", UCL.Core.UI.UCL_GUIStyle.LabelStyle);

        //}
        protected override void ContentOnGUI()
        {
            using (var aScope = new GUILayout.HorizontalScope())
            {
                GUILayout.Label("New ID", UCL.Core.UI.UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                m_NewID = GUILayout.TextField(m_NewID);
            }

            using (var aScope = new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button(UCL_LocalizeManager.Get("Rename"), UCL_GUIStyle.GetButtonStyle(Color.white, 22), GUILayout.ExpandWidth(false)))
                {
                    var aData = new RenameData();
                    aData.m_ID = m_Target.ID;
                    aData.m_NewID = m_NewID;
                    aData.m_Type = m_Target.GetType();

                    string groupID = m_Target.GroupID;
                    string aOldID = m_Target.ID;
                    //進行重新命名
                    m_Target.ID = m_NewID;
                    m_Target.GroupID = groupID;
                    m_Target.Save();

                    UCLI_AssetEntry.s_DeserializeAction = (iGenData) =>
                    {
                        if (aData.m_ID == iGenData.ID && aData.m_Type == iGenData.AssetType)//this is the rename type!!
                        {
                            //Rename!!
                            iGenData.ID = aData.m_NewID;
                        }
                    };
                    UCLI_Asset.RefreshAllAssetsWithReflection();
                    //RCGI_CommonData.RefreshAllCommonDatasWithReflection();

                    UCLI_AssetEntry.s_DeserializeAction = null;


                    m_Target.Delete(aOldID);


                    BackButtonClicked();
                }
                if (GUILayout.Button(UCL.Core.LocalizeLib.UCL_LocalizeManager.Get("Cancel"), UCL_GUIStyle.GetButtonStyle(Color.white, 22), GUILayout.ExpandWidth(false)))
                {
                    BackButtonClicked();
                }
            }

        }
    }
}
