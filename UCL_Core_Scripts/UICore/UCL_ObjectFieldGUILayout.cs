using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UCL.Core.LocalizeLib;
using UnityEngine;
namespace UCL.Core.UI
{
    /// <summary>
    /// Render object field using GUILayout
    /// </summary>
    public class UCL_ObjectFieldGUILayout
    {
        protected UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();
        Vector2 m_ScrollPos = Vector2.zero;
        public void ClearData()
        {
            m_DataDic.Clear();
        }
        virtual public void DrawObject(object iObj, string iDisplayName = "")
        {
            m_ScrollPos = GUILayout.BeginScrollView(m_ScrollPos);
            DrawFieldData(iObj, string.Empty, iDisplayName);
            GUILayout.EndScrollView();
        }
        virtual protected object DrawFieldData(object iObj, string iID = "", string iDisplayName = "")
        {

            //GUILayout.BeginHorizontal();
            bool aIsShowField = true;
            bool aIsDefaultType = true;
            object aResultObj = iObj;
            if (iObj != null)
            {
                if (string.IsNullOrEmpty(iDisplayName))
                {
                    iDisplayName = iObj.GetType().Name;
                }
                if (iObj is string)
                {
                    aIsShowField = false;
                    aResultObj = GUILayout.TextField((string)iObj);
                }
                else if (iObj.IsNumber())
                {
                    aIsShowField = false;
                    aResultObj = UCL_GUILayout.NumField(string.Empty, iObj);
                }
                else if (iObj is IList)
                {
                    GUILayout.BeginVertical();
                    aIsShowField = false;
                    GUILayout.BeginHorizontal();
                    string aShowKey = iID.ToString() + "_" + iID + "_Show";
                    aIsShowField = m_DataDic.GetData(aShowKey, true);
                    m_DataDic.SetData(aShowKey, UCL_GUILayout.Toggle(aIsShowField));
                    if (!string.IsNullOrEmpty(iDisplayName)) UCL_GUILayout.LabelAutoSize(iDisplayName);
                    GUILayout.EndHorizontal();
                    if (aIsShowField)
                    {
                        IList aList = iObj as IList;
                        string aCountKey = iID.ToString() + "_" + iID + "_Count";
                        int aCount = m_DataDic.GetData(aCountKey, aList.Count);
                        GUILayout.BeginHorizontal();
                        int aNewCount = UCL_GUILayout.IntField("Count", aCount);
                        m_DataDic.SetData(aCountKey, aNewCount);
                        if (aNewCount != aList.Count)
                        {
                            if (GUILayout.Button("Set Count"))
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
                                m_DataDic.SetData(aCountKey, aList.Count);
                            }
                        }
                        GUILayout.EndHorizontal();
                        int aAt = 0;
                        int aDeleteAt = -1;
                        List<object> aResultList = new List<object>();
                        foreach (var aListData in aList)
                        {
                            using (var aScope2 = new GUILayout.VerticalScope("box"))
                            {
                                int aDrawAt = aAt;
                                GUILayout.BeginHorizontal();
                                if (UCL_GUILayout.ButtonAutoSize(UCL.Core.LocalizeLib.UCL_LocalizeManager.Get("Delete")))
                                {
                                    aDeleteAt = aAt;
                                }

                                aResultList.Add(DrawFieldData(aListData, iID + "_" + (aAt++).ToString()));
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
                            m_DataDic.SetData(aCountKey, aList.Count);
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
                GUILayout.BeginVertical();
                {
                    GUILayout.BeginHorizontal();
                    string aShowKey = iID.ToString() + "_" + iID + "_Show";
                    aIsShowField = m_DataDic.GetData(aShowKey, false);
                    m_DataDic.SetData(aShowKey, UCL_GUILayout.Toggle(aIsShowField));
                    if (!string.IsNullOrEmpty(iDisplayName)) UCL_GUILayout.LabelAutoSize(iDisplayName);
                    GUILayout.EndHorizontal();
                }

                if (aIsShowField) using (var aScope = new GUILayout.VerticalScope("box"))
                    {
                        Type aType = iObj.GetType();
                        var aFields = aType.GetAllFieldsUnityVer(typeof(object));
                        foreach (var aField in aFields)
                        {
                            //GUILayout.Label(aField.FieldType.Name);
                            var aData = aField.GetValue(iObj);

                            if (aField.GetCustomAttribute<HideInInspector>() != null)
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
                            }

                            string aDisplayName = aField.Name;

                            if (aDisplayName[0] == 'm' && aDisplayName[1] == '_')
                            {
                                aDisplayName = aDisplayName.Substring(2, aDisplayName.Length - 2);
                            }
                            {
                                string aKey = "DrawField_" + aDisplayName;
                                if (UCL_LocalizeManager.ContainsKey(aKey))
                                {
                                    aDisplayName = UCL_LocalizeManager.Get(aKey);
                                }
                            }
                            var aDropDownAttr = aField.GetCustomAttribute<ATTR.UCL_DropDownAttribute>();
                            if (aDropDownAttr != null)
                            {
                                //int aIndex = Mathf.Max(0, Array.IndexOf(aList, property.stringValue));
                                //aIndex = EditorGUI.Popup(position, property.displayName, aIndex, aList);
                                //if (aList.Length > aIndex)
                                //{
                                //    property.stringValue = aList[aIndex];
                                //}
                                var aList = aDropDownAttr.GetList(iObj);
                                int aIndex = Mathf.Max(0, Array.IndexOf(aList, aData));
                                string aShowKey = iID.ToString() + "_" + aDisplayName + "_Show";
                                bool aIsShow = m_DataDic.GetData(aShowKey, false);

                                aIndex = UCL_GUILayout.Popup(aIndex, aList, ref aIsShow);
                                m_DataDic.SetData(aShowKey, aIsShow);
                                aField.SetValue(iObj, aList[aIndex]);
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
                            else if (aData.IsNumber())
                            {
                                string aKey = iID.ToString() + "_" + aDisplayName;
                                if (!m_DataDic.ContainsKey(aKey))
                                {
                                    m_DataDic.SetData(aKey, aData.ToString());
                                }
                                string aNum = m_DataDic.GetData(aKey, string.Empty);
                                var aResult = UCL.Core.UI.UCL_GUILayout.TextField(aDisplayName, (string)aNum);
                                m_DataDic.SetData(aKey, aResult);
                                object aResVal;
                                if (UCL.Core.MathLib.Num.TryParse(aResult, aData.GetType(), out aResVal))
                                {
                                    aField.SetValue(iObj, aResVal);
                                }
                            }
                            else if (aData is IList)
                            {
                                GUILayout.BeginHorizontal();
                                string aShowKey = iID.ToString() + "_" + aDisplayName + "_Show";
                                bool aIsShow = m_DataDic.GetData(aShowKey, false);
                                m_DataDic.SetData(aShowKey, UCL_GUILayout.Toggle(aIsShow));
                                UCL_GUILayout.LabelAutoSize(aDisplayName);
                                GUILayout.EndHorizontal();
                                if (aIsShow)
                                {
                                    IList aList = aData as IList;
                                    string aCountKey = iID.ToString() + "_" + aDisplayName + "_Count";
                                    int aCount = m_DataDic.GetData(aCountKey, aList.Count);
                                    GUILayout.BeginHorizontal();
                                    int aNewCount = UCL_GUILayout.IntField("Count", aCount);
                                    m_DataDic.SetData(aCountKey, aNewCount);
                                    if (aNewCount != aList.Count)
                                    {
                                        if (GUILayout.Button("Set Count"))
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
                                        if (GUILayout.Button(LocalizeLib.UCL_LocalizeManager.Get("Add")))
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
                                            m_DataDic.SetData(aCountKey, aList.Count);
                                        }
                                    }
                                    GUILayout.EndHorizontal();
                                    int aAt = 0;
                                    int aDeleteAt = -1;
                                    List<object> aResultList = new List<object>();
                                    foreach (var aListData in aList)
                                    {
                                        using (var aScope2 = new GUILayout.VerticalScope("box"))
                                        {
                                            int aDrawAt = aAt;
                                            GUILayout.BeginHorizontal();
                                            if (UCL_GUILayout.ButtonAutoSize(UCL.Core.LocalizeLib.UCL_LocalizeManager.Get("Delete")))
                                            {
                                                aDeleteAt = aAt;
                                            }

                                            aResultList.Add(DrawFieldData(aListData, iID + "_" + (aAt++).ToString()));

                                            //DrawFieldData(aListData, iID + "_" + (aAt++).ToString(),(iValue) => {
                                            //    System.Action aSetAct = null;
                                            //    aSetAct = () =>
                                            //    {
                                            //        aList[aDrawAt] = iValue;
                                            //        UCL.Core.ServiceLib.UCL_UpdateService.RemoveUpdateActionStaticVer(aSetAct);
                                            //    };
                                            //    UCL.Core.ServiceLib.UCL_UpdateService.AddUpdateActionStaticVer(aSetAct);

                                            //});
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
                                        m_DataDic.SetData(aCountKey, aList.Count);
                                    }
                                }
                            }
                            else if (aField.FieldType.IsEnum)
                            {
                                string aTypeName = aField.FieldType.Name;

                                GUILayout.BeginHorizontal();
                                UCL.Core.UI.UCL_GUILayout.LabelAutoSize(aDisplayName);
                                string aKey = iID.ToString() + aDisplayName;
                                bool flag = m_DataDic.GetData(aKey, false);
                                aField.SetValue(iObj, UCL.Core.UI.UCL_GUILayout.Popup((System.Enum)aData, ref flag, (iEnum) => {
                                    string aLocalizeKey = aTypeName + "_" + iEnum;
                                    if (UCL_LocalizeManager.ContainsKey(aLocalizeKey))
                                    {
                                        iEnum = UCL_LocalizeManager.Get(aLocalizeKey);
                                    }
                                    return iEnum;
                                }));
                                m_DataDic.SetData(aKey, flag);
                                GUILayout.EndHorizontal();
                            }
                            else if (aField.FieldType.IsStructOrClass())
                            {
                                UCL.Core.UI.UCL_GUILayout.LabelAutoSize(aDisplayName);
                                GUILayout.BeginHorizontal();
                                GUILayout.Space(10);
                                DrawFieldData(aData, iID);
                                aField.SetValue(iObj, aData);
                                GUILayout.EndHorizontal();
                            }
                        }

                    }
                GUILayout.EndVertical();
            }

            //GUILayout.EndHorizontal();
            return aResultObj;
        }
    }
}