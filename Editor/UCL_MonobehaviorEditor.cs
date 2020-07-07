using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.Linq;

namespace UCL.Core.EditorLib {
    [CustomEditor(typeof(MonoBehaviour),true)]
    public class UCL_MonobehaviorEditor : Editor {
        bool m_RequiresConstantRepaint = false;
        public override bool RequiresConstantRepaint() {
            return m_RequiresConstantRepaint;
        }
        public override void OnInspectorGUI() {
            DrawDefaultInspector();
            Type type = target.GetType();
            if(type.GetCustomAttributes(typeof(ATTR.EnableUCLEditor), true).Length == 0) return;
            m_RequiresConstantRepaint = (type.GetCustomAttributes(typeof(ATTR.RequiresConstantRepaintAttribute), true).Length > 0);
            var methods = type.GetMethods();//.Where(m => m.GetCustomAttributes(typeof(ATTR.UCL_FunctionButtonAttribute), false).Length > 0).ToArray();
            //Debug.LogWarning("type:" + type.Name + ",methods:" + methods.Length);
            if(methods.Length > 0) {
                var class_type = this.GetType();
                var class_name = class_type.Name;

                GUILayout.BeginVertical();
                //EditorGUILayout.BeginVertical();
                for(int i = 0; i < methods.Length; i++) {
                    var method = methods[i];
                    {
                        var attr_type = typeof(ATTR.UCL_FunctionButtonAttribute);
                        try {
                            var attr = method.GetCustomAttributes(attr_type, false);
                            if(attr.Length > 0) {
                                bool run_time_only = method.GetCustomAttributes(typeof(ATTR.UCL_RuntimeOnlyAttribute), false).Length > 0;
                                if(!run_time_only || Application.isPlaying) {
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
                        } catch(Exception e) {
                            Debug.LogError(class_name + " " + attr_type.Name + " Exception:" + e);
                        }
                    }
                    {
                        var attr_type = typeof(ATTR.UCL_DrawTexture2DAttribute);
                        try {
                            var attr = method.GetCustomAttributes(attr_type, false);
                            if(attr.Length > 0) {
                                //Debug.LogWarning("attr.Length:" + attr.Length);
                                GUILayout.Box(method.Name);
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
                Resources.UnloadUnusedAssets();
                //EditorGUILayout.EndVertical();
            }

        }
    }
}

