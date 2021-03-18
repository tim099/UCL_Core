using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.UI;

namespace UCL.Core.UI.Editor {
    public static class UCL_UICreator {
        [UnityEditor.MenuItem("GameObject/UI/UCL/Button")]
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
            
            var aBut = UCL_Button.Create(p);
            UnityEditor.Undo.RegisterCreatedObjectUndo(aBut.gameObject, "Create UCL_Button");
            aBut.m_Image.sprite = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            //UnityEditor.EditorGUIUtility.IconContent("[texturename]");
            UnityEditor.Selection.activeObject = aBut;
        }
        [UnityEditor.MenuItem("GameObject/UI/UCL/Image")]
        private static void CreateUCL_Image() {
            Object selectedObject = UnityEditor.Selection.activeObject;
            if(selectedObject != null) {
                Debug.LogWarning("CreateUCL_Button() selectedObject:" + selectedObject.name);
            }

            GameObject obj = selectedObject as GameObject;
            Transform p = null;
            if(obj != null) {
                p = obj.transform;
            }
            var but = UCL_Image.Create(p);
            UnityEditor.Selection.activeObject = but;
        }
    }
}