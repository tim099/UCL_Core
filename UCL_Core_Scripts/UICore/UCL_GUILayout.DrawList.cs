using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UCL.Core.LocalizeLib;
using UnityEngine;


namespace UCL.Core.UI
{
    static public partial class UCL_GUILayout
    {
        public enum ListOperation
        {
            None,
            Add,
            SetCount,
        }
        public const string IsMoveElementKey = "MoveElement";
        public const string IsDeleteElementKey = "Delete";

        private const string ListElementCountKey = "ListElementCount";
        private const string ITypeListKey = "ITypeList";
        public static void DrawList(IList iList, UCL_ObjectDictionary iDataDic, string iDisplayName = "",
            bool iIsAlwaysShowDetail = false)
        {
            DrawList(iList, iDataDic, iDisplayName, iIsAlwaysShowDetail, null);
        }
        //, DrawObjectParams iParams
        public static void DrawList(IList iList, DrawObjectParams iParams)
        {
            //GUILayout.Label($"DrawList:{iList.GetType().FullName}", UCL_GUIStyle.LabelStyle);

            //var iDataDic = iParams.m_DataDic;
            var iIsAlwaysShowDetail = iParams.m_IsAlwaysShowDetail;
            var iDisplayName = iParams.m_DisplayName;

            bool aIsMoveElement = false;
            bool aIsDelete = false;
            bool aIsShowField = false;
            var aType = iList.GetType();
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
                    if (!string.IsNullOrEmpty(iDisplayName)) GUILayout.Label(iDisplayName, UCL_GUIStyle.LabelStyle);
                }

                if (aIsShowField)
                {
                    {
                        GUILayout.Space(UCL_GUIStyle.GetScaledSize(5));
                        aIsMoveElement = BoolField(iParams.m_DataDic, IsMoveElementKey);
                        GUILayout.Label(UCL_LocalizeManager.Get("MoveElement"), UCL_GUIStyle.LabelStyle);
                    }
                    {
                        GUILayout.Space(UCL_GUIStyle.GetScaledSize(5));
                        aIsDelete = BoolField(iParams.m_DataDic, IsDeleteElementKey);
                        GUILayout.Label(UCL_LocalizeManager.Get("Delete"), UCL_GUIStyle.LabelStyle);
                    }
                }
                GUILayout.FlexibleSpace();

                if (GUILayout.Button(UCL_LocalizeManager.Get("Copy"), UCL_GUIStyle.ButtonStyle))
                {
                    UCL.Core.CopyPaste.SetCopyData(iList);
                }
                if (GUILayout.Button(UCL_LocalizeManager.Get("Paste"), UCL_GUIStyle.ButtonStyle))
                {
                    UCL.Core.CopyPaste.LoadCopyData(iList);
                }
            }

