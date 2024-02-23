
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/20 2024 22:46
using Cysharp.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core
{
    public enum UCL_AssetType
    {
        StreamingAssets = 0,
        PersistentDatas,

        Addressables,
        /// <summary>
        /// Path xxx\SteamLibrary\steamapps\workshop\content\xxxx
        /// Example D:\SteamLibrary\steamapps\workshop\content\1158310\2973143830
        /// </summary>
        SteamMods,
    }

    /// <summary>
    /// UCL_Module contains info about how to load assets in this module
    /// </summary>
    public class UCL_Module : UCL.Core.JsonLib.UnityJsonSerializable, UCLI_ID
    {
        public const string FileInfoID = "FileInfos.json";
        public const string NotInstalledID = "None";
        public class Config : UCL.Core.JsonLib.UnityJsonSerializable
        {
            public string m_Version = "1.0.0";
            public string m_ID;
        }
        /// <summary>
        /// StreamingAssets for BuiltinModules
        /// and PersistentDatas for Runtime
        /// </summary>
        public UCL_AssetType AssetType { get ; set ; }

        public Config m_Config = new Config();

        protected bool m_IsLoading = false;
        protected bool m_Installing = false;
        protected UCL_StreamingAssetFileInspector m_FileInfo = new UCL_StreamingAssetFileInspector();
        #region Interface
        /// <summary>
        /// Unique ID of this Module
        /// </summary>
        public string ID { get => m_Config.m_ID; set => m_Config.m_ID = value; }
        #endregion
        public bool IsLoading => m_IsLoading;


        public string RelativeModulePath => UCL_ModuleService.PathConfig.GetModulesRelativePath(ID);
        protected string RelativeFileInfosPath => Path.Combine(RelativeModulePath, FileInfoID);

        public string ModulePath => UCL_ModuleService.PathConfig.GetModulesPath(ID);

        protected string FileInfosPath => Path.Combine(ModulePath, FileInfoID);
        public void Init(string iID, UCL_AssetType iAssetType)
        {
            ID = iID;
            AssetType = iAssetType;
        }
        protected string GetConfigPath(string iFolderPath) => Path.Combine(iFolderPath, "Config.json");
        protected string GetResourcePath(string iFolderPath) => Path.Combine(iFolderPath, "ModResources");
        /// <summary>
        /// Save Module Config
        /// </summary>
        public void Save()
        {
            //Debug.LogError($"aFolderPath:{aFolderPath}");
            string aPath = UCL_ModulePath.GetBuiltinModulePath(ID);
            if(!Directory.Exists(aPath))
            {
                Directory.CreateDirectory(aPath);
            }
            var aConfigPath = GetConfigPath(aPath);
            File.WriteAllText(aConfigPath, m_Config.SerializeToJson().ToJsonBeautify());//SaveConfig
            if (Application.isEditor)
            {
                var aResFolder = GetResourcePath(aPath);
                if (!Directory.Exists(aResFolder))
                {
                    Directory.CreateDirectory(aResFolder);
                    string aResReadMe = "Please put Mod resources in this folder";
                    File.WriteAllText(Path.Combine(aResFolder, "Readme.txt"), aResReadMe);
                }
                m_FileInfo.m_TargetDirectory = RelativeModulePath;
                m_FileInfo.RefreshFileInfos();
                File.WriteAllText(FileInfosPath, m_FileInfo.SerializeToJson().ToJsonBeautify());
            }
        }
        public void Load(UCL_AssetType iAssetType)
        {
            if (m_IsLoading)
            {
                return;
            }
            AssetType = iAssetType;
            LoadAsync().Forget();
        }
        public string GetFolderPath(string iRelativeFolderPath)
        {
            string aPath = Path.Combine(ModulePath, iRelativeFolderPath);
            //Debug.LogError($"GetFolderPath SavePath:{SavePath}");
            //Debug.LogError($"GetFolderPath aPath:{aPath}");
            if (!Directory.Exists(aPath))
            {
                Directory.CreateDirectory(aPath);
            }
            return aPath;
        }
        protected Config LoadInstalledConfig()
        {
            Config aConfig = new Config();
            var aInstallPath = UCL_ModulePath.GetModulePath(ID);
            string aConfigPath = GetConfigPath(aInstallPath);
            if (File.Exists(aConfigPath))//Get config
            {
                string aJson = File.ReadAllText(aConfigPath);
                aConfig.DeserializeFromJson(JsonData.ParseJson(aJson));
            }
            else
            {
                aConfig.m_Version = NotInstalledID;//Not Installed!!
            }
            return aConfig;
        }
        protected void SaveInstalledConfig(Config iConfig)
        {
            var aInstallPath = UCL_ModulePath.GetModulePath(ID);
            if (!Directory.Exists(aInstallPath))//Not installed yet
            {
                return;
            }
            string aConfigPath = GetConfigPath(aInstallPath);
            File.WriteAllText(aConfigPath, iConfig.SerializeToJson().ToJsonBeautify());
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

            var aInstallPath = UCL_ModulePath.GetModulePath(ID);
            bool aNeedInstall = true;
            if(Directory.Exists(aInstallPath))//Installed, check version
            {
                Config aConfig = LoadInstalledConfig();
                if (aConfig.m_Version == m_Config.m_Version)//Same Version!!
                {
                    aNeedInstall = false;
                }
                Debug.LogError($"Version:{m_Config.m_Version},aConfig.m_Version:{aConfig.m_Version},aNeedInstall:{aNeedInstall}");
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
            if(m_Installing)
            {
                return;
            }
            if (m_IsLoading)
            {
                await UniTask.WaitUntil(() => !m_IsLoading);
            }
            m_Installing = true;
            try
            {
                //Debug.LogError($"Install m_AssetType:{m_AssetType},Application.platform:{Application.platform}");
                switch (AssetType)//Only install StreamingAssets(Builtin)
                {
                    case UCL_AssetType.StreamingAssets:
                        {
                            switch (Application.platform)
                            {
                                case RuntimePlatform.WindowsEditor:
                                case RuntimePlatform.IPhonePlayer:
                                case RuntimePlatform.WindowsPlayer:
                                    {
                                        //System.IO.Compression.ZipFile.ExtractToDirectory
                                        var aPath = UCL_ModulePath.GetBuiltinModulePath(ID);//Get the mod folder path
                                        var aInstallPath = UCL_ModulePath.GetModulePath(ID);
                                        //Debug.LogError($"BuiltinPath:{aPath}");
                                        //Debug.LogError($"InstallPath:{aInstallPath}");
                                        if (Directory.Exists(aPath))
                                        {
                                            UCL.Core.FileLib.Lib.CopyDirectory(aPath, aInstallPath);
                                        }
                                        break;
                                    }
                            }


                            //TODO StreamingAssets on Android can't load by File System
                            break;
                        }
                }
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
        protected async UniTask LoadAsync()
        {
            m_IsLoading = true;
            try
            {

                //if (Application.isEditor)
                //{
                //    string aPath = UCL_ModulePath.GetBuiltinModulePath(ID);
                //    var aConfigPath = GetConfigPath(aPath);
                //    string aJson = File.ReadAllText(aConfigPath);
                //    m_Config.DeserializeFromJson(JsonData.ParseJson(aJson));
                //}
                //else
                {
                    switch (AssetType)
                    {
                        case UCL_AssetType.StreamingAssets:
                            {
                                string aJson = await UCL_StreamingAssets.LoadString(GetConfigPath(UCL_ModulePath.GetBuiltinModuleRelativePath(ID)));
                                m_Config.DeserializeFromJson(JsonData.ParseJson(aJson));
                                break;
                            }
                        case UCL_AssetType.PersistentDatas:
                            {
                                string aPath = UCL_ModulePath.GetBuiltinModulePath(ID);
                                if (Directory.Exists(aPath))
                                {
                                    var aConfigPath = GetConfigPath(aPath);
                                    string aJson = File.ReadAllText(aConfigPath);
                                    m_Config.DeserializeFromJson(JsonData.ParseJson(aJson));
                                }
                                break;
                            }
                    }
                }
            }
            catch(System.Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                m_IsLoading = false;
            }

        }
    }
}