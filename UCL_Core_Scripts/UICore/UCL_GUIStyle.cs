﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI {

    static public class UCL_GUIStyle {
        public class StyleData
        {
            public const int DefaultFontSize = 12;
            public const float ThumbStyleSize = 10;
            public const float SliderHeight = 3;
            private float m_Scale = 1f;

            private int m_FontSize = DefaultFontSize;
            private GUIStyle m_BoxStyle = null;
            private GUIStyle m_ButtonStyle = null;
            private GUIStyle m_LabelStyle = null;
            private GUIStyle m_HorizontalSliderStyle = null;
            private GUIStyle m_HorizontalSliderThumbStyle = null;
            Dictionary<System.Tuple<Color, int>, GUIStyle> m_ButtonStyleDic = null;
            Dictionary<System.Tuple<Color, int>, GUIStyle> m_LabelStyleDic = null;

            public float Scale => m_Scale;
            public void SetScale(float iScale)
            {
                if(iScale == m_Scale) return;

                m_Scale = iScale;

                m_FontSize = Mathf.RoundToInt(m_Scale * DefaultFontSize);
                if (m_BoxStyle != null) {
                    m_BoxStyle.fontSize = m_FontSize;
                }
                //if(m_ButtonStyle != null)
                //{
                //    m_ButtonStyle.fontSize = m_FontSize;
                //}
                //if(m_LabelStyle != null)
                //{
                //    m_LabelStyle.fontSize = m_FontSize;
                //}
                m_HorizontalSliderThumbStyle.fixedWidth = Mathf.RoundToInt(m_Scale * ThumbStyleSize);
                m_HorizontalSliderThumbStyle.fixedHeight = Mathf.RoundToInt(m_Scale * ThumbStyleSize);

                m_HorizontalSliderStyle.fixedHeight = Mathf.RoundToInt(m_Scale * SliderHeight);
                foreach (var aKey in m_ButtonStyleDic.Keys)
                {
                    var aStyle = m_ButtonStyleDic[aKey];
                    aStyle.fontSize = Mathf.RoundToInt(m_Scale * aKey.Item2);
                }
                foreach (var aKey in m_LabelStyleDic.Keys)
                {
                    var aStyle = m_LabelStyleDic[aKey];
                    aStyle.fontSize = Mathf.RoundToInt(m_Scale * aKey.Item2);
                }
            }
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
                        //m_BoxStyle.clipping = TextClipping.Clip;
                        //m_BoxStyle.stretchWidth = false;
                        m_BoxStyle.wordWrap = true;

                        m_BoxStyle.fontSize = m_FontSize;
                    }
                    return m_BoxStyle;
                }
            }
            

            public GUIStyle ButtonStyle
            {
                get
                {
                    if (m_ButtonStyle == null)
                    {
                        m_ButtonStyle = GetButtonStyle(Color.white, DefaultFontSize);
                        //m_ButtonStyle = new GUIStyle(GUI.skin.button);
                        //m_ButtonStyle.richText = true;
                        //var aTextCol = Color.white;
                        //m_ButtonStyle.normal.textColor = aTextCol;
                        //m_ButtonStyle.focused.textColor = aTextCol;
                        //m_ButtonStyle.hover.textColor = aTextCol;

                        //m_ButtonStyle.fontSize = m_FontSize;
                    }
                    return m_ButtonStyle;
                }
            }
            


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
                    var aButtonStyle = new GUIStyle(GUI.skin.button);
                    aButtonStyle.normal.textColor = iCol;
                    aButtonStyle.active.textColor = iCol;
                    aButtonStyle.hover.textColor = iCol;
                    aButtonStyle.fontSize = Mathf.RoundToInt(iFontSize * m_Scale);
                    aButtonStyle.richText = true;
                    //Debug.LogError("aText.fontSize:" + aText.fontSize); 12
                    m_ButtonStyleDic.Add(aKey, aButtonStyle);
                }
                return m_ButtonStyleDic[aKey];
            }
            

            public GUIStyle LabelStyle
            {
                get
                {
                    if (m_LabelStyle == null)
                    {
                        m_LabelStyle = GetLabelStyle(Color.white, DefaultFontSize);
                        //s_LabelStyle = new GUIStyle(GUI.skin.label);
                        //s_LabelStyle.richText = true;
                        //var aTextCol = Color.white;
                        //s_LabelStyle.normal.textColor = aTextCol;
                        //s_LabelStyle.focused.textColor = aTextCol;
                        //s_LabelStyle.hover.textColor = aTextCol;
                        //s_LabelStyle.fontSize = 16;
                    }
                    return m_LabelStyle;
                }
            }
            

            
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
                    aText.fontSize = m_FontSize;
                    m_LabelStyleDic.Add(aKey, aText);
                }
                return m_LabelStyleDic[aKey];
            }


            public GUIStyle HorizontalSliderStyle
            {
                get
                {
                    if (m_HorizontalSliderStyle == null)
                    {
                        m_HorizontalSliderStyle = new GUIStyle(GUI.skin.horizontalSlider);
                        m_HorizontalSliderStyle.fixedHeight = Mathf.RoundToInt(m_Scale * SliderHeight);
                        //m_HorizontalSliderStyle.alignment = TextAnchor.MiddleCenter;
                    }
                    return m_HorizontalSliderStyle;
                }
            }
            public GUIStyle HorizontalSliderThumbStyle
            {
                get
                {
                    if (m_HorizontalSliderThumbStyle == null)
                    {
                        m_HorizontalSliderThumbStyle = new GUIStyle(GUI.skin.horizontalSliderThumb);
                        m_HorizontalSliderThumbStyle.fixedWidth = Mathf.RoundToInt(m_Scale * ThumbStyleSize);
                        m_HorizontalSliderThumbStyle.fixedHeight = Mathf.RoundToInt(m_Scale * ThumbStyleSize);
                    }
                    return m_HorizontalSliderThumbStyle;
                }
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
        public static int GetScaledSize(float iSize) => Mathf.RoundToInt(iSize * CurStyleData.Scale);
        public static StyleData CurStyleData => IsInEditorWindow ? EditorWindowData : Data;
        /// <summary>
        /// GUIStyle for GUILayout.Box
        /// </summary>
        static public GUIStyle BoxStyle => CurStyleData.BoxStyle;

        /// <summary>
        /// GUIStyle for GUILayout.Button
        /// </summary>
        static public GUIStyle ButtonStyle => CurStyleData.ButtonStyle;



        #region ButtonText
        public static GUIStyle GetButtonStyle(Color iCol, int iFontSize = 12) => CurStyleData.GetButtonStyle(iCol, iFontSize);
        public static GUIStyle ButtonTextRed => GetButtonStyle(Color.red);
        public static GUIStyle ButtonTextYellow => GetButtonStyle(Color.yellow);
        public static GUIStyle ButtonTextGreen => GetButtonStyle(Color.green);
        #endregion



        #region Label
        static public GUIStyle LabelStyle => CurStyleData.LabelStyle;

        public static GUIStyle GetLabelStyle(Color iTextCol, int iSize = 16) => CurStyleData.GetLabelStyle(iTextCol, iSize);
        #endregion


    }
}