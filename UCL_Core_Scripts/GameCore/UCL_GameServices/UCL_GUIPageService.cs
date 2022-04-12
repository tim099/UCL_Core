using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.Game
{
    public class UCL_GUIPageService : UCL_GameService
    {
        public override void Init()
        {
            base.Init();
        }
        private void OnGUI()
        {
            if (UCL.Core.UI.UCL_GUIPage.DrawOnGUI())
            {
                //UCL.Core.UI.UCL_CanvasBlocker.Instance.SetBlocking(true);
            }
        }
    }
}