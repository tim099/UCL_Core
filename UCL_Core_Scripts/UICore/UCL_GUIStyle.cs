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
                }
                return m_TextRed;
            }
        }
        // var style = new GUIStyle(GUI.skin.button);
        //style.normal.textColor = Color.blue;

        // Start is called before the first frame update

    }
}