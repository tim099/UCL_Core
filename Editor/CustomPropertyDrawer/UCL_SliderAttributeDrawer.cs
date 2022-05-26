using UnityEngine;
using UnityEditor;

namespace UCL.Core.PA
{
    [UnityEditor.CustomPropertyDrawer(typeof(UCL_IntSliderAttribute))]
    public class IntSliderAttributeDrawer : UnityEditor.PropertyDrawer
    {
        public override void OnGUI(Rect position, UnityEditor.SerializedProperty property, GUIContent label)
        {
            var aAttr = attribute as UCL_IntSliderAttribute;

            EditorGUI.BeginProperty(position, label, property);
            var prev = EditorGUI.indentLevel;
            //EditorGUI.indentLevel = 0;
            const float SilderSize = 0.65f;
            Rect aLabelRect = new Rect(new Vector2(position.position.x, position.position.y),
                new Vector2((1 - SilderSize) * position.size.x, position.size.y));
            EditorGUI.LabelField(aLabelRect, property.displayName);
            Rect aSliderRect = new Rect(new Vector2(position.position.x + (1 - SilderSize) * position.size.x, position.position.y),
                new Vector2(SilderSize * position.size.x, position.size.y));
            property.intValue = EditorGUI.IntSlider(aSliderRect, property.intValue, aAttr.m_LeftValue, aAttr.m_RightValue);
            //EditorGUI.indentLevel = prev;

            EditorGUI.EndProperty();


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