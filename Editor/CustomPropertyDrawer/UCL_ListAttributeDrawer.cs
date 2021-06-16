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
            var aStrArr = list_pro.m_StrArr;
            if (aStrArr == null) return;

            if (property.propertyType == SerializedPropertyType.String)
            {
                int index = Mathf.Max(0, Array.IndexOf(aStrArr, property.stringValue));
                index = EditorGUI.Popup(position, property.displayName, index, aStrArr);
                if (aStrArr.Length > index)
                {
                    property.stringValue = aStrArr[index];
                }
            }
            else if (property.propertyType == SerializedPropertyType.Integer)
            {
                property.intValue = EditorGUI.Popup(position, property.displayName, property.intValue, aStrArr);
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

            var aListAttr = attribute as UCL_ListAttribute;
            var aList = aListAttr.GetList(property.GetParent());
            if (aList == null) return;

            if (property.propertyType == SerializedPropertyType.String)
            {
                int aIndex = Mathf.Max(0, Array.IndexOf(aList, property.stringValue));
                aIndex = EditorGUI.Popup(position, property.displayName, aIndex, aList);
                if (aList.Length > aIndex)
                {
                    property.stringValue = aList[aIndex];
                }
            }
            else
            {
                base.OnGUI(position, property, label);
            }
        }
    }
}