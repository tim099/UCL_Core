
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/26 2024 12:53
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UCL.Core.LocalizeLib;
using UnityEngine;

namespace UCL.Core.UI {

    static public class UCL_GUIStyle {
        public const int DefaultFontSize = 12;
        public const int MediumFontSize = 16;
        public class StyleData
        {
            
            public const float ThumbStyleSize = 10;
            public const float SliderHeight = 3;
            private const string ScaleKey = "UCL_GUIStyle.Scale";



            private int m_FontSize = DefaultFontSize;
            private GUIStyle m_BoxStyle = null;
            private GUIStyle m_HorizontalSliderStyle = null;
            private GUIStyle m_HorizontalSliderThumbStyle = null;
            private GUIStyle m_TextFieldStyle = null;
            private GUIStyle m_TextAreaStyle = null;
            Dictionary<System.Tuple<Color, int>, GUIStyle> m_ButtonStyleDic = null;
            Dictionary<System.Tuple<Color, int>, GUIStyle> m_LabelStyleDic = null;

            public const float MinScale = 0.1f;
            public static float Scale
            {
                get => PlayerPrefs.GetFloat(ScaleKey, 1f);
                private set 
                {
                    //Debug.LogError($"Scale:{value}");
                    PlayerPrefs.SetFloat(ScaleKey, value);
                }
            }

            public StyleData()
            {
                ApplyScale();
            }
            public void ApplyScale()
            {
                var scale = Scale;
                //Debug.LogError($"ApplyScale Scale:{scale}");
                m_FontSize = Mathf.RoundToInt(scale * DefaultFontSize);
                if (m_BoxStyle != null)
                {
                    m_BoxStyle.fontSize = m_FontSize;
                }
                if (m_TextFieldStyle != null)
                {
                    m_TextFieldStyle.fontSize = m_FontSize;
                }
                if (m_TextAreaStyle != null)
                {
                    m_TextAreaStyle.fontSize = m_FontSize;
                }

                if (m_HorizontalSliderThumbStyle != null)
                {
                    m_HorizontalSliderThumbStyle.fixedWidth = Mathf.RoundToInt(scale * ThumbStyleSize);
                    m_HorizontalSliderThumbStyle.fixedHeight = Mathf.RoundToInt(scale * ThumbStyleSize);
                }

                if (m_HorizontalSliderStyle != null)
                {
                    m_HorizontalSliderStyle.fixedHeight = Mathf.RoundToInt(scale * SliderHeight);
                }

                if (m_ButtonStyleDic != null)
                {
                    foreach (var aKey in m_ButtonStyleDic.Keys)
                    {
                        var aStyle = m_ButtonStyleDic[aKey];
                        aStyle.fontSize = Mathf.RoundToInt(scale * aKey.Item2);
                    }
                }
                if (m_LabelStyleDic != null)
                {
                    foreach (var aKey in m_LabelStyleDic.Keys)
                    {
                        var aStyle = m_LabelStyleDic[aKey];
                        aStyle.fontSize = Mathf.RoundToInt(scale * aKey.Item2);
                    }
                }

            }
            public void SetScale(float iScale)
            {
                if(iScale == Scale) return;
                iScale = Mathf.Max(iScale, MinScale);//Scale must >= MinScale

                Scale = iScale;
                ApplyScale();
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
            

            public GUIStyle ButtonStyle => GetButtonStyle(Color.white, DefaultFontSize);



            public GUIStyle GetButtonStyle(Color iCol, int iFontSize = DefaultFontSize)
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
                    aButtonStyle.fontSize = Mathf.RoundToInt(iFontSize * Scale);
                    aButtonStyle.richText = true;
                    //Debug.LogError("aText.fontSize:" + aText.fontSize); 12
                    m_ButtonStyleDic.Add(aKey, aButtonStyle);
                }
                return m_ButtonStyleDic[aKey];
            }
            

