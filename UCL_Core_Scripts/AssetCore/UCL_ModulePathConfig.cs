
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/23 2024 14:46
using Cysharp.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UCL.Core.JsonLib;
using UnityEngine;

namespace UCL.Core
{
    public static class UCL_ModulePath
    {
        public const string BuiltinRootRelativePath = ".ModuleService";
        public const string ModulesRootRelativePath = "ModulesRoot";
        public const string ModulesFolderName = "Modules";
        public const string ConfigFileName = "Config.json";
        public const string FileInfosFileName = "FileInfos.json";
        #region RelativePath
        public static string BuiltinConfigRelativePath => Path.Combine(BuiltinRootRelativePath, ConfigFileName);
        public static string BuiltinModulesRelativePath => Path.Combine(BuiltinRootRelativePath, ModulesFolderName);
        public static string ModulesRelativePath => Path.Combine(ModulesRootRelativePath, ModulesFolderName);
        /// <summary>
        /// .ModuleService\Modules\{iID}
        /// </summary>
        /// <param name="iID"></param>
        /// <returns></returns>
        public static string GetBuiltinModuleRelativePath(string iID) => Path.Combine(BuiltinModulesRelativePath, iID);
        /// <summary>
        /// ModulesRoot\Modules\{iID}
        /// </summary>
        /// <param name="iID"></param>
        /// <returns></returns>
        public static string GetModuleRelativePath(string iID) => Path.Combine(ModulesRelativePath, iID);
        #endregion

        #region Builtin
        public static string BuiltinModulesPath => Path.Combine(UCL_AssetPath.GetPath(UCL_AssetType.StreamingAssets), BuiltinModulesRelativePath);

        public static string GetBuiltinModulePath(string iID)
        {
            
            return Path.Combine(UCL_AssetPath.GetPath(UCL_AssetType.StreamingAssets), GetBuiltinModuleRelativePath(iID));
        }
        #endregion

        #region Builtin
        public static string ModulesPath => Path.Combine(UCL_AssetPath.GetPath(UCL_AssetType.PersistentDatas), ModulesRelativePath);

        public static string GetModulePath(string iID)
        {
            string aFolder = UCL_AssetPath.GetPath(UCL_AssetType.PersistentDatas);
            string aModuleRelativePath = GetModuleRelativePath(iID);
            //Debug.LogError($"aFolder:{aFolder},aModuleRelativePath:{aModuleRelativePath}");
            return Path.Combine(aFolder, aModuleRelativePath);
        }
        #endregion
    }

    public class UCL_ModulePathConfig
    {
        public UCL_ModuleEditType m_ModuleEditType = UCL_ModuleEditType.Builtin;
        public UCL_AssetType AssetType
        {
            get
            {
                switch (m_ModuleEditType)
                {
                    case UCL_ModuleEditType.Builtin:
                        {
                            return UCL_AssetType.StreamingAssets;
                        }
                }
                return UCL_AssetType.PersistentDatas;//Runtime
            }
        }
        public string RootFolder
        {
            get
            {
                switch (AssetType)
                {
                    case UCL_AssetType.StreamingAssets:
                        {
                            return UCL_ModulePath.BuiltinRootRelativePath;//Builtin
                        }
                }
                return UCL_ModulePath.ModulesRootRelativePath;//Runtime
            }
        }
        #region RelativePath
        public string ModulesRelativePath => Path.Combine(RootFolder, UCL_ModulePath.ModulesFolderName);
        public string ConfigRelativePath => Path.Combine(RootFolder, UCL_ModulePath.ConfigFileName);
        public string GetModuleRelativePath(string iID)
        {
            return Path.Combine(ModulesRelativePath, iID);
        }
        public string GetModuleConfigRelativePath(string iID)
        {
            return Path.Combine(ModulesRelativePath, iID, UCL_ModulePath.ConfigFileName);
        }
        #endregion

        #region FileSystemPath
        public string FileSystemRootPath => UCL_AssetPath.GetPath(AssetType);
        public string RootPath => Path.Combine(FileSystemRootPath, RootFolder);
        public string ModulesPath => Path.Combine(FileSystemRootPath, ModulesRelativePath);
        public string ConfigPath => Path.Combine(FileSystemRootPath, ConfigRelativePath);

