using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

public static partial class RegexExtensionMethods
{
    /// <summary>
    /// Hight light the target Text
    /// </summary>
    /// <param name="iRegex">Regex create by iSearchName</param>
    /// <param name="iDisplayName">The origin Display text </param>
    /// <param name="iSearchName">the text you want to highlight</param>
    /// <param name="iColor">Highlight Color</param>
    /// <returns></returns>
    public static string HightLight(this Regex iRegex, string iDisplayName, string iSearchName, Color iColor)
    {
        if (iRegex == null) return iDisplayName;
        if (iRegex != null)
        {
            var aMaches = iRegex.Matches(iDisplayName.ToLower());
            if (aMaches.Count > 0)
            {
                var aMach = aMaches[0];
                if (aMach.Success)
                {
                    int aMatchAt = aMach.Index;
                    System.Text.StringBuilder aSB = new System.Text.StringBuilder();
                    string aValue = iDisplayName.Substring(aMatchAt, iSearchName.Length);
                    aSB.Append(iDisplayName.Substring(0, aMatchAt));
                    aSB.Append(aValue.RichTextColor(iColor));
                    aSB.Append(iDisplayName.Substring(aMatchAt + aValue.Length, iDisplayName.Length - aValue.Length - aMatchAt));
                    return aSB.ToString();
                }
            }
        }

        return iDisplayName;
    }

    /// <summary>
    /// Create a Regex to check file Extension match iFileExtensions
    /// etc. iFileExtensions = new string[] {"json", "prefab" } , return Regex will match a.json and b.prefab
    /// </summary>
    /// <param name="iFileExtensions">etc. iFileExtensions = new string[] {"json", "prefab" }</param>
    /// <returns>Regex that match iFileExtensions</returns>
    public static Regex GetFileExtensionRegex(this string[] iFileExtensions)
    {
        
        var aSB = new StringBuilder();
        for (var i = 0; i < iFileExtensions.Length; i++)
        {
            if (i > 0)
            {
                aSB.Append("|");
            }
            aSB.Append(iFileExtensions[i]);
        }

        string aFormat = "([^\\s]+(\\.(?i)(" + aSB.ToString() + "))$)";

        var aRegex = new Regex(aFormat, RegexOptions.Compiled);
        return aRegex;
    }
}
