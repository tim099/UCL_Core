using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public static partial class MiscExtensionMethods
{
    #region Text
    public static Text SetText(this Text text, string val) {
        if(text == null) {
            //Debug.LogError("SetText Fail text == null");
            return null;
        }
        text.text = val;
        return text;
    }
    public static Text SetText(this Text text, int val) {
        if(text == null) {
            return null;
        }
        text.text = ""+val;
        return text;
    }
    #endregion

    #region Image
    public static Image SetSprite(this Image target, Sprite val) {
        if(target == null) {
            return null;
        }
        target.sprite = val;
        return target;
    }
    #endregion
}
