using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.MathLib.Demo {
    [ATTR.EnableUCLEditor]
    public class UCL_LinearMappingDemo : MonoBehaviour {
        public List<float> m_Datas;
        [Range(0,1)]public float m_Val;
        public float m_OutPosition;
        public float m_OutValue;
        private void OnValidate() {
            m_OutPosition = MathLib.LinearMapping.GetPosition(m_Datas, m_Val);
            m_OutValue = MathLib.LinearMapping.GetValue(m_Datas, m_Val);
        }
        [ATTR.UCL_FunctionButton]
        public void InitData() {
            m_Datas = new List<float>();
            for(int i = 0; i < 10; i++) {
                m_Datas.Add(0.1f * i);
            }
        }
    }
}

