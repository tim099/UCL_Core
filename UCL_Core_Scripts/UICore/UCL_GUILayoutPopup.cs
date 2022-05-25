using System.Collections;
using System.Collections.Generic;
using UCL.Core.LocalizeLib;
using UnityEngine;
namespace UCL.Core.UI
{
    static public partial class UCL_GUILayout
    {
        #region Popup
        /// <summary>
        /// Show pop up
        /// </summary>
        /// <param name="iIndex"></param>
        /// <param name="iDisplayedOptions"></param>
        /// <param name="iDataDic"></param>
        /// <param name="iKey"></param>
        /// <param name="iOptions"></param>
        /// <returns></returns>
        public static int Popup(int iIndex, IList<string> iDisplayedOptions, UCL_ObjectDictionary iDataDic, string iKey, params GUILayoutOption[] iOptions)
        {
            string aShowKey = iKey + "_Show";
            bool aIsShow = iDataDic.GetData(aShowKey, false);

            iIndex = Popup(iIndex, iDisplayedOptions, ref aIsShow, iOptions);
            iDataDic.SetData(aShowKey, aIsShow);
            return iIndex;
        }


        static public GUIStyle ButtonStyle
        {
            get
            {
                if (m_Button == null)
                {
                    m_Button = new GUIStyle(GUI.skin.button);
                    m_Button.richText = true;
                    var aTextCol = Color.white;
                    m_Button.normal.textColor = aTextCol;
                    m_Button.focused.textColor = aTextCol;
                    m_Button.hover.textColor = aTextCol;
                }
                return m_Button;
            }
        }
        static GUIStyle m_Button = null;

        /// <summary>
        /// Show pop up with a search input field
        /// if iDisplayedOptions.Count >= iSearchThreshold then add search field
        /// </summary>
        /// <param name="iSelectedIndex"></param>
        /// <param name="iDisplayedOptions"></param>
        /// <param name="iDataDic"></param>
        /// <param name="iKey"></param>
        /// <param name="iSearchThreshold"></param>
        /// <param name="iOptions"></param>
        /// <returns></returns>
        public static int PopupAuto(int iSelectedIndex, IList<string> iDisplayedOptions, UCL_ObjectDictionary iDataDic, string iKey,
            int iSearchThreshold = 10, params GUILayoutOption[] iOptions)
        {
            if (iDisplayedOptions.Count >= iSearchThreshold)
            {
                return PopupSearch(iSelectedIndex, iDisplayedOptions, iDataDic, iKey, iOptions);
            }

            return Popup(iSelectedIndex, iDisplayedOptions, iDataDic, iKey, iOptions);
        }
        /// <summary>
        /// Show pop up with a search input field
        /// if iDisplayedOptions.Count >= iSearchThreshold then add search field
        /// </summary>
        /// <param name="iDisplayedOptions"></param>
        /// <param name="iDataDic"></param>
        /// <param name="iKey"></param>
        /// <param name="iSearchThreshold"></param>
        /// <param name="iOptions"></param>
        /// <returns></returns>
        public static int PopupAuto(IList<string> iDisplayedOptions, UCL_ObjectDictionary iDataDic, string iKey,
            int iSearchThreshold = 10, params GUILayoutOption[] iOptions)
        {
            string aKey = iKey + "_SelectedIndex";
            int aSelectedIndex = iDataDic.GetData(aKey, 0);
            aSelectedIndex = PopupAuto(aSelectedIndex, iDisplayedOptions, iDataDic, iKey, iSearchThreshold, iOptions);
            iDataDic.SetData(aKey, aSelectedIndex);
            return aSelectedIndex;
        }
        public static int PopupAuto(IList<string> iDisplayedOptions, int iInitIndex, UCL_ObjectDictionary iDataDic, string iKey,
            int iSearchThreshold = 10, params GUILayoutOption[] iOptions)
        {
            string aKey = iKey + "_SelectedIndex";
            int aSelectedIndex = iDataDic.GetData(aKey, iInitIndex);
            aSelectedIndex = PopupAuto(aSelectedIndex, iDisplayedOptions, iDataDic, iKey, iSearchThreshold, iOptions);
            iDataDic.SetData(aKey, aSelectedIndex);
            return aSelectedIndex;
        }

