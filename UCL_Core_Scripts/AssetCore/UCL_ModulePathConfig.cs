
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
                            return UCL_AssetType.BuiltinModules;
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
                            return UCL_ModulePath.RelativePath.ModuleServicePath;//Builtin
                        }
                }
                return UCL_ModulePath.RelativePath.ModulesRootRelativePath;//Runtime
            }
        }
        #region RelativePath
        public string ModulesRelativePath => Path.Combine(RootFolder, UCL_ModulePath.RelativePath.ModulesFolderName);
        public string ConfigRelativePath => Path.Combine(RootFolder, UCL_ModulePath.ConfigFileName);
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

        public string GetModulePath(string iID)
        {
            if(iID == null)
            {
                Debug.LogError("GetModulePath iID == null");
                return string.Empty;
            }
            return Path.Combine(ModulesPath, iID);
        }
        public string GetModuleConfigPath(string iID)
        {
            return Path.Combine(ModulesPath, iID, UCL_ModulePath.ConfigFileName);
        }
        #endregion

        public void SaveConfig(JsonData iJson)
        {
            string aPath = UCL_ModulePath.PersistantPath.GetModulePathConfig(m_ModuleEditType).ConfigPath;
            Debug.LogError($"Save Config aPath:{aPath}");
            switch (m_ModuleEditType)
            {
                case UCL_ModuleEditType.Builtin:
                    {
                        if (Application.isEditor)//Only in Editor can save to StreamingAssets
                        {
                            UCL.Core.FileLib.Lib.WriteAllText(aPath, iJson.ToJsonBeautify());
                        }
                        break;
                    }
                case UCL_ModuleEditType.Runtime:
                    {
                        UCL.Core.FileLib.Lib.WriteAllText(aPath, iJson.ToJsonBeautify());
                        break;
                    }
            }

        }
        public async UniTask<JsonData> LoadConfig()
        {
            string aJson = string.Empty;
            string aPath = UCL_ModulePath.RelativePath.BuiltinConfigPath;
            try
            {
                aJson = await UCL_StreamingAssets.LoadString(aPath);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }

            if (string.IsNullOrEmpty(aJson))
            {
                Debug.LogError($"LoadConfig() string.IsNullOrEmpty(aJson),path:{aPath}");
                return null;
            }
            return JsonData.ParseJson(aJson);
        }



        #region ModulePath

//        public void SaveModuleConfig(string iID, JsonData iJson)
//        {
//            if(m_ModuleEditType == UCL_ModuleEditType.Builtin && !Application.isEditor)
//            {
//                return;
//            }
//            string aFolderPath = GetModulePath(iID);
//            //string aModuleRelativePath = UCL_ModulePath.RelativePath.GetBuiltinModulePath(iID);
//            if (!Directory.Exists(aFolderPath))
//            {
//                Directory.CreateDirectory(aFolderPath);
//            }
//            UCL.Core.FileLib.Lib.WriteAllText(GetModuleConfigPath(iID), iJson.ToJsonBeautify());
//            var aResFolder = UCL_ModulePath.GetModResourcesPath(aFolderPath);
//            if (!Directory.Exists(aResFolder))
//            {
//                Directory.CreateDirectory(aResFolder);
//                string aResReadMe = "Please put Mod resources in this folder";
//                File.WriteAllText(Path.Combine(aResFolder, "Readme.txt"), aResReadMe);
//            }
////#if UNITY_EDITOR
////            if (AssetType == UCL_AssetType.StreamingAssets)
////            {
////                string aPath = UCL_ModulePath.GetModuleFileInfoPath(aFolderPath);
////                UCL_StreamingAssetFileInspector aFileInfos = new ();
////                aFileInfos.m_TargetDirectory = aModuleRelativePath;
////                //Debug.LogError($"aModuleRelativePath:{aModuleRelativePath}");
////                aFileInfos.RefreshFileInfos();
////                File.WriteAllText(aPath, aFileInfos.SerializeToJson().ToJsonBeautify());
////            }
////#endif
//        }
        //public async UniTask<UCL_StreamingAssetFileInspector> GetModuleStreamingAssetFileInspector(string iID)
        //{
        //    string aFolderPath = UCL_ModulePath.RelativePath.GetBuiltinModulePath(iID);
        //    string aPath = UCL_ModulePath.GetModuleFileInfoPath(aFolderPath);
        //    UCL_StreamingAssetFileInspector aFileInfos = new();
        //    string aJson = string.Empty;
        //    try
        //    {
        //        aJson = await UCL_StreamingAssets.LoadString(aPath);
        //    }
        //    catch(System.Exception e)
        //    {
        //        Debug.LogException(e);
        //    }
        //    if (!string.IsNullOrEmpty(aJson))
        //    {
        //        aFileInfos.DeserializeFromJson(JsonData.ParseJson(aJson));
        //    }

        //    return aFileInfos;
        //}
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