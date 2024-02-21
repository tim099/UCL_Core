
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
        /// <summary>
        /// StreamingAssets for BuiltinModules
        /// and PersistentDatas for Runtime
        /// </summary>
        public UCL_AssetType m_AssetType = UCL_AssetType.StreamingAssets;

        [SerializeField] protected string m_ID;


        protected bool m_IsLoading = false;
        protected UCL_StreamingAssetFileInspector m_FileInfo = new UCL_StreamingAssetFileInspector();
        #region Interface
        /// <summary>
        /// Unique ID of this Module
        /// </summary>
        public string ID { get => m_ID; set => m_ID = value; }
        #endregion
        public bool IsLoading => m_IsLoading;


        public string RelativeSavePath => UCL_ModulePath.GetBuiltinModuleRelativePath(ID);
        protected string RelativeFileInfosPath => Path.Combine(SavePath, FileInfoID);

        public string SavePath => UCL_ModulePath.GetBuiltinModulePath(ID);
        protected string FileInfosPath => Path.Combine(SavePath, FileInfoID);
        public void Init(string iID)
        {
            m_ID = iID;
            if (Application.isEditor)//Create BuiltinModule in streamming assets
            {
                m_AssetType = UCL_AssetType.StreamingAssets;
            }
            else//Create Module in PersistentDatas
            {
                m_AssetType = UCL_AssetType.PersistentDatas;
            }
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
            File.WriteAllText(aConfigPath, SerializeToJson().ToJsonBeautify());//SaveConfig
            if (Application.isEditor)
            {
                var aResFolder = GetResourcePath(aPath);
                if (!Directory.Exists(aResFolder))
                {
                    Directory.CreateDirectory(aResFolder);
                    string aResReadMe = "Please put Mod resources in this folder";
                    File.WriteAllText(Path.Combine(aResFolder, "Readme.txt"), aResReadMe);
                }
                m_FileInfo.m_TargetDirectory = RelativeSavePath;
                m_FileInfo.RefreshFileInfos();
                File.WriteAllText(FileInfosPath, m_FileInfo.SerializeToJson().ToJsonBeautify());
            }
        }
        public void Load()
        {
            if (m_IsLoading)
            {
                return;
            }
            LoadAsync().Forget();
        }
        /// <summary>
        /// Install to Application.persistentDataPath
        /// </summary>
        public void Install()
        {
            //Debug.LogError($"Install m_AssetType:{m_AssetType},Application.platform:{Application.platform}");
            switch (m_AssetType)//Only install StreamingAssets
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
        protected async UniTask LoadAsync()
        {
            m_IsLoading = true;
            try
            {
                string aPath = UCL_ModulePath.GetBuiltinModulePath(ID);
                if (Application.isEditor)
                {
                    var aConfigPath = GetConfigPath(aPath);
                    string aJson = File.ReadAllText(aConfigPath);
                    DeserializeFromJson(JsonData.ParseJson(aJson));

                    //m_FileInfo.m_TargetDirectory = aPath;
                    //m_FileInfo.RefreshFileInfos();
                    //File.WriteAllText(FileInfosPath, m_FileInfo.SerializeToJson().ToJsonBeautify());
                }
                else
                {
                    switch (m_AssetType)
                    {
                        case UCL_AssetType.StreamingAssets:
                            {
                                break;
                            }
                        case UCL_AssetType.PersistentDatas:
                            {
                                if (Directory.Exists(aPath))
                                {
                                    var aConfigPath = GetConfigPath(aPath);
                                    string aJson = File.ReadAllText(aConfigPath);
                                    JsonData aJsonData = JsonData.ParseJson(aJson);
                                    DeserializeFromJson(aJsonData);
                                }
                                break;
                            }
                    }
                }
            }
            catch(System.Exception ex)
            {

            }
            finally
            {
                m_IsLoading = false;
            }

        }
        public override JsonData SerializeToJson()
        {
            return base.SerializeToJson();
        }
    }
}