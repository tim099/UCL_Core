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


        #region DisplayProgressBar
        private static Action<string , string , float> m_DisplayProgressBar = null;
        /// <summary>
        /// Displays or updates a progress bar.
        /// </summary>
        /// <param name="title"></param>
        /// <param name="info"></param>
        /// <param name="progress"></param>
        public static void DisplayProgressBar(string title, string info, float progress)
        {
            if (m_DisplayProgressBar == null) return;
            m_DisplayProgressBar.Invoke(title, info, progress);
        }
        public static void InitDisplayProgressBar(Action<string, string, float> iDisplayProgressBar)
        {
            m_DisplayProgressBar = iDisplayProgressBar;
        }
        #endregion

        #region ClearProgressBar
        private static Action m_ClearProgressBar = null;

        /// <summary>
        /// Removes progress bar.
        /// </summary>
        public static void ClearProgressBar()
        {
            if (m_ClearProgressBar == null) return;
            m_ClearProgressBar.Invoke();
        }
        public static void InitClearProgressBar(Action iClearProgressBar)
        {
            m_ClearProgressBar = iClearProgressBar;
        }
        #endregion
    }
}