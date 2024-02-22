
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/22 2024 13:11
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UCL.Core;
using UnityEngine;

namespace UCL.Core
{
    public class UCL_StreamingAssetsFileData
    {
        /// <summary>
        /// 檔案格式(例如json)
        /// </summary>
        public string m_FileFormat = "json";
        /// <summary>
        /// 資料夾路徑
        /// </summary>
        [UCL.Core.PA.UCL_FolderExplorer(typeof(UCL_StreamingAssets), UCL_StreamingAssets.ReflectKeyStreamingAssetsPath)]
        public string m_FolderPath = string.Empty;
        public SearchOption m_SearchOption = SearchOption.TopDirectoryOnly;
        /// <summary>
        /// 緩存的檔案ID
        /// </summary>
        List<string> m_FileIDs = null;
        /// <summary>
        /// 緩存的檔名
        /// </summary>
        List<string> m_FileNames = null;
        #region static
        //static Dictionary<string, RCG_FileData> s_FileDatasDic = null;
        public static UCL_StreamingAssetsFileData GetFileData(string iFolderPath, string iFileFormat = "json")
        {
            return new UCL_StreamingAssetsFileData(iFolderPath, iFileFormat);
        }
        #endregion


        public UCL_StreamingAssetsFileData() { }
        public UCL_StreamingAssetsFileData(string iFolderPath, string iFileFormat = "json")
        {
            m_FolderPath = iFolderPath;
            m_FileFormat = iFileFormat;
        }
        /// <summary>
        /// 抓取所有檔名(不含副檔名
        /// </summary>
        /// <param name="iIsUseCache"></param>
        /// <returns></returns>
        public List<string> GetFileIDs(bool iIsUseCache = true)
        {
            if (m_FileIDs == null || !iIsUseCache)
            {
                UCL_StreamingAssets.CheckAndCreateDirectory(m_FolderPath);
                //Debug.LogError("m_FolderPath:" + m_FolderPath);
                m_FileIDs = UCL_StreamingAssets.GetFilesName(m_FolderPath, "*." + m_FileFormat);
            }
            return m_FileIDs;
        }
        public const string CommonDataMetaName = ".CommonDataMeta";
        public string CommonDataMetaPath => System.IO.Path.Combine(m_FolderPath, CommonDataMetaName);
        public string GetCommonDataMetaJson()
        {
            UCL_StreamingAssets.CheckAndCreateDirectory(m_FolderPath);
            if (!UCL_StreamingAssets.FileExists(CommonDataMetaPath))
            {
                return string.Empty;
            }
            return UCL_StreamingAssets.ReadAllText(CommonDataMetaPath);
        }
        public void SaveCommonDataMetaJson(string iJson)
        {
            UCL_StreamingAssets.CheckAndCreateDirectory(m_FolderPath);
            UCL_StreamingAssets.WriteAllText(CommonDataMetaPath, iJson);
        }
        /// <summary>
        /// 抓取所有檔名(含副檔名
        /// </summary>
        /// <param name="iIsUseCache"></param>
        /// <returns></returns>
        public List<string> GetFileNames(bool iIsUseCache = true)
        {
            if (m_FileNames == null || !iIsUseCache)
            {
                //RCG_StreamingAssets.CheckAndCreateDirectory(m_FolderPath);
                //Debug.LogError("m_FolderPath:" + m_FolderPath);
                m_FileNames = UCL_StreamingAssets.GetFilesName(m_FolderPath, "*." + m_FileFormat, false);
            }
            return m_FileNames;
        }
        /// <summary>
        /// 根據ID抓取檔案路徑(ID = 檔名去掉副檔名部分)
        /// </summary>
        /// <param name="iID"></param>
        /// <returns></returns>
        public string GetSavePath(string iID)
        {
            return string.Format("{0}/{1}.{2}", m_FolderPath, iID, m_FileFormat);
        }
        /// <summary>
        /// 根據ID存檔
        /// </summary>
        /// <param name="iID"></param>
        /// <param name="iContents"></param>
        public void WriteAllText(string iID, string iContents)
        {
            //RCG_StreamingAssets.CheckAndCreateDirectory(m_FolderPath);
            UCL_StreamingAssets.WriteAllText(GetSavePath(iID), iContents);
        }

        /// <summary>
        /// 根據ID讀檔
        /// </summary>
        /// <param name="iID"></param>
        /// <returns></returns>
        public string ReadAllText(string iID)
        {
            return UCL_StreamingAssets.ReadAllText(GetSavePath(iID));
        }
        /// <summary>
        /// 根據ID刪除檔案
        /// </summary>
        /// <param name="path"></param>
        public void DeleteFile(string iID)
        {
            UCL_StreamingAssets.DeleteFile(GetSavePath(iID));
        }
        /// <summary>
        /// 根據ID判斷檔案是否存在
        /// </summary>
        /// <param name="iPath"></param>
        /// <returns></returns>
        public bool FileExists(string iID)
        {
            return UCL_StreamingAssets.FileExists(GetSavePath(iID));
        }
        /// <summary>
        /// 抓取系統中實際檔案路徑
        /// </summary>
        /// <param name="iID"></param>
        /// <returns></returns>
        public string GetFileSystemPath(string iID)
        {
            return UCL_StreamingAssets.GetFileSystemPath(GetSavePath(iID));
        }
    }
}
