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
        static public GUIStyle ButtonStyle
        {
            get
            {
                if (m_BoxStyle == null)
                {
                    m_BoxStyle = new GUIStyle(GUI.skin.button);
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


        static Dictionary<Color, GUIStyle> m_ButtonTextColorDic;

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

        public static GUIStyle TextRed {
            get {
                return GetButtonText(Color.red);
            }
        }
        public static GUIStyle TextYellow {
            get {
                return GetButtonText(Color.yellow);
            }
        }
        public static GUIStyle TextGreen {
            get {
                return GetButtonText(Color.green);
            }
        }
        // var style = new GUIStyle(GUI.skin.button);
        //style.normal.textColor = Color.blue;

        // Start is called before the first frame update

    }
}