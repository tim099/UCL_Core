using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI {
    static public class UCL_GUIStyle {
        /// <summary>
        /// GUIStyle for GUILayout.Box
        /// </summary>
        static public GUIStyle BoxStyle
        {
            get
            {
                if (m_BoxStyle == null)
                {
                    m_BoxStyle = new GUIStyle(GUI.skin.box);
                    m_BoxStyle.richText = true;
                    var aTextCol = Color.white;
                    m_BoxStyle.normal.textColor = aTextCol;
                    m_BoxStyle.focused.textColor = aTextCol;
                    m_BoxStyle.hover.textColor = aTextCol;
                }
                return m_BoxStyle;
            }
        }
        static GUIStyle m_BoxStyle = null;
        /// <summary>
        /// GUIStyle for GUILayout.Button
        /// </summary>
        static public GUIStyle ButtonStyle
        {
            get
            {
                if (m_ButtonStyle == null)
                {
                    m_ButtonStyle = new GUIStyle(GUI.skin.button);
                    m_ButtonStyle.richText = true;
                    var aTextCol = Color.white;
                    m_ButtonStyle.normal.textColor = aTextCol;
                    m_ButtonStyle.focused.textColor = aTextCol;
                    m_ButtonStyle.hover.textColor = aTextCol;
                }
                return m_ButtonStyle;
            }
        }
        static GUIStyle m_ButtonStyle = null;



        #region ButtonText
        static Dictionary<Color, GUIStyle> m_ButtonTextColorDic = null;

        public static GUIStyle GetButtonText(Color iCol) {
            if(m_ButtonTextColorDic == null) {
                m_ButtonTextColorDic = new Dictionary<Color, GUIStyle>();
            }
            if(!m_ButtonTextColorDic.ContainsKey(iCol)) {
                var aText = new GUIStyle(GUI.skin.button);
                aText.normal.textColor = iCol;
                aText.active.textColor = iCol;
                aText.hover.textColor = iCol;
                m_ButtonTextColorDic.Add(iCol, aText);
            }
            return m_ButtonTextColorDic[iCol];
        }

        public static GUIStyle ButtonTextRed {
            get {
                return GetButtonText(Color.red);
            }
        }
        public static GUIStyle ButtonTextYellow {
            get {
                return GetButtonText(Color.yellow);
            }
        }
        public static GUIStyle ButtonTextGreen {
            get {
                return GetButtonText(Color.green);
            }
        }
        #endregion



        #region Label

        static Dictionary<System.Tuple<Color, int> , GUIStyle> m_LabelStyleDic = null;
        public static GUIStyle GetLabelStyle(Color iTextCol, int iSize = 16)
        {
            if (m_LabelStyleDic == null)
            {
                m_LabelStyleDic = new Dictionary<System.Tuple<Color, int>, GUIStyle>();
            }
            System.Tuple<Color, int> aKey = new System.Tuple<Color, int>(iTextCol, iSize);
            if (!m_LabelStyleDic.ContainsKey(aKey))
            {
                var aText = new GUIStyle(GUI.skin.label);
                aText.normal.textColor = iTextCol;
                aText.active.textColor = iTextCol;
                aText.hover.textColor = iTextCol;
                aText.fontSize = iSize;
                aText.richText = true;
                m_LabelStyleDic.Add(aKey, aText);
            }
            return m_LabelStyleDic[aKey];
        }
        #endregion


    }
}