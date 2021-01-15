using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.Linq;

namespace UCL.Core.EditorLib {
    [CustomEditor(typeof(ScriptableObject), true)]
    public class UCL_ScriptableObjectEditor : Editor {
        public override void OnInspectorGUI() {
            DrawDefaultInspector();
            Type type = target.GetType();
            if(type.GetCustomAttributes(typeof(ATTR.EnableUCLEditor), true).Length == 0) return;
            DrawATTR.Draw(target, type, this.GetType());
        }
    }
}