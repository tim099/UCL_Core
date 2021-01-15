using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib {
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_RandomPath : UCL_Path {
        [System.Serializable]
        public class MoveData {
            public float m_Acc = 0.1f;
            public Vector3 m_Vel = Vector3.zero;
            public Vector3 m_Position;

            public float m_VelDec = 0.99f;
        }
        [Range(0,1000)]public int m_Seed = 0;
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
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void UpdatePath() {
            if(m_PathPoints == null) {
                m_PathPoints = new List<Vector3>();
            }
            m_PathPoints.Clear();
            m_Rnd = new UCL_Random(m_Seed);

            Vector3 min = m_StartPosMin.position;
            Vector3 max = m_StartPosMax.position;
            Vector3 del = max - min;
            
            Vector3 StartPos = min + m_Rnd.OnRect(del.x, del.y).ToVec3();
            m_MoveData.m_Position = StartPos;
            m_PathPoints.Add(StartPos);

            for(int i = 0; i < 10000; i++) {
                m_MoveData.m_Vel += m_Rnd.OnUnitCircle().ToVec3() * m_MoveData.m_Acc;
                m_MoveData.m_Position += m_MoveData.m_Vel;
            }
            
        }
        List<Vector3> m_PathPoints;
        private void OnDrawGizmos() {
#if UNITY_EDITOR
            var tmp = Gizmos.color;
            if(m_PathPoints == null) {
                m_PathPoints = new List<Vector3>();
            }
            if(m_PathPoints.Count == 0) {
                UpdatePath();
            }
            UCL_DrawGizmos.DrawConstSizeSphere(m_PathPoints[0], 1.5f);
            //m_Rnd.OnRect(width, height);

            Gizmos.color = tmp;
#endif
        }

    }

}