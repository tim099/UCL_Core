using System.Collections;
using System.Collections.Generic;
using UnityEngine;


public static partial class EventExtensionMethods {
    public static void UCL_Invoke(this UCL.Core.UCL_Event ucl_event) {
        if(ucl_event == null) return;
        try {
            ucl_event.Invoke();
        } catch(System.Exception e) {
            Debug.LogError("UCL_Event Invoke Exception:" + e);
        }
    }
    public static void UCL_Invoke(this UCL.Core.UCL_FloatEvent ucl_event,float val) {
        if(ucl_event == null) return;
        try {
            ucl_event.Invoke(val);
        } catch(System.Exception e) {
            Debug.LogError("UCL_Event Invoke Exception:" + e);
        }
    }
    public static void UCL_Invoke(this UCL.Core.UCL_IntEvent ucl_event, int val) {
        if(ucl_event == null) return;
        try {
            ucl_event.Invoke(val);
        } catch(System.Exception e) {
            Debug.LogError("UCL_Event Invoke Exception:" + e);
        }
    }
    public static void UCL_Invoke(this UCL.Core.UCL_BoolEvent ucl_event, bool val) {
        if(ucl_event == null) return;
        try {
            ucl_event.Invoke(val);
        } catch(System.Exception e) {
            Debug.LogError("UCL_Event Invoke Exception:" + e);
        }
    }
}
