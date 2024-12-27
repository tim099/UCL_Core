
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 12/27 2024
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
namespace UCL.Core
{
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
    [UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditDataType.UCL_TextAsset)]
    public class UCL_TextAsset : UCL_Asset<UCL_TextAsset>, IDisposable
    {
        public UCL_ModResourcesData m_ModResourcesData = new UCL_ModResourcesData();


        public bool IsEmpty => m_ModResourcesData.IsEmpty;

        private (string key, string text)? m_Cache = null;
        private UCL_Data Data
        {
            get
            {
                //switch (m_DataLoadType)
                //{
                //    case DataLoadType.ModResources:
                //        {
                //            return m_ModResourcesData;
                //        }
                //    case DataLoadType.Addressable:
                //        {
                //            return m_AddressableData;
                //        }
                //}
                //return m_AddressableData;
                return m_ModResourcesData;
            }
        }
        public string Key => Data.Key;
        public string Text
        {
            get
            {
                if (IsEmpty) return string.Empty;

                if(m_Cache != null)
                {
                    if(m_Cache.Value.key != Key)//clear cache
                    {
                        m_Cache = null;
                    }
                }

                if (m_Cache == null)//Try to load text
                {
                    m_Cache = new(Key, m_ModResourcesData.ReadAllText());
                }

                if (m_Cache != null)
                {
                    return m_Cache.Value.text;
                }

                return string.Empty;
            }
        }


        /// <summary>
        /// Preview(OnGUI)
        /// </summary>
        /// <param name="iIsShowEditButton">Show edit button in preview window?</param>
        override public void Preview(UCL.Core.UCL_ObjectDictionary iDataDic, bool iIsShowEditButton = false)
        {
            //GUILayout.BeginHorizontal();
            using (var aScope = new GUILayout.VerticalScope("box", GUILayout.ExpandWidth(false)))
            {

                GUILayout.Label($"{UCL_LocalizeManager.Get("Preview")}({ID})", UCL.Core.UI.UCL_GUIStyle.LabelStyle);
                GUILayout.Label(Text, UCL_GUIStyle.LabelStyle);
                //var aTexture = Texture;
                //if (aTexture != null)
                //{
                //    float size = UCL_GUIStyle.GetScaledSize(64);
                //    GUILayout.Box(aTexture, GUILayout.Width(size), GUILayout.Height(size));
                //}

                if (iIsShowEditButton)
                {
                    ShowEditButtonOnGUI();
                }
            }
            //GUILayout.EndHorizontal();
        }

        public UCL_TextAsset()
        {
            ID = "New Asset";
        }
        //~UCL_SpriteAsset()
        //{
        //    Dispose();
        //}


        public void Dispose()
        {
            Data.Release();
        }
        public void Init(string iPath, string iName)
        {
            m_ModResourcesData.m_FolderPath = iPath;
            m_ModResourcesData.m_FileName = iName;
        }

    }

    [System.Serializable]
    public class UCL_TextAssetEntry : UCL_AssetEntryDefault<UCL_TextAsset>
    {
        public const string DefaultID = "Default";

        public string Text
        {
            get
            {
                if (IsEmpty) return string.Empty;

                try
                {
                    return GetData().Text;
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }

                return string.Empty;
            }
        }

        public UCL_TextAssetEntry() { m_ID = DefaultID; }
        public UCL_TextAssetEntry(string iID) { m_ID = iID; }
    }
}