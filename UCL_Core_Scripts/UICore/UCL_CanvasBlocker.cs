using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
namespace UCL.Core.UI {
    public class UCL_CanvasBlocker : UCL_Singleton<UCL_CanvasBlocker> {
        Canvas m_Canvas;
        Image m_BlockImage;
        public bool f_BlockOnHotControl = false;
        public bool Blocking {
            get {
                if(m_BlockImage == null) return false;
                return m_BlockImage.gameObject.activeSelf;
            }
        }
        bool m_Inited = false;
        protected void Init() {
            if(m_Inited) return;
            m_Inited = true;
            m_Canvas = gameObject.AddComponent<Canvas>();
            m_Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            m_Canvas.sortingOrder = short.MaxValue;
            gameObject.AddComponent<GraphicRaycaster>();
            #region CreateBlockImage
            var img = Core.UCL_GameObjectLib.CreateGameObject("BlockImg", transform);
            m_BlockImage = img.AddComponent<Image>();
            m_BlockImage.color = Color.clear;
            RectTransform rect = img.GetComponent<RectTransform>();
            Core.UCL_RectTransformLib.SetFullScreen(rect);
            #endregion
            m_BlockImage.gameObject.SetActive(false);
        }
        private void Awake() {
            Init();

        }
        public void SetBlocking(bool active) {
            if(!m_Inited) Init();
            m_BlockImage.gameObject.SetActive(active);
        }
        public void SetBlockOnHotControl(bool flag) {
            f_BlockOnHotControl = flag;
        }
        private void Update() {
            //if(GUIUtility.hotControl!=0) Debug.LogWarning("GUIUtility.hotControl:" + GUIUtility.hotControl);
            if(f_BlockOnHotControl) {
                SetBlocking(GUIUtility.hotControl != 0);
            }
        }
    }
}

