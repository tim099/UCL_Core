
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 01/10 2025
using Cysharp.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UCL.Core;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Config)]
    [UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditConfigType.UCL_OnModuleLoadedAsset)]
    public class UCL_OnModuleLoadedAsset : UCL_Asset<UCL_OnModuleLoadedAsset>
    {
        #region must override 一定要override的部份
        /// <summary>
        /// 預覽
        /// </summary>
        /// <param name="iIsShowEditButton">是否顯示編輯按鈕</param>
        override public void Preview(UCL.Core.UCL_ObjectDictionary iDataDic, bool iIsShowEditButton = false)
        {
            GUILayout.BeginHorizontal();
            using (var aScope = new GUILayout.VerticalScope("box"))//, GUILayout.MinWidth(130)
            {
                GUILayout.Label($"{UCL_LocalizeManager.Get("Preview")}({ID})", UCL.Core.UI.UCL_GUIStyle.LabelStyle);

                if (iIsShowEditButton)
                {
                    ShowEditButtonOnGUI();
                }
            }
            GUILayout.EndHorizontal();
        }

        public UCL_OnModuleLoadedAsset()
        {
            ID = "new asset";
        }
        public UCL_OnModuleLoadedAsset(string iID)
        {
            Init(iID);
        }
        #endregion

        /// <summary>
        /// 排序讀取順序用
        /// Read Order
        /// </summary>
        public int m_Order = 0;

        /// <summary>
        /// 讀取模組後要觸發的Actions
        /// Actions to Trigger After Loading the Module
        /// </summary>
        public List<UCLI_OnModuleLoaded> m_Actions = new();

        public async UniTask OnModuleLoaded(CancellationToken token)
        {
            if (m_Actions.IsNullOrEmpty())
            {
                return;
            }
            foreach(var action in m_Actions)
            {
                try
                {
                    await action.OnModuleLoaded(token);
                }
                catch (System.OperationCanceledException) { }
                catch(System.Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

    }

    public class UCL_OnModuleLoadedEntry : UCL_AssetEntryDefault<UCL_OnModuleLoadedAsset>
    {
        public const string DefaultID = "Default";
        public static UCL_LanguageCodeEntry s_DefaultLang = new UCL_LanguageCodeEntry(DefaultID);
        public UCL_OnModuleLoadedEntry() { ID = DefaultID; }
        public UCL_OnModuleLoadedEntry(string iID) { ID = iID; }

    }



}
