using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.ATTR {
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class UCL_DrawStringAttribute : Attribute {
        public object[] m_Params;
        public UCL_DrawStringAttribute(params object[] _params) {
            m_Params = _params;
        }
    }
}