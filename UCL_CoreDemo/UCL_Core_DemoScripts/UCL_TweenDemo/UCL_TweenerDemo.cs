using System.Collections;
using System.Collections.Generic;
using UCL.Core.Tween.Ease;
using UnityEngine;

namespace UCL.Core.Tween.Demo {
#if UNITY_EDITOR
    [ATTR.EnableUCLEditor]
    [ATTR.RequiresConstantRepaint]
#endif
    public class UCL_TweenerDemo : MonoBehaviour {
        public EaseType m_Ease;
        public float m_Duration = 5.0f;
        public Transform m_Target;
        public MathLib.UCL_Curve m_Curve;
        UCL_TweenerCurve m_Cur = null;
        private void Start() {
            StartTweener();
        }
#if UNITY_EDITOR
        [ATTR.UCL_DrawTexture2D(128 , 128, TextureFormat.ARGB32, "UCL_EaseTexture")]
        public void DrawEaseCurve(Core.TextureLib.UCL_Texture2D texture) {
            UCL_EaseTexture.DrawEase(m_Ease, texture);
            if(m_Cur != null) {
                Vector2 pos = m_Cur.GetPos();
                var tex = texture as UCL_EaseTexture;
                if(tex != null) {
                    pos.y -= tex.m_Min;
                    pos.y /= tex.m_Range;
                    //pos.y *= 0.99f;
                }
                texture.DrawDot(pos.x, pos.y, Color.red, 2);
            }
        }
        int count = 0;
        [ATTR.UCL_FunctionButton]
        public void StartTweener() {
            if(UnityEditor.EditorApplication.isPlaying) {
                var obj = Instantiate(m_Target.gameObject, m_Target.parent);
                obj.name = "target " + ++count;
                m_Cur = UCL_TweenerCurve.Create();
                var cur = m_Cur;
                m_Cur.Start(m_Duration, m_Curve, obj.transform)
                    .SetEase(m_Ease)
                    .OnComplete(delegate() {
                        if(m_Cur == cur) {
                            m_Cur = null;
                        }
                        Destroy(obj);
                    });
            } else {
                Debug.LogWarning("StartTweener() !UnityEditor.EditorApplication.isPlaying");
            }
        }
#endif

    }
}