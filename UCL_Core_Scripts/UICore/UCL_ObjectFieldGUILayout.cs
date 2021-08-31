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
        protected Dictionary<string, object> m_DataDic = new Dictionary<string, object>();
        protected bool ContainsKey(string iKey)
        {
            return m_DataDic.ContainsKey(iKey);
        }
        protected void SetData(string iKey, object iObj)
        {
            m_DataDic[iKey] = iObj;
        }
        protected T GetData<T>(string iKey, T iDefaultValue)
        {
            if (!m_DataDic.ContainsKey(iKey))
            {
                return iDefaultValue;
            }
            return (T)m_DataDic[iKey];
        }
        virtual public void DrawFieldData(object iObj, int iID)
        {
            using (var aScope = new GUILayout.VerticalScope("box"))
            {
                Type aType = iObj.GetType();
                var aFields = aType.GetAllFieldsUnityVer(typeof(object));
                foreach (var aField in aFields)
                {
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

                    if (aData == null)
                    {

                    }
                    else if (aData.IsNumber())
                    {
                        string aKey = iID.ToString() + "_" + aDisplayName;
                        if (!ContainsKey(aKey))
                        {
                            SetData(aKey, aData.ToString());
                        }
                        string aNum = GetData(aKey, string.Empty);
                        var aResult = UCL.Core.UI.UCL_GUILayout.TextField(aDisplayName, (string)aNum);
                        SetData(aKey, aResult);
                        object aResVal;
                        if (UCL.Core.MathLib.Num.TryParse(aResult, aData.GetType(), out aResVal))
                        {
                            aField.SetValue(iObj, aResVal);
                        }
                    }
                    else if (aData is bool)
                    {
                        aField.SetValue(iObj, UCL.Core.UI.UCL_GUILayout.BoolField(aDisplayName, (bool)aData));
                    }
                    else if (aField.FieldType == typeof(string))
                    {
                        var result = UCL.Core.UI.UCL_GUILayout.TextField(aDisplayName, (string)aData);
                        aField.SetValue(iObj, result);
                    }
                    else if (aField.FieldType.IsEnum)
                    {
                        string aTypeName = aField.FieldType.Name;

                        GUILayout.BeginHorizontal();
                        UCL.Core.UI.UCL_GUILayout.LabelAutoSize(aDisplayName);
                        string aKey = iID.ToString() + aDisplayName;
                        bool flag = GetData(aKey, false);
                        aField.SetValue(iObj, UCL.Core.UI.UCL_GUILayout.Popup((System.Enum)aData, ref flag, (iEnum) => {
                            string aLocalizeKey = aTypeName + "_" + iEnum;
                            if (UCL_LocalizeManager.ContainsKey(aLocalizeKey))
                            {
                                iEnum = UCL_LocalizeManager.Get(aLocalizeKey);
                            }
                            return iEnum;
                        }));
                        SetData(aKey, flag);
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