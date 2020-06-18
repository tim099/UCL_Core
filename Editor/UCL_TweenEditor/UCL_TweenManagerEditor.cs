using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.Tween {
    [CustomEditor(typeof(UCL_TweenManager))]
    public class UCL_TweenManagerEditor : Core.EditorLib.UCL_MonobehaviorEditor {
        public override void OnInspectorGUI() {
            base.OnInspectorGUI();

        }
    }
}

