using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Tween.Ease {
    public class Quad : UCL_Ease {
        override public EaseClass GetClass() {
            return EaseClass.Quad;
        }
        public override float GetEase(float x) {
            switch(m_Dir) {
                case EaseDir.In: return x * x;
                case EaseDir.Out: return 1 - (1 - x) * (1 - x);
            }
            return x < 0.5 ? 2 * x * x : 1 - 0.5f * Mathf.Pow(-2 * x + 2, 2);//InOut
        }
    }
}