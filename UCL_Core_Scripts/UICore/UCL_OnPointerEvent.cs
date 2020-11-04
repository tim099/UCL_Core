using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace UCL.Core.UI
{
    public class UCL_OnPointerEvent : MonoBehaviour, IPointerExitHandler, IPointerEnterHandler
    {
        public UCL.Core.UCL_Event m_OnPointerEnter = null;
        public UCL.Core.UCL_Event m_OnPointerExit = null;
        public bool m_Enter = false;
        virtual public void OnPointerEnter(PointerEventData eventData) {
            m_OnPointerEnter.UCL_Invoke();
            m_Enter = true;
        }
        virtual public void OnPointerExit(PointerEventData eventData) {
            m_OnPointerExit.UCL_Invoke();
            m_Enter = false;
        }
    }
}