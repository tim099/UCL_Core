using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace UCL.Core.CameraLib {
    [ExecuteInEditMode]
    public class UCL_OnRenderImagePipeline : MonoBehaviour {
        public bool m_RenderInEditMode = true;
        public List<UCL_OnRenderImageModule> m_Modules;
        RenderTexture m_TmpDes;
        Container.UCL_Vector<UCL_OnRenderImageModule> m_ActiveModules = new Container.UCL_Vector<UCL_OnRenderImageModule>();
        private void OnDestroy() {
            ClearTmpDes();
        }
        protected void ClearTmpDes() {
            if(m_TmpDes == null) return;
            RenderTexture.ReleaseTemporary(m_TmpDes);
            m_TmpDes = null;
        }
        //public Material m_Mat;
        private void OnRenderImage(RenderTexture source, RenderTexture destination) {
            //Graphics.Blit(source, destination, m_Mat);

            ///*
            if(m_Modules == null || m_Modules.Count == 0 || source == null || (!m_RenderInEditMode && !Application.isPlaying)) {
                Graphics.Blit(source, destination);
                return;
            }
            m_ActiveModules.Clear();
            for(int i = 0; i < m_Modules.Count; i++) {
                var module = m_Modules[i];
                if(module != null && !module.RenderOff()) {
                    m_ActiveModules.Add(module);
                }
            }
            if(m_ActiveModules.Count == 0) {
                Graphics.Blit(source, destination);
                return;
            }

            RenderTexture cur_source = source;
            if(m_TmpDes != null ) {//&& (m_TmpDes.width != source.width || m_TmpDes.height != source.height)
                RenderTexture.ReleaseTemporary(m_TmpDes);
                m_TmpDes = null;
            }
            if(m_TmpDes == null) {
                m_TmpDes = RenderTexture.GetTemporary(source.width, source.height);
            }
            for(int i = 0, count = m_ActiveModules.Count; i < count; i++) {
                var module = m_ActiveModules[i];
                if(!module.RenderOff()) {
                    if(i < count - 1) {
                        module.Render(ref cur_source, ref m_TmpDes);
                        cur_source = m_TmpDes;
                    } else {
                        module.Render(ref cur_source, ref destination);
                    }
                }
            }
            //*/
        }
    }
}

