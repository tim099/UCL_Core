using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Demo {
    [Core.ATTR.EnableUCLEditor]
    public class UCL_DebugInspectorDemo : MonoBehaviour {
        public enum Test {
            a = 1,
            b = 1 << 1,
            c = 1 << 2,
        }

        [UCL.Core.PA.UCL_EnumMask] public Test m_Test;
        public int m_Val = 10;
        public float m_F = 0.731f;
        public List<int> m_IntList = new List<int>();
        private void Start() {
            Show();
        }
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void Show() {
            UCL_DebugInspector.Show();
        }

        [UCL.Core.ATTR.UCL_FunctionButton]
        public void Hide() {
            UCL_DebugInspector.Hide();
        }
    }
}