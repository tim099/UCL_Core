using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System.Linq;
using System;
using System.Reflection;

namespace UCL.Core {
    public class UCL_ButtonProperty : PropertyAttribute {
        Type m_Type;
        public UCL_ButtonProperty() {

        }
        public UCL_ButtonProperty(Type _Type) {
            m_Type = _Type;
        }
        public void InvokeAct(string func_name,object obj) {
            //Debug.LogWarning("func_name:" + func_name + ",obj:" + obj.GetType().Name);
            if(m_Type == null) {
                m_Type = obj.GetType();
            }
            var method = m_Type.GetMethod(func_name);
            if(method != null) {
                try {
                    method?.Invoke(obj, null);
                } catch(Exception e) {
                    Debug.LogError("UCL_ButtonProperty: " + m_Type.Name + "_" + func_name + ".Invoke Exception:" + e.ToString());
                }
            } else {
                Debug.LogError(m_Type.Name+"_func_name:" + func_name+" Not Exist!!");
            }
        }
    }
#if UNITY_EDITOR
    [CustomPropertyDrawer(typeof(UCL_ButtonProperty))]
    public class UCL_ButtonPropertyDrawer : PropertyDrawer {
        public object GetParent(SerializedProperty prop) {
            var path = prop.propertyPath.Replace(".Array.data[", "[");
            object obj = prop.serializedObject.targetObject;
            var elements = path.Split('.');
            foreach(var element in elements.Take(elements.Length - 1)) {
                if(element.Contains("[")) {
                    var elementName = element.Substring(0, element.IndexOf("["));
                    var index = Convert.ToInt32(element.Substring(element.IndexOf("[")).Replace("[", "").Replace("]", ""));
                    obj = GetValue(obj, elementName, index);
                } else {
                    obj = GetValue(obj, element);
                }
            }
            return obj;
        }
        public object GetValue(object source, string name) {
            if(source == null)
                return null;
            var type = source.GetType();
            var f = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if(f == null) {
                var p = type.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if(p == null)
                    return null;
                return p.GetValue(source, null);
            }
            return f.GetValue(source);
        }

        public object GetValue(object source, string name, int index) {
            var enumerable = GetValue(source, name) as IEnumerable;
            var enm = enumerable.GetEnumerator();
            while(index-- >= 0)
                enm.MoveNext();
            return enm.Current;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) {

            var pro = attribute as UCL_ButtonProperty;
            EditorGUI.BeginProperty(position, label, property);
            if(GUI.Button(position, property.displayName)) {
                //Debug.LogWarning("Test!!");
                if(property.propertyType == SerializedPropertyType.String) {
                    pro?.InvokeAct(property.stringValue, GetParent(property));
                } else {
                    pro?.InvokeAct(property.displayName, GetParent(property));
                }
                
            }
            EditorGUI.EndProperty();
        }
    }
#endif
}