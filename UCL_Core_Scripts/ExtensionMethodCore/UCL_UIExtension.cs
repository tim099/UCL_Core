using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public static partial class UIExtensionMethods
{
    #region Button
    //public static void PressButton(this Button button) {
    //    UnityEngine.EventSystems.ExecuteEvents.Execute(button.gameObject,
    //        new UnityEngine.EventSystems.BaseEventData(eventSystem),
    //        UnityEngine.EventSystems.ExecuteEvents.submitHandler);

    //}
    //yourButton.Select();
    #endregion
    public static void ToTop(this ScrollRect iScrollRect) {
        if (iScrollRect == null) return;
        iScrollRect.verticalNormalizedPosition = 1f;
    }
    public static void ToBottom(this ScrollRect iScrollRect) {
        if (iScrollRect == null) return;
        iScrollRect.verticalNormalizedPosition = 0f;
    }
}
