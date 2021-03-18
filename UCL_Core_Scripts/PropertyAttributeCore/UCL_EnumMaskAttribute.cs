using UnityEngine;

//https://docs.unity3d.com/ScriptReference/EditorGUILayout.PropertyField.html

namespace UCL.Core.PA {
    [System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false)]
    public class UCL_EnumMaskAttribute : PropertyAttribute {
        public bool m_DrawProperty = false;
        public UCL_EnumMaskAttribute() {

        }
        public UCL_EnumMaskAttribute(bool draw_property) {
            m_DrawProperty = draw_property;
        }
    }
}