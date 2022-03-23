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
        bool m_Blocking = false;
        bool m_HotControlBlocking = false;
        protected void Init() {
            if(m_Inited) return;
            m_Inited = true;
            m_Canvas = gameObject.AddComponent<Canvas>();
            m_Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            m_Canvas.sortingOrder = short.MaxValue;
            gameObject.AddComponent<GraphicRaycaster>();
            #region CreateBlockImage
            var img = Core.GameObjectLib.CreateGameObject("BlockImg", transform);
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
        public void SetBlocking(bool iIsActive) {
            m_Blocking = iIsActive;
        }
        public void UpdateBlockingImage()
        {
            bool aBlock = (m_HotControlBlocking | m_Blocking);
            if (m_BlockImage.gameObject.activeSelf != aBlock)
            {
                m_BlockImage.gameObject.SetActive(aBlock);
            }
        }
        public void SetBlockOnHotControl(bool flag) {
            f_BlockOnHotControl = flag;
        }
        private void Update() {
            //if(GUIUtility.hotControl!=0) Debug.LogWarning("GUIUtility.hotControl:" + GUIUtility.hotControl);
            if(f_BlockOnHotControl) {
                m_HotControlBlocking = GUIUtility.hotControl != 0;
            }
            UpdateBlockingImage();
        }
    }
}

