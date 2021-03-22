using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
namespace UCL.Core.FileLib {
    [UCL.Core.ATTR.EnableUCLEditor]
    [CreateAssetMenu(fileName = "New FileCopier", menuName = "UCL/FileCopier")]
    public class UCL_FileCopier : ScriptableObject {
        public string m_SourceDirectory = "";
        public string m_DestinationDirectory = "";
        /// <summary>
        /// File with this extensions will be ignore
        /// </summary>
        public List<string> mIgnoreFileExtensions = null;
        /// <summary>
        /// Copy files from SourceDirectory to DestinationDirectory
        /// </summary>
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void Copy() {
            UCL.Core.FileLib.Lib.CopyDirectory(m_SourceDirectory, m_DestinationDirectory, mIgnoreFileExtensions);
        }
#if UNITY_EDITOR
        /// <summary>
        /// Explore SourceDirectory
        /// </summary>
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void ExploreSourceDirectory() {
            if(string.IsNullOrEmpty(m_SourceDirectory)) {
                var path = UCL.Core.EditorLib.AssetDatabaseMapper.GetAssetPath(this);
                m_SourceDirectory = FileLib.Lib.RemoveFolderPath(path, 1);
            }
            var dir = Core.FileLib.EditorLib.OpenAssetsFolderExplorer(m_SourceDirectory);
            if(!string.IsNullOrEmpty(dir)) {
                m_SourceDirectory = dir;
            }
        }
        /// <summary>
        /// Explore DestinationDirectory
        /// </summary>
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void ExploreDestinationDirectory() {
            if(string.IsNullOrEmpty(m_DestinationDirectory)) {
                var path = UCL.Core.EditorLib.AssetDatabaseMapper.GetAssetPath(this);
                m_DestinationDirectory = FileLib.Lib.RemoveFolderPath(path, 1);
            }
            var dir = Core.FileLib.EditorLib.OpenAssetsFolderExplorer(m_DestinationDirectory);
            if(!string.IsNullOrEmpty(dir)) {
                m_DestinationDirectory = dir;
            }
        }
#endif
    }
}