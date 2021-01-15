using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System.IO;
namespace UCL.Core.Misc {
    [Core.ATTR.EnableUCLEditor]
    public class UCL_ScreenShot : MonoBehaviour {
        /// <summary>
        /// if m_Image != null then the captured texture will set to m_Image
        /// </summary>
        public RawImage m_Image = null;
        public string m_SavePath = "";
        public TextureLib.ImageFormat m_SaveFormat = TextureLib.ImageFormat.jpg;
        Texture2D m_Texture = null;
#if UNITY_EDITOR
        private void Reset() {
            m_SavePath = Core.FileLib.EditorLib.GetLibFolderPath(FileLib.LibName.UCL_CoreLib) + "/UCL_CoreTextures/UCL_ScreenShot";
        }
#endif
        [Core.ATTR.UCL_RuntimeOnly]
        [Core.ATTR.UCL_FunctionButton]
        public void TakeScreenShot() {
            StartCoroutine(ScreenShot());
        }
        public void TakeScreenShot(string path) {
            m_SavePath = path;
            TakeScreenShot();
        }
        IEnumerator ScreenShot() {
            yield return new WaitForEndOfFrame();
            if(m_Texture != null) Destroy(m_Texture);
            m_Texture = ScreenCapture.CaptureScreenshotAsTexture();
            if(m_Image != null) m_Image.texture = m_Texture;
            UCL.Core.TextureLib.Lib.SaveTexture(m_SavePath, m_Texture, m_SaveFormat);
#if UNITY_EDITOR_WIN
            FileLib.WindowsLib.OpenAssetExplorer(FileLib.Lib.RemoveFolderPath(m_SavePath,1));
#endif
        }
    }
}