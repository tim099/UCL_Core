using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
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

    #region ScrollRect
    public static void ToTop(this ScrollRect iScrollRect) {
        if (iScrollRect == null) return;
        iScrollRect.verticalNormalizedPosition = 1f;
    }
    public static void ToBottom(this ScrollRect iScrollRect) {
        if (iScrollRect == null) return;
        iScrollRect.verticalNormalizedPosition = 0f;
    }
    #endregion

    #region Toggle
    /// <summary>
    /// Set iToggle.isOn = true, if already == true ,then iToggle.onValueChanged.Invoke(true);
    /// </summary>
    /// <param name="iToggle"></param>
    public static void SetIsOn(this Toggle iToggle)
    {
        if (iToggle == null) return;
        if (iToggle.isOn)
        {//Trigger action
            iToggle.onValueChanged.Invoke(true);
        }
        else
        {
            iToggle.isOn = true;
        }
    }


    #endregion

    #region Button
    /// <summary>
    /// Create a fake click event via script
    /// </summary>
    /// <param name="button"></param>
    public static void Click(this Button button)
    {
        if (button == null) return;
        ExecuteEvents.Execute(button.gameObject, new BaseEventData(EventSystem.current), ExecuteEvents.submitHandler);
    }
    #endregion
}
