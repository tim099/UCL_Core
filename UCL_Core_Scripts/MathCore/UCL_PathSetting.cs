using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib {
    public class UCL_PathSetting : UCL_Path {

        public UCL_Path m_Path = null;
        public VectorExtensionMethods.TransformMode m_TransformMode = VectorExtensionMethods.TransformMode.WorldSpace;

        [Header("Inverse the path")] public bool m_Inverse = false;

        public override Vector3 GetPos(float percent) {
            if(m_Path == null) return transform.position;

            if(m_Inverse) percent = 1 - percent;

            Vector3 pos = m_Path.GetPos(percent);

            pos = transform.TransformPoint(pos, m_TransformMode);


            return pos;
        }
        public override Rect GetRect(VectorExtensionMethods.Vec3ToVec2 dir = VectorExtensionMethods.Vec3ToVec2.xy) {
            if(m_Path == null) return Rect.zero;
            return m_Path.GetRect();
        }
        public override float GetPathLength() {
            if(m_Path == null) return 0;
            return m_Path.GetPathLength();
        }
        public override void UpdatePathPoint() {
            if(m_Path == null) return;
            m_Path.UpdatePathPoint();
        }
        #region OnDrawGizmos

        [Header("Editor DrawGizmos Setting")]//m_DisPointCount
#pragma warning disable 0219
        [SerializeField] [Range(-0.2f, 1.2f)] float m_DemoPoint = 0f;
#pragma warning restore 0219
        [SerializeField] Color m_DemoPointColor = Color.black;
        [SerializeField] int m_DisPointCount = 0;
        [SerializeField] Color m_PathCol = Color.green;
        [Range(0.5f, 5.0f)] public float m_PointSize = 1.5f;
        [SerializeField] bool f_OnlyDrawGizmosOnSelected = true;
        virtual protected void DrawGizmos() {
#if UNITY_EDITOR
            var tmp = Gizmos.color;
            {
                Vector3 prev = GetPos(0);
                Gizmos.color = m_PathCol;
                int m_SmoothAmount = 100;
                for(int i = 1; i <= m_SmoothAmount; i++) {
                    float pm = (float)i / m_SmoothAmount;
                    Vector3 currPt = GetPos(pm);
                    Gizmos.DrawLine(currPt, prev);
                    prev = currPt;
                }
            }

            if(m_DisPointCount > 0) {
                float seg = 1.0f / m_DisPointCount;
                for(int i = 0; i < m_DisPointCount; i++) {
                    var a = GetPos(i * seg);
                    //var a = m_GetPoint ? GetPoint(i * seg) : GetPos(i * seg);
                    UCL_DrawGizmos.DrawConstSizeSphere(a, 0.5f * m_PointSize);
                }
                /*
                for(int i = 0; i < m_WorldSpacePoints.Length - 1; i++) {
                    Gizmos.DrawLine(m_WorldSpacePoints[i], m_WorldSpacePoints[i + 1]);
                }
                */
            } else {
                Gizmos.color = m_DemoPointColor;
                UCL_DrawGizmos.DrawConstSizeSphere(GetPos(m_DemoPoint), 0.75f * m_PointSize);
            }

            Gizmos.color = tmp;
#endif
        }
        private void OnDrawGizmosSelected() {
            if(f_OnlyDrawGizmosOnSelected) DrawGizmos();
        }
        private void OnDrawGizmos() {
            if(!f_OnlyDrawGizmosOnSelected) DrawGizmos();
        }
        #endregion
    }
}