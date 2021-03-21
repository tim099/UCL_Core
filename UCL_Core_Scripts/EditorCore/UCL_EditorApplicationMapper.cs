using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.EditorLib
{
    public enum PlayModeStateChangeMapper
    {
        //
        // 摘要:
        //     Occurs during the next update of the Editor application if it is in edit mode
        //     and was previously in play mode.
        EnteredEditMode = 0,
        //
        // 摘要:
        //     Occurs when exiting edit mode, before the Editor is in play mode.
        ExitingEditMode = 1,
        //
        // 摘要:
        //     Occurs during the next update of the Editor application if it is in play mode
        //     and was previously in edit mode.
        EnteredPlayMode = 2,
        //
        // 摘要:
        //     Occurs when exiting play mode, before the Editor is in edit mode.
        ExitingPlayMode = 3
    }
    static public class EditorApplicationMapper
    {
        #region update
        public static event Action update = null;
        public static void Update()
        {
            if(update != null) update.Invoke();
        }
        #endregion
        #region playModeStateChanged
        public static event Action<PlayModeStateChangeMapper> playModeStateChanged = null;
        public static void PlayModeStateChanged(PlayModeStateChangeMapper iPlayModeStateChangeMapper)
        {
            if (playModeStateChanged != null) playModeStateChanged.Invoke(iPlayModeStateChangeMapper);
        }
        #endregion
    }
}