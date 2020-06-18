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
        /// <summary>
        /// true if Tween is Complete or Killed
        /// </summary>
        public bool End {
            get {
                return m_End;
            }
        }


        protected bool m_Completed = false;
        protected bool m_End = false;
        protected bool m_Paused = false;
        protected System.Action m_CompleteAct = null;
        protected float m_Timer = 0;
        protected float m_Duration = 0;
        
        virtual internal protected void Init() {
            m_Completed = false;
            m_End = false;
            m_Timer = 0;
            m_Duration = 0;
            m_CompleteAct = null;
            m_Paused = false;
            InitTween();
        }
        virtual protected void InitTween() { }

        /// <summary>
        /// return value is the remain time of update
        /// </summary>
        /// <param name="time_delta"></param>
        /// <returns></returns>
        virtual internal protected float TimeUpdate(float time_delta) {
            if(m_End || m_Paused) return 0;

            m_Timer += time_delta;

            TimeUpdateAction();

            return CheckComplete();
        }
        virtual protected void TimeUpdateAction() {

        }
        virtual protected float CheckComplete() {
            if(m_Duration > 0 && m_Timer >= m_Duration) {
                Complete();
                return m_Timer - m_Duration;
            }
            return 0;
        }
        virtual public void Pause() {
            m_Paused = true;
        }
        virtual public void Resume() {
            m_Paused = false;
        }
        virtual public void Kill(bool compelete = false) {
            if(compelete) Complete();
            m_End = true;
        }
        virtual internal protected void Complete() {
            if(m_Completed) return;
            m_CompleteAct?.Invoke();
            m_Completed = true;
            m_End = true;
        }
        virtual public void OnComplete(System.Action _CompleteAct) {
            m_CompleteAct = _CompleteAct;
        }
    }
}

