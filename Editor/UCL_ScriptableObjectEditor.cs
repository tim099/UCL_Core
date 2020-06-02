using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.Linq;

namespace UCL.Core.EditorLib {
    [CustomEditor(typeof(ScriptableObject), true)]
    public class UCL_ScriptableObjectEditor : Editor {
        public override void OnInspectorGUI() {
            DrawDefaultInspector();
            Type type = target.GetType();
            if(type.GetCustomAttributes(typeof(ATTR.EnableUCLEditor), true).Length == 0) return;

            var methods = type.GetMethods();
            if(methods.Length > 0) {
                GUILayout.BeginVertical();
                for(int i = 0; i < methods.Length; i++) {
                    var method = methods[i];
                    var attr = method.GetCustomAttributes(typeof(ATTR.UCL_FunctionButtonAttribute), false);
                    if(attr.Length > 0) {
                        for(int j = 0; j < attr.Length; j++) {
                            var ba = (ATTR.UCL_FunctionButtonAttribute)attr[j];
                            string but_name = ba.m_ButtonName;
                            if(string.IsNullOrEmpty(but_name)) but_name = method.Name;
                            if(GUILayout.Button(but_name)) {
                                method.Invoke(target, ba.m_Params);
                            }
                        }
                    }
                }
                GUILayout.EndVertical();
            }

        }
    }
}