using System.Collections;
using System.Collections.Generic;
using System.IO;
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
        public static Material LoadMaterial(string name) {
            Material mat = null;
#if UNITY_EDITOR
            var path = Path.Combine(Path.Combine(GetFolderPath(), "UCL_Core_Materials"), name+".mat");
            mat = UnityEditor.AssetDatabase.LoadMainAssetAtPath(path) as Material;
#endif
            return mat;
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

