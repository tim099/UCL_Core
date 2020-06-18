using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Tween {
    public class UCL_TweenerCurve : UCL_Tweener {
        protected MathLib.UCL_Curve m_Curve;
        protected Transform m_Target;

        UCL_TweenerCurve() {

        }

        public static UCL_TweenerCurve Create() {
            var obj = new UCL_TweenerCurve();
            UCL_TweenManager.Instance.Add(obj);
            return obj;
        }

        virtual public UCL_TweenerCurve Start(float duration, MathLib.UCL_Curve curve, Transform target) {
            m_Duration = duration;
            m_Curve = curve;
            m_Target = target;
            return this;
        }
        protected override void TweenerUpdate(float pos) {
            m_Target.transform.position = m_Curve.GetPoint(pos);

        }
    }
}