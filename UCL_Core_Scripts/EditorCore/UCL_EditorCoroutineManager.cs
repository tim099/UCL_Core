using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.EditorLib {
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    public static class UCL_EditorCoroutineManager {
#if UNITY_EDITOR
        static List<UCL.Core.EnumeratorLib.EnumeratorPlayer> m_Players = new List<EnumeratorLib.EnumeratorPlayer>();
#endif
        static UCL_EditorCoroutineManager() {
#if UNITY_EDITOR
            //Debug.Log("UCL_EditorCoroutineManager() Init");
            UCL_EditorUpdateManager.AddEditorUpdateAct(UpdateAction);
#endif
        }
        /// <summary>
        /// Start Coroutine in Editor
        /// Please Use the UCL.Core.EnumeratorLib.Wait instead of UnityEngine Builtin Wait
        /// </summary>
        /// <param name="enumerator"></param>
        public static UCL.Core.EnumeratorLib.EnumeratorPlayer StartCoroutine(IEnumerator enumerator) {
            UCL.Core.EnumeratorLib.EnumeratorPlayer player = null;
#if UNITY_EDITOR
            player = UCL.Core.EnumeratorLib.EnumeratorPlayer.Play(enumerator);
            player.m_PlayInEditor = true;
            m_Players.Add(player);
#endif
            return player;
        }

        public static void StopCoroutine(UCL.Core.EnumeratorLib.EnumeratorPlayer player) {
            if(player == null) return;
#if UNITY_EDITOR
            if(m_Players.Contains(player)) {
                m_Players.Remove(player);
            }
#endif
        }
        static void UpdateAction() {
#if UNITY_EDITOR
            if(m_Players == null || m_Players.Count == 0) return;
            for(int i = m_Players.Count - 1; i >= 0; i--) {
                var player = m_Players[i];
                player.Update();
                if(player.End()) {
                    m_Players.Remove(player);
                }
            }
#endif
        }
    }
}