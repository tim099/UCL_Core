using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static partial class ExtensionMethods {
    public static void DoOR(this object a, object b) {
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
    public static void DoAND(this object a, object b) {
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
    public static bool DoMaskCheck(this object a, object b) {
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
    public static void DoNot(this object a) {
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
