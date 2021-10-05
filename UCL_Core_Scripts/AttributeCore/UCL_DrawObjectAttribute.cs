using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UCL.Core.ATTR {
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class UCL_DrawObjectAttribute : UCL_Attribute {
#if UNITY_EDITOR
        public override void DrawAttribute(UnityEngine.Object iTarget, MethodInfo iMethodInfo)
        {
            System.Func<System.Type, UnityEngine.Object, UnityEngine.Object> aFunc =
                 delegate (System.Type iType, UnityEngine.Object iObj) {
                     GUILayout.Box(iMethodInfo.Name);
                     var aObj = UCL.Core.EditorLib.EditorGUILayoutMapper.ObjectField(iObj, iType, true);
                     return aObj;
                 };
            iMethodInfo.Invoke(iTarget, new object[1] { aFunc });
        }
#endif
    }
}