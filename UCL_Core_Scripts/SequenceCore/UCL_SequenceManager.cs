using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Tween {
    public class UCL_SequenceManager : UCL_Singleton<UCL_SequenceManager> {
        static public UCL_Sequence Sequence() {
            return UCL_Sequence.Create();
        }

        protected List<UCL_Tween> m_Tweens = new List<UCL_Tween>();

        protected float m_Time = 0;
        public void InitTime(float _Time) {
            m_Time = _Time;
        }

        internal protected void Add(UCL_Tween tween) {
            m_Tweens.Add(tween);
        }

        private void Update() {

        }
    }
}