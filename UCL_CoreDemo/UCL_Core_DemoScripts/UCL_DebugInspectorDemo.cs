using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Demo {
    [Core.ATTR.EnableUCLEditor]
    public class UCL_DebugInspectorDemo : MonoBehaviour {
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