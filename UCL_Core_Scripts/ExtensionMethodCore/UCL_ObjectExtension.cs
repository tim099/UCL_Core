using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;


public static partial class ObjectExtensionMethods {
    /// <summary>
    /// Create Instance of input iObj and copy value
    /// </summary>
    /// <param name="iType"></param>
    /// <returns></returns>
    public static object CopyInstance(this object iObj)
    {
        if (iObj == null) return null;
        var aSaveMode = UCL.Core.JsonLib.JsonConvert.SaveMode.Unity;
        return UCL.Core.JsonLib.JsonConvert.LoadDataFromJson(
            UCL.Core.JsonLib.JsonConvert.SaveDataToJson(iObj, aSaveMode), iObj.GetType(), aSaveMode);
    }
    public static bool IsNumber(this object iObj) {
        return iObj is sbyte
                || iObj is byte
                || iObj is short
                || iObj is ushort
                || iObj is int
                || iObj is uint
                || iObj is long
                || iObj is ulong
                || iObj is float
                || iObj is double
                || iObj is decimal;
    }
    /// <summary>
    /// return true if iObj is tuple
    /// </summary>
    /// <param name="iObj"></param>
    /// <returns></returns>
    public static bool IsTuple(this object iObj)
    {
        if (iObj == null) return false;
        return iObj.GetType().IsTuple();
    }
    public static List<object> GetTupleElements(this object iObj)
    {
        var aType = iObj.GetType();
        if (!aType.IsTuple()) return new List<object>();

        var aResult = aType.GetProperties()
          .Where(iProp => iProp.CanRead)
          //.Where(iProp => !iProp.GetIndexParameters().Any())
          //.Where(iProp => Regex.IsMatch(iProp.Name, "^Item[0-9]+$"))
          .Select(iProp => iProp.GetValue(iObj)).ToList();
        return aResult;
    }
}
namespace UCL.Core.ObjectOperatorExtension {
    public static partial class ExtensionMethods {
        public static bool IsZero(this object value) {
            if(value is sbyte) {
                return (sbyte)value == 0;
            } else if(value is byte) {
                return (byte)value == 0;
            } else if(value is short) {
                return (short)value == 0;
            } else if(value is ushort) {
                return (ushort)value == 0;
            } else if(value is int) {
                return (int)value == 0;
            } else if(value is uint) {
                return (uint)value == 0;
            } else if(value is long) {
                return (long)value == 0;
            } else if(value is ulong) {
                return (ulong)value == 0;
            } else if(value is ulong) {
                return (float)value == 0;
            } else if(value is double) {
                return (double)value == 0;
            } else if(value is decimal) {
                return (decimal)value == 0;
            }
            return false;
        }
        public static void EnumDoOR(this object a, object b) {
            var _type = a.GetType();
            var type = Enum.GetUnderlyingType(_type);
            if(type == typeof(int)) {
                a = ((int)a) | ((int)b);
            } else if(type == typeof(uint)) {
                a = ((uint)a) | ((uint)b);
            } else if(type == typeof(short)) {
                a = unchecked((short)((short)a | (short)b));
            } else if(type == typeof(ushort)) {
                a = unchecked((ushort)((ushort)a | (ushort)b));
            } else if(type == typeof(long)) {
                a = ((long)a) | ((long)b);
            } else if(type == typeof(ulong)) {
                a = ((ulong)a) | ((ulong)b);
            } else if(type == typeof(byte)) {
                a = unchecked((byte)((byte)a | (byte)b));
            } else if(type == typeof(sbyte)) {
                a = unchecked((sbyte)((sbyte)a | (sbyte)b));
            } else {
                throw new System.Exception("object.DoOR() Fail!! Type:" + type.FullName + " not support!!");
            }
        }
        public static object OR(this object a, object b) {
            var type = a.GetType();
            if(type == typeof(int)) {
                return ((int)a) | ((int)b);
            } else if(type == typeof(uint)) {
                return ((uint)a) | ((uint)b);
            } else if(type == typeof(short)) {
                return unchecked((short)((short)a | (short)b));
            } else if(type == typeof(ushort)) {
                return unchecked((ushort)((ushort)a | (ushort)b));
            } else if(type == typeof(long)) {
                return ((long)a) | ((long)b);
            } else if(type == typeof(ulong)) {
                return ((ulong)a) | ((ulong)b);
            } else if(type == typeof(byte)) {
                return unchecked((byte)((byte)a | (byte)b));
            } else if(type == typeof(sbyte)) {
                return unchecked((sbyte)((sbyte)a | (sbyte)b));
            } else {
                throw new System.Exception("object.DoOR() Fail!! Type:" + type.FullName + " not support!!");
            }
        }
        public static object EnumOR(this object a, object b) {
            var _type = a.GetType();
            var type = Enum.GetUnderlyingType(_type);
            if(type == typeof(int)) {
                return ((int)a) | ((int)b);
            } else if(type == typeof(uint)) {
                return ((uint)a) | ((uint)b);
            } else if(type == typeof(short)) {
                return unchecked((short)((short)a | (short)b));
            } else if(type == typeof(ushort)) {
                return unchecked((ushort)((ushort)a | (ushort)b));
            } else if(type == typeof(long)) {
                return ((long)a) | ((long)b);
            } else if(type == typeof(ulong)) {
                return ((ulong)a) | ((ulong)b);
            } else if(type == typeof(byte)) {
                return unchecked((byte)((byte)a | (byte)b));
            } else if(type == typeof(sbyte)) {
                return unchecked((sbyte)((sbyte)a | (sbyte)b));
            } else {
                throw new System.Exception("object.DoOR() Fail!! Type:" + type.FullName + " not support!!");
            }
        }
        public static void EnumDoAND(this object a, object b) {
            var _type = a.GetType();
            var type = Enum.GetUnderlyingType(_type);
            if(type == typeof(int)) {
                a = ((int)a) & ((int)b);
            } else if(type == typeof(uint)) {
                a = ((uint)a) & ((uint)b);
            } else if(type == typeof(short)) {
                a = unchecked((short)((short)a & (short)b));
            } else if(type == typeof(ushort)) {
                a = unchecked((ushort)((ushort)a & (ushort)b));
            } else if(type == typeof(long)) {
                a = ((long)a) & ((long)b);
            } else if(type == typeof(ulong)) {
                a = ((ulong)a) & ((ulong)b);
            } else if(type == typeof(byte)) {
                a = unchecked((byte)((byte)a & (byte)b));
            } else if(type == typeof(sbyte)) {
                a = unchecked((sbyte)((sbyte)a & (sbyte)b));
            } else {
                throw new System.Exception("object.DoAND() Fail!! Type:" + type.FullName + " not support!!");
            }
        }
        public static object AND(this object a, object b) {
            var type = a.GetType();
            if(type == typeof(int)) {
                return ((int)a) & ((int)b);
            } else if(type == typeof(uint)) {
                return ((uint)a) & ((uint)b);
            } else if(type == typeof(short)) {
                return unchecked((short)((short)a & (short)b));
            } else if(type == typeof(ushort)) {
                return unchecked((ushort)((ushort)a & (ushort)b));
            } else if(type == typeof(long)) {
                return ((long)a) & ((long)b);
            } else if(type == typeof(ulong)) {
                return ((ulong)a) & ((ulong)b);
            } else if(type == typeof(byte)) {
                return unchecked((byte)((byte)a & (byte)b));
            } else if(type == typeof(sbyte)) {
                return unchecked((sbyte)((sbyte)a & (sbyte)b));
            } else {
                throw new System.Exception("object.DoAND() Fail!! Type:" + type.FullName + " not support!!");
            }
        }
        public static object EnumAND(this object a, object b) {
            var _type = a.GetType();
            var type = Enum.GetUnderlyingType(_type);
            if(type == typeof(int)) {
                return ((int)a) & ((int)b);
            } else if(type == typeof(uint)) {
                return ((uint)a) & ((uint)b);
            } else if(type == typeof(short)) {
                return unchecked((short)((short)a & (short)b));
            } else if(type == typeof(ushort)) {
                return unchecked((ushort)((ushort)a & (ushort)b));
            } else if(type == typeof(long)) {
                return ((long)a) & ((long)b);
            } else if(type == typeof(ulong)) {
                return ((ulong)a) & ((ulong)b);
            } else if(type == typeof(byte)) {
                return unchecked((byte)((byte)a & (byte)b));
            } else if(type == typeof(sbyte)) {
                return unchecked((sbyte)((sbyte)a & (sbyte)b));
            } else {
                throw new System.Exception("object.DoAND() Fail!! Type:" + type.FullName + " not support!!");
            }
        }
        public static bool EnumDoMaskCheck(this object a, object b) {
            var _type = a.GetType();
            var type = Enum.GetUnderlyingType(_type);
            if(type == typeof(int)) {
                return (((int)a) & ((int)b)) != 0;
            } else if(type == typeof(uint)) {
                return (((uint)a) & ((uint)b)) != 0;
            } else if(type == typeof(short)) {
                return unchecked((short)((short)a & (short)b)) != 0;
            } else if(type == typeof(ushort)) {
                return unchecked((ushort)((ushort)a & (ushort)b)) != 0;
            } else if(type == typeof(long)) {
                return (((long)a) & ((long)b)) != 0;
            } else if(type == typeof(ulong)) {
                return (((ulong)a) & ((ulong)b)) != 0;
            } else if(type == typeof(byte)) {
                return unchecked((byte)((byte)a & (byte)b)) != 0;
            } else if(type == typeof(sbyte)) {
                return unchecked((sbyte)((sbyte)a & (sbyte)b)) != 0;
            } else {
                throw new System.Exception("object.DoMaskCheck() Fail!! Type:" + type.FullName + " not support!!");
            }
        }
        public static void EnumDoNot(this object a) {
            var _type = a.GetType();
            var type = Enum.GetUnderlyingType(_type);
            if(type == typeof(int)) {
                a = ~(int)a;
            } else if(type == typeof(uint)) {
                a = ~(uint)a;
            } else if(type == typeof(short)) {
                a = unchecked((short)~(short)a);
            } else if(type == typeof(ushort)) {
                a = unchecked((ushort)~(ushort)a);
            } else if(type == typeof(long)) {
                a = ~(long)a;
            } else if(type == typeof(ulong)) {
                a = ~(ulong)a;
            } else if(type == typeof(byte)) {
                a = (byte)~(byte)a;
            } else if(type == typeof(sbyte)) {
                a = unchecked((sbyte)~(sbyte)a);
            } else {
                throw new System.Exception("object.DoNot() Fail!! Type:" + type.FullName + " not support!!");
            }
        }
        public static object Not(this object a) {
            var type = a.GetType();
            if(type == typeof(int)) {
                return ~(int)a;
            } else if(type == typeof(uint)) {
                return ~(uint)a;
            } else if(type == typeof(short)) {
                return unchecked((short)~(short)a);
            } else if(type == typeof(ushort)) {
                return unchecked((ushort)~(ushort)a);
            } else if(type == typeof(long)) {
                return ~(long)a;
            } else if(type == typeof(ulong)) {
                return ~(ulong)a;
            } else if(type == typeof(byte)) {
                return (byte)~(byte)a;
            } else if(type == typeof(sbyte)) {
                return unchecked((sbyte)~(sbyte)a);
            } else {
                throw new System.Exception("object.Not() Fail!! Type:" + type.FullName + " not support!!");
            }
        }
        public static object EnumNot(this object a) {
            var _type = a.GetType();
            var type = Enum.GetUnderlyingType(_type);
            if(type == typeof(int)) {
                return ~(int)a;
            } else if(type == typeof(uint)) {
                return ~(uint)a;
            } else if(type == typeof(short)) {
                return unchecked((short)~(short)a);
            } else if(type == typeof(ushort)) {
                return unchecked((ushort)~(ushort)a);
            } else if(type == typeof(long)) {
                return ~(long)a;
            } else if(type == typeof(ulong)) {
                return ~(ulong)a;
            } else if(type == typeof(byte)) {
                return (byte)~(byte)a;
            } else if(type == typeof(sbyte)) {
                return unchecked((sbyte)~(sbyte)a);
            } else {
                throw new System.Exception("object.Not() Fail!! Type:" + type.FullName + " not support!!");
            }
        }

    }
}
namespace UCL.Core.ObjectReflectionExtension {
    public static partial class ExtensionMethods {
        /// <summary>
        /// Invoke the target function of iTarget
        /// </summary>
        /// <param name="iTarget">Target to invoke member function</param>
        /// <param name="iFunctionName">FuncionName of function to invoke</param>
        /// <param name="iParameters">params that pass to target function</param>
        /// <returns></returns>
        public static object Invoke(this object iTarget, string iFunctionName, params object[] iParameters) {
            if (iTarget == null) return null;
            return iTarget.InvokeFunc(iFunctionName, iParameters);
        }
        /// <summary>
        /// Invoke the target function of iTarget
        /// </summary>
        /// <param name="iTarget">Target to invoke member function</param>
        /// <param name="iFunctionName">FuncionName of function to invoke</param>
        /// <param name="iParameters">params that pass to target function</param>
        /// <returns></returns>
        public static object InvokeFunc(this object iTarget, string iFunctionName, object[] iParameters) {
            if (iTarget == null) return null;

