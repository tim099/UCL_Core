﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;



public static partial class ListExtensionMethods {
    /// <summary>
    /// return the first Element in iICollection
    /// </summary>
    /// <param name="iICollection"></param>
    /// <returns></returns>
    public static object FirstElementInCollection(this ICollection iICollection)
    {
        if (iICollection == null) return null;
        foreach(var aItem in iICollection)
        {
            return aItem;
        }
        return null;
    }
    /// <summary>
    /// return the last Element in ICollection
    /// </summary>
    /// <param name="iICollection"></param>
    /// <returns></returns>
    public static object LastElementInCollection(this ICollection iICollection)
    {
        if (iICollection == null) return null;
        int aAt = 0;
        foreach (var aItem in iICollection)
        {
            if(++aAt == iICollection.Count)//Last Item
            {
                return aItem;
            }
        }
        return null;
    }

    /// <summary>
    /// this function don't check the range of a,b !!so use carefully
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iList"></param>
    /// <param name="a"></param>
    /// <param name="b"></param>
    public static void Swap<T>(this IList<T> iList, int a, int b) {
        var tmp = iList[a];
        iList[a] = iList[b];
        iList[b] = tmp;
    }
    /// <summary>
    /// Swap element by index
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iList"></param>
    /// <returns></returns>
    public static IList<T> SwapElement<T>(this IList<T> iList, int iIndexA, int iIndexB)
    {
        if (iList.IsNullOrEmpty()) return default;
        T aTmp = iList[iIndexA];
        iList[iIndexA] = iList[iIndexB];
        iList[iIndexB] = aTmp;
        return iList;
    }
    /// <summary>
    /// return the first element of list
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iList"></param>
    /// <returns></returns>
    public static T FirstElement<T>(this IList<T> iList) {
        if(iList.Count == 0) return default;
        return iList[0];
    }
    /// <summary>
    /// return the last element of list
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iList"></param>
    /// <returns></returns>
    public static T LastElement<T>(this IList<T> iList) {
        if(iList.Count == 0) return default;
        return iList[iList.Count-1];
    }
    public static List<T> Append<T>(this List<T> iList, IEnumerable<T> iTarget)
    {
        if (iList == null) return null;
        if (iTarget == null) return iList;

        foreach(var aElement in iTarget)
        {
            iList.Add(aElement);
        }
        return iList;
    }
    public static string ConcatString(this IList<string> iList, string iSeperator = ", ")
    {
        System.Text.StringBuilder aSB = new System.Text.StringBuilder();
        foreach (var aStr in iList)
        {
            aSB.Append(aStr).Append(iSeperator);
        }
        return aSB.ToString();
    }
    public static string ConcatString<T>(this IList<T> iList, System.Func<T, string> iFunc, string iSeperator = ", ")
    {
        System.Text.StringBuilder aSB = new System.Text.StringBuilder();
        foreach(var aT in iList)
        {
            aSB.Append(iFunc(aT)).Append(iSeperator);
        }
        return aSB.ToString();
    }
    public static bool IsNullOrEmpty<T>(this IList<T> iList) {
        if(iList == null || iList.Count == 0) return true;
        return false;
    }
    /// <summary>
    /// Return the index of target in iList
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iList">Target iList</param>
    /// <param name="iTarget">Target to find index</param>
    /// <returns>return the index if iTarget, return -1 if not found Target in list</returns>
    public static int GetIndex<T>(this IList<T> iList,T iTarget)
    {
        if (iList.Count == 0) return -1;
        for(int i = 0; i < iList.Count; i++)
        {
            if (iList[i].Equals(iTarget)) return i;
        }
        return -1;
    }
    /// <summary>
    /// Get element inside iList
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iList"></param>
    /// <param name="iAt"></param>
    /// <returns></returns>
    public static T Get<T>(this IList<T> iList, int iAt) {
        if(iList == null || iList.Count == 0 || iAt < 0 || iAt >= iList.Count) return default;
        return iList[iAt];
    }
    /// <summary>
    /// Get element inside iList
    /// if At < 0 then get the first element
    /// if At >= iList.Count,then get the last element
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iList"></param>
    /// <param name="iAt"></param>
    /// <returns></returns>
    public static T SmartGet<T>(this IList<T> iList, int iAt)
    {
        if (iList == null || iList.Count == 0) return default;
        if (iAt < 0) iAt = 0;
        else if (iAt >= iList.Count) iAt = iList.Count - 1;
        return iList[iAt];
    }
    /// <summary>
    /// Remove first element of iList
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iList">target iList</param>
    public static void RemoveFirst<T>(this IList<T> iList) {
        if(iList == null || iList.Count == 0) return;
        iList.RemoveAt(0);
    }
    /// <summary>
    /// Reverse order of element in iList(Same as Linq.Reverse)
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iList"></param>
    public static IList<T> Reverse<T>(this IList<T> iList)
    {
        if (!iList.IsNullOrEmpty())
        {
            for (int i = 0; i < iList.Count / 2; i++)
            {
                T aTmp = iList[i];
                iList[i] = iList[iList.Count - i - 1];
                iList[iList.Count - i - 1] = aTmp;
            }
        }

        return iList;
    }
    /// <summary>
    /// Remove last element of iList
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iList">target iList</param>
    public static void RemoveLast<T>(this IList<T> iList) {
        if(iList == null || iList.Count == 0) return;
        iList.RemoveAt(iList.Count-1);
    }
    /// <summary>
    /// Clone the target iList
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iList">target to clone</param>
    /// <returns></returns>
    public static List<T> Clone<T>(this List<T> iList) {
        if(iList == null) return null;
        return new List<T>(iList);
    }
}

