using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.EditorLib
{
    public static class PrefabUtilityMapper
    {
        #region CreatePrefab
        private static System.Func<string, GameObject, GameObject> m_SaveAsPrefabAsset = null;
        /// <summary>
        /// Mapping to PrefabUtility.CreatePrefab Only work on Editor!!
        /// </summary>
        /// <param name="iAssetPath"></param>
        /// <returns></returns>
        static public GameObject SaveAsPrefabAsset(GameObject iObj, string iPath)
        {
            GameObject aObj = null;
#if UNITY_EDITOR
            if (m_SaveAsPrefabAsset == null) return null;
            aObj = m_SaveAsPrefabAsset.Invoke(iPath, iObj);
#endif
            return aObj;
        }
        public static void InitSaveAsPrefabAsset(System.Func<string, GameObject, GameObject> iCreatePrefab)
        {
            m_SaveAsPrefabAsset = iCreatePrefab;
        }

        #endregion
    }
}