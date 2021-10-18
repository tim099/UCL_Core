using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static partial class RegexExtensionMethods
{
    public static string HightLight(this System.Text.RegularExpressions.Regex iRegex, string iDisplayName, string iSearchName)
    {
        if (iRegex == null) return iDisplayName;
        if (iRegex != null)
        {
            var aMaches = iRegex.Matches(iDisplayName.ToLower());
            if (aMaches.Count > 0)
            {
                var aMach = aMaches[0];//aRegex.Matches(aDisplayName);
                if (aMach.Success)
                {
                    int aMatchAt = aMach.Index;
                    string aValue = iDisplayName.Substring(aMatchAt, iSearchName.Length);
                    iDisplayName = iDisplayName.Substring(0, aMatchAt)
                        + aValue.RichTextColor(Color.red)
                        + iDisplayName.Substring(aMatchAt + aValue.Length, iDisplayName.Length - aValue.Length - aMatchAt);
                }
            }
        }

        return iDisplayName;
    }
}
