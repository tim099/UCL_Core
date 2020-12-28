using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core {
    public class UCL_DebugInspector : MonoBehaviour {
        public static UCL_DebugInspector ins = null;
        protected Rect m_WindowRect;

        public static void Show() {
            if(ins == null) {
                ins = Core.GameObjectLib.Create<UCL_DebugInspector>("UCL_DebugInspector", null);
            }
            ins.ShowInspector();
        }
        public static void Hide() {
            if(ins == null) return;
            ins.HideInspector();
        }


        virtual public void ShowInspector() {
            gameObject.SetActive(true);
        }
        virtual public void HideInspector() {
            gameObject.SetActive(false);
        }
        virtual public void Init() {

        }
        private void OnGUI() {
            const int edge = 5;
            m_WindowRect = new Rect(edge, edge, Screen.width - 2 * edge, Screen.height - 2 * edge);
            m_WindowRect = GUILayout.Window(132125, m_WindowRect, ConsoleWindow, "UCL DebugInspector");

        }
        virtual protected void ConsoleWindow(int id) {
            GUILayout.BeginVertical();
            var objs = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            foreach(var obj in objs) {
                if(GUILayout.Button(obj.name)) {

                }
            }
            GUILayout.EndVertical();
        }
    }
}