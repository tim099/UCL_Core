
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 11/23 2024
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace UCL.Core
{
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
    public class UCL_LocalizeAsset : UCL_Asset<UCL_LocalizeAsset>
    {
        public static UCL_LocalizeAsset Default
        {
            get
            {
                if(!UCL_ModuleService.Initialized) return null;
                try
                {
                    return Util.GetData(UCL_LocalizeAssetEntry.DefaultID);
                }
                catch { }
                return null;
            }
        }


        public override JsonData Save()
        {
            return base.Save();
        }
        public enum LocalizeType
        {
            Default = 0,
            /// <summary>
            /// Download from googleSheet
            /// </summary>
            GoogleSheet,
        }
        public class LocalizeData
        {
            public Dictionary<string, string> m_LocalizeDic = new();
        }
        public class GidData : UCLI_ShortName
        {
            /// <summary>
            /// SheetIds on Google Spreadsheet.
            /// etc. https://docs.google.com/spreadsheets/d/1zLXwb8ASmI0B5_GxuUtQUopPFEOE29K18jp9mC9Auxo/edit#gid=0
            /// Gid = 0(gid = 0)
            /// </summary>
            [Header("SheetIds on Google Spreadsheet."
                + "\netc. https://docs.google.com/spreadsheets/d/1zLXwb8ASmI0B5_GxuUtQUopPFEOE29K18jp9mC9Auxo/edit#gid=0"
                + "\nGid = 0(gid = 0)")]

            /// <summary>
            /// Gid of Table that contains all Gid
            /// </summary>
            public long m_Gid = -1;

            /// <summary>
            /// info of Table
            /// </summary>
            public string m_Note;


            public GidData() { }
            public string GetShortName() => $"{m_Note}({m_Gid})";
        }
        public LocalizeType m_LocalizeType = LocalizeType.Default;
        public Dictionary<string, LocalizeData> m_LocalizeDatas = new();

        const string DownloadTemplate = "https://docs.google.com/spreadsheets/d/{0}/export?format={2}&gid={1}";
        /// <summary>
        /// Table id on Google Spreadsheet.
        /// etc. https://docs.google.com/spreadsheets/d/1zLXwb8ASmI0B5_GxuUtQUopPFEOE29K18jp9mC9Auxo/edit#gid=0
        /// TableId = 1zLXwb8ASmI0B5_GxuUtQUopPFEOE29K18jp9mC9Auxo
        /// </summary>
        [Header("etc. https://docs.google.com/spreadsheets/d/1zLXwb8ASmI0B5_GxuUtQUopPFEOE29K18jp9mC9Auxo/edit#gid=0" +
            "\nTableId = 1zLXwb8ASmI0B5_GxuUtQUopPFEOE29K18jp9mC9Auxo")]
        public string m_TableId = "1zLXwb8ASmI0B5_GxuUtQUopPFEOE29K18jp9mC9Auxo";

        /// <summary>
        /// SheetIds on Google Spreadsheet.
        /// etc. https://docs.google.com/spreadsheets/d/1zLXwb8ASmI0B5_GxuUtQUopPFEOE29K18jp9mC9Auxo/edit#gid=0
        /// Gid = 0(gid = 0)
        /// </summary>
        [Header("SheetIds on Google Spreadsheet."
            + "\netc. https://docs.google.com/spreadsheets/d/1zLXwb8ASmI0B5_GxuUtQUopPFEOE29K18jp9mC9Auxo/edit#gid=0"
            + "\nGid = 0(gid = 0)")]

        /// <summary>
        /// Gid of Table that contains all Gid
        /// </summary>
        public long m_GidTable = -1;

        /// <summary>
        /// All Gid Table
        /// </summary>
        public List<GidData> m_GidDatas = new();

        protected Regex m_SplitLineRegex = new Regex(@"\r\n", RegexOptions.Compiled);
        protected bool m_IsCancelDownload = false;
        protected bool m_IsDownloading = false;
        protected string m_DownloadingInfo;


        public bool ContainsKey(string lang, string key)
        {
            if (!m_LocalizeDatas.ContainsKey(lang))
            {
                return false;
            }

            var dic = m_LocalizeDatas[lang].m_LocalizeDic;
            return dic.ContainsKey(key);
        }
        public (bool success, string value) GetLocalize(string lang, string key)
        {
            if (!m_LocalizeDatas.ContainsKey(lang))
            {
                return (false, key);
            }

            var dic = m_LocalizeDatas[lang].m_LocalizeDic;
            if (!dic.ContainsKey(key))
            {
                return (false, key);
            }
            return (true, dic[key]);
        }

        public string GetDownloadPath(long iGID, string iFormat = "csv")
        {
            return string.Format(DownloadTemplate, m_TableId, iGID, iFormat);
        }

        protected void DownloadEnd(bool iSuccess)
        {
#if UNITY_EDITOR
            if (iSuccess) UCL.Core.EditorLib.AssetDatabaseMapper.Refresh();
            //UCL.Core.EditorLib.EditorUtilityMapper.ClearProgressBar();
#endif
            m_IsDownloading = false;
        }
        public async void StartDownload()
        {
            //Debug.LogError($"StartDownload m_IsDownloading:{m_IsDownloading}");
            if (m_IsDownloading) return;
            m_IsDownloading = true;
            m_IsCancelDownload = false;
            m_DownloadingInfo = "StartDownload";
            //Debug.LogError($"2 StartDownload");
            m_LocalizeDatas.Clear();//Clear old datas

            if (m_IsCancelDownload) return;

            if (m_GidTable != -1)
            {
                var path = GetDownloadPath(m_GidTable);
                byte[] iData = await WebRequestLib.Download(path);

                if (iData.IsNullOrEmpty())
                {
                    Debug.LogError("GidTable download fail!!iData == null || iData.Length == 0");
                    DownloadEnd(false);
                    return;
                }
                string aData = System.Text.Encoding.UTF8.GetString(iData);
                //Debug.LogError($"Data:{aData}");
                UCL.Core.CsvLib.CSVData aCSV = new UCL.Core.CsvLib.CSVData(aData);
                //Debug.LogError("CSV:" + aSB.ToString());
                m_GidDatas.Clear();
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
                        m_GidDatas.Add(new GidData() { m_Gid = aGid, m_Note = aRow.Get(1) });
                    }
                    else
                    {
                        Debug.LogError("aStr:" + aStr + ",long.TryParse Fail!!");
                    }
                }
            }

            if (!m_GidDatas.IsNullOrEmpty())
            {
                int aCompleteCount = 0;
                Dictionary<string, List<KeyPair>> aLangDic = new Dictionary<string, List<KeyPair>>();
                string[] aDatas = new string[m_GidDatas.Count];
                int aID = 0;
                List<UniTask> aTasks = new();
                foreach (var aGidData in m_GidDatas)
                {
                    long aGid = aGidData.m_Gid;
                    if (m_IsCancelDownload)
                    {
                        DownloadEnd(false);
                        return;
                    }
                    int aAt = aID++;
                    const string Format = "csv";//"xlsx","ods"
                    string aURL = GetDownloadPath(aGid, Format);
                    
                    aTasks.Add(Download());
                    //await Download();
                    async UniTask Download()
                    {
                        //Debug.LogError($"Download aURL:{aURL}");
                        byte[] iData = null;
                        try
                        {
                            iData = await UCL.Core.WebRequestLib.Download(aURL);
                        }
                        catch(System.Exception e)
                        {
                            Debug.LogException(e);
                            //retry once
                            try
                            {
                                await UniTask.WaitForSeconds(0.3f);//Delay
                                iData = await UCL.Core.WebRequestLib.Download(aURL);
                            }
                            catch { }
                        }
                        {
                            string aData = string.Empty;
                            if (iData == null)
                            {
                                Debug.LogError($"aGid:{aGid},iData == null");
                            }
                            else if (iData.Length == 0)
                            {
                                Debug.LogError($"aGid:{aGid},iData.Length == 0");
                            }
                            else
                            {
                                aData = System.Text.Encoding.UTF8.GetString(iData);
                            }
                            aDatas[aAt] = aData;
                            ++aCompleteCount;
                            float aProgress = 0.1f + ((0.9f * aCompleteCount) / m_GidDatas.Count);

                            m_DownloadingInfo = $"Download Localize aGid:{aGid}, Progress: {(100f * aProgress).ToString("N1")}%";
                        }
                    }
                    
                    await UniTask.WaitForSeconds(0.1f);
                }
                await UniTask.WhenAll(aTasks);
                //Debug.LogError($"Download End");
                for (int i = 0; i < aDatas.Length; i++)
                {
                    ParseData(aDatas[i], aLangDic);
                }
                foreach (var aLangName in aLangDic.Keys)
                {
                    if (!aLangName.IsNullOrEmpty())
                    {
                        if (!m_LocalizeDatas.ContainsKey(aLangName))
                        {
                            m_LocalizeDatas[aLangName] = new();
                        }
                        var dic = m_LocalizeDatas[aLangName].m_LocalizeDic;
                        var aLangs = aLangDic[aLangName];
                        for (int i = 0; i < aLangs.Count; i++)
                        {
                            var lang = aLangs[i];
                            dic[lang.m_Key] = lang.m_Localize;
                        }
                    }
                }
            }

            DownloadEnd(true);
        }
        public void ParseData(string iData, Dictionary<string, List<KeyPair>> iLangDic)
        {
            //Debug.LogError($"ParseData:{iData}");
            UCL.Core.CsvLib.CSVData aCSV = new UCL.Core.CsvLib.CSVData(iData);
            if (aCSV.Count > 1)
            {
                var aLangs = new List<string>();

                var aLangNames = aCSV.GetRow(0);

                for (int i = 1; i < aLangNames.Count; i++)//0 is Key
                {
                    string aLangName = aLangNames.Get(i);
                    //Debug.LogError($"aLangName:{aLangName}");
                    aLangs.Add(aLangName);
                    if (!iLangDic.ContainsKey(aLangName))
                    {
                        //Debug.LogError("Add aLangName:" + aLangName);
                        iLangDic.Add(aLangName, new List<KeyPair>());
                    }
                    for (int j = 1; j < aCSV.Count; j++)
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


        public UCL_LocalizeAsset()
        {
            ID = "Asset ID";
        }

        /// <summary>
        /// Preview(OnGUI)
        /// </summary>
        /// <param name="iIsShowEditButton">Show edit button in preview window?</param>
        override public void Preview(UCL.Core.UCL_ObjectDictionary iDataDic, bool iIsShowEditButton = false)
        {
            //GUILayout.BeginHorizontal();
            using (var aScope = new GUILayout.VerticalScope("box", GUILayout.ExpandWidth(false)))
            {

                GUILayout.Label($"{UCL_LocalizeManager.Get("Preview")}({ID})", UCL.Core.UI.UCL_GUIStyle.LabelStyle);

                if (iIsShowEditButton)
                {
                    if (GUILayout.Button(UCL_LocalizeManager.Get("Edit"), UCL.Core.UI.UCL_GUIStyle.ButtonStyle))
                    {
                        UCL_CommonEditPage.Create(this);
                    }
                }
            }
            //GUILayout.EndHorizontal();
        }

        public override void OnGUI(UCL_ObjectDictionary iDataDic)
        {
            GUILayout.BeginVertical();

            using (var scope = new GUILayout.VerticalScope("box"))//, GUILayout.Width(500)
            {
                UCL.Core.UI.UCL_GUILayout.DrawObjectData(this, iDataDic, string.Empty, true, LocalizeFieldName);
            }
            if (!m_IsDownloading)
            {
                if (GUILayout.Button("Download", UCL_GUIStyle.ButtonStyle))
                {
                    StartDownload();
                }
            }
            else if(!m_IsCancelDownload)
            {
                GUILayout.BeginHorizontal();

                if (GUILayout.Button("Cancel", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    m_IsCancelDownload = true;
                    DownloadEnd(false);
                }
                GUILayout.Label($"{m_DownloadingInfo}", UCL_GUIStyle.LabelStyle);

                GUILayout.EndHorizontal();
            }



            using (new GUILayout.VerticalScope("box"))//Preview
            {
                bool aIsShow = false;
                using (new GUILayout.HorizontalScope())
                {
                    aIsShow = UCL.Core.UI.UCL_GUILayout.Toggle(iDataDic, "ShowPreview", iDefaultValue: true);
                    UCL.Core.UI.UCL_GUILayout.LabelAutoSize(UCL_LocalizeManager.Get("Preview"));
                }

                if (aIsShow)
                {
                    //using (new GUILayout.VerticalScope(GUILayout.Width(200)))
                    {
                        Preview(iDataDic.GetSubDic("Preview"), false);
                    }
                }
            }

            GUILayout.EndVertical();


        }
    }

    [System.Serializable]
    public class UCL_LocalizeAssetEntry : UCL_AssetEntryDefault<UCL_LocalizeAsset>
    {
        public const string DefaultID = "Default";

        public UCL_LocalizeAssetEntry() { m_ID = DefaultID; }
        public UCL_LocalizeAssetEntry(string iID) { m_ID = iID; }

    }
}