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
            m_Player = Core.EnumeratorLib.UCL_CoroutineManager.StartCoroutine(enumerator);
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
            Core.EnumeratorLib.UCL_CoroutineManager.StopCoroutine(m_Player);
        }
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void EditorUpdateRigister()
        {
            UCL.Core.ServiceLib.UCL_UpdateService.AddUpdateAction(UpdateAct);
            //if (Application.isPlaying)
            //{
            //    UCL.Core.ServiceLib.UCL_UpdateService.Instance.AddUpdateAction(UpdateAct);
            //}
            //else
            //{
            //    UCL.Core.EditorLib.UCL_EditorUpdateManager.AddEditorUpdateAct(UpdateAct);
            //}
        }
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void EditorUpdateUnRigister()
        {
            UCL.Core.ServiceLib.UCL_UpdateService.RemoveUpdateAction(UpdateAct);
        }
        int m_Timer = 0;
        private void UpdateAct()
        {
            Debug.LogWarning("Hi:" + m_Timer++);
        }
        void Update() {

        }
#endif
    }
}