            public GUIStyle LabelStyle => GetLabelStyle(Color.white, DefaultFontSize);



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
                    aText.fontSize = Mathf.RoundToInt(Scale * iSize);
                    aText.richText = true;
                    //aText.fontSize = m_FontSize;
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
                        m_HorizontalSliderStyle.fixedHeight = Mathf.RoundToInt(Scale * SliderHeight);
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
                        m_HorizontalSliderThumbStyle.fixedWidth = Mathf.RoundToInt(Scale * ThumbStyleSize);
                        m_HorizontalSliderThumbStyle.fixedHeight = Mathf.RoundToInt(Scale * ThumbStyleSize);
                    }
                    return m_HorizontalSliderThumbStyle;
                }
            }
            public GUIStyle TextFieldStyle
            {
                get
                {
                    if (m_TextFieldStyle == null)
                    {
                        m_TextFieldStyle = new GUIStyle(GUI.skin.textField);
                        m_TextFieldStyle.fontSize = m_FontSize;
                    }
                    return m_TextFieldStyle;
                }
            }
            public GUIStyle TextAreaStyle
            {
                get
                {
                    if (m_TextAreaStyle == null)
                    {
                        m_TextAreaStyle = new GUIStyle(GUI.skin.textArea);
                        m_TextAreaStyle.fontSize = m_FontSize;
                    }
                    return m_TextAreaStyle;
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
        public static int GetScaledSize(float iSize) => Mathf.RoundToInt(iSize * StyleData.Scale);
        public static StyleData CurStyleData => IsInEditorWindow ? EditorWindowData : Data;
        /// <summary>
        /// GUIStyle for GUILayout.Box
        /// </summary>
        static public GUIStyle BoxStyle => CurStyleData.BoxStyle;

        /// <summary>
        /// GUIStyle for GUILayout.Button
        /// </summary>
        static public GUIStyle ButtonStyle => CurStyleData.ButtonStyle;
        static public GUIStyle TextAreaStyle => CurStyleData.TextAreaStyle;
        static public GUIStyle TextFieldStyle => CurStyleData.TextFieldStyle;

        #region ButtonText
        public static GUIStyle GetButtonStyle(Color iCol, int iFontSize = DefaultFontSize) => CurStyleData.GetButtonStyle(iCol, iFontSize);
        public static GUIStyle ButtonTextRed => GetButtonStyle(Color.red);
        public static GUIStyle ButtonTextYellow => GetButtonStyle(Color.yellow);
        public static GUIStyle ButtonTextGreen => GetButtonStyle(Color.green);
        #endregion

        #region GUI

        public static void SetSizeOnGUI()
        {
            using (var aScopeH = new GUILayout.HorizontalScope("box"))
            {
                var aStyleData = UCL_GUIStyle.CurStyleData;
                float aScale = UCL_GUIStyle.StyleData.Scale;
                aScale = Mathf.Max(aScale, StyleData.MinScale);
                int aSize = Mathf.RoundToInt(30f / aScale);
                var aButtonStyle = UCL_GUIStyle.GetButtonStyle(Color.white, aSize);
                if (GUILayout.Button(UCL_LocalizeManager.Get("Small"), aButtonStyle))
                {
                    aStyleData.SetScale(1f);
                }
                GUILayout.Space(30);
                if (GUILayout.Button(UCL_LocalizeManager.Get("Medium"), aButtonStyle))
                {
                    aStyleData.SetScale(1.5f);
                }
                GUILayout.Space(30);
                if (GUILayout.Button(UCL_LocalizeManager.Get("Big"), aButtonStyle))
                {
                    aStyleData.SetScale(2.5f);
                }
                GUILayout.Space(30);
                if (GUILayout.Button(UCL_LocalizeManager.Get("XL"), aButtonStyle))
                {
                    aStyleData.SetScale(4f);
                }
            }
        }

        static Stack<Color> s_ColorStack = new Stack<Color>();
        public static void PushGUIColor(Color iCol)
        {
            s_ColorStack.Push(GUI.color);
            GUI.color = iCol;
        }
        public static void PopGUIColor()
        {
            if(s_ColorStack.Count == 0)
            {
                return;
            }
            GUI.color = s_ColorStack.Pop();
        }
        #endregion


        #region Label
        static public GUIStyle LabelStyle => CurStyleData.LabelStyle;

        public static GUIStyle GetLabelStyle(Color iTextCol, int iSize = MediumFontSize) => CurStyleData.GetLabelStyle(iTextCol, iSize);
        #endregion


    }
    static public class UCL_Color
    {
        public static class Half
        {
            public static Color White
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => new Color(1, 1, 1, 0.5f);
            }
            public static Color Red
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => new Color(1, 0, 0, 0.5f);
            }
            public static Color Green
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => new Color(0, 1, 0, 0.5f);
            }
        }

        public static class OneThird
        {
            public static Color White
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => new Color(1, 1, 1, 0.33333333f);
            }
        }
    }
}