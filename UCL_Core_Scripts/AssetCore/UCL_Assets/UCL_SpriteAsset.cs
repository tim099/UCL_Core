
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
using UnityEngine;
namespace UCL.Core
{
    public class UCL_SpriteAsset : UCL_Asset<UCL_SpriteAsset>, IDisposable
    {
        public enum DataLoadType
        {
            ModResources = 0,
            Addressable,
        }

        public DataLoadType m_DataLoadType = DataLoadType.ModResources;

        //[UCL.Core.ATTR.AlwaysExpendOnGUI]
        [UCL.Core.PA.Conditional("m_DataLoadType", false, DataLoadType.ModResources)]
        public UCL_ModResourcesData m_ModResourcesData = new UCL_ModResourcesData();

        //[UCL.Core.ATTR.AlwaysExpendOnGUI]
        [UCL.Core.PA.Conditional("m_DataLoadType", false, DataLoadType.Addressable)]
        public UCL_AddressableData m_AddressableData = new UCL_AddressableData();



        private Sprite m_Sprite;
        private bool m_Loading = false;

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
        


        /// <summary>
        /// 預覽
        /// </summary>
        /// <param name="iIsShowEditButton">是否顯示編輯按鈕</param>
        override public void Preview(UCL.Core.UCL_ObjectDictionary iDataDic, bool iIsShowEditButton = false)
        {
            GUILayout.BeginHorizontal();
            using (var aScope = new GUILayout.VerticalScope("box", GUILayout.MinWidth(130)))
            {

                GUILayout.Label($"{UCL_LocalizeManager.Get("Preview")}({ID})", UCL.Core.UI.UCL_GUIStyle.LabelStyle);
                var aTexture = Texture;
                if (aTexture != null)
                {
                    GUILayout.Box(aTexture, GUILayout.Width(64), GUILayout.Height(64));
                }
                else
                {
                    GUILayout.Label($"Texture == null,Key:{Data.Key}");
                }
                //UCL.Core.UI.UCL_GUILayout.LabelAutoSize(LocalizeName);

                if (iIsShowEditButton)
                {
                    if (GUILayout.Button(UCL_LocalizeManager.Get("Edit")))
                    {
                        UCL_CommonEditPage.Create(this);
                    }
                }
            }
            GUILayout.EndHorizontal();
        }

        public Texture2D Texture
        {
            get
            {
                if (IsEmpty) return null;
                if (m_Loading)
                {
                    return null;
                }
                if(m_Sprite == null)//try to load
                {
                    LoadSprite().Forget();
                    return null;
                }

                return m_Sprite.texture;
            }
        }
        public UCL_SpriteAsset()
        {
            ID = "New SpriteAsset";
        }
        public UCL_SpriteAsset(string iID)
        {
            Init(iID);
        }
        ~UCL_SpriteAsset()
        {
            Dispose();
        }

        public async UniTask<Sprite> LoadSprite(CancellationToken iToken = default)
        {
            if (IsEmpty)
            {
                Debug.LogError("UCL_SpriteAsset.LoadSprite() IsEmpty");
                return null;
            }
            if (m_Loading)
            {
                await UniTask.WaitUntil(() => !m_Loading, cancellationToken: iToken);
            }

            try
            {
                if (m_Sprite == null)
                {
                    m_Sprite = await Data.LoadSpriteAsync(iToken);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                m_Loading = false;
            }

            return m_Sprite;
        }

        public void Dispose()
        {
            if (m_Sprite != null)
            {
                Data.Release(m_Sprite);
                //GameObject.Destroy(m_Sprite);
                m_Sprite = null;
            }
        }
        public void Init(DataLoadType iDataLoadType, string iPath, string iName)
        {
            //m_FolderPath = iFolderPath;
            //m_SpriteName = iSpriteName;
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
}


