using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Tween {
    public static class Lib {
        static public UCL_Sequence Sequence() {
            var seq = UCL_Sequence.Create();
            UCL_TweenManager.Instance.Add(UCL_Sequence.Create());
            return seq;
        }
    }
}