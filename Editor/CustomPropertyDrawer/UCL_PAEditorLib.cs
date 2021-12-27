using System.Collections;
using UnityEngine;
using System.Reflection;
using System.Linq;
using System;
using System.Collections.Generic;
using UnityEditor;

namespace UCL.Core.PA
{
    public static class EditorLib
    {
        static public object GetParent(this UnityEditor.SerializedProperty prop)
        {
            var path = prop.propertyPath.Replace(".Array.data[", "[");
            object obj = prop.serializedObject.targetObject;
            //Debug.LogWarning("GetRoot path:" + path);
            var elements = path.Split('.');
            foreach (var element in elements.Take(elements.Length - 1))
            {
                //Debug.LogWarning("GetRoot element:" + element);
                if (element.Contains("["))
                {
                    var elementName = element.Substring(0, element.IndexOf("["));
                    var index = Convert.ToInt32(element.Substring(element.IndexOf("[")).Replace("[", "").Replace("]", ""));
                    obj = UCL.Core.PA.Lib.GetValue(obj, elementName, index);
                }
                else
                {
                    obj = UCL.Core.PA.Lib.GetValue(obj, element);
                }
            }
            return obj;
        }
        static public string GetFieldName(this UnityEditor.SerializedProperty prop)
        {
            var path = prop.propertyPath;//.Replace(".Array.data[", "[");

            var elements = path.Split('.');
            var f_name = elements[elements.Length - 1];
            //Debug.LogWarning("GetFieldName path:" + path+",f_name:"+f_name);
            return f_name;
        }
        /*
        static public object GetValue(this UnityEditor.SerializedProperty property) {
            System.Type parent_type = property.serializedObject.targetObject.GetType();
            System.Reflection.FieldInfo field = parent_type.GetField(property.propertyPath);
            if(field == null) {
                Debug.LogWarning("SerializedProperty.GetValue Fail!! field == null property.propertyPath:" + property.propertyPath);
                return null;
            }
            return field.GetValue(property.serializedObject.targetObject);
        }
        */
        static public object GetValue(this UnityEditor.SerializedProperty iProperty)
        {
            var aParent = iProperty.GetParent();
            var aFieldName = iProperty.GetFieldName();

            System.Type aParentType = aParent.GetType();
            System.Reflection.FieldInfo aField = aParentType.GetField(aFieldName);
            if (aField == null)
            {
                Debug.LogWarning("SerializedProperty.GetValue Fail!! field == null property.propertyPath:" + iProperty.propertyPath);
                return null;
            }
            return aField.GetValue(aParent);

        }
        static public void SetValue(this UnityEditor.SerializedProperty iProperty, object iValue)
        {
            var aParent = iProperty.GetParent();
            bool aIsModified = false;
            List<KeyValuePair<FieldInfo, object>> aList = new List<KeyValuePair<FieldInfo, object>>();
            var aObj = aParent;//aField.GetValue(aParent);
            var aFieldNames = iProperty.propertyPath.Split('.');
            for (int i = 1; i < aFieldNames.Length; i++)
            {
                var aFieldName = aFieldNames[i];
                var aType = aObj.GetType();
                var aField = aType.GetField(aFieldName);
                if (aField == null)
                {
                    //Debug.LogError("SetPropertyValue aField == null,aFieldName:" + aFieldName + ",aType:" + aType.Name+ ",i:"+ i);
                    break;
                }
                else
                {
                    aList.Add(new KeyValuePair<FieldInfo, object>(aField, aObj));
                    aObj = aField.GetValue(aObj);
                }
            }
            for (int i = aList.Count - 1; i >= 0; --i)
            {
                
                var aFieldInfo = aList[i].Key;
                if (!aFieldInfo.GetValue(aList[i].Value).Equals(iValue))
                {
                    aIsModified = true;
                    aFieldInfo.SetValue(aList[i].Value, iValue);
                }

                //Debug.LogError("aList[i].Value:" + aList[i].Value.GetType().Name + ",iValue:" + iValue.GetType().Name);
                iValue = aList[i].Value;
            }
            if(iProperty.serializedObject.targetObject != null)
            {

                var aFieldName = aFieldNames[0];
                FieldInfo aField = iProperty.serializedObject.targetObject.GetType().GetField(aFieldName);
                //Debug.LogError("aFieldName:" + aFieldName+ ",aParent.GetType():"+ iProperty.serializedObject.targetObject.GetType().Name);
                if (aField != null && !aField.GetValue(iProperty.serializedObject.targetObject).Equals(aParent))
                {
                    aIsModified = true;
                    aField.SetValue(iProperty.serializedObject.targetObject, aParent);
                }
                if(aIsModified) EditorUtility.SetDirty(iProperty.serializedObject.targetObject);
            }
        }
    }
}