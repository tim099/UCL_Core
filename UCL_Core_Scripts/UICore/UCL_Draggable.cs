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
            var aCanvas = transform.GetComponentInParent<Canvas>();
            Vector3 aDragPos = eventData.position.ToVec3() + m_DragStartPosition;
            if(aCanvas.renderMode == RenderMode.ScreenSpaceCamera) {
                aDragPos = aCanvas.worldCamera.ScreenToWorldPoint(aDragPos);
            }
            transform.position = aDragPos;
            m_Dragging = true;
        }
        virtual public void OnBeginDrag(PointerEventData eventData) {
            m_OnBeginDragEvent.UCL_Invoke();
            var aCanvas = transform.GetComponentInParent<Canvas>();
            Vector3 aDragPos = transform.position;
            if(aCanvas.renderMode == RenderMode.ScreenSpaceCamera) {
                aDragPos = aCanvas.worldCamera.WorldToScreenPoint(aDragPos);
            }
            m_DragStartPosition = aDragPos - eventData.position.ToVec3();

            m_Dragging = true;
        }
        virtual public void OnEndDrag(PointerEventData eventData) {
            m_OnEndDragEvent.UCL_Invoke();
            m_Dragging = false;
        }
        #endregion
    }
}