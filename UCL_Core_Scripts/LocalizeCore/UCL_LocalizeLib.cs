using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.LocalizeLib
{
    public static class UCL_LocalizeLib
    {
        public static string GetEnumLocalize(string iTypeName, string iEnumName)
        {
            string aLocalizeKey = iTypeName + "_" + iEnumName;
            if (LocalizeLib.UCL_LocalizeManager.ContainsKey(aLocalizeKey))
            {
                iEnumName = string.Format("{0}({1})", LocalizeLib.UCL_LocalizeManager.Get(aLocalizeKey), iEnumName);
            }
            return iEnumName;
        }
        public static string GetLocalize(string iName)
        {
            if (LocalizeLib.UCL_LocalizeManager.ContainsKey(iName))
            {
                iName = string.Format("{0}({1})", LocalizeLib.UCL_LocalizeManager.Get(iName), iName);
            }
            return iName;
        }
    }
    public static partial class UCL_LocalizeExtensions
    {
        public static string GetLocalizeEnumName(this System.Enum iEnum)
        {
            string aEnumName = iEnum.ToString();
            string aLocalizeKey = iEnum.GetType().Name + "_" + aEnumName;
            if (LocalizeLib.UCL_LocalizeManager.ContainsKey(aLocalizeKey))
            {
                return string.Format("{0}({1})", LocalizeLib.UCL_LocalizeManager.Get(aLocalizeKey), iEnum);
            }
            return aEnumName;
        }
    }
}

