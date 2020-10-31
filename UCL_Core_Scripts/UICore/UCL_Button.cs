using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UCL.Core.UI {
    public class UCL_Button : MonoBehaviour, IPointerDownHandler, IPointerExitHandler, IPointerUpHandler, IPointerEnterHandler,
        IDragHandler, IBeginDragHandler, IEndDragHandler {
        public enum Transition {
            None = 0,
            ColorTint,
        }
        #region CreateButton
        static public UCL_Button Create(Transform parent) {
            var but = UCL.Core.GameObjectLib.Create<UCL_Button>("UCL_Button", parent);
            var obj = but.gameObject;
            var img = obj.AddComponent<Image>();
            {
                var rect = obj.GetComponent<RectTransform>();
                if(rect == null) rect = obj.AddComponent<RectTransform>();
                rect.sizeDelta = new Vector2(256, 64);
#if UNITY_EDITOR
                img.sprite = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
                //UnityEditor.EditorGUIUtility.IconContent("[texturename]");
#endif
                img.type = Image.Type.Sliced;
                but.m_Image = img;
            } 

            {
                var text = UCL.Core.GameObjectLib.Create<Text>("Text", but.transform);
                text.text = "Button";
                text.color = Color.black;
                text.resizeTextForBestFit = true;
                text.resizeTextMaxSize = 50;
                text.alignment = TextAnchor.MiddleCenter;
                var rect = text.gameObject.GetComponent<RectTransform>();
                if(rect == null) rect = text.gameObject.AddComponent<RectTransform>();
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.sizeDelta = Vector2.zero;

                but.m_Text = text;
            }
            return but;
        }
        #endregion
        public bool m_Pressed { get; protected set; } = false;
        public bool m_Enter { get; protected set; } = false;
        public bool m_Dragging { get; protected set; } = false;

        public Transition m_Transition = Transition.ColorTint;
        public bool m_Draggable = false;
        
        public float m_PressedTime = 0;

        public Image m_Image = null;
        public Text m_Text = null;
        public Color m_NormalColor = Color.white;
        public Color m_PressedColor = Color.gray;
        public UCL.Core.UCL_Event m_OnClick = null;
        public UCL.Core.UCL_Event m_OnPointerDown = null;
        public UCL.Core.UCL_Event m_OnPointerUp = null;
        public UCL.Core.UCL_FloatEvent m_OnPressed = null;

        virtual public void OnPointerEnter(PointerEventData eventData) {
            m_Enter = true;
            StateUpdate();
        }
        virtual public void OnPointerExit(PointerEventData eventData) {
            m_Enter = false;
            m_PressedTime = 0;
            StateUpdate();
        }

        virtual public void OnPointerDown(PointerEventData eventData) {
            m_OnPointerDown.UCL_Invoke();
            m_Pressed = true;
            m_PressedTime = 0;

            StateUpdate();
        }
        virtual public void OnPointerUp(PointerEventData eventData) {
            m_OnPointerUp.UCL_Invoke();
            if(m_Enter && m_Pressed) {
                m_OnClick.UCL_Invoke();
            }
            m_Pressed = false;
            m_PressedTime = 0;
            StateUpdate();
        }
        #region drag
        protected Vector3 m_DragStartPosition = Vector3.zero;
        virtual public void OnDrag(PointerEventData eventData) {
            if(!m_Draggable) return;
            //Debug.LogWarning("OnDrag:"+ eventData.position);
            var canvas = transform.GetComponentInParent<Canvas>();
            Vector3 drag_pos = eventData.position.ToVec3() + m_DragStartPosition;
            if(canvas.renderMode == RenderMode.ScreenSpaceCamera) {
                drag_pos = canvas.worldCamera.ScreenToWorldPoint(drag_pos);
            }
            transform.position = drag_pos;
            m_Dragging = true;
            StateUpdate();
        }
        virtual public void OnBeginDrag(PointerEventData eventData) {
            if(!m_Draggable) return;
            //Debug.LogWarning("OnBeginDrag");
            var canvas = transform.GetComponentInParent<Canvas>();
            Vector3 drag_pos = transform.position;
            if(canvas.renderMode == RenderMode.ScreenSpaceCamera) {
                drag_pos = canvas.worldCamera.WorldToScreenPoint(drag_pos);
            }
            m_DragStartPosition = drag_pos - eventData.position.ToVec3();

            m_Dragging = true;
            StateUpdate();
        }
        virtual public void OnEndDrag(PointerEventData eventData) {
            if(!m_Draggable) return;

            m_Dragging = false;
            //Debug.LogWarning("OnEndDrag");
            StateUpdate();
        }
        #endregion

        virtual protected void StateUpdate() {
            switch(m_Transition) {
                case Transition.ColorTint: {
                        if(m_Image != null) {
                            if(!m_Pressed) {
                                m_Image.color = m_NormalColor;
                            } else {
                                m_Image.color = m_PressedColor;
                            }
                        }
                        break;
                    }
            }

            //Debug.LogWarning(++m_Times+"m_Pressed:" + m_PressedTime.ToString("0.0") + ",m_Enter:" + m_Enter);
            if(m_Text != null) {
                //m_Text.text = "p:" + m_PressedTime.ToString("0.0") + ",E:" + (m_Enter ? "T" : "F") + ",D:" + (m_Dragging ? "T" : "F");
            }

        }
        virtual protected void Update() {
            if(m_Pressed && m_Enter) {
                m_PressedTime += Time.deltaTime;
                m_OnPressed.UCL_Invoke(m_PressedTime);
            }
            if(m_Text != null) {
                //m_Text.text = "p:" + m_PressedTime.ToString("0.0") + ",E:" + (m_Enter ? "T" : "F") + ",D:" + (m_Dragging ? "T" : "F");
            }
        }
    }
}