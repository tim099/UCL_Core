
// ATS_AutoHeader
// to change the auto header please go to ATS_AutoHeader.cs
// Create time : 02/20 2024 22:46
using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UCL.Core.JsonLib;
using UCL.Core.UI;
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
    public class UCL_Module : UCL.Core.JsonLib.UnityJsonSerializable, UCLI_ID, UCLI_ShortName
    {
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
        //protected UCL_StreamingAssetFileInspector m_FileInfo = new UCL_StreamingAssetFileInspector();
        #region Interface
        /// <summary>
        /// Unique ID of this Module
        /// </summary>
        public string ID { get => m_Config.m_ID; set => m_Config.m_ID = value; }
        public string GetShortName() => $"UCL_Module({ID})";
        #endregion
        public bool IsLoading => m_IsLoading;


        public string RelativeModulePath => UCL_ModuleService.PathConfig.GetModuleRelativePath(ID);

        public string ModulePath => UCL_ModuleService.PathConfig.GetModulePath(ID);

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
            UCL_ModuleService.PathConfig.SaveModuleConfig(ID, m_Config.SerializeToJson());


            //string aPath = UCL_ModulePath.GetBuiltinModulePath(ID);
            //if(!Directory.Exists(aPath))
            //{
            //    Directory.CreateDirectory(aPath);
            //}
            //var aConfigPath = GetConfigPath(aPath);
            //File.WriteAllText(aConfigPath, m_Config.SerializeToJson().ToJsonBeautify());//SaveConfig

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
            //Debug.LogError($"LoadInstalledConfig aConfigPath:{aConfigPath}");
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
                    case UCL_AssetType.StreamingAssets://(Builtin)
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
                                            if (Directory.Exists(aInstallPath))
                                            {
                                                Directory.Delete(aInstallPath, true);
                                            }
                                            UCL.Core.FileLib.Lib.CopyDirectory(aPath, aInstallPath);
                                        }
                                        else
                                        {
                                            Debug.LogError($"Install Fail aPath:{aPath}");
                                        }
                                        break;
                                    }
                                case RuntimePlatform.Android:
                                    {//Load from streaming asset!!
                                        UCL_StreamingAssetFileInspector aFileInfos = await UCL_ModuleService.PathConfig.GetModuleStreamingAssetFileInspector(ID);
                                        var aRelativePath = UCL_ModulePath.GetBuiltinModuleRelativePath(ID);
                                        var aInstallPath = UCL_ModulePath.GetModulePath(ID);

                                        if (Directory.Exists(aInstallPath))
                                        {
                                            Directory.Delete(aInstallPath, true);
                                        }
                                        Directory.CreateDirectory(aInstallPath);
                                        
                                        await InstallFolder(aFileInfos, aFileInfos.m_FolderInformations, aRelativePath, aInstallPath);

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
        protected async UniTask InstallFolder(UCL_StreamingAssetFileInspector iFileInfos, UCL_StreamingAssetFileInspector.FolderInformation iFolderInfos,
            string iRelativePath, string iInstallPath)
        {
            //Debug.LogError($"InstallFolder iInstallPath:{iInstallPath}");
            if (!Directory.Exists(iInstallPath))
            {
                Directory.CreateDirectory(iInstallPath);
            }
            foreach(var aFile in iFolderInfos.m_FileInfos)//Install Files
            {
                try
                {
                    string aName = aFile.FileName;
                    string aRelativePath = Path.Combine(iRelativePath, aName);
                    string aInstallPath = Path.Combine(iInstallPath, aName);
                    byte[] aBytes = await UCL_StreamingAssets.LoadBytes(aRelativePath);
                    if (aBytes != null)
                    {
                        File.WriteAllBytes(aInstallPath, aBytes);
                    }
                    else
                    {
                        Debug.LogError($"InstallFolder aFile:{aName},iRelativePath:{iRelativePath},aBytes == null");
                    }
                }
                catch(System.Exception e)
                {
                    Debug.LogException(e);
                }

            }
            foreach(var aFolder in iFolderInfos.m_FolderInfos)
            {
                try
                {
                    await InstallFolder(iFileInfos, aFolder, Path.Combine(iRelativePath, aFolder.Name), Path.Combine(iInstallPath, aFolder.Name));
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }
        protected async UniTask LoadAsync()
        {
            m_IsLoading = true;
            try
            {

                var aJson = await UCL_ModuleService.PathConfig.LoadModuleConfig(ID);
                if(aJson != null)
                {
                    m_Config.DeserializeFromJson(aJson);
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
        virtual public void OnGUI(UCL_ObjectDictionary iDataDic)
        {
            var aLabelStyle = UCL_GUIStyle.GetLabelStyle(Color.white, 18);
            var aButtonStyle = UCL_GUIStyle.GetButtonStyle(Color.white, 18);
            foreach (var aType in UCLI_Asset.GetAllAssetTypes())
            {
                try
                {
                    string aPropInfosStr = string.Empty;
                    try
                    {
                        UCLI_Asset aUtil = UCLI_Asset.GetUtilByType(aType);//Get Util
                        if (aUtil != null)
                        {
                            GUILayout.BeginHorizontal();
                            GUILayout.Label(aType.Name, aLabelStyle, GUILayout.ExpandWidth(false));
                            if (GUILayout.Button($"Edit", aButtonStyle, GUILayout.Width(100)))
                            {
                                aUtil.CreateSelectPage();
                            }
                            //GUILayout.Label($"{aType.FullName}");
                            //aUtil.RefreshAllDatas();
                            //Debug.LogWarning($"Util:{aUtil.GetType().FullName}.RefreshAllDatas()");
                            GUILayout.EndHorizontal();
                        }
                    }
                    catch (Exception iE)
                    {
                        Debug.LogError($"RCGI_CommonData aType:{aType.FullName},Exception:{iE}");
                        Debug.LogException(iE);
                    }
                }
                catch (Exception iE)
                {
                    Debug.LogException(iE);
                }

            }
        }
    }
}