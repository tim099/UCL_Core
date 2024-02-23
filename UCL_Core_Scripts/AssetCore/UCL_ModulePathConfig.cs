
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
        #region RelativePath
        public static string BuiltinModulesRelativePath => Path.Combine(BuiltinRootRelativePath, "BuiltinModules");
        public static string ModulesRelativePath => Path.Combine(ModulesRootRelativePath, "Modules");

        public static string GetBuiltinModuleRelativePath(string iID) => Path.Combine(BuiltinModulesRelativePath, iID);

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
        public string ModulesRelativePath => Path.Combine(RootFolder, "Modules");
        public string ConfigRelativePath => Path.Combine(RootFolder, "Config.json");
        public string GetModulesRelativePath(string iID)
        {
            return Path.Combine(ModulesRelativePath, iID);
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
                            aJson = await UCL_StreamingAssets.LoadString(ConfigRelativePath);
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogException(e);
                        }
                        break;
                    }
                case UCL_AssetType.PersistentDatas:
                    {
                        aJson = File.ReadAllText(ConfigPath);
                        break;
                    }
            }

            if (string.IsNullOrEmpty(aJson))
            {
                return null;
            }
            return JsonData.ParseJson(aJson);
        }
    }
}