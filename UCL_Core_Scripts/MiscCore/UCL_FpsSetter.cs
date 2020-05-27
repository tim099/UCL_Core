using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Misc {
#if UNITY_EDITOR
    [UCL.Core.ATTR.EnableUCLEditor]
#endif
    public class UCL_FpsSetter : MonoBehaviour {

        public int m_TargetFps = 60;

        [ATTR.UCL_FunctionButton("SetTargetFps")]
        public void SetTargetFps() {
            SetFps(m_TargetFps);
        }

        [ATTR.UCL_FunctionButton("SetFps 60", 60)]
        [ATTR.UCL_FunctionButton("SetFps 30", 30)]
        [ATTR.UCL_FunctionButton("SetFps 15", 15)]
        [ATTR.UCL_FunctionButton("SetFps 10", 10)]
        [ATTR.UCL_FunctionButton("SetFps 5", 5)]
        public void SetFps(int fps) {
            Debug.LogWarning("SetFps:" + fps);
#if UNITY_EDITOR
            QualitySettings.vSyncCount = 0; // VSync must be disabled.
#endif
            Application.targetFrameRate = fps;
        }
    }
}

