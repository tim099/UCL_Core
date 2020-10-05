using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib.Demo {
    [Core.ATTR.EnableUCLEditor]
    public class UCL_MathLibDemo : MonoBehaviour {
        [Range(-10,10)]public float m_FloatVal = 0;
        public long m_LongVal = 0;

        [UCL.Core.ATTR.UCL_FunctionButton]
        public void RoundToLong() {
            m_LongVal = Lib.RoundToLong(m_FloatVal);
        }

        [UCL.Core.ATTR.UCL_FunctionButton]
        public void FloorToLong() {
            m_LongVal = Lib.FloorToLong(m_FloatVal);
        }

        [UCL.Core.ATTR.UCL_FunctionButton]
        public void CeilToLong() {
            m_LongVal = Lib.CeilToLong(m_FloatVal);
        }
    }
}