using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace UCL.Core.UI
{
    [UCL.Core.ATTR.EnableUCLEditor]
    [RequireComponent(typeof(ScrollRect))]
    public class UCL_ScrollController : MonoBehaviour
    {
        public enum TriggerAction
        {
            Null = 0,
            MoveToTop = 1,
            MoveToButton = 1<<1,
        }

        [UCL.Core.PA.UCL_EnumMask] public ComponentState m_TriggerState;
        public TriggerAction m_TriggerAction;
        public ScrollRect m_Rect = null;
        void Reset() {
#if UNITY_EDITOR
            if(m_Rect == null) m_Rect = GetComponent<ScrollRect>();
#endif
        }
        void OnEnable() {
            Debug.LogWarning("OnEnable()");
            if(m_Rect == null) return;
            if((m_TriggerState & ComponentState.OnEnable) != 0) {
                Debug.LogWarning("m_TriggerState & ComponentState.OnEnable) != 0");
                TriggerAct();
            }
        }

        [UCL.Core.ATTR.UCL_FunctionButton]
        public void TriggerAct() {
            switch(m_TriggerAction) {
                case TriggerAction.MoveToTop: {
                        Debug.LogWarning("MoveToTop");
                        m_Rect.ToTop();
                        break;
                    }
                case TriggerAction.MoveToButton: {
                        Debug.LogWarning("MoveToButton");
                        m_Rect.ToBottom();
                        break;
                    }
            }
        }
    }
}