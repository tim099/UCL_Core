using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace UCL.Core.Game
{
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_GUIPageService : UCL_GameService
    {
        Canvas m_Canvas = null;
        Image m_BlockImage = null;
        Rect m_WindowRect;
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
        [UCL.Core.ATTR.UCL_DrawOnGUI]
        virtual protected void DrawInfo()
        {
            System.Text.StringBuilder aSB = new System.Text.StringBuilder();
            var aPageController = UI.UCL_GUIPageController.Ins;
            if (aPageController.IsEmpty)
            {
                aSB.Append("Page is Empty");
            }
            else
            {
                aSB.Append(aPageController.TopPage.GetType().Name);
            }
            GUILayout.Box(aSB.ToString());
        }

        private void OnGUI()
        {
            var aPageController = UI.UCL_GUIPageController.Ins;


            if (!aPageController.IsEmpty)
            {
                string aWindowName = aPageController.WindowName;
                if (!string.IsNullOrEmpty(aWindowName))
                {
                    const int Edge = 2;//5 pixel
                    m_WindowRect = new Rect(Edge, Edge, Screen.width - 2 * Edge, Screen.height - 2 * Edge);

                    m_WindowRect = GUILayout.Window(133126, m_WindowRect, (iID) => {
                        aPageController.DrawOnGUI();
                    }, aWindowName);
                }
                else
                {
                    aPageController.DrawOnGUI();
                }


                if (aPageController.IsBlockCanvas)
                {
                    m_BlockImage.color = aPageController.BlockCanvasColor;
                    m_BlockImage.gameObject.SetActive(true);
                }
                else
                {
                    m_BlockImage.gameObject.SetActive(false);
                }
            }
            else
            {
                m_BlockImage.gameObject.SetActive(false);
            }



            

        }
        private void Update()
        {
            UCL.Core.UI.UCL_GUIPageController.Ins.Update();
        }
    }
}