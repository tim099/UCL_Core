using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Reflection;

namespace UCL.Core.EditorLib {
    [CustomEditor(typeof(ScriptableObject), true)]
    public class UCL_ScriptableObjectEditor : Editor {

        bool m_RequiresConstantRepaint = false;
        public override bool RequiresConstantRepaint()
        {
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
            DrawATTR.DrawAllMethods(target, aType, this.GetType(), ()=> DrawDefaultInspector());
            Resources.UnloadUnusedAssets();
        }
    }
}