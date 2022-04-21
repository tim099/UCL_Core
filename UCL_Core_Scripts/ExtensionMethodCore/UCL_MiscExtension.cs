using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public static partial class MiscExtensionMethods
{
    #region GameObject
    /// <summary>
    /// if Component already exist, this will return iTarget.GetComponent<T>()
    /// otherwise return iTarget.AddComponent<T>();
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iTarget"></param>
    /// <returns></returns>
    public static T GetOrAddComponent<T>(this GameObject iTarget) where T : Component
    {
        if (iTarget == null) return null;
        T aComponent = iTarget.GetComponent<T>();
        if (aComponent == null)
        {
            aComponent = iTarget.AddComponent<T>();
        }
        return aComponent;
    }
    /// <summary>
    /// if Component already exist, this will return iTarget.gameObject.GetComponent<T>()
    /// otherwise return iTarget.gameObject.AddComponent<T>();
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="iTarget"></param>
    /// <returns></returns>
    public static T GetOrAddComponent<T>(this Component iTarget) where T : Component
    {
        if (iTarget == null) return null;
        T aComponent = iTarget.gameObject.GetComponent<T>();
        if (aComponent == null)
        {
            aComponent = iTarget.gameObject.AddComponent<T>();
        }
        return aComponent;
    }
    #endregion

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
