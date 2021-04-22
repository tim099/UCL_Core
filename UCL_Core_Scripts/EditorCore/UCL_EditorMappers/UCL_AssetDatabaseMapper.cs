using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.EditorLib
{
    /// <summary>
    /// this class Mapping Editor Function for none Editor Class, and Only working in Editor
    /// </summary>
    public static class AssetDatabaseMapper
    {
        #region LoadAssetAtPath
        private static System.Func<string, Type, UnityEngine.Object> m_LoadAssetAtPath = null;
        /// <summary>
        /// Mapping to AssetDatabase.LoadAssetAtPath Only work on Editor!!
        /// </summary>
        /// <param name="iAssetPath"></param>
        /// <param name="iType"></param>
        /// <returns></returns>
        static public UnityEngine.Object LoadAssetAtPath(string iAssetPath, Type iType)
        {
#if UNITY_EDITOR
            if (m_LoadAssetAtPath == null) return null;
            return m_LoadAssetAtPath.Invoke(iAssetPath, iType);
#else
            return null;
#endif
        }
        static public T LoadAssetAtPath<T>(string iAssetPath) where T : UnityEngine.Object
        {
#if UNITY_EDITOR
            if (m_LoadAssetAtPath == null) return null;
            return m_LoadAssetAtPath.Invoke(iAssetPath, typeof(T)) as T;
#else
            return null;
#endif
        }
        static public void InitLoadAssetAtPath(System.Func<string, Type, UnityEngine.Object> iLoadAssetAtPath)
        {
            m_LoadAssetAtPath = iLoadAssetAtPath;
        }
        #endregion

        #region GetBuiltinExtraResource
        private static System.Func<Type, string, UnityEngine.Object> m_GetBuiltinExtraResource = null;
        /// <summary>
        /// Mapping to AssetDatabase.GetBuiltinExtraResource Only work on Editor!!
        /// </summary>
        /// <param name="iType"></param>
        /// <param name="iAssetPath"></param>
        /// <returns></returns>
        static public UnityEngine.Object GetBuiltinExtraResource(Type iType, string iAssetPath)
        {
#if UNITY_EDITOR
            if (m_GetBuiltinExtraResource == null) return null;
            return m_GetBuiltinExtraResource.Invoke(iType, iAssetPath);
#else
            return null;
#endif
        }
        /// <summary>
        /// Mapping to AssetDatabase.GetBuiltinExtraResource, Only work in Editor!!
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iAssetPath"></param>
        /// <returns></returns>
        static public T GetBuiltinExtraResource<T>(string iAssetPath) where T : UnityEngine.Object
        {
#if UNITY_EDITOR
            if (m_GetBuiltinExtraResource == null) return null;
            return m_GetBuiltinExtraResource.Invoke(typeof(T), iAssetPath) as T;
#else
            return null;
#endif
        }
        static public void InitGetBuiltinExtraResource(System.Func<Type, string, UnityEngine.Object> iGetBuiltinExtraResource)
        {
            m_GetBuiltinExtraResource = iGetBuiltinExtraResource;
        }
        #endregion

        #region GetAssetPath
        private static System.Func<UnityEngine.Object, string> m_GetAssetPath = null;
        /// <summary>
        /// Mapping to AssetDatabase.GetAssetPath, Only work in Editor!!
        /// </summary>
        /// <param name="iAssetObject"></param>
        /// <returns></returns>
        static public string GetAssetPath(UnityEngine.Object iAssetObject)
        {
#if UNITY_EDITOR
            if (m_GetAssetPath == null) return string.Empty;
            return m_GetAssetPath.Invoke(iAssetObject);
#else
            return string.Empty;
#endif
        }
        static public void InitGetAssetPath(System.Func<UnityEngine.Object, string> iGetAssetPath)
        {
            m_GetAssetPath = iGetAssetPath;
        }
        private static System.Func<int, string> m_GetAssetPath2 = null;
        /// <summary>
        /// Returns the path name relative to the project folder where the asset is stored.
        /// </summary>
        /// <param name="iInstanceID">The instance ID of the asset.</param>
        /// <returns>The asset path name, or null, or an empty string if the asset does not exist.</returns>
        static public string GetAssetPath(int iInstanceID)
        {
#if UNITY_EDITOR
            if (m_GetAssetPath == null) return string.Empty;
            return m_GetAssetPath2.Invoke(iInstanceID);
#else
            return string.Empty;
#endif
        }
        static public void InitGetAssetPath2(System.Func<int, string> iGetAssetPath2)
        {
            m_GetAssetPath2 = iGetAssetPath2;
        }
        #endregion

        #region LoadMainAssetAtPath
        private static System.Func<string, UnityEngine.Object> m_LoadMainAssetAtPath = null;
        /// <summary>
        /// Mapping to AssetDatabase.LoadMainAssetAtPath Only work on Editor!!
        /// </summary>
        /// <param name="iAssetPath"></param>
        /// <returns></returns>
        static public UnityEngine.Object LoadMainAssetAtPath(string iAssetPath)
        {
#if UNITY_EDITOR
            if (m_LoadMainAssetAtPath == null) return null;
            return m_LoadMainAssetAtPath.Invoke(iAssetPath);
#else
            return null;
#endif
        }
        static public void InitLoadMainAssetAtPath(System.Func<string, UnityEngine.Object> iLoadMainAssetAtPath)
        {
            m_LoadMainAssetAtPath = iLoadMainAssetAtPath;
        }
        #endregion

        #region Refresh

        private static System.Action m_Refresh = null;
        /// <summary>
        /// Mapping to AssetDatabase.Refresh Only work on Editor!!
        /// </summary>
        /// <param name="iAssetPath"></param>
        /// <returns></returns>
        static public void Refresh()
        {
#if UNITY_EDITOR
            if (m_Refresh == null) return;
            m_Refresh.Invoke();
#endif
        }
        static public void InitRefresh(System.Action iRefresh)
        {
            m_Refresh = iRefresh;
        }
        #endregion

        #region Contains

        private static System.Func<UnityEngine.Object, bool> m_Contains = null;
        /// <summary>
        /// Mapping to AssetDatabase.Contains Only work on Editor!!
        /// </summary>
        /// <param name="iAssetPath"></param>
        /// <returns></returns>
        static public bool Contains(UnityEngine.Object iObj)
        {
#if UNITY_EDITOR
            if (m_Contains == null) return false;
            return m_Contains.Invoke(iObj);
#else
            return false;
#endif
        }
        public static void InitContains(System.Func<UnityEngine.Object, bool> iContains)
        {
            m_Contains = iContains;
        }
        #endregion

        #region CreateAsset

        private static System.Action<UnityEngine.Object, string> m_CreateAsset = null;
        /// <summary>
        /// Mapping to AssetDatabase.CreateAsset Only work on Editor!!
        /// </summary>
        /// <param name="iAssetPath"></param>
        /// <returns></returns>
        static public void CreateAsset([JetBrains.Annotations.NotNull] UnityEngine.Object iAsset, string iPath)
        {
#if UNITY_EDITOR
            if (m_CreateAsset == null) return;
            m_CreateAsset.Invoke(iAsset, iPath);
#endif
            return;
        }
        public static void InitCreateAsset(System.Action<UnityEngine.Object, string> iCreateAsset)
        {
            m_CreateAsset = iCreateAsset;
        }
        #endregion
    }
}