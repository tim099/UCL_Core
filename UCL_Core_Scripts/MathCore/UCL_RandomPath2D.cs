using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib {
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_RandomPath2D : UCL_Path {
        [System.Serializable]
        public class MoveData {
            public float m_Acc = 0.1f;
            public float m_AngelDel = 10f;
            public Vector2 m_Vel = Vector2.zero;
            public Vector3 m_Position;

            public float m_Inertia = 0.85f;
            public float m_VelDec = 0.95f;
        }
        [Range(0, 1000)] public int m_Seed = 0;
        public MoveData m_MoveData;
        public Transform m_StartPosMin;
        public Transform m_StartPosMax;
        UCL.Core.MathLib.UCL_Random m_Rnd;
        public override Vector3 GetPos(float percent) {

            //m_Rnd.OnUnitCircle
            return Vector3.zero;
        }
        public override Rect GetRect(VectorExtensionMethods.Vec3ToVec2 dir = VectorExtensionMethods.Vec3ToVec2.xy) {
            return base.GetRect(dir);
        }
        private void OnValidate() {
            UpdatePath();
        }
        public Color m_PathCol = Color.yellow;
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void UpdatePath() {
            if(m_StartPosMin == null || m_StartPosMax == null) return;

            if(m_PathPoints == null) {
                m_PathPoints = new List<Vector3>();
            }
            m_PathPoints.Clear();
            m_Rnd = new UCL_Random(m_Seed);

            Vector3 min = m_StartPosMin.position;
            Vector3 max = m_StartPosMax.position;
            Vector3 del = max - min;
            var sp = m_Rnd.OnRect(del.x, del.y);
            if(sp.x == 0) {
                m_MoveData.m_Vel = m_Rnd.OnUnitCircle(-0.25f*Mathf.PI, 0.25f * Mathf.PI);
            } else if(sp.x == del.x) {
                m_MoveData.m_Vel = m_Rnd.OnUnitCircle(0.75f * Mathf.PI, 1.25f * Mathf.PI);
            } else if(sp.y == 0) {
                m_MoveData.m_Vel = m_Rnd.OnUnitCircle(0.25f * Mathf.PI, 0.75f*Mathf.PI);
            } else {
                m_MoveData.m_Vel = m_Rnd.OnUnitCircle(1.25f * Mathf.PI, 1.75f * Mathf.PI);
            }
            Vector3 StartPos = min + sp.ToVec3();
            m_MoveData.m_Position = StartPos;
            
            m_PathPoints.Add(StartPos);
            float start_r = m_MoveData.m_Vel.Radius();
            for(int i = 0; i < 2000; i++) {
                m_MoveData.m_Vel *= m_MoveData.m_VelDec;
                float r = m_MoveData.m_Vel.Radius();
                float dr = r - start_r;
                Vector2 acc_vec = Vector2.zero;
                if(dr > 0 && dr < Mathf.PI) {
                    acc_vec = m_Rnd.OnUnitCircle(r - m_MoveData.m_AngelDel * Mathf.Deg2Rad,
                        r + m_MoveData.m_Inertia * m_MoveData.m_AngelDel * Mathf.Deg2Rad);//
                } else {
                    acc_vec = m_Rnd.OnUnitCircle(r - m_MoveData.m_Inertia * m_MoveData.m_AngelDel * Mathf.Deg2Rad,
                        r + m_MoveData.m_AngelDel * Mathf.Deg2Rad);//- m_MoveData.m_AngelDel * Mathf.Deg2Rad
                    
                }
                m_MoveData.m_Vel += acc_vec * m_MoveData.m_Acc;
                m_MoveData.m_Position += m_MoveData.m_Vel.ToVec3();
                if(m_MoveData.m_Position.x < min.x || m_MoveData.m_Position.x > max.x
                    || m_MoveData.m_Position.y < min.y || m_MoveData.m_Position.y > max.y) {
                    break;
                }
                if(i%10 == 0) {
                    m_PathPoints.Add(m_MoveData.m_Position);
                }
            }
            m_PathPoints.Add(m_MoveData.m_Position);

        }
        List<Vector3> m_PathPoints;
        private void OnDrawGizmos() {
#if UNITY_EDITOR
            if(m_StartPosMin == null || m_StartPosMax == null) return;

            var tmp = Gizmos.color;
            if(m_PathPoints == null) {
                m_PathPoints = new List<Vector3>();
            }
            if(m_PathPoints.Count == 0) {
                UpdatePath();
            }
            Gizmos.color = m_PathCol;
            var prev = m_PathPoints[0];
            UCL_DrawGizmos.DrawConstSizeSphere(prev, 1.5f);
            for(int i = 1; i < m_PathPoints.Count; i++) {
                var cur = m_PathPoints[i];
                //UCL_DrawGizmos.DrawConstSizeSphere(m_PathPoints[i%5==0], 1.5f);
                UCL_DrawGizmos.DrawLine(prev, cur);
                prev = cur;
            }

            //m_Rnd.OnRect(width, height);

            Gizmos.color = tmp;
#endif
        }

    }
}