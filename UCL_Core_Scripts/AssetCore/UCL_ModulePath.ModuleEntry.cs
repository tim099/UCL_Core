
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core
{
    public static partial class UCL_ModulePath
    {

        public static partial class PersistantPath
        {

            #region ModuleConfig
            /// <summary>
            /// module path config of each module(child of ModulesEntry)
            /// 模組路徑相關的所有設定
            /// </summary>
            public class ModuleEntry
            {
                public const string ConfigName = "Config.json";
                public const string AssetFolderName = "UCL_Assets";
                public const string ModResourcesFolderName = "ModResources";


                private AssetsInfoCache m_AssetsInfoCache = null;
                public ModulesEntry p_ModulePathConfig { get; private set; }


                public string ID { get; private set; }
                /// <summary>
                /// RootFolder of this module in file system
                /// </summary>
                public string RootFolder { get; private set; }

                /// <summary>
                /// Path of install folder of this module
                /// (this is a persistant path)
                /// </summary>
                public string InstallFolder => UCL_ModulePath.PersistantPath.Runtime.GetModulePath(ID);

                /// <summary>
                /// ConfigPath in file system
                /// </summary>
                public string ConfigPath => Path.Combine(RootFolder, ConfigName);
                /// <summary>
                /// 創意工坊使用的Logo
                /// </summary>
                public string LogoPath => Path.Combine(RootFolder, "Logo.png");

                public string ModResourcesPath => Path.Combine(RootFolder, ModResourcesFolderName);
                public string ZipConfigName => $"{ID}.json";
                public string ZipFileName => $"{ID}.zip";
                public string ZipFilePath => Path.Combine(PersistantPath.ModulesZipFolder, ZipFileName);

                // 區塊職責：安裝完整性哨兵 (install-complete sentinel)。
                // 物理意義：安裝(解壓/複製)「全部成功」後才寫入此標記檔，內容為已安裝版本字串。
                //          安裝判定改看此標記是否存在，而非「目錄是否存在」——避免「解壓中途失敗留下半成品目錄」被誤判成已安裝。
                // 數值影響：標記不存在 → 視為未安裝完成 → CheckAndInstall 會整包刪除重裝 (不保留半成品舊版)。
                /// <summary>安裝完整性標記檔名 (寫在 InstallFolder 根目錄，'.' 開頭避免被資產列舉掃到)。</summary>
                public const string InstallCompleteMarkerName = ".install_complete";
                /// <summary>安裝完整性標記檔完整路徑 (位於 InstallFolder 內)。</summary>
                public string InstallCompleteMarkerPath => Path.Combine(InstallFolder, InstallCompleteMarkerName);

                /// <summary>
                /// return true if installed
                /// 注意：僅檢查目錄存在，不保證內容完整。安裝完整性請改用 <see cref="InstallComplete"/>。
                /// </summary>
                /// <returns></returns>
                public bool Installed => Directory.Exists(InstallFolder);

                /// <summary>
                /// return true if 安裝「完整成功」(目錄存在且完整性標記檔存在)。
                /// 只有解壓/複製全部成功後才會寫入標記檔，故此值為 true ⟺ 上一次安裝完整無中斷。
                /// </summary>
                public bool InstallComplete => Directory.Exists(InstallFolder) && File.Exists(InstallCompleteMarkerPath);

                /// <summary>
                /// e.g. UCL_Assets/UCL_SpriteAsset
                /// </summary>
                /// <param name="iType"></param>
                /// <returns></returns>
                public static string GetAssetRelativePath(System.Type iType) => GetAssetRelativePath(iType.Name);
                /// <summary>
                /// e.g. UCL_Assets/UCL_SpriteAsset
                /// </summary>
                /// <param name="iType"></param>
                /// <returns></returns>
                public static string GetAssetRelativePath(string iTypeName) => Path.Combine(AssetFolderName, iTypeName);


                public ModuleEntry(ModulesEntry iModulePathConfig, string iD)
                {
                    p_ModulePathConfig = iModulePathConfig;
                    ID = iD;
                    RootFolder = p_ModulePathConfig.GetModulePath(ID);
                }


                public string ZipModule(string iTargetPath = "", bool iExportConfig = true)
                {
                    if (string.IsNullOrEmpty(iTargetPath))
                    {
                        iTargetPath = PersistantPath.ModulesZipFolder;
                    }
                    string aZipPath = Path.Combine(iTargetPath, ZipFileName);
                    string aConfigPath = ConfigPath;

                    Directory.CreateDirectory(iTargetPath);
                    if (File.Exists(aZipPath))
                    {
                        File.Delete(aZipPath);
                    }
                    System.IO.Compression.ZipFile.CreateFromDirectory(RootFolder, aZipPath, System.IO.Compression.CompressionLevel.NoCompression, false);
                    if (iExportConfig)
                    {
                        if (File.Exists(aConfigPath))//Copy config
                        {
                            File.Copy(aConfigPath, Path.Combine(iTargetPath, ZipConfigName), true);
                        }
                    }
                    return aZipPath;
                }

                /// <summary>
                /// Install to Application.persistentDataPath
                /// </summary>
                public async UniTask Install()
                {
                    //await UCL.Core.Page.UCL_OptionPage.ShowAlertAsync($"ModuleConfig Install() ID:{ID}", "");

                    try
                    {
                        string aTargetPath = InstallFolder;
                        if (Directory.Exists(aTargetPath))
                        {
                            Directory.Delete(aTargetPath, true);//Delete all old files before install
                        }

                        // 區塊職責：實際把 zip 解壓到 aTargetPath。
                        // 物理意義：Windows 直接從 StreamingAssets 實體 zip 解壓；其他平台 (Android/iOS 等 StreamingAssets 在壓縮包內)
                        //          先用 UnityWebRequest 讀成 byte[] 再記憶體解壓。
                        // 數值影響：回傳 true=解壓全程無例外；false=失敗 (caller 不可寫安裝完整性標記、且須刪除半成品)。
                        //          *關鍵修正*：原版例外只 Debug.LogException 然後當沒事，導致「解壓失敗卻被視為成功」→ 半成品固化。
                        //          現在改為「回傳成功與否 + 例外向 caller 表達」，讓 caller 決定刪半成品 + 不寫標記 → 下次重裝。
                        async UniTask<bool> InstallModule()
                        {
                            try
                            {
                                switch (Application.platform)
                                {
                                    case RuntimePlatform.WindowsEditor:
                                    case RuntimePlatform.WindowsPlayer:
                                        {
                                            System.IO.Compression.ZipFile.ExtractToDirectory(ZipFilePath, aTargetPath);
                                            Debug.Log($"ModuleConfig.Install ID:{ID},aTargetPath:{aTargetPath},ZipFilePath:{ZipFilePath}");
                                            break;
                                        }
                                    default:
                                        {
                                            byte[] aBytes = await UCL_StreamingAssets.FullPath.LoadBytes(ZipFilePath);
                                            if (aBytes == null)
                                            {
                                                Debug.LogError($"ModuleConfig.Install ID:{ID},aTargetPath:{aTargetPath},ZipFilePath:{ZipFilePath}, aBytes == null (StreamingAssets 讀取失敗或截斷)");
                                                return false;
                                            }
                                            UCL.Core.FileLib.ZipLib.UnzipFromBytes(aBytes, aTargetPath);
                                            break;
                                        }
                                }
                                return true;
                            }
                            catch (System.Exception ex)
                            {
                                // 失敗要「看得見」：印出完整 context (ID/目標路徑/zip 路徑/例外) 方便診斷初次安裝失敗的真因。
                                Debug.LogError($"ModuleConfig.InstallModule FAILED ID:{ID},aTargetPath:{aTargetPath},ZipFilePath:{ZipFilePath},Exception:{ex}");
                                Debug.LogException(ex);
                                return false;
                            }
                        }

                        // 區塊職責：安裝全部成功後寫入完整性標記檔。
                        // 物理意義：標記內容 = 此次安裝的版本字串 + UTC 時間戳 (給人除錯看)。
                        // 數值影響：只有此檔存在才視為「安裝完整」(見 InstallComplete)；故務必只在解壓/複製全成功後呼叫。
                        void WriteInstallCompleteMarker()
                        {
                            try
                            {
                                string aVersion = "1.0.0";
                                try { aVersion = GetConfig()?.m_Version ?? aVersion; } catch { /* 取版本失敗不致命，標記仍寫 */ }
                                Directory.CreateDirectory(aTargetPath);
                                File.WriteAllText(InstallCompleteMarkerPath, $"{aVersion}\n{DateTime.UtcNow:o}");
                            }
                            catch (System.Exception ex)
                            {
                                Debug.LogError($"ModuleConfig.WriteInstallCompleteMarker FAILED ID:{ID},path:{InstallCompleteMarkerPath},Exception:{ex}");
                            }
                        }

                        // 區塊職責：安裝失敗時清掉半成品目錄。
                        // 物理意義：依 Tim 需求「不保存舊版本/半成品」——失敗即整包刪除，確保不會有壞檔被後續流程讀到。
                        void DeletePartialInstall()
                        {
                            try
                            {
                                if (Directory.Exists(aTargetPath))
                                {
                                    Directory.Delete(aTargetPath, true);
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Debug.LogError($"ModuleConfig.DeletePartialInstall FAILED ID:{ID},aTargetPath:{aTargetPath},Exception:{ex}");
                            }
                        }


#if UNITY_EDITOR
                        //模擬Runtime安裝流程
                        if (Application.isEditor)
                        {
                            var aInstallMode = UCL_ModuleService.Ins.ModuleConfig.m_EditorInstallMode;
                            Debug.LogWarning($"Install:{ID},aInstallMode:{aInstallMode}");
                            switch (aInstallMode)
                            {
                                case UCL_ModuleService.EditorInstallMode.Default:
                                    {
                                        if (Directory.Exists(RootFolder))
                                        {
                                            UCL.Core.FileLib.Lib.CopyDirectory(RootFolder, aTargetPath);
                                            // 複製成功 → 寫完整性標記 (Editor Default 模式視同安裝成功)
                                            WriteInstallCompleteMarker();
                                        }
                                        else
                                        {
                                            Debug.LogError($"ModuleConfig.Install Editor.Default ID:{ID}, RootFolder not exist:{RootFolder}");
                                        }
                                        break;
                                    }
                                case UCL_ModuleService.EditorInstallMode.UnZip:
                                    {
                                        var aZipFilePath = ZipFilePath;
                                        if (File.Exists(aZipFilePath))//if in Editor and Zip file exist
                                        {
                                            File.Delete(aZipFilePath);
                                        }
                                        ZipModule();
                                        bool aOk = await InstallModule();
                                        if (aOk) WriteInstallCompleteMarker(); else DeletePartialInstall();
                                        File.Delete(aZipFilePath);
                                        break;
                                    }
                            }

                            return;
                        }


                        //Editor專用的安裝流程
                        //if(Application.isEditor)
                        //{
                        //    //Debug.LogWarning($"Install in Editor:{ID},!File.Exists({ZipFilePath}),CopyDirectory({RootFolder},{aTargetPath})");

                        //    //Editor內改為永遠使用當前Builtin資料夾安裝
                        //    if (Directory.Exists(RootFolder))
                        //    {
                        //        UCL.Core.FileLib.Lib.CopyDirectory(RootFolder, aTargetPath);
                        //    }

                        //    return;
                        //}


#endif
                        //await UCL.Core.Page.UCL_OptionPage.ShowAlertAsync($"ModuleConfig UCL_StreamingAssets.FullPath.LoadBytes", $"ZipFilePath:{ZipFilePath}");



                        // Runtime (built 版本) 安裝：解壓成功才寫完整性標記；失敗則刪半成品 (不留壞檔)，下次啟動 CheckAndInstall 會重裝。
                        bool aInstallOk = await InstallModule();
                        if (aInstallOk)
                        {
                            WriteInstallCompleteMarker();
                        }
                        else
                        {
                            Debug.LogError($"ModuleConfig.Install ID:{ID} 安裝失敗，刪除半成品並等待下次重裝。aTargetPath:{aTargetPath}");
                            DeletePartialInstall();
                        }


                        //await UCL.Core.Page.UCL_OptionPage.ShowAlertAsync($"aZip.ExtractToDirectory Done!!", "");
                        //Debug.LogError($"Install ID:{ID},aTargetPath:{aTargetPath}" +
                        //    $"\nZipFilePath:{ZipFilePath}");
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogException(ex);
                    }


                    //using(Stream stream = new MemoryStream(aBytes))
                    //{
                    //    using (ZipArchive aZip = new ZipArchive(stream))
                    //    {
                    //        aZip.ReadAllTextFromEntry
                    //    }
                    //}


                }
                /// <summary>
                /// UnInstall from Application.persistentDataPath
                /// </summary>
                public void UnInstall()
                {
                    string aPath = InstallFolder;
                    if (!Directory.Exists(aPath))
                    {
                        return;
                    }
                    Directory.Delete(aPath, true);
                }

                public UCL_Module.Config GetConfig()
                {
                    UCL_Module.Config aConfig = new UCL_Module.Config();
                    string aConfigPath = ConfigPath;
                    //Debug.Log($"GetConfig ID:{ID}, aConfigPath:{aConfigPath}");
                    if (File.Exists(aConfigPath))//Get config
                    {
                        string aJson = File.ReadAllText(aConfigPath);
                        aConfig.DeserializeFromJson(JsonData.ParseJson(aJson));
                    }
                    else
                    {
                        aConfig.m_Version = UCL_Module.NotInstalledID;//Not Installed!!
                    }
                    return aConfig;
                }

                /// <summary>
                /// Get BuiltinConfig from streaming asset ModulesZipFolder
                /// </summary>
                /// <returns></returns>
                public async UniTask<UCL_Module.Config> GetBuiltinConfig()
                {
                    if (Application.isEditor)
                    {
                        return UCL_ModulePath.PersistantPath.Builtin.GetModuleEntry(ID).GetConfig();
                    }


                    string aZipConfigPath = Path.Combine(PersistantPath.ModulesZipFolder, ZipConfigName);
                    string aJson = await UCL_StreamingAssets.FullPath.LoadString(aZipConfigPath);

                    JsonData aJsonData = JsonData.ParseJson(aJson);
                    UCL_Module.Config aConfig = new UCL_Module.Config();
                    aConfig.DeserializeFromJson(aJsonData);
                    return aConfig;
                }

                public void SaveConfig(UCL_Module.Config iConfig)
                {
                    string aFolderPath = RootFolder;
                    //string aModuleRelativePath = UCL_ModulePath.RelativePath.GetBuiltinModulePath(iID);
                    if (!Directory.Exists(aFolderPath))
                    {
                        Directory.CreateDirectory(aFolderPath);
                    }
                    var aJson = iConfig.SerializeToJson();
                    UCL.Core.FileLib.Lib.WriteAllText(ConfigPath, aJson.ToJsonBeautify());

                    var aResFolder = ModResourcesPath;
                    if (!Directory.Exists(aResFolder))
                    {
                        Directory.CreateDirectory(aResFolder);
                        string aResReadMe = "Please put Mod resources in this folder";
                        File.WriteAllText(Path.Combine(aResFolder, "Readme.txt"), aResReadMe);
                    }
                }

                /// <summary>
                /// root folder of UCL_Assets
                /// 抓取模組根目錄
                /// </summary>
                /// <param name="iAssetType"></param>
                /// <returns></returns>
                public string GetAssetFolderPath(Type iAssetType) => Path.Combine(RootFolder, GetAssetRelativePath(iAssetType));
                /// <summary>
                /// root folder of UCL_Assets
                /// 抓取模組根目錄
                /// </summary>
                /// <param name="iAssetName"></param>
                /// <returns></returns>
                public string GetAssetFolderPath(string iAssetName) => Path.Combine(RootFolder, GetAssetRelativePath(iAssetName));

                public string GetAssetPath(Type iAssetType, string iID)
                {
                    string aFolderPath = string.Empty;
                    string aPath = string.Empty;
                    try
                    {
                        aFolderPath = GetAssetFolderPath(iAssetType);
                        aPath = Path.Combine(aFolderPath, $"{iID}.json");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                        Debug.LogError($"{GetType().Name}.GetAssetPath iAssetType:{iAssetType},iID:{iID}" +
                            $"\nPath:{aPath}" +
                            $"\nFolderPath:{aFolderPath}" +
                            $", Exception:{ex}");
                    }

                    return aPath;
                }
                /// <summary>
                /// Check if asset exist
                /// </summary>
                /// <param name="iID">ID of asset</param>
                /// <returns>true if asset exist</returns>
                public bool ContainsAsset(Type iAssetType, string iID)// => FileDatas.FileExists(iID);
                {
                    string aPath = GetAssetPath(iAssetType, iID);
                    //Debug.LogError($"ContainsAsset aPath:{aPath}");
                    return File.Exists(aPath);
                }
                /// <summary>
                /// All assets of iAssetType's ID in this module
                /// 回傳所有可編輯的AssetsID
                /// </summary>
                /// <param name="iAssetType"></param>
                /// <returns></returns>
                public IList<string> GetAllAssetsID(Type iAssetType)
                {
                    string aFolderPath = GetAssetFolderPath(iAssetType);
                    var aIDs = UCL.Core.FileLib.Lib.GetFilesName(aFolderPath, "*.json", SearchOption.TopDirectoryOnly, true);
                    //Debug.Log($"GetAllAssetsID iAssetType:{iAssetType.FullName}, aFolderPath:{aFolderPath},aIDs:{aIDs.ConcatString()}");
                    return aIDs;
                }
                public UCLI_Asset GetAsset(Type type, string iID)
                {
                    string aPath = GetAssetPath(type, iID);
                    if (!File.Exists(aPath))
                    {
                        Debug.LogError($"{GetType().Name}.GetAsset<{type.Name}> !File.Exists iID:{iID},aPath:{aPath}");
                        return default;
                    }
                    string aJson = File.ReadAllText(aPath);
                    JsonData aJsonData = JsonData.ParseJson(aJson);
                    UCLI_Asset asset = type.CreateInstance() as UCLI_Asset;
                    asset.DeserializeFromJson(aJsonData);
                    asset.ID = iID;

                    return asset;
                }
                public T GetAsset<T>(string iID) where T : class, UCLI_Asset, new()
                {
                    return GetAsset(typeof(T), iID) as T;
                }

                public void ClearCache()
                {
                    m_AssetsInfoCache = null;
                }

                public AssetsInfoCache GetAssetsInfoCache(bool iUseCache = true)
                {
                    if(m_AssetsInfoCache != null && m_AssetsInfoCache.OutDated)
                    {
                        m_AssetsInfoCache = null;//Refresh
                    }
                    if (!iUseCache || m_AssetsInfoCache == null)
                    {
                        m_AssetsInfoCache = new AssetsInfoCache();
                        m_AssetsInfoCache.Init(this);
                    }
                    return m_AssetsInfoCache;
                }
            }

            /// <summary>
            /// 紀錄模組內的所有資料
            /// </summary>
            public class AssetsInfoCache
            {
                public ModuleEntry p_ModuleEntry { get; private set; }
                public bool OutDated
                {
                    get
                    {
                        return (System.DateTime.Now - m_InitTime).TotalSeconds > 10;//Refresh every 10 seconds
                    }
                }

                public AssetsInfoCache()
                {

                }

                public void Init(ModuleEntry moduleEntry)
                {
                    m_InitTime = System.DateTime.Now;

                    p_ModuleEntry = moduleEntry;

                    var allAssetTypeInfos = UCLI_Asset.GetAllAssetTypesInfo();
                    foreach (var typeName in allAssetTypeInfos.Keys)
                    {
                        UCL_AssetTypeInfo info = allAssetTypeInfos[typeName];
                        var type = info.m_Type;
                        var assetIDs = moduleEntry.GetAllAssetsID(type);
                        if (!assetIDs.IsNullOrEmpty())
                        {
                            var data = new AssetInfo();
                            data.Init(moduleEntry, info, assetIDs);
                            m_AssetsInfo[info] = data;
                        }
                        //UCLI_Asset util = UCLI_Asset.GetUtilByType(info.m_Type);
                    }
                }


                public System.DateTime m_InitTime;
                /// <summary>
                /// 所有的資料
                /// </summary>
                public Dictionary<UCL_AssetTypeInfo, AssetInfo> m_AssetsInfo = new Dictionary<UCL_AssetTypeInfo, AssetInfo>();
            }

            public class AssetInfo
            {
                public ModuleEntry p_ModuleEntry { get; private set; }

                public AssetInfo() { }


                public UCL_AssetTypeInfo m_AssetTypeInfo;
                public List<string> m_IDs = null;

                public void Init(ModuleEntry moduleEntry, UCL_AssetTypeInfo assetTypeInfo, IList<string> ids)
                {
                    p_ModuleEntry = moduleEntry;

                    m_AssetTypeInfo = assetTypeInfo;
                    m_IDs = ids.ToList();
                }
                public UCLI_Asset GetAsset(string id)
                {
                    if(p_ModuleEntry == null) return null;
                    return p_ModuleEntry.GetAsset(m_AssetTypeInfo.m_Type, id);
                }
            }
            #endregion



        }
    }
}