            if (aIsShowField)
            {
                void DrawAllElements()
                {

                    const int MaxItemsPerPage = 10;
                    int itemCount = iList.Count;
                    var result = DrawSelectPage(iParams.m_DataDic.GetSubDic(nameof(DrawSelectPage)), itemCount, MaxItemsPerPage);
                    int startIndex = result.startIndex;
                    int lastIndex = Mathf.Min(itemCount, startIndex + MaxItemsPerPage);
                    int index = 0;

                    int aDeleteAt = -1;
                    Dictionary<int, object> aResultList = new();
                    var aListType = aType.GetGenericValueType();
                    string aTypeName = aListType.Name;
                    int aMove = -1;
                    void DrawMove(int index)
                    {
                        using (new GUILayout.HorizontalScope("box"))
                        {
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button(" ▲ ▼ ", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            {
                                aMove = index;
                            }
                            GUILayout.FlexibleSpace();
                        }
                    }
                    foreach (var aListData in iList)
                    {
                        if (index >= lastIndex)
                        {
                            if (index < itemCount && aIsMoveElement)
                            {
                                DrawMove(index - 1);
                            }
                            break;
                        }
                        if (index++ < startIndex)
                        {
                            continue;
                        }
                        int aAt = index - 1;

                        if (aAt > 0 && aIsMoveElement)
                        {
                            DrawMove(aAt - 1);
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
                            //string aDisplayName = aListData.UCL_GetShortName(aListData != null ? aListData.GetType().Name : aTypeName);
                            var aParams = iParams.CreateChild(iParams.m_DataDic.GetSubDic(nameof(iList), aAt),
                                $"({aAt}) {aListData.UCL_GetShortName(aListData != null ? aListData.GetType().Name : aTypeName)}");
                            aParams.m_FieldType = aListType;

                            var aResult = DrawObjectData(aListData, aParams);
                            aResultList[aAt] = aResult;
                        }
                    }
                    //if (aMove >= 0) Debug.LogError($"aMove:{aMove}, itemCount:{itemCount}");
                    if (aMove >= 0 && aMove < itemCount - 1)
                    {
                        try
                        {
                            if (!aResultList.ContainsKey(aMove))
                            {
                                aResultList[aMove] = iList[aMove];
                            }
                            if (!aResultList.ContainsKey(aMove + 1))
                            {
                                aResultList[aMove + 1] = iList[aMove + 1];
                            }

                            object val = aResultList[aMove];
                            aResultList[aMove] = aResultList[aMove + 1];
                            aResultList[aMove + 1] = val;

                            //aResultList.SwapElement(aMove, aMove + 1);
                            iParams.m_DataDic.Swap(nameof(iList), aMove, aMove + 1);
                        }
                        catch(System.Exception ex)
                        {
                            Debug.LogError($"aMove:{aMove}, itemCount:{itemCount}, Exception:{ex}");
                            Debug.LogException(ex);
                        }

                    }
                    foreach(var key in aResultList.Keys)
                    {
                        iList[key] = aResultList[key];
                    }
                    //for (int i = 0; i < aResultList.Count; i++)
                    //{
                    //    iList[i] = aResultList[i];
                    //}
                    if (aDeleteAt >= 0)
                    {
                        iList.RemoveAt(aDeleteAt);
                        iParams.m_DataDic.Remove(nameof(iList), aDeleteAt);
                        iParams.m_DataDic.SetData(ListElementCountKey, iList.Count);
                    }
                };
                
                void OnDrawAllElements()
                {
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
                if (iList is Array aArray)
                {
                    //GUILayout.Label($"aArray.Rank:{aArray.Rank}", UCL_GUIStyle.LabelStyle);
                    if (aArray.Rank == 1)
                    {
                        OnDrawAllElements();
                    }
                    else if (aArray.Rank == 2)
                    {
                        int aWidth = aArray.GetLength(0);
                        int aHeight = aArray.GetLength(1);
                        var aListType = aType.GetGenericValueType();
                        string aTypeName = aListType.Name;
                        for (int y = 0; y < aHeight; y++)
                        {
                            for (int x = 0; x < aWidth; x++)
                            {
                                var aListData = aArray.GetValue(x, y);
                                //string aDisplayName = aListData.UCL_GetShortName(aListData != null ? aListData.GetType().Name : aTypeName);
                                var aParams = iParams.CreateChild(iParams.m_DataDic.GetSubDic("IList", x + y * aWidth),
                                    aListData.UCL_GetShortName(aListData != null ? aListData.GetType().Name : aTypeName));
                                aParams.m_FieldType = aListType;
                                var aResult = DrawObjectData(aListData, aParams);
                                //var aResult = DrawObjectData(aListData, iDataDic.GetSubDic("IList", x + y * aWidth), aDisplayName, iFieldNameFunc: iFieldNameFunc, iFieldType: aListType);
                                aArray.SetValue(aResult, x, y);
                            }
                        }
                    }
                    else
                    {
                        GUILayout.Label($"Array.Rank:{aArray.Rank}, not support yet!!", UCL_GUIStyle.LabelStyle);
                    }
                }
                else
                {
                    GUILayout.BeginHorizontal();
                    float width = UCL_GUIStyle.GetScaledSize(80);
                    var aGenericType = aType.GetGenericValueType();
                    bool isTypeList = typeof(UCLI_TypeList).IsAssignableFrom(aGenericType);
                    bool isTypeListable = typeof(UCLI_TypeListable).IsAssignableFrom(aGenericType);
                    bool hasTypeList = iParams.m_DataDic.ContainsKey(ITypeListKey);
                    int aCount = iParams.m_DataDic.GetData(ListElementCountKey, iList.Count);
                    ListOperation listOperation = ListOperation.None;
                    if (aCount != iList.Count)
                    {
                        if (GUILayout.Button(UCL_LocalizeManager.Get("SetCount"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(width)))
                        {
                            listOperation = ListOperation.SetCount;
                        }
                    }
                    else
                    {
                        if (GUILayout.Button(UCL_LocalizeManager.Get("Add"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(width)))
                        {
                            listOperation = ListOperation.Add;
                        }
                    } 
                    
                    int aNewCount = aCount;
                    if (isTypeList || isTypeListable)
                    {
                        //GUILayout.Label($"({aCount})", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    }
                    else
                    {
                        aNewCount = UCL_GUILayout.IntField(UCL_LocalizeManager.Get("Count"), aCount, GUILayout.MinWidth(width));
                        iParams.m_DataDic.SetData(ListElementCountKey, aNewCount);
                    }


                    int aSelectedType = -1;
                    {

                        List<string> aTypeNameList = null;
                        if (hasTypeList)
                        {
                            aTypeNameList = iParams.m_DataDic.GetData<List<string>>(ITypeListKey);
                        }
                        else
                        {
                            

                            if (isTypeList)
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
                                    iParams.m_DataDic.Add(ITypeListKey, aTypeNameList);
                                    iParams.m_DataDic.Add(ITypeListKey + "Type", aAllTypeList);
                                }
                            }
                            else if (isTypeListable)
                            {
                                var aAllTypeList = UCLI_TypeListable.GetAllITypes(aGenericType);

                                if (aAllTypeList != null)
                                {
                                    aTypeNameList = new List<string>();
                                    for (int i = 0; i < aAllTypeList.Count; i++)
                                    {
                                        aTypeNameList.Add(UCL_LocalizeLib.GetLocalize(aAllTypeList[i].Name));
                                    }
                                    iParams.m_DataDic.Add(ITypeListKey, aTypeNameList);
                                    iParams.m_DataDic.Add(ITypeListKey + "Type", aAllTypeList);
                                }
                            }
                            else
                            {
                                iParams.m_DataDic.Add(ITypeListKey, null);
                            }
                        }
                        
                        if (aTypeNameList != null)
                        {
                            aSelectedType = PopupAuto(aTypeNameList, iParams.m_DataDic, "SelectType", 10);//, GUILayout.Width(UCL_GUIStyle.GetScaledSize(240))
                        }
                    }

                    switch (listOperation)
                    {
                        case ListOperation.Add:
                            {
                                try
                                {
                                    if (aSelectedType >= 0)
                                    {
                                        var aTypes = iParams.m_DataDic.GetData<IList<Type>>(ITypeListKey + "Type");
                                        iList.Add(aTypes[aSelectedType].CreateInstance());
                                    }
                                    else
                                    {
                                        iList.Add(aGenericType.CreateInstance());
                                    }
                                }
                                catch (System.Exception iE)
                                {
                                    Debug.LogException(iE);
                                }
                                iParams.m_DataDic.SetData(ListElementCountKey, iList.Count);
                                break;
                            }
                        case ListOperation.SetCount:
                            {
                                if (aNewCount < 0) aNewCount = 0;
                                while (aNewCount < iList.Count)
                                {
                                    iList.RemoveAt(iList.Count - 1);
                                }
                                while (aNewCount > iList.Count)
                                {
                                    try
                                    {
                                        iList.Add(aGenericType.CreateInstance());
                                    }
                                    catch (System.Exception iE)
                                    {
                                        Debug.LogException(iE);
                                    }
                                }
                                break;
                            }
                    }

                    GUILayout.EndHorizontal();


                    OnDrawAllElements();
                }



            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }
        public static void DrawList(IList iList, UCL_ObjectDictionary iDataDic, string iDisplayName = "",
            bool iIsAlwaysShowDetail = false, Func<string, string> iFieldNameFunc = null,
            System.Action<System.Action> iOverrideDrawElement = null)
        {
            var aConfig = new DrawObjectConfigs(iFieldNameFunc);
            aConfig.m_OverrideDrawElement = iOverrideDrawElement;
            var aParams = new DrawObjectParams(iDataDic, iDisplayName, iIsAlwaysShowDetail, null, null, aConfig);
            DrawList(iList, aParams);
        }
        public static void DrawList(IList aList, UCL_ObjectDictionary iDataDic, string iDisplayName = "",
            bool iIsAlwaysShowDetail = false, System.Func<int, UCL_ObjectDictionary, object, object> iDrawElementAct = null)
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
                        aSelectedType = PopupAuto(aTypeNameList, iDataDic, "SelectType", 10, GUILayout.Width(UCL_GUIStyle.GetScaledSize(240)));
                    }
                    if (GUILayout.Button(UCL_LocalizeManager.Get("Add"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(80))))
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

                int aAt = 0;
                int aDeleteAt = -1;
                List<object> aResultList = new List<object>();
                var aListType = aType.GetGenericValueType();
                string aTypeName = aListType.Name;
                int aMove = -1;
                if(iDrawElementAct == null)
                {
                    iDrawElementAct = (iAt, iDataDic, aListData) =>
                    {
                        string aDisplayName = aListData.UCL_GetShortName(aListData != null ? aListData.GetType().Name : aTypeName);
                        var aResult = DrawObjectData(aListData, iDataDic,
                            aDisplayName, iFieldType: aListType);
                        return aResult;
                    };
                }
                foreach (var aListData in aList)
                {
                    if (aAt > 0 && aIsMoveElement)
                    {
                        using (new GUILayout.HorizontalScope("box"))
                        {
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button("▲", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            {
                                aMove = aAt - 1;
                            }
                            if (GUILayout.Button("▼", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
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

                        var aResult = iDrawElementAct.Invoke(aAt, iDataDic.GetSubDic("IList", aAt++), aListData);

                        //string aDisplayName = aListData.UCL_GetShortName(aListData != null ? aListData.GetType().Name : aTypeName);
                        //var aResult = DrawObjectData(aListData, iDataDic.GetSubDic("IList", aAt++),
                        //    aDisplayName, iFieldType: aListType);
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

            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }
    }
}