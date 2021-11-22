using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static partial class TransformExtensionMethods {
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
    public static void SetLossyScale(this Transform iTarget, Vector3 iScale)
    {
        if(iTarget.parent == null)
        {
            iTarget.localScale = iScale;
            return;
        }
        Vector3 pScale = iTarget.parent.lossyScale;
        float aX = iScale.x;
        if (pScale.x != 0) aX /= pScale.x;
        float aY = iScale.y;
        if (pScale.y != 0) aY /= pScale.y;
        float aZ = iScale.z;
        if (pScale.z != 0) aZ /= pScale.z;
        iTarget.localScale = new Vector3(aX,  aY, aZ);
    }
    #endregion

    #region Transform
    public static void SetPositionAndRotation(this Transform iTarget, Transform iMoveTarget)
    {
        iTarget.position = iMoveTarget.position;
        iTarget.rotation = iMoveTarget.rotation;
    }
    public static void ForceRebuildLayoutImmediate(this Transform iTarget)
    {
        var aRectTransform = iTarget.GetComponent<RectTransform>();
        if (aRectTransform != null)
        {
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(aRectTransform);
        }
    }
    #endregion

    #region RectTransform
    /// <summary>
    /// Set the Top value of RectTransform
    /// </summary>
    /// <param name="iRect"></param>
    /// <param name="iTop"></param>
    public static void SetTop(this RectTransform iRect, float iTop)
    {
        iRect.offsetMax = new Vector2(iRect.offsetMax.x, -iTop);
    }
    /// <summary>
    /// Copy the size and position from iTarget to iRect
    /// </summary>
    /// <param name="iRect"></param>
    /// <param name="iTarget"></param>
    public static void CopyValue(this RectTransform iRect, RectTransform iTarget)
    {
        if (iRect == null || iTarget == null) return;
        iRect.pivot = iTarget.pivot;
        iRect.localScale = iTarget.localScale;
        iRect.sizeDelta = iTarget.sizeDelta;
        iRect.position = iTarget.position;
    }
    /// <summary>
    /// Copy the size ,position, and rotation from iTarget to iRect
    /// support cross canvas copy
    /// </summary>
    /// <param name="iRect"></param>
    /// <param name="iTarget"></param>
    public static void CopyValueWorldSpace(this RectTransform iRect, RectTransform iTarget)
    {
        if (iRect == null || iTarget == null) return;
        Transform aTargetTransform = iRect.GetComponentInParent<Canvas>().transform;//iRect.parent;
        Canvas aCanvasB = iTarget.GetComponentInParent<Canvas>();
        Vector3[] aCorners = new Vector3[4];
        iTarget.GetWorldCorners(aCorners);
        for(int i = 0; i < 4; i++)
        {
            aCorners[i] = aCanvasB.transform.InverseTransformPoint(aCorners[i]);
        }
        Vector2 aHorVec = aCorners[0] - aCorners[3];
        Vector2 aVerVec = aCorners[0] - aCorners[1];
        Vector2 aMidPoint = 0.5f * (aCorners[0] + aCorners[2]);
        Vector2 aSize = new Vector2(aHorVec.magnitude, aVerVec.magnitude);
        //Debug.LogError("sCorners:" + sCorners.UCL_ToString());
        //Debug.LogError("aSize:" + aSize.ToString());
        iRect.pivot = iTarget.pivot;
        iRect.anchorMin = 0.5f * Vector2.one;
        iRect.anchorMax = 0.5f * Vector2.one;
        iRect.sizeDelta = aSize;//aTargetTransform.TransformPoint(aSize) - aTargetTransform.TransformPoint(Vector2.zero);//iTarget.sizeDelta;

        //iRect.position = aCanvasA.transform.TransformPoint(aMidPoint);
        iRect.position = aTargetTransform.TransformPoint(aMidPoint);

        //iRect.anchoredPosition = iTarget.anchoredPosition;
        float aX = -aHorVec.x;
        float aY = -aHorVec.y;
        float aRot = Mathf.Atan2(aY, aX) * Mathf.Rad2Deg;
        iRect.transform.rotation = Quaternion.Euler(0, 0, aRot);
        //Debug.LogError("aRot:" + aRot + ",aX:" + aX + ",aY:" + aY);
    }
    /// <summary>
    /// Get screen space rect of RectTransform
    /// </summary>
    /// <param name="iRect"></param>
    /// <returns></returns>
    public static Rect ScreenSpaceRect(this RectTransform iRect)
    {
        Vector2 aSize = Vector2.Scale(iRect.rect.size, iRect.lossyScale);
        return new Rect((Vector2)iRect.position - (aSize * iRect.pivot), aSize);
    }
    public static void SetFullScreen(this RectTransform iTarget) {
        iTarget.anchorMin = new Vector2(0, 0);
        iTarget.anchorMax = new Vector2(1, 1);
        iTarget.sizeDelta = new Vector2(0, 0);
    }
    /// <summary>
    /// Set the sizeDelta position and rotation of rect fit into point a and b
    /// </summary>
    /// <param name="iTarget">Target RectTransform</param>
    /// <param name="a">target point a</param>
    /// <param name="b">target point b</param>
    public static void SetBetweenTwoPoint(this RectTransform iTarget, Vector2 a, Vector2 b) {
        Vector2 del = b - a;
        float x = 0.5f * (a.x + b.x);
        float y = 0.5f * (a.y + b.y);
        float dis = del.magnitude;
        iTarget.sizeDelta = new Vector2(dis / iTarget.lossyScale.x, iTarget.sizeDelta.y);
        iTarget.position = new Vector3(x, y, iTarget.transform.position.z);
        iTarget.rotation = Quaternion.Euler(0, 0, Mathf.Rad2Deg * Mathf.Atan2(del.y, del.x));
    }

    public static void ForceRebuildLayoutImmediate(this RectTransform iTarget) {
        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(iTarget);
    }

    public static bool ContainPoint(this RectTransform iTarget, Vector2 point) {
        Rect rect = iTarget.rect;
        Vector3[] corners = new Vector3[4];
        iTarget.GetWorldCorners(corners);

        var a = corners[0];
        var b = corners[1];
        var c = corners[2];
        var d = corners[3];
        var canvas = iTarget.GetComponentInParent<Canvas>();
        if(canvas && canvas.renderMode == RenderMode.ScreenSpaceCamera) {
            a = canvas.worldCamera.WorldToScreenPoint(a);
            b = canvas.worldCamera.WorldToScreenPoint(b);
            c = canvas.worldCamera.WorldToScreenPoint(c);
            d = canvas.worldCamera.WorldToScreenPoint(d);
        }
        if(UCL.Core.MathLib.Geometry.CheckWithinTriangle(point, a, b, c)
            || UCL.Core.MathLib.Geometry.CheckWithinTriangle(point, a, c, d)) {
            return true;
        }
        return false;
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
