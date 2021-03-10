using System.Collections;
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
    /// <param name="list"></param>
    /// <param name="at"></param>
    /// <returns></returns>
    public static T Get<T>(this IList<T> list, int at) {
        if(list == null || list.Count == 0 || at < 0 || at >= list.Count) return default;
        return list[at];
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
    /// <param name="list">target to clone</param>
    /// <returns></returns>
    public static List<T> Clone<T>(this List<T> list) {
        if(list == null) return null;
        return new List<T>(list);
    }
}

