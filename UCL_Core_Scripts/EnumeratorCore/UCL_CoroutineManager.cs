using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.EnumeratorLib {
    public class UCL_CoroutineManager : MonoBehaviour {
        static UCL_CoroutineManager m_Instance = null;
        List<UCL.Core.EnumeratorLib.EnumeratorPlayer> m_Players = new List<EnumeratorLib.EnumeratorPlayer>();

        static UCL_CoroutineManager Instance {
            get{
                if (m_Instance == null)
                {
                    m_Instance = GameObjectLib.Create<UCL_CoroutineManager>("UCL_CoroutineManager", null);
                    DontDestroyOnLoad(m_Instance.gameObject);
                }
                return m_Instance;
            }
        }

        /// <summary>
        /// Start playing Coroutine
        /// </summary>
        /// <param name="enumerator"></param>
        /// <returns></returns>
        new public static EnumeratorPlayer StartCoroutine(IEnumerator enumerator) {
            UCL.Core.EnumeratorLib.EnumeratorPlayer player = null;
#if UNITY_EDITOR
            if(!Application.isPlaying) {//Edit Mode
                player = UCL.Core.EditorLib.UCL_EditorCoroutineManager.StartCoroutine(enumerator);
                //Debug.LogWarning("Editor Coroutine");
            }
#endif
            if(player == null) {
                //Debug.LogWarning("Runtime Coroutine");
                player = EnumeratorPlayer.Play(enumerator);
                player.m_PlayInEditor = false;
                Instance.m_Players.Add(player);
            }
            return player;
        }
        public static void StopCoroutine(UCL.Core.EnumeratorLib.EnumeratorPlayer iPlayer) {
            if(iPlayer == null) return;
#if UNITY_EDITOR
            if(iPlayer.m_PlayInEditor) {
                UCL.Core.EditorLib.UCL_EditorCoroutineManager.StopCoroutine(iPlayer);
                //Debug.LogWarning("Editor Stop Coroutine");
                return;
            }
#endif
            //Debug.LogWarning("Runtime Stop Coroutine");
            var instance = Instance;
            if(instance.m_Players.Contains(iPlayer)) {
                instance.m_Players.Remove(iPlayer);
            }
        }
        //private void FixedUpdate() {
        //    UpdateAction();
        //}
        private void LateUpdate()
        {
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