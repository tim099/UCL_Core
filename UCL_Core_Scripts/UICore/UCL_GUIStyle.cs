
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/26 2024 12:53
using System;
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
            /// <summary>全域 GUI 縮放比例（DPI / 使用者設定，存 PlayerPrefs）。改值請走 <see cref="SetScale"/> 觸發 <see cref="ApplyScale"/>。</summary>
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
            /// <summary>依當前 <see cref="Scale"/> 重算所有已 cache 樣式的字級與滑條尺寸。<see cref="SetScale"/> 內部會呼叫。</summary>
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
            /// <summary>覆寫全域 GUI 縮放（自動 clamp 到 <see cref="MinScale"/> 以上），與目前值相同則不動作。</summary>
            public void SetScale(float iScale)
            {
                if(iScale == Scale) return;
                iScale = Mathf.Max(iScale, MinScale);//Scale must >= MinScale

                Scale = iScale;
                ApplyScale();
            }
            /// <summary>GUILayout.Box 樣式（白字、richText、wordWrap，字級依 <see cref="Scale"/> 縮放）。</summary>
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
            

            /// <summary>白字、預設字級的 Button 樣式（同 <see cref="UCL_GUIStyle.ButtonStyle"/>）。</summary>
            public GUIStyle ButtonStyle => GetButtonStyle(Color.white, DefaultFontSize);



            /// <summary>取得 / cache 指定文字色與字級的 Button 樣式（key = (Color, fontSize)）。</summary>
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
            

            /// <summary>
            /// 白字、預設字級的 Label 樣式（同 <see cref="UCL_GUIStyle.LabelStyle"/>）。
            /// ⚠ 不要當作 Toggle / Button / TextField 等互動控制項的 GUIStyle 參。
            /// </summary>
            public GUIStyle LabelStyle => GetLabelStyle(Color.white, DefaultFontSize);



            /// <summary>
            /// 取得 / cache 指定文字色與字級的 Label 樣式（key = (Color, fontSize)）。
            /// ⚠ 同 <see cref="LabelStyle"/>，不要當作互動控制項 GUIStyle 參。
            /// </summary>
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


            /// <summary>HorizontalSlider 軌道樣式（高度依 <see cref="Scale"/> 縮放）。</summary>
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
            /// <summary>HorizontalSlider 拇指（thumb）樣式（寬高依 <see cref="Scale"/> 縮放）。</summary>
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
            /// <summary>TextField 樣式（單行輸入，字級依 <see cref="Scale"/> 縮放）。</summary>
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
            /// <summary>TextArea 樣式（多行輸入，字級依 <see cref="Scale"/> 縮放）。</summary>
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
        /// 在 EditorWindow.OnGUI 取 GUIStyle 時設 true、結束時設 false（建議用 <see cref="IsInEditorWindowScope"/>）。
        /// 開啟後 <see cref="CurStyleData"/> 會切到獨立的 EditorWindow cache，避免污染 runtime 樣式。
        /// </summary>
        static public bool IsInEditorWindow = false;


        static StyleData s_Data = null;
        static StyleData s_EditorWindowData = null;
        static StyleData Data => s_Data == null? s_Data = new StyleData() : s_Data;
        static StyleData EditorWindowData => s_EditorWindowData == null ? s_EditorWindowData = new StyleData() : s_EditorWindowData;
        /// <summary>把指定尺寸乘上當前 GUI 縮放比例（DPI / 使用者設定）。</summary>
        public static int GetScaledSize(float iSize) => Mathf.RoundToInt(iSize * StyleData.Scale);
#if UNITY_EDITOR
        /// <summary>當前生效的 StyleData（依 <see cref="IsInEditorWindow"/> 自動切 EditorWindow / Runtime 兩份 cache）。</summary>
        public static StyleData CurStyleData => IsInEditorWindow ? EditorWindowData : Data;
#else
        /// <summary>當前生效的 StyleData（Runtime 版本）。</summary>
        public static StyleData CurStyleData => Data;
