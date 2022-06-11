using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.PA
{
    [UnityEditor.CustomPropertyDrawer(typeof(UCL_FieldOnGUIAttribute))]
    public class UCL_FieldOnGUIAttributeDrawer : PropertyDrawer
    {
        private bool m_IsDrawed = false;
        //private Rect m_DrawRect = default;
        private UCL_ObjectDictionary m_ObjectDictionary = new UCL_ObjectDictionary();
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return 0;
            //if (m_IsDrawed)
            //{
            //    //Draw(m_DrawRect, property, label, true);
            //    return m_DrawRect.height;
            //}
            //return base.GetPropertyHeight(property, label);
        }

        public override void OnGUI(Rect position, UnityEditor.SerializedProperty property, GUIContent label)
        {
            Draw(position, property, label, false);
        }

        protected void Draw(Rect position, UnityEditor.SerializedProperty property, GUIContent label, bool iIsGetPropertyHeight)
        {
            m_IsDrawed = false;
            var aTargetObject = property.GetValue();
            if (aTargetObject != null)
            {
                UCL.Core.UI.UCLI_FieldOnGUI aOnGUI = aTargetObject as UCL.Core.UI.UCLI_FieldOnGUI;
                if (aOnGUI != null)
                {
                    EditorGUI.BeginProperty(position, GUIContent.none, property);
                    GUILayout.BeginVertical();
                    aOnGUI.OnGUI(label.text, m_ObjectDictionary);
                    GUILayout.EndVertical();
                    //if (iIsGetPropertyHeight) m_DrawRect = GUILayoutUtility.GetLastRect();
                    m_IsDrawed = true;
                    if (!iIsGetPropertyHeight)
                    {
                        property.SetValue(aOnGUI);
                    }
                    EditorGUI.EndProperty();
                }
            }
            if (!m_IsDrawed)
            {
                UnityEditor.EditorGUI.PropertyField(position, property, label, true);
            }
        }
    }
}