        /// <summary>
        /// Show pop up with a search input field
        /// </summary>
        /// <param name="iSelectedIndex"></param>
        /// <param name="iDisplayedOptions"></param>
        /// <param name="iDataDic"></param>
        /// <param name="iKey"></param>
        /// <param name="iOptions"></param>
        /// <returns></returns>
        public static int PopupSearch(int iSelectedIndex, IList<string> iDisplayedOptions, UCL_ObjectDictionary iDataDic, string iKey, params GUILayoutOption[] iOptions)
        {
            if (iDisplayedOptions.Count == 0)
            {
                Debug.LogError("UCL_GUILayoyt.Popup iDisplayedOptions.Count == 0");
                return 0;
            }
            if (iSelectedIndex < 0) iSelectedIndex = 0;
            if (iSelectedIndex >= iDisplayedOptions.Count) iSelectedIndex = iDisplayedOptions.Count - 1;
            string aCur = iDisplayedOptions[iSelectedIndex];

            string aShowKey = iKey + "_Show";
            bool aIsShow = iDataDic.GetData(aShowKey, false);
            if (aIsShow)//show search field
            {
                string aSearchKey = iKey + "_Search";
                string aInput = iDataDic.GetData(aSearchKey, string.Empty);

                GUILayout.BeginVertical(iOptions);

                if (GUILayout.Button(aCur, iOptions))
                {
                    aIsShow = false;
                }
                aInput = TextField(UCL_LocalizeManager.Get("Search"), aInput);
                iDataDic.SetData(aSearchKey, aInput);

                System.Text.RegularExpressions.Regex aRegex = null;
                {
                    if (!string.IsNullOrEmpty(aInput))
                    {
                        try
                        {
                            //aRegex = new System.Text.RegularExpressions.Regex(aInput.ToLower() + ".*", System.Text.RegularExpressions.RegexOptions.Compiled);
                            aRegex = new System.Text.RegularExpressions.Regex(aInput.ToLower(), System.Text.RegularExpressions.RegexOptions.Compiled);
                        }
                        catch (System.Exception iE)
                        {
                            aRegex = null;
                            Debug.LogException(iE);
                        }
                    }
                }

                //using (var aScope = new GUILayout.VerticalScope("box", iOptions))
                {
                    for (int i = 0; i < iDisplayedOptions.Count; i++)
                    {
                        var aOption = iDisplayedOptions[i];
                        if (aRegex != null && !aRegex.IsMatch(aOption.ToLower()))
                        {
                            continue;
                        }

                        string aDisplayName = aOption;
                        if (aRegex != null)
                        {
                            aDisplayName = aRegex.HightLight(aDisplayName, aInput, Color.red);
                        }


                        if (GUILayout.Button(aDisplayName, ButtonStyle, iOptions))
                        {
                            aIsShow = false;
                            iSelectedIndex = i;
                        }
                    }
                }
                GUILayout.EndVertical();
            }
            else
            {
                if (GUILayout.Button(aCur, iOptions))
                {
                    aIsShow = true;
                }
            }
            iDataDic.SetData(aShowKey, aIsShow);
            return iSelectedIndex;
        }

