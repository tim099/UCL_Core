using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static partial class ExtensionMethods {
    public static float GetValue(this Vector3 t, int at) {
        return at == 0 ? t.x :
               at == 1 ? t.y : t.z;
    }
    public static Vector3 Dot(this Vector3 a, Vector3 b) {
        return new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);
    }
}