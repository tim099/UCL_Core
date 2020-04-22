using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UCL.Core.PA {
    [System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false)]
    public class UCL_EnumMaskProperty : PropertyAttribute {

    }
#if UNITY_EDITOR
    [UnityEditor.CustomPropertyDrawer(typeof(UCL_EnumMaskProperty))]
    public class UCL_EnumMaskPropertyDrawer : UnityEditor.PropertyDrawer {
        const int m_BaseButtonCount = 2;
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) {
            if(m_Foldout && m_ValuesArr != null) {
                return EditorGUIUtility.singleLineHeight * (m_ValuesArr.Length + m_BaseButtonCount);
            }

            return EditorGUIUtility.singleLineHeight;
        }
        protected Array m_ValuesArr = null;
        protected bool m_Foldout = false;
        public override void OnGUI(Rect position, UnityEditor.SerializedProperty property, GUIContent label) {
            var target = fieldInfo.GetValue(property.serializedObject.targetObject);
            m_ValuesArr = Enum.GetValues(target.GetType());
            var type = target.GetType();
            var underlying_type = Enum.GetUnderlyingType(type);

            var val = Convert.ChangeType(target, underlying_type);

            EditorGUI.BeginProperty(position, label, property);
            
            m_Foldout = EditorGUI.Foldout(new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight), m_Foldout, label);

            if(m_Foldout) {
                var names = Enum.GetNames(fieldInfo.FieldType);
                //GUILayout.BeginHorizontal();
                if(GUI.Button(new Rect(position.x, position.y + EditorGUIUtility.singleLineHeight, 80, EditorGUIUtility.singleLineHeight), "Select All")) {
                    target = Convert.ChangeType(0, underlying_type);
                }
                if(GUI.Button(new Rect(position.x + 100, position.y + EditorGUIUtility.singleLineHeight, 40, EditorGUIUtility.singleLineHeight), "Clear")) {
                    target = Convert.ChangeType(0, underlying_type);
                }
                //GUILayout.EndHorizontal();
                for(int i = 0; i < names.Length; i++) {//.Length
                    var enum_val = m_ValuesArr.GetValue(i);
                    bool mask_check = target.DoMaskCheck(enum_val);
                    if(EditorGUI.ToggleLeft(new Rect(position.x, 
                        position.y + EditorGUIUtility.singleLineHeight * (m_BaseButtonCount + i), position.width, 
                        EditorGUIUtility.singleLineHeight), names[i], mask_check)) {
                        
                        
                        if(!mask_check) {
                            val = Convert.ChangeType(target.OR(enum_val), underlying_type);
                            //target = n_val;
                            Debug.LogWarning("Do OR:" + names[i] + ",target:" + target.ToString());
                        }
                    } else {
                        if(mask_check) {
                            val = Convert.ChangeType(target.AND(enum_val.Not()), underlying_type);
                            //target.DoAND(enum_val.Not());
                            Debug.LogWarning("Do AND:" + names[i] + ",target:" + target.ToString());
                        }

                    }
                }
            }
            target = val;
            EditorGUI.EndProperty();


            fieldInfo.SetValue(property.serializedObject.targetObject, target);
            property.serializedObject.ApplyModifiedProperties();
        }
    }
#endif
}