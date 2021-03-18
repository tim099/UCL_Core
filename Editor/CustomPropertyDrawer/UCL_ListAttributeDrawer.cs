using UnityEngine;
using System;
using UnityEditor;
namespace UCL.Core.PA
{
    [CustomPropertyDrawer(typeof(UCL_StrListAttribute))]
    public class UCL_StrListPropertyDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var list_pro = attribute as UCL_StrListAttribute;
            var list = list_pro.m_List;
            if (list == null) return;

            if (property.propertyType == SerializedPropertyType.String)
            {
                int index = Mathf.Max(0, Array.IndexOf(list, property.stringValue));
                index = EditorGUI.Popup(position, property.displayName, index, list);
                if (list.Length > index)
                {
                    property.stringValue = list[index];
                }
            }
            else if (property.propertyType == SerializedPropertyType.Integer)
            {
                property.intValue = EditorGUI.Popup(position, property.displayName, property.intValue, list);
            }
            else
            {
                base.OnGUI(position, property, label);
            }
        }
    }

    [CustomPropertyDrawer(typeof(UCL_ListAttribute))]
    public class UCL_ListAttributeDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {

            var list_pro = attribute as UCL_ListAttribute;
            var list = list_pro.GetList(property.GetParent());
            if (list == null) return;

            if (property.propertyType == SerializedPropertyType.String)
            {
                int index = Mathf.Max(0, Array.IndexOf(list, property.stringValue));
                index = EditorGUI.Popup(position, property.displayName, index, list);
                if (list.Length > index)
                {
                    property.stringValue = list[index];
                }
            }
            else
            {
                base.OnGUI(position, property, label);
            }
        }
    }
}