using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Tween.Ease {
    [System.Serializable]
    public class Elastic : UCL_Ease {
        public float m_C = (2 * Mathf.PI) / 3;
        public override float GetEase(float value) {
            switch(m_Dir) {
                case EaseDir.In: return (1.0f - GetEaseOut(1.0f - value));
                case EaseDir.Out: return GetEaseOut(value);
            }
            return value < 0.5 ? 0.5f * (1 - GetEaseOut(1 - 2.0f * value)) : 0.5f * (1 + GetEaseOut(2.0f * value - 1));//InOut
        }
        public float GetEaseOut(float x) {
            //if(x == 0 || x == 1) return -x;
            if(x >= 1) return 1;
            if(x <= 0) return 0;
            return 1 + Mathf.Pow(2, -10 * x) * Mathf.Sin((x * 10 - 0.75f) * m_C);
        }
    }
}