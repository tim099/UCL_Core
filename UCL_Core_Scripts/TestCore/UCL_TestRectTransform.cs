using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UCL.Core;
namespace UCL.Core.TestLib
{
    [ATTR.EnableUCLEditor]
    public class UCL_TestRectTransform : MonoBehaviour
    {
        public RectTransform m_Target;
        public Transform m_A, m_B;
        [UCL.Core.ATTR.UCL_DrawString]
        public string AnchoredPosition() {
            if(m_Target == null) return "";
            return m_Target.anchoredPosition.ToString();
        }
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void Test() {
            m_Target.SetBetweenTwoPoint(m_A.position.XY(), m_B.position.XY());
            Vector2 del = m_A.position - m_B.position;
            float x = 0.5f * (m_A.position.x + m_B.position.x);
            float y = 0.5f * (m_A.position.y + m_B.position.y);
            float dis = del.magnitude;
            m_Target.sizeDelta = new Vector2(dis / m_Target.lossyScale.x , m_Target.sizeDelta.y);
            m_Target.position = new Vector3(x, y,m_A.position.z);
            m_Target.rotation = Quaternion.Euler(0, 0, Mathf.Rad2Deg*Mathf.Atan2(del.y, del.x));
        }
    }
}