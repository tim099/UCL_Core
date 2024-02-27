
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/27 2024 10:41
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UnityEngine;
namespace UCL.Core
{
    public class UCL_GameObjectAsset : UCL_Asset<UCL_GameObjectAsset>, IDisposable
    {
        //[UCL.Core.ATTR.AlwaysExpendOnGUI]
        public UCL_AddressableData m_AddressableData = new UCL_AddressableData();


        public bool IsEmpty => m_AddressableData.IsEmpty;


        public async UniTask<GameObject> LoadAsync(CancellationToken iToken)
        {
            return await m_AddressableData.LoadAsync(iToken) as GameObject;
        }

        /// <summary>
        /// Preview(OnGUI)
        /// </summary>
        /// <param name="iIsShowEditButton">Show edit button in preview window?</param>
        override public void Preview(UCL.Core.UCL_ObjectDictionary iDataDic, bool iIsShowEditButton = false)
        {
            using (var aScope = new GUILayout.VerticalScope("box", GUILayout.ExpandWidth(false)))//, GUILayout.MinWidth(130)
            {

                GUILayout.Label($"{UCL_LocalizeManager.Get("Preview")}({ID})", UCL.Core.UI.UCL_GUIStyle.LabelStyle);
                GUILayout.Label(m_AddressableData.Key, UCL.Core.UI.UCL_GUIStyle.LabelStyle);
                //var aTexture = Texture;
                //if (aTexture != null)
                //{
                //    GUILayout.Box(aTexture, GUILayout.Width(64), GUILayout.Height(64));
                //}
                //else
                //{
                //    if(!m_Loading)
                //    {
                //        GUILayout.Label($"Texture == null,Key:{Data.Key}");
                //    }
                //}
                //UCL.Core.UI.UCL_GUILayout.LabelAutoSize(LocalizeName);

                if (iIsShowEditButton)
                {
                    if (GUILayout.Button(UCL_LocalizeManager.Get("Edit"), UCL.Core.UI.UCL_GUIStyle.ButtonStyle))
                    {
                        UCL_CommonEditPage.Create(this);
                    }
                }
            }
        }

        public UCL_GameObjectAsset()
        {
            ID = "New GameObject";
        }
        public UCL_GameObjectAsset(string iID)
        {
            Init(iID);
        }

        public void Dispose()
        {
            m_AddressableData.Release();
        }
        public void Init(string iPath, string iName)
        {
            m_AddressableData.m_AddressablePath = iPath;
            m_AddressableData.m_AddressableKey = iName;
        }

    }

    [System.Serializable]
    public class UCL_GameObjectAssetEntry : UCL_AssetEntryDefault<UCL_GameObjectAsset>
    {
        public const string DefaultID = "Default";
        public UCL_GameObjectAssetEntry() { m_ID = DefaultID; }
        public UCL_GameObjectAssetEntry(string iID) { m_ID = iID; }

    }
}