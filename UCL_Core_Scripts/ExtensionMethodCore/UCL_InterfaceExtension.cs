using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core
{
    static public partial class UCL_InterfaceExtension
    {
        public static void Copy(this UCLI_CopyPaste iTarget)
        {
            UCL.Core.CopyPaste.SetCopyData(iTarget);
        }
        public static T Paste<T>(this T iObj) where T : UCLI_CopyPaste
        {
            T aResultObj = default;
            if (iObj != null && typeof(T) == CopyPaste.s_CopyType)
            {
                UCL.Core.CopyPaste.LoadCopyData(iObj);
                aResultObj = iObj;
            }
            else
            {
                aResultObj = (T)UCL.Core.CopyPaste.GetCopyData();
            }
            return aResultObj;
        }
        //UCLI_CopyPaste
    }
}

