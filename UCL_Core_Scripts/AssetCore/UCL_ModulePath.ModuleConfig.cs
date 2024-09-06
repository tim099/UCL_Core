
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
                public string ModResourcesPath => Path.Combine(RootFolder, ModResourcesFolderName);
                public string ZipConfigName => $"{ID}.json";
                public string ZipFileName => $"{ID}.zip";
                public string ZipFilePath => Path.Combine(PersistantPath.ModulesZipFolder, ZipFileName);

                /// <summary>
                /// return true if installed
                /// </summary>
                /// <returns></returns>
                public bool Installed => Directory.Exists(InstallFolder);

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


                public void ZipModule(string iTargetPath = "", bool iExportConfig = true)
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

                        async UniTask InstallModule()
                        {
                            try
                            {
                                byte[] aBytes = await UCL_StreamingAssets.FullPath.LoadBytes(ZipFilePath);
                                if (aBytes == null)
                                {
                                    Debug.LogError($"ModuleConfig.Install ID:{ID},aTargetPath:{aTargetPath},ZipFilePath:{ZipFilePath}");
                                    return;
                                }

                                //await UCL.Core.Page.UCL_OptionPage.ShowAlertAsync($"ModuleConfig UCL_ZipFile.ExtractToDirectory aBytes.Length:{aBytes.Length}", $"aTargetPath:{aTargetPath}");

                                //var aStream = await UCL_StreamingAssets.FullPath.LoadNativeData(ZipFilePath);
                                UCL.Core.FileLib.ZipLib.UnzipFromBytes(aBytes, aTargetPath);
                            }
                            catch (System.Exception ex)
                            {
                                //await UCL.Core.Page.UCL_OptionPage.ShowAlertAsync($"ModuleConfig UCL_ZipFile.ExtractToDirectory ", $"Exception:{ex}");
                                Debug.LogException(ex);
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
                                        }
                                        break;
                                    }
                                case UCL_ModuleService.EditorInstallMode.UnZip:
                                    {
                                        var aZipFilePath = ZipFilePath;
                                        if (File.Exists(aZipFilePath))//if in Editor and Zip file not exist
                                        {
                                            File.Delete(aZipFilePath);
                                        }
                                        ZipModule();
                                        await InstallModule();
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



                        await InstallModule();


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
                    }catch (Exception ex)
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
                public T GetAsset<T>(string iID) where T : UCLI_Asset, new()
                {
                    string aPath = GetAssetPath(typeof(T), iID);
                    if (!File.Exists(aPath))
                    {
                        Debug.LogError($"{GetType().Name}.GetAsset<{typeof(T).Name}> !File.Exists iID:{iID},aPath:{aPath}");
                        return default;
                    }
                    string aJson = File.ReadAllText(aPath);
                    JsonData aJsonData = JsonData.ParseJson(aJson);
                    T asset = new T();
                    asset.DeserializeFromJson(aJsonData);
                    asset.ID = iID;

                    return asset;
                }
            }
            #endregion
        }
    }
}

