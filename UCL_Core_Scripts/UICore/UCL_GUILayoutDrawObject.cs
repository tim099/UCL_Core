﻿using System;
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
            if (iFieldNameFunc == null) iFieldNameFunc = UCL_StaticFunctions.LocalizeFieldName;
            GUILayout.BeginVertical();
            bool aIsShowField = true;
            bool aIsDefaultType = true;
            object aResultObj = iObj;
            Type aType = null;
            if (iObj != null)
            {
                if (string.IsNullOrEmpty(iDisplayName))
                {
                    iDisplayName = iObj.GetType().Name;
                }
                aType = iObj.GetType();
                if (iFieldType == null) iFieldType = aType;
                if (iObj is string aStr)
                {
                    aIsShowField = false;
                    aResultObj = GUILayout.TextArea(aStr, UCL_GUIStyle.TextAreaStyle);
                }
                if(iObj is bool aflag)
                {
                    aIsShowField = false;
                    aResultObj = UCL_GUILayout.CheckBox(aflag);
                }
                if (iObj is UCLI_FieldOnGUI aFieldOnGUI)
                {
                    aIsShowField = false;
                    aResultObj = iObj = aFieldOnGUI.OnGUI(iDisplayName, iDataDic);
                }
                else if (aType.IsEnum)
                {
                    aIsShowField = false;
                    GUILayout.BeginHorizontal();
                    if (!string.IsNullOrEmpty(iDisplayName)) GUILayout.Label(iDisplayName, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    string aTypeName = aType.Name;
                    aResultObj = PopupAuto((System.Enum)iObj, iDataDic);
                    GUILayout.EndHorizontal();
                }
                else if (iObj.IsNumber())
                {
                    aIsShowField = false;
                    aResultObj = UCL_GUILayout.NumField(string.Empty, iObj, iDataDic);
                }
                else if (aType.IsTuple())
                {
                    aIsShowField = false;
                    var aResult = iObj.GetTupleElements();
                    bool aIsValueChanged = false;
                    GUILayout.BeginVertical();
                    for (int i = 0; i < aResult.Count; i++)
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
                else if (iObj is IList aList)
                {
                    DrawList(aList, iDataDic, iDisplayName, iIsAlwaysShowDetail, iFieldNameFunc);
                }
                else if (iObj is IDictionary aDic)
                {
                    DrawDictionary(aDic, iDataDic, iDisplayName, iIsAlwaysShowDetail, iFieldNameFunc);
                }
                else if (iObj is Color)
                {
                    aIsShowField = false;
                    var aOriginCol = (Color)iObj;
                    using (new GUILayout.HorizontalScope())
                    {
                        bool aIsShow = Toggle(iDataDic, "Toggle");
                        GUILayout.BeginVertical();
                        UCL_GUILayout.LabelAutoSize(string.Format("{0}{1}", iDisplayName, "■".RichTextColor(aOriginCol)));
                        if (aIsShow)
                        {
                            aResultObj = SelectColor(aOriginCol);
                        }
                        GUILayout.EndVertical();
                    }
                }
                else if (iObj is Component)
                {
                    if (iObj is Transform)
                    {
                        var aTransform = iObj as Transform;
                        using (new GUILayout.VerticalScope("box"))
                        {
                            GUILayout.BeginHorizontal();
                            aIsShowField = UCL_GUILayout.Toggle(iDataDic, IsShowFieldKey);
                            GUILayout.Label("Transform");
                            GUILayout.EndHorizontal();

                            if (aIsShowField)
                            {
                                aTransform.position = VectorField("Position", aTransform.position, iDataDic.GetSubDic("Position"));
                                aTransform.eulerAngles = VectorField("Rotation", aTransform.eulerAngles, iDataDic.GetSubDic("Rotation"));
                                aTransform.localScale = VectorField("Scale", aTransform.localScale, iDataDic.GetSubDic("Scale"));
                            }
                            aResultObj = aTransform;
                        }
                    }
                    else
                    {
                        aResultObj = DrawField(aResultObj, iDataDic, iDisplayName, iIsAlwaysShowDetail, iFieldNameFunc, iFieldType);
                    }
                }
                else if (aType.IsStructOrClass())
                {
                    aIsDefaultType = false;
                }
            }
            if (aIsShowField && !aIsDefaultType)
            {
                //GUILayout.Label("!aIsDefaultType:" + aResultObj.GetType().Name);
                aResultObj = DrawField(aResultObj, iDataDic, iDisplayName, iIsAlwaysShowDetail, iFieldNameFunc, iFieldType);
                if (iDrawObjExSetting != null)
                {
                    iDrawObjExSetting.OnShowField?.Invoke();
                }
            }

            GUILayout.EndVertical();
            return aResultObj;
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

            if (iFieldNameFunc == null) iFieldNameFunc = UCL_StaticFunctions.LocalizeFieldName;
            bool aIsShowField = true;
            object aResultObj = iObj;
            Type aType = iObj.GetType();
            if (iFieldType == null) iFieldType = aType;
            GUILayout.BeginHorizontal();
            if (!iIsAlwaysShowDetail) aIsShowField = UCL_GUILayout.Toggle(iDataDic, IsShowFieldKey);
            else aIsShowField = true;
            GUILayout.BeginVertical();
            if (!iIsAlwaysShowDetail)
            {
                GUILayout.BeginHorizontal();
                if (iObj is UCLI_NameOnGUI)
                {
                    var aNameOnGUI = iObj as UCLI_NameOnGUI;
                    aNameOnGUI.NameOnGUI(iDataDic, iDisplayName);
                }
                else
                {
                    if (iObj is UCL.Core.UCLI_Icon aIcon)
                    {
                        var aTexture = aIcon.IconTexture;
                        if (aTexture != null)
                        {
                            using (new GUILayout.VerticalScope(GUILayout.Width(24), GUILayout.Height(24)))
                            {
                                GUILayout.FlexibleSpace();
                                GUILayout.Box(aTexture, GUILayout.Width(24), GUILayout.Height(24));
                                GUILayout.FlexibleSpace();
                            }
                        }
                    }
                    if(iObj is UCL.Core.UI.UCLI_IsEnable aEnable)
                    {
                        aEnable.IsEnable = UCL_GUILayout.CheckBox(aEnable.IsEnable);
                    }
                    if (!string.IsNullOrEmpty(iDisplayName)) GUILayout.Label(iDisplayName, UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
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

            if (aIsShowField) using (var aScope = new GUILayout.VerticalScope())
                {//"box"
                    if (aType.GetCustomAttribute<ATTR.EnableUCLEditor>(true) != null)
                    {

                        IList<MethodInfo> aAllMethods = aType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
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
                    var aFields = aType.GetAllFieldsUnityVer(typeof(object));
                    foreach (var aField in aFields)
                    {
                        var aData = aField.GetValue(iObj);

                        if (aField.GetCustomAttribute<HideInInspector>() != null
                            || aField.GetCustomAttribute<ATTR.UCL_HideOnGUIAttribute>(false) != null)
                        {
                            continue;
                        }
                        var aHeader = aField.GetCustomAttribute<HeaderAttribute>(true);
                        if (aHeader != null)
                        {
                            GUILayout.Box(aHeader.header, UI.UCL_GUIStyle.BoxStyle);
                        }
                        if (aData == null)
                        {
                            if (typeof(IList).IsAssignableFrom(aField.FieldType) || typeof(IDictionary).IsAssignableFrom(aField.FieldType))
                            {
                                aData = aField.FieldType.CreateInstance();
                                aField.SetValue(iObj, aData);
                            }
                        }

                        string aDisplayName = aField.Name;
                        string aDataKey = "_" + aDisplayName;

                        aDisplayName = iFieldNameFunc(aDisplayName);
                        var aShortName = aData as UCL.Core.UCLI_ShortName;
                        if (aShortName != null)
                        {
                            var aName = aShortName.GetShortName();
                            if (!string.IsNullOrEmpty(aName)) aDisplayName += $"({aName})";
                        }
                        bool aIsAlwaysShowDetail = aField.FieldType.GetCustomAttribute<ATTR.AlwaysExpendOnGUI>() != null;
                        bool aIsDrawed = false;
                        var aAttrs = aField.GetCustomAttributes();
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

                                aField.SetValue(iObj, aStrArr.DrawOnGUILocalized(iObj, aData, iDataDic, aField.Name));
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
                                if (aField.FieldType == typeof(string))
                                {
                                    aIsDrawed = true;
                                    var aFolderExplorerAttribute = aAttr as UCL.Core.PA.UCL_FolderExplorerAttribute;
                                    if (aData == null) aData = "";
                                    string aPath = (string)aData;
                                    var aResult = aFolderExplorerAttribute.OnGUI(iDataDic.GetSubDic(aField.Name), aPath, aDisplayName);
                                    aField.SetValue(iObj, aResult);
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
                                    int aResult = aSlider.OnGUI(aDisplayName, aVal, iDataDic.GetSubDic(aField.Name));

                                    if (aResult != aVal)
                                    {
                                        aField.SetValue(iObj, aResult);
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
                                    float aResult = aSlider.OnGUI(aDisplayName, aVal, iDataDic.GetSubDic(aField.Name));

                                    if (aResult != aVal)
                                    {
                                        aField.SetValue(iObj, aResult);
                                    }
                                }
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
                            //UCL_GUIStyle.CurStyleData.TextAreaStyle
                            var aResult = UCL.Core.UI.UCL_GUILayout.TextArea(aDisplayName, (string)aData);
                            aField.SetValue(iObj, aResult);
                        }
                        else if (aData == null)
                        {
                            //Debug.LogError("aData == null aField:" + aField.Name);
                        }
                        else if (aData is UCLI_FieldOnGUI)
                        {
                            UCLI_FieldOnGUI aVar = (UCLI_FieldOnGUI)aData;
                            aField.SetValue(iObj, aVar.OnGUI(aDisplayName, iDataDic.GetSubDic(aField.Name)));
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
                            if(aResult != aNumStr)//Set value
                            {
                                iDataDic.SetData(aDataKey, aResult);
                                object aResVal;
                                if (UCL.Core.MathLib.Num.TryParse(aResult, aField.FieldType, out aResVal))
                                {
                                    if (!aResVal.Equals(aData))
                                    {
                                        aField.SetValue(iObj, aResVal);
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
                        else if (aData is IList || aData is IDictionary)
                        {
                            ICollection aList = aData as ICollection;//IList and IDictionary is ICollection
                            aField.SetValue(iObj, DrawObjectData(aData, iDataDic.GetSubDic(aField.Name),
                                string.Format("{0}({1})", aDisplayName, aList.Count), aIsAlwaysShowDetail, iFieldNameFunc));
                        }
                        else if (aData is Color)
                        {
                            var aCol = (Color)aData;
                            var aNewCol = (Color)DrawObjectData(aData, iDataDic.GetSubDic(aField.Name),
                                aDisplayName, aIsAlwaysShowDetail, iFieldNameFunc);
                            if (aNewCol != aCol)
                            {
                                aField.SetValue(iObj, aNewCol);
                            }
                        }
                        else if (aField.FieldType.IsEnum)
                        {
                            var aResult = DrawObjectData(aData, iDataDic.GetSubDic(aField.Name),
                                aDisplayName, aIsAlwaysShowDetail, iFieldNameFunc);
                            if (aResult != aData)
                            {
                                aField.SetValue(iObj, aResult);
                            }
                        }
                        else if (aField.FieldType.IsStructOrClass())
                        {
                            DrawObjectData(aData, iDataDic.GetSubDic(aField.Name + "_FieldData"), aDisplayName, aIsAlwaysShowDetail, iFieldNameFunc);
                            aField.SetValue(iObj, aData);
                        }
                    }
                    if (iDrawObjExSetting != null)
                    {
                        iDrawObjExSetting.OnShowField?.Invoke();
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
        #endregion
    }
}
