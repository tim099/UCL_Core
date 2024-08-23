using System;
using System.Text;
using UnityEngine;
using System.Text.RegularExpressions;
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

    public static string ConvertToMD5(this string iStr)
    {
        using (var aCryptoMD5 = System.Security.Cryptography.MD5.Create())
        {
            var aBytes = Encoding.UTF8.GetBytes(iStr);
            var aHash = aCryptoMD5.ComputeHash(aBytes);

            var aMd5Str = BitConverter.ToString(aHash).Replace("-", string.Empty).ToUpper();

            return aMd5Str;
        }
    }
    /// <summary>
    /// Append RichText Color to the string
    /// </summary>
    /// <param name="iStr"></param>
    /// <param name="iCol"></param>
    /// <returns></returns>
    public static string RichTextColor(this string iStr, Color iCol)
    {
        return $"<color=#{ColorUtility.ToHtmlStringRGB(iCol)}>{iStr}</color>";
    }
    /// <summary>
    /// Append RichText Color to the string
    /// </summary>
    /// <param name="iStr"></param>
    /// <param name="iCol"></param>
    /// <returns></returns>
    public static string RichTextColor(this string iStr, string iCol = "FFFFFF")
    {
        return $"<color=#{iCol}>{iStr}</color>";
    }
    static Regex s_ColorRegex = null;
    /// <summary>
    /// Return iStr if string length <= iMaxLength
    /// otherwise return Substring of iMaxLength + iEndWith
    /// etc. "HelloWorld" with MaxLength 6 will return "HelloW..."
    /// this Version can detect rich text color(<color=#......>*.+?</color>)
    /// </summary>
    /// <param name="iStr"></param>
    /// <param name="iMaxLength"></param>
    /// <param name="iEndWith"></param>
    /// <returns></returns>
    public static string CutToMaxLengthRichText(this string iStr, int iMaxLength, string iEndWith = "...")
    {
        if (s_ColorRegex == null)
        {
            s_ColorRegex = new Regex("<color=#......>*.+?</color>");
        }
        var aMatches = s_ColorRegex.Matches(iStr);
        if (!aMatches.IsNullOrEmpty())
        {
            System.Text.StringBuilder aSB = new System.Text.StringBuilder();
            int aCurAt = 0;
            int aCurLength = 0;
            foreach (Match aMach in aMatches)
            {
                if (aMach.Success)
                {
                    int aMatchAt = aMach.Index;

                    {//None color part
                        int aLen = aMatchAt - aCurAt;
                        if (aMatchAt > aCurAt)
                        {
                            if (aCurLength + aLen < iMaxLength)
                            {
                                //aSB.Append("(" + aCurLength + ")");
                                aSB.Append(iStr.Substring(aCurAt, aLen));
                                //aSB.Append("@ ");
                                aCurLength += aLen;
                            }
                            else
                            {
                                //aSB.Append("(" + aCurLength + ")");
                                aSB.Append(iStr.Substring(aCurAt, iMaxLength - aCurLength));
                                aSB.Append(iEndWith);
                                //aSB.Append("&");
                                return aSB.ToString();
                            }
                        }
                    }

                    {//Color Part(<color=#......>*.+</color>)

                        int aLen = aMach.Length - 23;//15 = length of <color=#......>, 8 = length of </color>
                        if(aCurLength + aLen <= iMaxLength)
                        {
                            //aSB.Append("($" + aCurLength + ")");
                            aSB.Append(iStr.Substring(aMatchAt, aMach.Length));
                            //aSB.Append("#");
                            aCurLength += aLen;
                        }
                        else
                        {
                            //aSB.Append("($" + aCurLength + ")["+ aMach.Length + "]");
                            aSB.Append(iStr.Substring(aMatchAt, (iMaxLength - aCurLength) + 15));
                            aSB.Append("</color>");
                            aSB.Append(iEndWith);
                            return aSB.ToString();
                        }
                        
                        aCurAt = aMatchAt + aMach.Length;
                    }


                }
            }
            {
                int aLen = iStr.Length - aCurAt;
                if (aCurLength + aLen < iMaxLength)
                {
                    aSB.Append(iStr.Substring(aCurAt, aLen));
                    aCurLength += aLen;
                }
                else
                {
                    aSB.Append(iStr.Substring(aCurAt, iMaxLength - aCurLength));
                    aSB.Append(iEndWith);
                }
            }

            return aSB.ToString();
        }
        if(iStr.Length <= iMaxLength)
        {
            return iStr;
        }
        return iStr.Substring(0, iMaxLength) + iEndWith;
    }
    /// <summary>
    /// Return iStr if string length <= iMaxLength
    /// otherwise return Substring of iMaxLength + iEndWith
    /// etc. "HelloWorld" with MaxLength 6 will return "HelloW..."
    /// </summary>
    /// <param name="iStr"></param>
    /// <param name="iMaxLength"></param>
    /// <param name="iEndWith"></param>
    /// <returns></returns>
    public static string CutToMaxLength(this string iStr, int iMaxLength, string iEndWith = "...")
    {
        return iStr.Length <= iMaxLength ? iStr : iStr.Substring(0, iMaxLength) + iEndWith;
    }
    /// <summary>
    /// string.IsNullOrEmpty(iStr);
    /// </summary>
    /// <param name="iStr"></param>
    /// <returns></returns>
    public static bool IsNullOrEmpty(this string iStr)
    {
        return string.IsNullOrEmpty(iStr);
    }

    public static string RemoveLast(this string iStr) {
        int aLen = iStr.Length;
        if(aLen == 0) return string.Empty;
        return iStr.Remove(aLen - 1);
    }
    public static char LastElement(this string iStr)
    {
        int aLen = iStr.Length;
        if (aLen == 0) return default;
        return iStr[aLen - 1];
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
    #region Vector
    public static string ToStringDetailValue(this Vector3 vec3)
    {
        return $"({vec3.x},{ vec3.y},{ vec3.z})";
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
        /// <summary>
        /// 將該Transform與所有子Transform的active狀態轉換為字串輸出
        /// 可以用來記錄當下gameobject開關情況
        /// </summary>
        /// <param name=""transform""></param>
        /// <param name=""stringBuilder""></param>
        /// <returns></returns>
        public static string ActiveStateToString(this Transform iTransform, System.Text.StringBuilder stringBuilder = null, int space = 0)
        {
            if (stringBuilder == null)
            {
                stringBuilder = new System.Text.StringBuilder();
            }
            stringBuilder.Append('\t', space);
            stringBuilder.AppendLine($"[{iTransform.name}] activeSelf:{(iTransform.gameObject.activeSelf ? "O" : "X")},activeInHierarchy: {(iTransform.gameObject.activeInHierarchy ? "O" : "X")}");
            foreach (Transform child in iTransform)
            {
                ActiveStateToString(child, stringBuilder, space + 1);
            }
            return stringBuilder.ToString();
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