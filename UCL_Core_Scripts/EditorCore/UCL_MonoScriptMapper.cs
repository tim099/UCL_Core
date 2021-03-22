using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.EditorLib
{
    static public class MonoScriptMapper
    {
        #region FromMonoBehaviour
        private static System.Func<MonoBehaviour, TextAsset> m_FromMonoBehaviour = null;
        /// <summary>
        /// Mapping to MonoScript.FromMonoBehaviour, Only work on Editor!!
        /// </summary>
        /// <param name="iMonoBehaviour"></param>
        /// <returns></returns>
        public static UnityEngine.TextAsset FromMonoBehaviour(MonoBehaviour iMonoBehaviour)
        {
            if (m_FromMonoBehaviour == null) return null;
            return m_FromMonoBehaviour.Invoke(iMonoBehaviour);
        }
        public static void InitFromMonoBehaviour(System.Func<MonoBehaviour, TextAsset> iFromMonoBehaviour)
        {
            m_FromMonoBehaviour = iFromMonoBehaviour;
        }
        #endregion

        #region FromScriptableObject
        private static System.Func<ScriptableObject, TextAsset> m_FromScriptableObject = null;
        /// <summary>
        /// Mapping to MonoScript.ScriptableObject, Only work on Editor!!
        /// </summary>
        /// <param name="iScriptableObject"></param>
        /// <returns></returns>
        public static UnityEngine.TextAsset FromScriptableObject(ScriptableObject iScriptableObject)
        {
            if (m_FromScriptableObject == null) return null;
            return m_FromScriptableObject.Invoke(iScriptableObject);
        }
        public static void InitFromScriptableObject(System.Func<ScriptableObject, TextAsset> iFromScriptableObject)
        {
            m_FromScriptableObject = iFromScriptableObject;
        }
        #endregion
    }
}