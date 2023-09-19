using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core {
#if UNITY_EDITOR
    static public class UCL_ComponentEditor
    {
        static UnityEditor.SerializedObject s_Src;
        [UnityEditor.MenuItem("CONTEXT/Component/UCL/Copy value")]
        public static void CopySerialized(UnityEditor.MenuCommand iCmd) { s_Src = new UnityEditor.SerializedObject(iCmd.context); }

        [UnityEditor.MenuItem("CONTEXT/Component/UCL/Paste value")]
        public static void PasteSerialized(UnityEditor.MenuCommand iCmd)
        {
            if (s_Src == null) return;
            var aSrcType = s_Src.targetObject.GetType();
            var aDstType = iCmd.context.GetType();
            if (aSrcType == aDstType)
            {
                //Debug.LogWarning("PasteSerialized Same Type:" + aSrcType.ToString());
                UnityEditor.EditorUtility.CopySerialized(s_Src.targetObject, iCmd.context);
                return;
            }
            //Debug.LogWarning("PasteSerialized Type Src:" + aSrcType.ToString() + ",Dst:" + aDstType);
            UnityEditor.SerializedObject aDst = new UnityEditor.SerializedObject(iCmd.context);
            UnityEditor.SerializedProperty aItr = s_Src.GetIterator();
            if (aItr.NextVisible(true))
            {
                while (aItr.NextVisible(true))
                {
                    UnityEditor.SerializedProperty aSerializedProperty = aDst.FindProperty(aItr.name);
                    if (aSerializedProperty != null && aSerializedProperty.propertyType == aItr.propertyType)
                    {
                        aDst.CopyFromSerializedProperty(aItr);
                    }
                }
            }
            aDst.ApplyModifiedProperties();
        }
    }
#endif
}