using UnityEngine;
using UnityEditor;

namespace UCL.Core.PA
{
    [UnityEditor.CustomPropertyDrawer(typeof(UCL_IntSliderAttribute))]
    public class IntSliderAttributeDrawer : UnityEditor.PropertyDrawer
    {
        public override void OnGUI(Rect position, UnityEditor.SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var attr = attribute as UCL_IntSliderAttribute;
            var prev = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            property.intValue = EditorGUI.IntSlider(position, property.intValue, attr.m_LeftValue, attr.m_RightValue);
            EditorGUI.indentLevel = prev;
            EditorGUI.EndProperty();
        }
    }
    public class UCL_SliderAttribute : PropertyAttribute
    {
        public readonly float m_RightValue, m_LeftValue;
        public UCL_SliderAttribute(float left_value, float right_value)
        {
            m_RightValue = right_value;
            m_LeftValue = left_value;
        }
    }
    [UnityEditor.CustomPropertyDrawer(typeof(UCL_SliderAttribute))]
    public class SliderAttributeDrawer : UnityEditor.PropertyDrawer
    {
        public override void OnGUI(Rect position, UnityEditor.SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var attr = attribute as UCL_SliderAttribute;
            var prev = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            const float silder_size = 0.65f;
            Rect sliderRect = new Rect(new Vector2(position.position.x + (1 - silder_size) * position.size.x, position.position.y),
                new Vector2(silder_size * position.size.x, position.size.y));
            property.floatValue = EditorGUI.Slider(sliderRect, property.floatValue, attr.m_LeftValue, attr.m_RightValue);
            EditorGUI.indentLevel = prev;
            EditorGUI.EndProperty();
        }
    }
}