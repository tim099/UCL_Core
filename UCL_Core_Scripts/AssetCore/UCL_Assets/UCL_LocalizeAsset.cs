
// RCG_AutoHeader
// to change the auto header please go to RCG_AutoHeader.cs
// Create time : 11/23 2024
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        #region static
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
        public static void ClearLocalizeCache()
        {
            if (s_LocalizeDics != null)
            {
                s_LocalizeDics.Clear();
            }
        }
        public static Dictionary<string, string> GetLocalizeDic(string lang)
        {
            if(s_LocalizeDics == null)
            {
                if (!UCL_ModuleService.Initialized) return null;
                UCL_ModuleService.OnLoadModule += ClearLocalizeCache;//Clear Cache OnLoadModule
                s_LocalizeDics = new();
            }

            if (!s_LocalizeDics.ContainsKey(lang))
            {
                var dic = new Dictionary<string, string>();
                
                var util = UCL_LocalizeAsset.Util;
                var ids = util.GetAllIDs();
                var datas = ids.Select(id => util.GetData(id)).OrderBy(data => data.m_LoadOrder);//按照LoadOrder讀取
                foreach (var data in datas)
                {
                    var result = data.GetLocalizeData(lang);
                    if (result != null)
                    {
                        var localizeDic = result.m_LocalizeDic;
                        foreach (var key in localizeDic.Keys)
                        {
                            dic[key] = localizeDic[key];
                        }
                    }
                }
                s_LocalizeDics[lang] = dic;
            }

            return s_LocalizeDics[lang];
        }
        private static Dictionary<string, Dictionary<string, string>> s_LocalizeDics = null;
        #endregion

        public class GoogleSheetConfig
        {
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
        }


        public enum LocalizeType
        {
            Default = 0,
            /// <summary>
            /// Download from googleSheet
            /// </summary>
            GoogleSheet,
        }
        public enum Format
        {
            csv,
            tsv,
            //xlsx,
        }
        public class LocalizeData
        {
            public Dictionary<string, string> m_LocalizeDic = new();
        }
        public class ParseStringConfig
        {
            public List<UCL_ParseString> m_Parsers = new();

            public string Parse(string str)
            {
                if(m_Parsers.IsNullOrEmpty()) return str;

                foreach(var parser in m_Parsers)
                {
                    str = parser.Parse(str);
                }
                return str;
            }
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

            public Format m_Format = Format.csv;

            public GidData() { }
            public string GetShortName() => $"{m_Note}({m_Gid}).{m_Format}";
            //UCLI_Asset.s_CurOnGUIAsset
            /// <summary>
            /// return new data if the data of field altered
            /// </summary>
            /// <param name="iFieldName"></param>
            /// <param name="iEditTmpDatas"></param>
            /// <returns></returns>
            public object OnGUI(string iFieldName, UCL_ObjectDictionary iDataDic, UCL_GUILayout.DrawObjectParams iParams)
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
                                asset.StartDownloadTable(this).Forget();
                            }
                        }
                    };
                }


                UCL_GUILayout.DrawField(this, iDataDic, iFieldName, iDrawObjExSetting : aDrawObjExSetting);
                return this;
            }
        }
        public LocalizeType m_LocalizeType = LocalizeType.Default;

        /// <summary>
        /// 載入優先順序(值越小越先載入)
        /// 當多個語言表有同個Key時
        /// 後載入的會覆蓋先載入的
        /// </summary>
        public int m_LoadOrder = 0;

        [UCL.Core.PA.Conditional(nameof(m_SaveToModResources), false, false)]
        public Dictionary<string, LocalizeData> m_LocalizeDatas = new();
        /// <summary>
        /// 所有支援的語言Key
        /// </summary>
        public HashSet<string> m_LangKeys = new();
        /// <summary>
        /// 預設語言
        /// </summary>
        public UCL_LanguageCodeEntry m_DefaultLang = new();

        /// <summary>
        /// 保存到ModResources(優化讀取速度)
        /// </summary>
        public bool m_SaveToModResources = false;

        /// <summary>
        /// 有設定的話 會保存到這裡
        /// </summary>
        [UCL.Core.PA.Conditional(nameof(m_SaveToModResources), false, true)]
        public UCL_ModResourcesData m_SavaPath = new UCL_ModResourcesData();
        /// <summary>
        /// 有設定的話 會保存到這裡
        /// </summary>
        [UCL.Core.PA.Conditional(nameof(m_SaveToModResources), false, true)]
        public string m_SaveFolderName = "Default";

        public GoogleSheetConfig m_GoogleSheetData = new();

        public Dictionary<string, ParseStringConfig> m_ParseStringConfigs = new();


        const string DownloadTemplate = "https://docs.google.com/spreadsheets/d/{0}/export?format={2}&gid={1}";

        protected Regex m_SplitLineRegex = new Regex(@"\r\n", RegexOptions.Compiled);

        protected CancellationTokenSource m_CTS = null;
        protected bool m_IsCancelDownload = false;
        
        protected string m_DownloadingInfo;
        protected int m_CompleteCount = 0;

        public bool IsDownloading => m_CTS != null;

        /// <summary>
        /// 存檔位置
        /// </summary>
        public string FolderPath => Path.Combine(m_SavaPath.FileSystemFolderPath, m_SaveFolderName);

        private static Encoding FileEncoding => Encoding.UTF8;
        private const char seperator = '\0';
        public string GetSavePath(string langKey)
        {
            return Path.Combine(FolderPath, $"{langKey}.txt");
        }
        /// <summary>
        /// Get LocalizeData of target language
        /// </summary>
        /// <param name="language">language</param>
        /// <returns></returns>
        public LocalizeData GetLocalizeData(string language)
        {
            if (m_LocalizeDatas.IsNullOrEmpty()) return null;

            {
                if (m_LocalizeDatas.TryGetValue(language, out var result))
                {
                    return result;
                }
            }
            {//return DefaultLang
                if (m_LocalizeDatas.TryGetValue(m_DefaultLang.ID, out var result))
                {
                    return result;
                }
            }
            return m_LocalizeDatas.Values.FirstOrDefault();
        }
        public override JsonData Save()
        {
            ClearLocalizeCache();

            foreach (var key in m_LocalizeDatas.Keys)
            {
                m_LangKeys.Add(key);//紀錄LangKey
                if (!UCL_LanguageCodeAsset.Util.ContainsAsset(key))//如果有目前不包含的語言 自動新增到UCL_LanguageCodeAsset
                {
                    UCL_LanguageCodeAsset lang = new UCL_LanguageCodeAsset(key);
                    lang.Save();//Add key
                }
            }
            string defaultLang = m_DefaultLang.ID;
            if (m_LocalizeDatas.ContainsKey(defaultLang))//有預設語言的資料
            {
                var defaultLangDic = m_LocalizeDatas[defaultLang].m_LocalizeDic;
                foreach (var langKey in m_LocalizeDatas.Keys)
                {
                    if (langKey != defaultLang)//
                    {
                        var langDic = m_LocalizeDatas[langKey].m_LocalizeDic;
                        foreach(var key in defaultLangDic.Keys)
                        {
                            if (!langDic.ContainsKey(key))
                            {
                                langDic[key] = defaultLangDic[key];
                            }
                        }
                    }
                }
            }
            if (m_SaveToModResources)
            {
                m_SavaPath.m_ModuleID = UCL_ModuleService.CurEditModuleID;
                Directory.CreateDirectory(FolderPath);
                //Directory.CreateDirectory(m_SavaPath.FileSystemFolderPath);
                foreach (var langKey in m_LocalizeDatas.Keys)
                {
                    var dic = m_LocalizeDatas[langKey].m_LocalizeDic;
                    var path = GetSavePath(langKey);
                    using (FileStream fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        using (StreamWriter writer = new StreamWriter(fileStream, FileEncoding))
                        {
                            writer.WriteLine(dic.Keys.Count);//紀錄有多少筆資料
                            foreach (var key in dic.Keys)//寫入所有Key
                            {
                                var val = dic[key].Replace("\r\n", "\n");
                                
                                writer.Write(key.Length);
                                writer.Write(",");
                                writer.Write(val.Length);
                                writer.Write('\n');
                                //writer.WriteLine();

                                writer.Write(key);
                                writer.Write(":");
                                writer.Write(val);
                                writer.Write('\n');
                                //writer.WriteLine();
                            }
                        }
                    }
                    //System.Text.StringBuilder sb = new StringBuilder();

                }
                //m_LocalizeDatas.Clear();//清除LocalizeDatas 避免存進json
            }
            
            return base.Save();
        }
        public override JsonData SerializeToJson()
        {
            return base.SerializeToJson();
        }
        /// <summary>
        /// 複製一份
        /// </summary>
        /// <returns></returns>
        //override public UCLI_CommonEditable CloneInstance()
        //{
        //    var aClone = new UCL_LocalizeAsset();
        //    aClone.ID = this.ID;
        //    aClone.DeserializeFromJson(SerializeToJson());
        //    return aClone;
        //}
        public override void DeserializeFromJson(JsonData iJson)
        {
            base.DeserializeFromJson(iJson);
            if (m_SaveToModResources)//從ModResourcesData讀取
            {
                m_LocalizeDatas.Clear();
                foreach(var langKey in m_LangKeys)
                {
                    var path = GetSavePath(langKey);
                    if (!File.Exists(path))
                    {
                        Debug.LogError($"{GetType().Name}.DeserializeFromJson path:{path}, !File.Exists(path)");
                        continue;
                    }
                    try
                    {
                        var localizeData = m_LocalizeDatas[langKey] = new LocalizeData();
                        var dic = localizeData.m_LocalizeDic;
                        System.Text.StringBuilder stringBuilder = new System.Text.StringBuilder();
                        using (FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read))
                        {
                            using (StreamReader reader = new StreamReader(fileStream, FileEncoding))
                            {
                                int keyNum = 0;
                                string keyNumStr = reader.ReadLine();
                                if (!int.TryParse(keyNumStr, out keyNum))
                                {
                                    keyNum = 0;//ParseFail
                                    Debug.LogError($"{GetType().Name}.DeserializeFromJson keyNumStr:{keyNumStr}, !int.TryParse(keyNumStr, out keyNum)");
                                }
                                //Debug.LogError($"langKey:{langKey}, keyNum:{keyNum}");
                                for (int i = 0; i < keyNum; i++)
                                {
                                    string keyLenStr = null;
                                    string key = null;
                                    string val = null;
                                    try
                                    {
                                        keyLenStr = reader.ReadLine();//Format x,y
                                        //Debug.LogError($"({i})langKey:{langKey}, keyLenStr:{keyLenStr}");
                                        var keyLens = keyLenStr.Split(',');
                                        int keyLen = int.Parse(keyLens[0]);
                                        int valLen = int.Parse(keyLens[1]);

                                        //char[] keyBuffer = new char[keyLen];
                                        //keyLen = reader.ReadBlock(keyBuffer, 0, keyLen);
                                        //key = new string(keyBuffer);
                                        {
                                            stringBuilder.Clear();
                                            for (int j = 0; j < keyLen; j++)
                                            {
                                                var c = reader.Read();
                                                if (c == '\r')
                                                {
                                                    if (reader.Peek() == '\n')//convert \r\n to \n
                                                    {
                                                        reader.Read();
                                                        stringBuilder.Append('\n');
                                                        continue;
                                                    }
                                                }
                                                stringBuilder.Append((char)c);
                                            }
                                            key = stringBuilder.ToString();
                                        }


                                        reader.Read();//read :

                                        {
                                            stringBuilder.Clear();
                                            for (int j = 0; j < valLen; j++)
                                            {
                                                var c = reader.Read();
                                                if (c == '\r')
                                                {
                                                    if (reader.Peek() == '\n')//convert \r\n to \n
                                                    {
                                                        reader.Read();
                                                        stringBuilder.Append('\n');
                                                        continue;
                                                    }
                                                }
                                                stringBuilder.Append((char)c);
                                            }
                                        }
                                        reader.ReadLine();//read nextline
                                        val = stringBuilder.ToString();
                                        dic[key] = val;

                                        //char[] valBuffer = new char[valLen];
                                        //int readLen = reader.ReadBlock(valBuffer, 0, valLen);

                                        //val = new string(valBuffer);

                                        //if (valLen != readLen || valLen != val.Length)
                                        //{
                                        //    Debug.LogError($"({i})langKey:{langKey}, valLen:{valLen}, readLen:{readLen}, val.Length:{val.Length}, key:{key},val:{val}");
                                        //    valLen = readLen;
                                        //}
                                        //{
                                        //    System.Text.StringBuilder sb = new();
                                        //    sb.Append($"valLen:{valLen},");
                                        //    //int index = 0;
                                        //    foreach (var c in valBuffer)
                                        //    {
                                        //        sb.Append(c);
                                        //        sb.Append($"({(int)c})");
                                        //    }
                                        //    val = sb.ToString();
                                        //}

                                        //{
                                        //    System.Text.StringBuilder sb = new();
                                        //    sb.Append($"valLen:{valLen},");
                                        //    int index = 0;
                                        //    foreach (var c in valBuffer)
                                        //    {
                                        //        sb.Append(c);
                                        //        sb.Append($"({++index})");
                                        //    }
                                        //    val = sb.ToString();
                                        //}

                                        //dic[key] = val;
                                        ////Debug.LogError($"({i})langKey:{langKey}, key:{key},val:{val}");
                                        ////reader.ReadLine();//read nextline
                                    }
                                    catch (System.Exception e)
                                    {
                                        Debug.LogError($"langKey:{langKey}, keyLenStr:{keyLenStr}, key:{key}, val:{val}, Exception:{e}");
                                        Debug.LogException(e);
                                    }


                                }
                            }
                        }
                    }
                    catch(Exception ex)
                    {
                        Debug.LogException(ex);
                    }

                }
            }
            //m_GoogleSheetData.m_GidDatas = m_GidDatas.Clone();
            //m_GoogleSheetData.m_GidTable = m_GidTable;
            //m_GoogleSheetData.m_TableId = m_TableId;
        }

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
            return string.Format(DownloadTemplate, m_GoogleSheetData.m_TableId, iGID, iFormat);
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
            var path = GetDownloadPath(m_GoogleSheetData.m_GidTable);
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
            m_GoogleSheetData.m_GidDatas.Clear();
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
                    var data = new GidData() { m_Gid = aGid, m_Note = aRow.Get(1) };
                    if (aRow.Count >= 3)
                    {
                        string formatStr = aRow.Get(2);
                        Format format;
                        if(Enum.TryParse<Format>(formatStr, true, out format))
                        {
                            data.m_Format = format;
                        }
                    }
                    m_GoogleSheetData.m_GidDatas.Add(data);
                }
                else
                {
                    if(!string.IsNullOrEmpty(aStr)) Debug.LogError("aStr:" + aStr + ",long.TryParse Fail!!");
                }
            }
        }
        public async UniTask StartDownloadTable(GidData data)
        {
            if (m_CTS != null)
            {
                Cancel();//Cancel if downloading
            }
            m_CTS = new CancellationTokenSource();
            var token = m_CTS.Token;
            try
            {
                await DownloadTable(token, data, true);
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

        private async UniTask DownloadTable(CancellationToken token, GidData data, bool replaceOldKey, int order = -1)
        {
            var gid = data.m_Gid;
            Format format = data.m_Format;
            //const string Format = "csv";//"xlsx","ods"
            string aURL = GetDownloadPath(gid, format.ToString());
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
            //string aData = string.Empty;
            if (iData == null)
            {
                Debug.LogError($"aGid:{gid},iData == null");
            }
            else if (iData.Length == 0)
            {
                Debug.LogError($"aGid:{gid},iData.Length == 0");
            }
            //else
            //{
            //    aData = System.Text.Encoding.UTF8.GetString(iData);
            //}
            //Debug.LogError($"aData:{aData}, Format:{format}");
            if(order >= 0)//wait until for prev DownloadTable complete
            {
                if(order > m_CompleteCount)//wait until for prev DownloadTable complete
                {
                    await UniTask.WaitUntil(() => order <= m_CompleteCount);
                }
            }
            ParseData(iData, replaceOldKey, format);

            ++m_CompleteCount;
            float aProgress = 0.1f + ((0.9f * m_CompleteCount) / m_GoogleSheetData.m_GidDatas.Count);

            m_DownloadingInfo = $"Download Localize aGid:{gid}, Progress: {(100f * aProgress).ToString("N1")}%";
            m_IsCancelDownload = UCL.Core.EditorLib.EditorUtilityMapper.DisplayCancelableProgressBar($"Download Localize Gid:{gid}",
                m_DownloadingInfo, aProgress);
            if (m_IsCancelDownload) 
            {
                Cancel();
            }
        }
        /// <summary>
        /// Download if LocalizeType is GoogleSheet
        /// </summary>
        /// <returns></returns>
        public async UniTask Refresh()
        {
            if(m_LocalizeType == LocalizeType.GoogleSheet)
            {
                await StartDownload();
                Save();
            }
        }
        /// <summary>
        /// 下載
        /// </summary>
        public async UniTask StartDownload()
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

                if (m_GoogleSheetData.m_GidTable != -1)
                {
                    await LoadGidTable(token);
                }

                if (!m_GoogleSheetData.m_GidDatas.IsNullOrEmpty())
                {
                    List<UniTask> aTasks = new();

                    for (int i = 0; i < m_GoogleSheetData.m_GidDatas.Count; i++)
                    {
                        if (i > 0)
                        {
                            await UniTask.WaitForSeconds(0.1f, cancellationToken: token);
                            token.ThrowIfCancellationRequested();
                        }
                        var aGidData = m_GoogleSheetData.m_GidDatas[i];
                        aTasks.Add(DownloadTable(token, aGidData, false, i));
                        token.ThrowIfCancellationRequested();
                    }
                    await UniTask.WhenAll(aTasks);
                    //Debug.LogError($"Download End");
                }

                DisposeCTS();
            }
            catch (OperationCanceledException ex)
            {
                Debug.Log($"{GetType().Name}.{nameof(StartDownload)}, OperationCanceledException");
            } catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
#if UNITY_EDITOR
                UCL.Core.EditorLib.EditorUtilityMapper.ClearProgressBar();
#endif
            }
        }
        public string ParseString(string iLangName, string iStr)
        {
            //{
            //    StringBuilder aSB = new StringBuilder();
            //    for (int i = 1; i < iStr.Length - 1; i++)
            //    {
            //        char aC = iStr[i];
            //        aSB.Append($"{aC}({(int)aC})");
            //    }
            //    Debug.LogError($"ParseString:{iStr},aSB:{aSB.ToString()}");
            //}
            //iStr = iStr.Replace("，", ",");
            if (string.IsNullOrEmpty(iStr))
            {
                return string.Empty;
            }
            if(m_ParseStringConfigs.TryGetValue(iLangName, out ParseStringConfig config))
            {
                iStr = config.Parse(iStr);
            }
            int len = iStr.Length;
            if (len < 2 || iStr[0] != '"' || iStr[len - 1] != '"')
            {
                return iStr;
            }
            //return iStr.Substring(1, len - 2);//remove "xxx"
            {
                StringBuilder aSB = new StringBuilder();
                for (int i = 1; i < iStr.Length - 1; i++)
                {
                    char aC = iStr[i];
                    switch (aC)
                    {
                        case '"':
                            {
                                int nextId = i + 1;
                                if (iStr.Length > nextId && iStr[nextId] == '"')
                                {
                                    i++;
                                }

                                //aSB.Append('\\');
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
            

        }
        protected void ParseData(byte[] iBytes, bool replaceOldKey, Format format)
        {
            switch (format)
            {
                case Format.csv:
                case Format.tsv:
                    {
                        ParseCsvData(iBytes, replaceOldKey, format);
                        break;
                    }
                //case Format.xlsx:
                //    {
                //        ParseXlsxData(iBytes, replaceOldKey, format);
                //        break;
                //    }
            }
        }
        public void ParseCsvData(byte[] iBytes, bool replaceOldKey, Format format)
        {
            if(iBytes == null)
            {
                Debug.LogError($"{GetType().Name}.ParseCsvData iBytes == null");
                return;
            }
            string iData = System.Text.Encoding.UTF8.GetString(iBytes);
            //Debug.LogError($"ParseData:{iData}");
            char seperator = ',';
            switch (format)
            {
                case Format.tsv:
                    {
                        seperator = '\t';
                        break;
                    }
            }
            UCL.Core.CsvLib.CSVData aCSV = new UCL.Core.CsvLib.CSVData(iData, seperator);
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
                                var val = ParseString(aLangName, aCSV.GetData(j, i));
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
                                var val = ParseString(aLangName, aCSV.GetData(j, i));
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

                foreach(var key in m_LocalizeDatas.Keys)
                {
                    GUILayout.Label($"{UCL_LocalizeManager.GetLanguageCodeName(key)}({key})", UCL_GUIStyle.LabelStyle);
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
            if(m_LocalizeType == LocalizeType.GoogleSheet)
            {
                if (!IsDownloading)
                {
                    if (GUILayout.Button("Download", UCL_GUIStyle.ButtonStyle))
                    {
                        StartDownload().Forget();
                    }
                }
                else if (!m_IsCancelDownload)
                {
                    GUILayout.BeginHorizontal();

                    if (GUILayout.Button("Cancel", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        Cancel();
                    }
                    GUILayout.Label($"{m_DownloadingInfo}", UCL_GUIStyle.LabelStyle);

                    GUILayout.EndHorizontal();
                }
            }
            foreach(var key in m_LocalizeDatas.Keys)//同步語言到m_LangKeys
            {
                m_LangKeys.Add(key);
            }
            
            var langs = m_LocalizeDatas.Keys.ToList();
            if (!langs.IsNullOrEmpty())
            {
                GUILayout.Space(UCL_GUIStyle.GetScaledSize(10));
                GUILayout.BeginHorizontal();
                GUILayout.Label(UCL_LocalizeManager.Get("Lang"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                string lang = iDataDic.GetData(nameof(lang), langs[0]);
                lang = UCL_GUILayout.PopupAuto(lang, langs, iDataDic, "langs");
                iDataDic.SetData(nameof(lang), lang);
                GUILayout.EndHorizontal();

                //var lang = langIndex;

                var dic = m_LocalizeDatas[lang].m_LocalizeDic;

                var keys = dic.Keys.ToList();
                string key = "";
                if (!keys.IsNullOrEmpty())
                {
                    GUILayout.BeginHorizontal();

                    GUILayout.Label(UCL_LocalizeManager.Get("Key"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    key = iDataDic.GetData(nameof(key), keys[0]);
                    key = UCL_GUILayout.PopupAuto(key, keys, iDataDic, "keys");
                    iDataDic.SetData(nameof(key), key);
                    //var key = keys[keyIndex];

                    GUILayout.EndHorizontal();


                    GUILayout.BeginHorizontal();
                    GUILayout.Label(lang, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                    dic[key] = GUILayout.TextArea(dic[key], UCL_GUIStyle.TextAreaStyle);
                    GUILayout.EndHorizontal();

                    foreach (var curLang in langs)
                    {
                        if (curLang.Equals(lang))//不重複顯示相同語言的
                        {
                            continue;
                        }
                        var curLangDic = m_LocalizeDatas[curLang].m_LocalizeDic;

                        if (!curLangDic.ContainsKey(key))
                        {
                            curLangDic.Add(key, key);
                        }
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(curLang, UCL_GUIStyle.LabelStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60)));
                        curLangDic[key] = GUILayout.TextArea(curLangDic[key], UCL_GUIStyle.TextAreaStyle);
                        GUILayout.EndHorizontal();

                    }
                    //GUILayout.Label($"{dic[key]}", UCL_GUIStyle.LabelStyle);
                }
                GUILayout.Space(UCL_GUIStyle.GetScaledSize(20));
                if (!string.IsNullOrEmpty(key))
                {
                    if (GUILayout.Button(UCL_LocalizeManager.Get("DeleteTargetDes", key), UCL_GUIStyle.GetButtonStyle(Color.white)))
                    {
                        UCL_OptionPage.ConfirmDelete(key, () => 
                        {
                            foreach (var curLang in langs)
                            {
                                var curLangDic = m_LocalizeDatas[curLang].m_LocalizeDic;
                                if (curLangDic.ContainsKey(key))
                                {
                                    curLangDic.Remove(key);
                                }
                            }
                        });
                    }
                }


                GUILayout.Space(UCL_GUIStyle.GetScaledSize(20));
                using (var scope = new GUILayout.HorizontalScope())
                {
                    string newKeyName = iDataDic.GetData(nameof(newKeyName), "New Key");

                    if (GUILayout.Button(UCL_LocalizeManager.Get("Add"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(UCL_GUIStyle.GetScaledSize(60))))
                    {
                        if (!string.IsNullOrEmpty(newKeyName))
                        {
                            iDataDic.SetData(nameof(key), newKeyName);
                            foreach (var curLang in langs)
                            {
                                var curLangDic = m_LocalizeDatas[curLang].m_LocalizeDic;
                                if (!curLangDic.ContainsKey(newKeyName))
                                {
                                    curLangDic[newKeyName] = newKeyName;
                                }
                            }
                        }
                    }
                    newKeyName = GUILayout.TextField(newKeyName, UCL_GUIStyle.TextFieldStyle);
                    iDataDic.SetData(nameof(newKeyName), newKeyName);

                    //GUILayout.Label(UCL_LocalizeManager.Get("Add"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                }
                
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

        public IList<string> GetAllKeys()
        {
            if (m_LocalizeDatas.IsNullOrEmpty())
            {
                return Array.Empty<string>();
            }
            var dic = m_LocalizeDatas.Values.First().m_LocalizeDic;

            return dic.Keys.ToList();
        }
    }

    [System.Serializable]
    public class UCL_LocalizeAssetEntry : UCL_AssetEntryDefault<UCL_LocalizeAsset>
    {
        public const string DefaultID = "Default";

        public UCL_LocalizeAssetEntry() { m_ID = DefaultID; }
        public UCL_LocalizeAssetEntry(string iID) { m_ID = iID; }

    }

    public class UCL_LocalizeData : UCL.Core.JsonLib.UnityJsonSerializable, UCL.Core.UCLI_ShortName
    {
        public UCL_LocalizeData() { }

        public UCL_LocalizeAssetEntry m_Localize = new();

        [UCL.Core.PA.UCL_List(nameof(GetAllKeys))]
        public string m_Key;

        /// <summary>
        /// Get all ID of this UCL_Asset with cache
        /// 抓取此類型中所有的ID
        /// </summary>
        /// <returns></returns>
        public IList<string> GetAllKeys()
        {
            try
            {
                var data = m_Localize.GetData();
                return data.GetAllKeys();
            }
            catch { }//No Data Exception

            return Array.Empty<string>();
        }
        virtual public string GetShortName()
        {
            string name = null;
            try
            {
                name = GetLocalize();
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
            }
            if (string.IsNullOrEmpty(name))
            {
                return "N/A";
            }
            return name.Replace("\n", " ").CutToMaxLengthRichText(20);
        }
        public string GetLocalize()
        {
            if (string.IsNullOrEmpty(m_Key))
            {
                return string.Empty;
            }

            try
            {
                var data = m_Localize.GetData();
                var result = data.GetLocalize(UCL_LocalizeManager.s_LangName, m_Key);
                if (result.success)
                {
                    return result.value;
                }
            }
            catch { }//No Data Exception

            return m_Key;
        }

        public bool HasLocalize
        {
            get
            {
                if (string.IsNullOrEmpty(m_Key)) return false;
                if (m_Localize.IsEmpty)
                {
                    return false;
                }

                return true;
            }
        }
    }
}