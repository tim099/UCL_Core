using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public static partial class ActionExtensionExtensionMethods
{
    public static int GetInvocationCount(this System.Action iAction)
    {
        if (iAction == null) return 0;
        return iAction.GetInvocationList().GetLength(0);
    }
    //public static void AssignAction(this System.Action iTarget, System.Action iAction)
    //{
    //    iTarget -= iAction;
    //    iTarget += iAction;
    //}
}