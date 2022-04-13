using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace UCL.Core.Game
{
    public class UCL_GUIPageService : UCL_GameService
    {
        Canvas m_Canvas = null;
        Image m_BlockImage = null;
        public override void Init()
        {
            m_Canvas = gameObject.AddComponent<Canvas>();
            m_Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            m_Canvas.sortingOrder = short.MaxValue;
            gameObject.AddComponent<GraphicRaycaster>();
            #region CreateBlockImage
            var aImageObj = Core.GameObjectLib.CreateGameObject("BlockImg", transform);
            m_BlockImage = aImageObj.AddComponent<Image>();
            m_BlockImage.color = new Color(0,0,0,0.7f);
            RectTransform aRect = aImageObj.GetComponent<RectTransform>();
            Core.UCL_RectTransformLib.SetFullScreen(aRect);
            #endregion
            m_BlockImage.gameObject.SetActive(false);
        }
        private void OnGUI()
        {
            m_BlockImage.gameObject.SetActive(UCL.Core.UI.UCL_GUIPageController.Ins.DrawOnGUI());
        }
    }
}