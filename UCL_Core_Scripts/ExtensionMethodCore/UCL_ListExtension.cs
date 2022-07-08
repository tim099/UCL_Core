using System.Collections;
using System.Collections.Generic;
using UnityEngine;



public static partial class ListExtensionMethods {

    #region HashSet
    public static bool IsNullOrEmpty<T>(this HashSet<T> iHashSet)
    {
        if (iHashSet == null || iHashSet.Count == 0) return true;
        return false;
    }

    #endregion

    #region IEnumerable
    public static string ConcatString<T>(this IEnumerable<T> iList, System.Func<T, string> iFunc, string iSeperator = ", ")
    {
        System.Text.StringBuilder aSB = new System.Text.StringBuilder();
        bool aIsFirst = true;
        foreach (var aT in iList)
        {
            if (aIsFirst) aIsFirst = false;
            else aSB.Append(iSeperator);
            aSB.Append(iFunc(aT));

        }
        return aSB.ToString();
    }
    public static string ConcatString<T>(this IEnumerable<T> iList, string iSeperator = ", ")
    {
        System.Text.StringBuilder aSB = new System.Text.StringBuilder();
        bool aIsFirst = true;
        foreach (var aT in iList)
        {
            if (aIsFirst) aIsFirst = false;
            else aSB.Append(iSeperator);
            aSB.Append(aT.ToString());

        }
        return aSB.ToString();
    }
    #endregion

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
    public static List<T> ConvertType<T>(this IList iList)
    {
        var aList = new List<T>();
        if (iList == null) return aList;
        
        foreach(var aItem in iList)
        {
            T aItem1 = (T)aItem;
            aList.Add(aItem1);
        }
        return aList;
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
    /// <summary>
    /// Remove specific element from list
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iList"></param>
    /// <param name="iFilterFunc">return true if the element need to be remove from list</param>
    /// <returns></returns>
    public static List<T> FilterList<T>(this List<T> iList, System.Func<T,bool> iFilterFunc)
    {
        for (int i = iList.Count - 1; i >= 0; i--)
        {
            if (iFilterFunc(iList[i]))
            {
                iList.RemoveAt(i);
            }
        }
        return iList;
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
    /// <summary>
    /// return SubList of iList
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iList"></param>
    /// <param name="iStartIndex">where the SubList start(0 will start at the first element)</param>
    /// <param name="iLength">Length of SubList</param>
    /// <returns></returns>
    public static List<T> SubList<T>(this List<T> iList, int iStartIndex, int iLength)
    {
        List<T> aList = new List<T>();
        if (iList.IsNullOrEmpty()) return aList;
        int aLastIndex = iStartIndex + iLength;
        if (aLastIndex > iList.Count) aLastIndex = iList.Count;
        for(int i = iStartIndex; i < aLastIndex; i++)
        {
            aList.Add(iList[i]);
        }
        return aList;
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
    public static bool IsNullOrEmpty<T>(this IList<T> iList) {
        if(iList == null || iList.Count == 0) return true;
        return false;
    }
    public static bool IsNullOrEmpty(this IDictionary iDic)
    {
        if (iDic == null || iDic.Count == 0) return true;
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
    /// Return the index of target in Array
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iArray">Target Array</param>
    /// <param name="iTarget">Target to find index</param>
    /// <returns>return the index if iTarget, return 0 if not found Target in list</returns>
    public static int GetArrayIndex<T>(this System.Array iArray, T iTarget)
    {
        if (iArray.Length == 0) return 0;
        for (int i = 0; i < iArray.Length; i++)
        {
            if (iArray.GetValue(i).Equals(iTarget)) return i;
        }
        return 0;
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
        if (iList.IsNullOrEmpty())
        {
            return iList;
        }
        int aHalfCount = iList.Count / 2;
        for (int i = 0; i < aHalfCount; i++)
        {
            T aTmp = iList[i];
            iList[i] = iList[iList.Count - i - 1];
            iList[iList.Count - i - 1] = aTmp;
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

public static partial class DictionaryExtensionMethods
{
    public static string ConcatString<T,T2>(this IDictionary<T, T2> iDic, System.Func<T, T2, string> iFunc, string iSeperator = ", ")
    {
        System.Text.StringBuilder aSB = new System.Text.StringBuilder();
        bool aIsFirst = true;
        foreach (var aKey in iDic.Keys)
        {
            if (aIsFirst) aIsFirst = false;
            else aSB.Append(iSeperator);
            aSB.Append(iFunc(aKey, iDic[aKey]));

        }
        return aSB.ToString();
    }
    public static string ConcatString<T, T2>(this IDictionary<T, T2> iDic, string iSeperator = ", ")
    {
        return iDic.ConcatString((iKey, iEnement) => string.Format("({0}:{1})", iKey.ToString() , iEnement.ToString()), iSeperator);
    }
}

