using UnityEngine;
using UnityEditor;
using System;
using System.Reflection;

namespace UCL.Core.EditorLib {

    [CustomEditor(typeof(MonoBehaviour),true)]
    public class UCL_MonobehaviorEditor : Editor {
        bool m_RequiresConstantRepaint = false;
        public override bool RequiresConstantRepaint() {
            return m_RequiresConstantRepaint;
        }
        public override void OnInspectorGUI() {
            DrawDefaultInspector();
            Type type = target.GetType();
            if(type.GetCustomAttributes(typeof(ATTR.EnableUCLEditor), true).Length == 0) return;
            m_RequiresConstantRepaint = (type.GetCustomAttribute(typeof(ATTR.RequiresConstantRepaintAttribute), true) != null);
            DrawATTR.Draw(target, type, this.GetType());
            Resources.UnloadUnusedAssets();
        }
    }
}

