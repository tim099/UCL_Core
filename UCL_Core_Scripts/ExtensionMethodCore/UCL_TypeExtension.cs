using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using System.Linq;
public static partial class TypeExtensionMethods {
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
    /// <summary>
    /// Get Fields Include parent
    /// </summary>
    /// <param name="type"></param>
    /// <param name="bindingAttr"></param>
    /// <returns></returns>
    public static List<FieldInfo> GetAllFields(this Type type, BindingFlags bindingAttr) {
        FieldInfo[] fields = type.GetFields(bindingAttr);
        var list = fields.ToList();
        var base_type = type.BaseType;
        if(base_type != null) {
            if((bindingAttr & BindingFlags.Public) != 0) {
                bindingAttr ^= BindingFlags.Public;
            }
            var list2 = base_type.GetAllFields(bindingAttr);
            for(int i = 0; i < list.Count; i++) {
                list2.Add(list[i]);
            }
            return list2;
        }
        return list;
    }
    /// <summary>
    /// Get Fields Include parent,until parent is end_type
    /// </summary>
    /// <param name="type"></param>
    /// <param name="end_type"></param>
    /// <param name="bindingAttr"></param>
    /// <returns></returns>
    public static List<FieldInfo> GetAllFieldsUntil(this Type type, Type end_type, BindingFlags bindingAttr) {
        FieldInfo[] fields = type.GetFields(bindingAttr);
        var list = fields.ToList();
        var base_type = type.BaseType;
        if(base_type != null && base_type != end_type) {
            if((bindingAttr & BindingFlags.Public) != 0) {
                bindingAttr ^= BindingFlags.Public;
            }
            var list2 = base_type.GetAllFieldsUntil(end_type, bindingAttr);
            for(int i = 0; i < list.Count; i++) {
                list2.Add(list[i]);
            }
            return list2;
        }
        return list;
    }
}
