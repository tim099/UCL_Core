using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UCL.Core.LocalizeLib
{
    [System.Serializable]
    public struct KeyPair
    {
        public KeyPair(string iKey, string iLocalize)
        {
            m_Key = ParseString(iKey);
            m_Localize = ParseString(iLocalize);
            //Debug.LogError(m_Key + "," + m_Localize);
        }
        static public string ParseString(string iStr)
        {
            if (string.IsNullOrEmpty(iStr))
            {
                return string.Empty;
            }
            if(iStr[0] != '"') {
                return iStr;
            }
            StringBuilder aSB = new StringBuilder();
            for(int i = 1; i < iStr.Length - 1; i++)
            {
                char aC = iStr[i];
                switch (aC) {
                    case '"':
                        {
                            i++;
                            aSB.Append('\\');
                            aSB.Append('"');
                            break;
                        }
                    case '\r':
                        {
                            i++;
                            aSB.Append('\n');
                            break;
                        }
                    default:
                        {
                            aSB.Append(aC);
                            break;
                        }
                }
            }
            return aSB.ToString();
        }
        public void SaveToString(StringBuilder aSB)
        {
            aSB.Append('\"');
            aSB.Append(m_Key);
            aSB.Append('\"');
            aSB.Append(':');
            aSB.Append('\"');
            aSB.Append(m_Localize);
            aSB.Append('\"');
        }
        public string m_Key;
        public string m_Localize;
    }
    [Core.ATTR.EnableUCLEditor]
    [CreateAssetMenu(fileName = "New LocalizeDownloader", menuName = "UCL/Downloader/LocalizeDownloader")]
    public class UCL_LocalizeDownloader : ScriptableObject
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
        public string m_SaveFolder = "";
        public string m_FileName = "Lang.txt";
        public string SavePath { get { return m_SaveFolder; } }
        /// <summary>
        /// true if download success
        /// </summary>
        protected System.Action<bool> m_DownloadEndAct = null;
        protected Regex m_SplitLineRegex = new Regex(@"\r\n", RegexOptions.Compiled);
#if UNITY_EDITOR
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void ExploreSaveFolder()
        {
            m_SaveFolder = UCL.Core.FileLib.EditorLib.OpenAssetsFolderExplorer(m_SaveFolder);
        }
#endif
        public string GetDownloadPath(long iGID)
        {
            return string.Format(DownloadTemplate, m_TableId, iGID);
        }
        public void StartDownload(System.Action<bool> iEndAct)
        {
            m_DownloadEndAct = iEndAct;
            StartDownload();
        }
        protected void DownloadFail()
        {
            UCL.Core.EditorLib.EditorUtilityMapper.ClearProgressBar();
            m_DownloadEndAct?.Invoke(false);
        }
        [UCL.Core.ATTR.UCL_FunctionButton]
        public void StartDownload()
        {
#if UNITY_EDITOR
            if (string.IsNullOrEmpty(m_SaveFolder))
            {
                var path = UCL.Core.EditorLib.AssetDatabaseMapper.GetAssetPath(this);
                m_SaveFolder = Core.FileLib.Lib.RemoveFolderPath(path, 1);
            }
#endif
            UCL.Core.EditorLib.EditorUtilityMapper.DisplayProgressBar("StartDownload", "Init", 0.1f);
            HashSet<long> aGids = new HashSet<long>();
            foreach(long aGid in m_Gids)
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
                            if(i < aLangs.Count - 1) aSB.AppendLine();
                        }
                        string aPath = Path.Combine(aFolderName, m_FileName);
                        File.WriteAllText(aPath, aSB.ToString());
#if UNITY_EDITOR_WIN
                        //Core.FileLib.WindowsLib.OpenAssetExplorer(aPath);
#endif
                    }
#if UNITY_EDITOR
                    UCL.Core.EditorLib.AssetDatabaseMapper.Refresh();
                    UCL.Core.EditorLib.EditorUtilityMapper.ClearProgressBar();
                    m_DownloadEndAct?.Invoke(true);
#endif

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
                        UCL.Core.EditorLib.EditorUtilityMapper.DisplayProgressBar("Download Localize", "Progress: " + (100f * aProgress).ToString("N1") + "%", aProgress);
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

            if (m_GidTable != -1)
            {
                UCL.Core.EnumeratorLib.UCL_CoroutineManager.StartCoroutine(UCL.Core.WebRequestLib.Download(GetDownloadPath(m_GidTable)
                    , delegate (byte[] iData) {
                        if(iData == null || iData.Length == 0)
                        {
                            Debug.LogError("GidTable download fail!!iData == null || iData.Length == 0");
                            DownloadFail();
                            return;
                        }
                        string aData = System.Text.Encoding.UTF8.GetString(iData);
                        string[] aGidStrs = aData.SplitByLine();
                        for(int i = 0; i < aGidStrs.Length; i++)
                        {
                            string aStr = aGidStrs[i];
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
                                Debug.LogError("aStr:" + aStr+ ",long.TryParse Fail!!");
                            }
                        }
                        if (aGids != null && aGids.Count > 0)
                        {
                            aDownLoadAct();
                        }
                        else
                        {
                            Debug.LogError("aGids == null || aGids.Count == 0");
                            DownloadFail();
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
            //Debug.LogError("iData:" + iData);
            var aLines = m_SplitLineRegex.Split(iData);//SplitByLine(); RegexOptions.IgnoreCase
                                             //Debug.LogWarning("aLines:" + aLines.Length);
            if (aLines.Length > 1)
            {
                var aLangs = new List<string>();
                var aLangNames = aLines[0].Split(',');
                //Debug.LogWarning("aLines[0]:" + aLines[0]);
                for (int i = 1; i < aLangNames.Length; i++)//0 is Key
                {
                    string aLangName = aLangNames[i];
                    aLangs.Add(aLangName);
                    if (!iLangDic.ContainsKey(aLangName))
                    {
                        //Debug.LogError("Add aLangName:" + aLangName);
                        iLangDic.Add(aLangName, new List<KeyPair>());
                    }
                }
                for (int i = 1; i < aLines.Length; i++)
                {
                    //Debug.LogWarning("aLines["+i+"]:" + aLines[i]);
                    var aDatas = aLines[i].Split(',');
                    if (aDatas.Length > 1)
                    {
                        string aKey = aDatas[0];
                        if (!string.IsNullOrEmpty(aKey))
                        {
                            for (int j = 1; j < aLangNames.Length; j++)
                            {
                                string aLangName = aLangNames[j];
                                if (j < aDatas.Length)
                                {
                                    //Debug.LogError("Get aLangName:" + aLangName);
                                    iLangDic[aLangName].Add(new KeyPair(aKey, aDatas[j]));
                                }
                                else
                                {
                                    iLangDic[aLangName].Add(new KeyPair(aKey, string.Empty));
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

