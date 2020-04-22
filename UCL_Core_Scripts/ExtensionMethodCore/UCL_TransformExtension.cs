using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static partial class ExtensionMethods {
    #region LocalPosition

    public static void SetLocalPositionX(this Transform t, float x) {
        t.localPosition = new Vector3(x, t.localPosition.y, t.localPosition.z);
    }
    public static void SetLocalPositionY(this Transform t, float y) {
        t.localPosition = new Vector3(t.localPosition.x, y, t.localPosition.z);
    }
    public static void SetLocalPositionZ(this Transform t, float z) {
        t.localPosition = new Vector3(t.localPosition.x, t.localPosition.y, z);
    }

    #endregion
}
