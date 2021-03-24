using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.ATTR {
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class UCL_DrawObjectAttribute : Attribute {

    }
}