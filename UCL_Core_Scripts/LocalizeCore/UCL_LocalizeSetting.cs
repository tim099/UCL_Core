using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UCL.Core.LocalizeLib
{
    [System.Serializable]
    public class UCL_LocalizeSetting
    {
        const string DownloadTemplate = "https://docs.google.com/spreadsheets/d/{0}/export?format=csv&gid={1}";
        /// <summary>
        /// Table id on Google Spreadsheet.
        /// etc. https://docs.google.com/spreadsheets/d/1zLXwb8ASmI0B5_GxuUtQUopPFEOE29K18jp9mC9Auxo/edit#gid=0
        /// TableId = 1zLXwb8ASmI0B5_GxuUtQUopPFEOE29K18jp9mC9Auxo
        /// </summary>
        public string m_TableId = string.Empty;
        /// <summary>
        /// SheetIds on Google Spreadsheet.
        /// etc. https://docs.google.com/spreadsheets/d/1zLXwb8ASmI0B5_GxuUtQUopPFEOE29K18jp9mC9Auxo/edit#gid=0
        /// Gid = 0(gid = 0)
        /// </summary>
        public List<long> m_Gids = new List<long>();
        /// <summary>
        /// Gid of Table that contains all Gid
        /// </summary>
        public long m_GidTable = -1;

        [UCL.Core.PA.UCL_FolderExplorer]
        public string m_SaveFolder = "";

        public string m_FileName = "Lang.txt";
        public string SavePath => m_SaveFolder;
        /// <summary>
        /// true if download success
        /// </summary>
        protected System.Action<bool> m_DownloadEndAct = null;
        protected Regex m_SplitLineRegex = new Regex(@"\r\n", RegexOptions.Compiled);
        bool m_IsCancelDownload = false;
        bool m_IsDownloading = false;
        public string GetDownloadPath(long iGID)
        {
            return string.Format(DownloadTemplate, m_TableId, iGID);
        }
        public void StartDownload(System.Action<bool> iEndAct)
        {
            if (m_IsDownloading) return;
            m_DownloadEndAct = iEndAct;
            StartDownload();
        }
        protected void DownloadEnd(bool iSuccess)
        {
#if UNITY_EDITOR
            if(iSuccess) UCL.Core.EditorLib.AssetDatabaseMapper.Refresh();
            UCL.Core.EditorLib.EditorUtilityMapper.ClearProgressBar();
#endif
            m_IsDownloading = false;
            m_DownloadEndAct?.Invoke(iSuccess);
        }
        protected bool CheckCancelDownload(string iTitle, string iInfo, float iProgress)
        {
            if (m_IsCancelDownload)//Already Cancel
            {
                return m_IsCancelDownload;
            }

            m_IsCancelDownload = UCL.Core.EditorLib.EditorUtilityMapper.DisplayCancelableProgressBar(iTitle, iInfo, iProgress);
            if (m_IsCancelDownload) DownloadEnd(false);

            return m_IsCancelDownload;
        }
        public void StartDownload()
        {
            if (m_IsDownloading) return;
            m_IsDownloading = true;
            if (CheckCancelDownload("StartDownload", "Init", 0.1f))
            {
                return;
            }

            HashSet<long> aGids = new HashSet<long>();
            foreach (long aGid in m_Gids)
            {
                if (aGids.Contains(aGid))
                {
                    Debug.LogError("StartDownload(), Gid Repeat:" + aGid);
                }
                else
                {
                    aGids.Add(aGid);
                }
            }
            System.Action aDownLoadAct = delegate ()
            {
                int aCompleteCount = 0;
                Dictionary<string, List<KeyPair>> aLangDic = new Dictionary<string, List<KeyPair>>();
                System.Action aCompelteAct = delegate ()
                {
                    foreach (var aLangName in aLangDic.Keys)
                    {
                        //Debug.LogError("aLangName:" + aLangName);
                        string aFolderName = Path.Combine(SavePath, aLangName);

                        if (!Directory.Exists(aFolderName))
                        {
                            Directory.CreateDirectory(aFolderName);
                        }
                        var aLangs = aLangDic[aLangName];
                        StringBuilder aSB = new StringBuilder();
                        for (int i = 0; i < aLangs.Count; i++)
                        {
                            aLangs[i].SaveToString(aSB);
                            if (i < aLangs.Count - 1) aSB.AppendLine();
                        }
                        string aPath = Path.Combine(aFolderName, m_FileName);
                        File.WriteAllText(aPath, aSB.ToString());
#if UNITY_EDITOR_WIN
                        //Core.FileLib.WindowsLib.OpenAssetExplorer(aPath);
#endif
                    }

                    DownloadEnd(true);

                };
                string[] aDatas = new string[aGids.Count];
                int aID = 0;
                foreach (long aGid in aGids)
                {
                    int aAt = aID++;
                    UCL.Core.EnumeratorLib.UCL_CoroutineManager.StartCoroutine(UCL.Core.WebRequestLib.Download(GetDownloadPath(aGid), delegate (byte[] iData) {
                        string aData = string.Empty;
                        if (iData != null && iData.Length > 0)
                        {
                            aData = System.Text.Encoding.UTF8.GetString(iData);
                        }
                        else
                        {
                            Debug.LogError("aGid:" + aGid + ",iData == null || iData.Length > 0");
                        }
                        aDatas[aAt] = aData;
                        float aProgress = 0.1f + ((0.9f * aCompleteCount) / aGids.Count);
                        if (CheckCancelDownload("Download Localize", "Progress: " + (100f * aProgress).ToString("N1") + "%", aProgress))
                        {
                            return;
                        }
                        if (++aCompleteCount >= aGids.Count)
                        {
                            for (int i = 0; i < aGids.Count; i++)
                            {
                                ParseData(aDatas[i], aLangDic);
                            }

                            aCompelteAct.Invoke();
                        }
                    }));
                }
            };
            if (m_IsCancelDownload) return;

            if (m_GidTable != -1)
            {
                UCL.Core.EnumeratorLib.UCL_CoroutineManager.StartCoroutine(UCL.Core.WebRequestLib.Download(GetDownloadPath(m_GidTable)
                    , delegate (byte[] iData) {
                        if (iData == null || iData.Length == 0)
                        {
                            Debug.LogError("GidTable download fail!!iData == null || iData.Length == 0");
                            DownloadEnd(false);
                            return;
                        }
                        string aData = System.Text.Encoding.UTF8.GetString(iData);
                        UCL.Core.CsvLib.CSVData aCSV = new CsvLib.CSVData(aData);
                        System.Text.StringBuilder aSB = new StringBuilder();
                        foreach (var aRow in aCSV.m_Rows)
                        {
                            foreach (var aCol in aRow.m_Columes)
                            {
                                aSB.Append(aCol).Append(", ");
                            }
                            aSB.AppendLine();
                        }
                        //Debug.LogError("CSV:" + aSB.ToString());
                        foreach (var aRow in aCSV.m_Rows)
                        {
                            if (aRow.Count == 0)
                            {
                                continue;
                            }
                            string aStr = aRow.Get(0);
                            long aGid = 0;
                            if (long.TryParse(aStr, out aGid))
                            {
                                if (aGids.Contains(aGid))
                                {
                                    Debug.LogError("StartDownload(), Gid Repeat:" + aGid);
                                }
                                else
                                {
                                    aGids.Add(aGid);
                                }
                            }
                            else
                            {
                                Debug.LogError("aStr:" + aStr + ",long.TryParse Fail!!");
                            }
                        }
                        if (aGids != null && aGids.Count > 0)
                        {
                            aDownLoadAct();
                        }
                        else
                        {
                            Debug.LogError("aGids == null || aGids.Count == 0");
                            DownloadEnd(false);
                        }
                    }));
            }
            else
            {
                if (aGids != null && aGids.Count > 0)
                {
                    aDownLoadAct();
                }
            }
        }
        public void ParseData(string iData, Dictionary<string, List<KeyPair>> iLangDic)
        {
            UCL.Core.CsvLib.CSVData aCSV = new CsvLib.CSVData(iData);
            if (aCSV.Count > 1)
            {
                var aLangs = new List<string>();
                
                var aLangNames = aCSV.GetRow(0);
                //Debug.LogWarning("aLines[0]:" + aLines[0]);
                for (int i = 1; i < aLangNames.Count; i++)//0 is Key
                {
                    string aLangName = aLangNames.Get(i);
                    aLangs.Add(aLangName);
                    if (!iLangDic.ContainsKey(aLangName))
                    {
                        //Debug.LogError("Add aLangName:" + aLangName);
                        iLangDic.Add(aLangName, new List<KeyPair>());
                    }
                    for(int j = 1; j < aCSV.Count; j++)
                    {
                        string aKey = aCSV.GetData(j, 0);
                        if (!string.IsNullOrEmpty(aKey))
                        {
                            iLangDic[aLangName].Add(new KeyPair(aKey, aCSV.GetData(j, i)));
                        } 
                    }
                }
            }
        }
    }
}

