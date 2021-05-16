﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;



public static partial class ListExtensionMethods {
    /// <summary>
    /// this function don't check the range of a,b !!so use carefully
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="list"></param>
    /// <param name="a"></param>
    /// <param name="b"></param>
    public static void Swap<T>(this IList<T> list, int a, int b) {
        var tmp = list[a];
        list[a] = list[b];
        list[b] = tmp;
    }
    public static T FirstElement<T>(this IList<T> list) {
        if(list.Count == 0) return default;
        return list[0];
    }
    public static T LastElement<T>(this IList<T> list) {
        if(list.Count == 0) return default;
        return list[list.Count-1];
    }
    public static List<T> Append<T>(this List<T> iList, IEnumerable<T> iTarget)
    {
        if (iList == null) return null;
        foreach(var aElement in iTarget)
        {
            iList.Add(aElement);
        }
        return iList;
    }
    public static bool IsNullOrEmpty<T>(this IList<T> list) {
        if(list == null || list.Count == 0) return true;
        return false;
    }
    /// <summary>
    /// Return the index of target in iList
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iList">Target list</param>
    /// <param name="iTarget">Target to find index</param>
    /// <returns></returns>
    public static int GetIndex<T>(this IList<T> iList,T iTarget)
    {
        if (iList.Count == 0) return 0;
        for(int i = 0; i < iList.Count; i++)
        {
            if (iList[i].Equals(iTarget)) return i;
        }
        return 0;
    }
    /// <summary>
    /// Get element inside list
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
    /// Get element inside list
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
    /// Remove first element of list
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="list">target list</param>
    public static void RemoveFirst<T>(this IList<T> list) {
        if(list == null || list.Count == 0) return;
        list.RemoveAt(0);
    }
    /// <summary>
    /// Remove last element of list
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="list">target list</param>
    public static void RemoveLast<T>(this IList<T> list) {
        if(list == null || list.Count == 0) return;
        list.RemoveAt(list.Count-1);
    }
    /// <summary>
    /// Clone the target list
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iList">target to clone</param>
    /// <returns></returns>
    public static List<T> Clone<T>(this List<T> iList) {
        if(iList == null) return null;
        return new List<T>(iList);
    }
}

