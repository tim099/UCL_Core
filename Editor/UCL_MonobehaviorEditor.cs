using UnityEngine;
using UnityEditor;
using System;
using System.Reflection;

namespace UCL.Core.EditorLib {

    [CustomEditor(typeof(MonoBehaviour),true)]
    public class UCL_MonobehaviorEditor : Editor {
        bool m_RequiresConstantRepaint = false;
        UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();
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
            DrawATTR.DrawAllMethods(target, aType, this.GetType(), m_Dic, () => DrawDefaultInspector());
            Resources.UnloadUnusedAssets();
        }
    }
}

