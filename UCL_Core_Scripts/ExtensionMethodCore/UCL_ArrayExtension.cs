using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

public static partial class VectorExtensionMethods {
    /*
    /// <summary>
    /// this function don't check the range of a,b !!so use carefully
    /// </summary>
    /// <param name="arr"></param>
    /// <param name="a"></param>
    /// <param name="b"></param>
    public static void Swap(this System.Array arr, int a,int b) {
        var tmp = arr.GetValue(a);
        arr.SetValue(arr.GetValue(b), a);
        arr.SetValue(arr.GetValue(a), b);
    }
    */
    public static T ToStructure<T>(this byte[] buffer) {
        return UCL.Core.MarshalLib.Lib.ToStructure<T>(buffer);
    }
    public static bool IsNullOrEmpty<T>(this T[] arr) {
        if(arr == null || arr.Length == 0) return true;
        return false;
    }
    //public static List<T> ToList<T>(this T[] arr) {
    //    List<T> list = new List<T>();
    //    for(int i = 0, len = arr.Length; i < len; i++) {
    //        list.Add(arr[i]);
    //    }
    //    return list;
    //}
}