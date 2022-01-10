using System;
using System.Text;
using UnityEngine;

public static partial class StringExtensionMethods {
    public const string LineSeparator = "\r\n|\r|\n";
    public static string[] LineSeparatorArr {
        get
        {
            if(s_LineSeparatorArr == null)
            {
                s_LineSeparatorArr = new string[] { "\r\n", "\r", "\n", @"\n" };
            }
            return s_LineSeparatorArr;
        }       
    }
    public static string[] s_LineSeparatorArr = null;
    #region String
    /// <summary>
    /// Append RichText Color to the string
    /// </summary>
    /// <param name="iStr"></param>
    /// <param name="iCol"></param>
    /// <returns></returns>
    public static string RichTextColor(this string iStr, string iCol = "FFFFFF")
    {
        return string.Format("<color=#{0}>{1}</color>", iCol, iStr);
    }
    /// <summary>
    /// Append RichText Color to the string
    /// </summary>
    /// <param name="iStr"></param>
    /// <param name="iCol"></param>
    /// <returns></returns>
    public static string RichTextColor(this string iStr, Color iCol)
    {
        return string.Format("<color=#{0}>{1}</color>", ColorUtility.ToHtmlStringRGB(iCol), iStr);
    }
    public static string RemoveLast(this string str) {
        int len = str.Length;
        if(len == 0) return string.Empty;
        return str.Remove(len - 1);
    }
    public static string[] SplitByLine(this string iStr) {
        return System.Text.RegularExpressions.Regex.Split(iStr, LineSeparator);
    }
    public static string ConvertLineSeparator(this string iStr, string[] iLineSeparatorArr = null)
    {
        if (string.IsNullOrEmpty(iStr)) return iStr;
        if(iLineSeparatorArr == null)
        {
            iLineSeparatorArr = LineSeparatorArr;
        }
        
        var aLines = iStr.Split(iLineSeparatorArr, StringSplitOptions.None);
        if (aLines.Length == 0)
        {
            return string.Empty;
        }
        else if (aLines.Length == 1)
        {
            return aLines[0];
        }
        else
        {
            StringBuilder aSB = new StringBuilder();
            for(int i = 0; i < aLines.Length - 1; i++)
            {
                aSB.AppendLine(aLines[i]);
            }
            aSB.Append(aLines[aLines.Length - 1]);
            return aSB.ToString();
        }
    }
    #endregion

    #region StringBuilder
    public static void RemoveLast(this StringBuilder iBuilder) {
        if(iBuilder.Length == 0) return;
        iBuilder.Remove(iBuilder.Length - 1, 1);
    }

    #endregion
}
namespace UCL.Core.StringExtensionMethods
{
    public static partial class ExtraStringExtensionMethods
    {
        public static void CopyToClipboard(this string iStr)
        {
            GUIUtility.systemCopyBuffer = iStr;
        }
    }
}
//internal static string Clipboard
//{//https://flystone.tistory.com/138
//    get
//    {
//        TextEditor _textEditor = new TextEditor();
//        _textEditor.Paste();
//        return _textEditor.text;
//    }
//    set
//    {
//        TextEditor _textEditor = new TextEditor
//        { text = value };

//        _textEditor.OnFocus();
//        _textEditor.Copy();
//    }
//}