﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.UI {
    [RequireComponent(typeof(RectTransform))]
    public class UCL_RectTransformCollider : UCL.Core.Physic.UCL_Collider2D {
        public int m_ID = 0;
        public RectTransform m_Transform = null;

        public UCL.Core.UCL_Event m_OnMouseEnterEvent = null;
        public UCL.Core.UCL_Event m_OnMouseExitEvent = null;
        public bool IsMouseEntered {get {return m_MouseEntered;} }
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
                Vector3 select_pos = Input.mousePosition;
                var val = ContainPoint(select_pos);
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