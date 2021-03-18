using System.Collections;
using UnityEngine;
using System.Reflection;
using System.Linq;
using System;

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
        static public object GetValue(this UnityEditor.SerializedProperty property)
        {
            var parent = property.GetParent();
            var f_name = property.GetFieldName();

            System.Type parent_type = parent.GetType();
            System.Reflection.FieldInfo field = parent_type.GetField(f_name);
            if (field == null)
            {
                Debug.LogWarning("SerializedProperty.GetValue Fail!! field == null property.propertyPath:" + property.propertyPath);
                return null;
            }
            return field.GetValue(parent);

        }
        static public void SetValue(this UnityEditor.SerializedProperty property, object value)
        {
            System.Type parentType = property.serializedObject.targetObject.GetType();
            System.Reflection.FieldInfo field = parentType.GetField(property.propertyPath);
            if (field == null)
            {
                Debug.LogWarning("SerializedProperty.SetValue Fail!! field == null property.propertyPath:" + property.propertyPath);
                return;
            }
            field.SetValue(property.serializedObject.targetObject, value);
        }
    }
}