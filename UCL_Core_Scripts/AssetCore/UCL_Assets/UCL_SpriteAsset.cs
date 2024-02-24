
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/24 2024 09:40
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core
{
    public class UCL_SpriteAsset : UCL_Asset<UCL_SpriteAsset>
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


