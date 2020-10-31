using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public static partial class UIExtensionMethods
{
    public static void ToTop(this ScrollRect scrollRect) {
        scrollRect.verticalNormalizedPosition = 1f;
    }
    public static void ToBottom(this ScrollRect scrollRect) {
        scrollRect.verticalNormalizedPosition = 0f;
    }
}
