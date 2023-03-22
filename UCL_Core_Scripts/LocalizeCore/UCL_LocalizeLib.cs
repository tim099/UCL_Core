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
                string aLocalizdeName = UCL_LocalizeManager.Get(aLocalizeKey);
                if(aLocalizdeName != iEnumName) iEnumName = string.Format("{0}({1})", aLocalizdeName, iEnumName);
            }
            return iEnumName;
        }
        public static string GetLocalize(string iName)
        {
            if (LocalizeLib.UCL_LocalizeManager.ContainsKey(iName))
            {
                string aLocalizdeName = LocalizeLib.UCL_LocalizeManager.Get(iName);
                if (aLocalizdeName != iName) iName = string.Format("{0}({1})", aLocalizdeName, iName);
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
                string aLocalizdeName = UCL_LocalizeManager.Get(aLocalizeKey);
                if (aLocalizdeName != aEnumName) return string.Format("{0}({1})", aLocalizdeName, aEnumName);
            }
            return aEnumName;
        }
    }
}

