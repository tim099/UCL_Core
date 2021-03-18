using System;
using System.Collections.Generic;
using UnityEngine;
using UCL.Core.ObjectOperatorExtension;
using UnityEditor;

namespace UCL.Core.PA
{
    [UnityEditor.CustomPropertyDrawer(typeof(UCL_EnumMaskAttribute))]
    public class UCL_EnumMaskPropertyDrawer : UnityEditor.PropertyDrawer
    {
        const int m_BaseButtonCount = 2;
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (m_Foldout)
            {
                return EditorGUIUtility.singleLineHeight * (m_ArrLen + m_BaseButtonCount);
            }

            return EditorGUIUtility.singleLineHeight;
        }
        protected int m_ArrLen = 0;
        protected int m_Timer = 0;
        protected Array m_ValuesArr = null;
        protected bool m_Foldout = false;
        public override void OnGUI(Rect position, UnityEditor.SerializedProperty property, GUIContent label)
        {
            var attr = attribute as UCL_EnumMaskAttribute;
            var target_val = fieldInfo.GetValue(property.serializedObject.targetObject);
            var type = target_val.GetType();
            var underlying_type = Enum.GetUnderlyingType(type);
            var val = Convert.ChangeType(target_val, underlying_type);

            EditorGUI.BeginProperty(position, label, property);
            bool prev = m_Foldout;
            m_Foldout = EditorGUI.Foldout(new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight), m_Foldout, label, true);
            try
            {
                if (m_Foldout)
                {
                    var arr = Enum.GetValues(target_val.GetType());
                    var name_arr = Enum.GetNames(fieldInfo.FieldType);
                    List<object> m_list = new List<object>();
                    List<string> m_namelist = new List<string>();
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var v = arr.GetValue(i);
                        if (!Convert.ChangeType(v, underlying_type).IsZero())
                        {
                            m_list.Add(v);
                            m_namelist.Add(name_arr[i]);
                        }
                    }
                    m_ValuesArr = m_list.ToArray();
                    var names = m_namelist.ToArray();
                    m_ArrLen = m_ValuesArr.Length;
                    //GUILayout.BeginHorizontal();
                    if (GUI.Button(new Rect(position.x, position.y + EditorGUIUtility.singleLineHeight, 80, EditorGUIUtility.singleLineHeight), "Select All"))
                    {
                        var vz = Convert.ChangeType(0, underlying_type);
                        var nvz = vz.Not();
                        val = Convert.ChangeType(nvz, underlying_type);
                    }
                    if (GUI.Button(new Rect(position.x + 100, position.y + EditorGUIUtility.singleLineHeight, 40, EditorGUIUtility.singleLineHeight), "Clear"))
                    {
                        val = Convert.ChangeType(0, underlying_type);
                    }
                    //GUILayout.EndHorizontal();
                    for (int i = 0; i < names.Length; i++)
                    {
                        var enum_val = m_ValuesArr.GetValue(i);
                        bool mask_check = target_val.EnumDoMaskCheck(enum_val);
                        if (EditorGUI.ToggleLeft(new Rect(position.x,
                            position.y + EditorGUIUtility.singleLineHeight * (m_BaseButtonCount + i), position.width,
                            EditorGUIUtility.singleLineHeight), names[i], mask_check))
                        {
                            if (!mask_check)
                            {
                                val = Convert.ChangeType(target_val.EnumOR(enum_val), underlying_type);
                                //target = n_val;
                                //Debug.LogWarning("Do OR:" + names[i] + ",target:" + target.ToString());
                            }
                        }
                        else
                        {
                            if (mask_check)
                            {
                                val = Convert.ChangeType(target_val.EnumAND(enum_val.EnumNot()), underlying_type);
                                //target.DoAND(enum_val.Not());
                                //Debug.LogWarning("Do AND:" + names[i] + ",target:" + target.ToString());
                            }

                        }
                    }
                    if (attr.m_DrawProperty)
                    {
                        GUILayout.Box(val.ToString());
                        //EditorGUILayout.PropertyField(property);
                    }

                }
                target_val = val;
                fieldInfo.SetValue(property.serializedObject.targetObject, target_val);
                property.serializedObject.ApplyModifiedProperties();
            }
            catch (Exception e)
            {
                Debug.LogError("UCL_EnumMaskPropertyDrawer Exception:" + e);
            }
            EditorGUI.EndProperty();
        }
    }
}