using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

namespace UCL.Core.EditorLib
{
    public static class EditorUtilityMapper
    {
        #region OpenFilePanel
        private static Func<string, string, string, string> m_OpenFilePanel = null;
        public static void InitOpenFilePanel(Func<string, string, string, string> iOpenFilePanel)
        {
            m_OpenFilePanel = iOpenFilePanel;
        }
        /// <summary>
        /// Mapping to EditorUtility.OpenFilePanel, Only work on Editor!!
        /// </summary>
        /// <param name="title"></param>
        /// <param name="directory"></param>
        /// <param name="extension"></param>
        /// <returns></returns>
        public static string OpenFilePanel(string title, string directory, string extension)
        {
#if UNITY_EDITOR
            if (m_OpenFilePanel == null)
            {
                return string.Empty;
            }
            return m_OpenFilePanel.Invoke(title, directory, extension);
#else
            return string.Empty;
#endif
        }
        #endregion

        #region OpenFolderPanel
        private static Func<string, string, string, string> m_OpenFolderPanel = null;
        public static void InitOpenFolderPanel(Func<string, string, string, string> iOpenFolderPanel)
        {
            m_OpenFolderPanel = iOpenFolderPanel;
        }
        /// <summary>
        /// Mapping to EditorUtility.OpenFolderPanel, Only work on Editor!!
        /// </summary>
        /// <param name="title"></param>
        /// <param name="folder"></param>
        /// <param name="defaultName"></param>
        /// <returns></returns>
        public static string OpenFolderPanel(string title, string folder, string defaultName)
        {
#if UNITY_EDITOR
            if (m_OpenFilePanel == null)
            {
                return string.Empty;
            }
            return m_OpenFolderPanel.Invoke(title, folder, defaultName);
#else
            return string.Empty;
#endif
        }
        #endregion

        #region OpenFolderPanel
        private static Action<UnityEngine.Object, UnityEngine.Object> m_CopySerialized = null;
        public static void InitCopySerialized(Action<UnityEngine.Object, UnityEngine.Object> iCopySerialized)
        {
            m_CopySerialized = iCopySerialized;
        }
        /// <summary>
        /// Copy all settings of a Unity Object.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="dest"></param>
        public static void CopySerialized(UnityEngine.Object source, UnityEngine.Object dest)
        {
#if UNITY_EDITOR
            if (m_CopySerialized == null)
            {
                return;
            }
            m_CopySerialized.Invoke(source, dest);
#endif
        }
        #endregion

        #region SetDirty
        private static Action<UnityEngine.Object> m_SetDirty = null;
        public static void InitSetDirty(Action<UnityEngine.Object> iSetDirty)
        {
            m_SetDirty = iSetDirty;
        }
        /// <summary>
        /// Marks target object as dirty. (Only suitable for non-scene objects).
        /// </summary>
        /// <param name="iTarget">The object to mark as dirty.</param>
        public static void SetDirty([JetBrains.Annotations.NotNull] UnityEngine.Object iTarget)
        {
#if UNITY_EDITOR
            if (m_SetDirty == null)
            {
                return;
            }
            m_SetDirty.Invoke(iTarget);
#endif
        }
        #endregion
        //public static void SetDirty([NotNull] UnityEngine.Object target);
    }
}