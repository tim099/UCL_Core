using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Misc
{
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_EventTrigger : MonoBehaviour
    {
        public enum TriggerOn
        {
            None = 0,//No Auto Invoke StartTween()
            OnStart,//Invoke StartTween() On Start()
            OnEnable,//Invoke StartTween() On OnEnable()
            OnAwake,//Invoke StartTween() On Awake()
        }
        public TriggerOn m_TriggerOn = TriggerOn.OnStart;
        public UnityEngine.Events.UnityEvent m_Events;

        private void Awake()
        {
            if (m_TriggerOn == TriggerOn.OnAwake) TriggerEvent();
        }
        private void Start()
        {
            if (m_TriggerOn == TriggerOn.OnStart) TriggerEvent();
        }
        private void OnEnable()
        {
            if (m_TriggerOn == TriggerOn.OnEnable) TriggerEvent();
        }
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void TriggerEvent()
        {
            m_Events.Invoke();
        }
    }
}