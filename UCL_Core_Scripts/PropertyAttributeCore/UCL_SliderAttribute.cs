using UnityEngine;

namespace UCL.Core.PA {
    public class UCL_IntSliderAttribute : PropertyAttribute {
        public readonly int m_LeftValue, m_RightValue;
        public UCL_IntSliderAttribute(int iLeftValue, int iRightValue) {
            m_LeftValue = iLeftValue;
            m_RightValue = iRightValue;
        }
        public int OnGUI(string iDisplayName, int iVal, UCL_ObjectDictionary iDic)
        {
            return UCL.Core.UI.UCL_GUILayout.Slider(iDisplayName, iVal, m_LeftValue, m_RightValue, iDic);
        }
    }
    public class UCL_SliderAttribute : PropertyAttribute
    {
        public readonly float m_RightValue, m_LeftValue;
        public UCL_SliderAttribute(float iLeftValue, float iRightValue)
        {
            m_RightValue = iRightValue;
            m_LeftValue = iLeftValue;
        }
        public float OnGUI(string iDisplayName, float iVal, UCL_ObjectDictionary iDic)
        {
            return UCL.Core.UI.UCL_GUILayout.Slider(iDisplayName, iVal, m_LeftValue, m_RightValue, iDic);
        }
    }
}