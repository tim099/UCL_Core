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
        virtual public void DrawFieldData(object iObj, string iID, System.Action<object> iSetValueAct = null)
        {
            {
                if(iObj is string)
                {
                    //UCL_DebugInspector
                    //for (int i = 0; i < list.Count; i++)
                    //{
                    //    int at = i;
                    //    if (DrawElement("Element " + at.ToString() + " : ", list[at], delegate (object val) {
                    //        list[at] = val;
                    //    }))
                    //    {
                    //        aDeleteAt = at;
                    //    }
                    //}
                    var aValue = GUILayout.TextField((string)iObj);
                    if (iSetValueAct != null)
                    {
                        iSetValueAct.Invoke(aValue);
                    }
                     
                    return;
                }
            }
            using (var aScope = new GUILayout.VerticalScope("box"))
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

                    if (aField.FieldType == typeof(bool))
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
                        UCL_GUILayout.LabelAutoSize(aDisplayName);
                        string aShowKey = iID.ToString() + "_" + aDisplayName + "_Show";
                        bool aIsShow = m_DataDic.GetData(aShowKey, false);
                        m_DataDic.SetData(aShowKey, UCL_GUILayout.Toggle(aIsShow));
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
                                if(GUILayout.Button("Set Count"))
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
                                            if(aGenericType == typeof(string))
                                            {
                                                aList.Add(string.Empty);
                                            }
                                            else
                                            {
                                                aList.Add(Activator.CreateInstance(aGenericType));
                                            }
                                            
                                        }
                                        catch(System.Exception iE)
                                        {
                                            Debug.LogException(iE);
                                        }
                                        
                                    }
                                }
                            }
                            GUILayout.EndHorizontal();
                            int aAt = 0;
                            int aDeleteAt = -1;
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
                                    DrawFieldData(aListData, iID + "_" + (aAt++).ToString(),(iValue) => {
                                        System.Action aSetAct = null;
                                        aSetAct = () =>
                                        {
                                            aList[aDrawAt] = iValue;
                                            UCL.Core.ServiceLib.UCL_UpdateService.RemoveUpdateActionStaticVer(aSetAct);
                                        };
                                        UCL.Core.ServiceLib.UCL_UpdateService.AddUpdateActionStaticVer(aSetAct);
                                        
                                    });
                                    GUILayout.EndHorizontal();
                                }

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
        }
    }
}