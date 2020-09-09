using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib {
    public class UCL_RandomPath : UCL_Path {
        public int m_Seed = 0;
        public Transform m_StartPosMin;
        public Transform m_StartPosMax;
        UCL.Core.MathLib.UCL_Random m_Rnd;
        public override Vector3 GetPos(float percent) {
            m_Rnd = new UCL_Random(m_Seed);
            //m_Rnd.OnUnitCircle
            return Vector3.zero;
        }
        public override Rect GetRect(VectorExtensionMethods.Vec3ToVec2 dir = VectorExtensionMethods.Vec3ToVec2.xy) {
            return base.GetRect(dir);
        }
        private void OnDrawGizmos() {
            
        }
    }
}