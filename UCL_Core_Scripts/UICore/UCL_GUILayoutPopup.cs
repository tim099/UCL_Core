using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
        /// <param name="iSelectedIndex"></param>
        /// <param name="iDisplayedOptions"></param>
        /// <param name="iDataDic"></param>
        /// <param name="iKey"></param>
        /// <param name="iSearchThreshold"></param>
        /// <param name="iOptions"></param>
        /// <returns></returns>
        public static string PopupAuto(string iCurID, IList<string> iDisplayedOptions, UCL_ObjectDictionary iDataDic, string iKey,
            int iSearchThreshold = 10, params GUILayoutOption[] iOptions)
        {
            if (iDisplayedOptions.IsNullOrEmpty())
            {
                return iCurID;
            }
            int aIndex = iDisplayedOptions.IndexOf(iCurID);
            int aResultID;
            if (iDisplayedOptions.Count >= iSearchThreshold)
            {
                aResultID = PopupSearch(aIndex, iDisplayedOptions, iDataDic, iKey, iOptions);
            }
            else
            {
                aResultID = Popup(aIndex, iDisplayedOptions, iDataDic, iKey, iOptions);
            }
            if (aResultID < 0 || aResultID >= iDisplayedOptions.Count)
            {
                return iCurID;
            }
            return iDisplayedOptions[aResultID];
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
        /// <summary>
        /// return cur page index(start from 0)
        /// </summary>
        /// <param name="iDataDic"></param>
        /// <param name="itemsCount"></param>
        /// <param name="maxItemsPerPage"></param>
        /// <returns></returns>
        public static (int pageIndex, int startIndex) DrawSelectPage(UCL_ObjectDictionary iDataDic, int itemsCount, int maxItemsPerPage)
        {
            int pageCount = 1;
            if (itemsCount > maxItemsPerPage)
            {
                pageCount = 1 + ((itemsCount - 1) / maxItemsPerPage);
            }
            int state = 0;
            int curPage = iDataDic.GetData(nameof(curPage), 0);
            if (curPage >= pageCount) curPage = pageCount - 1;
            if (curPage < 0) curPage = 0;

            int startIndex = curPage * maxItemsPerPage;
            int lastIndex = startIndex + maxItemsPerPage;
            if (lastIndex > itemsCount)
            {
                lastIndex = itemsCount;
            }
            if(pageCount <= 1)
            {
                return (0, 0);
            }

            GUILayout.BeginHorizontal();
            //GUILayout.FlexibleSpace();
            float space = UCL_GUIStyle.GetScaledSize(2);

            if (GUILayout.Button("|<", UCL_GUIStyle.GetButtonStyle(Color.white), GUILayout.ExpandWidth(false)))
            {
                state = -2;//first page
            }
            GUILayout.Space(space);
            if (GUILayout.Button(" < ",
                UCL_GUIStyle.GetButtonStyle(Color.white), GUILayout.ExpandWidth(false)))
            {
                state = -1;//prev page
            }
            //GUILayout.Space(space);
            if (pageCount < 10)
            {
                GUILayout.Box($"{(curPage + 1)} / {pageCount}", UCL_GUIStyle.BoxStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50)));
            }
            else
            {
                int len = Mathf.CeilToInt(Mathf.Log10((int)pageCount));
                int width = 30 + 10 * len;
                float size = UCL_GUIStyle.GetScaledSize(width);
                curPage = UCL_GUILayout.IntFieldAuto(curPage + 1, iDataDic.GetSubDic("PageInput"), GUILayout.Width(size)) - 1;

                GUILayout.Box($"/{pageCount}", UCL_GUIStyle.BoxStyle, GUILayout.Width(size));
                //GUILayout.Box($"{(curPage + 1)} / {pageCount}", UCL_GUIStyle.BoxStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(width)));
            }

            //GUILayout.Label($"{(curPage + 1)} / {pageCount}", UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(50)));
            if (GUILayout.Button(" > ",
                UCL_GUIStyle.GetButtonStyle(Color.white), GUILayout.ExpandWidth(false)))
            {
                state = 1;//next page
            }
            GUILayout.Space(space);
            if (GUILayout.Button(">|", UCL_GUIStyle.GetButtonStyle(Color.white), GUILayout.ExpandWidth(false)))
            {
                state = 2;//lase page
            }
            //GUILayout.Space(space);
            //GUILayout.Label($"{startIndex + 1} ~ {lastIndex}", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            int newPage = curPage;
            if (state != 0)
            {

                switch (state)
                {
                    case -1:
                        {
                            if (newPage <= 0)
                            {
                                newPage = pageCount - 1;
                            }
                            else
                            {
                                newPage--;
                            }
                            break;
                        }
                    case 1:
                        {
                            if (newPage >= pageCount - 1)
                            {
                                newPage = 0;
                            }
                            else
                            {
                                newPage++;
                            }
                            break;
                        }
                    case -2:
                        {
                            newPage = 0;
                            break;
                        }
                    case 2:
                        {
                            newPage = pageCount - 1;
                            break;
                        }
                }
            }
            iDataDic.SetData(nameof(curPage), newPage);

            return (curPage,curPage * maxItemsPerPage);
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

                if (GUILayout.Button(aCur, UCL_GUIStyle.ButtonStyle, iOptions))
                {
                    aIsShow = false;
                }
                GUILayout.BeginHorizontal(iOptions);
                GUILayout.Label(UCL_LocalizeManager.Get("Search"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

                aInput = GUILayout.TextField(aInput, UCL_GUIStyle.TextFieldStyle);//TextField(UCL_LocalizeManager.Get("Search"), aInput);
                GUILayout.EndHorizontal();

                iDataDic.SetData(aSearchKey, aInput);

                Regex aRegex = null;
                {
                    if (!string.IsNullOrEmpty(aInput))
                    {
                        try
                        {
                            //aRegex = new System.Text.RegularExpressions.Regex(aInput.ToLower() + ".*", System.Text.RegularExpressions.RegexOptions.Compiled);
                            aRegex = new Regex(aInput, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                        }
                        catch (System.Exception iE)
                        {
                            aRegex = null;
                            Debug.LogException(iE);
                        }
                    }
                }
                var aIDs = iDisplayedOptions;
                //aRegex != null && !aRegex.IsMatch(aOption)
                if (aRegex != null)
                {
                    aIDs = iDisplayedOptions.Where(option => aRegex.IsMatch(option)).ToList();
                }
                const int MaxItemsPerPage = 20;
                int itemCount = aIDs.Count;
                var result = DrawSelectPage(iDataDic.GetSubDic(nameof(DrawSelectPage)), itemCount, MaxItemsPerPage);
                int startIndex = result.startIndex;
                int lastIndex = Mathf.Min(itemCount, startIndex + MaxItemsPerPage);


                //index of current display option
                int index = 0;
                //using (var aScope = new GUILayout.VerticalScope("box", iOptions))
                {
                    for (int i = 0; i < iDisplayedOptions.Count; i++)
                    {
                        var aOption = iDisplayedOptions[i];
                        if (aRegex != null && !aRegex.IsMatch(aOption))
                        {
                            continue;
                        }
                        if (index >= lastIndex)
                        {
                            break;
                        }
                        if (index++ < startIndex)
                        {
                            continue;
                        }

                        string aDisplayName = aOption;
                        if (aRegex != null)
                        {
                            aDisplayName = aRegex.HightLight(aDisplayName, aInput, Color.red);
                        }

                        //Assertion failed on expression: '!(o->TestHideFlag(Object::kDontSaveInEditor) && (options & kAllowDontSaveObjectsToBePersistent) == 0)'
                        //UnityEngine.GUILayout:Button(string, UnityEngine.GUIStyle, UnityEngine.GUILayoutOption[])
                        if (GUILayout.Button(aDisplayName, UI.UCL_GUIStyle.ButtonStyle, iOptions))
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
                if (GUILayout.Button(aCur, UCL_GUIStyle.ButtonStyle, iOptions))
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
            if (iDisplayedOptions.IsNullOrEmpty())
            {
                Debug.LogError("UCL_GUILayoyt.Popup iDisplayedOptions.IsNullOrEmpty()");
                return 0;
            }
            if (iSelectedIndex < 0) iSelectedIndex = 0;
            if (iSelectedIndex >= iDisplayedOptions.Count) iSelectedIndex = iDisplayedOptions.Count - 1;
            string aCur = iDisplayedOptions[iSelectedIndex];
            GUILayout.BeginVertical(iOptions);
            if (iOpened)
            {    
                //using (var aScope = new GUILayout.VerticalScope(iOptions))
                {
                    if (GUILayout.Button(aCur, UCL_GUIStyle.ButtonStyle, iOptions))
                    {
                        iOpened = false;
                    }
                    for (int i = 0; i < iDisplayedOptions.Count; i++)
                    {
                        if (GUILayout.Button(iDisplayedOptions[i], UCL_GUIStyle.ButtonStyle, iOptions))
                        {
                            iOpened = false;
                            iSelectedIndex = i;
                            break;
                        }
                    }
                }
            }
            else
            {
                //using (var aScope = new GUILayout.VerticalScope(iOptions))
                {
                    if (GUILayout.Button(aCur, UCL_GUIStyle.ButtonStyle, iOptions))
                    {
                        iOpened = true;
                    }
                }
            }
            GUILayout.EndVertical();
            return iSelectedIndex;
        }
        /// <summary>
        /// Show enum popup
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iEnum"></param>
        /// <returns></returns>
        public static T PopupAuto<T>(T iEnum, UCL_ObjectDictionary iDataDic, string iKey,
            int iSearchThreshold = 10, params GUILayoutOption[] iOptions) where T : System.Enum
        {
            System.Type aType = iEnum.GetType();
            string[] aNames = System.Enum.GetNames(aType);
            string[] aDisplayNames = new string[aNames.Length];
            string aTypeName = aType.Name;
            for (int i = 0; i < aNames.Length; i++)
            {
                aDisplayNames[i] = UCL_LocalizeLib.GetEnumLocalize(aTypeName, aNames[i]);
            }
            int aID = aNames.GetIndex(iEnum.ToString());
            aID = PopupAuto(aID, aDisplayNames, iDataDic, iKey, iSearchThreshold, iOptions);
            return (T)System.Enum.Parse(aType, aNames[aID], true);
        }
        /// <summary>
        /// Show enum popup
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iEnum"></param>
        /// <returns></returns>
        public static T PopupAuto<T>(T iEnum, IList<T> iEnums, UCL_ObjectDictionary iDataDic,  string iKey,
            int iSearchThreshold = 10, params GUILayoutOption[] iOptions) where T : System.Enum
        {
            System.Type aType = iEnum.GetType();
            string[] aNames = new string[iEnums.Count];
            for (int i = 0; i < iEnums.Count; i++)
            {
                aNames[i] = iEnums[i].ToString();
            }
            string[] aDisplayNames = new string[iEnums.Count];
            string aTypeName = aType.Name;
            for (int i = 0; i < iEnums.Count; i++)
            {
                aDisplayNames[i] = UCL_LocalizeLib.GetEnumLocalize(aTypeName, aNames[i]);
            }
            int aID = aNames.GetIndex(iEnum.ToString());
            aID = PopupAuto(aID, aDisplayNames, iDataDic, iKey, iSearchThreshold, iOptions);
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
        public static T PopupAuto<T>(T iEnum, UCL_ObjectDictionary iDataDic,
            int iSearchThreshold = 10, params GUILayoutOption[] iOptions) where T : System.Enum
        {
            System.Type aType = iEnum.GetType();
            var aEnums = System.Enum.GetValues(aType);
            var aDisplayNames = new string[aEnums.Length];
            for (int i = 0; i < aEnums.Length; i++)
            {
                aDisplayNames[i] = ((System.Enum)aEnums.GetValue(i)).GetLocalizeEnumName();
            }
            int aID = PopupAuto(aEnums.GetArrayIndex(iEnum), aDisplayNames, iDataDic, "Popup", iSearchThreshold, iOptions);
            //int aID = PopupAuto(aDisplayNames, aEnums.GetArrayIndex(iEnum), iDataDic, "Popup", iSearchThreshold, iOptions);

            //T aRes = (T)System.Enum.Parse(aType, aNames[aID], true);
            return (T)aEnums.GetValue(aID);
        }


        /// <summary>
        /// cache version of PopupSearch(Performance-Optimized Version)
        /// </summary>
        /// <param name="iIndex"></param>
        /// <param name="iDisplayOptions"></param>
        /// <param name="iDataDic"></param>
        /// <param name="iKey"></param>
        /// <param name="iOptions"></param>
        /// <returns></returns>
        public static int PopupSearchCache(int iIndex, IList<string> iDisplayOptions, UCL_ObjectDictionary iDataDic, string iKey, params GUILayoutOption[] iOptions)
        {
            if (iDisplayOptions.Count == 0)
            {
                Debug.LogError($"{nameof(UCL_GUILayout)}.{nameof(PopupSearchCache)} iDisplayedOptions.Count == 0");
                return 0;
            }
            var dic = iDataDic.GetSubDic(iKey);
            bool clearCache = false;
            int count = dic.GetData(nameof(count), -1);
            if(count != iDisplayOptions.Count)
            {
                count = iDisplayOptions.Count;
                clearCache = true;
                dic.SetData(nameof(count), count);
            }
            if (iIndex < 0) iIndex = 0;
            if (iIndex >= count) iIndex = count - 1;

            string curOption = iDisplayOptions[iIndex];

            bool aIsShow = dic.GetData(nameof(aIsShow), false);
            if (aIsShow)//show search field
            {
                const string SearchKey = "Search";
                string input = iDataDic.GetData(SearchKey, string.Empty);

                GUILayout.BeginVertical(iOptions);

                if (GUILayout.Button(curOption, UCL_GUIStyle.ButtonStyle, iOptions))
                {
                    aIsShow = false;
                }
                GUILayout.BeginHorizontal(iOptions);
                GUILayout.Label(UCL_LocalizeManager.Get("Search"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

                var newInput = GUILayout.TextField(input, UCL_GUIStyle.TextFieldStyle);//TextField(UCL_LocalizeManager.Get("Search"), aInput);
                if(newInput != input)
                {
                    clearCache = true;
                    input = newInput;
                }
                GUILayout.EndHorizontal();

                iDataDic.SetData(SearchKey, input);

                Regex regex = null;
                {

                    if (!string.IsNullOrEmpty(input))
                    {
                        string key = nameof(regex);
                        if (clearCache)
                        {
                            dic.Remove(key);
                        }
                        if (!dic.ContainsKey(key))
                        {
                            try
                            {
                                //aRegex = new System.Text.RegularExpressions.Regex(aInput.ToLower() + ".*", System.Text.RegularExpressions.RegexOptions.Compiled);
                                regex = new Regex(input, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                                dic.Add(key, regex);
                            }
                            catch (System.Exception iE)
                            {
                                regex = null;
                                Debug.LogException(iE);
                            }
                        }
                        else//use cache
                        {
                            regex = dic.GetData(key, regex);
                        }
                        
                    }
                }

                var aIDs = iDisplayOptions;
                //aRegex != null && !aRegex.IsMatch(aOption)

                Dictionary<int, int> indexMapping = null;

                if (regex != null)
                {
                    string key = nameof(aIDs);
                    if (clearCache)
                    {
                        dic.Remove(key);
                        dic.Remove(nameof(indexMapping));
                    }
                    if (!dic.ContainsKey(key))
                    {
                        try
                        {
                            indexMapping = new();
                            List<string> options = new();
                            for (int i = 0; i < iDisplayOptions.Count; i++)
                            {
                                var id = iDisplayOptions[i];
                                if (regex.IsMatch(id))
                                {
                                    indexMapping[options.Count] = i;
                                    options.Add(id);
                                }
                            }
                            //var options = iDisplayOptions.Where(option => !string.IsNullOrEmpty(option) && regex.IsMatch(option)).ToList();
                            aIDs = options;

                            dic.Add(key, aIDs);
                            dic.Add(nameof(indexMapping), indexMapping);
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogException(e);
                        }
                    }
                    else//use cache
                    {
                        aIDs = dic.GetData(key, aIDs);
                        indexMapping = dic.GetData(nameof(indexMapping), indexMapping);
                    }

                }
                const int MaxItemsPerPage = 20;
                int itemCount = aIDs.Count;
                var result = DrawSelectPage(iDataDic.GetSubDic(nameof(DrawSelectPage)), itemCount, MaxItemsPerPage);
                int startIndex = result.startIndex;
                int lastIndex = Mathf.Min(itemCount, startIndex + MaxItemsPerPage);
                //var pageOptions = iDisplayOptions;
                //if(pagec)

                //index of current display option
                //using (var aScope = new GUILayout.VerticalScope("box", iOptions))
                {
                    for (int i = 0; i < aIDs.Count; i++)
                    {
                        if (i >= lastIndex)
                        {
                            break;
                        }
                        if (i < startIndex)
                        {
                            continue;
                        }
                        var aOption = aIDs[i];
                        string aDisplayName = aOption;
                        if (regex != null)
                        {
                            aDisplayName = regex.HightLight(aDisplayName, input, Color.red);
                        }

                        //Assertion failed on expression: '!(o->TestHideFlag(Object::kDontSaveInEditor) && (options & kAllowDontSaveObjectsToBePersistent) == 0)'
                        //UnityEngine.GUILayout:Button(string, UnityEngine.GUIStyle, UnityEngine.GUILayoutOption[])
                        if (GUILayout.Button(aDisplayName, UI.UCL_GUIStyle.ButtonStyle, iOptions))
                        {
                            aIsShow = false;
                            
                            if(indexMapping != null)
                            {
                                iIndex = indexMapping[i];
                            }
                            else
                            {
                                iIndex = i;
                            }
                        }

                    }
                }
                GUILayout.EndVertical();
            }
            else
            {
                if (GUILayout.Button(curOption, UCL_GUIStyle.ButtonStyle, iOptions))
                {
                    aIsShow = true;
                }
            }
            dic.SetData(nameof(aIsShow), aIsShow);
            return iIndex;
        }

        #region PopupGrouped
        /// <summary>
        /// 分組版下拉選單（PopupSearchCache 的分組擴充, 2026-07-28 Tim 拍板規格）。
        /// 職責：依「分隔符前綴」自動把選項摺成分組（例 A_01/A_02/B_01/C → A、B、Other 三組），
        ///       展開面板內嵌一列分組切換（All(預設)/各組/Other），選組後只列該組選項；搜尋欄照常可用（先組過濾再搜尋）。
        /// 規格拍板：
        ///   - 單層分組：取「第一個分隔符前」的字串當組名（A_B_01 → A 組）；無分隔符 → Other（未分組）
        ///   - 全部同組（含全部未分組）→ 隱藏分組列, 直接套用原版 PopupAuto 行為
        ///   - 無 Other 內容則省略 Other 選項；選組後顯示全名（不去前綴）；標籤固定英文 All / Other（不 localize）
        /// 數值影響：回傳值恆為「原始 iDisplayOptions 的 index」（組過濾與搜尋過濾經 indexMapping 雙層映回）。
        /// </summary>
        /// <param name="iIndex">當前選中項於 iDisplayOptions 的 index</param>
        /// <param name="iDisplayOptions">全部選項</param>
        /// <param name="iDataDic">GUI 狀態快取容器</param>
        /// <param name="iKey">快取 key（同一容器多個下拉時區分用）</param>
        /// <param name="iSeparator">分組分隔符（預設 "_"；動畫路徑類選項可傳 "/"）</param>
        /// <param name="iSearchThreshold">退化為原版 PopupAuto 時的搜尋欄門檻（僅單組退化路徑使用）</param>
        public static int PopupGrouped(int iIndex, IList<string> iDisplayOptions, UCL_ObjectDictionary iDataDic, string iKey,
            string iSeparator = "_", int iSearchThreshold = 10, string iDefaultGroup = null, params GUILayoutOption[] iOptions)
        {
            if (iDisplayOptions.Count == 0)
            {
                Debug.LogError($"{nameof(UCL_GUILayout)}.{nameof(PopupGrouped)} iDisplayOptions.Count == 0");
                return 0;
            }
            // 分組標籤（拍板：固定英文, 不走 localize）
            const string AllGroup = "All";
            const string OtherGroup = "Other";

            var dic = iDataDic.GetSubDic(iKey);
            bool clearCache = false;

            // --- 區塊職責：分組推導（快取, **選項內容**變動時重建）---
            // 物理意義：每個選項的組名 = 第一個分隔符「前」的字串（單層分組）；
            //           無分隔符、或分隔符在字首（組名為空）→ 歸入 Other（未分組）。
            // 數值影響：groupNames 為組序清單（首次出現順序, Other 恆排最後）; optionGroups[i] = 第 i 個選項的組名。
            // 🩸 2026-09-01：失效判準由「數量」改成「內容 hash」，且**移到展開判斷之外**。
            //   舊版兩個問題疊在一起才會現形（實測：新增 / 刪除 UCL_Asset 後 ID 下拉永遠不更新）：
            //   ① 只比 count ⇒ 同時增刪、改名、內容換人而個數不變時完全看不出來。
            //   ② clearCache 在這裡算好，卻**只在 `if (aIsShow)` 展開分支裡被消費** ——
            //      下拉收合時清單變動的話，那一幀 count 被更新成新值、clearCache 卻沒有人用，
            //      下一幀 count 已經相等 ⇒ 失效訊號**永久遺失**，`dic["aIDs"]` 那份舊清單再也不會被清。
            //   ⇒ 一般形：**在 A 處計算、只在 B 分支消費的失效旗標，B 沒跑到時訊號就沒了**。
            //     症狀是「快取永遠不更新」而不是「錯了一次」—— 後者會被回報，前者會被當成規格。
            //   📌 內容 hash 的寫法沿用同檔 ValueDropdown（GetListHashCode），不另造第二套。
            int aOptionsHash = iDisplayOptions.GetListHashCode();
            int count = dic.GetData(nameof(count), -1);
            List<string> groupNames = dic.GetData<List<string>>(nameof(groupNames), null);
            string[] optionGroups = dic.GetData<string[]>(nameof(optionGroups), null);
            if (dic.GetData(nameof(aOptionsHash), 0) != aOptionsHash || groupNames == null || optionGroups == null)
            {
                count = iDisplayOptions.Count;
                dic.SetData(nameof(count), count);
                dic.SetData(nameof(aOptionsHash), aOptionsHash);
                clearCache = true;

                // 清單換了 ⇒ 過濾結果與搜尋 regex 一律作廢。**在這裡清而不是等展開時清** ——
                // 收合狀態下也會走到這裡，這正是舊版把失效訊號弄丟的那一格。
                dic.Remove("aIDs");
                dic.Remove("indexMapping");
                dic.Remove("regex");

                groupNames = new List<string>();
                optionGroups = new string[count];
                bool aHasOther = false;
                for (int i = 0; i < count; i++)
                {
                    string aOpt = iDisplayOptions[i] ?? string.Empty;
                    int aSep = string.IsNullOrEmpty(iSeparator) ? -1 : aOpt.IndexOf(iSeparator, System.StringComparison.Ordinal);
                    string aGroup = aSep > 0 ? aOpt.Substring(0, aSep) : OtherGroup;
                    if (aGroup == OtherGroup) aHasOther = true;
                    else if (!groupNames.Contains(aGroup)) groupNames.Add(aGroup);
                    optionGroups[i] = aGroup;
                }
                if (aHasOther) groupNames.Add(OtherGroup); // 無未分組內容則省略 Other 選項（拍板）
                dic.SetData(nameof(groupNames), groupNames);
                dic.SetData(nameof(optionGroups), optionGroups);
            }

            // --- 退化路徑（拍板 問題1）：全部同一分組（含全部未分組）→ 隱藏分組列, 套用原版 ---
            if (groupNames.Count <= 1)
            {
                return PopupAuto(iIndex, iDisplayOptions, iDataDic, iKey + "_Plain", iSearchThreshold, iOptions);
            }

            if (iIndex < 0) iIndex = 0;
            if (iIndex >= count) iIndex = count - 1;
            string curOption = iDisplayOptions[iIndex];

            bool aIsShow = dic.GetData(nameof(aIsShow), false);
            if (aIsShow)//展開面板：收合鈕 + 分組列 + 搜尋列 + 分頁選項清單
            {
                GUILayout.BeginVertical(iOptions);

                if (GUILayout.Button(curOption, UCL_GUIStyle.ButtonStyle, iOptions))
                {
                    aIsShow = false;
                }

                // --- 區塊職責：分組切換列（嵌入面板內的下拉, 拍板規格）---
                // 數值影響：groupSel 存於本 key 的 subdic（per-下拉記憶）; 換組觸發 clearCache 重建過濾快取。
                // iDefaultGroup：**僅作為首次開啟時的預設選取組**（之後以使用者手選的 groupSel 為準）；
                //               指定的組名不存在於當前選項時退回 All, 避免過濾出空清單。
                string aInitGroup = AllGroup;
                if (!string.IsNullOrEmpty(iDefaultGroup) && groupNames.Contains(iDefaultGroup))
                {
                    aInitGroup = iDefaultGroup;
                }
                string groupSel = dic.GetData(nameof(groupSel), aInitGroup);
                {
                    var aGroupOptions = new List<string>(groupNames.Count + 1) { AllGroup };
                    aGroupOptions.AddRange(groupNames);
                    int aGroupIdx = Mathf.Max(0, aGroupOptions.IndexOf(groupSel));
                    GUILayout.BeginHorizontal(iOptions);
                    GUILayout.Label("Group", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    int aGroupNew = Popup(aGroupIdx, aGroupOptions, dic.GetSubDic("GroupPopup"), "Sel");
                    GUILayout.EndHorizontal();
                    if (aGroupNew != aGroupIdx && aGroupNew >= 0 && aGroupNew < aGroupOptions.Count)
                    {
                        groupSel = aGroupOptions[aGroupNew];
                        dic.SetData(nameof(groupSel), groupSel);
                        clearCache = true;
                    }
                }

                // --- 搜尋列（同 PopupSearchCache; 搜尋輸入存本 key 的 subdic, 不跨下拉共用）---
                const string SearchKey = "Search";
                string input = dic.GetData(SearchKey, string.Empty);
                GUILayout.BeginHorizontal(iOptions);
                GUILayout.Label(UCL_LocalizeManager.Get("Search"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                var newInput = GUILayout.TextField(input, UCL_GUIStyle.TextFieldStyle);
                if (newInput != input)
                {
                    clearCache = true;
                    input = newInput;
                }
                GUILayout.EndHorizontal();
                dic.SetData(SearchKey, input);

                Regex regex = null;
                if (!string.IsNullOrEmpty(input))
                {
                    string key = nameof(regex);
                    if (clearCache)
                    {
                        dic.Remove(key);
                    }
                    if (!dic.ContainsKey(key))
                    {
                        try
                        {
                            regex = new Regex(input, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                            dic.Add(key, regex);
                        }
                        catch (System.Exception iE)
                        {
                            regex = null;
                            Debug.LogException(iE);
                        }
                    }
                    else//use cache
                    {
                        regex = dic.GetData(key, regex);
                    }
                }

                // --- 區塊職責：組過濾 + 搜尋過濾（快取, 雙層 indexMapping 合成映回原始 index）---
                // 計算邏輯：先依 groupSel 過濾（All = 不過濾）, 再依 regex 過濾; 每層都維護「顯示序 → 原始 index」映射。
                IList<string> aIDs = iDisplayOptions;
                Dictionary<int, int> indexMapping = null;
                // ⭐ 沒有組過濾也沒有搜尋 ⇒ **不吃快取，直接用當幀傳進來的清單**（對齊舊版 PopupSearchCache）。
                //   舊版在常態下根本沒有清單快取（`if (regex != null)` 才進快取），所以它永遠是最新的；
                //   分組版把它改成無條件快取，於是「常態」從沒有快取變成一定有快取 —— 那才是回歸的來源。
                //   ⇒ 這裡把常態還原成沒有快取：快取只服務真的需要它的那兩種情況（選了組 / 打了搜尋字）。
                bool aNeedFilter = groupSel != AllGroup || regex != null;
                if (!aNeedFilter)
                {
                    dic.Remove(nameof(aIDs));
                    dic.Remove(nameof(indexMapping));
                }
                else
                {
                    string key = nameof(aIDs);
                    if (clearCache)
                    {
                        dic.Remove(key);
                        dic.Remove(nameof(indexMapping));
                    }
                    if (!dic.ContainsKey(key))
                    {
                        var aFiltered = new List<string>();
                        var aMapping = new Dictionary<int, int>();
                        for (int i = 0; i < iDisplayOptions.Count; i++)
                        {
                            if (groupSel != AllGroup && optionGroups[i] != groupSel) continue; // 組過濾
                            var aID = iDisplayOptions[i];
                            if (regex != null && !regex.IsMatch(aID)) continue; // 搜尋過濾
                            aMapping[aFiltered.Count] = i;
                            aFiltered.Add(aID);
                        }
                        aIDs = aFiltered;
                        indexMapping = aMapping;
                        dic.Add(key, aIDs);
                        dic.Add(nameof(indexMapping), indexMapping);
                    }
                    else//use cache
                    {
                        aIDs = dic.GetData(key, aIDs);
                        indexMapping = dic.GetData(nameof(indexMapping), indexMapping);
                    }
                }

                // --- 分頁 + 選項按鈕（同 PopupSearchCache; 選中經 indexMapping 映回原始 index）---
                const int MaxItemsPerPage = 20;
                int itemCount = aIDs.Count;
                var result = DrawSelectPage(dic.GetSubDic(nameof(DrawSelectPage)), itemCount, MaxItemsPerPage);
                int startIndex = result.startIndex;
                int lastIndex = Mathf.Min(itemCount, startIndex + MaxItemsPerPage);
                for (int i = 0; i < aIDs.Count; i++)
                {
                    if (i >= lastIndex)
                    {
                        break;
                    }
                    if (i < startIndex)
                    {
                        continue;
                    }
                    var aOption = aIDs[i];
                    string aDisplayName = aOption; // 顯示全名（拍板 問題2: 不去前綴）
                    if (regex != null)
                    {
                        aDisplayName = regex.HightLight(aDisplayName, input, Color.red);
                    }
                    if (GUILayout.Button(aDisplayName, UI.UCL_GUIStyle.ButtonStyle, iOptions))
                    {
                        aIsShow = false;
                        if (indexMapping != null && indexMapping.ContainsKey(i))
                        {
                            iIndex = indexMapping[i];
                        }
                        else
                        {
                            iIndex = i;
                        }
                    }
                }
                GUILayout.EndVertical();
            }
            else
            {
                if (GUILayout.Button(curOption, UCL_GUIStyle.ButtonStyle, iOptions))
                {
                    aIsShow = true;
                }
            }
            dic.SetData(nameof(aIsShow), aIsShow);
            return iIndex;
        }
        #endregion
        #endregion


        #region ValueDropdown
        public static int ValueDropdown(int iSelectedIndex, IList<string> iDisplayedOptions, UCL_ObjectDictionary iDataDic, string iKey,
            params GUILayoutOption[] iOptions)
        {
            if (iDisplayedOptions.Count == 0)
            {
                Debug.LogError("UCL_GUILayoyt.ValueDropdown iDisplayedOptions.Count == 0");
                return 0;
            }
            if (iSelectedIndex < 0) iSelectedIndex = 0;
            if (iSelectedIndex >= iDisplayedOptions.Count) iSelectedIndex = iDisplayedOptions.Count - 1;

            string curOption = iDisplayedOptions[iSelectedIndex];
            


            var dic = iDataDic.GetSubDic(iKey);
            int hash = iDisplayedOptions.GetListHashCode();
            if(dic.GetData("OptionsHash", 0) != hash)//list changed
            {
                dic.Clear();
                dic.SetData("OptionsHash", hash);
                //Debug.LogWarning($"ValueDropdown hash:{hash}");
            }
            const string ShowKey = "Show";
            bool aIsShow = dic.GetData(ShowKey, false);
            if (aIsShow)//show search field
            {


                GUILayout.BeginVertical(iOptions);

                if (GUILayout.Button(curOption, UCL_GUIStyle.ButtonStyle, iOptions))
                {
                    aIsShow = false;
                }
                GUILayout.BeginHorizontal(iOptions);
                GUILayout.Label(UCL_LocalizeManager.Get("Search"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

                string searchInput = dic.GetData(nameof(searchInput), string.Empty);
                string newSearchInput = GUILayout.TextField(searchInput, UCL_GUIStyle.TextFieldStyle);
                

                IList<string> ids = iDisplayedOptions;
                Regex regex = null;
                Dictionary<int, int> indexMapping = null;

                if (searchInput != newSearchInput)
                {
                    searchInput = newSearchInput;
                    //Refresh cache
                    dic.Remove(nameof(ids));
                    dic.Remove(nameof(regex));
                    dic.Remove(nameof(indexMapping));
                    dic.SetData(nameof(searchInput), searchInput);
                }

                GUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(searchInput))
                {
                    if (!dic.ContainsKey(nameof(ids)))
                    {
                        //Debug.LogWarning($"ValueDropdown Refresh, searchInput:{searchInput}");
                        try
                        {
                            //aRegex = new System.Text.RegularExpressions.Regex(aInput.ToLower() + ".*", System.Text.RegularExpressions.RegexOptions.Compiled);
                            regex = new Regex(searchInput, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                        }
                        catch (System.Exception iE)
                        {
                            regex = null;
                            Debug.LogException(iE);
                        }

                        //aRegex != null && !aRegex.IsMatch(aOption)
                        if (regex != null)
                        {
                            ids = new List<string>();
                            indexMapping = new();
                            for (int i = 0; i < iDisplayedOptions.Count; i++)
                            {
                                string option = iDisplayedOptions[i];
                                if (regex.IsMatch(option))
                                {
                                    indexMapping[ids.Count] = i;
                                    ids.Add(option);
                                }
                            }

                            //filterResult = iDisplayedOptions.Where(option => regex.IsMatch(option)).ToList();
                        }
                        dic.SetData(nameof(ids), ids);
                        dic.SetData(nameof(regex), regex);
                        dic.SetData(nameof(indexMapping), indexMapping);
                    }
                    else
                    {
                        ids = dic.GetData<IList<string>>(nameof(ids));
                        regex = dic.GetData<Regex>(nameof(regex));
                        indexMapping = dic.GetData<Dictionary<int, int>>(nameof(indexMapping));
                    }
                    

                }

                const int MaxItemsPerPage = 10;
                int itemCount = ids.Count;
                var result = DrawSelectPage(dic.GetSubDic(nameof(DrawSelectPage)), itemCount, MaxItemsPerPage);
                int startIndex = result.startIndex;
                int lastIndex = Mathf.Min(itemCount, startIndex + MaxItemsPerPage);

                for (int i = startIndex; i < lastIndex; i++)
                {
                    var option = ids[i];

                    string displayName = option;
                    if (regex != null)
                    {
                        displayName = regex.HightLight(displayName, searchInput, Color.red);
                    }
                    if (GUILayout.Button(displayName, UI.UCL_GUIStyle.ButtonStyle, iOptions))
                    {
                        aIsShow = false;
                        if(indexMapping != null)
                        {
                            iSelectedIndex = indexMapping[i];
                        }
                        else
                        {
                            iSelectedIndex = i;
                        }
                    }

                }
                GUILayout.EndVertical();
            }
            else
            {
                if (GUILayout.Button(curOption, UCL_GUIStyle.ButtonStyle, iOptions))
                {
                    aIsShow = true;
                }
            }
            dic.SetData(ShowKey, aIsShow);
            return iSelectedIndex;
        }
        #endregion
    }
}