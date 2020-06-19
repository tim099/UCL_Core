using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Tween {
#if UNITY_EDITOR
    [ATTR.EnableUCLEditor]
#endif
    public class UCL_TweenManager : UCL_Singleton<UCL_TweenManager> {
        public int TweenCount {
            get {
                if(m_TimeManager == null) return 0;
                return m_TimeManager.TweenCount;
            }
        }
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
#if UNITY_EDITOR
        [ATTR.UCL_FunctionButton("KillAllTweens(complete = false)", false)]
        [ATTR.UCL_FunctionButton("KillAllTweens(complete = true)",true)]
#endif
        public void KillAllTweens(bool complete = false) {
            if(!m_Inited) Init();
            m_TimeManager.KillAllTweens(complete);
        }
        private void Update() {
            if(!m_Inited) Init();

            m_TimeManager.TimeUpdate(Time.deltaTime);
        }
    }
}