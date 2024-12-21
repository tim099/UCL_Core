
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 12/21 2024
using System;
using System.Collections;
using System.Collections.Generic;
using UCL.Core.LocalizeLib;
using UnityEngine;


namespace UCL.Core.UI
{
    static public partial class UCL_GUILayout
    {
        public static void DrawHashSet(object iHashSet, DrawObjectParams iParams)
        {
            var aType = iHashSet.GetType();

            //GUILayout.Label($"DrawHashSet:{aType.GetTypeName()}", UCL_GUIStyle.LabelStyle);
            //HashSet
            IEnumerable aEnumerable = iHashSet as IEnumerable;
            if (aEnumerable == null)
            {
                GUILayout.Label($"DrawHashSet Type:{aType.GetTypeName()}, aEnumerable == null", UCL_GUIStyle.LabelStyle);
                return;
            }
            var aDataDic = iParams.m_DataDic;
            //var iDataDic = iParams.m_DataDic;
            var iIsAlwaysShowDetail = iParams.m_IsAlwaysShowDetail;
            var iDisplayName = iParams.m_DisplayName;

            bool aIsDelete = false;
            bool aIsShowField = false;

            GUILayout.BeginHorizontal();
            if (!iIsAlwaysShowDetail) aIsShowField = Toggle(iParams.m_DataDic, IsShowFieldKey);
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
                        GUILayout.Space(UCL_GUIStyle.GetScaledSize(5));
                        aIsDelete = BoolField(iParams.m_DataDic, IsDeleteElementKey);
                        UCL_GUILayout.LabelAutoSize(UCL_LocalizeManager.Get("Delete"));
                    }
                }
                GUILayout.FlexibleSpace();
            }

            if (aIsShowField)
            {
                void DrawAllElements()
                {
                    using (new GUILayout.HorizontalScope("box"))//Add Data to HashSet
                    {
                        var aKeyType = aType.GetGenericKeyType();
                        const string AddKey = "AddData";
                        if (!aDataDic.ContainsKey(AddKey))
                        {
                            aDataDic.SetData(AddKey, aKeyType.CreateInstance());
                        }
                        bool add = false;
                        if (GUILayout.Button(UCL_LocalizeManager.Get("Add"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(80)))
                        {
                            add = true;
                        }
                        GUILayout.Label(UCL_LocalizeManager.Get("Key"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                        var aKey = aDataDic.GetData(AddKey);
                        string aKeyName = string.Empty;
                        var aKeyShortName = aKey as UCL.Core.UCLI_ShortName;
                        if (aKeyShortName != null) aKeyName = aKeyShortName.GetShortName();
                        if (aKeyName.IsNullOrEmpty()) aKeyName = UCL_LocalizeManager.Get(aKeyType.Name);
                        var aParams = iParams.CreateChild(aDataDic.GetSubDic(iDisplayName + "_AddKey"), aKeyName);

                        aDataDic.SetData(AddKey, DrawObjectData(aKey, aParams));
                        //iDataDic.SetData(AddKey, DrawObjectData(aKey, iDataDic.GetSubDic(iDisplayName + "_AddKey"), aKeyName, iFieldNameFunc: iFieldNameFunc));
                        if (add)
                        {
                            try
                            {
                                var aNewKey = aDataDic.GetData(AddKey);
                                //if (!iDic.Contains(aNewKey))
                                {
                                    aDataDic.Remove(AddKey);
                                    var aGenericType = aType.GetGenericValueType();
                                    //iDic.Add(aNewKey, aGenericType.CreateInstance());
                                    var addMethod = aType.GetMethod("Add");
                                    addMethod.Invoke(aEnumerable, new[] { aNewKey });
                                }
                            }
                            catch (System.Exception iE)
                            {
                                Debug.LogException(iE);
                            }
                        }
                    }

                    const int MaxItemsPerPage = 10;
                    int itemCount = aEnumerable.GetCount();
                    var result = DrawSelectPage(aDataDic.GetSubDic(nameof(DrawSelectPage)), itemCount, MaxItemsPerPage);
                    int startIndex = result.startIndex;
                    int lastIndex = Mathf.Min(itemCount, startIndex + MaxItemsPerPage);
                    //GUILayout.Label($"startIndex:{startIndex},lastIndex:{lastIndex}", UCL_GUIStyle.LabelStyle);
                    int aAt = 0;
                    const string SetDataKey = "HashSet";
                    int aDeleteAt = -1;
                    object aDeleteTarget = null;
                    List<object> aResultList = new List<object>();
                    var aListType = aType.GetGenericValueType();
                    string aTypeName = aListType.Name;
                    foreach (var aListData in aEnumerable)
                    {
                        if (aAt >= lastIndex)
                        {
                            break;
                        }
                        if (aAt < startIndex)
                        {
                            ++aAt;
                            continue;
                        }

                        using (new GUILayout.HorizontalScope("box"))
                        {
                            //GUILayout.Label($"At:{aAt}", UCL_GUIStyle.LabelStyle);
                            if (aIsDelete)
                            {
                                if (GUILayout.Button(UCL_LocalizeManager.Get("Delete"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                                {
                                    aDeleteAt = aAt;
                                    aDeleteTarget = aListData;
                                }
                            }
                            var aParams = iParams.CreateChild(iParams.m_DataDic.GetSubDic(SetDataKey, aAt),
                                $"({aAt}) {aListData.UCL_GetShortName(aListData != null ? aListData.GetType().Name : aTypeName)}");
                            aParams.m_FieldType = aListType;


                            GUILayout.Label($"{aListData}", UCL_GUIStyle.LabelStyle);

                            ++aAt;
                        }
                    }
                    if (aDeleteTarget != null)
                    {
                        var method = aType.GetMethod("Remove");
                        method.Invoke(aEnumerable, new[] { aDeleteTarget });

                        iParams.m_DataDic.Remove(SetDataKey, aDeleteAt);
                    }
                };

                var iOverrideDrawElement = iParams.m_DrawObjectConfigs.m_OverrideDrawElement;
                if (iOverrideDrawElement != null)
                {
                    iOverrideDrawElement.Invoke(DrawAllElements);
                }
                else
                {
                    DrawAllElements();
                }


            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }
    }
}