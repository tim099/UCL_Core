
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
            public string m_Version = "1.0.0";
            public string m_ID;

            public List<UCL_ModuleEntry> m_DependenciesModules = new ();

            /// <summary>
            /// return true if Installed
            /// </summary>
            public bool Installed => m_Version != NotInstalledID;
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
                if (aBuiltinConfig.m_Version == m_Config.m_Version)//Same Version!!
                {
                    aNeedInstall = false;
                }
                Debug.LogWarning($"ID:{ID},Cur Version:{m_Config.m_Version},Builtin Version:{aBuiltinConfig.m_Version},aNeedInstall:{aNeedInstall}");
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


        //protected async UniTask InstallFolder(UCL_StreamingAssetFileInspector iFileInfos, UCL_StreamingAssetFileInspector.FolderInformation iFolderInfos,
        //    string iRelativePath, string iInstallPath)
        //{
        //    //Debug.LogError($"InstallFolder iInstallPath:{iInstallPath}");
        //    if (!Directory.Exists(iInstallPath))
        //    {
        //        Directory.CreateDirectory(iInstallPath);
        //    }
        //    foreach(var aFile in iFolderInfos.m_FileInfos)//Install Files
        //    {
        //        try
        //        {
        //            string aName = aFile.FileName;
        //            string aRelativePath = Path.Combine(iRelativePath, aName);
        //            string aInstallPath = Path.Combine(iInstallPath, aName);
        //            byte[] aBytes = await UCL_StreamingAssets.LoadBytes(aRelativePath);
        //            if (aBytes != null)
        //            {
        //                File.WriteAllBytes(aInstallPath, aBytes);
        //            }
        //            else
        //            {
        //                Debug.LogError($"InstallFolder aFile:{aName},iRelativePath:{iRelativePath},aBytes == null");
        //            }
        //        }
        //        catch(System.Exception e)
        //        {
        //            Debug.LogException(e);
        //        }

        //    }
        //    foreach(var aFolder in iFolderInfos.m_FolderInfos)
        //    {
        //        try
        //        {
        //            await InstallFolder(iFileInfos, aFolder, Path.Combine(iRelativePath, aFolder.Name), Path.Combine(iInstallPath, aFolder.Name));
        //        }
        //        catch (System.Exception e)
        //        {
        //            Debug.LogException(e);
        //        }
        //    }
        //}

        virtual public void OnGUI(UCL_ObjectDictionary iDataDic)
        {
            //var aLabelStyle = UCL_GUIStyle.GetLabelStyle(Color.white, 18);
            //var aButtonStyle = UCL_GUIStyle.GetButtonStyle(Color.white, 18);



            var aAllAssetTypeNames = UCLI_Asset.GetAllAssetTypeNames();
            if (!aAllAssetTypeNames.IsNullOrEmpty())
            {
                using (var aScope = new GUILayout.HorizontalScope())
                {
                    bool aEdit = false;
                    if (GUILayout.Button($"Edit", UCL_GUIStyle.ButtonStyle, GUILayout.Width(160)))
                    {
                        aEdit = true;
                    }


                    const string SelectedIDKey = "SelectedID";
                    int aSelectedID = iDataDic.GetData(SelectedIDKey, 0);
                    aSelectedID = UCL_GUILayout.PopupAuto(aSelectedID, aAllAssetTypeNames, iDataDic, "SelectAssetType");
                    iDataDic.SetData(SelectedIDKey, aSelectedID);


                    if (aEdit)
                    {
                        var aAllAssetsTypes = UCLI_Asset.GetAllAssetTypes();
                        var aType = aAllAssetsTypes[aSelectedID];
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
        }
        //virtual public object OnGUI(string iFieldName, UCL_ObjectDictionary iDataDic)
        //{
        //    UCL_GUILayout.DrawField(this, iDataDic.GetSubDic("Data"), iFieldName);

        //    //var aLabelStyle = UCL_GUIStyle.GetLabelStyle(Color.white, 18);
        //    //var aButtonStyle = UCL_GUIStyle.GetButtonStyle(Color.white, 18);



        //    var aAllAssetTypeNames = UCLI_Asset.GetAllAssetTypeNames();
        //    if (!aAllAssetTypeNames.IsNullOrEmpty())
        //    {
        //        using(var aScope = new GUILayout.HorizontalScope())
        //        {
        //            bool aEdit = false;
        //            if (GUILayout.Button($"Edit", UCL_GUIStyle.ButtonStyle, GUILayout.Width(160)))
        //            {
        //                aEdit = true;
        //            }


        //            const string SelectedIDKey = "SelectedID";
        //            int aSelectedID = iDataDic.GetData(SelectedIDKey, 0);
        //            aSelectedID = UCL_GUILayout.PopupAuto(aSelectedID, aAllAssetTypeNames, iDataDic, "SelectAssetType");
        //            iDataDic.SetData(SelectedIDKey, aSelectedID);


        //            if(aEdit)
        //            {
        //                var aAllAssetsTypes = UCLI_Asset.GetAllAssetTypes();
        //                var aType = aAllAssetsTypes[aSelectedID];
        //                try
        //                {
        //                    UCLI_Asset aUtil = UCLI_Asset.GetUtilByType(aType);//Get Util
        //                    if (aUtil != null)
        //                    {
        //                        aUtil.CreateSelectPage();
        //                    }
        //                }
        //                catch (Exception iE)
        //                {
        //                    Debug.LogError($"RCGI_CommonData aType:{aType.FullName},Exception:{iE}");
        //                    Debug.LogException(iE);
        //                }
        //            }

        //        }
        //    }

        //    return this;

        //    //foreach (var aType in aAllAssetsTypes)
        //    //{
        //    //    try
        //    //    {
        //    //        string aPropInfosStr = string.Empty;
        //    //        try
        //    //        {
        //    //            UCLI_Asset aUtil = UCLI_Asset.GetUtilByType(aType);//Get Util
        //    //            if (aUtil != null)
        //    //            {
        //    //                GUILayout.BeginHorizontal();
        //    //                if (GUILayout.Button($"Edit", aButtonStyle, GUILayout.ExpandWidth(false)))
        //    //                {
        //    //                    aUtil.CreateSelectPage();
        //    //                }
        //    //                GUILayout.Label(aType.Name, aLabelStyle, GUILayout.ExpandWidth(false));

        //    //                //GUILayout.Label($"{aType.FullName}");
        //    //                //aUtil.RefreshAllDatas();
        //    //                //Debug.LogWarning($"Util:{aUtil.GetType().FullName}.RefreshAllDatas()");
        //    //                GUILayout.EndHorizontal();
        //    //            }
        //    //        }
        //    //        catch (Exception iE)
        //    //        {
        //    //            Debug.LogError($"RCGI_CommonData aType:{aType.FullName},Exception:{iE}");
        //    //            Debug.LogException(iE);
        //    //        }
        //    //    }
        //    //    catch (Exception iE)
        //    //    {
        //    //        Debug.LogException(iE);
        //    //    }

        //    //}
        //}
    }
}