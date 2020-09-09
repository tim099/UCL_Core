using System.Collections;
using System.Collections.Generic;
using UnityEngine;



public static partial class VectorExtensionMethods {
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
}

