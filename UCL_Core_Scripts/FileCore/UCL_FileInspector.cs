using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.FileLib {
    [UCL.Core.ATTR.EnableUCLEditor]
    [CreateAssetMenu(fileName = "New FileInspector", menuName = "UCL/FileInspector")]
    public class UCL_FileInspector : ScriptableObject {
        [RuntimeInitializeOnLoadMethod]
        public static void UpdateFilesData() {

        }
        [System.Serializable]
        public class FileInformation {
            public FileInformation(string _FolderPath, string _FileName, string _Extension) {
                m_FolderPath = _FolderPath;
                m_FileName = _FileName;
                m_Extension = _Extension;
            }
            /// <summary>
            /// File full name, include extension
            /// 完整檔名 包含副檔名
            /// </summary>
            public string FileName {
                get { return m_FileName + "." + m_Extension; }
            }
            /// <summary>
            /// File name, exclude extension
            /// 檔名 不包含副檔名
            /// </summary>
            public string Name {
                get { return m_FileName; }
            }

            /// <summary>
            /// Full path of file
            /// 完整檔案路徑
            /// </summary>
            public string FilePath {
                get { return System.IO.Path.Combine(m_FolderPath, FileName); }
            }

            public string m_FileName;
            public string m_FolderPath;
            public string m_Extension;
        }
        /// <summary>
        /// ignore the meta files in dir
        /// 無視meta檔
        /// </summary>
        public bool m_IgnoreMetaFiles = true;
        /// <summary>
        /// Target directory, if m_TargetDirectory == "" then will set to the folder FileInspector located
        /// 目標路徑 會抓取該路徑下的所有檔案
        /// </summary>
        public string m_TargetDirectory = "";

        /// <summary>
        /// File Infos
        /// 檔案資訊
        /// </summary>
        public List<FileInformation> m_FileInfos = new List<FileInformation>();

        private Dictionary<string, FileInformation> m_FileInfosDic = null;
#if UNITY_EDITOR
        /// <summary>
        /// Explore TargetDirectory
        /// 開啟檔案瀏覽器設定目標路徑
        /// </summary>
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void ExploreTargetDirectory() {
            if(string.IsNullOrEmpty(m_TargetDirectory)) {
                var path = UnityEditor.AssetDatabase.GetAssetPath(this);
                m_TargetDirectory = FileLib.Lib.RemoveFolderPath(path, 1);
            }
            var dir = Core.FileLib.EditorLib.OpenAssetsFolderExplorer(m_TargetDirectory);
            if(!string.IsNullOrEmpty(dir)) {
                m_TargetDirectory = dir;
            }
        }
#endif
        /// <summary>
        /// Refresh FileInfos
        /// 更新檔案資料
        /// </summary>
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void RefreshFileInfos() {
#if UNITY_EDITOR
            if(string.IsNullOrEmpty(m_TargetDirectory)) {
                var path = UnityEditor.AssetDatabase.GetAssetPath(this);
                m_TargetDirectory = FileLib.Lib.RemoveFolderPath(path, 1);
            }

            m_FileInfos = new List<FileInformation>();
            var files = System.IO.Directory.GetFiles(m_TargetDirectory, "*", System.IO.SearchOption.AllDirectories);
            for(int i = 0; i < files.Length; i++) {
                var file = files[i].Substring(m_TargetDirectory.Length + 1);
                var file_extension = FileLib.Lib.GetFileExtension(file);
                if(m_IgnoreMetaFiles && file_extension != "meta") {
                    var result = FileLib.Lib.SplitPath(file, 1);
                    m_FileInfos.Add(new FileInformation(result.Item1,
                        result.Item2.Substring(0, result.Item2.Length - file_extension.Length - 1),
                        file_extension));
                } 
            }
            RefreshFileInfosDic();
#endif
        }
        /// <summary>
        /// Refresh FileInfosDic
        /// 更新FileInfosDic
        /// </summary>
        private void RefreshFileInfosDic() {
            m_FileInfosDic = new Dictionary<string, FileInformation>();
            for(int i = 0; i < m_FileInfos.Count; i++) {
                FileInformation info = m_FileInfos[i];
                m_FileInfosDic[info.Name] = info;
            }
        }
        /// <summary>
        /// Get FileInfo by file name
        /// 根據檔名取得檔案資訊
        /// </summary>
        /// <param name="file_name"></param>
        /// <returns></returns>
        public FileInformation GetFileInfo(string file_name) {
            if(m_FileInfos == null) return null;
            if(m_FileInfosDic == null || m_FileInfosDic.Count != m_FileInfos.Count) {
                RefreshFileInfosDic();
            }
            if(m_FileInfosDic.ContainsKey(file_name)) {
                return m_FileInfosDic[file_name];
            }
            return null;
        }
    }
}

