using System;
using System.Collections;
using System.Collections.Generic;
using UCL.Core.LocalizeLib;
using UnityEngine;


namespace UCL.Core.UI
{
    static public partial class UCL_GUILayout
    {
        public static void DrawDictionary(IDictionary iDic, UCL_ObjectDictionary iDataDic, string iDisplayName = "",
            bool iIsAlwaysShowDetail = false, Func<string, string> iFieldNameFunc = null)
        {
            bool aIsDelete = false;
            bool aIsShowField = false;
            var aType = iDic.GetType();
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
                        aIsDelete = BoolField(iDataDic, "Delete");
                        UCL_GUILayout.LabelAutoSize(UCL_LocalizeManager.Get("Delete"));
                    }
                }
                GUILayout.FlexibleSpace();
            }
            if (aIsShowField)
            {
                using (new GUILayout.HorizontalScope("box"))
                {
                    var aKeyType = aType.GetGenericKeyType();
                    string aAddKey = "AddData";
                    if (!iDataDic.ContainsKey(aAddKey))
                    {
                        iDataDic.SetData(aAddKey, aKeyType.CreateInstance());
                    }
                    var aKey = iDataDic.GetData(aAddKey);
                    string aKeyName = string.Empty;
                    var aKeyShortName = aKey as UCL.Core.UCLI_ShortName;
                    if (aKeyShortName != null) aKeyName = aKeyShortName.GetShortName();
                    if (aKeyName.IsNullOrEmpty()) aKeyName = UCL_LocalizeManager.Get(aKeyType.Name);
                    iDataDic.SetData(aAddKey, DrawObjectData(aKey,
                        iDataDic.GetSubDic(iDisplayName + "_AddKey"), aKeyName, iFieldNameFunc: iFieldNameFunc));
                    using (new GUILayout.HorizontalScope("box"))
                    {
                        if (GUILayout.Button(UCL_LocalizeManager.Get("Add"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(80)))
                        {
                            try
                            {
                                var aNewKey = iDataDic.GetData(aAddKey);
                                if (!iDic.Contains(aNewKey))
                                {
                                    iDataDic.Remove(aAddKey);
                                    var aGenericType = aType.GetGenericValueType();
                                    iDic.Add(aNewKey, aGenericType.CreateInstance());
                                }
                            }
                            catch (System.Exception iE)
                            {
                                Debug.LogException(iE);
                            }
                        }
                    }

                }

                var aValueType = aType.GetGenericValueType();
                object aDeleteAt = null;
                string aDeleteKeyName = string.Empty;
                List<Tuple<object, object>> aResultList = new List<Tuple<object, object>>();
                foreach (var aKey in iDic.Keys)
                {
                    using (new GUILayout.HorizontalScope("box"))
                    {
                        string aKeyName = aKey.UCL_GetShortName(aKey.UCL_ToString());
                        if (aIsDelete)
                        {
                            if (GUILayout.Button(UCL_LocalizeManager.Get("Delete"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            {
                                aDeleteAt = aKey;
                                aDeleteKeyName = "Dic_" + aKeyName;
                            }
                        }
                        using (new GUILayout.VerticalScope())
                        {
                            var aSubDic = iDataDic.GetSubDic("Dic_" + aKeyName);
                            var aDicData = iDic[aKey];
                            string aDefaultName = aValueType.Name;
                            if(aDicData != null)
                            {
                                var aDicDataType = aDicData.GetType();
                                aDefaultName = aDicDataType.GetTypeName();
                                if (aDicData is IList aDicDataList)
                                {
                                    aDefaultName = $"{aDefaultName}({aDicDataList.Count})";
                                }
                            }
                            string aDisplayName = aDicData.UCL_GetShortName(aDefaultName);
                            GUILayout.Label(aKeyName, UCL_GUIStyle.LabelStyle);
                            aResultList.Add(new Tuple<object, object>(aKey, DrawObjectData(aDicData, aSubDic, aDisplayName, iFieldNameFunc: iFieldNameFunc)));
                        }
                    }
                }
                for (int i = 0; i < aResultList.Count; i++)
                {
                    iDic[aResultList[i].Item1] = aResultList[i].Item2;
                }
                if (aDeleteAt != null)
                {
                    iDic.Remove(aDeleteAt);
                    iDataDic.Remove(aDeleteKeyName);
                }
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="iDic"></param>
        /// <param name="iDataDic"></param>
        /// <param name="iDisplayName"></param>
        /// <param name="iIsAlwaysShowDetail"></param>
        /// <param name="iDrawElementAct">object iKey,string iDefaultName,string iKeyName, UCL_ObjectDictionary iDataDic,object aData</param>
        public static void DrawDictionary(IDictionary iDic, UCL_ObjectDictionary iDataDic, string iDisplayName = "",
            bool iIsAlwaysShowDetail = false, System.Func<object, string, string, UCL_ObjectDictionary, object, object> iDrawElementAct = null)
        {
            bool aIsDelete = false;
            bool aIsShowField = false;
            var aType = iDic.GetType();
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
                        aIsDelete = BoolField(iDataDic, "Delete");
                        UCL_GUILayout.LabelAutoSize(UCL_LocalizeManager.Get("Delete"));
                    }
                }
                GUILayout.FlexibleSpace();
            }
            if (aIsShowField)
            {
                using (new GUILayout.HorizontalScope("box"))
                {
                    var aKeyType = aType.GetGenericKeyType();
                    string aAddKey = "AddData";
                    if (!iDataDic.ContainsKey(aAddKey))
                    {
                        iDataDic.SetData(aAddKey, aKeyType.CreateInstance());
                    }
                    var aKey = iDataDic.GetData(aAddKey);
                    string aKeyName = string.Empty;
                    var aKeyShortName = aKey as UCL.Core.UCLI_ShortName;
                    if (aKeyShortName != null) aKeyName = aKeyShortName.GetShortName();
                    if (aKeyName.IsNullOrEmpty()) aKeyName = UCL_LocalizeManager.Get(aKeyType.Name);
                    iDataDic.SetData(aAddKey, DrawObjectData(aKey,
                        iDataDic.GetSubDic(iDisplayName + "_AddKey"), aKeyName));
                    using (new GUILayout.HorizontalScope("box"))
                    {
                        if (GUILayout.Button(UCL_LocalizeManager.Get("Add"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(80)))
                        {
                            try
                            {
                                var aNewKey = iDataDic.GetData(aAddKey);
                                if (!iDic.Contains(aNewKey))
                                {
                                    iDataDic.Remove(aAddKey);
                                    var aGenericType = aType.GetGenericValueType();
                                    iDic.Add(aNewKey, aGenericType.CreateInstance());
                                }
                            }
                            catch (System.Exception iE)
                            {
                                Debug.LogException(iE);
                            }
                        }
                    }

                }

                var aValueType = aType.GetGenericValueType();
                object aDeleteAt = null;
                string aDeleteKeyName = string.Empty;
                List<Tuple<object, object>> aResultList = new List<Tuple<object, object>>();
                if(iDrawElementAct == null)
                {
                    iDrawElementAct = (iKey, iDefaultName, iKeyName, iDataDic, aData) =>
                    {
                        string aDisplayName = aData.UCL_GetShortName(iDefaultName);
                        GUILayout.Label(iKeyName, UCL_GUIStyle.LabelStyle);
                        var aResult = DrawObjectData(aData, iDataDic, iDefaultName);
                        return aData;
                    };
                }
                foreach (var aKey in iDic.Keys)
                {
                    using (new GUILayout.HorizontalScope("box"))
                    {
                        string aKeyName = aKey.UCL_GetShortName(aKey.UCL_ToString());
                        if (aIsDelete)
                        {
                            if (GUILayout.Button(UCL_LocalizeManager.Get("Delete"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                            {
                                aDeleteAt = aKey;
                                aDeleteKeyName = "Dic_" + aKeyName;
                            }
                        }
                        using (new GUILayout.VerticalScope())
                        {
                            var aSubDic = iDataDic.GetSubDic("Dic_" + aKeyName);
                            var aDicData = iDic[aKey];
                            string aDefaultName = aValueType.Name;
                            if (aDicData != null)
                            {
                                var aDicDataType = aDicData.GetType();
                                aDefaultName = aDicDataType.GetTypeName();
                                if (aDicData is IList aDicDataList)
                                {
                                    aDefaultName = $"{aDefaultName}({aDicDataList.Count})";
                                }
                            }
                            var aResult = iDrawElementAct.Invoke(aKey, aDefaultName, aKeyName, aSubDic, aDicData);

                            aResultList.Add(new Tuple<object, object>(aKey, aResult));
                        }
                    }
                }
                for (int i = 0; i < aResultList.Count; i++)
                {
                    iDic[aResultList[i].Item1] = aResultList[i].Item2;
                }
                if (aDeleteAt != null)
                {
                    iDic.Remove(aDeleteAt);
                    iDataDic.Remove(aDeleteKeyName);
                }
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }
    }
}