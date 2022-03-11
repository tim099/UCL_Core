using UnityEngine;

namespace UCL.Core.PA {
    public class UCL_IntSliderAttribute : PropertyAttribute {
        public readonly int m_LeftValue, m_RightValue;
        public UCL_IntSliderAttribute(int iLeftValue, int iRightValue) {
            m_LeftValue = iLeftValue;
            m_RightValue = iRightValue;
        }
    }
}