        /// <summary>
        /// Show pop up
        /// </summary>
        /// <param name="iSelectedIndex"></param>
        /// <param name="iDisplayedOptions"></param>
        /// <param name="iOpened"></param>
        /// <param name="iOptions"></param>
        /// <returns></returns>
        public static int Popup(int iSelectedIndex, IList<string> iDisplayedOptions, ref bool iOpened, params GUILayoutOption[] iOptions)
        {
            if (iDisplayedOptions.Count == 0)
            {
                Debug.LogError("UCL_GUILayoyt.Popup iDisplayedOptions.Count == 0");
                return 0;
            }
            if (iSelectedIndex < 0) iSelectedIndex = 0;
            if (iSelectedIndex >= iDisplayedOptions.Count) iSelectedIndex = iDisplayedOptions.Count - 1;
            string aCur = iDisplayedOptions[iSelectedIndex];
            if (iOpened)
            {
                GUILayout.BeginVertical(iOptions);
                using (var aScope = new GUILayout.VerticalScope("box", iOptions))
                {
                    if (GUILayout.Button(aCur, iOptions))
                    {
                        iOpened = false;
                    }
                }
                using (var aScope = new GUILayout.VerticalScope("box", iOptions))
                {
                    for (int i = 0; i < iDisplayedOptions.Count; i++)
                    {
                        if (GUILayout.Button(iDisplayedOptions[i], iOptions))
                        {
                            iOpened = false;
                            iSelectedIndex = i;
                            break;
                        }
                    }
                }
                GUILayout.EndVertical();
            }
            else
            {
                GUILayout.BeginVertical(iOptions);
                using (var aScope = new GUILayout.VerticalScope("box", iOptions))
                {
                    if (GUILayout.Button(aCur, iOptions))
                    {
                        iOpened = true;
                    }
                }
                GUILayout.EndVertical();
            }
            return iSelectedIndex;
        }
        /// <summary>
        /// Show enum popup
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iEnum"></param>
        /// <param name="iIsOpened"></param>
        /// <returns></returns>
        public static T Popup<T>(T iEnum, ref bool iIsOpened) where T : System.Enum
        {
            System.Type aType = iEnum.GetType();
            var aNames = System.Enum.GetNames(aType);
            int aID = aNames.GetIndex(iEnum.ToString());
            aID = Popup(aID, aNames, ref iIsOpened);
            return (T)System.Enum.Parse(aType, aNames[aID], true);
        }
        public static Color SelectColor(Color iColor)
        {
            System.Func<string, float, float> aSelectColField = (iName, iCol) =>
            {
                GUILayout.BeginHorizontal();

                iCol = GUILayout.HorizontalSlider(iCol, 0, 1, GUILayout.Width(100));
                int aIntVal = Mathf.RoundToInt(iCol * 255f);
                int aNewIntVal = IntField(iName, aIntVal, GUILayout.Width(40));
                if (aNewIntVal != aIntVal)
                {
                    if (aNewIntVal > 255) aNewIntVal = 255;
                    if (aNewIntVal < 0) aNewIntVal = 0;
                    iCol = aNewIntVal / 255f;
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                return iCol;
            };
            GUILayout.BeginVertical();

            GUILayout.BeginHorizontal();
            LabelAutoSize("●", iColor, 64);
            System.Action<Color> aSelectColButton = (iButColor) =>
            {
                if (ButtonAutoSize("■", 22, Color.gray, iButColor))
                {
                    iColor = iButColor;
                }
            };
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            aSelectColButton(Color.red);
            aSelectColButton(Color.green);
            aSelectColButton(Color.blue);
            aSelectColButton(Color.yellow);

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            aSelectColButton(Color.black);
            aSelectColButton(Color.white);
            aSelectColButton(Color.gray);
            aSelectColButton(Color.cyan);
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            iColor.r = aSelectColField("R", iColor.r);
            iColor.g = aSelectColField("G", iColor.g);
            iColor.b = aSelectColField("B", iColor.b);
            iColor.a = aSelectColField("A", iColor.a);
            GUILayout.EndVertical();
            return iColor;
        }
        /// <summary>
        /// Show enum popup
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iEnum"></param>
        /// <param name="iIsOpened"></param>
        /// <returns></returns>
        public static T Popup<T>(T iEnum, UCL_ObjectDictionary iDataDic, System.Func<T, string> iGetNameFunc = null, params GUILayoutOption[] iOptions) where T : System.Enum
        {
            System.Type aType = iEnum.GetType();
            string[] aNames = System.Enum.GetNames(aType);
            var aValues = System.Enum.GetValues(typeof(T));
            int aID = 0;
            for (int i = 0; i < aValues.Length; i++)
            {
                if (((T)aValues.GetValue(i)).Equals(iEnum))
                {
                    aID = i;
                    break;
                }
            }
            if (iGetNameFunc != null)
            {
                for (int i = 0; i < aNames.Length; i++)
                {
                    aNames[i] = iGetNameFunc((T)(aValues.GetValue(i)));
                }
            }

            bool aIsOpened = iDataDic.GetData("IsOpened", false);
            aID = Popup(aID, aNames, ref aIsOpened, iOptions);
            iDataDic.SetData("IsOpened", aIsOpened);
            return (T)aValues.GetValue(aID);
        }
        /// <summary>
        /// Show enum popup
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iEnum"></param>
        /// <param name="iIsOpened"></param>
        /// <returns></returns>
        public static T PopupAuto<T>(T iEnum, System.Func<string, string> iGetDisplayName, UCL_ObjectDictionary iDataDic,
            int iSearchThreshold = 10, params GUILayoutOption[] iOptions) where T : System.Enum
        {
            System.Type aType = iEnum.GetType();
            var aNames = System.Enum.GetNames(aType);
            var aDisplayNames = new string[aNames.Length];
            for (int i = 0; i < aNames.Length; i++)
            {
                aDisplayNames[i] = iGetDisplayName(aNames[i]);
            }

            int aID = PopupAuto(aDisplayNames, aNames.GetIndex(iEnum.ToString()), iDataDic, "Popup", iSearchThreshold);

            T aRes = (T)System.Enum.Parse(aType, aNames[aID], true);
            return aRes;
        }
        #endregion
    }
}