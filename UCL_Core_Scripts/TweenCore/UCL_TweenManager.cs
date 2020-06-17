using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Tween {
    public class UCL_TweenManager : UCL_Singleton<UCL_TweenManager> {
        protected List<UCL_Tween> m_Tweens = new List<UCL_Tween>();
        internal protected void Add(UCL_Tween tween) {
            m_Tweens.Add(tween);
        }
    }
}