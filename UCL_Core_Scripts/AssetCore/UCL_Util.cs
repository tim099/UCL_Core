
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/22 2024 13:09
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core
{
    public class UCL_Util<T> where T : class, new()
    {
        private static T s_Util = null;
        public static T Util => s_Util == null ? s_Util = new T() : s_Util;
    }
}
