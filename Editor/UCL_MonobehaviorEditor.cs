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

            var methods = type.GetMethods();//.Where(m => m.GetCustomAttributes(typeof(ATTR.UCL_FunctionButtonAttribute), false).Length > 0).ToArray();
            //Debug.LogWarning("type:" + type.Name + ",methods:" + methods.Length);
            if(methods.Length > 0) {
                GUILayout.BeginVertical();
                //EditorGUILayout.BeginVertical();
                for(int i = 0; i < methods.Length; i++) {
                    var method = methods[i];
                    try {
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
                    } catch(Exception e) {
                        Debug.LogError("UCL_MonobehaviorEditor ATTR.UCL_FunctionButtonAttribute Exception:" + e);
                    }

                    try {
                        var attr = method.GetCustomAttributes(typeof(ATTR.UCL_DrawTexture2DAttribute), false);
                        if(attr.Length > 0) {
                            for(int j = 0; j < attr.Length; j++) {
                                var ba = (ATTR.UCL_DrawTexture2DAttribute)attr[j];
                                var tex = ba.GetTexture();
                                method.Invoke(target, new object[] { tex });
                                GUILayout.Box(tex.texture);
                            }
                        }
                    } catch(Exception e) {
                        Debug.LogError("UCL_MonobehaviorEditor ATTR.UCL_DrawTexture2DAttribute Exception:" + e);
                    }
                }
                GUILayout.EndVertical();
                //EditorGUILayout.EndVertical();
            }

        }
    }
}

