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

    public static bool IsStruct(this Type iType) {
        return iType.IsValueType && !iType.IsPrimitive;
    }
    public static bool IsStructOrClass(this Type iType) {
        return iType.IsStruct() | iType.IsClass;
    }
    public static bool IsString(this Type iType)
    {
        return iType == typeof(string);
    }
    public static bool IsBool(this Type iType)
    {
        return iType == typeof(bool);
    }
    public static bool IsNumericType(this Type iType)
    {
        switch (Type.GetTypeCode(iType))
        {
            case TypeCode.Byte:
            case TypeCode.SByte:
            case TypeCode.UInt16:
            case TypeCode.UInt32:
            case TypeCode.UInt64:
            case TypeCode.Int16:
            case TypeCode.Int32:
            case TypeCode.Int64:
            case TypeCode.Decimal:
            case TypeCode.Double:
            case TypeCode.Single:
                return true;            
        }
        switch (Type.GetTypeCode(Nullable.GetUnderlyingType(iType)))
        {
            case TypeCode.Byte:
            case TypeCode.SByte:
            case TypeCode.UInt16:
            case TypeCode.UInt32:
            case TypeCode.UInt64:
            case TypeCode.Int16:
            case TypeCode.Int32:
            case TypeCode.Int64:
            case TypeCode.Decimal:
            case TypeCode.Double:
            case TypeCode.Single:
                return true;
        }
        return false;
    }
    public static bool IsNumber(this Type iType)
    {
        return iType == typeof(int)
            || iType == typeof(float)
                || iType == typeof(byte)
                || iType == typeof(short)
                || iType == typeof(ushort)
                || iType == typeof(sbyte)
                || iType == typeof(uint)
                || iType == typeof(long)
                || iType == typeof(ulong)
                || iType == typeof(double)
                || iType == typeof(decimal);
    }
    /// <summary>
    /// Convert the string to number of type
    /// </summary>
    /// <param name="iType"></param>
    /// <param name="iString"></param>
    /// <returns></returns>
    public static object TryParseToNumber(this Type iType, string iString)
    {
        if (iType == typeof(int))
        {
            int aResult;
            if (!int.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(float))
        {
            float aResult;
            if (!float.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(sbyte))
        {
            sbyte aResult;
            if (!sbyte.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(byte))
        {
            byte aResult;
            if (!byte.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(short))
        {
            short aResult;
            if (!short.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(ushort))
        {
            ushort aResult;
            if (!ushort.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(uint))
        {
            uint aResult;
            if (!uint.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(long))
        {
            long aResult;
            if (!long.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(ulong))
        {
            ulong aResult;
            if (!ulong.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(double))
        {
            double aResult;
            if (!double.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }
        if (iType == typeof(decimal))
        {
            decimal aResult;
            if (!decimal.TryParse(iString, out aResult))
            {
                aResult = 0;
            }
            return aResult;
        }

        return 0;
    }

    public static string GetTypeName(this Type iType)
    {
        if (iType.IsGenericType)
        {
            if (typeof(IList).IsAssignableFrom(iType))
            {
                var aGenericArguments = iType.GetGenericArguments();
                if (!aGenericArguments.IsNullOrEmpty())
                {
                    return $"IList<{iType.GetGenericArguments()[0].Name}>";
                }
            }
            else if (typeof(IDictionary).IsAssignableFrom(iType))
            {
                var aGenericArguments = iType.GetGenericArguments();
                if (aGenericArguments.Length >= 2)
                {
                    return $"IDictionary<{aGenericArguments[0].Name}{aGenericArguments[1].Name}>";
                }
            }
        }
        return iType.Name;
    }
    /// <summary>
    /// return type of a GenericType(List,Array,Dic)
    /// etc. List<T> will return type of T
    /// </summary>
    /// <param name="iType"></param>
    /// <returns></returns>
    public static Type GetGenericKeyType(this Type iType)
    {
        var aGenericTypeArguments = iType.GetGenericArguments();
        return aGenericTypeArguments[0];
    }
    /// <summary>
    /// return type of a GenericType(List,Array,Dic)
    /// etc. List<T> will return type of T
    /// </summary>
    /// <param name="iType"></param>
    /// <returns></returns>
    public static Type GetGenericValueType(this Type iType)
    {
        var aGenericTypeArguments = iType.GetGenericArguments();//GetTypeInfo().
        var aContentType = aGenericTypeArguments[0];
        if (typeof(IDictionary).IsAssignableFrom(iType))
        {
            aContentType = aGenericTypeArguments[1];//[0] is Key type [1] is Value type!!
        }
        return aContentType;
    }
    /// <summary>
    /// Create Instance of input type
    /// </summary>
    /// <param name="iType"></param>
    /// <returns></returns>
    public static object CreateInstance(this Type iType)
    {
        if (iType == null) return null;

        if (iType == typeof(string)) return string.Empty;
        if (iType == typeof(int)) return (int)0;
        if (iType == typeof(uint)) return (uint)0;
        if (iType == typeof(long)) return (long)0;
        if (iType == typeof(ulong)) return (ulong)0;
        if (iType == typeof(short)) return (short)0;
        if (iType == typeof(ushort)) return (ushort)0;
        if (iType == typeof(byte)) return (byte)0;
        if (iType == typeof(sbyte)) return (sbyte)0;
        if (iType == typeof(float)) return (float)0;
        if (iType == typeof(double)) return (double)0;
        if (typeof(UnityEngine.Object).IsAssignableFrom(iType))
        {
            if (typeof(Component).IsAssignableFrom(iType)) return null;
            if (iType == typeof(Sprite)) return null;
            
            Debug.LogError("iType:" + iType.Name + ",is UnityEngine.Object!!");
            //return null;
        }
        if (iType.IsTuple())
        {
            //Debug.LogError("iType:" + iType.Name + "iType.IsTuple():" + iType.IsTuple());
            Type[] aTypeArray = iType.GetGenericArguments();

            var aConstructer = iType.GetConstructor(aTypeArray);
            if (aConstructer != null)
            {
                object[] aValues = new object[aTypeArray.Length];
                for (int i = 0; i < aTypeArray.Length; i++)
                {
                    aValues[i] = aTypeArray[i].CreateInstance();
                }
                return aConstructer.Invoke(aValues);
            }
        }
        object aObj = null;
        try
        {
            aObj = Activator.CreateInstance(iType);
        }catch(System.Exception iE)
        {
            Debug.LogException(iE);
        }
        return aObj;
    }
    private static HashSet<Type> s_TupleTypes = null;
    /// <summary>
    /// return true if iType is tuple
    /// </summary>
    /// <param name="iType"></param>
    /// <returns></returns>
    public static bool IsTuple(this Type iType)
    {
        if (iType == null) return false;
        if (!iType.IsGenericType) return false;
        if(s_TupleTypes == null)
        {
            s_TupleTypes = new HashSet<Type>(){ 
                 //typeof(ValueTuple<>), typeof(ValueTuple<,>),
                 //typeof(ValueTuple<,,>), typeof(ValueTuple<,,,>),
                 //typeof(ValueTuple<,,,,>), typeof(ValueTuple<,,,,,>),
                 //typeof(ValueTuple<,,,,,,>), typeof(ValueTuple<,,,,,,,>),
                 typeof(Tuple<>), typeof(Tuple<,>),
                 typeof(Tuple<,,>), typeof(Tuple<,,,>),
                 typeof(Tuple<,,,,>), typeof(Tuple<,,,,,>),
                 typeof(Tuple<,,,,,,>), typeof(Tuple<,,,,,,,>)

            };
        }
        var aGenType = iType.GetGenericTypeDefinition();
        //Debug.LogError("aGenType:" + aGenType.Name+ ",iType:"+ iType.Name);
        return s_TupleTypes.Contains(aGenType);
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
