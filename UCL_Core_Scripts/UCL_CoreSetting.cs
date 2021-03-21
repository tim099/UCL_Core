using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UCL
{
    #region Enum
    public enum ComponentState : int
    {
        Null = 0,
        OnAwake = 1,
        OnStart = 1 << 1,
        OnDestroy = 1 << 2,
        OnEnable = 1 << 3,
        OnDisable = 1 << 4,
        OnReset = 1 << 5,
    }
    #endregion
}

namespace UCL.Core {

    [Core.ATTR.EnableUCLEditor]
    [CreateAssetMenu(fileName = "CoreSetting", menuName = "UCL/CoreSetting")]
    public class UCL_CoreSetting : ScriptableObject {
        public static UCL_CoreSetting GetCoreSetting() {
            return Resources.Load<UCL_CoreSetting>("CoreSetting");
        }
        public static Material LoadMaterial(string iName) {
            Material aMat = null;
#if UNITY_EDITOR
            var aPath = Path.Combine(GetFolderPath(), "UCL_Core_Materials", iName + ".mat");
            aMat = UCL.Core.EditorLib.AssetDatabaseMapper.LoadMainAssetAtPath(aPath) as Material;
#endif
            return aMat;
        }
#if UNITY_EDITOR
        /// <summary>
        /// return the root directory of UCL_Core
        /// </summary>
        /// <returns></returns>
        public static string GetFolderPath() {
            var aCoreSetting = GetCoreSetting();
            if(aCoreSetting == null) {
                Debug.LogError("UCL.Core.UCL_CoreSetting GetFolderPath() Fail!!core_setting == null");
                return "";
            }
            string aPath = UCL.Core.EditorLib.AssetDatabaseMapper.GetAssetPath(aCoreSetting);

            return FileLib.Lib.RemoveFolderPath(aPath, 2);
        }
#endif
    }
}

