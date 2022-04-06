using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace UCL.Core.CameraLib {
    [ExecuteInEditMode]
    public class UCL_OnRenderImagePipeline : MonoBehaviour {
        public bool m_RenderInEditMode = true;
        public List<UCL_OnRenderImageModule> m_Modules = new List<UCL_OnRenderImageModule>();
        public List<UCLI_OnRenderImage> m_RuntimeModules = new List<UCLI_OnRenderImage>();
        RenderTexture m_TmpDes = null;
        Container.UCL_Vector<UCLI_OnRenderImage> m_ActiveModules = new Container.UCL_Vector<UCLI_OnRenderImage>();
        private void OnDestroy() {
            ClearTmpDes();
        }
        protected void ClearTmpDes() {
            if(m_TmpDes == null) return;
            RenderTexture.ReleaseTemporary(m_TmpDes);
            m_TmpDes = null;
        }
        public void AddModule(UCLI_OnRenderImage iModule)
        {
            m_RuntimeModules.Add(iModule);
        }
        public void ClearRuntimeModules()
        {
            m_RuntimeModules.Clear();
        }
        public void RemoveModule(UCLI_OnRenderImage iModule)
        {
            m_RuntimeModules.Remove(iModule);
        }
        //public Material m_Mat;
        private void OnRenderImage(RenderTexture iSource, RenderTexture iDestination) {
            //Graphics.Blit(source, destination, m_Mat);
            if(!m_RenderInEditMode && !Application.isPlaying)
            {
                Graphics.Blit(iSource, iDestination);
                return;
            }
            ///*
            if((m_Modules.IsNullOrEmpty() && m_RuntimeModules.IsNullOrEmpty()) || iSource == null) {
                Graphics.Blit(iSource, iDestination);
                return;
            }
            m_ActiveModules.Clear();
            for(int i = 0; i < m_Modules.Count; i++) {
                var aModule = m_Modules[i];
                if(aModule != null && !aModule.RenderOff()) {
                    m_ActiveModules.Add(aModule);
                }
            }
            for (int i = 0; i < m_RuntimeModules.Count; i++)
            {
                m_ActiveModules.Add(m_RuntimeModules[i]);
            }
            if(m_ActiveModules.Count == 0) {
                Graphics.Blit(iSource, iDestination);
                return;
            }
            if (m_ActiveModules.Count == 1)
            {
                m_ActiveModules[0].Render(ref iSource, ref iDestination);
                return;
            }
            RenderTexture aCurSource = iSource;
            ClearTmpDes();
            if (m_TmpDes == null) {
                m_TmpDes = RenderTexture.GetTemporary(iSource.width, iSource.height);
            }
            for(int i = 0, aCount = m_ActiveModules.Count; i < aCount; i++) {
                var aModule = m_ActiveModules[i];
                if (i < aCount - 1)
                {
                    aModule.Render(ref aCurSource, ref m_TmpDes);
                    aCurSource = m_TmpDes;
                }
                else
                {
                    //Debug.LogError("RenderAt:" + i+ ",aCount:"+ aCount);
                    aModule.Render(ref aCurSource, ref iDestination);
                }
            }
            //*/
        }
    }
}

