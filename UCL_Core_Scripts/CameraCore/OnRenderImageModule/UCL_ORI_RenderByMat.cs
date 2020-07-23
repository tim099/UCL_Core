using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.CameraLib {
    public class UCL_ORI_RenderByMat : UCL_OnRenderImageModule {
        [System.Serializable]
        public class MatData {
            public string m_Name;
        }

        [System.Serializable]
        public class TextureData : MatData {
            public Texture m_Texture;
        }
        [System.Serializable]
        public class FloatData : MatData {
            public float m_Val;
        }
        public List<TextureData> m_Textures;
        public List<FloatData> m_Floats;

        public Material m_Mat;
        public string m_MainTexName = "_MainTex";
        public override bool RenderOff() {
            if(base.RenderOff()) return true;
            return (m_Mat == null);
        }
        public override void Render(ref RenderTexture source, ref RenderTexture destination) {
            //Graphics.Blit(source, destination);

            for(int i = 0; i < m_Textures.Count; i++) {
                var tex = m_Textures[i];
                m_Mat.SetTexture(tex.m_Name, tex.m_Texture);
            }
            foreach(var val in m_Floats) {
                m_Mat.SetFloat(val.m_Name, val.m_Val);
            }
            m_Mat.SetTexture(m_MainTexName, source);
            Graphics.Blit(source, destination, m_Mat);//, 0
        }
    }
}