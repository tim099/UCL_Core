
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/24 2024 09:40
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
namespace UCL.Core
{
    public interface UCLI_LoadTextureConfig
    {
        public FilterMode FilterMode { get; }
    }


    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
    [UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditDataType.UCL_SpriteAsset)]
    public class UCL_SpriteAsset : UCL_Asset<UCL_SpriteAsset>, IDisposable, UCLI_LoadTextureConfig
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


        [UCL.Core.PA.Conditional(nameof(m_DataLoadType), false, DataLoadType.ModResources)]
        public FilterMode m_FilterMode = FilterMode.Bilinear;


        public FilterMode FilterMode => m_FilterMode;
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

        public async UniTask<Sprite> GetSpriteAsync(CancellationToken iToken)
        {
            await Data.LoadAsync(iToken);
            return Data.GetSprite();
        }
        public async UniTask<Texture2D> GetTextureAsync(CancellationToken iToken)
        {
            return await Data.LoadTextureAsync(iToken, this);
            //await Data.LoadAsync(iToken);
            //iToken.ThrowIfCancellationRequested();
            //return Data.GetSprite().texture;
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
                if (iIsShowEditButton)
                {
                    ShowEditButtonOnGUI();
                }

                var aTexture = Texture;
                if (aTexture != null)
                {
                    float size = UCL_GUIStyle.GetScaledSize(128);
                    GUILayout.Box(aTexture, GUILayout.Width(size), GUILayout.Height(size));
                }
            }
            //GUILayout.EndHorizontal();
        }
        public Sprite Sprite
        {
            get
            {
                if (IsEmpty) return null;

                return Data.GetSprite();
            }
        }

        public Texture2D Texture
        {
            get
            {
                var aSprite = Sprite;
                if (aSprite == null) return null;

                return aSprite.texture;
            }
        }
        public UCL_SpriteAsset()
        {
            ID = "New SpriteAsset";
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
    public class UCL_SpriteAssetEntry : UCL_AssetEntryDefault<UCL_SpriteAsset>
    {
        public const string DefaultID = "Default";
        public UCL_SpriteAssetEntry() { m_ID = DefaultID; }
        public UCL_SpriteAssetEntry(string iID) { m_ID = iID; }

        public override bool IsEmpty
        {
            get
            {
                if (!Util.ContainsAsset(ID))//Asset not exist!!
                {
                    return true;
                }

                try
                {
                    var data = GetData();
                    if (data == null) return true;
                    return data.IsEmpty;
                }
                catch (Exception)
                {

                }

                return true;//Exception
            }
        }

        public Texture2D Texture
        {
            get
            {
                var aData = GetData();
                if(aData == null)
                {
                    Debug.LogError($"UCL_SpriteAssetEntry m_ID:{m_ID},aData == null");
                    return null;
                }
                var aTexture = aData.Texture;
                if(aTexture == null)
                {
                    Debug.LogError($"UCL_SpriteAssetEntry m_ID:{m_ID},aTexture == null");
                }
                return aTexture;
            }
        }
        public Sprite Sprite
        {
            get
            {
                var aData = GetData();
                if (aData == null)
                {
                    return null;
                }
                return aData.Sprite;
            }
        }

        public void SetImage(Image iImage)
        {
            if (IsEmpty)
            {
                iImage.sprite = null;
                return;
            }
            iImage.sprite = Sprite;
            //iImage.preserveAspect = IsPreserveAspect;
        }
        public async UniTask SetImageAsync(Image iImage, CancellationToken iToken)
        {
            if (IsEmpty)
            {
                iImage.sprite = null;
                return;
            }
            var aSprite = await GetData().GetSpriteAsync(iToken);
            iToken.ThrowIfCancellationRequested();
            if (iImage == null)//Image Destroyed
            {
                return;
            }
            iImage.sprite = aSprite;
        }
    }
}


