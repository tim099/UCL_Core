using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI {
    static public class UCL_GUIStyle {
        static Dictionary<Color, GUIStyle> m_ButtonTextColorDic;

        public static GUIStyle GetButtonText(Color col) {
            if(m_ButtonTextColorDic == null) {
                m_ButtonTextColorDic = new Dictionary<Color, GUIStyle>();
            }
            if(!m_ButtonTextColorDic.ContainsKey(col)) {
                var text = new GUIStyle(GUI.skin.button);
                text.normal.textColor = col;
                text.active.textColor = col;
                text.hover.textColor = col;
                m_ButtonTextColorDic.Add(col, text);
            }
            return m_ButtonTextColorDic[col];
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