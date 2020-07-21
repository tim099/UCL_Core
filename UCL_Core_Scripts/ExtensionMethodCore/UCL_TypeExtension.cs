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
    //Primitive Type、Reference Type、Value Type
    //https://dotblogs.com.tw/Mystic_Pieces/2017/10/15/020448
    //All Primitive Type: Boolean, Byte, SByte, Int16, UInt16, Int32, UInt32, Int64, UInt64, IntPtr, UIntPtr, Char, Double, Single

    public static bool IsStruct(this Type type) {
        return type.IsValueType && !type.IsPrimitive;
    }
    public static bool IsStructOrClass(this Type type) {
        return type.IsStruct() | type.IsClass;
    }

}
