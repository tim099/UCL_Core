using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI {
    static public class UCL_GUILayout {
        #region property field
        /// <summary>
        /// Draw iList using GUILayout
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="iList"></param>
        static public void ListField<T>(List<T> iList)
        {
            if(iList == null)
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
                if (GUILayout.Button("Delete",GUILayout.Width(80)))
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
        static public void ListField<T>(List<T> iList, System.Func<T,T> iDrawElementFunc, System.Func<T> iCreateElementFunc
            ,string iAddElementButtonName = "Add Element", string iDeleteButtonName = "Delete")
        {
            if (iList == null)
            {
                return;
            }
            System.Type aType = typeof(T);
            GUILayout.BeginVertical();
            if (GUILayout.Button(iAddElementButtonName))
            {
                iList.Add(iCreateElementFunc());
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
            }
            GUILayout.EndVertical();
        }
        /// <summary>
        /// Display target obj in TextField and return result value
        /// </summary>
        /// <param name="iObj"></param>
        /// <returns></returns>
        static public object ObjectField(object iObj)
        {
            if(iObj is string)
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
        static public object NumField(string iLabel, object iVal, int min_width = 80) {
            GUILayout.BeginHorizontal();
            if(!string.IsNullOrEmpty(iLabel)) LabelAutoSize(iLabel);
            string aResult = GUILayout.TextField(iVal.ToString(), GUILayout.MinWidth(min_width));
            GUILayout.EndHorizontal();
            if(string.IsNullOrEmpty(aResult)) {
                return System.Convert.ChangeType(0, iVal.GetType());
            }
            object aResultValue;
            if(Core.MathLib.Num.TryParse(aResult, iVal.GetType(), out aResultValue)) return aResultValue;
            return iVal;
        }
        static public int IntField(string label, int val, int min_width = 80) {
            GUILayout.BeginHorizontal();
            LabelAutoSize(label);
            string result = GUILayout.TextField(val.ToString(), GUILayout.MinWidth(min_width));
            GUILayout.EndHorizontal();

            int res_val = 0;
            if(int.TryParse(result, out res_val)) return res_val;
            return val;
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
            if(float.TryParse(x, out res_val))val.x = res_val;

            LabelAutoSize("Y");
            string y = GUILayout.TextField(val.y.ToString(), GUILayout.MinWidth(80));
            if(float.TryParse(y, out res_val)) val.y = res_val;

            LabelAutoSize("Z");
            string z = GUILayout.TextField(val.z.ToString(), GUILayout.MinWidth(80));
            if(float.TryParse(z, out res_val)) val.z = res_val;
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
            if(float.TryParse(x, out res_val)) val.x = res_val;

            LabelAutoSize("Y");
            string y = GUILayout.TextField(val.y.ToString(), GUILayout.MinWidth(80));
            if(float.TryParse(y, out res_val)) val.y = res_val;
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
            if(float.TryParse(x, out res_val)) val.x = res_val;

            LabelAutoSize(ystr);
            string y = GUILayout.TextField(val.y.ToString(), GUILayout.MinWidth(80));
            if(float.TryParse(y, out res_val)) val.y = res_val;
            GUILayout.EndHorizontal();
            return val;
        }
        static public System.Tuple<string,string,string> Vector3Field(string label, string x, string y, string z) {
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
            return new System.Tuple<string, string, string>(x,y,z);
        }
        static public string TextField(string label, string val, int min_width) {
            GUILayout.BeginHorizontal();
            LabelAutoSize(label);
            string result = GUILayout.TextField(val, GUILayout.MinWidth(min_width));
            GUILayout.EndHorizontal();
            return result;
        }
        #endregion
        static public void DrawSprite(Sprite sprite) {
            if(sprite == null) return;
            DrawSprite(sprite, sprite.rect.width, sprite.rect.height);
        }
        static public void DrawSpriteFixedWidth(Sprite sprite, float width) {
            if(sprite == null) return;
            DrawSprite(sprite, width, sprite.rect.height * (width / sprite.rect.width));
        }
        static public void DrawSpriteFixedHeight(Sprite sprite, float height) {
            if(sprite == null) return;
            DrawSprite(sprite, sprite.rect.width * (height / sprite.rect.height), height);
        }
        static public void DrawSprite(Sprite sprite, float width, float height) {
            if(sprite == null) return;
            DrawSprite(sprite, width, width, height, height);
        }
        static public void DrawSprite(Sprite sprite, float min_width, float max_width, float min_height, float max_height) {
            if(sprite == null) return;
            Rect sprite_rect = sprite.rect;
            Rect rect = GUILayoutUtility.GetRect(min_width, max_width, min_height, max_height);
            if(rect.width > max_width) rect.width = max_width;
            if(rect.height > max_height) rect.height = max_height;

            var tex = sprite.texture;
            sprite_rect.xMin /= tex.width;
            sprite_rect.xMax /= tex.width;
            sprite_rect.yMin /= tex.height;
            sprite_rect.yMax /= tex.height;
            GUI.DrawTextureWithTexCoords(rect, tex, sprite_rect);
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
        #endregion

        #region Popup
        public static int Popup(int iSelectedIndex, string[] iDisplayedOptions, ref bool iOpened, params GUILayoutOption[] iOptions) {
            if(iSelectedIndex < 0) iSelectedIndex = 0;
            if(iSelectedIndex >= iDisplayedOptions.Length) iSelectedIndex = iDisplayedOptions.Length - 1;
            string aCur = iDisplayedOptions[iSelectedIndex];
            if(iOpened) {
                GUILayout.BeginVertical(iOptions);
                if(GUILayout.Button(aCur, iOptions)) {
                    iOpened = false;
                }
                using(var scope = new GUILayout.VerticalScope("box", iOptions)) {
                    for(int i = 0; i < iDisplayedOptions.Length; i++) {
                        if(GUILayout.Button(iDisplayedOptions[i], iOptions)) {
                            iOpened = false;
                            return i;
                        }
                    }
                }
                GUILayout.EndVertical();
            } else {
                if(GUILayout.Button(aCur, iOptions)) {
                    iOpened = true;
                }
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
        /// <summary>
        /// Show a popup and return selected Index
        /// </summary>
        /// <param name="iSelectedIndex"></param>
        /// <param name="iDisplayedOptions"></param>
        /// <param name="iOpened"></param>
        /// <param name="iOptions"></param>
        /// <returns></returns>
        public static int Popup(int iSelectedIndex, List<string> iDisplayedOptions, ref bool iOpened, params GUILayoutOption[] iOptions) {
            if(iDisplayedOptions.Count == 0)
            {
                Debug.LogError("UCL_GUILayoyt.Popup iDisplayedOptions.Count == 0");
                return 0;
            }
            if(iSelectedIndex < 0) iSelectedIndex = 0;
            if(iSelectedIndex >= iDisplayedOptions.Count) iSelectedIndex = iDisplayedOptions.Count - 1;
            string aCur = iDisplayedOptions[iSelectedIndex];
            if(iOpened) {
                GUILayout.BeginVertical(iOptions);
                if(GUILayout.Button(aCur, iOptions)) {
                    iOpened = false;
                }
                using(var scope = new GUILayout.VerticalScope("box", iOptions)) {
                    for(int i = 0; i < iDisplayedOptions.Count; i++) {
                        if(GUILayout.Button(iDisplayedOptions[i], iOptions)) {
                            iOpened = false;
                            return i;
                        }
                    }
                }
                GUILayout.EndVertical();
            } else {
                if(GUILayout.Button(aCur, iOptions)) {
                    iOpened = true;
                }
            }
            return iSelectedIndex;
        }
        #endregion
    }
}