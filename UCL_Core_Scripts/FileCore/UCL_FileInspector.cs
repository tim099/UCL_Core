using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
namespace UCL.Core.FileLib {
    [UCL.Core.ATTR.EnableUCLEditor]
    [CreateAssetMenu(fileName = "New FileInspector", menuName = "UCL/FileInspector")]
    public class UCL_FileInspector : ScriptableObject, ISerializationCallbackReceiver, UCL.Core.UCLI_FileExplorer
    {

        [System.Serializable]
        public class FileInformation {
            /// <summary>
            /// file name(without extension)
            /// </summary>
            public string m_FileName;

            /// <summary>
            /// file extension
            /// </summary>
            public string m_Extension;

            /// <summary>
            /// folder path
            /// </summary>
            public string m_Path;
            public FileInformation() { }
            public FileInformation(string iFullPath)
            {
                if (string.IsNullOrEmpty(iFullPath))
                {
                    return;
                }
                var aFileName = FileLib.Lib.GetFileName(iFullPath);
                if (!string.IsNullOrEmpty(aFileName))
                {
                    m_Extension = FileLib.Lib.GetFileExtension(aFileName);
                    m_FileName = aFileName.Substring(0, aFileName.Length - m_Extension.Length - 1);
                }

                m_Path = iFullPath.Substring(0, iFullPath.Length - aFileName.Length - 1);
            }
            public FileInformation(string iFileName, string iPath, string iExtension) {
                m_FileName = iFileName;
                m_Path = iPath;
                m_Extension = iExtension;
            }
            /// <summary>
            /// File full name, include extension
            /// 完整檔名 包含副檔名
            /// </summary>
            public string FileName => m_FileName + "." + m_Extension;

            /// <summary>
            /// File name, exclude extension
            /// 檔名 不包含副檔名
            /// </summary>
            public string Name => m_FileName;


            /// <summary>
            /// Full path of file
            /// 完整檔案路徑
            /// </summary>
            public string FullPath => System.IO.Path.Combine(m_Path, FileName);
            /// <summary>
            /// Full path of file(without wxtension
            /// 完整檔案路徑(無副檔名
            /// </summary>
            public string FilePathWithoutExtension => System.IO.Path.Combine(m_Path, Name);

        }
        [System.Serializable]
        public class SerializableFolderInformation
        {
            /// <summary>
            /// folderName
            /// </summary>
            public string m_Name;
            /// <summary>
            /// folder path
            /// </summary>
            public string m_Path;
            [NonReorderable]
            public List<FileInformation> m_FileInfos = new List<FileInformation>();
            public int m_SubDirCount = 0;

            public string Name => m_Name;
            public string FullPath => m_Path + "/" + m_Name;
        }
        public class FolderInformation : SerializableFolderInformation, UCL.Core.UCLI_FileExplorer
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
                        m_FileInfos = new List<FileInformation>();

                        m_FileInfos = aData.m_FileInfos;
                        
