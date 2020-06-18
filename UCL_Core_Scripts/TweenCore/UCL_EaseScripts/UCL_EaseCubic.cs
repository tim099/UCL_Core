using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Tween.Ease {
    public class Cubic : UCL_Ease {
        override public EaseClass GetClass() {
            return EaseClass.Cubic;
        }
        public override float GetEase(float x) {
            switch(m_Dir) {
                case EaseDir.In: return x * x * x;
                case EaseDir.Out: return 1 - (1 - x) * (1 - x) * (1 - x);
            }
            return x < 0.5 ? 4 * x * x * x : 1 - 0.5f * Mathf.Pow(-2 * x + 2, 3);//InOut
        }
    }
}