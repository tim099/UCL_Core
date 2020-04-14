using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UCL.Core.Misc {
    public class UCL_EditorOnly : MonoBehaviour {
#if !UNITY_EDITOR
        private void Awake() {
            Destroy(gameObject);//if not root GameObject, destroy manually
        }
#endif

#if UNITY_EDITOR
        void SetEditorOnlyTag() {
            if(gameObject.tag == "EditorOnly") return;
            gameObject.tag = "EditorOnly";
            UnityEditor.EditorUtility.SetDirty(gameObject);
        }
        private void OnValidate() {
            SetEditorOnlyTag();
        }
#endif
    }
}