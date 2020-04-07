using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core {
    public class UCL_GameManager : UCL_Singleton<UCL_GameManager> {
        public UnityEngine.Events.UnityEvent m_ExitGameEvent = new UnityEngine.Events.UnityEvent();
        public HashSet<string> m_BlockExitGameFlag = new HashSet<string>();
        public bool f_ExitGame { get; protected set; } = false;
        private void Awake() {
            Instance = this;
        }

        virtual public void ExitGame() {
            if(f_ExitGame) return;
            f_ExitGame = true;
            m_ExitGameEvent?.Invoke();
            StartCoroutine(ExitGameCoroutine());
        }
        IEnumerator ExitGameCoroutine() {
            int n = 0;
            while(m_BlockExitGameFlag.Count > 0 || n > 10000) {
                n++;
                yield return null;
            }

            yield return new WaitForSeconds(1.0f);

            Application.Quit();
        }
    }

}

