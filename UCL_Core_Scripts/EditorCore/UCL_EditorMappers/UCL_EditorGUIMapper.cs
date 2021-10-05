using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.EditorLib
{
    public static class EditorGUIMapper
    {

        #region Popup
        private static System.Func<Rect, int, string[], int> m_Popup = null;
        /// <summary>
        /// Makes a generic popup selection field.
        /// </summary>
        /// <param name="position">Rectangle on the screen to use for the field.</param>
        /// <param name="selectedIndex">The index of the option the field shows.</param>
        /// <param name="displayedOptions">An array with the options shown in the popup.</param>
        /// <returns></returns>
        public static int Popup(Rect position, int selectedIndex, string[] displayedOptions)
        {
            if (m_Popup == null) return 0;
            return m_Popup.Invoke(position, selectedIndex, displayedOptions);
        }
        public static void InitPopup(System.Func<Rect, int, string[], int> iPopup)
        {
            m_Popup = iPopup;
        }
        #endregion
    }
}