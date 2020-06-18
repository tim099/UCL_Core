using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Tween {
    public class UCL_TweenTimeManager {
        List<UCL_Tween> m_Tweens;

        UCL_TweenTimeManager() {
            m_Tweens = new List<UCL_Tween>();
        }
        public static UCL_TweenTimeManager Create() {
            return new UCL_TweenTimeManager();
        }
        public void Init() {

        }

        internal void Add(UCL_Tween tween) {
            m_Tweens.Add(tween);
        }

        internal void TimeUpdate(float delta_time) {
            for(int i = 0; i < m_Tweens.Count; i++) {
                var tween = m_Tweens[i];
                tween.TimeUpdate(delta_time);
            }
        }

        private void Update() {

        }
    }
}