#endif
        /// <summary>GUILayout.Box 用樣式（白字、richText、wordWrap）。</summary>
        static public GUIStyle BoxStyle => CurStyleData.BoxStyle;

        /// <summary>GUILayout.Button 用白字標準樣式（richText 開啟）；button-like Toggle 也吃這個。</summary>
        static public GUIStyle ButtonStyle => CurStyleData.ButtonStyle;
        /// <summary>GUILayout.TextArea 用多行輸入樣式（隨 Scale 縮放字級）。</summary>
        static public GUIStyle TextAreaStyle => CurStyleData.TextAreaStyle;
        /// <summary>GUILayout.TextField 用單行輸入樣式（隨 Scale 縮放字級）。</summary>
        static public GUIStyle TextFieldStyle => CurStyleData.TextFieldStyle;

        #region ButtonText
        /// <summary>取得指定文字色 / 字級的 Button 樣式（依 (Color, fontSize) 為 key 內部 cache）。</summary>
        public static GUIStyle GetButtonStyle(Color iCol, int iFontSize = DefaultFontSize) => CurStyleData.GetButtonStyle(iCol, iFontSize);
        /// <summary>紅字 Button 樣式（危險動作 / 警示用）。</summary>
        public static GUIStyle ButtonTextRed => GetButtonStyle(Color.red);
        /// <summary>黃字 Button 樣式（提醒用）。</summary>
        public static GUIStyle ButtonTextYellow => GetButtonStyle(Color.yellow);
        /// <summary>綠字 Button 樣式（確認 / 成功動作用）。</summary>
        public static GUIStyle ButtonTextGreen => GetButtonStyle(Color.green);
        #endregion

        #region GUI

        /// <summary>繪製 Small / Medium / Big / XL 四顆按鈕，點擊切換全域 GUI 縮放比例（寫入 PlayerPrefs）。</summary>
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

        /// <summary>using 範圍內暫存並覆寫 <c>GUI.color</c>；Dispose 時還原原值。</summary>
        public class UCL_GUIColorScope : IDisposable
        {
            private Color m_PrevColor;
            public UCL_GUIColorScope(Color col) 
            {
                m_PrevColor = GUI.color;
                GUI.color = col;
                //PushGUIColor(iCol);
            }
            public void Dispose()
            {
                //PopGUIColor();
                GUI.color = m_PrevColor;
            }
        }

        /// <summary>把當前 <c>GUI.color</c> 推入 stack 並切到 <paramref name="iCol"/>；用 <see cref="PopGUIColor"/> 還原。</summary>
        public static void PushGUIColor(Color iCol)
        {
            s_ColorStack.Push(GUI.color);
            GUI.color = iCol;
        }
        /// <summary>還原最近一次 <see cref="PushGUIColor"/> 的色（stack 空時忽略）。</summary>
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
        /// <summary>
        /// 預設白色 Label 樣式。僅供純文字顯示用；
        /// ⚠ 不要當作 Toggle / Button / TextField 等互動控制項的 GUIStyle 參（會破壞視覺與點擊熱區）。
        /// </summary>
        static public GUIStyle LabelStyle => CurStyleData.LabelStyle;

        /// <summary>
        /// 取得指定文字色 / 字級的 Label 樣式（依 (Color, fontSize) 為 key 內部 cache）。
        /// ⚠ 同 <see cref="LabelStyle"/>，不要當作 Toggle / Button / TextField 的 GUIStyle 參。
        /// </summary>
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

    /// <summary>using 範圍內把 <see cref="UCL_GUIStyle.IsInEditorWindow"/> 設為指定值，Dispose 時還原（避免洩漏到後續 OnGUI）。</summary>
    public class IsInEditorWindowScope : IDisposable
    {
        public bool prev;
        public IsInEditorWindowScope(bool flag)
        {
            prev = UCL_GUIStyle.IsInEditorWindow;
            UCL_GUIStyle.IsInEditorWindow = flag;
        }


        public void Dispose()
        {
            UCL_GUIStyle.IsInEditorWindow = prev;
        }
    }
}