        public string GetModulesPath(string iID)
        {
            return Path.Combine(ModulesPath, iID);
        }
        public string GetModuleConfigPath(string iID)
        {
            return Path.Combine(ModulesPath, iID, UCL_ModulePath.ConfigFileName);
        }
        #endregion

        public void SaveConfig(JsonData iJson)
        {
            switch (AssetType)
            {
                case UCL_AssetType.StreamingAssets:
                    {
                        if (Application.isEditor)//Only in Editor can save to StreamingAssets
                        {
                            UCL.Core.FileLib.Lib.WriteAllText(ConfigPath, iJson.ToJsonBeautify());
                        }
                        break;
                    }
                case UCL_AssetType.PersistentDatas:
                    {
                        UCL.Core.FileLib.Lib.WriteAllText(ConfigPath, iJson.ToJsonBeautify());
                        break;
                    }
            }

        }
        public async UniTask<JsonData> LoadConfig()
        {
            string aJson = string.Empty;
            switch (AssetType)
            {
                case UCL_AssetType.StreamingAssets:
                    {
                        try
                        {
                            aJson = await UCL_StreamingAssets.LoadString(UCL_ModulePath.BuiltinConfigRelativePath);
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogException(e);
                        }
                        break;
                    }
                case UCL_AssetType.PersistentDatas:
                    {
                        string aPath = ConfigPath;
                        try
                        {
                            if (File.Exists(aPath))
                            {
                                aJson = File.ReadAllText(ConfigPath);
                            }
                            else
                            {
                                //Try to load from StreamingAssets(Builtin)
                                aJson = await UCL_StreamingAssets.LoadString(UCL_ModulePath.BuiltinConfigRelativePath);
                            }
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogException(e);
                        }


                        break;
                    }
            }

            if (string.IsNullOrEmpty(aJson))
            {
                return null;
            }
            return JsonData.ParseJson(aJson);
        }




        #region ModulePath
        protected string GetModuleResourcePath(string iFolderPath) => Path.Combine(iFolderPath, "ModResources");
        protected string GetModuleFileInfoPath(string iFolderPath) => Path.Combine(iFolderPath, UCL_ModulePath.FileInfosFileName);
        public void SaveModuleConfig(string iID, JsonData iJson)
        {
            if(AssetType == UCL_AssetType.StreamingAssets && !Application.isEditor)
            {
                return;
            }
            string aFolderPath = GetModulesPath(iID);
            if(!Directory.Exists(aFolderPath))
            {
                Directory.CreateDirectory(aFolderPath);
            }
            UCL.Core.FileLib.Lib.WriteAllText(GetModuleConfigPath(iID), iJson.ToJsonBeautify());
            var aResFolder = GetModuleResourcePath(aFolderPath);
            if (!Directory.Exists(aResFolder))
            {
                Directory.CreateDirectory(aResFolder);
                string aResReadMe = "Please put Mod resources in this folder";
                File.WriteAllText(Path.Combine(aResFolder, "Readme.txt"), aResReadMe);
            }

            if (AssetType == UCL_AssetType.StreamingAssets && Application.isEditor)
            {
                UCL_StreamingAssetFileInspector m_FileInfo = new ();
                m_FileInfo.m_TargetDirectory = aFolderPath;
                m_FileInfo.RefreshFileInfos();
                File.WriteAllText(GetModuleFileInfoPath(aFolderPath), m_FileInfo.SerializeToJson().ToJsonBeautify());
            }
        }
        public async UniTask<JsonData> LoadModuleConfig(string iID)
        {
            string aJson = string.Empty;
            switch (AssetType)
            {
                case UCL_AssetType.StreamingAssets:
                    {
                        try
                        {
                            aJson = await UCL_StreamingAssets.LoadString(GetModuleConfigRelativePath(iID));
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogException(e);
                        }
                        break;
                    }
                case UCL_AssetType.PersistentDatas:
                    {
                        string aPath = GetModuleConfigPath(iID);
                        if(File.Exists(aPath))
                        {
                            aJson = File.ReadAllText(aPath);
                        }
                        break;
                    }
            }

            if (string.IsNullOrEmpty(aJson))
            {
                return null;
            }
            return JsonData.ParseJson(aJson);
        }
        #endregion
    }
}