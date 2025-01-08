
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

    public enum ELoadingState
    {
        None,
        Loading,
        Complete,

        Disposed,
    }
    #region Steam

    /// <summary>
    /// 用在SteamWorkshop
    /// </summary>
    public enum WorkshopFileType
    {
        k_EWorkshopFileTypeFirst = 0,
        /// <summary>
        /// 這種類型主要用於描述將上傳到 Steam 社區的文件。這些文件通常是由用戶生成的內容，如模組、地圖、皮膚等，供其他玩家下載和使用
        /// 通常是由玩家創建並分享的內容，旨在增強遊戲體驗。
        /// </summary>
        k_EWorkshopFileTypeCommunity = 0,
        /// <summary>
        /// 這種類型用於描述包含微交易的文件。這些文件通常是遊戲內購買的內容，如特殊道具、裝備等。
        /// </summary>
        k_EWorkshopFileTypeMicrotransaction = 1,
        /// <summary>
        /// 這種類型用於描述集合文件。集合文件是由多個單獨的工作坊項目組成的集合，方便玩家一次性訂閱多個項目。
        /// </summary>
        k_EWorkshopFileTypeCollection = 2,
        /// <summary>
        /// 這種類型用於描述藝術作品文件。這些文件通常是用戶創建的藝術作品，如壁紙、概念藝術等。
        /// </summary>
        k_EWorkshopFileTypeArt = 3,
        /// <summary>
        /// 這種類型用於描述視頻文件。這些文件通常是用戶創建的遊戲視頻、教程等。
        /// </summary>
        k_EWorkshopFileTypeVideo = 4,

        /// <summary>
        /// 這種類型用於描述截圖文件。這些文件通常是用戶在遊戲中截取的圖片，展示遊戲中的精彩瞬間。
        /// </summary>
        k_EWorkshopFileTypeScreenshot = 5,

        /// <summary>
        /// 這種類型用於描述完整的遊戲文件。這些文件通常是由開發者創建的完整遊戲或遊戲模組。
        /// 通常是完整的遊戲或大型遊戲模組，而不是單純的附加內容。
        /// 通常是由遊戲開發者創建並上傳，而不是由普通玩家生成的內容。
        /// </summary>
        k_EWorkshopFileTypeGame = 6,
        /// <summary>
        /// 這種類型用於描述軟件文件。這些文件通常是用戶創建的應用程序或工具。
        /// </summary>
        k_EWorkshopFileTypeSoftware = 7,
        /// <summary>
        /// 這種類型用於描述概念文件。這些文件通常是用戶創建的遊戲或項目的概念設計。
        /// </summary>
        k_EWorkshopFileTypeConcept = 8,
        /// <summary>
        /// 這種類型用於描述網絡指南文件。這些文件通常是用戶創建的遊戲指南、教程等。
        /// </summary>
        k_EWorkshopFileTypeWebGuide = 9,
        /// <summary>
        /// 這種類型用於描述集成指南文件。這些文件通常是遊戲內部的指南或教程。
        /// </summary>
        k_EWorkshopFileTypeIntegratedGuide = 10,
        /// <summary>
        /// 這種類型用於描述商品文件。這些文件通常是與遊戲相關的實物商品，如T恤、海報等。
        /// </summary>
        k_EWorkshopFileTypeMerch = 11,
        /// <summary>
        /// 這種類型用於描述控制器綁定文件。這些文件通常是用戶創建的遊戲控制器配置。
        /// </summary>
        k_EWorkshopFileTypeControllerBinding = 12,
        /// <summary>
        /// 這種類型用於描述 Steamworks 訪問邀請文件。這些文件通常是用於邀請其他用戶訪問特定的 Steamworks 功能。
        /// </summary>
        k_EWorkshopFileTypeSteamworksAccessInvite = 13,
        /// <summary>
        /// 這種類型用於描述 Steam 上的視頻文件。這些文件通常是用戶創建的遊戲視頻、教程、實況錄像等。
        /// </summary>
        k_EWorkshopFileTypeSteamVideo = 14,
        /// <summary>
        /// 這種類型用於描述由遊戲管理的項目文件。這些文件通常是由遊戲內部系統管理和更新的內容，如遊戲內的物品、資源等。
        /// </summary>
        k_EWorkshopFileTypeGameManagedItem = 15,
        /// <summary>
        /// 這種類型用於描述短視頻片段文件。這些文件通常是用戶創建的遊戲精彩瞬間、短片等。
        /// </summary>
        k_EWorkshopFileTypeClip = 16,
        k_EWorkshopFileTypeMax = 17
    }

    #endregion

    /// <summary>
    /// UCL_Module contains info about how to load assets in this module
    /// </summary>
    public class UCL_Module : UCL.Core.JsonLib.UnityJsonSerializable, UCLI_ID, UCLI_ShortName, UCLI_FieldOnGUI
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
            /// <summary>
            /// 編輯過的次數(用來判斷是否要安裝)
            /// </summary>
            public long m_UTC_TimeStamp;

            /// <summary>
            /// 模組ID 理論上會跟創建模組資料夾同名
            /// </summary>
            public string m_ID;
            /// <summary>
            /// 用在Steam工作坊的Title
            /// </summary>
            public string m_Title = "Mod Title";
            /// <summary>
            /// 模組描述
            /// </summary>
            public string m_Description = "Mod Description";

            /// <summary>
            /// 用在SteamWorkshop的檔案類型
            /// </summary>
            public WorkshopFileType m_WorkshopFileType = WorkshopFileType.k_EWorkshopFileTypeCommunity;

            /// <summary>
            /// 模組語言(Steam)
            /// </summary>
            public SteamAPILangCode m_SteamAPILangCode = SteamAPILangCode.english;



            /// <summary>
            /// 模組標籤
            /// </summary>
            public List<string> m_Tags = new List<string>();

            /// <summary>
            /// 相依模組 載入此模組時會同時載入相依模組
            /// </summary>
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
                //Debug.LogError($"CheckVersion m_Version:({m_Version},{iConfig.m_Version})");
                //Debug.LogError($"CheckVersion m_UTC_TimeStamp:({m_UTC_TimeStamp},{iConfig.m_UTC_TimeStamp})");

                if (m_Version != iConfig.m_Version) return false;

                //aBuiltinConfig.CheckVersion(m_Config) 比較方式是用內建版本與已安裝版本進行比較
                if (m_UTC_TimeStamp < iConfig.m_UTC_TimeStamp)//當前"內建版本"比"已安裝版本"更舊 代表玩家編輯過 不強制更新
                {
                    //Debug.LogError($"CheckVersion m_UTC_TimeStamp({m_UTC_TimeStamp}) < iConfig.m_UTC_TimeStamp({iConfig.m_UTC_TimeStamp})");
                    return true;
                }

                if (m_LastEditTime != iConfig.m_LastEditTime)//edited!!
                {
                    return false;
                }

                return true;
            }
            public long GetTimeStamp()
            {
                long timeStamp = Convert.ToInt64((DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds);
                return timeStamp;
            }
            public void OnModuleEdit()
            {
                m_LastEditTime = System.DateTime.Now.ToString(DateFormat);
                m_UTC_TimeStamp = GetTimeStamp();
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
        protected ELoadingState m_LoadLogoState = ELoadingState.None;
        protected Texture2D m_Logo;
        /// <summary>
        /// 編輯器內 選取的GroupID
        /// </summary>
        static protected string SelectedGroupID 
        {
            get => PlayerPrefs.GetString(nameof(SelectedGroupID), string.Empty);
            set => PlayerPrefs.SetString(nameof(SelectedGroupID), value);
        }

        static protected string SelectedAssetID
        {
            get => PlayerPrefs.GetString(nameof(SelectedAssetID), string.Empty);
            set => PlayerPrefs.SetString(nameof(SelectedAssetID), value);
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
                //Debug.LogError($"Set ID:{value}");
                m_ID = value;
                m_Config.m_ID = value;
            }
        }
        private string m_ID = string.Empty;
        public string GetShortName() => $"UCL_Module({ID})";
        #endregion
        public bool IsLoading => m_IsLoading;

        public void Init(string iID, UCL_ModuleEditType iModuleEditType, UCL_Module.Config config)
        {
            ID = iID;
            //AssetType = iAssetType;
            ModuleEditType = iModuleEditType;

            if(config != null)
            {
                m_Config = config;
            }

            if (ID != UCL_ModuleEntry.CoreModuleID)
            {
                var coreModule = UCL_ModuleEntry.CoreModule;
                if (!m_Config.m_DependenciesModules.Contains(coreModule))
                {
                    m_Config.m_DependenciesModules.Insert(0, coreModule);
                }
                //m_Config.m_DependenciesModules.Add(new UCL_ModuleEntry(UCL_ModuleEntry.CoreModuleID));
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
            LoadConfig();
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
            ClearCache();
        }
//#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        protected void LoadConfig()
//#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
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
        public string ExportModule(bool iExportConfig = true)
        {
            string aFolderPath = Path.Combine(Application.persistentDataPath, "ExportedModules");
            Directory.CreateDirectory(aFolderPath);
            string path = m_ModuleEntry.ZipModule(aFolderPath, iExportConfig);

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            UCL.Core.FileLib.WindowsLib.OpenExplorer(aFolderPath);
#endif
            return path;
        }
        public void ClearCache()
        {
            ModuleEntry.ClearCache();
        }
        public Texture2D Logo
        {
            get
            {
                if(m_LoadLogoState == ELoadingState.None)
                {
                    GetLogoAsync().Forget();
                }
                return m_Logo;
            }
        }
        public async UniTask<Texture2D> GetLogoAsync()
        {
            switch (m_LoadLogoState)
            {
                case ELoadingState.Loading://等待載入完成
                    {
                        await UniTask.WaitUntil(() => m_LoadLogoState != ELoadingState.Loading);
                        break;
                    }
                case ELoadingState.None:
                    {
                        m_LoadLogoState = ELoadingState.Loading;
                        var path = ModuleEntry.LogoPath;
                        if (!File.Exists(path))
                        {
                            return null;
                        }
                        m_Logo = await UCL.Core.TextureLib.Lib.LoadTextureFromFile(path);
                        m_LoadLogoState = ELoadingState.Complete;
                        break;
                    }
            }

            return m_Logo;
        }
        /// <summary>
        /// 顯示模組內容
        /// </summary>
        /// <param name="iFieldName"></param>
        /// <param name="iDataDic"></param>
        /// <returns></returns>
        /// <param name="iParams"></param>
        virtual public object OnGUI(string iFieldName, UCL.Core.UCL_ObjectDictionary iDataDic, UCL_GUILayout.DrawObjectParams iParams)
        {
            UCL.Core.UI.UCL_GUILayout.DrawObjExSetting aSetting = new UCL_GUILayout.DrawObjExSetting();
            aSetting.OnShowField = () =>
            {
                using (var scope = new GUILayout.HorizontalScope())
                {
                    bool showLogo = UCL_GUILayout.Toggle(iDataDic, "showLogoToggle");
                    GUILayout.BeginVertical();
                    GUILayout.Label("Logo", UCL_GUIStyle.LabelStyle);
                    if (showLogo)
                    {
                        Texture2D logo = Logo;
                        //if(!iDataDic.ContainsKey(nameof(logo)))//尚未讀取Logo
                        //{
                        //    async void LoadLogo()
                        //    {
                        //        var result = await GetLogoAsync();
                        //        if(iDataDic != null)
                        //        {
                        //            iDataDic.SetData(nameof(logo), result);
                        //        }
                        //    }

                        //    iDataDic.SetData(nameof(logo), null);
                        //    LoadLogo();
                        //}
                        //logo = iDataDic.GetData<Texture2D>(nameof(logo), null);
                        if(logo != null)
                        {
                            float size = UCL_GUIStyle.GetScaledSize(128);
                            GUILayout.Box(logo, UCL_GUIStyle.BoxStyle, GUILayout.Height(size));
                        }
                    }
                    
                    GUILayout.EndVertical();
                }
                    

                using (var scope = new GUILayout.HorizontalScope())
                {
                    bool showInfo = UCL_GUILayout.Toggle(iDataDic, "ContentToggle");
                    using (var scope2 = new GUILayout.VerticalScope())
                    {
                        GUILayout.Label(UCL_LocalizeManager.Get("Module Content"), UCL_GUIStyle.LabelStyle);
                        if (showInfo)
                        {
                            var dic = iDataDic.GetSubDic("PreviewDatas");
                            var cache = ModuleEntry.GetAssetsInfoCache(true);
                            var assetsInfo = cache.m_AssetsInfo;
                            if (!assetsInfo.IsNullOrEmpty())
                            {
                                
                                List<string> typeNameList = dic.GetData<List<string>>(nameof(typeNameList), null);
                                List<UCL_AssetTypeInfo> typeInfoList = dic.GetData<List<UCL_AssetTypeInfo>>(nameof(typeInfoList), null);
                                if (typeNameList == null)
                                {
                                    typeNameList = new();
                                    typeInfoList = new();
                                    foreach (var asset in assetsInfo.Keys)
                                    {
                                        typeNameList.Add(UCL_LocalizeLib.GetLocalize(asset.m_Type.Name));
                                        typeInfoList.Add(asset);
                                    }
                                    dic.SetData(nameof(typeNameList), typeNameList);
                                    dic.SetData(nameof(typeInfoList), typeInfoList);
                                }

                                int selectedIndex = dic.GetData(nameof(selectedIndex), 0);
                                GUILayout.BeginHorizontal();
                                GUILayout.Label(UCL_LocalizeManager.Get("Asset Type"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                                selectedIndex = UCL_GUILayout.PopupAuto(selectedIndex, typeNameList, dic, "selectedIndexPopup");
                                GUILayout.EndHorizontal();
                                dic.SetData(nameof(selectedIndex), selectedIndex);

                                var typeInfo = typeInfoList[selectedIndex];
                                var type = typeInfo.m_Type;
                                var typeDic = dic.GetSubDic(type.FullName);

                                UCL_ModulePath.PersistantPath.AssetInfo assetInfo = assetsInfo[typeInfo];

                                List<string> assetNameList = typeDic.GetData<List<string>>(nameof(assetNameList), null);
                                if (assetNameList == null)
                                {
                                    assetNameList = new();
                                    foreach (var id in assetInfo.m_IDs)
                                    {
                                        assetNameList.Add(UCL_LocalizeLib.GetLocalize(id));
                                    }
                                    typeDic.SetData(nameof(assetNameList), assetNameList);
                                }
                                GUILayout.BeginHorizontal();
                                int assetIndex = typeDic.GetData(nameof(assetIndex), 0);
                                GUILayout.Label("ID", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                                //選取要查看的AssetID
                                assetIndex = UCL_GUILayout.PopupAuto(assetIndex, assetNameList, typeDic, "assetIndexPopup");

                                typeDic.SetData(nameof(assetIndex), assetIndex);
                                GUILayout.EndHorizontal();
                                var previewDic = typeDic.GetSubDic($"preview_{assetIndex}");
                                GUILayout.BeginHorizontal();
                                string assetId = assetInfo.m_IDs[assetIndex];
                                bool preview = UCL_GUILayout.Toggle(previewDic, "PreviewToggle", iDefaultValue:false);
                                using (var scope4 = new GUILayout.VerticalScope())
                                {
                                    GUILayout.Label(UCL_LocalizeLib.GetLocalize(assetId), UCL_GUIStyle.LabelStyle);
                                    if (preview)
                                    {
                                        var data = assetInfo.GetAsset(assetId);
                                        data.Preview(previewDic);
                                    }
                                }
                                GUILayout.EndHorizontal();
                            }
                        }
                    }
                }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                if (GUILayout.Button(UCL_LocalizeManager.Get("Open Folder"), UCL_GUIStyle.ButtonStyle))
                {
                    UCL.Core.FileLib.WindowsLib.OpenExplorer(ModuleEntry.RootFolder);
                }
#endif
                //const int MaxItemsPerPage = 10;
                //using(var scope = new GUILayout.HorizontalScope())
                //{
                //    bool showInfo = UCL_GUILayout.Toggle(iDataDic, "ContentToggle");
                //    using (var scope2 = new GUILayout.VerticalScope())
                //    {
                //        GUILayout.Label(UCL_LocalizeManager.Get("Module Content"), UCL_GUIStyle.LabelStyle);

                //        if (showInfo)
                //        {
                //            var cache = ModuleEntry.GetAssetsInfoCache(true);
                //            var assetsInfo = cache.m_AssetsInfo;
                //            if (!assetsInfo.IsNullOrEmpty())
                //            {
                //                var dic = iDataDic.GetSubDic("PreviewDatas");
                //                using (var aVerticalScope = new GUILayout.VerticalScope())
                //                {
                //                    int typeCount = cache.m_AssetsInfo.Count;
                //                    var typeResult = UCL_GUILayout.DrawSelectPage(dic.GetSubDic(nameof(UCL_GUILayout.DrawSelectPage)),
                //                        typeCount, MaxItemsPerPage);

                //                    int typeStartIndex = typeResult.startIndex;
                //                    int typeLastIndex = Mathf.Min(typeCount, typeStartIndex + MaxItemsPerPage);

                //                    int index = 0;
                //                    foreach (var typeInfo in cache.m_AssetsInfo.Keys)
                //                    {
                //                        if (index >= typeLastIndex)
                //                        {
                //                            break;
                //                        }
                //                        if (index++ < typeStartIndex)
                //                        {
                //                            continue;
                //                        }


                //                        var type = typeInfo.m_Type;
                //                        var typeDic = dic.GetSubDic(type.FullName);

                //                        GUILayout.BeginHorizontal();
                //                        bool show = UCL_GUILayout.Toggle(typeDic, "ShowInfo");
                //                        using(var scope3 = new GUILayout.VerticalScope())
                //                        {
                //                            GUILayout.Label(UCL_LocalizeLib.GetLocalize(type.Name), UCL_GUIStyle.LabelStyle);
                //                            if (show)
                //                            {
                //                                UCL_ModulePath.PersistantPath.AssetInfo assetInfo = assetsInfo[typeInfo];

                //                                int itemCount = assetInfo.m_IDs.Count;
                //                                var result = UCL_GUILayout.DrawSelectPage(typeDic.GetSubDic(nameof(UCL_GUILayout.DrawSelectPage)), itemCount, MaxItemsPerPage);
                //                                int startIndex = result.startIndex;
                //                                int lastIndex = Mathf.Min(itemCount, startIndex + MaxItemsPerPage);
                //                                for (int i = 0; i < itemCount; i++)
                //                                {
                //                                    string id = assetInfo.m_IDs[i];
                //                                    if (i >= lastIndex)
                //                                    {
                //                                        break;
                //                                    }
                //                                    if (i < startIndex)
                //                                    {
                //                                        continue;
                //                                    }

                //                                    var previewDic = typeDic.GetSubDic($"preview_{id}");
                //                                    GUILayout.BeginHorizontal();

                //                                    bool preview = UCL_GUILayout.Toggle(previewDic, "PreviewToggle");
                //                                    using(var scope4 = new GUILayout.VerticalScope())
                //                                    {
                //                                        GUILayout.Label(UCL_LocalizeLib.GetLocalize(id), UCL_GUIStyle.LabelStyle);
                //                                        if (preview)
                //                                        {
                //                                            var data = assetInfo.GetAsset(id);
                //                                            data.Preview(previewDic);
                //                                        }
                //                                    }
                //                                    GUILayout.EndHorizontal();
                //                                }
                //                                //GUILayout.Label(ids.ConcatToString(), UCL_GUIStyle.LabelStyle);
                //                            }
                //                        }

                //                        GUILayout.EndHorizontal();
                //                    }
                //                }
                //            }
                //        }
                //    }
                //}
            };

            UCL_GUILayout.DrawField(this, iDataDic.GetSubDic("Data"), iFieldName, false, iDrawObjExSetting: aSetting);
            return this;
        }
        virtual public void ContentOnGUI(UCL_ObjectDictionary iDataDic)
        {
            //var aLabelStyle = UCL_GUIStyle.GetLabelStyle(Color.white, 18);
            //var aButtonStyle = UCL_GUIStyle.GetButtonStyle(Color.white, 18);

            var aAllAssetTypeInfos = UCLI_Asset.GetAllAssetTypesInfo();
            if (!aAllAssetTypeInfos.IsNullOrEmpty())
            {
                using (var aScope = new GUILayout.HorizontalScope())
                {
                    GUILayout.Label(UCL_LocalizeManager.Get("Asset Group"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
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