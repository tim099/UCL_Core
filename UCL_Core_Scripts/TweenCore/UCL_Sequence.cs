using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace UCL.Core.Tween {
    public class UCL_Sequence : UCL_Tween {
        static public UCL_Sequence Create() {
            var seq = new UCL_Sequence();
            UCL_TweenManager.Instance.Add(seq);
            return seq;
        }
        UCL_Sequence() {

        }
    }
}