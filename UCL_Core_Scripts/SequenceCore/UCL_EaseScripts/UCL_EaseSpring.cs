using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Tween.Ease {
    [System.Serializable]
    public class Spring : UCL_Ease {
        public float m_Base = 0.5f;
        public float m_Pow3 = 3f;
        public float m_PowRate = 1.2f;
        public float m_Spring = 1.3f;
        public float m_Scale = 1.0f;// 1.0f / 1.531757f;
        public override float GetEase(float x) {
            x = Mathf.Clamp01(x);
            return m_Scale * 
                (Mathf.Sin(x * Mathf.PI * (m_Base + m_Pow3 * x * x * x)) *
                Mathf.Pow(1f - x, m_PowRate) + x) *
                (1f + (m_Spring * (1f - x)));
        }
    }
}