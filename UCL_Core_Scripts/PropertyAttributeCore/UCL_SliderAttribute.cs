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
            GUILayout.BeginHorizontal();
            GUILayout.Label(iDisplayName, GUILayout.ExpandWidth(false));
            int aResult = (int)GUILayout.HorizontalSlider(iVal, m_LeftValue, m_RightValue, GUILayout.ExpandWidth(true));
            if (aResult != iVal) iDic.Clear();
            aResult = UCL.Core.UI.UCL_GUILayout.IntField(aResult, iDic, GUILayout.MinWidth(80), GUILayout.ExpandWidth(false));
            int aMaxValue = System.Math.Max(m_LeftValue, m_RightValue);
            int aMinValue = System.Math.Min(m_LeftValue, m_RightValue);
            if (aResult > aMaxValue) aResult = aMaxValue;
            else if (aResult < aMinValue) aResult = aMinValue;

            GUILayout.EndHorizontal();
            return aResult;
        }
    }
}