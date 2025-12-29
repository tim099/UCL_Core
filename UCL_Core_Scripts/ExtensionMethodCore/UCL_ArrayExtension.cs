using System;
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
    public static void MoveRight<T>(this T[,] array, int y)
    {
        int width = array.GetLength(0);
        int height = array.GetLength(1);

        var sym = array[width - 1, y]; // 先存最後一個元素
        for (int x = width - 1; x > 0; x--) // 從右往左移
        {
            array[x, y] = array[x - 1, y];
        }
        array[0, y] = sym; // 把原本最後的元素放到最前面
    }
    public static void MoveLeft<T>(this T[,] array, int y)
    {
        int width = array.GetLength(0);
        int height = array.GetLength(1);

        var sym = array[0, y]; // 先存最左邊的元素
        for (int x = 0; x < width - 1; x++) // 從左往右移
        {
            array[x, y] = array[x + 1, y];
        }
        array[width - 1, y] = sym; // 把原本最左的元素放到最右邊
    }
    public static void MoveUp<T>(this T[,] array, int x)
    {
        int width = array.GetLength(0);
        int height = array.GetLength(1);

        var sym = array[x, 0];
        for (int y = 1; y < height; y++)
        {
            array[x, y - 1] = array[x, y];
        }
        array[x, height - 1] = sym;
    }
    public static void MoveDown<T>(this T[,] array, int x)
    {
        int width = array.GetLength(0);
        int height = array.GetLength(1);

        var sym = array[x, height - 1]; // 先存最後一個元素
        for (int y = height - 1; y > 0; y--) // 從底往上移
        {
            array[x, y] = array[x, y - 1];
        }
        array[x, 0] = sym; // 把原本最後的元素放到最上面
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