                        aFolderInfosCount = aData.m_SubDirCount;
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
                m_SubDirCount = m_FolderInfos.Count;
                aResult.Add(this);
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
                    var aFileInfo = new FileInformation(aFiles[i]);
                    //var aFile = aFiles[i].Substring(m_Path.Length + 1);
                    //var aFileExtension = FileLib.Lib.GetFileExtension(aFile);
                    if (iIgnoreMetaFiles && aFileInfo.m_Extension != "meta")
                    {
                        m_FileInfos.Add(aFileInfo);
                        //var aResult = FileLib.Lib.SplitPath(aFile, 1);
                        //m_FileInfos.Add(new FileInformation(aResult.Item2.Substring(0, aResult.Item2.Length - aFileExtension.Length - 1),
                        //    m_Path,
                        //    aFileExtension));
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
            public List<string> GetAllFilesName(string iPath)
            {
                List<string> aFiles = new List<string>();
                var aInfos = GetAllFileInfos(iPath);
                for(int i = 0; i < aInfos.Count; i++)
                {
                    aFiles.Add(aInfos[i].Name);
                }
                return aFiles;
            }
            
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
            public List<FolderInformation> GetFolderInfos(SearchOption iSearchOption = SearchOption.AllDirectories)
            {
                List<FolderInformation> aFolderInfos = new List<FolderInformation>();

                switch (iSearchOption)
                {
                    case SearchOption.TopDirectoryOnly:
                        {
                            foreach (var aDir in m_FolderInfos)
                            {
                                aFolderInfos.Add(aDir);
                            }
                            break;
                        }
                    case SearchOption.AllDirectories:
                        {
                            foreach (var aDir in m_FolderInfos)
                            {
                                aFolderInfos.Add(aDir);
                                aFolderInfos.Append(aDir.GetFolderInfos(iSearchOption));
                            }
                            break;
                        }
                }
                return aFolderInfos;
            }
            public List<FileInformation> GetFileInfos(SearchOption iSearchOption = SearchOption.AllDirectories)
            {
                List<FileInformation> aFileInfos = new List<FileInformation>();

                switch (iSearchOption)
                {
                    case SearchOption.TopDirectoryOnly:
                        {
                            foreach (var aFileInfo in m_FileInfos)
                            {
                                aFileInfos.Add(aFileInfo);
                            }
                            break;
                        }
                    case SearchOption.AllDirectories:
                        {
                            foreach (var aFileInfo in m_FileInfos)
                            {
                                aFileInfos.Add(aFileInfo);
                            }
                            foreach (var aDir in m_FolderInfos)
                            {
                                aFileInfos.Append(aDir.GetFileInfos(iSearchOption));
                            }
                            break;
                        }
                }
                return aFileInfos;
            }
            #region Interface
            virtual public bool DirectoryExists(string iPath)
            {
                if (string.IsNullOrEmpty(iPath))//this dir
                {
                    return true;
                }
                //m_FolderInfos
                var aRootFolderName = UCL.Core.FileLib.Lib.GetRootFolderName(iPath);
                foreach(var aFolder in m_FolderInfos)
                {
                    if(aFolder.m_Name == aRootFolderName)
                    {
                        if (aRootFolderName == iPath) return true;//Target folder find!!
                        
                        string aSubFolderPath = UCL.Core.FileLib.Lib.GetSubFolderName(iPath);
                        //Debug.LogError("aSubFolderPath:"+ aSubFolderPath);
                        return aFolder.DirectoryExists(aSubFolderPath);
                    }
                }
                //Debug.LogError("!DirectoryExists iPath:" + iPath+ ",aRootFolderName:"+ aRootFolderName
                    //+ ",m_FolderInfos:"+ m_FolderInfos.ConcatString((iFolderInfo)=>iFolderInfo.m_Name));
                return false;
            }
            /// <summary>
            /// Returns the names of the subdirectories (including their paths) 
            /// that match the specified search pattern in the specified directory, and optionally searches subdirectories.
            /// </summary>
            /// <param name="iPath">The relative or absolute path to the directory to search. This string is not case-sensitive.</param>
            /// <param name="iSearchPattern">The search string to match against the names of subdirectories in path.
            /// This parameter can contain a combination of valid literal and wildcard characters, but it doesn't support regular expressions.</param>
            /// <param name="iSearchOption">One of the enumeration values that specifies whether the search operation 
            /// should include all subdirectories or only the current directory.</param>
            /// <returns></returns>
            virtual public string[] GetDirectories(string iPath, string iSearchPattern = "*",
                    SearchOption iSearchOption = SearchOption.AllDirectories, bool iRemoveRootPath = false)
            {
                if (!string.IsNullOrEmpty(iPath))
                {
                    if (!DirectoryExists(iPath)) return new string[0];
                    var aRootFolderName = UCL.Core.FileLib.Lib.GetRootFolderName(iPath);
                    foreach (var aFolder in m_FolderInfos)
                    {
                        if (aFolder.Name == aRootFolderName)
                        {
                            string aSubDir = string.Empty;
                            if(iPath.Length > aRootFolderName.Length) aSubDir = UCL.Core.FileLib.Lib.GetSubFolderName(iPath);
                            //Debug.LogError("aSubDir:" + aSubDir);
                            return aFolder.GetDirectories(aSubDir, iSearchPattern, iSearchOption, iRemoveRootPath);
                        }
                    }
                    return new string[0];//Folder Not Find!!
                }


                List<FolderInformation> aFolderInfos = GetFolderInfos(iSearchOption);

                var aDirs = new string[aFolderInfos.Count];//Directory.GetDirectories(iPath, iSearchPattern, iSearchOption);
                if (iRemoveRootPath)
                {
                    for (int i = 0; i < aDirs.Length; i++)
                    {
                        aDirs[i] = aFolderInfos[i].Name;//FileLib.Lib.GetFolderName(aDirs[i]);
                    }
                }
                else
                {
                    for (int i = 0; i < aDirs.Length; i++)
                    {
                        aDirs[i] = aFolderInfos[i].FullPath;//FileLib.Lib.GetFolderName(aDirs[i]);
                    }
                }
                //Debug.LogError("iPath:"+ iPath + ",aDirs:" + aDirs.ConcatString());
                return aDirs;
            }
            /// <summary>
            /// Returns the names of files (including their paths) that match the specified search pattern in the specified directory.
            /// </summary>
            /// <param name="iPath">The relative or absolute path to the directory to search. This string is not case-sensitive.</param>
            /// <param name="iSearchPattern">The search string to match against the names of files in path. This parameter can contain a combination of valid literal path
            /// and wildcard (* and ?) characters, but it doesn't support regular expressions.</param>
            /// <returns></returns>
            virtual public string[] GetFiles(string iPath, string iSearchPattern = "*", SearchOption iSearchOption = SearchOption.TopDirectoryOnly,
                bool iRemoveRootPath = false)
            {
                if (!DirectoryExists(iPath))
                {
                    Debug.LogError("!DirectoryExists iPath:" + iPath);
                    return new string[0];
                }
                if (!string.IsNullOrEmpty(iPath))
                {
                    var aRootFolderName = UCL.Core.FileLib.Lib.GetRootFolderName(iPath);
                    foreach (var aFolder in m_FolderInfos)
                    {
                        if (aFolder.Name == aRootFolderName)
                        {
                            string aSubDir = string.Empty;
                            if (iPath.Length > aRootFolderName.Length) aSubDir = UCL.Core.FileLib.Lib.GetSubFolderName(iPath);
                            //Debug.LogError("aSubDir:" + aSubDir);
                            return aFolder.GetFiles(aSubDir, iSearchPattern, iSearchOption, iRemoveRootPath);
                        }
                    }
                    Debug.LogError("Folder Not Find!!aRootFolderName:" + aRootFolderName+ ",iPath:"+ iPath);
                    return new string[0];//Folder Not Find!!
                }
                var aFileInfos = GetFileInfos(iSearchOption);

                var aFilePaths = new string[aFileInfos.Count];//Directory.GetFiles(iPath, iSearchPattern, iSearchOption);
                if (iRemoveRootPath)
                {
                    for (int i = 0; i < aFileInfos.Count; i++)
                    {
                        aFilePaths[i] = aFileInfos[i].FileName;
                    }
                }
                else
                {
                    for (int i = 0; i < aFileInfos.Count; i++)
                    {
                        aFilePaths[i] = aFileInfos[i].FullPath;
                    }
                }
                return aFilePaths;
            }
            #endregion
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
        [UCL.Core.PA.UCL_FolderExplorer]
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
        [NonReorderable]
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
        public List<FileInformation> GetAllFileInfos(string iPath) => FolderInformations.GetAllFileInfos(iPath);
        public List<string> GetAllFilesName(string iPath) => FolderInformations.GetAllFilesName(iPath);

        #region Interface
        virtual public bool DirectoryExists(string iPath) => FolderInformations.DirectoryExists(iPath);
        /// <summary>
        /// Returns the names of the subdirectories (including their paths) 
        /// that match the specified search pattern in the specified directory, and optionally searches subdirectories.
        /// </summary>
        /// <param name="iPath">The relative or absolute path to the directory to search. This string is not case-sensitive.</param>
        /// <param name="iSearchPattern">The search string to match against the names of subdirectories in path.
        /// This parameter can contain a combination of valid literal and wildcard characters, but it doesn't support regular expressions.</param>
        /// <param name="iSearchOption">One of the enumeration values that specifies whether the search operation 
        /// should include all subdirectories or only the current directory.</param>
        /// <returns></returns>
        virtual public string[] GetDirectories(string iPath, string iSearchPattern = "*",
                SearchOption iSearchOption = SearchOption.AllDirectories, bool iRemoveRootPath = false)
            => FolderInformations.GetDirectories(iPath, iSearchPattern, iSearchOption, iRemoveRootPath);
        /// <summary>
        /// Returns the names of files (including their paths) that match the specified search pattern in the specified directory.
        /// </summary>
        /// <param name="iPath">The relative or absolute path to the directory to search. This string is not case-sensitive.</param>
        /// <param name="iSearchPattern">The search string to match against the names of files in path. This parameter can contain a combination of valid literal path
        /// and wildcard (* and ?) characters, but it doesn't support regular expressions.</param>
        /// <returns></returns>
        virtual public string[] GetFiles(string iPath, string iSearchPattern = "*", SearchOption iSearchOption = SearchOption.TopDirectoryOnly,
            bool iRemoveRootPath = false) => FolderInformations.GetFiles(iPath, iSearchPattern, iSearchOption, iRemoveRootPath);
        #endregion
    }
}

