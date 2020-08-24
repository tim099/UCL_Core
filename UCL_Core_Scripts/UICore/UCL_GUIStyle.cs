using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI {
    static public class UCL_GUIStyle {
        static GUIStyle m_TextRed;

        public static GUIStyle TextRed {
            get {
                if(m_TextRed == null) {
                    m_TextRed = new GUIStyle(GUI.skin.button);
                    m_TextRed.normal.textColor = Color.red;
                    m_TextRed.active.textColor = Color.red;
                    m_TextRed.hover.textColor = new Color(1f,0.3f,0.3f);
                }
                return m_TextRed;
            }
        }
        // var style = new GUIStyle(GUI.skin.button);
        //style.normal.textColor = Color.blue;

        // Start is called before the first frame update

    }
}