using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI {

    static public class UCL_GUIStyle {
        private class StyleData
        {
            /// <summary>
            /// GUIStyle for GUILayout.Box
            /// </summary>
            public GUIStyle BoxStyle
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
            GUIStyle m_BoxStyle = null;

            public GUIStyle ButtonStyle
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
            GUIStyle m_ButtonStyle = null;


            public GUIStyle GetButtonStyle(Color iCol, int iFontSize = 12)
            {

                if (m_ButtonStyleDic == null)
                {
                    //Debug.LogError("GetButtonText! m_ButtonTextColorDic == null");
                    m_ButtonStyleDic = new Dictionary<System.Tuple<Color, int>, GUIStyle>();
                }
                var aKey = new System.Tuple<Color, int>(iCol, iFontSize);
                if (!m_ButtonStyleDic.ContainsKey(aKey))
                {
                    var aText = new GUIStyle(GUI.skin.button);
                    aText.normal.textColor = iCol;
                    aText.active.textColor = iCol;
                    aText.hover.textColor = iCol;
                    aText.fontSize = iFontSize;
                    //Debug.LogError("aText.fontSize:" + aText.fontSize); 12
                    m_ButtonStyleDic.Add(aKey, aText);
                }
                return m_ButtonStyleDic[aKey];
            }
            Dictionary<System.Tuple<Color, int>, GUIStyle> m_ButtonStyleDic = null;

            public GUIStyle LabelStyle
            {
                get
                {
                    if (s_LabelStyle == null)
                    {
                        s_LabelStyle = new GUIStyle(GUI.skin.label);
                        s_LabelStyle.richText = true;
                        var aTextCol = Color.white;
                        s_LabelStyle.normal.textColor = aTextCol;
                        s_LabelStyle.focused.textColor = aTextCol;
                        s_LabelStyle.hover.textColor = aTextCol;
                    }
                    return s_LabelStyle;
                }
            }
            GUIStyle s_LabelStyle = null;

            Dictionary<System.Tuple<Color, int>, GUIStyle> m_LabelStyleDic = null;
            public GUIStyle GetLabelStyle(Color iTextCol, int iSize = 16)
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

        }


        /// <summary>
        /// Please set this flag to true if get GUIStyle in EditorWindow.OnGUI()
        /// and set to false when EditorWindow.OnGUI() end 
        /// </summary>
        static public bool IsInEditorWindow = false;

        static StyleData s_Data = null;
        static StyleData s_EditorWindowData = null;
        static StyleData Data => s_Data == null? s_Data = new StyleData() : s_Data;
        static StyleData EditorWindowData => s_EditorWindowData == null ? s_EditorWindowData = new StyleData() : s_EditorWindowData;
        /// <summary>
        /// GUIStyle for GUILayout.Box
        /// </summary>
        static public GUIStyle BoxStyle => IsInEditorWindow ? EditorWindowData.BoxStyle : Data.BoxStyle;

        /// <summary>
        /// GUIStyle for GUILayout.Button
        /// </summary>
        static public GUIStyle ButtonStyle => IsInEditorWindow ? EditorWindowData.ButtonStyle : Data.ButtonStyle;



        #region ButtonText
        public static GUIStyle GetButtonStyle(Color iCol, int iFontSize = 12) => IsInEditorWindow? EditorWindowData.GetButtonStyle(iCol, iFontSize)
            : Data.GetButtonStyle(iCol, iFontSize);
        public static GUIStyle ButtonTextRed => GetButtonStyle(Color.red);
        public static GUIStyle ButtonTextYellow => GetButtonStyle(Color.yellow);
        public static GUIStyle ButtonTextGreen => GetButtonStyle(Color.green);
        #endregion



        #region Label
        static public GUIStyle LabelStyle => IsInEditorWindow ? EditorWindowData.LabelStyle : Data.LabelStyle;

        public static GUIStyle GetLabelStyle(Color iTextCol, int iSize = 16) => IsInEditorWindow ? EditorWindowData.GetLabelStyle(iTextCol, iSize)
            : Data.GetLabelStyle(iTextCol, iSize);
        #endregion


    }
}