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
        /// <summary>
        /// Equivalent to UnityEditor.EditorApplication.playModeStateChanged in EditorMode
        /// </summary>
        public static event Action<PlayModeStateChangeMapper> playModeStateChanged = null;
        public static void PlayModeStateChanged(PlayModeStateChangeMapper iPlayModeStateChangeMapper)
        {
            if (playModeStateChanged != null) playModeStateChanged.Invoke(iPlayModeStateChangeMapper);
        }
        #endregion

        #region isPlaying
        private static System.Func<bool> m_GetIsPlaying = null;
        private static System.Action<bool> m_SetIsPlaying = null;
        public static void InitIsPlaying(System.Func<bool> iGetIsPlaying, System.Action<bool> iSetIsPlaying)
        {
            m_GetIsPlaying = iGetIsPlaying;
            m_SetIsPlaying = iSetIsPlaying;
        }
        /// <summary>
        /// Is editor currently in play mode?
        /// </summary>
        public static bool isPlaying
        {
            get
            {
                if (m_GetIsPlaying != null) return m_GetIsPlaying.Invoke();
                return false;
            }
            set
            {
                if (m_SetIsPlaying != null)
                {
                    m_SetIsPlaying.Invoke(value);
                }
            }
        }
        #endregion

        #region isPaused
        private static System.Func<bool> m_GetIsPaused = null;
        private static System.Action<bool> m_SetIsPaused = null;
        public static void InitIsPaused(System.Func<bool> iGetIsPaused, System.Action<bool> iSetIsPaused)
        {
            m_GetIsPaused = iGetIsPaused;
            m_SetIsPaused = iSetIsPaused;
        }
        /// <summary>
        /// Is editor currently paused?
        /// </summary>
        public static bool isPaused
        {
            get
            {
                if (m_GetIsPaused != null) return m_GetIsPaused.Invoke();
                return false;
            }
            set
            {
                if (m_SetIsPaused != null)
                {
                    m_SetIsPaused.Invoke(value);
                }
            }
        }
        #endregion

        #region isCompiling
        private static System.Func<bool> m_GetIsCompiling = null;
        public static void InitIsCompiling(System.Func<bool> iGetIsCompiling)
        {
            m_GetIsCompiling = iGetIsCompiling;
        }
        /// <summary>
        /// Is editor currently compiling scripts? (Read Only)
        /// </summary>
        public static bool isCompiling
        {
            get
            {
                if (m_GetIsCompiling != null) return m_GetIsCompiling.Invoke();
                return false;
            }
        }
        #endregion

        #region isPlayingOrWillChangePlaymode
        private static System.Func<bool> m_GetIsPlayingOrWillChangePlaymode = null;
        public static void InitIsPlayingOrWillChangePlaymode(System.Func<bool> iGetIsPlayingOrWillChangePlaymode)
        {
            m_GetIsPlayingOrWillChangePlaymode = iGetIsPlayingOrWillChangePlaymode;
        }
        /// <summary>
        /// Is editor either currently in play mode, or about to switch to it? (Read Only)
        /// </summary>
        public static bool isPlayingOrWillChangePlaymode
        {
            get
            {
                if (m_GetIsPlayingOrWillChangePlaymode != null) return m_GetIsPlayingOrWillChangePlaymode.Invoke();
                return false;
            }
        }
        #endregion

        #region isUpdating
        private static System.Func<bool> m_GetIsUpdating = null;
        public static void InitIsUpdating(System.Func<bool> iGetIsUpdating)
        {
            m_GetIsUpdating = iGetIsUpdating;
        }
        /// <summary>
        /// True if the Editor is currently refreshing the AssetDatabase.
        /// </summary>
        public static bool isUpdating
        {
            get
            {
                if (m_GetIsUpdating != null) return m_GetIsUpdating.Invoke();
                return false;
            }
        }
        #endregion
    }
}