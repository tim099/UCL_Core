using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
namespace UCL.Core.ObjectOperatorExtension {
    public static partial class ExtensionMethods {
        public static bool IsNumber(this object a) {
            return a is sbyte
                    || a is byte
                    || a is short
                    || a is ushort
                    || a is int
                    || a is uint
                    || a is long
                    || a is ulong
                    || a is float
                    || a is double
                    || a is decimal;
        }
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
        public static object Invoke(this object obj, string function_name, params object[] parameters) {
            return obj.InvokeFunc(function_name, parameters);
        }
        public static object InvokeFunc(this object obj, string function_name, object[] parameters) {
            Type type = obj.GetType();
            Type[] types = new Type[parameters.Length];
            for(int i = 0; i < parameters.Length; i++) {
                types[i] = parameters[i].GetType();
            }
            var method = type.GetMethod(function_name, types);
            if(method == null) {
                Debug.LogError("InvokeFunc Fail!!function_name:" + function_name + " not exist in type:" + type.Name);
                return null;
            }
            return method.Invoke(obj, parameters);
        }
    }
}
public static partial class ExtensionMethods {
    public static byte[] ToByteArray(this object obj) {
        return UCL.Core.MarshalLib.Lib.ToByteArray(obj);
    }
    public static object GetMember(this object obj, string name) {
        if(obj == null) return null;
        Type type = obj.GetType();
        var info = type.GetField(name);
        if(info == null) return null;
        return info.GetValue(obj);
    }
    public static T GetMember<T>(this object obj, string name) {
        return (T)obj.GetMember(name);
    }
    public static string UCL_ToString(this object obj, int space = 0) {
        if(obj == null) {
            if(space == 0) return "UCL_ToString Error!! obj == null";
            return string.Empty;
        }
        Type type = obj.GetType();
        if(type.IsPrimitive || !type.IsStructOrClass() || obj is Enum) {
            if(space == 0) return "(" + type.Name + ") : " + obj.ToString();
            return obj.ToString();
        }
        if(obj is string) {
            if(space == 0) return "(" + type.Name + ") : " + (string)obj;
            return (string)obj;
        }

        string space_str = string.Empty;
        StringBuilder builder = new StringBuilder();
        if(space > 0) {
            builder.Append("\n");
            space_str = new string('\t', space);
        }
        IEnumerable ienum = obj as IEnumerable;
        if(ienum != null) {
            var dic = obj as IDictionary;
            if(dic != null) {
                builder.Append(space_str + "(" + type.Name + ")" + " : [");
                string arrStr = string.Empty;
                foreach(var key in dic.Keys) {
                    builder.Append("("+key.UCL_ToString(space + 1) + " , " + dic[key].UCL_ToString(space + 1) + "), ");
                }
                builder.RemoveLast();
                builder.RemoveLast();
                builder.Append("]");
            } else {
                builder.Append(space_str + "(" + type.Name + ")" + " : [");
                string arrStr = string.Empty;
                foreach(var val in ienum) {
                    builder.Append(val.UCL_ToString(space + 1) + ",");
                }
                builder.RemoveLast();
                builder.Append("]");
            }
            return builder.ToString();
        }

        FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if(fields.Length > 0) {
            if(space == 0) {
                builder.AppendLine(space_str + type.Name);//",fields.Length:" + fields.Length+
            }
            foreach(var field in fields) {
                var value = field.GetValue(obj);
                Type f_type = field.FieldType;
                builder.AppendLine(space_str + "(" + f_type.Name + ")" + field.Name + " : " + value.UCL_ToString(space + 1));
            }
        } else {
            return obj.ToString();
        }

        return builder.ToString();
    }
}