using System.Collections;
using System.Collections.Generic;
using UnityEngine;


public static partial class EventExtensionMethods {

    /// <summary>
    /// return true if iEvent is null or iEvent.GetPersistentEventCount() == 0
    /// </summary>
    /// <param name="iEvent"></param>
    /// <returns></returns>
    public static bool IsNullOrEmpty(this UCL.Core.UCL_Event iEvent) {
        if(iEvent != null && iEvent.GetPersistentEventCount() > 0) return false;
        return true;
    }

    /// <summary>
    /// Do null check before Invoke Event, and catch exception
    /// </summary>
    /// <param name="iEvent"></param>
    /// <param name="iVal"></param>
    public static void UCL_Invoke(this UCL.Core.UCL_Event iEvent) {
        if(iEvent == null) return;
        try {
            iEvent.Invoke();
        } catch(System.Exception e) {
            Debug.LogException(e);
        }
    }
    /// <summary>
    /// Do null check before Invoke Event, and catch exception
    /// </summary>
    /// <param name="iEvent"></param>
    /// <param name="iVal"></param>
    public static void UCL_Invoke(this UCL.Core.UCL_FloatEvent iEvent,float iVal) {
        if(iEvent == null) return;
        try {
            iEvent.Invoke(iVal);
        } catch(System.Exception e) {
            Debug.LogException(e);
        }
    }
    /// <summary>
    /// Do null check before Invoke Event, and catch exception
    /// </summary>
    /// <param name="iEvent"></param>
    /// <param name="iVal"></param>
    public static void UCL_Invoke(this UCL.Core.UCL_IntEvent iEvent, int iVal) {
        if(iEvent == null) return;
        try {
            iEvent.Invoke(iVal);
        } catch(System.Exception e) {
            Debug.LogException(e);
        }
    }
    /// <summary>
    /// Do null check before Invoke Event, and catch exception
    /// </summary>
    /// <param name="iEvent"></param>
    /// <param name="iVal"></param>
    public static void UCL_Invoke(this UCL.Core.UCL_BoolEvent iEvent, bool iVal) {
        if(iEvent == null) return;
        try {
            iEvent.Invoke(iVal);
        } catch(System.Exception e) {
            Debug.LogException(e);
        }
    }
}
