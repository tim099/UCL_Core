using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace UCL.Core.Tween {
    public class UCL_Tween {
        public bool Completed {
            get {
                return m_Completed;
            }
        }
        protected bool m_Completed = false;
        protected System.Action m_CompleteAct = null;
        protected float m_Timer = 0;
        protected float m_EndTime = 0;
        virtual internal protected void Init() {
            m_Completed = false;
            m_Timer = 0;
        }
        virtual internal protected void TimeUpdate(float time_delta) {
            if(m_Completed) return;

            m_Timer += time_delta;
            if(m_Timer >= m_EndTime) {
                Complete();
            }
        }
        virtual internal protected void Complete() {
            m_CompleteAct?.Invoke();
            m_Completed = true;
        }
        virtual public void OnComplete(System.Action _CompleteAct) {
            m_CompleteAct = _CompleteAct;
        }
    }
}

