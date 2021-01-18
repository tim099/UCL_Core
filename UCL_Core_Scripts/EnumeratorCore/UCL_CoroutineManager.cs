using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.EnumeratorLib {
    public class UCL_CoroutineManager : MonoBehaviour {
        static UCL_CoroutineManager ins = null;
        List<UCL.Core.EnumeratorLib.EnumeratorPlayer> m_Players = new List<EnumeratorLib.EnumeratorPlayer>();

        static UCL_CoroutineManager GetInstance() {
            if(ins == null) {
                ins = GameObjectLib.Create<UCL_CoroutineManager>("UCL_CoroutineManager", null);
                DontDestroyOnLoad(ins.gameObject);
            }

            return ins;
        }

        /// <summary>
        /// Start playing Coroutine
        /// </summary>
        /// <param name="enumerator"></param>
        /// <returns></returns>
        new public static EnumeratorPlayer StartCoroutine(IEnumerator enumerator) {
            UCL.Core.EnumeratorLib.EnumeratorPlayer player = null;
#if UNITY_EDITOR
            if(!Application.isPlaying) {
                player = UCL.Core.EditorLib.UCL_EditorCoroutineManager.StartCoroutine(enumerator);
                //Debug.LogWarning("Editor Coroutine");
            }
#endif
            if(player == null) {
                //Debug.LogWarning("Runtime Coroutine");
                player = EnumeratorPlayer.Play(enumerator);
                player.m_PlayInEditor = false;
                GetInstance().m_Players.Add(player);
            }
            return player;
        }
        public static void StopCoroutine(UCL.Core.EnumeratorLib.EnumeratorPlayer player) {
            if(player == null) return;
#if UNITY_EDITOR
            if(player.m_PlayInEditor) {
                UCL.Core.EditorLib.UCL_EditorCoroutineManager.StopCoroutine(player);
                //Debug.LogWarning("Editor Stop Coroutine");
                return;
            }
#endif
            //Debug.LogWarning("Runtime Stop Coroutine");
            var instance = GetInstance();
            if(instance.m_Players.Contains(player)) {
                instance.m_Players.Remove(player);
            }
        }
        private void FixedUpdate() {
            UpdateAction();
        }
        private void UpdateAction() {
            if(m_Players == null || m_Players.Count == 0) return;
            for(int i = m_Players.Count - 1; i >= 0; i--) {
                var player = m_Players[i];
                player.Update();
                if(player.End()) {
                    m_Players.Remove(player);
                }
            }
        }
    }
}