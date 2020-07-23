using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.CameraLib {
    abstract public class UCL_OnRenderImageModule : MonoBehaviour {
        virtual public bool RenderOff() {//return true if Skip Render!!
            return false;
        }
        virtual public void Render(RenderTexture source, RenderTexture destination) {
            Graphics.Blit(source, destination);
        }
    }
}