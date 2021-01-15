using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.CameraLib {
    abstract public class UCL_OnRenderImageModule : MonoBehaviour {
        public bool m_RenderOff = false;
        public bool m_RenderInEditMode = true;

        virtual public bool RenderOff() {//return true if Skip Render!!
            return (m_RenderOff || !enabled || !gameObject.activeInHierarchy || (!m_RenderInEditMode && !Application.isPlaying));
        }
        virtual public void Render(ref RenderTexture source, ref RenderTexture destination) {
            Graphics.Blit(source, destination);
        }
    }
}