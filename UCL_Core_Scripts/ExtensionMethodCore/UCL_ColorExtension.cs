using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public static partial class UCL_ColorExtension
{
    /// <summary>
    /// Set Image color alpha value
    /// </summary>
    /// <param name="iImage"></param>
    /// <param name="iAlpha"></param>
    public static void SetAlpha(this Image iImage, float iAlpha)
    {
        if (iImage == null) return;
        var iCol = iImage.color;
        iImage.color = new Color(iCol.r, iCol.g, iCol.b, iAlpha);
    }
}
