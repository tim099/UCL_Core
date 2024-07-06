using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UCL.Core.LocalizeLib;
using UnityEngine;

namespace UCL.Core.UI
{
    static public partial class UCL_GUILayout
    {
        public const string IsShowFieldKey = "IsShowField";
        public class DrawObjExSetting
        {
            public System.Action OnShowField;
        }
        #region DrawObject
        public class DrawObjectConfigs
        {
            public Func<string, string> m_FieldNameFunc;
            public System.Action<System.Action> m_OverrideDrawElement;
            public DrawObjectConfigs() {
                m_FieldNameFunc = UCL_StaticFunctions.LocalizeFieldName;
            }
            public DrawObjectConfigs(Func<string, string> iFieldNameFunc) {
                m_FieldNameFunc = iFieldNameFunc;
                if(m_FieldNameFunc == null)
                {
                    m_FieldNameFunc = UCL_StaticFunctions.LocalizeFieldName;
                }
            }
        }
        public class DrawObjectParams
        {
            /// <summary>
            /// dictionary to save display data
            /// </summary>
            public UCL_ObjectDictionary m_DataDic;
            /// <summary>
            /// the name show when hide detail
            /// </summary>
            public string m_DisplayName;
            public bool m_IsAlwaysShowDetail;
            
            public System.Type m_FieldType;
            public DrawObjExSetting m_DrawObjExSetting;
            public DrawObjectConfigs m_DrawObjectConfigs;
            public DrawObjectParams() { }

            public DrawObjectParams(UCL_ObjectDictionary iDataDic, string iDisplayName, bool iIsAlwaysShowDetail = false, 
                System.Type iFieldType = null, DrawObjExSetting iDrawObjExSetting = null, DrawObjectConfigs iDrawObjectConfigs = null) {

                m_DataDic = iDataDic;
                m_DisplayName = iDisplayName;
                m_FieldType = iFieldType;
                m_IsAlwaysShowDetail = iIsAlwaysShowDetail;
                
                if(iDrawObjectConfigs == null)
                {
                    iDrawObjectConfigs = new DrawObjectConfigs();
                }
                m_DrawObjectConfigs = iDrawObjectConfigs;

                //if (iDrawObjExSetting == null)
                //{
                //    iDrawObjExSetting = new DrawObjExSetting();
                //}
                m_DrawObjExSetting = iDrawObjExSetting;
            }

