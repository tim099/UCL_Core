using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UCL.Core.LocalizeLib;
using UnityEngine;

namespace UCL.Core.UI {
    public interface UCLI_FieldOnGUI
    {
        /// <summary>
        /// return new data if the data of field altered
        /// </summary>
        /// <param name="iFieldName"></param>
        /// <param name="iEditTmpDatas"></param>
        /// <returns></returns>
        object OnGUI(string iFieldName, UCL_ObjectDictionary iEditTmpDatas);
    }

    static public partial class UCL_GUILayout {
        /// <summary>
        /// set to true if RequireRepaint
        /// </summary>
        static public bool s_RequireRepaint = false;

        #region NumField
        static HashSet<char> NumHash
        {
            get
            {
                if (s_NumHash == null)
                {
                    s_NumHash = new HashSet<char>() { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', '-' };
                }
                return s_NumHash;
            }
        }
        static HashSet<char> s_NumHash = null;

        static public T NumField<T>(string iLabel, T iVal, int iMinWidth = 80)
        {
            GUILayout.BeginHorizontal();
            if (!string.IsNullOrEmpty(iLabel)) LabelAutoSize(iLabel);
            string aResult = GUILayout.TextField(iVal.ToString(), GUILayout.MinWidth(iMinWidth));
            GUILayout.EndHorizontal();
            if (string.IsNullOrEmpty(aResult))
            {
                return (T)System.Convert.ChangeType(0, iVal.GetType());
            }
            object aResultValue;
            if (MathLib.Num.TryParse(aResult, iVal.GetType(), out aResultValue)) return (T)aResultValue;
            return iVal;
        }
        static public T NumField<T>(string iLabel, T iVal, UCL_ObjectDictionary iDataDic, params GUILayoutOption[] iOptions)
        {
            GUILayout.BeginHorizontal();
            LabelAutoSize(iLabel);
            iVal = NumField(iVal, iDataDic, iOptions);
            GUILayout.EndHorizontal();
            return iVal;
        }
        static public T NumField<T>(T iVal, UCL_ObjectDictionary iDataDic, params GUILayoutOption[] iOptions)
        {
            const string aKey = "NumField";
            string aResult = GUILayout.TextField(iDataDic.GetData(aKey, iVal.ToString()), iOptions);
            var aNumHash = NumHash;
            for (int i = 0; i < aResult.Length; i++)
            {
                if (!aNumHash.Contains(aResult[i]))
                {
                    aResult = aResult.Remove(i, 1);
                    break;
                }
            }
            iDataDic.SetData(aKey, aResult);
            if (string.IsNullOrEmpty(aResult))
            {
                return (T)System.Convert.ChangeType(0, iVal.GetType());
            }
            object aResultValue;
            if (MathLib.Num.TryParse(aResult, iVal.GetType(), out aResultValue)) return (T)aResultValue;
            return iVal;
        }

        static public Vector3 VectorField(string iLabel, Vector3 iVal, UCL_ObjectDictionary iDataDic, params GUILayoutOption[] iOptions)
        {
            using(new GUILayout.HorizontalScope("box", iOptions))
            {
                GUILayout.Label(iLabel, GUILayout.MinWidth(80), GUILayout.ExpandWidth(false));
                float aX = NumField<float>("X", iVal.x, iDataDic.GetSubDic("X"));
                float aY = NumField<float>("Y", iVal.y, iDataDic.GetSubDic("Y"));
                float aZ = NumField<float>("Z", iVal.z, iDataDic.GetSubDic("Z"));
                return new Vector3(aX, aY, aZ);
            }
        }
        static public int Slider(string iLabel, int iVal, int m_LeftValue, int m_RightValue, UCL_ObjectDictionary iDic)
        {
            GUILayout.BeginHorizontal();
            var aDic = iDic.GetSubDic("Slider");
            if(!string.IsNullOrEmpty(iLabel)) GUILayout.Label(iLabel, GUILayout.ExpandWidth(false));
            float aPrevVal = iDic.GetData("SliderVal", (float)iVal);
            float aSliderVal = GUILayout.HorizontalSlider(aPrevVal, m_LeftValue, m_RightValue, GUILayout.ExpandWidth(true));
            int aResult = Mathf.RoundToInt(aSliderVal);
            if (Input.GetMouseButtonUp(0))
            {
                iDic.SetData("SliderVal", aResult);
            }
            else
            {
                iDic.SetData("SliderVal", aSliderVal);
            }

            if (aResult != iVal)
            {
                aDic.Clear();
            }
            int aInput = UCL.Core.UI.UCL_GUILayout.IntField(aResult, aDic, GUILayout.MinWidth(80), GUILayout.ExpandWidth(false));
            if(aInput != aResult)
            {
                aResult = aInput;
                iDic.Clear();
            }
            int aMaxValue = System.Math.Max(m_LeftValue, m_RightValue);
            int aMinValue = System.Math.Min(m_LeftValue, m_RightValue);
            if (aResult > aMaxValue) aResult = aMaxValue;
            else if (aResult < aMinValue) aResult = aMinValue;

            GUILayout.EndHorizontal();
            return aResult;
        }
        static public float Slider(string iLabel, float iVal, float m_LeftValue, float m_RightValue, UCL_ObjectDictionary iDic)
        {
            GUILayout.BeginHorizontal();
            if (!string.IsNullOrEmpty(iLabel)) GUILayout.Label(iLabel, GUILayout.ExpandWidth(false));
            float aResult = GUILayout.HorizontalSlider(iVal, m_LeftValue, m_RightValue, GUILayout.ExpandWidth(true));
            if (aResult != iVal) iDic.Clear();
            aResult = UCL.Core.UI.UCL_GUILayout.NumField(aResult, iDic, GUILayout.MinWidth(80), GUILayout.ExpandWidth(false));
            float aMaxValue = System.Math.Max(m_LeftValue, m_RightValue);
            float aMinValue = System.Math.Min(m_LeftValue, m_RightValue);
            if (aResult > aMaxValue) aResult = aMaxValue;
            else if (aResult < aMinValue) aResult = aMinValue;

            GUILayout.EndHorizontal();
            return aResult;
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
        static public int IntField(string iLabel, int iVal, params GUILayoutOption[] iOptions)
        {
            GUILayout.BeginHorizontal();
            LabelAutoSize(iLabel);
            iVal = IntField(iVal, iOptions);
            GUILayout.EndHorizontal();
            return iVal;
        }
        static public int IntField(int iVal, params GUILayoutOption[] iOptions)
        {
            string aResult = GUILayout.TextField(iVal.ToString(), iOptions);
            if (string.IsNullOrEmpty(aResult)) return 0;
            int aResVal = 0;
            if (int.TryParse(aResult, out aResVal)) return aResVal;
            return iVal;
        }
        static public int IntField(string iLabel, int iVal, UCL_ObjectDictionary iDataDic, params GUILayoutOption[] iOptions)
        {
            GUILayout.BeginHorizontal();
            LabelAutoSize(iLabel);
            int aResult = IntField(iVal, iDataDic, iOptions);
            GUILayout.EndHorizontal();
            return aResult;
        }
        static public int IntField(int iVal, UCL.Core.UCL_ObjectDictionary iDataDic, params GUILayoutOption[] iOptions)
        {
            const string aKey = "IntFieldValue";
            string aResult = GUILayout.TextField(iDataDic.GetData(aKey, iVal.ToString()), iOptions);
            var aNumHash = NumHash;
            for (int i = 0; i < aResult.Length; i++)
            {
                if (!aNumHash.Contains(aResult[i]))
                {
                    aResult = aResult.Remove(i, 1);
                    break;
                }
            }
            iDataDic.SetData(aKey, aResult);

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
        #endregion


        static public string TextField(string iLabel, UCL.Core.UCLI_ObjectDictionary iDataDic, string iKey)
        {
            GUILayout.BeginHorizontal();
            LabelAutoSize(iLabel);
            string aResult = GUILayout.TextField(iDataDic.GetData(iKey,string.Empty));
            iDataDic.SetData(iKey, aResult);
            GUILayout.EndHorizontal();
            return aResult;
        }
        static public string TextField(string iLabel, string iVal) {
            GUILayout.BeginHorizontal();
            LabelAutoSize(iLabel);
            string result = GUILayout.TextField(iVal);
            GUILayout.EndHorizontal();
            return result;
        }
        static public string TextArea(string iLabel, string iVal)
        {
            GUILayout.BeginHorizontal();
            LabelAutoSize(iLabel);
            string result = GUILayout.TextArea(iVal);
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
        static public bool Toggle(UCL_ObjectDictionary iObjectDic, string iKey, int iSize = 21, bool iDefaultValue = false)
        {
            bool iVal = iObjectDic.GetData(iKey, iDefaultValue);
            if (GUILayout.Button(iVal ? "▼" : "►", GUILayout.Width(iSize), GUILayout.Height(iSize)))
            {
                iVal = !iVal;
            }
            iObjectDic.SetData(iKey, iVal);
            return iVal;
        }
        static public bool Toggle(UCL_ObjectDictionary iObjectDic, string iKey, string iLabel, int iSize = 21, int iLabelSize = 16)
        {
            GUILayout.BeginHorizontal();
            bool iVal = Toggle(iObjectDic, iKey, iSize);
            LabelAutoSize(iLabel, iLabelSize);
            GUILayout.EndHorizontal();
            return iVal;
        }
        static public bool BoolField(UCL_ObjectDictionary iDic, string iKey, int iSize = 21, bool iDefaultValue = false)
        {
            bool aVal = iDic.GetData(iKey, iDefaultValue);
            if (GUILayout.Button(aVal ? "✔" : " ", GUILayout.Width(iSize), GUILayout.Height(iSize)))
            {
                aVal = !aVal;
            }
            iDic.SetData(iKey, aVal);
            return aVal;
        }
        static public bool BoolField(bool iVal, params GUILayoutOption[] iOptions)
        {
            if (GUILayout.Button(iVal ? "✔" : " ", iOptions))
            {
                iVal = !iVal;
            }

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
        static public bool CheckBox(bool iVal, int iSize = 21)
        {
            if (GUILayout.Button(iVal ? "✔" : " ", GUILayout.Width(iSize), GUILayout.Height(iSize)))
            {
                iVal = !iVal;
            }

            return iVal;
        }
        static public void DrawCopyPaste(UCL.Core.JsonLib.IJsonSerializable iTarget)
        {
            if (GUILayout.Button("Copy", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                GUIUtility.systemCopyBuffer = iTarget.SerializeToJson().ToJson();
            }
            if (GUILayout.Button("Paste", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                if (!string.IsNullOrEmpty(GUIUtility.systemCopyBuffer))
                {
                    try
                    {
                        var aJson = UCL.Core.JsonLib.JsonData.ParseJson(GUIUtility.systemCopyBuffer);
                        iTarget.DeserializeFromJson(aJson);
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }
            }
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
        
        static public Rect DrawTexture(Texture iTexture, float iMinWidth, float iMaxWidth, float iMinHeight, float iMaxHeight)
        {
            if (iTexture == null) return default;
            Rect aRect = GUILayoutUtility.GetRect(iMinWidth, iMaxWidth, iMinHeight, iMaxHeight);
            if (aRect.width > iMaxWidth) aRect.width = iMaxWidth;
            if (aRect.height > iMaxHeight) aRect.height = iMaxHeight;

            GUI.DrawTexture(aRect, iTexture);
            return aRect;
        }
        static public Rect DrawTexture(Texture iTexture, float iWidth, float iHeight)
        {
            if (iTexture == null) return default;
            return DrawTexture(iTexture, iWidth, iWidth, iHeight, iHeight);
        }
        /// <summary>
        /// Draw with Graphics.DrawTexture
        /// </summary>
        /// <param name="iTexture"></param>
        /// <param name="iWidth"></param>
        /// <param name="iHeight"></param>
        /// <param name="iMat"></param>
        /// <returns></returns>
        static public Rect GraphicsDrawTexture(Texture iTexture, float iWidth, float iHeight, Material iMat)
        {
            if (iTexture == null) return default;
            return GraphicsDrawTexture(iTexture, iWidth, iWidth, iHeight, iHeight, iMat);
        }
        static public Rect GraphicsDrawTexture(Texture iTexture, float iMinWidth, float iMaxWidth, float iMinHeight, float iMaxHeight, Material iMat)
        {
            if (iTexture == null) return default;
            Rect aRect = GUILayoutUtility.GetRect(iMinWidth, iMaxWidth, iMinHeight, iMaxHeight);
            if (aRect.width > iMaxWidth) aRect.width = iMaxWidth;
            if (aRect.height > iMaxHeight) aRect.height = iMaxHeight;
            if (Event.current.type == EventType.Repaint)
            {
                Graphics.DrawTexture(aRect, iTexture, iMat);
            }
            
            return aRect;
        }
        #region Button
        /// <summary>
        /// Draw a GUILayout Button fit the size of text
        /// </summary>
        /// <param name="iName">the content of button</param>
        /// <param name="iFontSize">font size</param>
        /// <returns></returns>
        public static bool ButtonAutoSize(string iName, int iFontSize = 22) {
            var aButtonStyle = UCL_GUIStyle.GetButtonStyle(Color.white, iFontSize);
            Vector2 size = aButtonStyle.CalcSize(new GUIContent(iName));
            bool flag = GUILayout.Button(iName, style: aButtonStyle, GUILayout.Width(size.x), GUILayout.Height(size.y));

            return flag;
        }
        public static bool ButtonAutoSize(string iName, int iFontSize, Color iButtonColor, Color iTextColor) {
            var aButtonStyle = UCL_GUIStyle.GetButtonStyle(iTextColor, iFontSize);

            Color col_tmp = GUI.backgroundColor;
            GUI.backgroundColor = iButtonColor;
            Vector2 size = aButtonStyle.CalcSize(new GUIContent(iName));
            bool flag = GUILayout.Button(iName, style: aButtonStyle, GUILayout.Width(size.x), GUILayout.Height(size.y));
            GUI.backgroundColor = col_tmp;
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
            , bool iIsShowFiles = false, UCLI_FileExplorer iFileExplorer = null)
        {
            if (iFileExplorer == null) iFileExplorer = UCL_FileExplorer.Ins;
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
                var aAllDirsName = iFileExplorer.GetDirectories(aPath, iSearchOption: System.IO.SearchOption.TopDirectoryOnly, iRemoveRootPath: true);
                iDataDic.SetData(AllDirsNameKey, aAllDirsName);
                if (iIsShowFiles)
                {
                    var aAllFilesName = iFileExplorer.GetFiles(aPath, iSearchOption: System.IO.SearchOption.TopDirectoryOnly, iRemoveRootPath: true);
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
                if (iFileExplorer.DirectoryExists(string.IsNullOrEmpty(iRoot) ? aNewPath : iRoot + "/" + aNewPath))
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
                        aCount = System.Math.Min(aPaths.Length, iPathDisplayCount);
                    }
                    System.Text.StringBuilder aPathSB = new System.Text.StringBuilder();
                    for (int i = aCount; i >= 1; i--)
                    {
                        string aFolderName = aPaths[aPaths.Length - i];
                        if (i < aCount) aPathSB.Append('/');
                        aPathSB.Append(aFolderName);
                        string aSelectPath = aPathSB.ToString();
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

                        if (GUILayout.Button(aFolderName))
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
                if (!iDataDic.ContainsKey(AllDirsNameKey))
                {
                    aSelectDir(iPath);
                }
                string[] aDirs = iDataDic.GetData<string[]>(AllDirsNameKey);
                string[] aFiles = iDataDic.GetData<string[]>(AllFilesNameKey);
                if (!aDirs.IsNullOrEmpty() || (iIsShowFiles && !aFiles.IsNullOrEmpty()))//Select folder menu
                {
                    const int DirHeight = 25;
                    using (var aScope2 = new GUILayout.ScrollViewScope(iDataDic.GetData("FolderScrollPos", Vector2.zero), "box",
                        GUILayout.Height(System.Math.Min(6, aDirs.Length) * DirHeight + 6)))
                    {
                        iDataDic.SetData("FolderScrollPos", aScope2.scrollPosition);
                        for (int i = 0; i < aDirs.Length; i++)
                        {
                            string aDir = aDirs[i];
                            GUILayout.BeginHorizontal();
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
                            Label(aDir, FileLib.Lib.GetFolderName(iDataDic.GetData(ShowToggleKey, string.Empty)) == aDir ?
                                Color.yellow : Color.white);
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
                    }
                }
            }

            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            return iPath;
        }
    }
}