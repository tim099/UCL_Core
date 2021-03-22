using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
namespace UCL.Core.PA
{
    [UnityEditor.CustomPropertyDrawer(typeof(UCL_EditorOnlyAttribute))]
    public class UCL_EditorOnlyDrawer : UnityEditor.PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return 0f;
        }
        public override void OnGUI(Rect iPosition, UnityEditor.SerializedProperty iProperty, GUIContent iLabel)
        {
            string aAddr = iProperty.stringValue;
            UnityEngine.Object aObj = null;
            if (!string.IsNullOrEmpty(aAddr))
            {
                aObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(aAddr);
            }
            UnityEditor.EditorGUILayout.BeginHorizontal();
            UnityEditor.EditorGUILayout.LabelField(iLabel);//, GUILayout.Width(position.width * 0.4f)
            aObj = UnityEditor.EditorGUILayout.ObjectField(aObj, typeof(UnityEngine.Object), false);
            UnityEditor.EditorGUILayout.EndHorizontal();
            if (aObj != null)
            {
                iProperty.stringValue = AssetDatabase.GetAssetPath(aObj);
            }
            else
            {
                iProperty.stringValue = string.Empty;
            }
            
            //SerializedObject aSerializedObject = new SerializedObject(aObj);
            //var aProperty = aSerializedObject.GetIterator();
            //if (aObj != null)
            //{
            //    UnityEditor.EditorGUI.PropertyField(position, aProperty, label, true);
            //}
            //if(aProperty.propertyType == SerializedPropertyType.ObjectReference)
            //{
            //    property.stringValue = AssetDatabase.GetAssetPath(aProperty.objectReferenceValue);
            //}
        }
    }
}