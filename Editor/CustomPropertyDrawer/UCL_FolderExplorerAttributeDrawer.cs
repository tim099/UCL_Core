using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
namespace UCL.Core.PA
{
    [UnityEditor.CustomPropertyDrawer(typeof(UCL_FolderExplorerAttribute))]
    public class UCL_FolderExplorerAttributeDrawer : PropertyDrawer
    {
        UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return 0;
        }
        public override void OnGUI(Rect position, UnityEditor.SerializedProperty property, GUIContent label)
        {
            Draw(position, property, label, false);
        }
        protected void Draw(Rect position, UnityEditor.SerializedProperty property, GUIContent label, bool iIsGetPropertyHeight)
        {
            var aTargetObject = property.GetValue();

            GUILayout.BeginHorizontal();
            
            
            if (aTargetObject != null && aTargetObject is string)
            {
                var aAttr = attribute as UCL_FolderExplorerAttribute;
                string aPath = aTargetObject as string;
                string aNewPath = aAttr.OnGUI(m_DataDic, aPath, property.displayName);
                //string aNewPath = UCL.Core.UI.UCL_GUILayout.FolderExplorer(m_DataDic, aPath, aAttr.m_FolderRoot, property.displayName);
                if (aNewPath != aPath)
                {
                    property.stringValue = aNewPath;
                }
            }
            else
            {
                UnityEditor.EditorGUI.PropertyField(position, property, label, true);
            }
            
            GUILayout.EndHorizontal();
        }
    }
}