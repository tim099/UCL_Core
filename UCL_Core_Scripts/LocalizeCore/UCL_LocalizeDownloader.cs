using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace UCL.Core.LocalizeLib
{
    [System.Serializable]
    public struct KeyPair
    {
        public KeyPair(string _Key, string _Localize)
        {
            m_Key = _Key;
            m_Localize = _Localize;
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
            List<long> aGids = m_Gids.Clone();
            System.Action aDownLoadAct = delegate ()
            {
                int aCompleteCount = 0;
                //List<string> aDatas = new List<string>();
                //List<string> aLanguageNames = new List<string>();
                Dictionary<string, List<KeyPair>> aLangDic = new Dictionary<string, List<KeyPair>>();
                System.Action aCompelteAct = delegate ()
                {
                    foreach (var aLangName in aLangDic.Keys)
                    {
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
                            aSB.AppendLine();
                        }
                        string aPath = Path.Combine(aFolderName, m_FileName);
                        File.WriteAllText(aPath, aSB.ToString());
#if UNITY_EDITOR_WIN
                        //Core.FileLib.WindowsLib.OpenAssetExplorer(aPath);
#endif
                    }
#if UNITY_EDITOR
                    UCL.Core.EditorLib.AssetDatabaseMapper.Refresh();
#endif
                    UCL.Core.EditorLib.EditorUtilityMapper.ClearProgressBar();

                };
                for (int aID = 0; aID < aGids.Count; aID++)
                {
                    UCL.Core.EnumeratorLib.UCL_CoroutineManager.StartCoroutine(UCL.Core.WebRequestLib.Download(GetDownloadPath(aGids[aID]), delegate (byte[] iData) {
                        string aData = System.Text.Encoding.UTF8.GetString(iData);
                        var aLines = aData.SplitByLine();
                        if (aLines.Length > 1)
                        {
                            var aLangs = new List<string>();
                            var aLangNames = aLines[0].Split(',');
                            for (int i = 1; i < aLangNames.Length; i++)//0 is Key
                            {
                                string aLangName = aLangNames[i];
                                aLangs.Add(aLangName);
                                if (!aLangDic.ContainsKey(aLangName))
                                {
                                    //Debug.LogError("Add aLangName:" + aLangName);
                                    aLangDic.Add(aLangName, new List<KeyPair>());
                                }
                            }
                            for (int i = 1; i < aLines.Length; i++)
                            {
                                var aDatas = aLines[i].Split(',');
                                if (aDatas.Length > 1)
                                {
                                    string aKey = aDatas[0];
                                    for (int j = 1; j < aLangNames.Length; j++)
                                    {
                                        string aLangName = aLangNames[j];
                                        if (j < aDatas.Length)
                                        {
                                            //Debug.LogError("Get aLangName:" + aLangName);
                                            aLangDic[aLangName].Add(new KeyPair(aKey, aDatas[j]));
                                        }
                                        else
                                        {
                                            aLangDic[aLangName].Add(new KeyPair(aKey, string.Empty));
                                        }
                                    }
                                }
                            }
                        }
                        float aProgress = 0.1f + ((0.9f * aCompleteCount) / aGids.Count);
                        UCL.Core.EditorLib.EditorUtilityMapper.DisplayProgressBar("Download Localize", "Progress: " + (100f*aProgress).ToString("N1")+"%", aProgress);
                        if (++aCompleteCount >= aGids.Count)
                        {
                            aCompelteAct.Invoke();
                        }
                    }));
                }
            };

            if (m_GidTable != -1)
            {
                UCL.Core.EnumeratorLib.UCL_CoroutineManager.StartCoroutine(UCL.Core.WebRequestLib.Download(GetDownloadPath(m_GidTable)
                    , delegate (byte[] iData) {
                        string aData = System.Text.Encoding.UTF8.GetString(iData);
                        string[] aGidStrs = aData.SplitByLine();
                        for(int i = 0; i < aGidStrs.Length; i++)
                        {
                            long aResult = 0;
                            if(long.TryParse(aGidStrs[i],out aResult))
                            {
                                aGids.Add(aResult);
                            }
                        }
                        if (aGids != null && aGids.Count > 0)
                        {
                            aDownLoadAct();
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
    }
}

