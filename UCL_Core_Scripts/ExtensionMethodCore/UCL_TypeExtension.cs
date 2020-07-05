using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public static partial class ExtensionMethods {
    public static MethodInfo[] GetAllMethods(this Type type) {
        var methods = new List<MethodInfo>();
        methods.AddRange(type.GetMethods());

        var base_type = type.BaseType;
        while(base_type != null) {
            methods.AddRange(base_type.GetMethods());
            base_type = base_type.BaseType;
        }

        return methods.ToArray();
    }
}
