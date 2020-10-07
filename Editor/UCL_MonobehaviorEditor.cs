using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Reflection;

namespace UCL.Core.EditorLib {
    public static class DrawATTR {
        public static void Draw(UnityEngine.Object target, Type type, Type class_type) {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance);
            //.Where(m => m.GetCustomAttributes(typeof(ATTR.UCL_FunctionButtonAttribute), false).Length > 0).ToArray();
            //Debug.LogWarning("type:" + type.Name + ",methods:" + methods.Length);
            Exception exception = null;
            if(methods.Length > 0) {
                var class_name = class_type.Name;

                GUILayout.BeginVertical();
                //EditorGUILayout.BeginVertical();
                for(int i = 0; i < methods.Length; i++) {
                    var method = methods[i];

                    {
                        var attr_type = typeof(ATTR.UCL_DrawStringAttribute);
                        try {
                            var attr = method.GetCustomAttributes(attr_type, false);
                            if(attr.Length > 0) {
                                for(int j = 0; j < attr.Length; j++) {
                                    var ba = (ATTR.UCL_DrawStringAttribute)attr[j];
                                    string str = method.Invoke(target, ba.m_Params) as string;
                                    if(!string.IsNullOrEmpty(str)) GUILayout.Box(str);
                                }
                            }
                        } catch(Exception e) {
                            exception = e;
                            Debug.LogWarning(attr_type.Name + ":"
                                + class_name + " " + attr_type.Name + " Exception:" + e);
                        }
                    }
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
                            exception = e;
                            Debug.LogWarning("UCL_FunctionButton:"
                                + class_name + " " + attr_type.Name + " Exception:" + e);
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
                                    var return_type = method.ReturnType;
                                    //Debug.LogWarning("return_type:" + return_type.Name);
                                    if(return_type.IsAssignableFrom(typeof(Core.TextureLib.UCL_Texture2D))) {//IsSubclassOf
                                        var tex = method.Invoke(target, null) as Core.TextureLib.UCL_Texture2D;
                                        if(tex != null) GUILayout.Box(tex.texture);
                                    } else {
                                        var tex = ba.GetTexture();
                                        method.Invoke(target, new object[] { tex });
                                        GUILayout.Box(tex.texture);
                                    }

                                }
                            }
                        } catch(Exception e) {
                            exception = e;
                            Debug.LogWarning("UCL_DrawTexture2D:"
                                + class_name + " " + attr_type.Name + " Exception:" + e);
                        }
                    }
                    {
                        //UnityEditor.EditorGUILayout.ObjectField()
                        var attr_type = typeof(ATTR.UCL_DrawObjectAttribute);
                        try {
                            var attr = method.GetCustomAttributes(attr_type, false);
                            if(attr.Length > 0) {
                                for(int j = 0; j < attr.Length; j++) {
                                    System.Func<System.Type, UnityEngine.Object, UnityEngine.Object> func = 
                                        delegate (System.Type stype, UnityEngine.Object obj) {
                                            return UnityEditor.EditorGUILayout.ObjectField(obj, stype, true);
                                        };
                                    method.Invoke(target, new object[1] { func });

                                    //UnityEngine.Object obj = method.Invoke(target, null) as UnityEngine.Object;
                                    //UnityEditor.EditorGUILayout.ObjectField(obj, obj.GetType(), true);
                                }
                            }
                        } catch(Exception e) {
                            exception = e;
                            Debug.LogWarning("UCL_FunctionButton:"
                                + class_name + " " + attr_type.Name + " Exception:" + e);
                        }
                    }
                }
                GUILayout.EndVertical();

                if(exception != null) {
                    throw exception;
                }
                //EditorGUILayout.EndVertical();
            }
        }
    }
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
            DrawATTR.Draw(target, type, this.GetType());
            Resources.UnloadUnusedAssets();
        }
    }
}

