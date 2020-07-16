using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.DebugLib {
    public class UCL_DebugOnGUI : UCL_Singleton<UCL_DebugOnGUI> {
        public class DebugData {
            public DebugData() {
                m_OnGUIact = new UnityEngine.Events.UnityEvent();

            }
            internal protected void Draw() {
                //Debug.LogWarning("Draw");
                if(m_OnGUIact != null) m_OnGUIact.Invoke();
            }
            public DebugData AddOnGUIAct(UnityEngine.Events.UnityAction act) {
                m_OnGUIact.AddListener(act);
                //m_OnGUIact.Invoke();
                return this;
            }
            public UnityEngine.Events.UnityEvent m_OnGUIact;
        }

        Queue<DebugData> m_Datas = new Queue<DebugData>();
        Queue<DebugData> m_DatasBuffer = new Queue<DebugData>();

        public DebugData CreateData() {
            var data = new DebugData();
            m_DatasBuffer.Enqueue(data);
            return data;
        }
        private void Update() {
            
        }
        private void LateUpdate() {
            //Debug.LogWarning("m_Datas.Clear() m_Datas.Count:" + m_Datas.Count);
            m_Datas.Clear();
            GameObjectLib.swap(ref m_Datas, ref m_DatasBuffer);
        }
        private void OnGUI() {
            //Debug.LogWarning("OnGUI() m_Datas.Count:" + m_Datas.Count);
            GUILayout.BeginHorizontal();
            foreach(var data in m_Datas) {
                data.Draw();
            }
            GUILayout.EndHorizontal();
        }
    }
}