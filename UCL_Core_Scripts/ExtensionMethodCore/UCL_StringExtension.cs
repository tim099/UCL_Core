using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public static partial class StringExtensionMethods {
    public const string LineSeparator = "\r\n|\r|\n";
    #region String
    public static string RemoveLast(this string str) {
        int len = str.Length;
        if(len == 0) return string.Empty;
        return str.Remove(len - 1);
    }
    public static string[] SplitByLine(this string str) {
        return System.Text.RegularExpressions.Regex.Split(str, LineSeparator);
    }
    #endregion

    #region StringBuilder
    public static void RemoveLast(this StringBuilder builder) {
        int len = builder.Length;
        if(len == 0) return;
        builder.Remove(len - 1, 1);
    }

    #endregion
}
