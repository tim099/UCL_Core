using System;
using System.Collections;
using System.Collections.Generic;
using UCL.Core.ServiceLib;
using UnityEngine;

namespace UCL.Core.EditorLib {
    static public class UCL_EditorUpdateManager {

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        static public void Init()
        {
#if UNITY_EDITOR
            m_PrevFrameTime = System.DateTime.Now;
            //Debug.Log("UCL_EditorUpdateManager() Init EditorApplicationMapper.update");
            EditorApplicationMapper.update += UpdateAction;
#endif
        }

        static System.DateTime m_PrevFrameTime = default;
        //static float m_TotalTime = 0f;

        /// <summary>
        /// Main update loop
        /// </summary>
        static void UpdateAction() {
            //m_TotalTime += aTimeDel;
            //Debug.LogWarning("aTimeDel:" + aTimeDel+ ",m_TotalTime:"+ m_TotalTime);
#if UNITY_EDITOR
            float aTimeDel = 0;
            if (m_PrevFrameTime != default)
            {
                aTimeDel = (float)((System.DateTime.Now - m_PrevFrameTime).TotalSeconds);
                m_PrevFrameTime = System.DateTime.Now;
            }
            try
            {
                UCL_UpdateController.Ins.UpdateAction(aTimeDel);
            }
            catch (Exception iE)
            {
                Debug.LogException(iE);
            }
#endif
        }
    }
}

