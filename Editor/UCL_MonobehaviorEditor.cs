using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Reflection;

namespace UCL.Core.EditorLib {
    public static class DrawATTR {
        public static void Draw(UnityEngine.Object iTarget, Type iType, Type iClassType) {
            var aMethods = iType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance);
            //.Where(m => m.GetCustomAttributes(typeof(ATTR.UCL_FunctionButtonAttribute), false).Length > 0).ToArray();
            //Debug.LogWarning("type:" + type.Name + ",methods:" + methods.Length);
            Exception aException = null;
            if(aMethods.Length > 0) {
                var aClassName = iClassType.Name;

                GUILayout.BeginVertical();
                //EditorGUILayout.BeginVertical();
                for(int i = 0; i < aMethods.Length; i++) {
                    var aMethod = aMethods[i];

                    {
                        var aAttrType = typeof(ATTR.UCL_DrawStringAttribute);
                        try {
                            var aAttr = aMethod.GetCustomAttributes(aAttrType, false);
                            if(aAttr.Length > 0) {
                                for(int j = 0; j < aAttr.Length; j++) {
                                    var ba = (ATTR.UCL_DrawStringAttribute)aAttr[j];
                                    string str = aMethod.Invoke(iTarget, ba.m_Params) as string;
                                    if(!string.IsNullOrEmpty(str)) GUILayout.Box(str);
                                }
                            }
                        } catch(Exception e) {
                            aException = e;
                            Debug.LogWarning(aAttrType.Name + ":"
                                + aClassName + " " + aAttrType.Name + " Exception:" + e);
                        }
                    }
                    {
                        var aAttrType = typeof(ATTR.UCL_FunctionButtonAttribute);
                        try {
                            var aAttr = aMethod.GetCustomAttributes(aAttrType, false);
                            if(aAttr.Length > 0) {
                                bool run_time_only = aMethod.GetCustomAttributes(typeof(ATTR.UCL_RuntimeOnlyAttribute), false).Length > 0;
                                if(!run_time_only || Application.isPlaying) {
                                    for(int j = 0; j < aAttr.Length; j++) {
                                        var ba = (ATTR.UCL_FunctionButtonAttribute)aAttr[j];
                                        string but_name = ba.m_ButtonName;
                                        if(string.IsNullOrEmpty(but_name)) but_name = aMethod.Name;
                                        if(GUILayout.Button(but_name)) {
                                            aMethod.Invoke(iTarget, ba.m_Params);
                                        }
                                    }
                                }
                            }
                        } catch(Exception e) {
                            aException = e;
                            Debug.LogWarning("UCL_FunctionButton:"
                                + aClassName + " " + aAttrType.Name + " Exception:" + e);
                        }
                    }
                    {
                        var aAttrType = typeof(ATTR.UCL_DrawTexture2DAttribute);
                        try {
                            var aAttr = aMethod.GetCustomAttributes(aAttrType, false);
                            if(aAttr.Length > 0) {
                                //Debug.LogWarning("attr.Length:" + attr.Length);
                                GUILayout.Box(aMethod.Name);
                                for(int j = 0; j < aAttr.Length; j++) {
                                    var ba = (ATTR.UCL_DrawTexture2DAttribute)aAttr[j];
                                    var return_type = aMethod.ReturnType;
                                    //Debug.LogWarning("return_type:" + return_type.Name);
                                    if(return_type.IsAssignableFrom(typeof(Core.TextureLib.UCL_Texture2D))) {//IsSubclassOf
                                        var tex = aMethod.Invoke(iTarget, null) as Core.TextureLib.UCL_Texture2D;
                                        if(tex != null) GUILayout.Box(tex.texture);
                                    } else {
                                        var tex = ba.GetTexture();
                                        aMethod.Invoke(iTarget, new object[] { tex });
                                        GUILayout.Box(tex.texture);
                                    }

                                }
                            }
                        } catch(Exception e) {
                            aException = e;
                            Debug.LogWarning("UCL_DrawTexture2D:"
                                + aClassName + " " + aAttrType.Name + " Exception:" + e);
                        }
                    }
                    {
                        var aAttrType = typeof(ATTR.UCL_DrawObjectAttribute);
                        try {
                            var aAttr = aMethod.GetCustomAttributes(aAttrType, false);
                            if(aAttr.Length > 0) {
                                for(int j = 0; j < aAttr.Length; j++) {
                                    System.Func<System.Type, UnityEngine.Object, UnityEngine.Object> func = 
                                        delegate (System.Type stype, UnityEngine.Object obj) {
                                            return UnityEditor.EditorGUILayout.ObjectField(obj, stype, true);
                                        };
                                    aMethod.Invoke(iTarget, new object[1] { func });

                                    //UnityEngine.Object obj = method.Invoke(target, null) as UnityEngine.Object;
                                    //UnityEditor.EditorGUILayout.ObjectField(obj, obj.GetType(), true);
                                }
                            }
                        } catch(Exception e) {
                            aException = e;
                            Debug.LogWarning("UCL_FunctionButton:"
                                + aClassName + " " + aAttrType.Name + " Exception:" + e);
                        }
                    }
                    {
                        //UnityEditor.EditorGUILayout.ObjectField()
                        var aAttrType = typeof(ATTR.UCL_DrawOnGUIAttribute);
                        try
                        {
                            var aAttr = aMethod.GetCustomAttributes(aAttrType, false);
                            if (aAttr.Length > 0)
                            {
                                aMethod.Invoke(iTarget, null);
                            }
                        }
                        catch (Exception e)
                        {
                            aException = e;
                            Debug.LogWarning("UCL_FunctionButton:"
                                + aClassName + " " + aAttrType.Name + " Exception:" + e);
                        }
                    }
                }
                GUILayout.EndVertical();

                if(aException != null) {
                    Debug.LogException(aException);
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

