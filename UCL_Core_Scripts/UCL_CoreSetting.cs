using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace UCL.Core {
#if UNITY_EDITOR
    [Core.ATTR.EnableUCLEditor]
#endif
    [CreateAssetMenu(fileName = "CoreSetting", menuName = "UCL/CoreSetting")]
    public class UCL_CoreSetting : ScriptableObject {
        public static UCL_CoreSetting GetCoreSetting() {
            return Resources.Load<UCL_CoreSetting>("CoreSetting");
        }
#if UNITY_EDITOR
        /// <summary>
        /// return the root directory of UCL_Core
        /// </summary>
        /// <returns></returns>
        public static string GetFolderPath() {
            var core_setting = GetCoreSetting();
            if(core_setting == null) {
                Debug.LogError("UCL.Core.UCL_CoreSetting GetFolderPath() Fail!!core_setting == null");
                return "";
            }
            string path = UnityEditor.AssetDatabase.GetAssetPath(core_setting);

            return FileLib.Lib.RemoveFolderPath(path, 2);
        }
#endif
    }
}

