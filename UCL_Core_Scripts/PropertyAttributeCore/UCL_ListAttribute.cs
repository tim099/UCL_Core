using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.Reflection;
using UCL.Core.ObjectReflectionExtension;
namespace UCL.Core.PA {
    public class UCL_StrListAttribute : PropertyAttribute {
        public string[] m_List;
        public UCL_StrListAttribute(params string[] list) {
            m_List = list;
        }
        public UCL_StrListAttribute(Type type, string func_name) {
            var method = type.GetMethod(func_name);
            if(method != null) {
                try {
                    m_List = method.Invoke(null, null) as string[];
                } catch(Exception e) {
                    Debug.LogError("UCL_ListProperty method.Invoke Exception:" + e.ToString());
                }

            } else { //might be accessor
                PropertyInfo propInfo = type.GetProperty(func_name);
                if(propInfo == null) { // not accessor!!
                    Debug.LogError("UCL_ListProperty:" + type.Name + ",func_name == null :" + func_name);
                    return;
                }
                MethodInfo[] methInfos = propInfo.GetAccessors();
                for(int i = 0; i < methInfos.Length; i++) {
                    MethodInfo m = methInfos[i];
                    //Console.WriteLine("Accessor #{0}:", ctr + 1);
                    //Console.WriteLine("   Name: {0}", m.Name);
                    //Console.WriteLine("   Visibility: {0}", GetVisibility(m));
                    //Console.Write("   Property Type: ");
                    // Determine if this is the property getter or setter.
                    if(m.ReturnType == typeof(void)) {//setter
                        //Console.WriteLine("Setter");
                        //Console.WriteLine("   Setting the property value.");
                        //  Set the value of the property.
                        //m.Invoke(test, new object[] { "The Modified Caption" });
                    } else {//getter
                        //Console.WriteLine("Getter");
                        // Get the value of the property.
                        //Console.WriteLine("   Property Value: {0}", m.Invoke(test, new object[] { }));
                        m_List = m.Invoke(null , new object[] { }) as string[];
                        if(m_List != null) break;
                    }
                }
            }
        }
    }
    public class UCL_ListAttribute : PropertyAttribute {
        string m_MethodName = null;
        object[] m_Params = null;
        public UCL_ListAttribute(string _MethodName, params object[] para) {
            m_MethodName = _MethodName;
            m_Params = para;
        }
        public string[] GetList(object target) {
            return (string[]) target.Invoke(m_MethodName, m_Params);
        }
        //public UCL_ListAttribute (System.Action<List<string>> get)
    }
#if UNITY_EDITOR
    [CustomPropertyDrawer(typeof(UCL_StrListAttribute))]
    public class UCL_StrListPropertyDrawer : PropertyDrawer {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) {
            var list_pro = attribute as UCL_StrListAttribute;
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

    [CustomPropertyDrawer(typeof(UCL_ListAttribute))]
    public class UCL_ListAttributeDrawer : PropertyDrawer {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label) {

            var list_pro = attribute as UCL_ListAttribute;
            var list = list_pro.GetList(property.GetParent());
            if(list == null) return;

            if(property.propertyType == SerializedPropertyType.String) {
                int index = Mathf.Max(0, Array.IndexOf(list, property.stringValue));
                index = EditorGUI.Popup(position, property.displayName, index, list);
                if(list.Length > index) {
                    property.stringValue = list[index];
                }
            } else {
                base.OnGUI(position, property, label);
            }
        }
    }
#endif

}