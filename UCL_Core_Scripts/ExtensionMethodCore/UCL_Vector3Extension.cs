using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static partial class VectorExtensionMethods {
    public enum Vec3ToVec2 {
        xy,
        xz,
        yz,
        yx,
        zx,
        zy
    }

    public static float GetValue(this Vector3 t, int at) {
        return at == 0 ? t.x :
               at == 1 ? t.y : t.z;
    }
    public static Vector3 Dot(this Vector3 a, Vector3 b) {
        return new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);
    }
    public static Vector3 ToVec3(this Vector2 a) {
        return new Vector3(a.x, a.y, 0);
    }

    #region Vector2
    public static Vector2 XY(this Vector3 a) { return new Vector2(a.x, a.y); }
    public static Vector2 XZ(this Vector3 a) { return new Vector2(a.x, a.z); }
    public static Vector2 YZ(this Vector3 a) { return new Vector2(a.y, a.z); }
    public static Vector2 YX(this Vector3 a) { return new Vector2(a.y, a.x); }
    public static Vector2 ZX(this Vector3 a) { return new Vector2(a.z, a.x); }
    public static Vector2 ZY(this Vector3 a) { return new Vector2(a.z, a.y); }

    public static Vector2 ToVec2(this Vector3 a, Vec3ToVec2 dir = Vec3ToVec2.xy) {
        switch(dir) {
            case Vec3ToVec2.xy: return new Vector2(a.x, a.y);
            case Vec3ToVec2.xz: return new Vector2(a.x, a.z);
            case Vec3ToVec2.yz: return new Vector2(a.y, a.z);

            case Vec3ToVec2.yx: return new Vector2(a.y, a.x);
            case Vec3ToVec2.zx: return new Vector2(a.z, a.x);
            case Vec3ToVec2.zy: return new Vector2(a.z, a.y);
        }
        return new Vector2(a.x, a.y);
    }
    public static float Radius(this Vector2 a) {
        return Mathf.Atan2(a.y,a.x);
    }

    #endregion
}