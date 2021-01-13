using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace UCL.Core.FileLib.Demo {
    [UCL.Core.ATTR.EnableUCLEditor]
    public class UCL_FileLibDemo : MonoBehaviour {

        public string m_Path = "";

#if UNITY_EDITOR
        private void Reset() {
        }

        [UCL.Core.ATTR.UCL_FunctionButton]
        public void SelectPath() {
            m_Path = Core.FileLib.EditorLib.OpenAssetsFolderExplorer(m_Path);
        }

        [UCL.Core.ATTR.UCL_FunctionButton]
        public void GetAllFoldersAtPath() {
            var dirs = Core.FileLib.Lib.GetDirectories(m_Path);
            foreach(var dir in dirs) {
                Debug.LogWarning("dir:" + dir);
            }
        }
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void GetAllFilesAtPath() {
            var dirs = Core.FileLib.Lib.GetFiles(m_Path);
            foreach(var dir in dirs) {
                Debug.LogWarning("file:" + dir);
            }
        }
#endif
    }
}

