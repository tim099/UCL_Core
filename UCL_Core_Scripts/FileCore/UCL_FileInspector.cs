using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UCL.Core.FileLib {
    [UCL.Core.ATTR.EnableUCLEditor]
    [CreateAssetMenu(fileName = "New FileInspector", menuName = "UCL/FileInspector")]
    public class UCL_FileInspector : ScriptableObject, ISerializationCallbackReceiver {

        [System.Serializable]
        public class FileInformation {
            public FileInformation() { }
            public FileInformation(string iFileName, string iPath, string iExtension) {
                m_FileName = iFileName;
                m_Path = iPath;
                m_Extension = iExtension;
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


            public string m_Path;
            /// <summary>
            /// Full path of file
            /// 完整檔案路徑
            /// </summary>
            public string FilePath => System.IO.Path.Combine(m_Path, FileName);
            /// <summary>
            /// Full path of file(without wxtension
            /// 完整檔案路徑(無副檔名
            /// </summary>
            public string FilePathWithoutExtension => System.IO.Path.Combine(m_Path, Name);
            //public FolderInformation FolderInformation { get; set; }
            public string m_FileName;

            public string m_Extension;
        }
        [System.Serializable]
        public class SerializableFolderInformation
        {
            public string m_Name;
            public string m_Path;
            public List<FileInformation> m_FileInfos = new List<FileInformation>();
            public int m_FolderInfosCount = 0;
        }
        [System.Serializable]
        public class FolderInformation
        {
            public FolderInformation() { }
            public FolderInformation(string iPath, string iFileFormat, bool iIgnoreMetaFiles) {
                Init(iPath, iFileFormat, iIgnoreMetaFiles);
            }
            public FolderInformation(List<SerializableFolderInformation> iData)
            {
                if (iData.Count > 0)
                {
                    int aFolderInfosCount = 0;
                    {
                        var aData = iData[0];
                        m_Name = aData.m_Name;
                        m_Path = aData.m_Path;
                        m_FileInfos = aData.m_FileInfos;
                        aFolderInfosCount = aData.m_FolderInfosCount;
                        iData.RemoveAt(0);
                    }

                    for(int i = 0; i < aFolderInfosCount; i++)
                    {
                        m_FolderInfos.Add(new FolderInformation(iData));
                    }
                }
            }
            public List<SerializableFolderInformation> GetSerializableFolderInformation()
            {
                List<SerializableFolderInformation> aResult = new List<SerializableFolderInformation>();
                SerializableFolderInformation aData = new SerializableFolderInformation();
                aData.m_Name = m_Name;
                aData.m_Path = m_Path;
                aData.m_FileInfos = m_FileInfos;
                aData.m_FolderInfosCount = m_FolderInfos.Count;
                aResult.Add(aData);
                foreach (var aFolder in m_FolderInfos)
                {
                    aResult.Append(aFolder.GetSerializableFolderInformation());
                }

                return aResult;
            }
            public List<FileInformation> AllFileInfos
            {
                get
                {
                    var m_AllFileInfos = new List<FileInformation>();
                    //for (int i = 0; i < m_FileInfos.Count; i++) m_FileInfos[i].FolderInformation = this;
                    m_AllFileInfos.Append(m_FileInfos);
                    for (int i = 0; i < m_FolderInfos.Count; i++)
                    {
                        m_AllFileInfos.Append(m_FolderInfos[i].AllFileInfos);
                    }
                    return m_AllFileInfos;
                }
            }
            public void Init(string iPath, string iFileFormat, bool iIgnoreMetaFiles)
            {
                m_Path = iPath;
                m_Name = UCL.Core.FileLib.Lib.GetFolderName(m_Path);

                var aFiles = System.IO.Directory.GetFiles(m_Path, iFileFormat, System.IO.SearchOption.TopDirectoryOnly);
                for (int i = 0; i < aFiles.Length; i++)
                {
                    var aFile = aFiles[i].Substring(m_Path.Length + 1);
                    var aFileExtension = FileLib.Lib.GetFileExtension(aFile);
                    if (iIgnoreMetaFiles && aFileExtension != "meta")
                    {
                        var aResult = FileLib.Lib.SplitPath(aFile, 1);
                        m_FileInfos.Add(new FileInformation(aResult.Item2.Substring(0, aResult.Item2.Length - aFileExtension.Length - 1),
                            m_Path,
                            aFileExtension));
                    }
                }
                var aDirs = System.IO.Directory.GetDirectories(m_Path);
                for (int i = 0; i < aDirs.Length; i++)
                {
                    string aDir = aDirs[i];
                    m_FolderInfos.Add(new FolderInformation(aDir, iFileFormat, iIgnoreMetaFiles));
                }
            }
            public List<FileInformation> GetAllFileInfos(string iPath)
            {
                //Debug.LogError("GetFiles:" + iPath);
                if (string.IsNullOrEmpty(iPath))
                {
                    return AllFileInfos;
                }
                var aFolderPath = UCL.Core.FileLib.Lib.SplitPath(iPath, -1);
                //Debug.LogError("aFolderPath:" + aFolderPath.Item1+",2:"+ aFolderPath.Item2);
                for (int i = 0; i < m_FolderInfos.Count; i++)
                {
                    var aFolderInfo = m_FolderInfos[i];
                    if(aFolderInfo.m_Name == aFolderPath.Item1)
                    {
                        return aFolderInfo.GetAllFileInfos(aFolderPath.Item2);
                    }
                }
                return new List<FileInformation>();
            }
            /// <summary>
            /// folderName
            /// </summary>
            public string m_Name;
            public string m_Path;
            public List<FileInformation> m_FileInfos = new List<FileInformation>();
            [HideInInspector] public List<FolderInformation> m_FolderInfos = new List<FolderInformation>();

            /// <summary>
            /// Get FileInfo by file name
            /// 根據檔名取得檔案資訊
            /// </summary>
            /// <param name="iFileName"></param>
            /// <returns></returns>
            public FileInformation GetFileInfo(string iFileName)
            {
                if (!m_FileInfos.IsNullOrEmpty())
                {
                    for (int i = 0; i < m_FileInfos.Count; i++)
                    {
                        var aInfo = m_FileInfos[i];
                        if (aInfo.FileName == iFileName)
                        {
                            //aInfo.FolderInformation = this;
                            return aInfo;
                        }
                    }
                }
                if (!m_FolderInfos.IsNullOrEmpty())
                {
                    for (int i = 0; i < m_FolderInfos.Count; i++)
                    {
                        var aInfo = m_FolderInfos[i].GetFileInfo(iFileName);
                        if (aInfo != null) return aInfo;
                    }
                }

                return null;
            }
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
        /// Target file extension, if m_FileExtension == string.Empty then don't check file extensions
        /// 目標副檔名 若為空時則不檢查副檔名
        /// </summary>
        public string m_FileExtension = string.Empty;
        /// <summary>
        /// Folder Infos
        /// 資料夾資訊
        /// </summary>
        [HideInInspector]
        public FolderInformation FolderInformations { get; protected set; } = new FolderInformation();

        public List<FileInformation> AllFileInfos => FolderInformations.AllFileInfos;
#if UNITY_EDITOR
        /// <summary>
        /// Explore TargetDirectory
        /// 開啟檔案瀏覽器設定目標路徑
        /// </summary>
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void ExploreTargetDirectory() {
            if(string.IsNullOrEmpty(m_TargetDirectory)) {
                var aPath = UCL.Core.EditorLib.AssetDatabaseMapper.GetAssetPath(this);
                m_TargetDirectory = FileLib.Lib.RemoveFolderPath(aPath, 1);
            }
            var dir = Core.FileLib.EditorLib.OpenAssetsFolderExplorer(m_TargetDirectory);
            if(!string.IsNullOrEmpty(dir)) {
                m_TargetDirectory = dir;
            }
        }
#endif
        //[SerializeField] string m_SerializeData = string.Empty;
        [SerializeField] List<SerializableFolderInformation> m_SerializableFolderInformation = new List<SerializableFolderInformation>();
        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            m_SerializableFolderInformation = FolderInformations.GetSerializableFolderInformation();
            //Debug.LogWarning("OnBeforeSerialize:" + m_SerializableFolderInformation.Count);
            //m_SerializeData = UCL.Core.JsonLib.JsonConvert.SaveDataToJson(m_FolderInformation).ToJson();
            //Debug.LogWarning("OnBeforeSerialize:" + m_SerializeData);
            //m_FolderInformation = null;
        }
        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            FolderInformations = new FolderInformation(m_SerializableFolderInformation);
            //Debug.LogWarning("OnAfterDeserialize:" + m_SerializableFolderInformation.Count);
            //if (!string.IsNullOrEmpty(m_SerializeData))
            //{
            //    m_FolderInformation = UCL.Core.JsonLib.JsonConvert.LoadDataFromJson<FolderInformation>(JsonLib.JsonData.ParseJson(m_SerializeData));
            //}
            //m_SerializeData = null;
        }
        /// <summary>
        /// Refresh FileInfos
        /// 更新檔案資料
        /// </summary>
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void RefreshFileInfos() {
#if UNITY_EDITOR
            if(string.IsNullOrEmpty(m_TargetDirectory)) {
                var aPath = UCL.Core.EditorLib.AssetDatabaseMapper.GetAssetPath(this);
                m_TargetDirectory = FileLib.Lib.RemoveFolderPath(aPath, 1);
            }
            string aFileFormat = "*";
            if (!string.IsNullOrEmpty(m_FileExtension))
            {
                aFileFormat = "*." + m_FileExtension;
            }
            FolderInformations = new FolderInformation(m_TargetDirectory, aFileFormat, m_IgnoreMetaFiles);

            UCL.Core.EditorLib.EditorUtilityMapper.SetDirty(this);
#endif
        }

        /// <summary>
        /// Get FileInfo by file name
        /// 根據檔名取得檔案資訊
        /// </summary>
        /// <param name="iFileName"></param>
        /// <returns></returns>
        public FileInformation GetFileInfo(string iFileName) {
            return FolderInformations.GetFileInfo(iFileName);
        }
    }
}

