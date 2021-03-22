using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.EditorLib
{
    public static class HandlesMapper
    {
        #region Label
        private static System.Action<Vector3, string> m_Label = null;
        /// <summary>
        /// Make a text label positioned in 3D space.
        /// </summary>
        /// <param name="position"></param>
        /// <param name="text"></param>
        public static void Label(Vector3 position, string text)
        {
            if (m_Label == null) return;
            m_Label.Invoke(position, text);
        }
        public static void InitLabel(System.Action<Vector3, string> iLabel)
        {
            m_Label = iLabel;
        }
        #endregion

        #region BeginGUI
        private static System.Action m_BeginGUI = null;

        /// <summary>
        /// Begin a 2D GUI block inside the 3D handle GUI.
        /// </summary>
        public static void BeginGUI()
        {
            if (m_BeginGUI == null) return;
            m_BeginGUI.Invoke();
        }
        public static void InitBeginGUI(System.Action iBeginGUI)
        {
            m_BeginGUI = iBeginGUI;
        }
        #endregion

        #region EndGUI
        private static System.Action m_EndGUI = null;

        /// <summary>
        /// End a 2D GUI block and get back to the 3D handle GUI.
        /// </summary>
        public static void EndGUI()
        {
            if (m_EndGUI == null) return;
            m_EndGUI.Invoke();
        }
        public static void InitEndGUI(System.Action iEndGUI)
        {
            m_EndGUI = iEndGUI;
        }
        #endregion
    }
}