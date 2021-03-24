using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.ATTR {
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public class EnableUCLEditor : Attribute { }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public class RequiresConstantRepaintAttribute : Attribute { }
    public static class Lib {

    }
}