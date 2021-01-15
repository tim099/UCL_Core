using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static partial class VectorExtensionMethods {
    #region Vector3

    public enum Vec3ToVec2 {
        xy,
        xz,
        yz,
        yx,
        zx,
        zy
    }
    public static Vector3 Min(this Vector3 vec, params Vector3[] vecs) {
        Vector3 min = vec;
        for(int i = 0; i < vecs.Length; i++) {
            Vector3 nvec = vecs[i];
            if(nvec.x < min.x) min.x = nvec.x;
            if(nvec.y < min.y) min.y = nvec.y;
            if(nvec.z < min.z) min.z = nvec.z;
        }
        return min;
    }
    public static Vector3 Max(this Vector3 vec,params Vector3[] vecs) {
        Vector3 max = vec;
        for(int i = 0; i < vecs.Length; i++) {
            Vector3 nvec = vecs[i];
            if(nvec.x > max.x) max.x = nvec.x;
            if(nvec.y > max.y) max.y = nvec.y;
            if(nvec.z > max.z) max.z = nvec.z;
        }
        return max;
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

    public static System.Tuple<string,string,string> ToTupleString(this Vector3 vec) {
        return new System.Tuple<string, string, string>(vec.x.ToString(), vec.y.ToString(), vec.z.ToString());
    }
    #endregion

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
    public static float Degree(this Vector2 a) {
        return Mathf.Atan2(a.y, a.x) * Mathf.Rad2Deg;
    }
    public static float Radius(this Vector2 a) {
        return Mathf.Atan2(a.y,a.x);
    }
    public static Vector2 Min(this Vector2 vec, params Vector2[] vecs) {
        Vector2 min = vec;
        for(int i = 0; i < vecs.Length; i++) {
            Vector2 nvec = vecs[i];
            if(nvec.x < min.x) min.x = nvec.x;
            if(nvec.y < min.y) min.y = nvec.y;
        }
        return min;
    }
    public static Vector2 Max(this Vector2 vec, params Vector2[] vecs) {
        Vector2 max = vec;
        for(int i = 0; i < vecs.Length; i++) {
            Vector2 nvec = vecs[i];
            if(nvec.x > max.x) max.x = nvec.x;
            if(nvec.y > max.y) max.y = nvec.y;
        }
        return max;
    }
    public static bool CheckBetween(this Vector2 a, Vector2 start, Vector2 end) {
        Vector2 min = start.Min(end);
        Vector2 max = start.Max(end);
        if(a.x >= min.x && a.x <= max.x && a.y >= min.y && a.y <= max.y) {
            return true;
        }
        return false;
    }
    #endregion
}