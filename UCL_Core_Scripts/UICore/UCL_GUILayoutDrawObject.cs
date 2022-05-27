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
            bool iIsAlwaysShowDetail = false, Func<string, string> iFieldNameFunc = null, System.Type iFieldType = null)
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
                if (iObj is string)
                {
                    aIsShowField = false;
                    aResultObj = GUILayout.TextField((string)iObj);
                }
                else if (aType.IsEnum)
                {
                    GUILayout.BeginHorizontal();
                    if (!string.IsNullOrEmpty(iDisplayName)) LabelAutoSize(iDisplayName);
                    string aTypeName = aType.Name;
                    aResultObj = PopupAuto((System.Enum)iObj, (iEnum) => UCL_LocalizeLib.GetEnumLocalize(aTypeName, iEnum), iDataDic);
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
                else if (iObj is IList)
                {
                    bool aIsMoveElement = false;
                    bool aIsDelete = false;

                    GUILayout.BeginHorizontal();
                    if (!iIsAlwaysShowDetail) aIsShowField = Toggle(iDataDic, "Show");
                    GUILayout.BeginVertical();
                    using(new GUILayout.HorizontalScope())//Show Title(iDisplayName)
                    {
                        if (iIsAlwaysShowDetail)
                        {
                            aIsShowField = true;
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(iDisplayName)) UCL_GUILayout.LabelAutoSize(iDisplayName);
                        }

                        if (aIsShowField)
                        {
                            {
                                GUILayout.Space(5);
                                aIsMoveElement = BoolField(iDataDic, "MoveElement");
                                UCL_GUILayout.LabelAutoSize(UCL_LocalizeManager.Get("MoveElement"));
                            }
                            {
                                GUILayout.Space(5);
                                aIsDelete = BoolField(iDataDic, "Delete");
                                UCL_GUILayout.LabelAutoSize(UCL_LocalizeManager.Get("Delete"));
                            }
                        }
                        GUILayout.FlexibleSpace();
                    }

                    if (aIsShowField)
                    {
                        IList aList = iObj as IList;
                        string aCountKey = "Count";
                        int aCount = iDataDic.GetData(aCountKey, aList.Count);
                        GUILayout.BeginHorizontal();
                        int aNewCount = UCL_GUILayout.IntField(UCL_LocalizeManager.Get("Count"), aCount, GUILayout.MinWidth(80));
                        iDataDic.SetData(aCountKey, aNewCount);
                        if (aNewCount != aList.Count)
                        {
                            if (GUILayout.Button(UCL_LocalizeManager.Get("SetCount")))
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
                            string aITypeListKey = "ITypeList";
                            List<string> aTypeNameList = null;
                            if (iDataDic.ContainsKey(aITypeListKey))
                            {
                                aTypeNameList = iDataDic.GetData<List<string>>(aITypeListKey);
                            }
                            else
                            {
                                var aGenericType = aType.GetGenericValueType();

                                if (typeof(UCLI_TypeList).IsAssignableFrom(aGenericType))
                                {
                                    var aTypeList = aGenericType.CreateInstance() as UCLI_TypeList;
                                    if (aTypeList != null)
                                    {
                                        var aAllTypeList = aTypeList.GetAllTypes();
                                        aTypeNameList = new List<string>();
                                        for (int i = 0; i < aAllTypeList.Count; i++)
                                        {
                                            string aName = aAllTypeList[i].Name;
                                            if (UCL_LocalizeManager.ContainsKey(aName))
                                            {
                                                aName = string.Format("{0}({1})", UCL_LocalizeManager.Get(aName), aName);
                                            }
                                            aTypeNameList.Add(aName);
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
                                aSelectedType = PopupAuto(aTypeNameList, iDataDic, "SelectType", 10, GUILayout.Width(240));
                            }
                            if (GUILayout.Button(UCL_LocalizeManager.Get("Add"), GUILayout.Width(80)))
                            {
                                try
                                {
                                    var aGenericType = aType.GetGenericValueType();
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
                        var aListType = iObj.GetType().GetGenericValueType();
                        string aTypeName = aListType.Name;
                        int aMove = -1;
                        foreach (var aListData in aList)
                        {
                            if (aAt > 0 && aIsMoveElement)
                            {
                                //const int Height = 16;
                                using (new GUILayout.HorizontalScope("box"))//, GUILayout.Height(Height)
                                {
                                    GUILayout.FlexibleSpace();//GUILayout.Space(25);
                                    if (GUILayout.Button("▲", GUILayout.ExpandWidth(false)))//, GUILayout.Height(Height)
                                    {
                                        aMove = aAt - 1;
                                    }
                                    if (GUILayout.Button("▼", GUILayout.ExpandWidth(false)))//, GUILayout.Height(Height)
                                    {
                                        aMove = aAt - 1;
                                    }
                                    GUILayout.FlexibleSpace();
                                }
                            }
                            using (new GUILayout.HorizontalScope("box"))
                            {
                                if (aIsDelete)
                                {
                                    if (GUILayout.Button(UCL_LocalizeManager.Get("Delete"), GUILayout.ExpandWidth(false)))
                                    {
                                        aDeleteAt = aAt;
                                    }
                                }
                                string aDisplayName = aListData.UCL_GetShortName(aListData != null? aListData.GetType().Name : aTypeName);
                                if (aListData!= null)
                                {

                                }
                                var aResult = DrawObjectData(aListData, iDataDic.GetSubDic("IList", aAt++),
                                    aDisplayName, iFieldNameFunc: iFieldNameFunc, iFieldType: aListType);
                                aResultList.Add(aResult);
                            }
                        }
                        if (aMove >= 0 && aMove < aResultList.Count - 1)
                        {
                            aResultList.SwapElement(aMove, aMove + 1);
                            iDataDic.Swap("IList", aMove, aMove + 1);
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
                    GUILayout.EndVertical();
                    GUILayout.EndHorizontal();
                }
                else if (iObj is IDictionary)
                {
                    bool aIsDelete = false;
                    GUILayout.BeginHorizontal();
                    if (!iIsAlwaysShowDetail) aIsShowField = Toggle(iDataDic, "Show");
                    GUILayout.BeginVertical();
                    using (new GUILayout.HorizontalScope())//Show Title(iDisplayName)
                    {
                        if (iIsAlwaysShowDetail)
                        {
                            aIsShowField = true;
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(iDisplayName)) UCL_GUILayout.LabelAutoSize(iDisplayName);
                        }

                        if (aIsShowField)
                        {
                            {
                                GUILayout.Space(5);
                                aIsDelete = BoolField(iDataDic, "Delete");
                                UCL_GUILayout.LabelAutoSize(UCL_LocalizeManager.Get("Delete"));
                            }
                        }
                        GUILayout.FlexibleSpace();
                    }
                    if (aIsShowField)
                    {
                        IDictionary aDic = iObj as IDictionary;
                        using(new GUILayout.HorizontalScope("box"))
                        {
                            var aKeyType = aType.GetGenericKeyType();
                            string aAddKey = "AddData";
                            if (!iDataDic.ContainsKey(aAddKey))
                            {
                                iDataDic.SetData(aAddKey, aKeyType.CreateInstance());
                            }
                            iDataDic.SetData(aAddKey, DrawObjectData(iDataDic.GetData(aAddKey),
                                iDataDic.GetSubDic(iDisplayName + "_AddKey"), UCL_LocalizeManager.Get(aKeyType.Name), iFieldNameFunc: iFieldNameFunc));
                            using(new GUILayout.HorizontalScope("box"))
                            {
                                if (GUILayout.Button(UCL_LocalizeManager.Get("Add"), GUILayout.Width(80)))
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
                            }

                        }


                        object aDeleteAt = null;
                        List<Tuple<object, object>> aResultList = new List<Tuple<object, object>>();
                        foreach (var aKey in aDic.Keys)
                        {
                            using (new GUILayout.HorizontalScope("box"))
                            {
                                if (aIsDelete)
                                {
                                    if (GUILayout.Button(UCL_LocalizeManager.Get("Delete"), GUILayout.ExpandWidth(false)))
                                    {
                                        aDeleteAt = aKey;
                                    }
                                }
                                using(new GUILayout.VerticalScope())
                                {
                                    string aKeyName = aKey.UCL_GetShortName(aKey.UCL_ToString());
                                    GUILayout.Label(aKeyName);
                                    aResultList.Add(new Tuple<object, object>(aKey, DrawObjectData(aDic[aKey],
                                        iDataDic.GetSubDic(iDisplayName + "Dic_" + aKeyName), aKeyName, iFieldNameFunc: iFieldNameFunc)));
                                }
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
                    GUILayout.EndVertical();
                    GUILayout.EndHorizontal();
                }
                else if (iObj is Color)
                {
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
                else if (iObj.GetType().IsStructOrClass())
                {
                    aIsDefaultType = false;
                }
            }
            if (!aIsDefaultType)
            {
                GUILayout.BeginHorizontal();
                if (!iIsAlwaysShowDetail) aIsShowField = UCL_GUILayout.Toggle(iDataDic, "IsShowField");
                else aIsShowField = true;
                GUILayout.BeginVertical();
                if (!iIsAlwaysShowDetail)
                {
                    GUILayout.BeginHorizontal();
                
                    if (!string.IsNullOrEmpty(iDisplayName)) UCL_GUILayout.LabelAutoSize(iDisplayName);

                    if (iObj is UCLI_CopyPaste)
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(UCL_LocalizeManager.Get("Copy"), GUILayout.ExpandWidth(false)))
                        {
                            UCL.Core.CopyPaste.SetCopyData(iObj);
                        }
                        bool aCanCopy = UCL.Core.CopyPaste.HasCopyData(iFieldType);
                        if (GUILayout.Button(UCL_LocalizeManager.Get("Paste"), UCL.Core.UI.UCL_GUIStyle.GetButtonText(aCanCopy ? Color.white : Color.red),
                            GUILayout.ExpandWidth(false)))
                        {
                            if (aCanCopy)
                            {
                                iDataDic.Clear();

                                if (iObj != null && aType == CopyPaste.s_CopyType)
                                {
                                    UCL.Core.CopyPaste.LoadCopyData(iObj);
                                    aResultObj = iObj;
                                }
                                else
                                {
                                    iObj = aResultObj = UCL.Core.CopyPaste.GetCopyData();
                                }
                            }
                        }
                    }

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
                                if (!string.IsNullOrEmpty(aName)) aDisplayName += "(" + aName + ")";
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
                                    GUILayout.Label(aDisplayName, GUILayout.ExpandWidth(false));

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
                                        GUILayout.BeginHorizontal();
                                        var aResult = FolderExplorer(iDataDic.GetSubDic(aField.Name), aPath, aFolderExplorerAttribute.m_FolderRoot, aDisplayName,
                                            iIsShowFiles: false);
                                        GUILayout.EndHorizontal();
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
                                }else if(aAttr is PA.UCL_SliderAttribute)
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

                    }
                GUILayout.EndVertical();

                GUILayout.EndHorizontal();
            }

            GUILayout.EndVertical();
            return aResultObj;
        }
        #endregion
    }
}
