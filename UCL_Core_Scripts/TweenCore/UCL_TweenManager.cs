using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Tween {
    public class UCL_TweenManager : UCL_Singleton<UCL_TweenManager> {
        UCL_TweenTimeManager m_TimeManager;
        bool m_Inited = false;

        private void Awake() {
            Init();
        }
        public void Init() {
            if(m_Inited) return;

            m_Inited = true;
            m_TimeManager = UCL_TweenTimeManager.Create();
        }

        internal void Add(UCL_Tween tween) {
            if(!m_Inited) Init();

            m_TimeManager.Add(tween);
        }

        private void Update() {
            if(!m_Inited) Init();

            m_TimeManager.TimeUpdate(Time.deltaTime);
        }
    }
}