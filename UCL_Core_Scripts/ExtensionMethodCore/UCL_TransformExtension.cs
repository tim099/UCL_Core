using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static partial class VectorExtensionMethods {
    #region LocalPosition

    public static void SetLocalX(this Transform t, float x) {
        t.localPosition = new Vector3(x, t.localPosition.y, t.localPosition.z);
    }
    public static void SetLocalY(this Transform t, float y) {
        t.localPosition = new Vector3(t.localPosition.x, y, t.localPosition.z);
    }
    public static void SetLocalZ(this Transform t, float z) {
        t.localPosition = new Vector3(t.localPosition.x, t.localPosition.y, z);
    }


    #endregion

    #region Position
    public static void SetX(this Transform t, float x) {
        t.position = new Vector3(x, t.position.y, t.position.z);
    }
    public static void SetY(this Transform t, float y) {
        t.position = new Vector3(t.position.x, y, t.position.z);
    }
    public static void SetZ(this Transform t, float z) {
        t.position = new Vector3(t.position.x, t.position.y, z);
    }
    public static void SetPosition(this Transform t, Transform target) {
        t.position = target.position;
    }
    public static void SetValue(this Transform t, Transform target) {
        t.position = target.position;
        t.rotation = target.rotation;
        t.localScale = target.localScale;
    }
    #endregion

    public enum TransformMode {
        WorldSpace,
        Local,
        WorldSpaceUnscaled,
        LocalUnscaled,
    }
    public static Vector3 TransformPoint(this Transform transform, Vector3 pos, TransformMode m_TransformMode) {
        switch(m_TransformMode) {
            case TransformMode.Local: return transform.TransformPointLocal(pos);
            case TransformMode.WorldSpaceUnscaled: return transform.TransformPointUnscaled(pos);
            case TransformMode.LocalUnscaled: return transform.TransformPointLocalUnscaled(pos);
        }
        return transform.TransformPoint(pos);//WorldSpace
    }
    public static Vector3 TransformPointLocal(this Transform transform, Vector3 position) {
        var localToWorldMatrix = Matrix4x4.TRS(transform.localPosition, transform.localRotation, transform.localScale);
        return localToWorldMatrix.MultiplyPoint3x4(position);
    }
    public static Vector3 InverseTransformLocalPoint(this Transform transform, Vector3 position) {
        var worldToLocalMatrix = Matrix4x4.TRS(transform.localPosition, transform.localRotation, transform.localScale).inverse;
        return worldToLocalMatrix.MultiplyPoint3x4(position);
    }

    public static Vector3 TransformPointLocalUnscaled(this Transform transform, Vector3 position) {
        var localToWorldMatrix = Matrix4x4.TRS(transform.localPosition, transform.localRotation, Vector3.one);
        return localToWorldMatrix.MultiplyPoint3x4(position);
    }
    public static Vector3 InverseTransformLocalPointUnscaled(this Transform transform, Vector3 position) {
        var worldToLocalMatrix = Matrix4x4.TRS(transform.localPosition, transform.localRotation, Vector3.one).inverse;
        return worldToLocalMatrix.MultiplyPoint3x4(position);
    }
    /// <summary>
    /// reference https://answers.unity.com/questions/1238142/version-of-transformtransformpoint-which-is-unaffe.html
    /// </summary>
    /// <param name="transform"></param>
    /// <param name="position"></param>
    /// <returns></returns>
    public static Vector3 TransformPointUnscaled(this Transform transform, Vector3 position) {
        var localToWorldMatrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
        return localToWorldMatrix.MultiplyPoint3x4(position);
    }
    /// <summary>
    /// reference https://answers.unity.com/questions/1238142/version-of-transformtransformpoint-which-is-unaffe.html
    /// </summary>
    /// <param name="transform"></param>
    /// <param name="position"></param>
    /// <returns></returns>
    public static Vector3 InverseTransformPointUnscaled(this Transform transform, Vector3 position) {
        var worldToLocalMatrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one).inverse;
        return worldToLocalMatrix.MultiplyPoint3x4(position);
    }
}
