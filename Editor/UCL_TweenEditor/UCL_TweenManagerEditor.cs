using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UCL.Core.Tween {
    [CustomEditor(typeof(UCL_TweenManager))]
    public class UCL_TweenManagerEditor : Core.EditorLib.UCL_MonobehaviorEditor {
        public override bool RequiresConstantRepaint() {
            return true;
        }
        public override void OnInspectorGUI() {
            var manager = target as UCL_TweenManager;
            
            GUILayout.BeginVertical();
            //Debug.LogWarning("TweenCount:" + manager.TweenCount);
            GUILayout.Box("TweenCount:" + manager.TweenCount);
            GUILayout.EndVertical();

            base.OnInspectorGUI();
            //this.Repaint();
            //this.RequiresConstantRepaint
        }
    }
}

