using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Tween.Ease {
    [System.Serializable]
    public class Bounce : UCL_Ease {
        public float m_T1 = 1f;
        public float m_T2 = 2f;
        public float m_T3 = 2.5f;
        public float m_N = 7.5625f;
        public float m_D = 2.75f;
        override public EaseClass GetClass() {
            return EaseClass.Bounce;
        }
        public override float GetEase(float x) {
            switch(m_Dir) {
                case EaseDir.In:return (1.0f - GetEaseOut(1.0f - x));
                case EaseDir.Out:return GetEaseOut(x);
            }
            return x < 0.5 ? 0.5f * (1 - GetEaseOut(1 - 2.0f*x)) : 0.5f * (1 + GetEaseOut(2.0f * x - 1));//InOut
        }
        public float GetEaseOut(float x) {
            if(x < m_T1 / m_D) {
                return m_N * x * x;
            } else if(x < m_T2 / m_D) {
                return m_N * (x -= (0.75f * m_T2) / m_D) * x + 0.75f;
            } else if(x < m_T3 / m_D) {
                return m_N * (x -= (0.9f * m_T3) / m_D) * x + 0.9375f;
            }
            return m_N * (x -= (1.05f * m_T3) / m_D) * x  + 0.984375f;
        }
    }
}