            public DrawObjectParams CreateChild(UCL_ObjectDictionary iDataDic = null, string iDisplayName = null, bool iIsAlwaysShowDetail = false)
            {
                var aChild = new DrawObjectParams();
                aChild.m_DrawObjectConfigs = m_DrawObjectConfigs;//inherit config
                aChild.m_DataDic = iDataDic;
                aChild.m_DisplayName = iDisplayName;
                aChild.m_IsAlwaysShowDetail = iIsAlwaysShowDetail;
                return aChild;
            }
            public string GetDisplayName(Type iType)
            {
                if (string.IsNullOrEmpty(m_DisplayName))
                {
                    return iType.Name;
                }
                return m_DisplayName;
            }
        }
        /// <summary>
        /// Draw a object inspector using GUILayout
        /// </summary>
        /// <param name="iObj">target object</param>
        /// <param name="iDataDic">dictionary to save display data</param>
        /// <param name="iDisplayName">the name show when hide detail</param>
        /// <param name="iIsAlwaysShowDetail">if set to true then will not show the detail toggle</param>
        /// <param name="iFieldNameFunc">param is the field name and return the display name</param>
        /// <returns></returns>
        public static object DrawObjectData(object iObj, UCL_ObjectDictionary iDataDic, string iDisplayName = "",
            bool iIsAlwaysShowDetail = false, Func<string, string> iFieldNameFunc = null, System.Type iFieldType = null
            , DrawObjExSetting iDrawObjExSetting = null)
        {
            return DrawObjectData(iObj, new DrawObjectParams(iDataDic, iDisplayName, iIsAlwaysShowDetail, iFieldType, iDrawObjExSetting, new DrawObjectConfigs(iFieldNameFunc)));
        }
        private static Dictionary<System.Type, System.Func<object, DrawObjectParams, object>> s_DrawObjectDic = null;
        /// <summary>
        /// Draw a object inspector using GUILayout
        /// </summary>
        /// <param name="iTarget"></param>
        /// <param name="iDrawObjectParams"></param>
        /// <returns></returns>
        public static object DrawObjectData(object iTarget, DrawObjectParams iDrawObjectParams)
        {
            GUILayout.BeginVertical();
            bool aIsShowField = true;
            var aResult = iTarget;
            Type aType = null;
            if (iTarget != null)
            {
                aType = iTarget.GetType();

                if (s_DrawObjectDic == null)
                {
                    s_DrawObjectDic = new Dictionary<Type, Func<object, DrawObjectParams, object>>();
                }
                if (!s_DrawObjectDic.ContainsKey(aType))
                {
                    if (iTarget is string)
                    {
                        object DrawString(object iObj, DrawObjectParams aParams)
                        {
                            return GUILayout.TextArea((string)iObj, UCL_GUIStyle.TextAreaStyle);
                        }
                        s_DrawObjectDic[aType] = DrawString;
                    }
                    else if (iTarget is bool)
                    {
                        object DrawFlag(object iObj, DrawObjectParams aParams)
                        {
                            return UCL_GUILayout.CheckBox((bool)iObj);
                        }
                        s_DrawObjectDic[aType] = DrawFlag;
                    }
                    else if (iTarget is UCLI_FieldOnGUI)
                    {
                        object Draw(object iObj, DrawObjectParams aParams)
                        {
                            return (iObj as UCLI_FieldOnGUI).OnGUI(aParams.GetDisplayName(aType), aParams.m_DataDic);
                        }
                        s_DrawObjectDic[aType] = Draw;
                    }
                    else if (aType.IsEnum)
                    {
                        object Draw(object iObj, DrawObjectParams aParams)
                        {
                            GUILayout.BeginHorizontal();
                            GUILayout.Label(aParams.GetDisplayName(aType), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            var resultObj = PopupAuto((System.Enum)iObj, aParams.m_DataDic);
                            GUILayout.EndHorizontal();
                            return resultObj;
                        }
                        s_DrawObjectDic[aType] = Draw;
                    }
                    else if (iTarget.IsNumber())
                    {
                        object Draw(object iObj, DrawObjectParams aParams)
                        {
                            return UCL_GUILayout.NumField(string.Empty, iObj, aParams.m_DataDic);
                        }
                        s_DrawObjectDic[aType] = Draw;
                    }
                    else if (aType.IsTuple())
                    {
                        object Draw(object iObj, DrawObjectParams aParams)
                        {
                            object aResultObj = iObj;
                            aIsShowField = false;
                            var aResult = iObj.GetTupleElements();
                            bool aIsValueChanged = false;
                            GUILayout.BeginVertical();
                            for (int i = 0; i < aResult.Count; i++)
                            {
                                var aTupleData = aResult[i];
                                var aResultData = DrawObjectData(aTupleData, 
                                    aParams.CreateChild(aParams.m_DataDic.GetSubDic("_" + i.ToString()), aTupleData.UCL_GetShortName()));
                                //var aResultData = DrawObjectData(aTupleData, aDataDic.GetSubDic("_" + i.ToString()), aTupleData.UCL_GetShortName(), iFieldNameFunc: aFieldNameFunc);
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
                            return aResultObj;
                        }
                        s_DrawObjectDic[aType] = Draw;
                    }
                    else if (iTarget is IList)
                    {
                        object Draw(object iObj, DrawObjectParams aParams)
                        {
                            DrawList(iObj as IList, aParams);
                            return iObj;
                        }
                        s_DrawObjectDic[aType] = Draw;
                        
                    }
                    else if (iTarget is IDictionary)
                    {
                        object Draw(object iObj, DrawObjectParams aParams)
                        {
                            DrawDictionary(iObj as IDictionary, aParams);
                            return iObj;
                        }
                        s_DrawObjectDic[aType] = Draw;
                    }
                    else if (iTarget is Color)
                    {
                        object Draw(object iObj, DrawObjectParams aParams)
                        {
                            object aResultObj = iObj;
                            var aOriginCol = (Color)iObj;
                            using (new GUILayout.HorizontalScope())
                            {
                                bool aIsShow = Toggle(aParams.m_DataDic, "Toggle");
                                GUILayout.BeginVertical();
                                UCL_GUILayout.LabelAutoSize(string.Format("{0}{1}", aParams.GetDisplayName(aType), "■".RichTextColor(aOriginCol)));
                                if (aIsShow)
                                {
                                    aResultObj = SelectColor(aOriginCol);
                                }
                                GUILayout.EndVertical();
                            }
                            return aResultObj;
                        }
                        s_DrawObjectDic[aType] = Draw;
                    }
                    else if (iTarget is Component)
                    {
                        object Draw(object iObj, DrawObjectParams aParams)
                        {
                            object aResultObj = iObj;
                            if (iObj is Transform)
                            {
                                var aDataDic = aParams.m_DataDic;
                                var aTransform = iObj as Transform;
                                using (new GUILayout.VerticalScope("box"))
                                {
                                    GUILayout.BeginHorizontal();
                                    aIsShowField = UCL_GUILayout.Toggle(aDataDic, IsShowFieldKey);
                                    GUILayout.Label("Transform", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                                    GUILayout.EndHorizontal();

                                    if (aIsShowField)
                                    {
                                        aTransform.position = VectorField("Position", aTransform.position, aDataDic.GetSubDic("Position"));
                                        aTransform.eulerAngles = VectorField("Rotation", aTransform.eulerAngles, aDataDic.GetSubDic("Rotation"));
                                        aTransform.localScale = VectorField("Scale", aTransform.localScale, aDataDic.GetSubDic("Scale"));
                                    }
                                    aResultObj = aTransform;
                                }
                            }
                            else
                            {
                                aResultObj = DrawField(aResultObj, aParams);
                                //aResultObj = DrawField(aResultObj, aDataDic, aDisplayName, aIsAlwaysShowDetail, aFieldNameFunc, aFieldType);
                            }
                            return aResultObj;
                        }
                        s_DrawObjectDic[aType] = Draw;
                    }
                    else if (aType.IsStructOrClass())
                    {
                        object Draw(object iObj, DrawObjectParams aParams)
                        {
                            var aResultObj = iObj;
                            if (aIsShowField)
                            {
                                aResultObj = DrawField(iObj, aParams);
                                //aResultObj = DrawField(aResultObj, aDataDic, aDisplayName, aIsAlwaysShowDetail, aFieldNameFunc, aFieldType);
                                var aDrawObjExSetting = aParams.m_DrawObjExSetting;
                                if (aDrawObjExSetting != null)
                                {
                                    aDrawObjExSetting.OnShowField?.Invoke();
                                }
                            }
                            return aResultObj;
                        }
                        s_DrawObjectDic[aType] = Draw;
                    }
                    else
                    {
                        object Draw(object iObj, DrawObjectParams aParams)
                        {

                            GUILayout.Label($"{iObj}, Type:{aType.FullName}, not supported yet!", UCL_GUIStyle.LabelStyle);
                            return iObj;
                        }
                        s_DrawObjectDic[aType] = Draw;
                    }
                }
                //GUILayout.Label($"Type:{aType.FullName}", UCL_GUIStyle.LabelStyle);
                aResult = s_DrawObjectDic[aType].Invoke(iTarget, iDrawObjectParams);
            }


            GUILayout.EndVertical();
            return aResult;
        }

        /// <summary>
        /// Draw all Field OnGUI
        /// </summary>
        /// <param name="iObj">target object</param>
        /// <param name="iDataDic"></param>
        /// <param name="iDisplayName"></param>
        /// <param name="iIsAlwaysShowDetail"></param>
        /// <param name="iFieldNameFunc"></param>
        /// <param name="iFieldType"></param>
        /// <returns></returns>
        public static object DrawField(object iObj, UCL_ObjectDictionary iDataDic, string iDisplayName = "",
            bool iIsAlwaysShowDetail = false, Func<string, string> iFieldNameFunc = null, System.Type iFieldType = null
            , DrawObjExSetting iDrawObjExSetting = null)
        {
            if (iObj == null) return null;
            var aParams = new DrawObjectParams(iDataDic, iDisplayName, iIsAlwaysShowDetail, iFieldType, iDrawObjExSetting, new DrawObjectConfigs(iFieldNameFunc));
            return DrawField(iObj, aParams);
        }

        public class FieldInfoCache
        {
            public FieldInfo m_FieldInfo;
            public bool m_AlwaysExpendOnGUI;
            public IEnumerable<Attribute> m_Attrs;
            public string m_Header;
            public FieldInfoCache() { }
            public FieldInfoCache(FieldInfo iFieldInfo)
            {
                m_FieldInfo = iFieldInfo;
                var aHeader = m_FieldInfo.GetCustomAttribute<HeaderAttribute>(true);
                if (aHeader != null)
                {
                    m_Header = aHeader.header;
                }
                m_AlwaysExpendOnGUI = (m_FieldInfo.FieldType.GetCustomAttribute<ATTR.AlwaysExpendOnGUI>() != null);
                m_Attrs = m_FieldInfo.GetCustomAttributes();
            }
        }

        public class TypeFieldInfoCache
        {
            public bool m_EnableUCLEditor;
            public List<FieldInfoCache> m_FieldInfos = new List<FieldInfoCache>();
            public IList<MethodInfo> m_AllMethods;
            

            public TypeFieldInfoCache() { }
            public TypeFieldInfoCache(System.Type aType)
            {
                m_EnableUCLEditor = (aType.GetCustomAttribute<ATTR.EnableUCLEditor>(true) != null);
                if (m_EnableUCLEditor)
                {
                    m_AllMethods = aType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                var aFieldInfos = aType.GetAllFieldsUnityVer(typeof(object));
                foreach(var aFieldInfo in aFieldInfos)
                {
                    bool aHideInInspector = (aFieldInfo.GetCustomAttribute<HideInInspector>() != null || aFieldInfo.GetCustomAttribute<ATTR.UCL_HideOnGUIAttribute>(false) != null);
                    if (!aHideInInspector)//Ignore HideInInspector Field
                    {
                        var aFieldInfoCache = new FieldInfoCache(aFieldInfo);
                        m_FieldInfos.Add(aFieldInfoCache);
                    }
                }
            }
        }

        private static Dictionary<System.Type, TypeFieldInfoCache> s_TypeFieldInfoCacheDic = null;
        /// <summary>
        /// Draw all Field OnGUI
        /// </summary>
        /// <param name="iObj"></param>
        /// <param name="iParams"></param>
        /// <returns></returns>
        public static object DrawField(object iObj, DrawObjectParams iParams)
        {
            if (iObj == null) return null;


            var iFieldNameFunc = iParams.m_DrawObjectConfigs.m_FieldNameFunc;
            var iFieldType = iParams.m_FieldType;
            var iIsAlwaysShowDetail = iParams.m_IsAlwaysShowDetail;
            var iDataDic = iParams.m_DataDic;
            

            //if (iFieldNameFunc == null) iFieldNameFunc = UCL_StaticFunctions.LocalizeFieldName;
            bool aIsShowField = true;
            object aResultObj = iObj;
            Type aType = iObj.GetType();
            if (iFieldType == null) iFieldType = aType;


            GUILayout.BeginHorizontal();
            if (!iIsAlwaysShowDetail)
            {
                aIsShowField = UCL_GUILayout.Toggle(iDataDic, IsShowFieldKey);
            }
            //else
            //{
            //    aIsShowField = true;
            //}
            GUILayout.BeginVertical();
            if (!iIsAlwaysShowDetail)
            {
                var iDisplayName = iParams.GetDisplayName(aType);
                GUILayout.BeginHorizontal();
                if (iObj is UCLI_NameOnGUI aNameOnGUI)
                {
                    aNameOnGUI.NameOnGUI(iDataDic, iDisplayName);
                }
                else
                {
                    if (iObj is UCL.Core.UCLI_Icon aIcon)
                    {
                        var aTexture = aIcon.IconTexture;
                        if (aTexture != null)
                        {
                            float aSize = UCL_GUIStyle.GetScaledSize(24);
                            using (new GUILayout.VerticalScope(GUILayout.Width(aSize), GUILayout.Height(aSize)))
                            {
                                GUILayout.FlexibleSpace();
                                GUILayout.Box(aTexture, GUILayout.Width(aSize), GUILayout.Height(aSize));
                                GUILayout.FlexibleSpace();
                            }
                        }
                    }
                    if (iObj is UCL.Core.UI.UCLI_IsEnable aEnable)
                    {
                        aEnable.IsEnable = UCL_GUILayout.CheckBox(aEnable.IsEnable);
                    }
                    GUILayout.Label(iDisplayName, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                }

                if (iObj is UCLI_CopyPaste)
                {
                    if (DrawCopyPaste(ref iObj, iDataDic, iFieldType))
                    {
                        aIsShowField = false;
                        aResultObj = iObj;
                    }
                }
                //else
                //{
                //    GUILayout.Label("!UCLI_CopyPaste :" + aType.Name);
                //}
                GUILayout.EndHorizontal();
            }

            if (aIsShowField)
            {
                using (var aScope = new GUILayout.VerticalScope())
                {
                    if(s_TypeFieldInfoCacheDic == null)
                    {
                        s_TypeFieldInfoCacheDic = new Dictionary<Type, TypeFieldInfoCache>();
                    }
                    if (!s_TypeFieldInfoCacheDic.ContainsKey(aType))
                    {
                        var aFieldInfoCache = new TypeFieldInfoCache(aType);
                        s_TypeFieldInfoCacheDic[aType] = aFieldInfoCache;
                    }
                    TypeFieldInfoCache aInfoCache = s_TypeFieldInfoCacheDic[aType];

                    if (aInfoCache.m_EnableUCLEditor)
                    {
                        IList<MethodInfo> aAllMethods = aInfoCache.m_AllMethods;
                        if (aAllMethods.Count > 0)
                        {
                            GUILayout.BeginVertical();
                            for (int i = 0; i < aAllMethods.Count; i++)
                            {
                                var aMethod = aAllMethods[i];
                                var aMethodDic = iDataDic.GetSubDic("Method", i);
                                {
                                    var aAttrType = typeof(ATTR.UCL_Attribute);
                                    var aAttrs = aMethod.GetCustomAttributes(aAttrType, false);
                                    if (aAttrs.Length > 0)
                                    {
                                        for (int j = 0; j < aAttrs.Length; j++)
                                        {
                                            var aAttr = (ATTR.UCL_Attribute)aAttrs[j];
                                            try
                                            {
                                                aAttr.Draw(iObj, aMethod, aMethodDic.GetSubDic("Attr", j));
                                            }
                                            catch (Exception iE)
                                            {
                                                Debug.LogException(iE);
                                            }
                                        }
                                    }
                                }
                            }
                            GUILayout.EndVertical();
                        }
                    }


                    var aFieldInfos = aInfoCache.m_FieldInfos;
                    foreach (var aFieldInfoCache in aFieldInfos)
                    {
                        var aFieldInfo = aFieldInfoCache.m_FieldInfo;
                        var aData = aFieldInfo.GetValue(iObj);


                        var aHeader = aFieldInfoCache.m_Header;//aFieldInfo.GetCustomAttribute<HeaderAttribute>(true);
                        if (!string.IsNullOrEmpty(aHeader))
                        {
                            GUILayout.Box(aHeader, UI.UCL_GUIStyle.BoxStyle);
                        }

                        if (aData == null)
                        {
                            if (typeof(IList).IsAssignableFrom(aFieldInfo.FieldType) || typeof(IDictionary).IsAssignableFrom(aFieldInfo.FieldType))
                            {
                                aData = aFieldInfo.FieldType.CreateInstance();
                                aFieldInfo.SetValue(iObj, aData);
                            }
                        }

                        string aDisplayName = aFieldInfo.Name;
                        string aDataKey = "_" + aDisplayName;

                        aDisplayName = iFieldNameFunc(aDisplayName);
                        var aShortName = aData as UCL.Core.UCLI_ShortName;
                        if (aShortName != null)
                        {
                            var aName = aShortName.GetShortName();
                            if (!string.IsNullOrEmpty(aName)) aDisplayName += $"({aName})";
                        }
                        bool aIsAlwaysShowDetail = aFieldInfoCache.m_AlwaysExpendOnGUI;

                        bool aIsDrawed = false;
                        IEnumerable<Attribute> aAttrs = aFieldInfoCache.m_Attrs;
                        foreach (var aAttr in aAttrs)
                        {
                            if (aAttr is IShowInCondition && !((IShowInCondition)aAttr).IsShow(iObj))
                            {
                                aIsDrawed = true;
                                break;
                            }
                            else if (aAttr is IStrList)
                            {
                                aIsDrawed = true;
                                var aStrArr = aAttr as IStrList;
                                GUILayout.BeginHorizontal();
                                GUILayout.Label(aDisplayName, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));

                                aFieldInfo.SetValue(iObj, aStrArr.DrawOnGUILocalized(iObj, aData, iDataDic, aFieldInfo.Name));
                                GUILayout.EndHorizontal();
                            }
                            else if (aAttr is ITexture2D)
                            {
                                var aTextureArr = aAttr as ITexture2D;
                                GUILayout.BeginHorizontal();
                                GUILayout.Box(aTextureArr.GetTexture(iObj, aData), GUILayout.Width(64), GUILayout.Height(64));
                                GUILayout.EndHorizontal();
                            }
                            else if (aAttr is SpaceAttribute)
                            {
                                GUILayout.Space((aAttr as SpaceAttribute).height);
                            }
                            else if (aAttr is UCL.Core.PA.UCL_FolderExplorerAttribute)
                            {
                                if (aFieldInfo.FieldType == typeof(string))
                                {
                                    aIsDrawed = true;
                                    var aFolderExplorerAttribute = aAttr as UCL.Core.PA.UCL_FolderExplorerAttribute;
                                    if (aData == null) aData = "";
                                    string aPath = (string)aData;
                                    var aResult = aFolderExplorerAttribute.OnGUI(iDataDic.GetSubDic(aFieldInfo.Name), aPath, aDisplayName);
                                    aFieldInfo.SetValue(iObj, aResult);
                                }
                            }
                            else if (aAttr is ATTR.AlwaysExpendOnGUI)
                            {
                                aIsAlwaysShowDetail = true;
                            }
                            else if (aAttr is PA.UCL_IntSliderAttribute)
                            {
                                if (aData is int)
                                {
                                    aIsDrawed = true;
                                    var aSlider = aAttr as PA.UCL_IntSliderAttribute;
                                    int aVal = (int)aData;
                                    int aResult = aSlider.OnGUI(aDisplayName, aVal, iDataDic.GetSubDic(aFieldInfo.Name));

                                    if (aResult != aVal)
                                    {
                                        aFieldInfo.SetValue(iObj, aResult);
                                    }
                                }
                            }
                            else if (aAttr is PA.UCL_SliderAttribute)
                            {
                                if (aData is float)
                                {
                                    aIsDrawed = true;
                                    var aSlider = aAttr as PA.UCL_SliderAttribute;
                                    float aVal = (float)aData;
                                    float aResult = aSlider.OnGUI(aDisplayName, aVal, iDataDic.GetSubDic(aFieldInfo.Name));

                                    if (aResult != aVal)
                                    {
                                        aFieldInfo.SetValue(iObj, aResult);
                                    }
                                }
                            }
                        }
                        if (aIsDrawed)
                        {
                            //aField.SetValue(iObj, aDropDownAttr.DrawOnGUI(iObj, aData, m_DataDic, "_" + aDisplayName));
                        }
                        else if (aFieldInfo.FieldType == typeof(bool))
                        {
                            //if (aData == null) aData = false; //bool is not nullable
                            aFieldInfo.SetValue(iObj, UCL.Core.UI.UCL_GUILayout.BoolField(aDisplayName, (bool)aData));
                        }
                        else if (aFieldInfo.FieldType == typeof(string))
                        {
                            if (aData == null) aData = "";
                            //UCL_GUIStyle.CurStyleData.TextAreaStyle
                            var aResult = UCL.Core.UI.UCL_GUILayout.TextArea(aDisplayName, (string)aData);
                            aFieldInfo.SetValue(iObj, aResult);
                        }
                        else if (aData == null)
                        {
                            //Debug.LogError("aData == null aField:" + aField.Name);
                        }
                        else if (aData is UCLI_FieldOnGUI)
                        {
                            UCLI_FieldOnGUI aVar = (UCLI_FieldOnGUI)aData;
                            aFieldInfo.SetValue(iObj, aVar.OnGUI(aDisplayName, iDataDic.GetSubDic(aFieldInfo.Name)));
                        }
                        else if (aData.IsNumber())
                        {
                            string aValKey = aDataKey + "_Val";
                            if (!iDataDic.ContainsKey(aDataKey))
                            {
                                iDataDic.SetData(aDataKey, aData.ToString());//Save NumberStr
                            }
                            if (!iDataDic.ContainsKey(aValKey))
                            {
                                iDataDic.SetData(aValKey, aData);//Save Val
                            }
                            string aNumStr = iDataDic.GetData(aDataKey, string.Empty);
                            var aResult = UCL_GUILayout.TextField(aDisplayName, aNumStr);
                            if (aResult != aNumStr)//Set value
                            {
                                iDataDic.SetData(aDataKey, aResult);
                                object aResVal;
                                if (UCL.Core.MathLib.Num.TryParse(aResult, aFieldInfo.FieldType, out aResVal))
                                {
                                    if (!aResVal.Equals(aData))
                                    {
                                        aFieldInfo.SetValue(iObj, aResVal);
                                        iDataDic.SetData(aValKey, aResVal);//Save Val
                                    }
                                }
                            }
                            else
                            {
                                if (!iDataDic.GetData(aValKey, aData).Equals(aData))//Check if value changed
                                {
                                    string aDataStr = aData.ToString();
                                    if (aDataStr != aNumStr)
                                    {
                                        iDataDic.SetData(aDataKey, aDataStr);
                                    }
                                    iDataDic.SetData(aValKey, aData);
                                }
                            }
                        }
                        else if (aData is IList or IDictionary)//aData is IList || aData is IDictionary
                        {
                            ICollection aList = aData as ICollection;//IList and IDictionary is ICollection
                            var aParams = iParams.CreateChild(iDataDic.GetSubDic(aFieldInfo.Name), $"{aDisplayName}({aList.Count})", aIsAlwaysShowDetail);
                            var aResult = DrawObjectData(aData, aParams);
                            //var aResult = DrawObjectData(aData, iDataDic.GetSubDic(aFieldInfo.Name), $"{aDisplayName}({aList.Count})", aAlwaysExpendOnGUI, iFieldNameFunc);
                            aFieldInfo.SetValue(iObj, aResult);
                        }
                        else if (aData is Color)
                        {
                            var aCol = (Color)aData;
                            var aParams = iParams.CreateChild(iDataDic.GetSubDic(aFieldInfo.Name), aDisplayName, aIsAlwaysShowDetail);
                            var aNewCol = (Color)DrawObjectData(aData, aParams);
                            //var aNewCol = (Color)DrawObjectData(aData, iDataDic.GetSubDic(aFieldInfo.Name), aDisplayName, aIsAlwaysShowDetail, iFieldNameFunc);
                            if (aNewCol != aCol)
                            {
                                aFieldInfo.SetValue(iObj, aNewCol);
                            }
                        }
                        else if (aFieldInfo.FieldType.IsEnum)
                        {
                            var aParams = iParams.CreateChild(iDataDic.GetSubDic(aFieldInfo.Name), aDisplayName, aIsAlwaysShowDetail);
                            var aResult = DrawObjectData(aData, aParams);
                            //var aResult = DrawObjectData(aData, iDataDic.GetSubDic(aFieldInfo.Name), aDisplayName, aIsAlwaysShowDetail, iFieldNameFunc);
                            if (aResult != aData)
                            {
                                aFieldInfo.SetValue(iObj, aResult);
                            }
                        }
                        else if (aFieldInfo.FieldType.IsStructOrClass())
                        {
                            var aParams = iParams.CreateChild(iDataDic.GetSubDic(aFieldInfo.Name + "_FieldData"), aDisplayName, aIsAlwaysShowDetail);
                            DrawObjectData(aData, aParams);
                            //DrawObjectData(aData, iDataDic.GetSubDic(aFieldInfo.Name + "_FieldData"), aDisplayName, aIsAlwaysShowDetail, iFieldNameFunc);
                            aFieldInfo.SetValue(iObj, aData);
                        }
                    }
                    var iDrawObjExSetting = iParams.m_DrawObjExSetting;
                    if (iDrawObjExSetting != null)
                    {
                        iDrawObjExSetting.OnShowField?.Invoke();
                    }
                }
            }
                
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            return aResultObj;
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="iObj"></param>
        /// <param name="iDataDic"></param>
        /// <param name="iFieldType"></param>
        /// <returns>return true if paste data</returns>
        public static bool DrawCopyPaste(ref object iObj, UCL_ObjectDictionary iDataDic, System.Type iFieldType = null)
        {
            bool aIsPaste = false;
            Type aType = iObj.GetType();
            if (iFieldType == null) iFieldType = aType;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(UCL_LocalizeManager.Get("Copy"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
            {
                UCLI_CopyPaste.s_Copying = true;
                UCL.Core.CopyPaste.SetCopyData(iObj);
                UCLI_CopyPaste.s_Copying = false;
            }
            bool aCanCopy = UCL.Core.CopyPaste.HasCopyData(iFieldType);
            if (GUILayout.Button(UCL_LocalizeManager.Get("Paste"), UCL_GUIStyle.GetButtonStyle(aCanCopy ? Color.white : Color.red),
                GUILayout.ExpandWidth(false)))
            {
                if (aCanCopy)
                {
                    UCLI_CopyPaste.s_Copying = true;
                    if (iObj != null && aType == CopyPaste.s_CopyType)
                    {
                        UCL.Core.CopyPaste.LoadCopyData(iObj);
                    }
                    else
                    {
                        iObj = UCL.Core.CopyPaste.GetCopyData();
                    }
                    aIsPaste = true;
                    iDataDic.Clear();
                    UCLI_CopyPaste.s_Copying = false;
                }
            }
            return aIsPaste;
        }
        public static class Preview
        {
            const int MaxRecursive = 10;
            static private Dictionary<Type, System.Action<object, UCL_ObjectDictionary, int>> s_OnGUICacheDic = new();
            public static void OnGUI(object iObj, UCL_ObjectDictionary iDataDic, int iSpace = 0)
            {
                try
                {
                    if (iSpace > MaxRecursive) return;
                    if (iObj == null)
                    {
                        GUILayout.Label("Null", UCL_GUIStyle.LabelStyle);
                        return;
                    }
                    Type aType = iObj.GetType();
                    if(!s_OnGUICacheDic.ContainsKey(aType))
                    {
                        if (aType.IsPrimitive || !aType.IsStructOrClass()
                            || (iObj is Enum or Vector4 or Vector3 or Vector2 or Vector3Int or Vector2Int))
                        {
                            void PrimitiveOnGUI(object iObj, UCL_ObjectDictionary iDataDic, int iSpace = 0)
                            {
                                GUILayout.Label(iObj.ToString(), UCL_GUIStyle.LabelStyle);
                            }
                            s_OnGUICacheDic[aType] = PrimitiveOnGUI;
                        }
                        else if (iObj is string)
                        {
                            void StringOnGUI(object iObj, UCL_ObjectDictionary iDataDic, int iSpace = 0)
                            {
                                GUILayout.Label((string)iObj, UCL_GUIStyle.LabelStyle);
                            }
                            s_OnGUICacheDic[aType] = StringOnGUI;
                        }
                        else if(iObj is JsonLib.JsonData)
                        {
                            void JsonDataOnGUI(object iObj, UCL_ObjectDictionary iDataDic, int iSpace = 0)
                            {
                                JsonLib.JsonData aJson = iObj as JsonLib.JsonData;
                                using (var aScope = new GUILayout.HorizontalScope())
                                {
                                    GUILayout.Label($"(Json):{aJson.ToJsonBeautify()}", UCL_GUIStyle.LabelStyle);
                                }
                            }
                            s_OnGUICacheDic[aType] = JsonDataOnGUI;
                        }
                        else if (iObj is IEnumerable)
                        {
                            s_OnGUICacheDic[aType] = IEnumerableOnGUI;
                        }
                        else
                        {
                            s_OnGUICacheDic[aType] = AllFieldsOnGUI;
                        }
                    }

                    s_OnGUICacheDic[aType].Invoke(iObj, iDataDic, iSpace);
                }
                catch (Exception iE)
                {
                    Debug.LogException(iE);
                    GUILayout.Label($"Exception:{iE}", UCL_GUIStyle.LabelStyle);
                }
            }
            //static private Dictionary<Type, System.Action<object, UCL_ObjectDictionary, int>> s_FieldOnGUICacheDic = new();
            public static void AllFieldsOnGUI(object iObj, UCL_ObjectDictionary iDataDic, int iSpace = 0)
            {
                Type aType = iObj.GetType();
                FieldInfo[] aFields = aType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (aFields.Length > 0)
                {
                    using (var aHScope = new GUILayout.HorizontalScope())
                    {
                        bool aIsShowField = UCL_GUILayout.Toggle(iDataDic, IsShowFieldKey);
                        using (var aScope = new GUILayout.VerticalScope())
                        {
                            using (var aScope2 = new GUILayout.HorizontalScope())
                            {
                                GUILayout.Label(aType.Name, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                            }
                            if (aIsShowField)
                            {
                                foreach (var aField in aFields)
                                {
                                    using (var aScope2 = new GUILayout.VerticalScope())
                                    {
                                        var aVal = aField.GetValue(iObj);
                                        Type aFieldType = aField.FieldType;
                                        if(aVal is string aStr)
                                        {
                                            GUILayout.Label($"({aFieldType.Name}){aField.Name} : {aStr}", UCL_GUIStyle.LabelStyle);
                                        }
                                        else if (aVal.IsNumber())
                                        {
                                            GUILayout.Label($"({aFieldType.Name}){aField.Name} : {aVal}", UCL_GUIStyle.LabelStyle);
                                        }
                                        else
                                        {
                                            using (var aScope3 = new GUILayout.HorizontalScope())
                                            {
                                                GUILayout.Label($"({aFieldType.GetTypeName()}){aField.Name} : ", UCL_GUIStyle.LabelStyle);
                                            }
                                            using (var aScope3 = new GUILayout.HorizontalScope())//"box"
                                            {
                                                OnGUI(aVal, iDataDic.GetSubDic($"Field_{aField.Name}"), iSpace + 1);
                                            }
                                        }

                                    }
                                }
                            }
                        }
                    }

                    return;
                }
                GUILayout.Label(iObj.ToString(), UCL_GUIStyle.LabelStyle);
            }

            public static void IEnumerableOnGUI(object iObj, UCL_ObjectDictionary iDataDic, int iSpace = 0)
            {
                IEnumerable aEnum = iObj as IEnumerable;
                if(aEnum == null)
                {
                    return;
                }
                using (var aScope = new GUILayout.VerticalScope())
                {
                    if (aEnum is IDictionary aDic)
                    {
                        using (var aScope2 = new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label($"({aEnum.GetType().Name}) : [", UCL_GUIStyle.LabelStyle);
                        }
                        foreach (var aKey in aDic.Keys)
                        {

                            using (var aScope2 = new GUILayout.HorizontalScope())
                            {
                                var aVal = aDic[aKey];
                                string aHash = aKey.GetHashCode().ToString();
                                OnGUI(aKey, iDataDic.GetSubDic($"{aHash}_Key"), iSpace + 1);
                                GUILayout.Label($":", UCL_GUIStyle.LabelStyle);
                                OnGUI(aVal, iDataDic.GetSubDic($"{aHash}_Val"), iSpace + 1);
                                GUILayout.Label($",", UCL_GUIStyle.LabelStyle);
                            }
                        }
                        using (var aScope2 = new GUILayout.HorizontalScope())
                        {
                            GUILayout.Label($"]", UCL_GUIStyle.LabelStyle);
                        }
                    }
                    else
                    {
                        bool aIsShowField = UCL_GUILayout.Toggle(iDataDic, IsShowFieldKey);
                        if (aIsShowField)
                        {
                            int aIndex = 0;
                            foreach (var aVal in aEnum)
                            {
                                using (var aScope2 = new GUILayout.HorizontalScope())
                                {
                                    OnGUI(aVal, iDataDic.GetSubDic($"{++aIndex}_Val"), iSpace + 1);
                                    //GUILayout.Label($",", UCL_GUIStyle.LabelStyle);
                                }
                            }
                        }

                    }
                }
            }
        }

        #endregion
    }
}
