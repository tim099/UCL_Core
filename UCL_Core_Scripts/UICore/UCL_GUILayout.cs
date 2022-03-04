using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UCL.Core.UI {
    public interface IFieldOnGUI
    {
        /// <summary>
        /// return true if the data of field altered
        /// </summary>
        /// <param name="iFieldName"></param>
        /// <param name="iEditTmpDatas"></param>
        /// <returns></returns>
        bool OnGUI(string iFieldName, UCL_ObjectDictionary iEditTmpDatas);
    }
    /// <summary>
    /// return all types inherit base class
    /// </summary>
    public interface ITypeList
    {
        IList<Type> GetAllTypes();

    }
    static public class UCL_GUILayout {
        #region property field
        /// <summary>
        /// Draw iList using GUILayout
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iList"></param>
        static public void ListField<T>(List<T> iList)
        {
            if (iList == null)
            {
                return;
            }
            System.Type aType = typeof(T);
            GUILayout.BeginVertical();
            if (GUILayout.Button("Add Element"))
            {
                if (aType == typeof(string))
                {
                    object aVal = string.Empty;
                    iList.Add((T)aVal);
                }
                else
                {
                    iList.Add(System.Activator.CreateInstance<T>());
                }

            }
            int aDeleteAt = -1;
            for (int i = 0; i < iList.Count; i++)
            {
                GUILayout.BeginHorizontal();
                iList[i] = (T)ObjectField(iList[i]);
                if (GUILayout.Button("Delete", GUILayout.Width(80)))
                {
                    aDeleteAt = i;
                }
                GUILayout.EndHorizontal();
            }
            if (aDeleteAt >= 0)
            {
                iList.RemoveAt(aDeleteAt);
            }
            GUILayout.EndVertical();
        }

        /// <summary>
        /// Draw iList using GUILayout
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iList"></param>
        /// <param name="iDrawElementFunc">Draw element function</param>
        /// <param name="iCreateElementFunc">Create element function</param>
        /// <param name="iAddElementButtonName"></param>
        /// <param name="iDeleteButtonName"></param>
        /// <returns>return true if the list add or delete element</returns>
        static public bool ListField<T>(List<T> iList, System.Func<T, T> iDrawElementFunc, System.Func<T> iCreateElementFunc
            , string iAddElementButtonName = "Add Element", string iDeleteButtonName = "Delete")
        {
            bool iIsModified = false;
            if (iList == null)
            {
                return iIsModified;
            }
            System.Type aType = typeof(T);
            GUILayout.BeginVertical();
            if (GUILayout.Button(iAddElementButtonName))
            {
                iList.Add(iCreateElementFunc());
                iIsModified = true;
            }
            int aDeleteAt = -1;
            for (int i = 0; i < iList.Count; i++)
            {
                GUILayout.BeginHorizontal();
                iList[i] = iDrawElementFunc(iList[i]);
                if (GUILayout.Button(iDeleteButtonName, GUILayout.Width(80)))
                {
                    aDeleteAt = i;
                }
                GUILayout.EndHorizontal();
            }
            if (aDeleteAt >= 0)
            {
                iList.RemoveAt(aDeleteAt);
                iIsModified = true;
            }
            GUILayout.EndVertical();
            return iIsModified;
        }
        /// <summary>
        /// Display target obj in TextField and return result value
        /// </summary>
        /// <param name="iObj"></param>
        /// <returns></returns>
        static public object ObjectField(object iObj)
        {
            if (iObj is string)
            {
                return GUILayout.TextField(iObj as string);
            }
            if (iObj.IsNumber())
            {
                string aResult = GUILayout.TextField(iObj.ToString());
                if (string.IsNullOrEmpty(aResult))
                {
                    return System.Convert.ChangeType(0, iObj.GetType());
                }
                object aResultValue;
                if (Core.MathLib.Num.TryParse(aResult, iObj.GetType(), out aResultValue)) return aResultValue;
            }
            return iObj;
        }
        static public object NumField(string iLabel, object iVal, int iMinWidth = 80) {
            GUILayout.BeginHorizontal();
            if (!string.IsNullOrEmpty(iLabel)) LabelAutoSize(iLabel);
            string aResult = GUILayout.TextField(iVal.ToString(), GUILayout.MinWidth(iMinWidth));
            GUILayout.EndHorizontal();
            if (string.IsNullOrEmpty(aResult)) {
                return System.Convert.ChangeType(0, iVal.GetType());
            }
            object aResultValue;
            if (Core.MathLib.Num.TryParse(aResult, iVal.GetType(), out aResultValue)) return aResultValue;
            return iVal;
        }
        static public int IntField(string iLabel, int iVal, int iMinWidth = 80) {
            GUILayout.BeginHorizontal();
            LabelAutoSize(iLabel);
            string aResult = GUILayout.TextField(iVal.ToString(), GUILayout.MinWidth(iMinWidth));
            GUILayout.EndHorizontal();
            if (string.IsNullOrEmpty(aResult)) return 0;
            int aResVal = 0;
            if (int.TryParse(aResult, out aResVal)) return aResVal;
            return iVal;
        }
        static public int IntField(string iLabel, int iVal, int iMinWidth, int iMaxWidth)
        {
            GUILayout.BeginHorizontal();
            LabelAutoSize(iLabel);
            string aResult = GUILayout.TextField(iVal.ToString(), GUILayout.MinWidth(iMinWidth), GUILayout.MaxWidth(iMaxWidth));
            GUILayout.EndHorizontal();
            if (string.IsNullOrEmpty(aResult)) return 0;
            int aResVal = 0;
            if (int.TryParse(aResult, out aResVal)) return aResVal;
            return iVal;
        }
        static HashSet<char> NumHash
        {
            get
            {
                if(s_NumHash == null)
                {
                    s_NumHash = new HashSet<char>() { '0','1','2','3','4','5','6','7','8','9','.'};
                }
                return s_NumHash;
            }
        }
        static HashSet<char> s_NumHash = null;
        static public int IntField(string iLabel, int iVal, UCL.Core.UCL_ObjectDictionary iDataDic, int iMinWidth = 80)
        {
            GUILayout.BeginHorizontal();
            LabelAutoSize(iLabel);

            string aResult = GUILayout.TextField(iDataDic.GetData("IntFieldValue", iVal.ToString()), GUILayout.MinWidth(iMinWidth));
            var aNumHash = NumHash;
            for (int i = 0; i < aResult.Length; i++)
            {
                if (!aNumHash.Contains(aResult[i]))
                {
                    aResult = aResult.Remove(i, 1);
                    break;
                }
            }
            iDataDic.SetData("IntFieldValue", aResult);
            GUILayout.EndHorizontal();

            int aResVal = 0;
            if (int.TryParse(aResult, out aResVal)) return aResVal;
            return iVal;
        }
        static public float FloatField(string iLabel, float iVal, int iMinWidth = 80) {
            GUILayout.BeginHorizontal();
            LabelAutoSize(iLabel);
            string result = GUILayout.TextField(iVal.ToString(), GUILayout.MinWidth(iMinWidth));
            GUILayout.EndHorizontal();

            float aResultVal = 0;
            if (float.TryParse(result, out aResultVal))
            {
                return aResultVal;
            }
            return iVal;
        }
        static public string TextField(string iLabel, string iVal) {
            GUILayout.BeginHorizontal();
            LabelAutoSize(iLabel);
            string result = GUILayout.TextField(iVal);
            GUILayout.EndHorizontal();
            return result;
        }
        static public bool Toggle(bool iVal, int iSize = 21)
        {
            if (GUILayout.Button(iVal ? "▼" : "►", GUILayout.Width(iSize), GUILayout.Height(iSize)))
            {
                iVal = !iVal;
            }
            return iVal;
        }
        static public bool Toggle(UCL_ObjectDictionary iObjectDic, string iKey, int iSize = 21)
        {
            bool iVal = iObjectDic.GetData(iKey, false);
            if (GUILayout.Button(iVal ? "▼" : "►", GUILayout.Width(iSize), GUILayout.Height(iSize)))
            {
                iVal = !iVal;
            }
            iObjectDic.SetData(iKey, iVal);
            return iVal;
        }
        static public bool Toggle(UCL_ObjectDictionary iObjectDic, string iKey, string iLabel, int iSize = 21, int iLabelSize = 21)
        {
            GUILayout.BeginHorizontal();
            bool iVal = Toggle(iObjectDic, iKey, iSize);
            LabelAutoSize(iLabel, iLabelSize);
            GUILayout.EndHorizontal();
            return iVal;
        }
        static public bool BoolField(bool iVal, int iSize = 21)
        {
            GUILayout.BeginHorizontal();

            if (GUILayout.Button(iVal ? "✔" : " ", GUILayout.Width(iSize), GUILayout.Height(iSize)))
            {
                iVal = !iVal;
            }
            GUILayout.EndHorizontal();

            return iVal;
        }
        static public bool BoolField(string iLabel, bool iVal, int iSize = 21)
        {
            GUILayout.BeginHorizontal();

            LabelAutoSize(iLabel);
            if (GUILayout.Button(iVal ? "✔" : " ", GUILayout.Width(iSize), GUILayout.Height(iSize)))
            {
                iVal = !iVal;
            }
            GUILayout.EndHorizontal();

            return iVal;
        }
        static public Vector3 Vector3Field(string label, Vector3 val) {
            GUILayout.BeginHorizontal();
            LabelAutoSize(label);
            GUILayout.FlexibleSpace();
            LabelAutoSize("X");
            string x = GUILayout.TextField(val.x.ToString(), GUILayout.MinWidth(80));
            float res_val = 0;
            if (float.TryParse(x, out res_val)) val.x = res_val;

            LabelAutoSize("Y");
            string y = GUILayout.TextField(val.y.ToString(), GUILayout.MinWidth(80));
            if (float.TryParse(y, out res_val)) val.y = res_val;

            LabelAutoSize("Z");
            string z = GUILayout.TextField(val.z.ToString(), GUILayout.MinWidth(80));
            if (float.TryParse(z, out res_val)) val.z = res_val;
            GUILayout.EndHorizontal();
            return val;
        }
        static public Vector2 Vector2Field(string label, Vector2 val) {
            GUILayout.BeginHorizontal();
            LabelAutoSize(label);
            GUILayout.FlexibleSpace();
            LabelAutoSize("X");
            string x = GUILayout.TextField(val.x.ToString(), GUILayout.MinWidth(80));
            float res_val = 0;
            if (float.TryParse(x, out res_val)) val.x = res_val;

            LabelAutoSize("Y");
            string y = GUILayout.TextField(val.y.ToString(), GUILayout.MinWidth(80));
            if (float.TryParse(y, out res_val)) val.y = res_val;
            GUILayout.EndHorizontal();
            return val;
        }
        static public Vector2 Vector2Field(string label, string xstr, string ystr, Vector2 val) {
            GUILayout.BeginHorizontal();
            LabelAutoSize(label);
            GUILayout.FlexibleSpace();
            LabelAutoSize(xstr);
            string x = GUILayout.TextField(val.x.ToString(), GUILayout.MinWidth(80));
            float res_val = 0;
            if (float.TryParse(x, out res_val)) val.x = res_val;

            LabelAutoSize(ystr);
            string y = GUILayout.TextField(val.y.ToString(), GUILayout.MinWidth(80));
            if (float.TryParse(y, out res_val)) val.y = res_val;
            GUILayout.EndHorizontal();
            return val;
        }
        static public System.Tuple<string, string, string> Vector3Field(string label, string x, string y, string z) {
            GUILayout.BeginHorizontal();
            LabelAutoSize(label);
            GUILayout.FlexibleSpace();
            LabelAutoSize("X");
            x = GUILayout.TextField(x, GUILayout.MinWidth(80));

            LabelAutoSize("Y");
            y = GUILayout.TextField(y, GUILayout.MinWidth(80));

            LabelAutoSize("Z");
            z = GUILayout.TextField(z, GUILayout.MinWidth(80));

            GUILayout.EndHorizontal();
            return new System.Tuple<string, string, string>(x, y, z);
        }
        static public string TextField(string label, string val, int min_width) {
            GUILayout.BeginHorizontal();
            LabelAutoSize(label);
            string result = GUILayout.TextField(val, GUILayout.MinWidth(min_width));
            GUILayout.EndHorizontal();
            return result;
        }
        #endregion
        static public Rect DrawSprite(Sprite iSprite) {
            if (iSprite == null) return default;
            return DrawSprite(iSprite, iSprite.rect.width, iSprite.rect.height);
        }
        static public Rect DrawSpriteFixedSize(Sprite iSprite, float iSize = 128)
        {
            if (iSprite == null) return GUILayoutUtility.GetRect(iSize, iSize);

            if (iSprite.rect.height < iSprite.rect.width)
            {
                return DrawSpriteFixedHeight(iSprite, iSize);
            }
            else
            {
                return DrawSpriteFixedWidth(iSprite, iSize);
            }
        }
        static public Rect DrawSpriteFixedWidth(Sprite iSprite, float iWidth) {
            if (iSprite == null) return GUILayoutUtility.GetRect(iWidth, iWidth);
            return DrawSprite(iSprite, iWidth, iSprite.rect.height * (iWidth / iSprite.rect.width));
        }
        static public Rect DrawSpriteFixedHeight(Sprite iSprite, float iHeight) {
            if (iSprite == null) return GUILayoutUtility.GetRect(iHeight, iHeight);
            return DrawSprite(iSprite, iSprite.rect.width * (iHeight / iSprite.rect.height), iHeight);
        }
        static public Rect DrawSprite(Sprite iSprite, float iWidth, float iHeight) {
            return DrawSprite(iSprite, iWidth, iWidth, iHeight, iHeight);
        }
        static public Rect DrawSprite(Sprite iSprite, float iMinWidth, float iMaxWidth, float iMinHeight, float iMaxHeight) {
            Rect aRect = GUILayoutUtility.GetRect(iMinWidth, iMaxWidth, iMinHeight, iMaxHeight);
            if (iSprite == null)
            {
                return aRect;
            }
            Rect aSpriteRect = iSprite.rect;
            
            if (aRect.width > iMaxWidth) aRect.width = iMaxWidth;
            if (aRect.height > iMaxHeight) aRect.height = iMaxHeight;

            var aTex = iSprite.texture;
            aSpriteRect.xMin /= aTex.width;
            aSpriteRect.xMax /= aTex.width;
            aSpriteRect.yMin /= aTex.height;
            aSpriteRect.yMax /= aTex.height;
            GUI.DrawTextureWithTexCoords(aRect, aTex, aSpriteRect);
            return aRect;
        }
        static public bool DrawableTexture(Texture2D iTexture, UCL_ObjectDictionary iDataDic, float iWidth, float iHeight, Color iDrawCol)
        {
            bool aIsUpdated = false;
            var aRect = DrawTexture(iTexture, iWidth, iHeight);
            var aCurrentEvent = Event.current;

            var aMousePos = aCurrentEvent.mousePosition;
            Vector2Int aPrevPos = iDataDic.GetData("PrevPos", Vector2Int.left);//Position in PrevFrame
            Vector2Int aCurPos = aRect.GetPosIntInRect(aMousePos);
            Vector2 aPrevMousePos = iDataDic.GetData("PrevMousePos", Vector2.negativeInfinity);

            
            int aOutTimer = iDataDic.GetData("OutTimer", 0);//the time ouside of border
            //GUILayout.Label("Timer:" + (aTimer++) + ",OutTimer:" + aOutTimer);

            if (aCurrentEvent.type == EventType.MouseUp)
            {
                aPrevMousePos = Vector2.negativeInfinity;
                aPrevPos = Vector2Int.left;
                iDataDic.SetData("PrevMousePos", Vector2.negativeInfinity);
                iDataDic.SetData("PrevPos", Vector2Int.left);
            }

            if (aRect.Contains(aMousePos))
            {
                if (aCurrentEvent.type == EventType.MouseDown)
                {
                    iTexture.SetPixel(aCurPos.x, aCurPos.y, iDrawCol);
                    iTexture.Apply();
                    aIsUpdated = true;
                    aPrevPos = aCurPos;
                }
                else if (aCurrentEvent.type == EventType.MouseDrag)
                {
                    if (aRect.Contains(aPrevMousePos))
                    {
                        if (aPrevPos != Vector2Int.left)
                        {
                            iTexture.DrawLine(aPrevPos, aCurPos, iDrawCol);
                            aIsUpdated = true;
                        }
                    }
                    else if(aPrevMousePos != Vector2.negativeInfinity)
                    {
                        var aPos = aRect.GetPosIntOnBorder(aMousePos, aPrevMousePos);
                        iTexture.DrawLine(aPos, aCurPos, iDrawCol);
                        aIsUpdated = true;
                    }
                    aPrevPos = aCurPos;
                }
                iDataDic.SetData("PrevPos", aPrevPos);
                iDataDic.SetData("PrevMousePos", aMousePos);
                iDataDic.SetData("OutTimer", 0);
            }
            else//out of range
            {
                iDataDic.SetData("OutTimer", aOutTimer + 1);
                if (aCurrentEvent.type == EventType.MouseDown)
                {
                    iDataDic.SetData("PrevPos", aCurPos);
                    iDataDic.SetData("PrevMousePos", aMousePos);
                }
                else if (aCurrentEvent.type == EventType.MouseDrag)
                {
                    if (aRect.Contains(aPrevMousePos) && aPrevPos != Vector2.left)//Prev pos in Rect
                    {
                        //Debug.LogError("aPrevPos:" + aPrevPos + ",aPrevMousePos:" + aPrevMousePos+ ",aOutTimer:"+ aOutTimer);
                        var aPos = aRect.GetPosIntOnBorder(aPrevMousePos, aMousePos);
                        iTexture.DrawLine(aPrevPos, aPos, iDrawCol);
                        iDataDic.SetData("PrevMousePos", aMousePos);
                        aIsUpdated = true;
                    }
                    iDataDic.SetData("PrevPos", aCurPos);
                }
                if (aOutTimer > 2)
                {
                    iDataDic.SetData("OutTimer", 0);
                    iDataDic.SetData("PrevMousePos", aMousePos);
                    iDataDic.SetData("PrevPos", Vector2.left);
                }
            }
            
            return aIsUpdated;
        }
        static public Rect DrawTexture(Texture iTexture, float width, float height)
        {
            if (iTexture == null) return default;
            return DrawTexture(iTexture, width, width, height, height);
        }
        static public Rect DrawTexture(Texture iTexture, float iMinWidth, float iMaxWidth, float iMinHeight, float iMaxHeight)
        {
            if (iTexture == null) return default;
            Rect aRect = GUILayoutUtility.GetRect(iMinWidth, iMaxWidth, iMinHeight, iMaxHeight);
            if (aRect.width > iMaxWidth) aRect.width = iMaxWidth;
            if (aRect.height > iMaxHeight) aRect.height = iMaxHeight;

            GUI.DrawTexture(aRect, iTexture);
            return aRect;
        }
        #region Button
        static GUIStyle sButtonGuiStyle = new GUIStyle(GUI.skin.button);
        /// <summary>
        /// Draw a GUILayout Button fit the size of text
        /// </summary>
        /// <param name="name">the content of button</param>
        /// <param name="fontsize">font size</param>
        /// <returns></returns>
        public static bool ButtonAutoSize(string name, int fontsize = 22) {
            sButtonGuiStyle.fontSize = fontsize;
            sButtonGuiStyle.normal.textColor = Color.white;

            Vector2 size = sButtonGuiStyle.CalcSize(new GUIContent(name));
            bool flag = GUILayout.Button(name, style: sButtonGuiStyle, GUILayout.Width(size.x), GUILayout.Height(size.y));

            return flag;
        }
        /// <summary>
        /// Draw a GUILayout Button fit the size of text
        /// </summary>
        /// <param name="name">the content of button</param>
        /// <param name="fontsize">font size</param>
        /// <param name="but_color">button color</param>
        /// <returns></returns>
        public static bool ButtonAutoSize(string name, int fontsize, Color but_color) {
            sButtonGuiStyle.fontSize = fontsize;
            Color col_tmp = GUI.backgroundColor;
            GUI.backgroundColor = but_color;
            Vector2 size = sButtonGuiStyle.CalcSize(new GUIContent(name));
            bool flag = GUILayout.Button(name, style: sButtonGuiStyle, GUILayout.Width(size.x), GUILayout.Height(size.y));
            GUI.backgroundColor = col_tmp;
            return flag;
        }
        public static bool ButtonAutoSize(string name, int fontsize, Color but_color, Color text_color) {
            sButtonGuiStyle.fontSize = fontsize;
            var aOldTextCol = sButtonGuiStyle.normal.textColor;
            sButtonGuiStyle.normal.textColor = text_color;

            Color col_tmp = GUI.backgroundColor;
            GUI.backgroundColor = but_color;
            Vector2 size = sButtonGuiStyle.CalcSize(new GUIContent(name));
            bool flag = GUILayout.Button(name, style: sButtonGuiStyle, GUILayout.Width(size.x), GUILayout.Height(size.y));
            GUI.backgroundColor = col_tmp;
            sButtonGuiStyle.normal.textColor = aOldTextCol;
            return flag;
        }
        #endregion

        #region Label
        static GUIStyle sLabelGuiStyle = new GUIStyle(GUI.skin.label);
        public static void LabelAutoSize(string iName, int iFontsize = 13) {
            sLabelGuiStyle.fontSize = iFontsize;
            sLabelGuiStyle.normal.textColor = Color.white;
            sLabelGuiStyle.richText = true;
            Vector2 aSize = sLabelGuiStyle.CalcSize(new GUIContent(iName));
            GUILayout.Label(iName, style: sLabelGuiStyle, GUILayout.Width(aSize.x + 1f), GUILayout.Height(aSize.y));
        }
        public static void LabelAutoSize(string iName, Color iColor, int iFontsize = 13)
        {
            sLabelGuiStyle.fontSize = iFontsize;
            sLabelGuiStyle.normal.textColor = iColor;
            sLabelGuiStyle.richText = true;
            Vector2 aSize = sLabelGuiStyle.CalcSize(new GUIContent(iName));
            GUILayout.Label(iName, style: sLabelGuiStyle, GUILayout.Width(aSize.x + 1f), GUILayout.Height(aSize.y));
        }
        public static void Label(string iName, Color iColor)
        {
            var aOriginalCol = GUI.skin.label.normal.textColor;
            GUI.skin.label.normal.textColor = iColor;
            GUILayout.Label(iName);
            GUI.skin.label.normal.textColor = aOriginalCol;
        }
        #endregion

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
            int iSearchThreshold = 10, params GUILayoutOption[] iOptions) {
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
            int iSearchThreshold = 10, params GUILayoutOption[] iOptions) {
            string aKey = iKey + "_SelectedIndex";
            int aSelectedIndex = iDataDic.GetData(aKey, 0);
            aSelectedIndex = PopupAuto(aSelectedIndex, iDisplayedOptions, iDataDic, iKey, iSearchThreshold, iOptions);
            iDataDic.SetData(aKey, aSelectedIndex);
            return aSelectedIndex;
        }
        public static int PopupAuto(IList<string> iDisplayedOptions, int iInitIndex, UCL_ObjectDictionary iDataDic, string iKey,
            int iSearchThreshold = 10, params GUILayoutOption[] iOptions) {
            string aKey = iKey + "_SelectedIndex";
            int aSelectedIndex = iDataDic.GetData(aKey, iInitIndex);
            aSelectedIndex = PopupAuto(aSelectedIndex, iDisplayedOptions, iDataDic, iKey, iSearchThreshold, iOptions);
            iDataDic.SetData(aKey, aSelectedIndex);
            return aSelectedIndex;
        }
        /// <summary>
        /// Draw a FolderExplorer using GUILayout
        /// </summary>
        /// <param name="iDataDic"></param>
        /// <param name="iPath">Current folder path</param>
        /// <param name="iRoot">Root of the path</param>
        /// <param name="iLabel">Title Label(etc. Folder Path)</param>
        /// <param name="iPathDisplayCount">max path button display on top, if iPathDisplayCount <= 0 then unlimited</param>
        /// <returns>return the selected folder</returns>
        public static string FolderExplorer(UCL_ObjectDictionary iDataDic, string iPath, string iRoot = "", string iLabel = "", int iPathDisplayCount = -1
            ,bool iIsShowFiles = false)
        {
            const string AllDirsNameKey = "AllDirsName";
            const string AllFilesNameKey = "AllFilesNameKey";
            const string DirPathKey = "DirPath";
            const string ShowToggleKey = "ShowToggle";//current showing toggle
            const string PathKey = "Path";
            System.Action<string> aSetPath = (iNewPath) => {
                iPath = iNewPath; 
                iDataDic.Remove(AllDirsNameKey);
                iDataDic.Remove(AllFilesNameKey);
                iDataDic.Remove(ShowToggleKey);
                iDataDic.SetData(DirPathKey, iNewPath);
                iDataDic.SetData(PathKey, iNewPath);
            };
            System.Action<string> aSelectDir = (iDirPath) =>
            {
                string aPath = string.IsNullOrEmpty(iRoot) ? iDirPath : iRoot + "/" + iDirPath;
                var aAllDirsName = UCL.Core.FileLib.Lib.GetDirectories(aPath, iSearchOption: System.IO.SearchOption.TopDirectoryOnly);
                for (int j = 0; j < aAllDirsName.Length; j++)
                {
                    aAllDirsName[j] = FileLib.Lib.GetFolderName(aAllDirsName[j]);
                }
                iDataDic.SetData(AllDirsNameKey, aAllDirsName);
                if (iIsShowFiles)
                {
                    var aAllFilesName = UCL.Core.FileLib.Lib.GetFiles(aPath, iSearchOption: System.IO.SearchOption.TopDirectoryOnly);
                    for (int j = 0; j < aAllFilesName.Length; j++)
                    {
                        aAllFilesName[j] = FileLib.Lib.GetFileName(aAllFilesName[j]);
                    }
                    iDataDic.SetData(AllFilesNameKey, aAllFilesName);
                }
                iDataDic.SetData(DirPathKey, iDirPath.LastElement() == '/' ? iDirPath.RemoveLast() : iDirPath);
            };

            GUILayout.BeginHorizontal();
            bool aIsShow = Toggle(iDataDic, "FolderExplorerToggle");

            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();

            if (!string.IsNullOrEmpty(iLabel))
            {
                GUILayout.Label(iLabel, GUILayout.ExpandWidth(false));
            }
            
            var aCurPath = iDataDic.GetData(PathKey, iPath);
            var aNewPath = GUILayout.TextField(aCurPath);
            iDataDic.SetData(PathKey, aNewPath);
            if (aNewPath != aCurPath)
            {
                if (System.IO.Directory.Exists(string.IsNullOrEmpty(iRoot) ? aNewPath : iRoot + "/" + aNewPath))
                {
                    aSetPath(aNewPath);
                }
            }
            GUILayout.EndHorizontal();

            if (!aIsShow)
            {
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                return iPath;
            }

            string aShowToggle = iDataDic.GetData(ShowToggleKey, string.Empty);
            using (var aScope = new GUILayout.VerticalScope("box", GUILayout.MinWidth(300)))
            {
                //iDataDic.SetData("ScrollPos", GUILayout.BeginScrollView(iDataDic.GetData<Vector2>("ScrollPos")));
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("<<", GUILayout.Width(40)))
                {
                    aSetPath(string.Empty);
                }
                if (!string.IsNullOrEmpty(iPath))//Path menu (on the top)
                {
                    var aTmpPath = (iPath.LastElement() == '/' || iPath.LastElement() == '\\') ? iPath.RemoveLast() : iPath;
                    string[] aPaths = aTmpPath.Split('/', '\\');

                    int aCount = aPaths.Length;
                    if (iPathDisplayCount > 0)
                    {
                        aCount = Math.Min(aPaths.Length, iPathDisplayCount);
                    }

                    for (int i = aCount; i >= 1; i--)
                    {
                        string aSelectPath = aPaths[aPaths.Length - i];
                        {
                            bool aIsCur = aShowToggle == aSelectPath;
                            if (aIsCur)
                            {
                                if (!Toggle(aIsCur))//Hide
                                {
                                    iDataDic.Remove(ShowToggleKey);
                                    iDataDic.Remove(AllDirsNameKey);
                                    iDataDic.Remove(AllFilesNameKey);
                                }
                            }
                            else
                            {
                                if (Toggle(aIsCur))//Show
                                {
                                    iDataDic.SetData(ShowToggleKey, aSelectPath);
                                    aShowToggle = aSelectPath;
                                    iDataDic.Remove(AllDirsNameKey);
                                    iDataDic.Remove(AllFilesNameKey);
                                    System.Text.StringBuilder aSB = new System.Text.StringBuilder();
                                    for (int j = 0; j < aPaths.Length - i; j++)
                                    {
                                        if (j > 0) aSB.Append('/');
                                        aSB.Append(aPaths[j]);
                                    }
                                    aSelectDir(aSB.ToString());
                                }
                            }
                        }

                        if (GUILayout.Button(aSelectPath))
                        {
                            System.Text.StringBuilder aSB = new System.Text.StringBuilder();
                            for (int j = 0; j <= aPaths.Length - i; j++)
                            {
                                if (j > 0) aSB.Append('/');
                                aSB.Append(aPaths[j]);
                            }
                            aSetPath(aSB.ToString());
                        }

                    }
                }

                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                //GUILayout.EndScrollView();
                if (!iDataDic.ContainsKey(AllDirsNameKey))
                {
                    aSelectDir(iPath);
                }
                string[] aDirs = iDataDic.GetData<string[]>(AllDirsNameKey);
                string[] aFiles = iDataDic.GetData<string[]>(AllFilesNameKey);
                if (!aDirs.IsNullOrEmpty() || (iIsShowFiles && !aFiles.IsNullOrEmpty()))//Select folder menu
                {
                    using (var aScope2 = new GUILayout.VerticalScope("box", GUILayout.Height(180)))
                    {
                        iDataDic.SetData("FolderScrollPos", GUILayout.BeginScrollView(iDataDic.GetData("FolderScrollPos", Vector2.zero)));
                        for (int i = 0; i < aDirs.Length; i++)
                        {
                            string aDir = aDirs[i];
                            GUILayout.BeginHorizontal();
                            //UCL.Core.UI.UCL_GUIStyle.GetButtonText()
                            if (GUILayout.Button("  ❐   "))
                            {
                                string aCurDirPath = iDataDic.GetData(DirPathKey, iPath);
                                if (string.IsNullOrEmpty(aCurDirPath))
                                {
                                    aSetPath(aDir);
                                }
                                else
                                {
                                    aSetPath(aCurDirPath + "/" + aDir);
                                }
                            }
                            Label(aDir, iDataDic.GetData(ShowToggleKey, string.Empty) == aDir ? Color.yellow : Color.white);
                            //GUILayout.Label(aDir);
                            GUILayout.FlexibleSpace();
                            GUILayout.EndHorizontal();
                        }

                        if (iIsShowFiles)
                        {
                            
                            if (!aFiles.IsNullOrEmpty())
                            {
                                for (int i = 0; i < aFiles.Length; i++)
                                {
                                    GUILayout.Label(aFiles[i]);
                                }
                            }
                        }
                        GUILayout.EndScrollView();
                    }
                }
            }     

            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            return iPath;
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
                //GUILayout.BeginHorizontal();

                if (GUILayout.Button(aCur, iOptions))
                {
                    aIsShow = false;
                }
                aInput = TextField(UCL.Core.LocalizeLib.UCL_LocalizeManager.Get("Search"), aInput);
                iDataDic.SetData(aSearchKey, aInput);
                //GUILayout.EndHorizontal();

                System.Text.RegularExpressions.Regex aRegex = null;
                {
                    if (!string.IsNullOrEmpty(aInput))
                    {
                        try
                        {
                            aRegex = new System.Text.RegularExpressions.Regex(aInput.ToLower() + ".*", System.Text.RegularExpressions.RegexOptions.Compiled);
                        } catch (System.Exception iE)
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
        public static int Popup(int iSelectedIndex, IList<string> iDisplayedOptions, ref bool iOpened, params GUILayoutOption[] iOptions) {
            if (iDisplayedOptions.Count == 0)
            {
                Debug.LogError("UCL_GUILayoyt.Popup iDisplayedOptions.Count == 0");
                return 0;
            }
            if (iSelectedIndex < 0) iSelectedIndex = 0;
            if (iSelectedIndex >= iDisplayedOptions.Count) iSelectedIndex = iDisplayedOptions.Count - 1;
            string aCur = iDisplayedOptions[iSelectedIndex];
            if (iOpened) {
                GUILayout.BeginVertical(iOptions);
                using (var aScope = new GUILayout.VerticalScope("box", iOptions))
                {
                    if (GUILayout.Button(aCur, iOptions))
                    {
                        iOpened = false;
                    }
                }
                using (var aScope = new GUILayout.VerticalScope("box", iOptions)) {
                    for (int i = 0; i < iDisplayedOptions.Count; i++) {
                        if (GUILayout.Button(iDisplayedOptions[i], iOptions)) {
                            iOpened = false;
                            return i;
                        }
                    }
                }
                GUILayout.EndVertical();
            } else {
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
        public static T Popup<T>(T iEnum, ref bool iIsOpened) where T : System.Enum {
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
                int aNewIntVal = IntField(iName, aIntVal, 50, 50);
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
        public static T Popup<T>(T iEnum, UCL_ObjectDictionary iDataDic, System.Func<T,string> iGetNameFunc = null, params GUILayoutOption[] iOptions) where T : System.Enum
        {
            System.Type aType = iEnum.GetType();
            string[] aNames = System.Enum.GetNames(aType);
            var aValues = System.Enum.GetValues(typeof(T));
            int aID = 0;
            for(int i = 0; i < aValues.Length; i++)
            {
                if(((T)aValues.GetValue(i)).Equals(iEnum))
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
        public static T Popup<T>(T iEnum, System.Func<string, string> iGetDisplayName, UCL_ObjectDictionary iDataDic, params GUILayoutOption[] iOptions) where T : System.Enum
        {
            bool aIsOpened = iDataDic.GetData("PopupIsOpened", false);
            T aRes = Popup(iEnum, ref aIsOpened, iGetDisplayName, iOptions);
            iDataDic.SetData("PopupIsOpened", aIsOpened);

            return aRes;
        }

        /// <summary>
        /// Show enum popup
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iEnum"></param>
        /// <param name="iIsOpened"></param>
        /// <returns></returns>
        public static T Popup<T>(T iEnum, ref bool iIsOpened, System.Func<string, string> iGetDisplayName, params GUILayoutOption[] iOptions) where T : System.Enum
        {
            System.Type aType = iEnum.GetType();
            var aNames = System.Enum.GetNames(aType);
            var aDisplayNames = new string[aNames.Length];
            int aID = aNames.GetIndex(iEnum.ToString());
            for(int i = 0; i < aNames.Length; i++)
            {
                aDisplayNames[i] = iGetDisplayName(aNames[i]);
            }
            aID = Popup(aID, aDisplayNames, ref iIsOpened, iOptions);
            return (T)System.Enum.Parse(aType, aNames[aID], true);
        }
        public static System.Enum Popup(System.Enum iEnum, ref bool iOpened) {
            System.Type aType = iEnum.GetType();
            var aNames = System.Enum.GetNames(aType);
            int aID = 0;
            for(; aID < aNames.Length; aID++) {
                if(iEnum.ToString() == aNames[aID]) {
                    break;
                }
            }
            aID = Popup(aID, aNames, ref iOpened);
            return System.Enum.Parse(aType, aNames[aID], true) as System.Enum;
        }


        #endregion

        #region DrawField

        /// <summary>
        /// Draw a object inspector using GUILayout
        /// </summary>
        /// <param name="iObj">target object</param>
        /// <param name="iDataDic">dictionary to save display data</param>
        /// <param name="iDisplayName">the name show when hide detail</param>
        /// <param name="iIsAlwaysShowDetail">if set to true then will not show the detail toggle</param>
        /// <param name="iFieldNameFunc">param is the field name and return the display name</param>
        /// <returns></returns>
        public static object DrawObjectData(object iObj, UCL_ObjectDictionary iDataDic, string iDisplayName = "", bool iIsAlwaysShowDetail = false, Func<string, string> iFieldNameFunc = null)
        {
            GUILayout.BeginVertical();
            bool aIsShowField = true;
            bool aIsDefaultType = true;
            object aResultObj = iObj;
            if (iObj != null)
            {
                if (string.IsNullOrEmpty(iDisplayName))
                {
                    iDisplayName = iObj.GetType().Name;
                }
                Type aType = iObj.GetType();
                if (iObj is string)
                {
                    aIsShowField = false;
                    aResultObj = GUILayout.TextField((string)iObj);
                }
                else if (aType.IsEnum)
                {
                    string aTypeName = aType.Name;

                    GUILayout.BeginHorizontal();
                    //string aKey = "EnumOpen";
                    //bool flag = iDataDic.GetData(aKey, false);
                    aResultObj = UCL.Core.UI.UCL_GUILayout.Popup((System.Enum)iObj, (iEnum) => {
                        string aLocalizeKey = aTypeName + "_" + iEnum;
                        if (LocalizeLib.UCL_LocalizeManager.ContainsKey(aLocalizeKey))
                        {
                            iEnum = string.Format("{0}({1})", LocalizeLib.UCL_LocalizeManager.Get(aLocalizeKey), iEnum);
                        }
                        return iEnum;
                    }, iDataDic);
                    //iDataDic.SetData(aKey, flag);
                    GUILayout.EndHorizontal();
                }
                else if (iObj.IsNumber())
                {
                    aIsShowField = false;
                    aResultObj = UCL_GUILayout.NumField(string.Empty, iObj);
                }
                else if (aType.IsTuple())
                {
                    aIsShowField = false;
                    var aResult = iObj.GetTupleElements();
                    bool aIsValueChanged = false;
                    GUILayout.BeginVertical();
                    for(int i = 0; i < aResult.Count; i++)
                    {
                        var aTupleData = aResult[i];
                        var aResultData = DrawObjectData(aTupleData, iDataDic.GetSubDic("_" + i.ToString()), aTupleData.UCL_GetShortName(), iFieldNameFunc: iFieldNameFunc);
                        if (aResultData != aResult[i])
                        {
                            aIsValueChanged = true;
                            aResult[i] = aResultData;
                        }
                    }
                    if (aIsValueChanged)
                    {
                        Type[] aTypeArray = aType.GetGenericArguments();

                        var aConstructer = aType.GetConstructor(aTypeArray);
                        if (aConstructer != null && aTypeArray.Length == aResult.Count)
                        {
                            aResultObj = aConstructer.Invoke(aResult.ToArray());
                        }
                    }
                    GUILayout.EndVertical();
                }
                else if (iObj is IList)
                {
                    GUILayout.BeginVertical();
                    aIsShowField = false;
                    GUILayout.BeginHorizontal();
                    string aShowKey = "Show";
                    aIsShowField = iDataDic.GetData(aShowKey, true);
                    iDataDic.SetData(aShowKey, UCL_GUILayout.Toggle(aIsShowField));
                    if (!string.IsNullOrEmpty(iDisplayName)) UCL_GUILayout.LabelAutoSize(iDisplayName);
                    GUILayout.EndHorizontal();
                    if (aIsShowField)
                    {
                        IList aList = iObj as IList;
                        string aCountKey = "Count";
                        int aCount = iDataDic.GetData(aCountKey, aList.Count);
                        GUILayout.BeginHorizontal();
                        int aNewCount = UCL_GUILayout.IntField(LocalizeLib.UCL_LocalizeManager.Get("Count"), aCount);
                        iDataDic.SetData(aCountKey, aNewCount);
                        if (aNewCount != aList.Count)
                        {
                            if (GUILayout.Button(LocalizeLib.UCL_LocalizeManager.Get("SetCount")))
                            {
                                if (aNewCount < 0) aNewCount = 0;
                                while (aNewCount < aList.Count)
                                {
                                    aList.RemoveAt(aList.Count - 1);
                                }
                                while (aNewCount > aList.Count)
                                {
                                    try
                                    {
                                        var aGenericType = iObj.GetType().GetGenericValueType();
                                        aList.Add(aGenericType.CreateInstance());
                                    }
                                    catch (System.Exception iE)
                                    {
                                        Debug.LogException(iE);
                                    }
                                }
                            }
                        }
                        else
                        {
                            if (GUILayout.Button(LocalizeLib.UCL_LocalizeManager.Get("Add")))
                            {
                                try
                                {
                                    var aGenericType = iObj.GetType().GetGenericValueType();
                                    aList.Add(aGenericType.CreateInstance());
                                }
                                catch (System.Exception iE)
                                {
                                    Debug.LogException(iE);
                                }
                                iDataDic.SetData(aCountKey, aList.Count);
                            }
                        }
                        GUILayout.EndHorizontal();
                        int aAt = 0;
                        int aDeleteAt = -1;
                        List<object> aResultList = new List<object>();
                        string aTypeName = iObj.GetType().GetGenericValueType().Name;
                        foreach (var aListData in aList)
                        {
                            using (var aScope2 = new GUILayout.VerticalScope("box"))
                            {
                                int aDrawAt = aAt;
                                GUILayout.BeginHorizontal();
                                if (UCL_GUILayout.ButtonAutoSize(LocalizeLib.UCL_LocalizeManager.Get("Delete")))
                                {
                                    aDeleteAt = aAt;
                                }
                                aResultList.Add(DrawObjectData(aListData, iDataDic.GetSubDic("_" + (aAt++).ToString()), aListData.UCL_GetShortName(aTypeName), iFieldNameFunc: iFieldNameFunc));
                                GUILayout.EndHorizontal();
                            }

                        }
                        for (int i = 0; i < aResultList.Count; i++)
                        {
                            aList[i] = aResultList[i];
                        }
                        if (aDeleteAt >= 0)
                        {
                            aList.RemoveAt(aDeleteAt);
                            iDataDic.SetData(aCountKey, aList.Count);
                        }
                        //GUILayout.EndVertical();
                    }
                    GUILayout.EndVertical();
                }
                else if (iObj is IDictionary)
                {
                    GUILayout.BeginHorizontal();
                    aIsShowField = false;
                    string aShowKey = "Show";
                    bool aIsShow = iDataDic.GetData(aShowKey, false);
                    iDataDic.SetData(aShowKey, UCL_GUILayout.Toggle(aIsShow));
                    UCL_GUILayout.LabelAutoSize(iDisplayName);
                    GUILayout.EndHorizontal();
                    if (aIsShow)
                    {
                        IDictionary aDic = iObj as IDictionary;
                        GUILayout.BeginHorizontal();
                        UCL_GUILayout.LabelAutoSize("Count : " + aDic.Count);
                        var aKeyType = aType.GetGenericKeyType();
                        string aAddKey = "AddData";
                        if (!iDataDic.ContainsKey(aAddKey))
                        {
                            iDataDic.SetData(aAddKey, aKeyType.CreateInstance());
                        }
                        iDataDic.SetData(aAddKey, DrawObjectData(iDataDic.GetData(aAddKey), iDataDic.GetSubDic(iDisplayName + "_AddKey"), aKeyType.Name, iFieldNameFunc: iFieldNameFunc));
                        if (GUILayout.Button(LocalizeLib.UCL_LocalizeManager.Get("Add"), GUILayout.Width(80)))
                        {
                            try
                            {
                                var aNewKey = iDataDic.GetData(aAddKey);
                                if (!aDic.Contains(aNewKey))
                                {
                                    iDataDic.Remove(aAddKey);
                                    var aGenericType = aType.GetGenericValueType();
                                    aDic.Add(aNewKey, aGenericType.CreateInstance());
                                }
                            }
                            catch (System.Exception iE)
                            {
                                Debug.LogException(iE);
                            }
                        }
                        GUILayout.EndHorizontal();
                        object aDeleteAt = null;
                        List<Tuple<object, object>> aResultList = new List<Tuple<object, object>>();
                        foreach (var aKey in aDic.Keys)
                        {
                            using (var aScope2 = new GUILayout.VerticalScope("box"))
                            {
                                GUILayout.BeginHorizontal();
                                if (UCL_GUILayout.ButtonAutoSize(UCL.Core.LocalizeLib.UCL_LocalizeManager.Get("Delete")))
                                {
                                    aDeleteAt = aKey;
                                }
                                GUILayout.BeginVertical();
                                string aKeyName = aKey.UCL_GetShortName(aKey.UCL_ToString());
                                GUILayout.Label(aKeyName);
                                aResultList.Add(new Tuple<object, object>(aKey, DrawObjectData(aDic[aKey], iDataDic.GetSubDic(iDisplayName + "Dic_" + aKeyName), aKeyName, iFieldNameFunc: iFieldNameFunc)));
                                GUILayout.EndVertical();
                                GUILayout.EndHorizontal();
                            }
                        }
                        for (int i = 0; i < aResultList.Count; i++)
                        {
                            aDic[aResultList[i].Item1] = aResultList[i].Item2;
                        }
                        if (aDeleteAt != null)
                        {
                            aDic.Remove(aDeleteAt);
                        }
                    }
                }
                else if (iObj.GetType().IsStructOrClass())
                {
                    aIsDefaultType = false;
                }
            }
            if (!aIsDefaultType)
            {
                GUILayout.BeginVertical();
                if(!iIsAlwaysShowDetail) {
                    GUILayout.BeginHorizontal();
                    string aShowKey = "_Show";
                    aIsShowField = iDataDic.GetData(aShowKey, false);
                    iDataDic.SetData(aShowKey, UCL_GUILayout.Toggle(aIsShowField));
                    if (!string.IsNullOrEmpty(iDisplayName)) UCL_GUILayout.LabelAutoSize(iDisplayName);
                    GUILayout.EndHorizontal();
                }

                if (aIsShowField) using (var aScope = new GUILayout.VerticalScope("box"))
                    {
                        Type aType = iObj.GetType();
                        var aFields = aType.GetAllFieldsUnityVer(typeof(object));
                        foreach (var aField in aFields)
                        {
                            var aData = aField.GetValue(iObj);

                            if (aField.GetCustomAttribute<HideInInspector>() != null
                                || aField.GetCustomAttribute<ATTR.UCL_HideOnGUIAttribute>(false) != null)
                            {
                                continue;
                            }
                            if (aData == null)
                            {
                                if (typeof(IList).IsAssignableFrom(aField.FieldType))
                                {
                                    aData = aField.FieldType.CreateInstance();
                                    aField.SetValue(iObj, aData);
                                }
                                else if (typeof(IDictionary).IsAssignableFrom(aField.FieldType))
                                {
                                    aData = aField.FieldType.CreateInstance();
                                    aField.SetValue(iObj, aData);
                                }
                            }

                            string aDisplayName = aField.Name;
                            string aDataKey = "_" + aDisplayName;

                            if(iFieldNameFunc == null) {
                                if (aDisplayName[0] == 'm' && aDisplayName[1] == '_')
                                {
                                    aDisplayName = aDisplayName.Substring(2, aDisplayName.Length - 2);
                                }
                                string aKey = "DrawField_" + aDisplayName;
                                if (LocalizeLib.UCL_LocalizeManager.ContainsKey(aKey))
                                {
                                    aDisplayName = LocalizeLib.UCL_LocalizeManager.Get(aKey);
                                }
                            }
                            else
                            {
                                aDisplayName = iFieldNameFunc(aDisplayName);
                            }
                            bool aIsAlwaysShowDetail = aField.FieldType.GetCustomAttribute<ATTR.AlwaysExpendOnGUI>() != null;
                            bool aIsDrawed = false;
                            var aAttrs = aField.GetCustomAttributes();
                            foreach (var aAttr in aAttrs)
                            {
                                if (aAttr is IShowInCondition)
                                {
                                    if (!((IShowInCondition)aAttr).IsShow(iObj))
                                    {
                                        aIsDrawed = true;
                                        break;
                                    }
                                }
                                else if (aAttr is IStringArr)
                                {
                                    var aStrArr = aAttr as IStringArr;
                                    aIsDrawed = true;
                                    GUILayout.BeginHorizontal();
                                    //using(var aScope2 = new GUILayout.HorizontalScope("box", GUILayout.ExpandWidth(false)))
                                    {
                                        GUILayout.Label(aDisplayName, GUILayout.ExpandWidth(false));
                                    }

                                    aField.SetValue(iObj, aStrArr.DrawOnGUILocalized(iObj, aData, iDataDic, "_" + aDisplayName));
                                    GUILayout.EndHorizontal();
                                }else if(aAttr is ITexture2D)
                                {
                                    var aTextureArr = aAttr as ITexture2D;
                                    GUILayout.BeginHorizontal();
                                    GUILayout.Box(aTextureArr.GetTexture(iObj, aData), GUILayout.Width(64), GUILayout.Height(64));
                                    GUILayout.EndHorizontal();
                                }
                                else if(aAttr is SpaceAttribute)
                                {
                                    GUILayout.Space((aAttr as SpaceAttribute).height);
                                }
                                else if (aAttr is UCL.Core.PA.UCL_FolderExplorerAttribute)
                                {
                                    if (aField.FieldType == typeof(string))
                                    {
                                        var aFolderExplorerAttribute = aAttr as UCL.Core.PA.UCL_FolderExplorerAttribute;
                                        if (aData == null) aData = "";
                                        aIsDrawed = true;
                                        string aPath = (string)aData;
                                        GUILayout.BeginHorizontal();
                                        //UCL_GUILayout.LabelAutoSize(aDisplayName);
                                        var aResult = FolderExplorer(iDataDic.GetSubDic(aField.Name), aPath, aFolderExplorerAttribute.m_FolderRoot, aDisplayName,
                                            iIsShowFiles : false);
                                        GUILayout.EndHorizontal();
                                        aField.SetValue(iObj, aResult);
                                    }
                                }
                                else if(aAttr is ATTR.AlwaysExpendOnGUI)
                                {
                                    aIsAlwaysShowDetail = true;
                                }
                            }
                            if (aIsDrawed)
                            {
                                //aField.SetValue(iObj, aDropDownAttr.DrawOnGUI(iObj, aData, m_DataDic, "_" + aDisplayName));
                            }
                            else if (aField.FieldType == typeof(bool))
                            {
                                if (aData == null) aData = false;
                                aField.SetValue(iObj, UCL.Core.UI.UCL_GUILayout.BoolField(aDisplayName, (bool)aData));
                            }
                            else if (aField.FieldType == typeof(string))
                            {
                                if (aData == null) aData = "";
                                var aResult = UCL.Core.UI.UCL_GUILayout.TextField(aDisplayName, (string)aData);
                                aField.SetValue(iObj, aResult);
                            }
                            else if (aData == null)
                            {
                                //Debug.LogError("aData == null aField:" + aField.Name);

                            }
                            else if (aData is IFieldOnGUI)
                            {
                                IFieldOnGUI aVar = (IFieldOnGUI)aData;
                                if (aVar.OnGUI(aDisplayName, iDataDic.GetSubDic(aField.Name)))
                                {
                                    aField.SetValue(iObj, aVar);
                                }
                            }
                            else if (aData.IsNumber())
                            {
                                if (!iDataDic.ContainsKey(aDataKey))
                                {
                                    iDataDic.SetData(aDataKey, aData.ToString());
                                }
                                string aNum = iDataDic.GetData(aDataKey, string.Empty);
                                var aResult = UCL.Core.UI.UCL_GUILayout.TextField(aDisplayName, (string)aNum);
                                iDataDic.SetData(aDataKey, aResult);
                                object aResVal;
                                if (UCL.Core.MathLib.Num.TryParse(aResult, aData.GetType(), out aResVal))
                                {
                                    aField.SetValue(iObj, aResVal);
                                }
                            }
                            else if (aData is IList)
                            {
                                IList aList = aData as IList;
                                GUILayout.BeginHorizontal();
                                string aShowKey = aDataKey + "_Show";
                                bool aIsShow = iDataDic.GetData(aShowKey, false);
                                iDataDic.SetData(aShowKey, UCL_GUILayout.Toggle(aIsShow));
                                UCL_GUILayout.LabelAutoSize(string.Format("{0}({1})",aDisplayName, aList.Count));
                                GUILayout.EndHorizontal();
                                if (aIsShow)
                                {
                                    string aCountKey = aDataKey + "_Count";
                                    int aCount = iDataDic.GetData(aCountKey, aList.Count);
                                    GUILayout.BeginHorizontal();
                                    int aNewCount = UCL_GUILayout.IntField(LocalizeLib.UCL_LocalizeManager.Get("Count"), aCount);
                                    iDataDic.SetData(aCountKey, aNewCount);
                                    if (aNewCount != aList.Count)
                                    {
                                        if (GUILayout.Button(LocalizeLib.UCL_LocalizeManager.Get("SetCount")))
                                        {
                                            if (aNewCount < 0) aNewCount = 0;
                                            while (aNewCount < aList.Count)
                                            {
                                                aList.RemoveAt(aList.Count - 1);
                                            }
                                            while (aNewCount > aList.Count)
                                            {
                                                try
                                                {
                                                    var aGenericType = aField.FieldType.GetGenericValueType();
                                                    aList.Add(aGenericType.CreateInstance());
                                                }
                                                catch (System.Exception iE)
                                                {
                                                    Debug.LogException(iE);
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        string aITypeListKey = aField.Name + "ITypeList";
                                        List<string> aTypeNameList = null;
                                        if (iDataDic.ContainsKey(aITypeListKey))
                                        {
                                            aTypeNameList = iDataDic.GetData<List<string>>(aITypeListKey);
                                        }
                                        else
                                        {
                                            var aGenericType = aField.FieldType.GetGenericValueType();

                                            if (typeof(ITypeList).IsAssignableFrom(aGenericType))
                                            {
                                                var aTypeList = aGenericType.CreateInstance() as ITypeList;
                                                if (aTypeList != null)
                                                {
                                                    var aAllTypeList = aTypeList.GetAllTypes();
                                                    aTypeNameList = new List<string>();
                                                    for(int i = 0; i < aAllTypeList.Count; i++)
                                                    {
                                                        aTypeNameList.Add(LocalizeLib.UCL_LocalizeManager.Get(aAllTypeList[i].Name));
                                                    }
                                                    iDataDic.Add(aITypeListKey, aTypeNameList);
                                                    iDataDic.Add(aITypeListKey + "Type", aAllTypeList);
                                                }
                                            }
                                            else
                                            {
                                                iDataDic.Add(aITypeListKey, null);
                                            }
                                        }
                                        int aSelectedType = -1;
                                        if (aTypeNameList != null)
                                        {
                                            aSelectedType = PopupAuto(aTypeNameList, iDataDic, aField.Name, 10, GUILayout.Width(160));
                                        }
                                        if (GUILayout.Button(LocalizeLib.UCL_LocalizeManager.Get("Add"), GUILayout.Width(80)))
                                        {
                                            try
                                            {
                                                var aGenericType = aField.FieldType.GetGenericValueType();
                                                if (aSelectedType >= 0)
                                                {
                                                    var aTypes = iDataDic.GetData<IList<Type>>(aITypeListKey + "Type");
                                                    aList.Add(aTypes[aSelectedType].CreateInstance());
                                                }
                                                else
                                                {
                                                    aList.Add(aGenericType.CreateInstance());
                                                }
                                            }
                                            catch (System.Exception iE)
                                            {
                                                Debug.LogException(iE);
                                            }
                                            iDataDic.SetData(aCountKey, aList.Count);
                                        }
                                    }
                                    GUILayout.EndHorizontal();
                                    int aAt = 0;
                                    int aDeleteAt = -1;
                                    List<object> aResultList = new List<object>();
                                    string aTypeName = aData.GetType().GetGenericValueType().Name;
                                    foreach (var aListData in aList)
                                    {
                                        using (var aScope2 = new GUILayout.VerticalScope("box"))
                                        {
                                            GUILayout.BeginHorizontal();
                                            if (UCL_GUILayout.ButtonAutoSize(UCL.Core.LocalizeLib.UCL_LocalizeManager.Get("Delete")))
                                            {
                                                aDeleteAt = aAt;
                                            }
                                            aResultList.Add(DrawObjectData(aListData, iDataDic.GetSubDic(aDisplayName + "Dic_" + (aAt++).ToString()), aListData.UCL_GetShortName(aTypeName), iFieldNameFunc: iFieldNameFunc));
                                            GUILayout.EndHorizontal();
                                        }
                                    }
                                    for (int i = 0; i < aResultList.Count; i++)
                                    {
                                        aList[i] = aResultList[i];
                                    }
                                    if (aDeleteAt >= 0)
                                    {
                                        aList.RemoveAt(aDeleteAt);
                                        iDataDic.SetData(aCountKey, aList.Count);
                                    }
                                }
                            }
                            else if (aData is IDictionary)
                            {
                                GUILayout.BeginHorizontal();
                                string aShowKey = aDataKey + "_Show";
                                bool aIsShow = iDataDic.GetData(aShowKey, false);
                                iDataDic.SetData(aShowKey, UCL_GUILayout.Toggle(aIsShow));
                                UCL_GUILayout.LabelAutoSize(aDisplayName);
                                GUILayout.EndHorizontal();
                                if (aIsShow)
                                {
                                    IDictionary aDic = aData as IDictionary;
                                    GUILayout.BeginHorizontal();
                                    UCL_GUILayout.LabelAutoSize("Count : " + aDic.Count);
                                    var aKeyType = aField.FieldType.GetGenericKeyType();
                                    string aAddKey = aDataKey + "_Add";
                                    if (!iDataDic.ContainsKey(aAddKey))
                                    {
                                        iDataDic.SetData(aAddKey, aKeyType.CreateInstance());
                                    }
                                    iDataDic.SetData(aAddKey, DrawObjectData(iDataDic.GetData(aAddKey), iDataDic.GetSubDic(aDisplayName + "_AddKey"), aKeyType.Name, iFieldNameFunc: iFieldNameFunc));
                                    if (GUILayout.Button(LocalizeLib.UCL_LocalizeManager.Get("Add"), GUILayout.Width(80)))
                                    {
                                        try
                                        {
                                            var aNewKey = iDataDic.GetData(aAddKey);
                                            if (!aDic.Contains(aNewKey))
                                            {
                                                iDataDic.Remove(aAddKey);
                                                var aGenericType = aField.FieldType.GetGenericValueType();
                                                aDic.Add(aNewKey, aGenericType.CreateInstance());
                                            }
                                        }
                                        catch (System.Exception iE)
                                        {
                                            Debug.LogException(iE);
                                        }
                                    }
                                    GUILayout.EndHorizontal();
                                    object aDeleteAt = null;
                                    List<Tuple<object, object>> aResultList = new List<Tuple<object, object>>();
                                    foreach (var aKey in aDic.Keys)
                                    {
                                        using (var aScope2 = new GUILayout.VerticalScope("box"))
                                        {
                                            GUILayout.BeginHorizontal();
                                            if (UCL_GUILayout.ButtonAutoSize(UCL.Core.LocalizeLib.UCL_LocalizeManager.Get("Delete")))
                                            {
                                                aDeleteAt = aKey;
                                            }
                                            GUILayout.BeginVertical();
                                            string aKeyName = aKey.UCL_GetShortName(aKey.UCL_ToString());
                                            GUILayout.Label(aKeyName);
                                            aResultList.Add(new Tuple<object, object>(aKey, DrawObjectData(aDic[aKey], iDataDic.GetSubDic(aDisplayName + "Dic_" + aKeyName), aKeyName, iFieldNameFunc: iFieldNameFunc)));
                                            GUILayout.EndVertical();
                                            GUILayout.EndHorizontal();
                                        }
                                    }
                                    for (int i = 0; i < aResultList.Count; i++)
                                    {
                                        aDic[aResultList[i].Item1] = aResultList[i].Item2;
                                    }
                                    if (aDeleteAt != null)
                                    {
                                        aDic.Remove(aDeleteAt);
                                    }
                                }
                            }
                            else if (aField.FieldType.IsEnum)
                            {
                                string aTypeName = aField.FieldType.Name;

                                GUILayout.BeginHorizontal();
                                UCL.Core.UI.UCL_GUILayout.LabelAutoSize(aDisplayName);
                                aField.SetValue(iObj, UCL.Core.UI.UCL_GUILayout.Popup((System.Enum)aData, (iEnum) => {
                                    string aLocalizeKey = aTypeName + "_" + iEnum;
                                    if (LocalizeLib.UCL_LocalizeManager.ContainsKey(aLocalizeKey))
                                    {
                                        iEnum = string.Format("{0}({1})", LocalizeLib.UCL_LocalizeManager.Get(aLocalizeKey), iEnum);
                                    }
                                    return iEnum;
                                }, iDataDic.GetSubDic(aField.Name)));
                                GUILayout.EndHorizontal();
                            }
                            else if (aField.FieldType.IsStructOrClass())
                            {
                                DrawObjectData(aData, iDataDic.GetSubDic(aDisplayName + "_FieldData"), aDisplayName, iFieldNameFunc: iFieldNameFunc, iIsAlwaysShowDetail: aIsAlwaysShowDetail);
                                aField.SetValue(iObj, aData);
                            }
                        }

                    }
                GUILayout.EndVertical();
            }
            GUILayout.EndVertical();
            //GUILayout.EndHorizontal();
            return aResultObj;
        }
        #endregion
    }
}