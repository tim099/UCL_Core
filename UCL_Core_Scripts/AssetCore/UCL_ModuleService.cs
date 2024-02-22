using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{
    public static class UCL_ModulePath
    {
        public const string RootRelativePath = ".ModuleService";
        public const string ModulesRootRelativePath = "ModulesRoot";
        #region RelativePath
        public static string BuiltinModulesRelativePath => Path.Combine(RootRelativePath, "BuiltinModules");
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
    /// <summary>
    /// Responsible for all operations related to Module
    /// </summary>
    public class UCL_ModuleService//: UCLI_FieldOnGUI
    {
        public const string CoreModuleID = "Core";
        public class ModuleEntry
        {
            public string m_ID;
            /// <summary>
            /// relative path in file system
            /// </summary>
            public string m_Path;

        }
        public class Config : UCL.Core.JsonLib.UnityJsonSerializable
        {
            public string m_CurrentEditModule = string.Empty;

            /// <summary>
            /// All BuiltinModules are in StreammingAssets
            /// </summary>
            public List<string> m_BuiltinModules = new List<string>();


            public string m_Version = "1.0.0";


            virtual public string SaveRelativePath => UCL_ModulePath.BuiltinModulesRelativePath;
            virtual public string GetBuiltinModulesPath(UCL_AssetType iAssetType)
            {
                var aPath = UCL_AssetPath.GetPath(iAssetType);
                return Path.Combine(aPath, SaveRelativePath);
            }
            public void OnGUI(UCL_ObjectDictionary iDataDic)
            {
                //GUILayout.Label("Config", UCL_GUIStyle.LabelStyle);
                UCL_GUILayout.DrawField(this, iDataDic, "Config");
                using(var aScope = new GUILayout.HorizontalScope())
                {
                    List<string> aModules = new List<string>();
                    aModules.Add(string.Empty);//Null
                    aModules.Append(m_BuiltinModules);
                    if (!string.IsNullOrEmpty(m_CurrentEditModule))
                    {
                        if (GUILayout.Button("Edit", UCL_GUIStyle.ButtonStyle, GUILayout.Width(150)))
                        {

                        }
                    }
                    m_CurrentEditModule = UCL_GUILayout.PopupAuto(m_CurrentEditModule, aModules, iDataDic, "SelectModules");

                }

            }
            public override JsonData SerializeToJson()
            {
//#if UNITY_EDITOR
//#endif
                if (Application.isEditor)//Check streamming assets for BuiltinModules
                {
                    var aBuiltinModulesPath = UCL_ModulePath.BuiltinModulesPath;
                    if (!Directory.Exists(aBuiltinModulesPath))//Init
                    {
                        InitBuiltinModules();
                    }
                    //Debug.LogError($"aBuiltinModulesPath:{aBuiltinModulesPath}");
                    var aDirs = UCL.Core.FileLib.Lib.GetDirectories(aBuiltinModulesPath, iSearchOption: SearchOption.TopDirectoryOnly, iRemoveRootPath : true);
                    //Debug.LogError($"aDirs:{aDirs.ConcatString()}");
                    m_BuiltinModules = aDirs.ToList();
                    if (!m_BuiltinModules.Contains(CoreModuleID))//Create Core Module!!
                    {
                        CreateModule(CoreModuleID, UCL_AssetType.StreamingAssets);
                        m_BuiltinModules.Add(CoreModuleID);
                    }
                }
                return base.SerializeToJson();
            }
            private void InitBuiltinModules()
            {
                var aBuiltinModulesPath = UCL_ModulePath.BuiltinModulesPath;
                if (!Directory.Exists(aBuiltinModulesPath))
                {
                    Directory.CreateDirectory(aBuiltinModulesPath);
                }
                
            }
            public UCL_Module CreateModule(string iID, UCL_AssetType iAssetType)
            {
                UCL_Module aModule = new UCL_Module();
                aModule.Init(iID, iAssetType);
                //var aPath = GetBuiltinModulesPath(aModule.m_AssetType);
                aModule.Save();//aPath
                return aModule;
            }
            public UCL_Module LoadModule(string iID, UCL_AssetType iAssetType)
            {
                UCL_Module aModule = new UCL_Module();
                aModule.ID = iID;
                aModule.Load(iAssetType);
                return aModule;
            }
        }

        public static UCL_ModuleService Ins
        {
            get
            {
                if(s_Ins == null)
                {
                    s_Ins = new UCL_ModuleService();
                    s_Ins.Init();
                }
                return s_Ins;
            }
        }
        public static UCL_Module CurEditModule
        {
            get
            {
                return Ins.m_CurEditModule;
            }
        }
        protected static UCL_ModuleService s_Ins = null;
        public static bool Initialized
        {
            get
            {
                return Ins.m_Initialized;
            }
        }

        public Config ModuleConfig => m_Config;

        protected bool m_Initialized = false;
        protected bool m_LoadingConfig = false;
        protected Config m_Config = new Config();

        protected UCL_Module m_CurEditModule = null;
        protected List<UCL_Module> m_LoadedModules = new List<UCL_Module>();

        virtual protected string SavePath
        {
            get
            {
                string aPath = string.Empty;
                if (Application.isEditor)//Save to streamming asset if in Editor
                {
                    return GetSavePath(UCL_AssetType.StreamingAssets);
                }

                return GetSavePath(UCL_AssetType.PersistentDatas);//save to Application.persistentDataPath path
            }
        }
        virtual protected string SaveRelativePath => Path.Combine(UCL_ModulePath.RootRelativePath, "Config.json");


        /// <summary>
        /// 暫定固定會有一個核心設定模組 放在StreammingAssets
        /// 只有在Editor內能夠編輯 Build出來的版本只能夠讀取(用UnityWebRequest讀取StreammingAssets 避免跨平台出問題)
        /// </summary>
        virtual protected void Init()
        {
            InitAsync().Forget();
        }

        virtual protected async UniTask InitAsync()
        {
            var aModulesPath = UCL_ModulePath.ModulesPath;
            if (!Directory.Exists(aModulesPath))
            {
                Directory.CreateDirectory(aModulesPath);
            }
            //Debug.LogError("InitAsync()");
            //string aStr = await UCL_StreamingAssets.LoadString(".Install/CommonData/ATS_IconSprite/Icon_Heal.json");
            //Debug.LogError($"InitAsync() End aStr:{aStr}");
            await LoadConfig();
            foreach(var aModuleID in m_Config.m_BuiltinModules)//Check if BuiltinModules installed
            {
                var aModule = m_Config.LoadModule(aModuleID, UCL_AssetType.StreamingAssets);
                await aModule.CheckAndInstall();
                if (Application.isEditor)
                {
                    m_LoadedModules.Add(aModule);
                }
            }
            if (!Application.isEditor)//Runtime using Modules from PersistentDatas
            {
                foreach (var aModuleID in m_Config.m_BuiltinModules)
                {
                    var aModule = m_Config.LoadModule(aModuleID, UCL_AssetType.PersistentDatas);
                    m_LoadedModules.Add(aModule);
                }
            }

            //Cheack and Install all Builtin Module to PersistantData path
            m_Initialized = true;
        }

        virtual protected string GetSavePath(UCL_AssetType iAssetType)
        {
            var aPath = UCL_AssetPath.GetPath(iAssetType);
            return Path.Combine(aPath, SaveRelativePath);
        }
        virtual protected void SaveConfig()
        {
            var aJson = m_Config.SerializeToJson();
            //Debug.LogError($"Application.isEditor:{Application.isEditor}");
            string aPath = SavePath;
            UCL.Core.FileLib.Lib.WriteAllText(aPath, aJson.ToJsonBeautify());
        }
        virtual protected async UniTask LoadConfig()
        {
            if (m_LoadingConfig)
            {
                return;
            }
            m_LoadingConfig = true;

            try
            {
                if (Application.isEditor)//Load from streamming asset if in Editor
                {
                    var aPath = GetSavePath(UCL_AssetType.StreamingAssets);
                    if (File.Exists(aPath))
                    {
                        LoadConfig(File.ReadAllText(aPath));
                    }
                }
                else //Try to load from Application.persistentDataPath
                {
                    var aPath = GetSavePath(UCL_AssetType.PersistentDatas);
                    if (File.Exists(aPath))
                    {
                        LoadConfig(File.ReadAllText(aPath));
                    }
                    else //config not exist!! try to load from streamming asset
                    {
                        try
                        {
                            string aJson = await UCL_StreamingAssets.LoadString(SaveRelativePath);
                            if (!string.IsNullOrEmpty(aJson))
                            {
                                LoadConfig(aJson);
                            }
                        }
                        catch (System.Exception e)
                        {
                            Debug.LogException(e);
                        }
                        //aPath = GetSavePath(UCL_AssetType.StreamingAssets);
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                m_LoadingConfig = false;
            }
        }
        virtual protected void LoadConfig(string iJson)
        {
            JsonData aJsonData = JsonData.ParseJson(iJson);
            m_Config.DeserializeFromJson(aJsonData);
        }
        virtual public string GetFolderPath(string iRelativeFolderPath)
        {
            if(m_CurEditModule == null)
            {
                return string.Empty;
            }
            return m_CurEditModule.GetFolderPath(iRelativeFolderPath);
            //return $".Install/CommonData/{iType.Name}";
        }
        virtual public void OnGUI(UCL_ObjectDictionary iDataDic)
        {
            using(var aScope = new GUILayout.HorizontalScope("box"))
            {
                GUILayout.Label("UCL_ModuleService", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                if(GUILayout.Button("Save Config", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    SaveConfig();
                }
                if (GUILayout.Button("Load Config", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    LoadConfig().Forget();
                }
            }

            m_Config.OnGUI(iDataDic.GetSubDic("Config"));
            string aCurrentEditModule = m_Config.m_CurrentEditModule;
            if (m_CurEditModule != null && m_CurEditModule.ID != aCurrentEditModule)
            {
                m_CurEditModule = null;//Clear
            }

            if (m_CurEditModule == null && !string.IsNullOrEmpty(aCurrentEditModule))
            {
                m_CurEditModule = m_Config.LoadModule(aCurrentEditModule, UCL_AssetType.StreamingAssets);
            }
            if(m_CurEditModule != null && !m_CurEditModule.IsLoading)
            {
                bool aLoadModule = false;
                using (var aScope = new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Save Module", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        m_CurEditModule.Save();
                    }
                    if (GUILayout.Button("Load Module", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        aLoadModule = true;
                    }
                    if (GUILayout.Button("Install Module", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                    {
                        //Debug.LogError("Install Module");
                        m_CurEditModule.Install().Forget();
                    }
#if UNITY_STANDALONE_WIN
                    if (GUILayout.Button(UCL_LocalizeManager.Get($"Open Module Folder"), UCL_GUIStyle.ButtonStyle))
                    {
                        UCL.Core.FileLib.WindowsLib.OpenExplorer(UCL_ModulePath.ModulesPath);
                    }
#endif
                }



                UCL_GUILayout.DrawObjectData(m_CurEditModule, iDataDic.GetSubDic("CurEditModule"), "CurEditModule");
                if (aLoadModule)
                {
                    m_CurEditModule = null;
                }

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
}
