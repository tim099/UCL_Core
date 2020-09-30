using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.UI;

namespace UCL.Core.UI.Editor {
    public static class UCL_UICreator {
        [UnityEditor.MenuItem("GameObject/UI/UCL_Button")]
        private static void CreateUCL_Button() {
            Object selectedObject = UnityEditor.Selection.activeObject;
            if(selectedObject != null) {
                Debug.LogWarning("CreateUCL_Button() selectedObject:" + selectedObject.name);
            }

            GameObject obj = selectedObject as GameObject;
            Transform p = null;
            if(obj != null) {
                p = obj.transform;   
            }
            var but = UCL.Core.GameObjectLib.Create<UCL_Button>("UCL_Button", p);
            UnityEditor.Selection.activeObject = but;
            but.gameObject.AddComponent<Image>();
        }
    }
}