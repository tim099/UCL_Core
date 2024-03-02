
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/21 2024 17:10
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Cysharp.Threading.Tasks;
using System.IO;
using UCL.Core.JsonLib;

namespace UCL.Core
{
    public class UCL_StreamingAssetFileInspector : UCL.Core.JsonLib.UnityJsonSerializable, UCL.Core.UCLI_FileExplorer
    {

        [System.Serializable]
        public class FileInformation : UCL.Core.JsonLib.UnityJsonSerializable
        {
            /// <summary>
            /// file name(without extension)
            /// </summary>
            public string m_FileName;

            /// <summary>
            /// file extension
            /// </summary>
            public string m_Extension;

            /// <summary>
            /// RelativePath in Application.streamingAssetsPath
            /// </summary>
            public string RelativePath { get; set; }
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

                RelativePath = iFullPath.Substring(0, iFullPath.Length - aFileName.Length - 1);
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
            public string FullPath => System.IO.Path.Combine(RelativePath, FileName);
            /// <summary>
            /// Full path of file(without wxtension
            /// 完整檔案路徑(無副檔名
            /// </summary>
            public string FilePathWithoutExtension => System.IO.Path.Combine(RelativePath, Name);

        }

        [System.Serializable]
        public class FolderInformation : UCL.Core.JsonLib.UnityJsonSerializable, UCL.Core.UCLI_FileExplorer
        {
            /// <summary>
            /// folderName
            /// </summary>
            public string m_Name;
            /// <summary>
            /// folder path
            /// </summary>
            public string m_Path;

            public List<FileInformation> m_FileInfos = new List<FileInformation>();
            
            public List<FolderInformation> m_FolderInfos = new List<FolderInformation>();
            public string Name => m_Name;
            /// <summary>
            /// RelativePath in Application.streamingAssetsPath
            /// </summary>
            public string RelativePath => Path.Combine(m_Path, m_Name);

            public FolderInformation() { }
            public FolderInformation(string iPath, string iFileFormat, bool iIgnoreMetaFiles = true)
            {
                Init(iPath, iFileFormat, iIgnoreMetaFiles);
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
                var aFileSystemPath = Path.Combine(Application.streamingAssetsPath, m_Path);


                var aFiles = System.IO.Directory.GetFiles(aFileSystemPath, iFileFormat, System.IO.SearchOption.TopDirectoryOnly);
                for (int i = 0; i < aFiles.Length; i++)
                {
                    string aFilePath = aFiles[i];
                    var aFileInfo = new FileInformation(aFilePath);
                    if (iIgnoreMetaFiles && aFileInfo.m_Extension != "meta")
                    {
                        m_FileInfos.Add(aFileInfo);
                    }
                }
                var aDirs = System.IO.Directory.GetDirectories(aFileSystemPath);
                for (int i = 0; i < aDirs.Length; i++)
                {
                    string aDir = aDirs[i];
                    var aDirInfo = new DirectoryInfo(aDir);
                    m_FolderInfos.Add(new FolderInformation(Path.Combine(m_Path, aDirInfo.Name), iFileFormat, iIgnoreMetaFiles));
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
                    if (aFolderInfo.m_Name == aFolderPath.Item1)
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
                for (int i = 0; i < aInfos.Count; i++)
                {
                    aFiles.Add(aInfos[i].Name);
                }
                return aFiles;
            }

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

            public override void DeserializeFromJson(JsonData iJson)
            {
                base.DeserializeFromJson(iJson);
                var aRelativePath = RelativePath;
                foreach (var aFileInfo in m_FileInfos)
                {
                    aFileInfo.RelativePath = aRelativePath;
                }
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
                foreach (var aFolder in m_FolderInfos)
                {
                    if (aFolder.m_Name == aRootFolderName)
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
                            if (iPath.Length > aRootFolderName.Length) aSubDir = UCL.Core.FileLib.Lib.GetSubFolderName(iPath);
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
                        aDirs[i] = aFolderInfos[i].RelativePath;//FileLib.Lib.GetFolderName(aDirs[i]);
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
                    Debug.LogError("Folder Not Find!!aRootFolderName:" + aRootFolderName + ",iPath:" + iPath);
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
        public FolderInformation m_FolderInformations = new FolderInformation();

        public List<FileInformation> AllFileInfos => m_FolderInformations.AllFileInfos;


        /// <summary>
        /// Refresh FileInfos
        /// 更新檔案資料
        /// </summary>
        public void RefreshFileInfos()
        {
            string aFileFormat = "*";
            if (!string.IsNullOrEmpty(m_FileExtension))
            {
                aFileFormat = "*." + m_FileExtension;
            }
            m_FolderInformations = new FolderInformation(m_TargetDirectory, aFileFormat, m_IgnoreMetaFiles);
        }

        /// <summary>
        /// Get FileInfo by file name
        /// 根據檔名取得檔案資訊
        /// </summary>
        /// <param name="iFileName"></param>
        /// <returns></returns>
        public FileInformation GetFileInfo(string iFileName)
        {
            return m_FolderInformations.GetFileInfo(iFileName);
        }
        public List<FileInformation> GetAllFileInfos(string iPath) => m_FolderInformations.GetAllFileInfos(iPath);
        public List<string> GetAllFilesName(string iPath) => m_FolderInformations.GetAllFilesName(iPath);

        #region Interface
        virtual public bool DirectoryExists(string iPath) => m_FolderInformations.DirectoryExists(iPath);
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
            => m_FolderInformations.GetDirectories(iPath, iSearchPattern, iSearchOption, iRemoveRootPath);
        /// <summary>
        /// Returns the names of files (including their paths) that match the specified search pattern in the specified directory.
        /// </summary>
        /// <param name="iPath">The relative or absolute path to the directory to search. This string is not case-sensitive.</param>
        /// <param name="iSearchPattern">The search string to match against the names of files in path. This parameter can contain a combination of valid literal path
        /// and wildcard (* and ?) characters, but it doesn't support regular expressions.</param>
        /// <returns></returns>
        virtual public string[] GetFiles(string iPath, string iSearchPattern = "*", SearchOption iSearchOption = SearchOption.TopDirectoryOnly,
            bool iRemoveRootPath = false) => m_FolderInformations.GetFiles(iPath, iSearchPattern, iSearchOption, iRemoveRootPath);
        #endregion
    }
}