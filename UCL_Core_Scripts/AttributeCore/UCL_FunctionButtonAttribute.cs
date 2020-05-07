using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.ATTR {
    public class UCL_FunctionButtonAttribute : Attribute {
        public string m_ButtonName;
        public UCL_FunctionButtonAttribute() {

        }
        public UCL_FunctionButtonAttribute(string _ButtonName) {
            m_ButtonName = _ButtonName;
        }
    }
}

