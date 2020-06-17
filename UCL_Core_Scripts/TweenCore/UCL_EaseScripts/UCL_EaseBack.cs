using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Tween.Ease {
    public class Back : UCL_Ease {
        public float c1 = 1.70158f;
        public override float GetEase(float x) {
            switch(m_Dir) {
                case EaseDir.In: return (c1 + 1) * x * x * x - c1 * x * x;
                case EaseDir.Out: return 1 - ((c1 + 1) * (1 - x) * (1 - x) * (1 - x) - c1 * (1 - x) * (1 - x));
            }
            float c2 = c1 * 1.525f;
            return x < 0.5f
                ? 0.5f * (4 * x * x * ((c2 + 1) * 2 * x - c2))
                : 0.5f * ((4 * (x - 1) * (x - 1)) * ((c2 + 1) * (x * 2 - 2) + c2) + 2);//InOut
        }
    }
}