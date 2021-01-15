using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.CameraLib {
    [RequireComponent(typeof(Camera))]
    [ExecuteInEditMode]
    public class UCL_CameraDepthTexture : MonoBehaviour {
        public DepthTextureMode m_Mode = DepthTextureMode.Depth;
        public RenderTexture m_TargetTexture;
        public Camera m_Camera;
        //public Shader m_DepthShader;
        public Material m_Mat;
        //public string m_DepthData;
        private void Awake() {
            UpdateSetting();
            
            //m_Camera.tex
        }
        void UpdateSetting() {
            if(m_Camera == null) m_Camera = GetComponent<Camera>();
            if(m_Camera == null) return;
            if(m_TargetTexture != null) m_Camera.targetTexture = m_TargetTexture;
            m_Camera.depthTextureMode = m_Mode;
            //if(m_DepthShader != null) m_Camera.SetReplacementShader(m_DepthShader, "RenderType");

            //m_DepthData = m_DepthBuffer.UCL_ToString();
            //m_DepthData = m_DepthBuffer.ToByteArray();
        }
        private void OnRenderImage(RenderTexture source, RenderTexture destination) {
            if(m_Mat) {
                Graphics.Blit(source, destination, m_Mat);
            } else {
                Graphics.Blit(source, destination);
            }

        }
#if UNITY_EDITOR
        private void OnValidate() {
            UpdateSetting();
        }
#endif
    }
}