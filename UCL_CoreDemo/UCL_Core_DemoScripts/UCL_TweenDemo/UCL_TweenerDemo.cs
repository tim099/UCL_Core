using System.Collections;
using System.Collections.Generic;
using UCL.Core.Tween.Ease;
using UnityEngine;

namespace UCL.Core.Tween.Demo {
    [ATTR.EnableUCLEditor]
    public class UCL_TweenerDemo : MonoBehaviour {
        public EaseType m_Ease;
        public float m_Duration = 5.0f;
        public Transform m_Target;
        public MathLib.UCL_Curve m_Curve;
        private void Start() {
            StartTweener();
        }
#if UNITY_EDITOR
        [ATTR.UCL_DrawTexture2D(128, 128, TextureFormat.ARGB32, "UCL_EaseTexture")]
        public void DrawEaseCurve(Core.TextureLib.UCL_Texture2D texture) {
            UCL_EaseTexture.DrawEase(m_Ease, texture);
        }
#endif
        [ATTR.UCL_FunctionButton]
        public void StartTweener() {
            UCL_TweenerCurve.Create().Start(m_Duration, m_Curve, m_Target).SetEase(m_Ease);
        }
    }
}