using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.ATTR {
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    //[System.Diagnostics.Conditional("UNITY_EDITOR")]
    public class EnableUCLEditor : Attribute { }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    //[System.Diagnostics.Conditional("UNITY_EDITOR")]
    public class RequiresConstantRepaintAttribute : Attribute { }

    /// <summary>
    /// Always show detail OnGUI(when you draw object using UCL.Core.UI.UCL_GUILayout.DrawObjectData)
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Field, AllowMultiple = false)]
    public class AlwaysExpendOnGUI : Attribute { }

    public static class Lib {

    }
}