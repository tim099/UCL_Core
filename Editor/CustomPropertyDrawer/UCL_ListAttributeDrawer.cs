using UnityEngine;
using System;
using UnityEditor;
using System.Linq;
namespace UCL.Core.PA
{
    [CustomPropertyDrawer(typeof(UCL_StrListAttribute))]
    public class UCL_StrListPropertyDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var list_pro = attribute as UCL_StrListAttribute;
            var aStrArr = list_pro.m_StrList;
            if (aStrArr == null) return;

            if (property.propertyType == SerializedPropertyType.String)
            {
                int index = Mathf.Max(0, aStrArr.IndexOf(property.stringValue));
                index = EditorGUI.Popup(position, property.displayName, index, aStrArr.ToArray());
                if (aStrArr.Count > index)
                {
                    property.stringValue = aStrArr[index];
                }
            }
            else if (property.propertyType == SerializedPropertyType.Integer)
            {
                property.intValue = EditorGUI.Popup(position, property.displayName, property.intValue, aStrArr.ToArray());
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
            var aObj = property.GetParent();
            var aListAttr = attribute as UCL_ListAttribute;
            var aList = aListAttr.GetStrList(aObj);
            if (aList == null) return;

            if (property.propertyType == SerializedPropertyType.String)
            {
                int aIndex = Mathf.Max(0, aList.IndexOf(property.stringValue));
                var aDisplayList = aListAttr.GetDisplayList(aObj);
                aIndex = EditorGUI.Popup(position, property.displayName, aIndex, aDisplayList);
                if (aList.Count > aIndex)
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