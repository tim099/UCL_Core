using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.EnumeratorLib
{
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_EditorCoroutineDemo : MonoBehaviour
    {
#if UNITY_EDITOR
        Core.EnumeratorLib.EnumeratorPlayer m_Player = null;
        public void EditorStartCoroutine(IEnumerator enumerator) {
            EditorStopCoroutine();
            m_Player = Core.EditorLib.UCL_EditorCoroutineManager.StartCoroutine(enumerator);
        }
        IEnumerator EnumeratorTest(int num) {
            for(int i = 0; i < num; i++) {
                Debug.LogWarning("i:" + i + ",time:" + System.DateTime.Now.ToString("ss.f"));
                yield return Core.EnumeratorLib.Wait.WaitSeconds(0.2f + 0.1f * i);
            }
        }
        IEnumerator EnumeratorTest2(int at) {
            for(int i = at; i >= 0; i--) {
                Debug.LogWarning("=================Test2:" + i + ",time:" + System.DateTime.Now.ToString("ss.f")+"=======================");
                yield return EnumeratorTest(i);
                yield return Core.EnumeratorLib.Wait.WaitForUpdate(2 * at);
            }
        }

        [UCL.Core.ATTR.UCL_FunctionButton]
        public void EditorCoroutine() {
            EditorStartCoroutine(EnumeratorTest(10));
        }

        [UCL.Core.ATTR.UCL_FunctionButton]
        public void EditorCoroutine2() {
            EditorStartCoroutine(EnumeratorTest2(5));
        }

        [UCL.Core.ATTR.UCL_FunctionButton]
        public void EditorStopCoroutine() {
            Core.EditorLib.UCL_EditorCoroutineManager.StopCoroutine(m_Player);
        }
        void Update() {

        }
#endif
    }
}