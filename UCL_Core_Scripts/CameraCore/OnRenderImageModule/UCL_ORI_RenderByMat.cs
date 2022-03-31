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

        [Header("if OutputTexture is not null,will Blit to OutputTexture")]
        public RenderTexture m_OutputTexture;
        public Material m_Mat;
        public string m_MainTexName = "_MainTex";
        public override bool RenderOff() {
            if(base.RenderOff()) return true;
            return (m_Mat == null);
        }
        public override void Render(ref RenderTexture source, ref RenderTexture destination) {
            //Graphics.Blit(source, destination);
            //Debug.LogWarning("Render(ref RenderTexture source, ref RenderTexture destination)");

            if(m_Mat != null) {
                for(int i = 0; i < m_Textures.Count; i++) {
                    var tex = m_Textures[i];
                    m_Mat.SetTexture(tex.m_Name, tex.m_Texture);
                }
                foreach(var val in m_Floats) {
                    m_Mat.SetFloat(val.m_Name, val.m_Val);
                }
                m_Mat.SetTexture(m_MainTexName, source);
                if(m_OutputTexture != null) {
                    //Debug.LogWarning("Graphics.Blit(source, m_OutputTexture);");
                    m_OutputTexture.Release();
                    Graphics.Blit(source, m_OutputTexture, m_Mat);
                    destination.Release();
                    Graphics.Blit(m_OutputTexture, destination);
                } else {
                    //Debug.LogWarning("Graphics.Blit(source, destination, m_Mat);");
                    destination.Release();
                    Graphics.Blit(source, destination, m_Mat);
                }
            } else {
                //Debug.LogWarning("m_Mat == null");
                destination.Release();
                Graphics.Blit(source, destination);
                if(m_OutputTexture != null) {
                    //Debug.LogWarning("Graphics.Blit(source, m_OutputTexture);");
                    m_OutputTexture.Release();
                    Graphics.Blit(destination, m_OutputTexture);
                }
            }
        }
    }
}