
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/27 2024 18:03
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core
{
    public static partial class RCG_GameObjectExtensions
    {
        public static void TrySetActive(this GameObject iObj, bool iValue)
        {
            if (iObj == null) return;
            if (iObj.activeSelf == iValue) return;
            iObj.SetActive(iValue);
        }
        public static void ToggleActiveState(this GameObject iObj)
        {
            if (iObj == null) return;
            iObj.SetActive(!iObj.activeSelf);
        }
    }
}