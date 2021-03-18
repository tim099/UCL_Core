using UnityEngine;

namespace UCL.Core.PA
{
    [UnityEditor.CustomPropertyDrawer(typeof(UCL_ReadOnlyAttribute))]
    public class UCL_ReadOnlyDrawer : UnityEditor.PropertyDrawer
    {
        public override void OnGUI(Rect position, UnityEditor.SerializedProperty property, GUIContent label)
        {
            GUI.enabled = false;
            UnityEditor.EditorGUI.PropertyField(position, property, label, true);
            GUI.enabled = true;
        }
    }
}
