using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Tween.Ease {
    public class Quint : UCL_Ease {
        override public EaseClass GetClass() {
            return EaseClass.Quint;
        }
        public override float GetEase(float x) {
            switch(m_Dir) {
                case EaseDir.In: return x * x * x * x * x;
                case EaseDir.Out: return 1 - (1 - x) * (1 - x) * (1 - x) * (1 - x) * (1 - x);
            }
            return x < 0.5 ? 16 * x * x * x * x * x : 1 - 0.5f * Mathf.Pow(-2 * x + 2, 5);//InOut
        }
    }
}