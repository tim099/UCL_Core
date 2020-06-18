using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace UCL.Core.Tween {
    public class UCL_Sequence : UCL_Tween {
        protected List<UCL_Tween> m_Tweens;
        protected internal override void Init() {
            base.Init();
            m_Tweens = new List<UCL_Tween>();
        }
        static public UCL_Sequence Create() {
            var seq = new UCL_Sequence();
            seq.Init();
            return seq;
        }
        override protected float CheckComplete() {
            if(m_Duration > 0 && m_Timer >= m_Duration) {
                Complete();
                return m_Timer - m_Duration;
            }
            return 0;
        }
        UCL_Sequence() {

        }
    }
}