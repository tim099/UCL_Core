using System.Collections;
using System.Collections.Generic;
using UCL.Core.LocalizeLib;
using UnityEngine;

namespace UCL.Core
{
    public static class UCL_StaticFunctions
    {
        public static string FieldNameUnityVer(string iDisplayName)
        {
            if (iDisplayName[0] == 'm' && iDisplayName[1] == '_')
            {
                return iDisplayName.Substring(2, iDisplayName.Length - 2);
            }
            return iDisplayName;
        }
        public static string LocalizeFieldName(string iDisplayName)
        {
            if (iDisplayName[0] == 'm' && iDisplayName[1] == '_')
            {
                iDisplayName = iDisplayName.Substring(2, iDisplayName.Length - 2);
            }
            iDisplayName = UCL_LocalizeManager.Get(iDisplayName);
            return iDisplayName;
        }
    }
}

