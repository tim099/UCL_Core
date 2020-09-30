using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace UCL.Core.UI {
    public class UCL_Button : MonoBehaviour, IPointerDownHandler, IPointerExitHandler, IPointerUpHandler, IPointerEnterHandler {
        public bool m_Pressed = false;
        public bool m_Enter = false;

        public void OnPointerEnter(PointerEventData eventData) {
            m_Enter = true;
            StateUpdate();
        }
        public void OnPointerExit(PointerEventData eventData) {
            m_Enter = false;
            StateUpdate();
        }

        public void OnPointerDown(PointerEventData eventData) {
            m_Pressed = true;
            StateUpdate();
        }
        public void OnPointerUp(PointerEventData eventData) {
            m_Pressed = false;
            StateUpdate();
        }
        int m_Times = 0;
        virtual protected void StateUpdate() {
            Debug.LogWarning(++m_Times+"m_Pressed:" + m_Pressed + ",m_Enter:" + m_Enter);
        }
    }
}