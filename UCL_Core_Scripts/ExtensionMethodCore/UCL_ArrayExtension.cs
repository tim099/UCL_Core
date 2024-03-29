﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

public static partial class ArrayExtensionMethods {
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
    public static T[,] CloneArray<T>(this T[,] iArray)
    {
        int aWidth = iArray.GetLength(0);
        int aHeight = iArray.GetLength(1);
        T[,] aNewArr = new T[aWidth, aHeight];
        for (int aX = 0; aX < aWidth; aX++)
        {
            for (int aY = 0; aY < aHeight; aY++)
            {
                aNewArr[aX, aY] = iArray[aX, aY];
            }
        }
        return aNewArr;
    }
    /// <summary>
    /// Set all element in iArray to iVal
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iArray"></param>
    /// <param name="iVal"></param>
    /// <returns></returns>
    public static T[,] InitArray<T>(this T[,] iArray, T iVal)
    {
        int aWidth = iArray.GetLength(0);
        int aHeight = iArray.GetLength(1);
        for (int aX = 0; aX < aWidth; aX++)
        {
            for (int aY = 0; aY < aHeight; aY++)
            {
                iArray[aX, aY] = iVal;
            }
        }
        return iArray;
    }
    /// <summary>
    /// Convert byte array into structure
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iByteArray"></param>
    /// <returns></returns>
    public static T ToStructure<T>(this byte[] iByteArray) {
        return UCL.Core.MarshalLib.Lib.ToStructure<T>(iByteArray);
    }
    public static object ToStructure(this byte[] iByteArray, Type iType)
    {
        return UCL.Core.MarshalLib.Lib.ToStructure(iByteArray, iType);
    }
    public static bool IsNullOrEmpty<T>(this T[] arr) {
        if(arr == null || arr.Length == 0) return true;
        return false;
    }
    /// <summary>
    /// convert bytes array to float array
    /// </summary>
    /// <param name="iArray"></param>
    /// <returns></returns>
    public static float[] ToFloatArray(this byte[] iArray)
    {
        float[] aFloatArr = new float[iArray.Length / 4];
        if (BitConverter.IsLittleEndian)
        {
            for (int i = 0; i < aFloatArr.Length; i++)
            {
                Array.Reverse(iArray, i * 4, 4);
                aFloatArr[i] = BitConverter.ToSingle(iArray, i * 4) / 0x80000000;
            }
        }
        else
        {
            for (int i = 0; i < aFloatArr.Length; i++)
            {
                aFloatArr[i] = BitConverter.ToSingle(iArray, i * 4) / 0x80000000;
            }
        }

        return aFloatArr;
    }

    /// <summary>
    /// Convert byte array into Hexadecimal string
    /// </summary>
    /// <param name="iBytes"></param>
    /// <returns></returns>
    public static string ToHexString(this byte[] iBytes)
    {
        return UCL.Core.MarshalLib.Lib.ByteArrayToHexString(iBytes);
    }
    /// <summary>
    /// Convert Hexadecimal string into byte array 
    /// </summary>
    /// <param name="iHexString"></param>
    /// <returns></returns>
    public static byte[] HexStringToByteArray(this string iHexString)
    {
        return UCL.Core.MarshalLib.Lib.HexStringToByteArray(iHexString);
    }



    //public static List<T> ToList<T>(this T[] arr) {
    //    List<T> list = new List<T>();
    //    for(int i = 0, len = arr.Length; i < len; i++) {
    //        list.Add(arr[i]);
    //    }
    //    return list;
    //}
}