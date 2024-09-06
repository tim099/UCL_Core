
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/20 2024 22:46
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{
    public enum UCL_AssetType
    {
        StreamingAssets = 0,
        PersistentDatas,

        Addressables,


        BuiltinModules,
        /// <summary>
        /// Path xxx\SteamLibrary\steamapps\workshop\content\xxxx
        /// Example D:\SteamLibrary\steamapps\workshop\content\1158310\2973143830
        /// </summary>
        SteamMods,
    }

    /// <summary>
    /// UCL_Module contains info about how to load assets in this module
    /// </summary>
    public class UCL_Module : UCL.Core.JsonLib.UnityJsonSerializable, UCLI_ID, UCLI_ShortName//, UCLI_FieldOnGUI
    {
        public const string NotInstalledID = "None";
        public class Config : UCL.Core.JsonLib.UnityJsonSerializable
        {
            const string DateFormat = "yyyy/MM/dd/HH:mm:ss.ffff";

            public string m_Version = "1.0.0";
            /// <summary>
            /// 最後編輯的時間(用來判斷是否要安裝)
            /// </summary>
            public string m_LastEditTime = string.Empty;
            public string m_ID;

            public List<UCL_ModuleEntry> m_DependenciesModules = new ();

            /// <summary>
            /// return true if Installed
            /// </summary>
            public bool Installed => m_Version != NotInstalledID;
            /// <summary>
            /// return true if Config version is same
            /// </summary>
            /// <param name="iConfig"></param>
            /// <returns></returns>
            public bool CheckVersion(Config iConfig)
            {
                if (m_Version != iConfig.m_Version) return false;
                if (m_LastEditTime != iConfig.m_LastEditTime) return false;

                return true;
            }

            public void OnModuleEdit()
            {
                m_LastEditTime = System.DateTime.Now.ToString(DateFormat);
            }
        }


        /// <summary>
        /// StreamingAssets for BuiltinModules
        /// and PersistentDatas for Runtime
        /// </summary>
        public UCL_AssetType AssetType { get ; set ; }
        public UCL_ModuleEditType ModuleEditType {
            get => m_ModuleEditType;
            private set
            {
                m_ModuleEditType = value;
                m_ModuleEntry = UCL_ModulePath.PersistantPath.GetModulesEntry(ModuleEditType).GetModuleEntry(ID);
            } 
        }
        private UCL_ModuleEditType m_ModuleEditType;
        public UCL_ModulePath.PersistantPath.ModuleEntry ModuleEntry => m_ModuleEntry;


        public UCL_ModulePath.PersistantPath.ModuleEntry BuiltinModuleEntry
        {
            get => UCL_ModulePath.PersistantPath.Builtin.GetModuleEntry(ID);
        }
        public UCL_ModulePath.PersistantPath.ModuleEntry RuntimeModuleEntry
        {
            get => UCL_ModulePath.PersistantPath.Runtime.GetModuleEntry(ID);
        }

        public Config m_Config = new Config();

        protected bool m_IsLoading = false;
        protected bool m_Installing = false;

        /// <summary>
        /// 編輯器內 選取的GroupID
        /// </summary>
        static protected string SelectedGroupID {
            get => PlayerPrefs.GetString("SelectedGroupID", string.Empty);
            set => PlayerPrefs.SetString("SelectedGroupID", value);
        }

        static protected string SelectedAssetID
        {
            get => PlayerPrefs.GetString("SelectedAssetID", string.Empty);
            set => PlayerPrefs.SetString("SelectedAssetID", value);
        }
        protected UCL_ModulePath.PersistantPath.ModuleEntry m_ModuleEntry;
        //protected UCL_StreamingAssetFileInspector m_FileInfo = new UCL_StreamingAssetFileInspector();
        #region Interface
        /// <summary>
        /// Unique ID of this Module
        /// </summary>
        public string ID { 
            get => m_ID; 
            set
            {
                m_ID = value;
                m_Config.m_ID = value;
            }
        }
        private string m_ID = string.Empty;
        public string GetShortName() => $"UCL_Module({ID})";
        #endregion
        public bool IsLoading => m_IsLoading;

        public void Init(string iID, UCL_ModuleEditType iModuleEditType)
        {
            ID = iID;
            //AssetType = iAssetType;
            ModuleEditType = iModuleEditType;
            if (ID != UCL_ModuleEntry.CoreModuleID)
            {
                m_Config.m_DependenciesModules.Add(new UCL_ModuleEntry(UCL_ModuleEntry.CoreModuleID));
            }
        }
        public void Load(string iID, UCL_ModuleEditType iModuleEditType)
        {
            //Debug.LogError($"UCL_Module.Load, iID:{iID},iModuleEditType:{iModuleEditType}");
            if (m_IsLoading)
            {
                Debug.LogError("UCL_Module.Load, m_IsLoading");
                return;
            }
            ID = iID;
            ModuleEditType = iModuleEditType;
            LoadAsync().Forget();
        }
        /// <summary>
        /// Save Module Config
        /// </summary>
        public void Save()
        {
            //Debug.LogError($"aFolderPath:{aFolderPath}");
            ModuleEntry.SaveConfig(m_Config);

            //UCL_ModuleService.PathConfig.SaveModuleConfig(ID, m_Config.SerializeToJson());
        }
        /// <summary>
        /// 當前模組被編輯時觸發
        /// (例如UCL_Asset存檔或刪除時)
        /// </summary>
        public void OnModuleEdit()
        {
            m_Config.OnModuleEdit();
            Save();
        }
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        protected async UniTask LoadAsync()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
            m_IsLoading = true;
            try
            {
                m_Config = ModuleEntry.GetConfig();

                //var aJson = await UCL_ModuleService.PathConfig.LoadModuleConfig(ID);
                //if (aJson != null)
                //{
                //    m_Config.DeserializeFromJson(aJson);
                //}
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                m_IsLoading = false;
            }

        }

        /// <summary>
        /// Check if this Module is installed, if not than install this module
        /// </summary>
        /// <returns></returns>
        public async UniTask CheckAndInstall()
        {
            if (m_IsLoading)
            {
                await UniTask.WaitUntil(() => !m_IsLoading);
            }
            //Debug.LogError($"UCL_Module.CheckAndInstall, iID:{ID},iModuleEditType:{ModuleEditType}");
            bool aNeedInstall = true;
            if(m_Config.Installed && RuntimeModuleEntry.Installed)//Installed, check version
            {
                Config aBuiltinConfig = await BuiltinModuleEntry.GetBuiltinConfig();
                if (aBuiltinConfig.CheckVersion(m_Config))//Same Version!!
                {
                    aNeedInstall = false;
                }
                Debug.LogWarning($"ID:{ID},Cur Version:{m_Config.m_Version},Builtin Version:{aBuiltinConfig.m_Version}" +
                    $"LastEditTime:{m_Config.m_LastEditTime},Builtin LastEditTime:{aBuiltinConfig.m_LastEditTime}" +
                    $",aNeedInstall:{aNeedInstall}");
            }

            if(aNeedInstall)
            {
                await Install();
            }
        }
        /// <summary>
        /// Install to Application.persistentDataPath
        /// </summary>
        public async UniTask Install()
        {
            //await UCL.Core.Page.UCL_OptionPage.ShowAlertAsync($"UCL_Module.Install() ID:{ID}", "");
            if (m_Installing)
            {
                Debug.LogError($"UCL_Module.Install() ID:{ID} ,m_Installing!!");
                return;
            }
            if (m_IsLoading)
            {
                await UniTask.WaitUntil(() => !m_IsLoading);
            }
            m_Installing = true;
            try
            {
                UCL_ModulePath.PersistantPath.ModuleEntry aModuleConfig = UCL_ModulePath.PersistantPath.Builtin.GetModuleEntry(ID);

                await aModuleConfig.Install();
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                m_Installing = false;
            }

        }
        public void UnInstall()
        {
            if (m_Installing || m_IsLoading)
            {
                Debug.LogError($"UnInstall() ID:{ID},m_Installing:{m_Installing},m_IsLoading:{m_IsLoading}");
                return;
            }

            UCL_ModulePath.PersistantPath.ModuleEntry aModuleConfig = UCL_ModulePath.PersistantPath.Builtin.GetModuleEntry(ID);
            aModuleConfig.UnInstall();
        }

        /// <summary>
        /// 將模組輸出到指定目錄下
        /// </summary>
        public void ExportModule(bool iExportConfig = true)
        {
            string aFolderPath = Path.Combine(Application.persistentDataPath, "ExportedModules");
            Directory.CreateDirectory(aFolderPath);
            m_ModuleEntry.ZipModule(aFolderPath, iExportConfig);

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            UCL.Core.FileLib.WindowsLib.OpenExplorer(aFolderPath);
#endif
        }

        virtual public void OnGUI(UCL_ObjectDictionary iDataDic)
        {
            //var aLabelStyle = UCL_GUIStyle.GetLabelStyle(Color.white, 18);
            //var aButtonStyle = UCL_GUIStyle.GetButtonStyle(Color.white, 18);

            var aAllAssetTypeInfos = UCLI_Asset.GetAllAssetTypesInfo();
            if (!aAllAssetTypeInfos.IsNullOrEmpty())
            {
                using (var aScope = new GUILayout.HorizontalScope())
                {
                    GUILayout.Label("Asset Group", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    var groups = UCL_ModuleService.Ins.GetAssetGroups();//UCLI_Asset.GetAssetGroups();

                    var localizedGroups = UCLI_Asset.GetLocalizedAssetGroups();

                    int index = groups.IndexOf(SelectedGroupID);
                    int newIndex = UCL_GUILayout.PopupAuto(index, localizedGroups, iDataDic, "SelectedGroupID");
                    SelectedGroupID = groups[newIndex];
                }
                using (var aScope = new GUILayout.HorizontalScope())
                {
                    bool aEdit = false;
                    if (GUILayout.Button(UCL_LocalizeManager.Get("Edit"), UCL_GUIStyle.ButtonStyle, GUILayout.Width(160)))
                    {
                        aEdit = true;
                    }

                    var assetInfos = aAllAssetTypeInfos.Values.ToList();
                    assetInfos.Sort((a, b) => a.m_SortOrder.CompareTo(b.m_SortOrder));
                    var aAllAssetTypeNames = new List<string>();
                    var aAllAssetTypeLocalizedNames = new List<string>();
                    foreach (var info in assetInfos)
                    {
                        if(string.IsNullOrEmpty(SelectedGroupID) || string.IsNullOrEmpty(info.m_Group) || info.m_Group == SelectedGroupID)
                        {
                            aAllAssetTypeLocalizedNames.Add(info.m_LocalizedName);
                            aAllAssetTypeNames.Add(info.m_Name);
                        }
                    }
                    if (aAllAssetTypeNames.Count == 0)
                    {
                        aAllAssetTypeLocalizedNames.Add(string.Empty);
                        aAllAssetTypeNames.Add(string.Empty);
                    }

                    int aSelectedID = aAllAssetTypeNames.IndexOf(SelectedAssetID);
                    aSelectedID = UCL_GUILayout.PopupAuto(aSelectedID, aAllAssetTypeLocalizedNames, iDataDic, "SelectAssetType");//選取要編輯的UCL_Asset類型
                    SelectedAssetID = aAllAssetTypeNames[aSelectedID];


                    if (aEdit)//編輯按鈕被按下
                    {
                        var aType = aAllAssetTypeInfos[SelectedAssetID].m_Type;
                        try
                        {
                            UCLI_Asset aUtil = UCLI_Asset.GetUtilByType(aType);//Get Util
                            if (aUtil != null)
                            {
                                aUtil.CreateSelectPage();
                            }
                        }
                        catch (Exception iE)
                        {
                            Debug.LogError($"RCGI_CommonData aType:{aType.FullName},Exception:{iE}");
                            Debug.LogException(iE);
                        }
                    }

                }
            }
            GUILayout.Space(40);
            if(GUILayout.Button("Export Module", UCL_GUIStyle.ButtonStyle))
            {
                ExportModule(false);
            }
        }

        #region AssetCommonMeta

        public const string AssetMetaMetaName = ".CommonDataMeta";

        private Dictionary<string, UCL_AssetCommonMeta> m_AssetMetaDic = new ();

        private string GetAssetMetaPath(string iTypeName) => Path.Combine(ModuleEntry.GetAssetFolderPath(iTypeName), AssetMetaMetaName);
        public UCL_AssetCommonMeta GetAssetMeta(string iTypeName)
        {
            if (!m_AssetMetaDic.ContainsKey(iTypeName))
            {
                void SaveAssetMetaJson(string iJson)
                {
                    //if (!Directory.Exists(SaveFolderPath))
                    string path = GetAssetMetaPath(iTypeName);
                    var folderPath = Path.GetDirectoryName(path);
                    //https://stackoverflow.com/questions/27575384/c-sharp-directory-createdirectory-path-should-i-check-if-path-exists-first
                    Directory.CreateDirectory(folderPath);
                    File.WriteAllText(path, iJson);
                }
                UCL_AssetCommonMeta CreateAssetCommonMeta()
                {
                    UCL_AssetCommonMeta aCommonDataMeta = new UCL_AssetCommonMeta();

                    aCommonDataMeta.Init(iTypeName, SaveAssetMetaJson);

                    string aJson = GetAssetMetaJson(iTypeName);
                    if (!string.IsNullOrEmpty(aJson))
                    {
                        JsonData aData = JsonData.ParseJson(aJson);
                        aCommonDataMeta.DeserializeFromJson(aData);
                    }

                    return aCommonDataMeta;
                }
                m_AssetMetaDic[iTypeName] = CreateAssetCommonMeta();
            }

            return m_AssetMetaDic[iTypeName];
        }

        public string GetAssetMetaJson(string iTypeName)
        {
            //if (!Directory.Exists(SaveFolderPath)) https://stackoverflow.com/questions/27575384/c-sharp-directory-createdirectory-path-should-i-check-if-path-exists-first
            //Directory.CreateDirectory(SaveFolderPath);
            string path = GetAssetMetaPath(iTypeName);
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            return File.ReadAllText(path);
        }

        #endregion
    }
}