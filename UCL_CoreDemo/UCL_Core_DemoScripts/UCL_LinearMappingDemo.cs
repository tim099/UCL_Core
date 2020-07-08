using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib.Demo {
    [ATTR.EnableUCLEditor]
    public class UCL_LinearMappingDemo : MonoBehaviour {
        public List<float> m_Datas;
        [PA.UCL_ReadOnly] public List<float> m_OutPositions;
        [Range(0,1)]public float m_Val;
        public UCL_Curve m_Curve;
        public float m_OutPosition;
        public float m_OutValue;

        public Transform m_S, m_E;
        private void OnValidate() {
            m_OutPosition = MathLib.LinearMapping.GetX(m_Datas, m_Val);
            m_OutValue = MathLib.LinearMapping.GetY(m_Datas, m_Val);
            m_OutPositions.Clear();
            if(m_Datas.Count >= 1) {
                float seg = 1.0f / (m_Datas.Count-1);
                for(int i = 0; i < m_Datas.Count; i++) {
                    m_OutPositions.Add(MathLib.LinearMapping.GetX(m_Datas, i * seg));
                }
            }
            m_RevTargetVal = MathLib.LinearMapping.GetX(m_Datas, m_TargetVal);
        }
        private void OnDrawGizmos() {
            var prev_col = Gizmos.color;
            Gizmos.color = Color.green;
            Vector3 s = m_S.position;
            Vector3 e = m_E.position;
            Vector3 prev = Vector3.Lerp(s,e, m_Datas[0]);
            UCL_DrawGizmos.DrawConstSizeSphere(prev, 1f);
            for(int i = 1; i < m_Datas.Count; i++) {
                Vector3 cur = Vector3.Lerp(s, e, m_Datas[i]);
                Gizmos.DrawLine(prev, cur);
                UCL_DrawGizmos.DrawConstSizeSphere(cur, 1f);
                prev = cur;
            }
            //m_Datas.FirstElement
            Gizmos.color = Color.red;
            float seg = 1f / (m_Datas.Count-1);
            float x = 0;
            float y = 0;
            
            for(int i = 0; i < m_Datas.Count; i++) {
                y = seg * i;
                x = MathLib.LinearMapping.GetX(m_Datas, y);
                Vector3 cur = Vector3.Lerp(s, e, CurveFunc(x));
                //Gizmos.DrawLine(prev, cur);
                UCL_DrawGizmos.DrawConstSizeSphere(cur, 1f);
                prev = cur;
            }
            /*
            Gizmos.color = Color.red;
            float seg = 1.0f / (m_Datas.Count - 1);
            for(int i = 0; i < m_Datas.Count; i++) {
                Vector3 cur = Vector3.Lerp(s, e, MathLib.LinearMapping.get);
                UCL_DrawGizmos.DrawConstSizeSphere(cur, 1f);
                prev = cur;
            }
            */
            Gizmos.color = prev_col;
        }
#if UNITY_EDITOR
        [Core.ATTR.UCL_DrawTexture2D(256, 256, TextureFormat.ARGB32, typeof(Core.TextureLib.UCL_Texture2D))]
        public void Editor_DebugCurve(Core.TextureLib.UCL_Texture2D m_CurveTexture) {
            m_CurveTexture.SetColor(Color.black);
            float min = 0, max = 1.2f;
            if(m_Datas.Count > 1) {
                m_CurveTexture.DrawLineAutoFit(delegate (float u) {
                    return u;
                }, Color.red, min, max, true, true);
                m_CurveTexture.DrawLineAutoFit(delegate (float u) {
                    return MathLib.LinearMapping.GetY(m_Datas, u);
                }, Color.green, min, max, false, false);
                m_CurveTexture.DrawLineAutoFit(delegate (float u) {
                    return CurveFunc(u);
                }, Color.yellow, min, max, false, false);
                m_CurveTexture.DrawDot(m_TargetVal,
                    MathLib.LinearMapping.GetY(m_Datas, m_TargetVal)/max, Color.white, 2);
                m_CurveTexture.DrawDot(m_TargetVal,
                    MathLib.LinearMapping.GetX(m_Datas, m_TargetVal) / max,
                    Color.cyan, 2);
            }
        }
        [Range(0,1)] public float m_TargetVal = 0;
        [Range(0, 1)] public float m_RevTargetVal = 0;
        public float CurveFunc(float x) {
            //return (2f * x - 0.75f* x * x - 0.25f*x*x*x);
            return x * x;
        }
        public int m_SegCount = 10;
        [ATTR.UCL_FunctionButton]
        public void InitData() {
            m_Datas = MathLib.LinearMapping.ConvertFunction(CurveFunc, m_SegCount);
        }
    }
#endif
}

