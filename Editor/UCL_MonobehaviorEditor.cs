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
            Type aType = target.GetType();
            if (aType.GetCustomAttribute<ATTR.EnableUCLEditor>(true) == null)
            {
                DrawDefaultInspector();
                return;
            }
            m_RequiresConstantRepaint = (aType.GetCustomAttribute<ATTR.RequiresConstantRepaintAttribute>(true) != null);
            DrawATTR.Draw(target, aType, this.GetType(), () => DrawDefaultInspector());
            Resources.UnloadUnusedAssets();
        }
    }
}

