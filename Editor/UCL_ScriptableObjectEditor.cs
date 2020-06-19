﻿using System.Collections;
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
                
                var class_type = this.GetType();
                var class_name = class_type.Name;

                GUILayout.BeginVertical();
                for(int i = 0; i < methods.Length; i++) {
                    var method = methods[i];

                    {
                        var attr_type = typeof(ATTR.UCL_FunctionButtonAttribute);
                        try {
                            var attr = method.GetCustomAttributes(attr_type, false);
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
                            Debug.LogError(class_name + " " + attr_type.Name + " Exception:" + e);
                        }
                    }
                    {
                        var attr_type = typeof(ATTR.UCL_DrawTexture2DAttribute);
                        try {
                            var attr = method.GetCustomAttributes(attr_type, false);
                            if(attr.Length > 0) {
                                for(int j = 0; j < attr.Length; j++) {
                                    var ba = (ATTR.UCL_DrawTexture2DAttribute)attr[j];
                                    var tex = ba.GetTexture();
                                    method.Invoke(target, new object[] { tex });
                                    GUILayout.Box(tex.texture);
                                }
                            }
                        } catch(Exception e) {
                            Debug.LogError(class_name + " " + attr_type.Name + " Exception:" + e);
                        }
                    }

                }
                GUILayout.EndVertical();
            }

        }
    }
}