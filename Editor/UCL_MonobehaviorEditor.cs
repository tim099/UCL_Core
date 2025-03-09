using UnityEngine;
using UnityEditor;
using System;
using System.Reflection;
using UCL.Core.UI;

namespace UCL.Core.EditorLib {

    [CustomEditor(typeof(MonoBehaviour),true)]
    public class UCL_MonobehaviorEditor : Editor {
        bool m_RequiresConstantRepaint = false;
        UCL_ObjectDictionary m_Dic = new UCL_ObjectDictionary();
        public override bool RequiresConstantRepaint() {
            return m_RequiresConstantRepaint;
        }
        public override void OnInspectorGUI()
        {
            try
            {
                UCL_GUIStyle.IsInEditorWindow = true;
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
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                UCL_GUIStyle.IsInEditorWindow = false;
            }

            
        }
    }
}

