using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace UCL.Core.Misc {
    [Core.ATTR.EnableUCLEditor]
    public class UCL_ScreenShot : MonoBehaviour {
        public RawImage m_Image;
        Texture2D m_Texture;

        [Core.ATTR.UCL_RuntimeOnly]
        [Core.ATTR.UCL_FunctionButton]
        public void TakeScreenShot() {
            m_Texture = ScreenCapture.CaptureScreenshotAsTexture();
            if(m_Image != null) m_Image.texture = m_Texture;
        }
    }
}