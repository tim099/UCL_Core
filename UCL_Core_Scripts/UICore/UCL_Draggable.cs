using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace UCL.Core.UI
{
    public class UCL_Draggable : MonoBehaviour, IDragHandler, IBeginDragHandler, IEndDragHandler
    {

        public UCL.Core.UCL_Event m_OnBeginDragEvent = null;
        public UCL.Core.UCL_Event m_OnEndDragEvent = null;
        public bool IsDragging { get { return m_Dragging; } }
        protected Vector3 m_DragStartPosition = Vector3.zero;
        protected bool m_Dragging = false;

        #region drag

        virtual public void OnDrag(PointerEventData eventData) {
            var canvas = transform.GetComponentInParent<Canvas>();
            Vector3 drag_pos = eventData.position.ToVec3() + m_DragStartPosition;
            if(canvas.renderMode == RenderMode.ScreenSpaceCamera) {
                drag_pos = canvas.worldCamera.ScreenToWorldPoint(drag_pos);
            }
            transform.position = drag_pos;
            m_Dragging = true;
        }
        virtual public void OnBeginDrag(PointerEventData eventData) {
            m_OnBeginDragEvent.UCL_Invoke();
            var canvas = transform.GetComponentInParent<Canvas>();
            Vector3 drag_pos = transform.position;
            if(canvas.renderMode == RenderMode.ScreenSpaceCamera) {
                drag_pos = canvas.worldCamera.WorldToScreenPoint(drag_pos);
            }
            m_DragStartPosition = drag_pos - eventData.position.ToVec3();

            m_Dragging = true;
        }
        virtual public void OnEndDrag(PointerEventData eventData) {
            m_OnEndDragEvent.UCL_Invoke();
            m_Dragging = false;
        }
        #endregion
    }
}