﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
namespace UCL.Core.ATTR {
    /// <summary>
    /// Create a button trigger member function
    /// </summary>
    //[System.Diagnostics.Conditional("UNITY_EDITOR")]
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class UCL_FunctionButtonAttribute : UCL_Attribute {
        public string m_ButtonName;
        public object[] m_Params;
        public UCL_FunctionButtonAttribute() {

        }
        public UCL_FunctionButtonAttribute(string _ButtonName) {
            m_ButtonName = _ButtonName;
        }
        public UCL_FunctionButtonAttribute(string _ButtonName, params object[] _params) {
            m_ButtonName = _ButtonName;
            m_Params = _params;
        }
        public override void Draw(object iTarget, MethodInfo iMethodInfo, UCL_ObjectDictionary iDic)
        {
            bool aIsRunTimeOnly = iMethodInfo.GetCustomAttribute(typeof(ATTR.UCL_RuntimeOnlyAttribute), false) != null;
            if (!aIsRunTimeOnly || Application.isPlaying)
            {
                string aButName = m_ButtonName;
                if (string.IsNullOrEmpty(aButName)) aButName = iMethodInfo.Name;
                if (GUILayout.Button(aButName))
                {
                    iMethodInfo.Invoke(iTarget, m_Params);
                }
            }
        }
    }
    /// <summary>
    /// Function button only show on Editor runtime
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class UCL_RuntimeOnlyAttribute : Attribute {
        public UCL_RuntimeOnlyAttribute() {

        }
    }
}

