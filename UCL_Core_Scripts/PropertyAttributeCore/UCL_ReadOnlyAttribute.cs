using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.PA {
    public class UCL_ReadOnlyAttribute : PropertyAttribute {

    }
#if UNITY_EDITOR
    [UnityEditor.CustomPropertyDrawer(typeof(UCL_ReadOnlyAttribute))]
    public class ReadOnlyDrawer : UnityEditor.PropertyDrawer {
        public override void OnGUI(Rect position, UnityEditor.SerializedProperty property, GUIContent label) {
            GUI.enabled = false;
            UnityEditor.EditorGUI.PropertyField(position, property, label, true);
            GUI.enabled = true;
        }
    }
#endif
}