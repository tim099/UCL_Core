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
            var aMatches = iRegex.Matches(iDisplayName.ToLower());
            if (aMatches.Count > 0)
            {
                System.Text.StringBuilder aSB = new System.Text.StringBuilder();
                int aCurAt = 0;
                foreach (Match aMach in aMatches)
                {
                    if (aMach.Success)
                    {
                        int aMatchAt = aMach.Index;
                        if(aMatchAt > aCurAt)
                        {
                            aSB.Append(iDisplayName.Substring(aCurAt, aMatchAt - aCurAt));
                        }
                        aSB.Append(iDisplayName.Substring(aMatchAt, aMach.Length).RichTextColor(iColor));
                        aCurAt = aMatchAt + aMach.Length;
                    }
                }
                if(aCurAt != iDisplayName.Length)
                {
                    aSB.Append(iDisplayName.Substring(aCurAt, iDisplayName.Length - aCurAt));
                }
                return aSB.ToString();
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
