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
        protected float m_StartTime = 0;

        virtual internal protected void Init(float _StartTime) {
            m_StartTime = _StartTime;
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

