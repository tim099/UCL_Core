using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
namespace UCL.Core.PA
{
    [CustomPropertyDrawer(typeof(UCL_ButtonAttribute))]
    public class UCL_ButtonPropertyDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {

            var aAttr = attribute as UCL_ButtonAttribute;
            EditorGUI.BeginProperty(position, label, property);

            var size = new Vector2(0.2f * position.size.x, position.size.y);
            var size_2 = new Vector2(0.8f * position.size.x, position.size.y);
            Rect but_rect = new Rect(position.position, size);
            Rect text_rect = new Rect(new Vector2(position.position.x + size.x, position.position.y), size_2);
            if (GUI.Button(but_rect, "Invoke"))
            {
                var obj = property.serializedObject.targetObject;

                string aFuncName = System.Text.RegularExpressions.Regex.Replace(property.name, "m_", "");
                var target = property.GetParent();
                var value = property.GetValue();
                UCL.Core.ServiceLib.UCL_UpdateService.AddAction(delegate ()
                {
                    aAttr?.InvokeAct(aFuncName, target, value);
                });
            }
            if (label != null && property != null)
            {
                try
                {
                    EditorGUI.PropertyField(text_rect, property, label, false);
                }
                catch (System.Exception iE)
                {
                    Debug.LogException(iE);
                    //Debug.LogWarning(" UCL_ButtonPropertyDrawer EditorGUI.PropertyField Exception:" + e);
                    return;
                }

            }
            EditorGUI.EndProperty();
            //base.OnGUI(position, property, label);
        }
    }
}