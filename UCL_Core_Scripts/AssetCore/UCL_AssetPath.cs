
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/20 2024 22:48
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core
{
    public static class UCL_AssetPath
    {
        /// <summary>
        /// Return the corresponding root directory path based on the enum
        /// notice the difference in slash directions on different operating systems according to Path.DirectorySeparatorChar
        /// </summary>
        /// <param name="iAssetType"></param>
        /// <returns></returns>
        public static string GetPath(UCL_AssetType iAssetType)
        {
            switch (iAssetType)
            {
                case UCL_AssetType.StreamingAssets:
                    {
                        return Application.streamingAssetsPath;
                    }
                case UCL_AssetType.PersistentData:
                    {
                        return Application.persistentDataPath;
                    }
                case UCL_AssetType.Addressables:
                    {
                        return string.Empty;
                    }
            }
            return string.Empty;
        }
    }
}