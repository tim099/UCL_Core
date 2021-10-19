using System.Collections;
using System.Collections.Generic;
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
    public static string HightLight(this System.Text.RegularExpressions.Regex iRegex, string iDisplayName, string iSearchName, Color iColor)
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
}
