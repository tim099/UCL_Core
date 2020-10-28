using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI {
    [RequireComponent(typeof(RectTransform))]
    public class UCL_RectTransformCollider : UCL.Core.Physic.UCL_Collider2D {
        public RectTransform m_Transform = null;

        public UCL.Core.UCL_Event m_OnMouseEnterEvent = null;
        public UCL.Core.UCL_Event m_OnMouseExitEvent = null;
        protected bool m_MouseEntered = false;
        public override bool ContainPoint(Vector2 point) {
            if(m_Transform == null) {
                m_Transform = GetComponent<RectTransform>();
                if(m_Transform == null) return false;
            }
            return m_Transform.ContainPoint(point);
        }
        virtual public void OnDragEnterEvent() {

        }
        virtual protected void Update() {
            if(!m_OnMouseEnterEvent.IsNullOrEmpty() || !m_OnMouseExitEvent.IsNullOrEmpty()) {
                var val = ContainPoint(Input.mousePosition);
                if(!m_MouseEntered) {
                    if(val) {
                        m_OnMouseEnterEvent.Invoke();
                        m_MouseEntered = true;
                    }
                } else {
                    if(!val) {
                        m_OnMouseExitEvent.Invoke();
                        m_MouseEntered = false;
                    }
                }

            }
        }
    }
}