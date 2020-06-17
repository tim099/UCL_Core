using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Tween.Ease {
    public class Circ : UCL_Ease {
        public override float GetEase(float x) {
            switch(m_Dir) {
                case EaseDir.In: return 1 - Mathf.Sqrt(1 - x * x) ;
                case EaseDir.Out: return Mathf.Sqrt(1 - (x - 1) * (x - 1));
            }
            return x < 0.5f ? 
                      0.5f * (1 - Mathf.Sqrt(1 - 4 * x * x))
                    : 0.5f * (Mathf.Sqrt(1 - 4*(x-1)*(x-1)) + 1);//InOut
        }
    }
}