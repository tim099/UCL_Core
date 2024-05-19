
// ATS_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 02/20 2024 18:29
using System;
using System.Collections;
using System.Collections.Generic;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.StringExtensionMethods;
using UnityEngine;
using UCL.Core.EditorLib.Page;
using System.IO.Compression;
using UCL.Core.UI;
using UCL.Core;
using UCL.Core.Page;

namespace UCL.Core.Page
{
    /// <summary>
    /// Common edit page of UCLI_CommonEditable
    /// </summary>
    public class UCL_CommonEditPage : UCL_EditorPage
    {
        static public UCL_CommonEditPage Create(UCLI_CommonEditable iData)
        {
            if(iData == null)
            {
                Debug.LogError("CommonEditPage Create() iData == null");
                return null;
            }
            //var aPage = new RCG_CommonEditPage(iData);
            var aPage = new UCL_CommonEditPage(iData.CloneInstance());
            UCL.Core.UI.UCL_GUIPageController.CurrentRenderIns.Push(aPage);
            return aPage;
        }
        public UCL_CommonEditPage() { }
        public UCL_CommonEditPage(UCLI_CommonEditable iData)
        {
            m_Data = iData;
            m_WindowName = UCL_LocalizeManager.Get(iData.GetType().Name.Replace("RCG_", string.Empty).Replace("Data", string.Empty) + "Editor");
            UpdateInitJson();
        }
        override public string WindowName => m_WindowName;
        protected override bool ShowCloseButton => false;
        UCLI_CommonEditable m_Data = null;
        string m_WindowName = string.Empty;
        /// <summary>
        /// Init value of m_Data.SerializeToJson()(Refresh when save data)
        /// Show "SaveBeforeExit?" popup if data modified when exit this page
        /// </summary>
        string m_InitJson = string.Empty;
        protected UCL.Core.UCL_ObjectDictionary m_DataDic = new UCL.Core.UCL_ObjectDictionary();
        void UpdateInitJson()
        {
            m_InitJson = m_Data.SerializeToJson().ToJson();
        }
        bool IsModified()
        {
            string aCurJson = m_Data.SerializeToJson().ToJson();
            return (aCurJson != m_InitJson);
        }
        public override void OnClose()
        {
            m_Data.ClearAllCache();
            base.OnClose();
        }
        protected override void BackButtonClicked()
        {
            //string aCurJson = m_Data.SerializeToJson().ToJson();
            if (!IsModified())//Not Modified
            {
                base.BackButtonClicked();
            }
            else
            {
                //Debug.LogError("m_InitJson:" + m_InitJson+ ",aCurJson:"+ aCurJson);
                UCL_OptionPage.Create(UCL_LocalizeManager.Get("SaveBeforeExit?", m_Data.ID), "",

                    new ButtonData(UCL_LocalizeManager.Get("ExitWitoutSave"), () =>
                    {
                        base.BackButtonClicked();
                    },UCL.Core.UI.UCL_GUIStyle.GetButtonStyle(Color.red)),
                    new ButtonData(UCL_LocalizeManager.Get("Save"), () =>
                    {
                        m_Data.Save();
                        base.BackButtonClicked();
                    }),
                    new ButtonData(UCL_LocalizeManager.Get("Cancel"))

                    );
            }
            
        }
        protected override void TopBarButtons()
        {
            m_Data.ID = UCL.Core.UI.UCL_GUILayout.TextField(UCL_LocalizeManager.Get("ID"), m_Data.ID, 260);
            if (GUILayout.Button(UCL_LocalizeManager.Get("Save"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                //var aJson = m_Data.Save();
                //m_InitJson = aJson.ToJson();
                m_Data.Save();
                UpdateInitJson();
            }
            if (GUILayout.Button(UCL_LocalizeManager.Get("Copy"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                var aData = m_Data.SerializeToJson();
                string aSaveData = aData.ToJsonBeautify();
                aSaveData.CopyToClipboard();
            }
            if (!string.IsNullOrEmpty(GUIUtility.systemCopyBuffer))
            {
                if (GUILayout.Button(UCL_LocalizeManager.Get("Paste"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    try
                    {
                        JsonData aJson = JsonData.ParseJson(GUIUtility.systemCopyBuffer);
                        m_Data.DeserializeFromJson(aJson);
                        m_DataDic.Clear();
                    }
                    catch (System.Exception iE)
                    {
                        Debug.LogException(iE);
                    }
                }
            }
            GUILayout.Space(10);
            var aType = m_Data.GetType();
            GUILayout.Label(aType.Name, UCL_GUIStyle.LabelStyle);

            if (GUILayout.Button(UCL_LocalizeManager.Get("Copy"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                aType.Name.CopyToClipboard();
            }
            //if (GUILayout.Button(UCL_LocalizeManager.Get("Rename"), GUILayout.ExpandWidth(false)))
            //{
            //    RCG_RenamePage.Create(m_Data);//開啟重新命名分頁
            //}
            //if (GUILayout.Button(UCL_LocalizeManager.Get("FindReference"), GUILayout.ExpandWidth(false)))
            //{
            //    RCG_FindReferencePage.Create(m_Data);//開啟尋找連接分頁
            //}
#if !UNITY_STANDALONE_WIN
            if (GUILayout.Button(UCL_LocalizeManager.Get("OpenFile"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                UCL.Core.FileLib.WindowsLib.OpenExplorer(m_Data.AssetPath);
            }
#endif
        }
        protected override void ContentOnGUI()
        {
            if (m_Data == null)
            {
                GUILayout.Box("m_Data == null");
                return;
            }
            m_Data.OnGUI(m_DataDic);
        }
    }
}