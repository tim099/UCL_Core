using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UCL.Core.UI {
    public class UCL_Button : MonoBehaviour, IPointerDownHandler, IPointerExitHandler, IPointerUpHandler, IPointerEnterHandler {
        static Color DisableCol = new Color(0.5f, 0.5f, 0.5f, 0.5f);
        public enum Transition {
            None = 0,
            ColorTint,
        }
        #region CreateButton
        static public UCL_Button Create(Transform parent) {
            var aBut = UCL.Core.GameObjectLib.Create<UCL_Button>("UCL_Button", parent);
            var aObj = aBut.gameObject;
            var aImg = aObj.AddComponent<Image>();
            {
                var rect = aObj.GetComponent<RectTransform>();
                if(rect == null) rect = aObj.AddComponent<RectTransform>();
                rect.sizeDelta = new Vector2(256, 64);
                aImg.type = Image.Type.Sliced;
                aBut.m_Image = aImg;
            }
            {
                var text = UCL.Core.GameObjectLib.Create<Text>("Text", aBut.transform);
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

                aBut.m_Text = text;
            }
            aBut.m_Image.sprite = UCL.Core.EditorLib.AssetDatabaseMapper.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            return aBut;
        }
        #endregion
        public bool Interactable
        {
            get { return m_Interactable; }
            set
            {
                m_Interactable = value;
                StateUpdate();
            }
        }
        public bool Pressed { get; protected set; } = false;
        public bool Enter { get; protected set; } = false;
        [SerializeField] protected bool m_Interactable = true;

        public Transition m_Transition = Transition.ColorTint;
        
        public float m_PressedTime = 0;

        public Image m_Image = null;
        public Text m_Text = null;
        [UCL.Core.PA.Conditional("m_Transition", false, Transition.ColorTint)] public Color m_NormalColor = Color.white;
        [UCL.Core.PA.Conditional("m_Transition", false, Transition.ColorTint)] public Color m_PressedColor = Color.gray;
        [UCL.Core.PA.Conditional("m_Transition", false, Transition.ColorTint)] public Color m_DisabledColor = DisableCol;
        public UCL.Core.UCL_Event m_OnClick = null;
        public UCL.Core.UCL_Event m_OnPointerDown = null;
        public UCL.Core.UCL_Event m_OnPointerUp = null;
        public UCL.Core.UCL_Event m_OnPointerEnter = null;
        public UCL.Core.UCL_Event m_OnPointerExit = null;
        public UCL.Core.UCL_FloatEvent m_OnPressed = null;

        private void Awake()
        {
            Interactable = m_Interactable;//Initialize Color
        }
        private void OnValidate()
        {
            StateUpdate();
        }
        virtual public void OnPointerEnter(PointerEventData eventData) {
            if (!Interactable) return;
            Enter = true;
            m_OnPointerEnter.UCL_Invoke();
            StateUpdate();
        }
        virtual public void OnPointerExit(PointerEventData eventData) {
            if (!Interactable) return;
            Enter = false;
            m_OnPointerExit.UCL_Invoke();
            m_PressedTime = 0;
            StateUpdate();
        }

        virtual public void OnPointerDown(PointerEventData eventData) {
            if (!Interactable) return;
            m_OnPointerDown.UCL_Invoke();
            Pressed = true;
            m_PressedTime = 0;

            StateUpdate();
        }
        virtual public void OnPointerUp(PointerEventData eventData) {
            if (!Interactable) return;
            m_OnPointerUp.UCL_Invoke();
            if(Enter && Pressed) {
                m_OnClick.UCL_Invoke();
            }
            Pressed = false;
            m_PressedTime = 0;
            StateUpdate();
        }
        virtual protected void StateUpdate() {
            switch(m_Transition) {
                case Transition.ColorTint: {
                        if(m_Image != null) {
                            if (Interactable)
                            {
                                if (!Pressed)
                                {
                                    m_Image.color = m_NormalColor;
                                }
                                else
                                {
                                    m_Image.color = m_PressedColor;
                                }
                            }
                            else
                            {
                                m_Image.color = m_DisabledColor;
                            }

                        }
                        break;
                    }
            }

            //Debug.LogWarning(++m_Times+"m_Pressed:" + m_PressedTime.ToString("0.0") + ",m_Enter:" + m_Enter);
            //if(m_Text != null) {
                //m_Text.text = "p:" + m_PressedTime.ToString("0.0") + ",E:" + (m_Enter ? "T" : "F") + ",D:" + (m_Dragging ? "T" : "F");
            //}

        }
        virtual protected void Update() {
            if(Pressed && Enter) {
                m_PressedTime += Time.deltaTime;
                m_OnPressed.UCL_Invoke(m_PressedTime);
            }
        }
    }
}