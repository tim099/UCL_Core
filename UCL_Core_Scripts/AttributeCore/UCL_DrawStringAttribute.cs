﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UCL.Core.ATTR {
    //[System.Diagnostics.Conditional("UNITY_EDITOR")]
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class UCL_DrawStringAttribute : UCL_Attribute {
        public object[] m_Params;
        public UCL_DrawStringAttribute(params object[] iParams) {
            m_Params = iParams;
        }
        public override void Draw(object iTarget, MethodInfo iMethodInfo, UCL_ObjectDictionary iDic)
        {
            string aStr = iMethodInfo.Invoke(iTarget, m_Params) as string;
            if (!string.IsNullOrEmpty(aStr)) GUILayout.Box(aStr);
        }
    }
}