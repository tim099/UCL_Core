using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core {
    public class UCL_GameManager : UCL_Singleton<UCL_GameManager> {
        public static bool f_ApplicationQuit = false;

        public UnityEngine.Events.UnityEvent m_ExitGameEvent = new UnityEngine.Events.UnityEvent();
        public HashSet<string> m_BlockExitGameFlag = new HashSet<string>();
        public bool f_ExitGame { get; protected set; } = false;
        public int m_ExitGameMaxWaitFrame = 1000;
        [RuntimeInitializeOnLoadMethod]
        static void RuntimeInitializeInit() {
            //UCL_GameManager.Get();
        }


        private void Awake() {
            if(!SetInstance(this))return;
        }
        private void OnApplicationQuit() {
            Debug.LogWarning("UCL_GameManager OnApplicationQuit()");
            f_ApplicationQuit = true;
        }
        protected override void OnDestroy() {
            base.OnDestroy();
            Debug.LogWarning("UCL_GameManager OnDestroy()");
        }
        virtual public void ExitGame() {
            Debug.LogWarning("ExitGame()");
            if(f_ExitGame) return;
            f_ExitGame = true;
            m_ExitGameEvent?.Invoke();
            StartCoroutine(ExitGameCoroutine());
        }
        IEnumerator ExitGameCoroutine() {
            int n = 0;
            while(m_BlockExitGameFlag.Count > 0 && n < m_ExitGameMaxWaitFrame) {
                n++;
                yield return null;
            }

            yield return new WaitForSeconds(1.0f);

            Application.Quit();
        }
    }

}

