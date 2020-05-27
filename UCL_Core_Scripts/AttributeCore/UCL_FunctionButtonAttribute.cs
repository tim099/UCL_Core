using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.ATTR {
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class UCL_FunctionButtonAttribute : Attribute {
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
    }
}

