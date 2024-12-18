
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 12/18 2024
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
    [UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditDataType.UCL_VideoAsset)]
    public class UCL_VideoAsset : UCL_Asset<UCL_VideoAsset>, IDisposable
    {
        public enum DataLoadType
        {
            ModResources = 0,
            Addressable,
        }


        public DataLoadType m_DataLoadType = DataLoadType.ModResources;

        //[UCL.Core.ATTR.AlwaysExpendOnGUI]
        [UCL.Core.PA.Conditional(nameof(m_DataLoadType), false, DataLoadType.ModResources)]
        public UCL_ModResourcesData m_ModResourcesData = new UCL_ModResourcesData();

        //[UCL.Core.ATTR.AlwaysExpendOnGUI]
        [UCL.Core.PA.Conditional(nameof(m_DataLoadType), false, DataLoadType.Addressable)]
        public UCL_AddressableData m_AddressableData = new UCL_AddressableData();

        public float m_PlaybackSpeed = 1f;

        public bool IsEmpty => Data.IsEmpty;

        private UCL_Data Data
        {
            get
            {
                switch (m_DataLoadType)
                {
                    case DataLoadType.ModResources:
                        {
                            return m_ModResourcesData;
                        }
                    case DataLoadType.Addressable:
                        {
                            return m_AddressableData;
                        }
                }
                return m_AddressableData;
            }
        }
        public async UniTask PlayVideo(UnityEngine.Video.VideoPlayer videoPlayer, CancellationToken token)
        {
            if (IsEmpty)
            {
                return;
            }
            switch (m_DataLoadType)
            {
                case DataLoadType.ModResources:
                    {
                        videoPlayer.url = m_ModResourcesData.UnityWebrequestURL;
                        break;
                    }
                case DataLoadType.Addressable:
                    {
                        var clip = await m_AddressableData.LoadObjectAsync<VideoClip>(token);
                        token.ThrowIfCancellationRequested();
                        if (clip != null)
                        {
                            videoPlayer.clip = clip;
                        }
                        break;
                    }
            }
            videoPlayer.playbackSpeed = m_PlaybackSpeed;
            videoPlayer.Play();
        }
        //public async UniTask<VideoSource> GetVideoSourceAsync(CancellationToken iToken)
        //{
        //    await Data.LoadAsync(iToken);
        //    return Data.GetSprite();
        //}

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

        public UCL_VideoAsset()
        {
            ID = "New VideoAsset";
        }
        //~UCL_SpriteAsset()
        //{
        //    Dispose();
        //}


        public void Dispose()
        {
            Data.Release();
        }
        public void Init(DataLoadType iDataLoadType, string iPath, string iName)
        {
            m_DataLoadType = iDataLoadType;
            switch (m_DataLoadType)
            {
                case DataLoadType.ModResources:
                    {
                        m_ModResourcesData.m_FolderPath = iPath;
                        m_ModResourcesData.m_FileName = iName;
                        break;
                    }
                case DataLoadType.Addressable:
                    {
                        m_AddressableData.m_AddressablePath = iPath;
                        m_AddressableData.m_AddressableKey = iName;
                        break;
                    }
            }
        }

    }

    [System.Serializable]
    public class UCL_VideoAssetEntry : UCL_AssetEntryDefault<UCL_VideoAsset>
    {
        public const string DefaultID = "Default";
        public UCL_VideoAssetEntry() { m_ID = DefaultID; }
        public UCL_VideoAssetEntry(string iID) { m_ID = iID; }
    }
}
