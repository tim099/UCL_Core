using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace UCL.Core {
#if UNITY_EDITOR
    static  public class UCL_ComponentEditor{
        static SerializedObject m_Src;
        [MenuItem("CONTEXT/Component/UCL_Copy value")]
        public static void CopySerialized(MenuCommand cmd) { m_Src = new SerializedObject(cmd.context); }

        [MenuItem("CONTEXT/Component/UCL_Paste value")]
        public static void PasteSerialized(MenuCommand cmd) {
            if(m_Src.targetObject.GetType() == cmd.context.GetType()) {
                EditorUtility.CopySerialized(m_Src.targetObject, cmd.context);
                return;
            }
            SerializedObject dst = new SerializedObject(cmd.context);
            SerializedProperty itr = m_Src.GetIterator();
            while(itr.NextVisible(true)) {
                SerializedProperty prop_element = dst.FindProperty(itr.name);
                if(prop_element != null && prop_element.propertyType == itr.propertyType) {
                    dst.CopyFromSerializedProperty(itr);
                }
            }
            dst.ApplyModifiedProperties();
        }
    }
#endif
}