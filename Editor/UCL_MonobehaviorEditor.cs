using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.Linq;

namespace UCL.Core.EditorLib {
    [CustomEditor(typeof(MonoBehaviour),true)]
    public class UCL_MonobehaviorEditor : Editor {
        public override void OnInspectorGUI() {
            DrawDefaultInspector();
            Type type = target.GetType();
            if(type.GetCustomAttributes(typeof(ATTR.EnableUCLEditor), true).Length == 0) return;

            var methods = type.GetMethods().Where(m => m.GetCustomAttributes(typeof(ATTR.UCL_FunctionButtonAttribute), false).Length > 0).ToArray();
            //Debug.LogWarning("type:" + type.Name + ",methods:" + methods.Length);
            if(methods.Length > 0) {
                GUILayout.BeginVertical();
                //EditorGUILayout.BeginVertical();
                for(int i = 0; i < methods.Length; i++) {
                    var method = methods[i];
                    if(GUILayout.Button(method.Name)) {
                        method.Invoke(target, null);
                    }
                }
                GUILayout.EndVertical();
                //EditorGUILayout.EndVertical();
            }

        }
    }
}

