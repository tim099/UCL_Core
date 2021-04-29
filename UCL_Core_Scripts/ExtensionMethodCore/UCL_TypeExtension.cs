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
    /// <param name="iType"></param>
    /// <param name="iBindingAttr"></param>
    /// <returns></returns>
    public static List<FieldInfo> GetAllFields(this Type iType, BindingFlags iBindingAttr) {
        FieldInfo[] aFields = iType.GetFields(iBindingAttr);
        List<FieldInfo> aList = new List<FieldInfo>();//aFields.ToList();
        HashSet<string> aFieldNameHash = new HashSet<string>();
        for (int i = 0; i < aFields.Length; i++)
        {
            var aField = aFields[i];
            aFieldNameHash.Add(aField.Name);
            aList.Add(aField);
        }
        
        var aBaseType = iType.BaseType;
        
        if(aBaseType != null) {
            if((iBindingAttr & BindingFlags.Public) != 0) {
                iBindingAttr ^= BindingFlags.Public;
            }
            var aList2 = aBaseType.GetAllFields(iBindingAttr);
            for(int i = 0; i < aList2.Count; i++) {
                var aField = aList2[i];
                if (!aFieldNameHash.Contains(aField.Name))
                {
                    aFieldNameHash.Add(aField.Name);
                    aList.Add(aField);
                }
            }
        }
        return aList;
    }
    /// <summary>
    /// Get Fields Include parent, until parent is iEndType
    /// </summary>
    /// <param name="iType"></param>
    /// <param name="iEndType"></param>
    /// <param name="iBindingAttr"></param>
    /// <returns></returns>
    public static List<FieldInfo> GetAllFieldsUntil(this Type iType, Type iEndType, BindingFlags iBindingAttr) {
        FieldInfo[] aFields = iType.GetFields(iBindingAttr);
        List<FieldInfo> aList = new List<FieldInfo>();//aFields.ToList();
        HashSet<string> aFieldNameHash = new HashSet<string>();
        for (int i = 0; i < aFields.Length; i++)
        {
            var aField = aFields[i];
            aFieldNameHash.Add(aField.Name);
            aList.Add(aField);
        }

        var aBaseType = iType.BaseType;

        if (aBaseType != null && aBaseType != iEndType)
        {
            if ((iBindingAttr & BindingFlags.Public) != 0)
            {
                iBindingAttr ^= BindingFlags.Public;
            }
            var aList2 = aBaseType.GetAllFieldsUntil(iEndType, iBindingAttr);
            for (int i = 0; i < aList2.Count; i++)
            {
                var aField = aList2[i];
                if (!aFieldNameHash.Contains(aField.Name))
                {
                    aFieldNameHash.Add(aField.Name);
                    aList.Add(aField);
                }
            }
        }
        return aList;
    }

    /// <summary>
    /// Get Fields Include parent, until parent is iEndType
    /// Ignore Fields with [HideInInspector] 
    /// Ignore NonPublic Fields whithout[SerializeField]
    /// </summary>
    /// <param name="iType"></param>
    /// <param name="iEndType"></param>
    /// <returns></returns>
    public static List<FieldInfo> GetAllFieldsUnityVer(this Type iType, Type iEndType = null, int aLayer = 0)
    {
        List<FieldInfo> aList = new List<FieldInfo>();
        HashSet<string> aFieldNameHash = new HashSet<string>();
        if (aLayer == 0)//get Public field of root type
        {
            FieldInfo[] aPublicFields = iType.GetFields(BindingFlags.Instance | BindingFlags.Public);
            
            for (int i = 0; i < aPublicFields.Length; i++)
            {
                var aField = aPublicFields[i];
                if (aField.GetCustomAttribute<HideInInspector>() == null)
                {
                    aFieldNameHash.Add(aField.Name);
                    aList.Add(aField);
                }
            }
        }
        {//get NonPublicFields
            FieldInfo[] aNonPublicFields = iType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
            for (int i = 0; i < aNonPublicFields.Length; i++)
            {
                var aField = aNonPublicFields[i];
                if (aField.GetCustomAttribute<SerializeField>() != null)//SerializeField Only!!
                {
                    aFieldNameHash.Add(aField.Name);
                    aList.Add(aField);
                }
            }
        }

        var aBaseType = iType.BaseType;

        if (aBaseType != null && aBaseType != iEndType)
        {
            var aList2 = aBaseType.GetAllFieldsUnityVer(iEndType, aLayer + 1);
            for (int i = 0; i < aList2.Count; i++)
            {
                var aField = aList2[i];
                if (!aFieldNameHash.Contains(aField.Name))
                {
                    aFieldNameHash.Add(aField.Name);
                    aList.Add(aField);
                }
            }
        }
        return aList;
    }
}
