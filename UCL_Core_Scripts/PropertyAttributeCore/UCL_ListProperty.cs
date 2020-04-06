using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;

namespace UCL.Core {
    public class UCL_StrListProperty : PropertyAttribute {
        public string[] m_List;
        public UCL_StrListProperty(params string[] list) {
            m_List = list;
        }
        public UCL_StrListProperty(Type type, string func_name) {
            var method = type.GetMethod(func_name);
            if(method != null) {
                try {
                    m_List = method.Invoke(null, null) as string[];
                } catch(Exception e) {
                    Debug.LogError("UCL_ListProperty method.Invoke Exception:" + e.ToString());
                }

            } else {
                Debug.LogError("UCL_ListProperty:"+ type.Name+ ",func_name == null :" + func_name);
            }
        }
    }

#if UNITY_EDITOR
    [CustomPropertyDrawer(typeof(UCL_StrListProperty))]
    public class UCL_StrListPropertyDrawer : PropertyDrawer {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) {
            var list_pro = attribute as UCL_StrListProperty;
            var list = list_pro.m_List;
            if(list == null) return;

            if(property.propertyType == SerializedPropertyType.String) {
                int index = Mathf.Max(0, Array.IndexOf(list, property.stringValue));
                index = EditorGUI.Popup(position, property.displayName, index, list);
                if(list.Length > index) {
                    property.stringValue = list[index];
                }
            } else if(property.propertyType == SerializedPropertyType.Integer) {
                property.intValue = EditorGUI.Popup(position, property.displayName, property.intValue, list);
            } else {
                base.OnGUI(position, property, label);
            }
        }
    }
#endif

}