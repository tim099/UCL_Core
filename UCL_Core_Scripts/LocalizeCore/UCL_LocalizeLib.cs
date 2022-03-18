using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.LocalizeLib
{
    public static class UCL_LocalizeLib
    {
        public static string GetEnumLocalize(string iTypeName, string iEnum)
        {
            string aLocalizeKey = iTypeName + "_" + iEnum;
            if (LocalizeLib.UCL_LocalizeManager.ContainsKey(aLocalizeKey))
            {
                iEnum = string.Format("{0}({1})", LocalizeLib.UCL_LocalizeManager.Get(aLocalizeKey), iEnum);
            }
            return iEnum;
        }
    }
}

