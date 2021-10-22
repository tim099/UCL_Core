using UnityEngine;
using System;
using UnityEditor;
namespace UCL.Core.PA
{
    [CustomPropertyDrawer(typeof(UCL_PA_DrawTexture2DAttribute))]
    public class UCL_PA_DrawTexture2DAttributeDrawer : PropertyDrawer
    {
        Texture2D m_Texture2D = null;
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (m_Texture2D != null)
            {
                return ((float)m_Texture2D.height/m_Texture2D.width)* EditorGUIUtility.singleLineHeight * 6 + EditorGUIUtility.singleLineHeight;
            }
            return EditorGUIUtility.singleLineHeight;
        }
        public override void OnGUI(Rect iPosition, SerializedProperty iProperty, GUIContent iLabel)
        {
            EditorGUI.PropertyField(new Rect(iPosition.x, iPosition.y, iPosition.width, EditorGUIUtility.singleLineHeight), iProperty, iLabel);

            var aAttr = attribute as UCL_PA_DrawTexture2DAttribute;
            m_Texture2D = aAttr.GetTexture(iProperty.GetParent(), iProperty.objectReferenceValue);
            if(m_Texture2D != null)
            {
                GUI.Box(new Rect(iPosition.x, iPosition.y + EditorGUIUtility.singleLineHeight, iPosition.width, iPosition.height- EditorGUIUtility.singleLineHeight), m_Texture2D);
            }
        }
    }
}