using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.CameraLib {
    public interface UCLI_OnRenderImage
    {
        void Render(ref RenderTexture iSource, ref RenderTexture iDestination);
    }

    public delegate void RenderDelegate(ref RenderTexture iSource, ref RenderTexture iDestination);
    public class UCL_OnRenderImageAction : UCLI_OnRenderImage
    {
        RenderDelegate m_RenderDelegate = null;

        public UCL_OnRenderImageAction() { }
        public UCL_OnRenderImageAction(RenderDelegate iRenderDelegate) {
            m_RenderDelegate = iRenderDelegate;
        }
        virtual public void Render(ref RenderTexture iSource, ref RenderTexture iDestination)
        {
            if (m_RenderDelegate == null)
            {
                Graphics.Blit(iSource, iDestination);
                return;
            }
            m_RenderDelegate.Invoke(ref iSource,ref iDestination);
        }
    }
    abstract public class UCL_OnRenderImageModule : MonoBehaviour, UCLI_OnRenderImage
    {
        public bool m_RenderOff = false;
        public bool m_RenderInEditMode = true;

        virtual public bool RenderOff() {//return true if Skip Render!!
            return (m_RenderOff || !enabled || !gameObject.activeInHierarchy || (!m_RenderInEditMode && !Application.isPlaying));
        }
        virtual public void Render(ref RenderTexture iSource, ref RenderTexture iDestination) {
            Graphics.Blit(iSource, iDestination);
        }
    }
}