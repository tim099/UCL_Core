using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

namespace UCL.Core.EditorLib
{
    public static class EditorUtilityMapper
    {
        #region OpenFilePanel
        static Func<string, string, string, string> m_OpenFilePanel = null;
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
            if(m_OpenFilePanel == null)
            {
                return string.Empty;
            }
            return m_OpenFilePanel.Invoke(title, directory, extension);
        }
        #endregion

        #region OpenFolderPanel
        static Func<string, string, string, string> m_OpenFolderPanel = null;
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
            if (m_OpenFilePanel == null)
            {
                return string.Empty;
            }
            return m_OpenFolderPanel.Invoke(title, folder, defaultName);
        }
        #endregion
    }
}