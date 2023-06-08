using System;
using System.Collections;
using System.Collections.Generic;
using UCL.Core.LocalizeLib;
using UnityEngine;


namespace UCL.Core.UI
{
    static public partial class UCL_GUILayout
    {
        public const string IsMoveElementKey = "MoveElement";
        public const string IsDeleteElementKey = "Delete";

        private const string ListElementCountKey = "ListElementCount";
        private const string ITypeListKey = "ITypeList";
        public static void DrawList(IList aList, UCL_ObjectDictionary iDataDic, string iDisplayName = "",
            bool iIsAlwaysShowDetail = false, Func<string, string> iFieldNameFunc = null,
            System.Action<System.Action> iOverrideDrawElement = null)
        {
            bool aIsMoveElement = false;
            bool aIsDelete = false;
            bool aIsShowField = false;
            var aType = aList.GetType();
            GUILayout.BeginHorizontal();
            if (!iIsAlwaysShowDetail) aIsShowField = Toggle(iDataDic, IsShowFieldKey);
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
                        aIsMoveElement = BoolField(iDataDic, IsMoveElementKey);
                        UCL_GUILayout.LabelAutoSize(UCL_LocalizeManager.Get("MoveElement"));
                    }
                    {
                        GUILayout.Space(5);
                        aIsDelete = BoolField(iDataDic, IsDeleteElementKey);
                        UCL_GUILayout.LabelAutoSize(UCL_LocalizeManager.Get("Delete"));
                    }
                }
                GUILayout.FlexibleSpace();
            }

            if (aIsShowField)
            {
                int aCount = iDataDic.GetData(ListElementCountKey, aList.Count);
                GUILayout.BeginHorizontal();
                int aNewCount = UCL_GUILayout.IntField(UCL_LocalizeManager.Get("Count"), aCount, GUILayout.MinWidth(80));
                iDataDic.SetData(ListElementCountKey, aNewCount);
                if (aNewCount != aList.Count)
                {
                    if (GUILayout.Button(UCL_LocalizeManager.Get("SetCount"), UCL_GUIStyle.ButtonStyle))
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
                                var aGenericType = aType.GetGenericValueType();
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

                    List<string> aTypeNameList = null;
                    if (iDataDic.ContainsKey(ITypeListKey))
                    {
                        aTypeNameList = iDataDic.GetData<List<string>>(ITypeListKey);
                    }
                    else
                    {
                        var aGenericType = aType.GetGenericValueType();

                        if (typeof(UCLI_TypeList).IsAssignableFrom(aGenericType))
                        {
                            var aTypeList = aGenericType.CreateInstance() as UCLI_TypeList;
                            if (aTypeList != null)
                            {
                                System.Func<string, string> aNameFunc = null;
                                if (aTypeList is UCLI_GetTypeName)
                                {
                                    var aListName = (UCLI_GetTypeName)(aTypeList);
                                    aNameFunc = (iName) => aListName.GetTypeName(iName);
                                }
                                else
                                {
                                    aNameFunc = UCL.Core.LocalizeLib.UCL_LocalizeLib.GetLocalize;
                                }
                                var aAllTypeList = aTypeList.GetAllTypes();
                                aTypeNameList = new List<string>();
                                for (int i = 0; i < aAllTypeList.Count; i++)
                                {
                                    aTypeNameList.Add(aNameFunc(aAllTypeList[i].Name));
                                }
                                iDataDic.Add(ITypeListKey, aTypeNameList);
                                iDataDic.Add(ITypeListKey + "Type", aAllTypeList);
                            }
                        }
                        else
                        {
                            iDataDic.Add(ITypeListKey, null);
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
                                var aTypes = iDataDic.GetData<IList<Type>>(ITypeListKey + "Type");
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
                        iDataDic.SetData(ListElementCountKey, aList.Count);
                    }
                }
                GUILayout.EndHorizontal();

                System.Action aDrawElementAct = () =>
                {

                    int aAt = 0;
                    int aDeleteAt = -1;
                    List<object> aResultList = new List<object>();
                    var aListType = aType.GetGenericValueType();
                    string aTypeName = aListType.Name;
                    int aMove = -1;
                    foreach (var aListData in aList)
                    {
                        if (aAt > 0 && aIsMoveElement)
                        {
                            using (new GUILayout.HorizontalScope("box"))
                            {
                                GUILayout.FlexibleSpace();
                                if (GUILayout.Button("¡¶", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                                {
                                    aMove = aAt - 1;
                                }
                                if (GUILayout.Button("¡¿", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
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
                                if (GUILayout.Button(UCL_LocalizeManager.Get("Delete"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                                {
                                    aDeleteAt = aAt;
                                }
                            }
                            string aDisplayName = aListData.UCL_GetShortName(aListData != null ? aListData.GetType().Name : aTypeName);
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
                        iDataDic.Remove("IList", aDeleteAt);
                        iDataDic.SetData(ListElementCountKey, aList.Count);
                    }
                };
                if (iOverrideDrawElement != null)
                {
                    iOverrideDrawElement.Invoke(aDrawElementAct);
                }
                else
                {
                    aDrawElementAct.Invoke();
                }
                
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

    }
}