            Type aType = iTarget.GetType();
            MethodInfo aMethod = null;
            bool aHasParams = !iParameters.IsNullOrEmpty();
            if (aHasParams)//Method with iParameters
            {
                Type[] aParameterTypes = new Type[iParameters.Length];
                for (int i = 0; i < iParameters.Length; i++)
                {
                    aParameterTypes[i] = iParameters[i].GetType();
                }
                aMethod = aType.GetMethod(iFunctionName, aParameterTypes);
            }
            else//Method without iParameters
            {
                aMethod = aType.GetMethod(iFunctionName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            }
            
            if (aMethod == null) {
                if (!aHasParams)//might be accessor
                {
                    
                    PropertyInfo aPropInfo = aType.GetProperty(iFunctionName);
                    if (aPropInfo == null)
                    { // not accessor!!
                        Debug.LogError("InvokeFunc Fail!!FunctionName:" + iFunctionName + " not exist in Type:" + aType.Name);
                        return null;
                    }
                    MethodInfo[] aAccessors = aPropInfo.GetAccessors();

                    for (int i = 0; i < aAccessors.Length; i++)
                    {
                        MethodInfo aAccessor = aAccessors[i];
                        // Determine if this is the property getter or setter.
                        if (aAccessor.ReturnType == typeof(void))//setter
                        {

                        }
                        else//getter
                        {
                            return aAccessor.Invoke(iTarget, null);
                        }
                    }
                }
                Debug.LogError("InvokeFunc Fail!!FunctionName:" + iFunctionName + " not exist in Type:" + aType.Name);
                return null;
            }
            return aMethod.Invoke(iTarget, iParameters);
        }
    }
}
namespace UCL.Core.ObjectMemoryExtension
{
    public static partial class ExtensionMethods
    {
        /// <summary>
        /// DeepClone the target
        /// </summary>
        /// <typeparam name="T"> type of target</typeparam>
        /// <param name="iTarget">target to clone</param>
        /// <returns>Clone of target</returns>
        public static T DeepClone<T>(this T iTarget)
        {

            if (!typeof(T).IsSerializable)
            {
                Debug.LogError("DeepClone fail!! Target not Serializable!!");
                return default(T);
            }

            if (iTarget != null)
            {
                using (MemoryStream aStream = new MemoryStream())
                {
                    var aFormatter = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                    aFormatter.Serialize(aStream, iTarget);
                    aStream.Seek(0, SeekOrigin.Begin);
                    T aClonedSource = (T)aFormatter.Deserialize(aStream);
                    return aClonedSource;
                }
            }

            return default(T);

        }
    }
}
public static partial class ObjectExtensionMethods
{
    public static byte[] ToByteArray(this object obj) {
        return UCL.Core.MarshalLib.Lib.ToByteArray(obj);
    }
    public static byte[] ToByteArray(this Array iArr) {
        int aArrLength = iArr.Length * Marshal.SizeOf(iArr.GetType().GetElementType());
        var aResult = new byte[aArrLength];
        Buffer.BlockCopy(iArr, 0, aResult, 0, aArrLength);
        return aResult;
    }
    public static T[] ToArray<T>(this byte[] iArr)
    {
        int aArrLength = iArr.Length / Marshal.SizeOf(iArr.GetType().GetElementType());
        var aResult = new T[aArrLength];
        Buffer.BlockCopy(iArr, 0, aResult, 0, iArr.Length);
        return aResult;
    }
    public static object GetMember(this object obj, string name, BindingFlags flags) {
        if(obj == null) return null;
        Type type = obj.GetType();
        var info = type.GetField(name, flags);
        if(info == null) return null;
        return info.GetValue(obj);
    }
    public static object GetMember(this object obj, string name) {
        if(obj == null) return null;
        Type type = obj.GetType();
        var info = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        if(info == null) return null;
        return info.GetValue(obj);
    }
    public static T GetMember<T>(this object obj, string name) {
        return (T)obj.GetMember(name);
    }
    /*
    public static string UCL_ToBitString(this long val, string seperator = "_") {
        long mask = 1;
        StringBuilder sb = new StringBuilder();
        for(int i = 0; i < 64; i++) {
            if((val & mask) != 0) {
                sb.Append("1");
            } else {
                sb.Append("0");
            }
            mask <<= 1;
        }
        return sb.ToString();
    }
    */
    public static string UCL_ToBitString(this object obj, string seperator = "_") {
        var arr = obj.ToByteArray();
        StringBuilder sb = new StringBuilder();
        for(int i = arr.Length-1; i >= 0 ; i--) {
            byte mask = 0b1000_0000;
            byte val = arr[i];
            for(int j = 0; j < 8; j++) {
                if((val & mask) != 0) {
                    sb.Append("1");
                } else {
                    sb.Append("0");
                }
                mask >>= 1;
            }
            if(i > 0)sb.Append(seperator);
        }
        return sb.ToString();
    }
    public static string UCL_ToString(this object iObj, int iSpace = 0) {
        try {
            if(iSpace > 10) return string.Empty;
            if(iObj == null) {
                if(iSpace == 0) return "UCL_ToString Error!! obj == null";
                return string.Empty;
            }
            Type type = iObj.GetType();
            if(type.IsPrimitive || !type.IsStructOrClass() || iObj is Enum
                || iObj is Vector4 || iObj is Vector3 || iObj is Vector2
                || iObj is Vector3Int || iObj is Vector2Int) {
                if(iSpace == 0) return "(" + type.Name + ") : " + iObj.ToString();
                return iObj.ToString();
            }
            if(iObj is string) {
                if(iSpace == 0) return "(" + type.Name + ") : " + (string)iObj;
                return (string)iObj;
            }

            string aSpaceStr = string.Empty;
            StringBuilder aBuilder = new StringBuilder();
            if(iSpace > 0) {
                aBuilder.Append("\n");
                aSpaceStr = new string('\t', iSpace);
            }
            IEnumerable aEnum = iObj as IEnumerable;
            if(aEnum != null) {
                if(iObj is UCL.Core.JsonLib.JsonData)
                {
                    aBuilder.Append(aSpaceStr + "(JsonData):");
                    aBuilder.Append(((UCL.Core.JsonLib.JsonData)iObj).ToJsonBeautify());
                    return aBuilder.ToString();
                }
                var iDic = iObj as IDictionary;
                if(iDic != null) {
                    aBuilder.Append(aSpaceStr + "(" + type.Name + ")" + " : [");
                    string arrStr = string.Empty;
                    foreach(var key in iDic.Keys) {
                        aBuilder.Append("(" + key.UCL_ToString(iSpace + 1) + " , " + iDic[key].UCL_ToString(iSpace + 1) + "), ");
                    }
                    aBuilder.RemoveLast();
                    aBuilder.RemoveLast();
                    aBuilder.Append("]");
                } else {
                    aBuilder.Append(aSpaceStr + "(" + type.Name + ")" + " : [");
                    string arrStr = string.Empty;
                    bool aFlag = false;
                    foreach(var val in aEnum) {
                        aFlag = true;
                        aBuilder.Append(val.UCL_ToString(iSpace + 1) + ", ");
                    }
                    if (aFlag)
                    {
                        aBuilder.RemoveLast();
                        aBuilder.RemoveLast();
                    }
                    aBuilder.Append("]");
                }
                return aBuilder.ToString();
            }

            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if(fields.Length > 0) {
                if(iSpace == 0) {
                    aBuilder.AppendLine(aSpaceStr + type.Name);//",fields.Length:" + fields.Length+
                }
                foreach(var field in fields) {
                    var value = field.GetValue(iObj);
                    Type f_type = field.FieldType;
                    aBuilder.AppendLine(aSpaceStr + "(" + f_type.Name + ")" + field.Name + " : " + value.UCL_ToString(iSpace + 1));
                }
            } else {
                return iObj.ToString();
            }

            return aBuilder.ToString();
        } catch (Exception iE){
            Debug.LogException(iE);
            return "UCL_ToString Exception:" + iE.ToString();
        }
    }

    public static string UCL_GetShortName(this object iObj, string iDefault = "")
    {
        if (iObj == null) return iDefault;
        if(iObj is UCL.Core.UCLI_ShortName)
        {
            return (iObj as UCL.Core.UCLI_ShortName).GetShortName();
        }
        if(iObj is Enum)
        {
            string aLocalizeKey = iObj.GetType().Name + "_" + iObj.ToString();
            if (UCL.Core.LocalizeLib.UCL_LocalizeManager.ContainsKey(aLocalizeKey))
            {
                return UCL.Core.LocalizeLib.UCL_LocalizeManager.Get(aLocalizeKey);
            }
        }
        return iDefault;
    }
}

//Misc
public static partial class ColorExtensionMethods
{
    public static bool IsBetween(this Color col, Color min, Color max) {
        if(min.r > col.r || col.r > max.r) {
            return false;
        }
        if(min.g > col.g || col.g > max.g) {
            return false;
        }
        if(min.b > col.b || col.b > max.b) {
            return false;
        }
        if(min.a > col.a || col.a > max.a) {
            return false;
        }
        return true;
    }
}