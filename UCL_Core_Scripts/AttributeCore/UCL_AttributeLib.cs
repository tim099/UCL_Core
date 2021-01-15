using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.ATTR {
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class EnableUCLEditor : Attribute { }

    public class RequiresConstantRepaintAttribute : Attribute { }
    public static class Lib {

    }
}