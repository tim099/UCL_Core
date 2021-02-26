using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.Misc
{
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_TimeScaleSetter : MonoBehaviour
    {
        [ATTR.UCL_FunctionButton("SetTimeScale 2", 2f)]
        [ATTR.UCL_FunctionButton("SetTimeScale 1", 1f)]
        [ATTR.UCL_FunctionButton("SetTimeScale 0.5", 0.5f)]
        [ATTR.UCL_FunctionButton("SetTimeScale 0.25", 0.25f)]
        public void SetTimeScale(float iTimeScale)
        {
            Time.timeScale = iTimeScale;
        }
    }
}