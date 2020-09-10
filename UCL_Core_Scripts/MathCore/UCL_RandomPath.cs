using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib {
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_RandomPath : UCL_Path {
        [System.Serializable]
        public struct MoveData {
            //public Vector3 m_Acc;
            public Vector3 m_Vel;
            public Vector3 m_Position;
        }
        public int m_Seed = 0;
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
            if(m_PathPoints.Count == 0) {
                m_Rnd = new UCL_Random(m_Seed);

                Vector3 min = m_StartPosMin.position;
                Vector3 max = m_StartPosMax.position;
                Vector3 del = max - min;
                Vector3 StartPos = min + m_Rnd.OnRect(del.x, del.y).ToVec3();
                m_PathPoints.Add(StartPos);
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