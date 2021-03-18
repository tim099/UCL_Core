using UnityEngine;

namespace UCL.Core.PA {
    public class UCL_IntSliderAttribute : PropertyAttribute {
        public readonly int m_RightValue, m_LeftValue;
        public UCL_IntSliderAttribute(int left_value, int right_value) {
            m_RightValue = right_value;
            m_LeftValue = left_value;
        }
    }
}