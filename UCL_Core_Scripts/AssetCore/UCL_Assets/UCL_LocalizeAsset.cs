
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
        public class GidData : UCLI_ShortName, UCLI_FieldOnGUI
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
            //UCLI_Asset.s_CurOnGUIAsset
            /// <summary>
            /// return new data if the data of field altered
            /// </summary>
            /// <param name="iFieldName"></param>
            /// <param name="iEditTmpDatas"></param>
            /// <returns></returns>
            public object OnGUI(string iFieldName, UCL_ObjectDictionary iDataDic)
            {
                
                UCL_GUILayout.DrawObjExSetting aDrawObjExSetting = null;



                if (UCLI_Asset.s_CurOnGUIAsset is UCL_LocalizeAsset asset)
                {
                    aDrawObjExSetting = new UCL_GUILayout.DrawObjExSetting();
                    aDrawObjExSetting.OnShowField = () =>
                    {
                        if (asset.IsDownloading)
                        {
                            if (GUILayout.Button(UCL_LocalizeManager.Get("Cancel"), UCL_GUIStyle.ButtonStyle))
                            {
                                asset.Cancel();
                            }
                        }
                        else
                        {
                            if (GUILayout.Button(UCL_LocalizeManager.Get("Download"), UCL_GUIStyle.ButtonStyle))
                            {
                                asset.StartDownloadTable(m_Gid).Forget();
                            }
                        }
                    };
                }


                UCL_GUILayout.DrawField(this, iDataDic, iFieldName, iDrawObjExSetting : aDrawObjExSetting);
                return this;
            }
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

        protected CancellationTokenSource m_CTS = null;
        protected bool m_IsCancelDownload = false;
        
        protected string m_DownloadingInfo;
        protected int m_CompleteCount = 0;

        public bool IsDownloading => m_CTS != null;
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
            DisposeCTS();
        }
        private void DisposeCTS()
        {
            if (m_CTS == null)
            {
                return;
            }
            m_CTS.Dispose();
            m_CTS = null;
        }
        private void Cancel()
        {
            if(m_CTS == null)
            {
                return;
            }
            m_IsCancelDownload = true;

            if (!m_CTS.IsCancellationRequested)
            {
                m_CTS.Cancel();
            }
            
            m_CTS.Dispose();
            m_CTS = null;
        }
        private async UniTask LoadGidTable(CancellationToken token)
        {
            var path = GetDownloadPath(m_GidTable);
            byte[] iData = await WebRequestLib.Download(path);
            token.ThrowIfCancellationRequested();

            if (iData.IsNullOrEmpty())
            {
                Debug.LogError("GidTable download fail!!iData == null || iData.Length == 0");
                DisposeCTS();
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
        public async UniTask StartDownloadTable(long gid)
        {
            if (m_CTS != null)
            {
                Cancel();//Cancel if downloading
            }
            m_CTS = new CancellationTokenSource();
            var token = m_CTS.Token;
            try
            {
                await DownloadTable(token, gid, true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                DisposeCTS();
            }
        }

        private async UniTask DownloadTable(CancellationToken token, long gid, bool replaceOldKey = false)
        {
            const string Format = "csv";//"xlsx","ods"
            string aURL = GetDownloadPath(gid, Format);
            //Debug.LogError($"Download table: {aURL}");
            byte[] iData = null;
            try
            {
                iData = await UCL.Core.WebRequestLib.Download(aURL);
            }
            catch (System.Exception e)
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
            token.ThrowIfCancellationRequested();
            string aData = string.Empty;
            if (iData == null)
            {
                Debug.LogError($"aGid:{gid},iData == null");
            }
            else if (iData.Length == 0)
            {
                Debug.LogError($"aGid:{gid},iData.Length == 0");
            }
            else
            {
                aData = System.Text.Encoding.UTF8.GetString(iData);
            }

            ParseData(aData, replaceOldKey);

            ++m_CompleteCount;
            float aProgress = 0.1f + ((0.9f * m_CompleteCount) / m_GidDatas.Count);

            m_DownloadingInfo = $"Download Localize aGid:{gid}, Progress: {(100f * aProgress).ToString("N1")}%";
        }
        public async void StartDownload()
        {
            //Debug.LogError($"StartDownload m_IsDownloading:{IsDownloading}");
            if(m_CTS != null)
            {
                Cancel();//Cancel if downloading
            }

            try
            {
                m_CompleteCount = 0;
                
                m_CTS = new CancellationTokenSource();
                var token = m_CTS.Token;
                m_IsCancelDownload = false;
                m_DownloadingInfo = "StartDownload";
                //Debug.LogError($"2 StartDownload");
                m_LocalizeDatas.Clear();//Clear old datas

                if (m_IsCancelDownload) return;

                if (m_GidTable != -1)
                {
                    await LoadGidTable(token);
                }

                if (!m_GidDatas.IsNullOrEmpty())
                {
                    List<UniTask> aTasks = new();
                    for (int i = 0; i < m_GidDatas.Count; i++)
                    {
                        if (i > 0)
                        {
                            await UniTask.WaitForSeconds(0.1f, cancellationToken: token);
                            token.ThrowIfCancellationRequested();
                        }
                        var aGidData = m_GidDatas[i];
                        long aGid = aGidData.m_Gid;
                        aTasks.Add(DownloadTable(token, aGid));
                        token.ThrowIfCancellationRequested();
                    }
                    await UniTask.WhenAll(aTasks);
                    //Debug.LogError($"Download End");
                }

                DisposeCTS();
            }
            catch(OperationCanceledException ex)
            {
                Debug.Log($"{GetType().Name}.{nameof(StartDownload)}, OperationCanceledException");
            }catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
        public void ParseData(string iData, bool replaceOldKey)
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
                    if (!m_LocalizeDatas.ContainsKey(aLangName))
                    {
                        //Debug.LogError("Add aLangName:" + aLangName);
                        m_LocalizeDatas.Add(aLangName, new());
                    }

                    if (replaceOldKey)
                    {
                        for (int j = 1; j < aCSV.Count; j++)
                        {
                            string key = aCSV.GetData(j, 0);
                            if (!string.IsNullOrEmpty(key))
                            {
                                var val = aCSV.GetData(j, i);
                                var dic = m_LocalizeDatas[aLangName].m_LocalizeDic;
                                dic[key] = val;
                            }
                        }
                    }
                    else
                    {
                        for (int j = 1; j < aCSV.Count; j++)
                        {
                            string key = aCSV.GetData(j, 0);
                            if (!string.IsNullOrEmpty(key))
                            {
                                var val = aCSV.GetData(j, i);
                                var dic = m_LocalizeDatas[aLangName].m_LocalizeDic;
                                if (dic.ContainsKey(key))
                                {
                                    Debug.LogError($"ParseData:{iData}, key:{key}, val:{val}, key exist!!");
                                }
                                else
                                {
                                    dic.Add(key, val);
                                }
                            }
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
            if (!IsDownloading)
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
                    Cancel();
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