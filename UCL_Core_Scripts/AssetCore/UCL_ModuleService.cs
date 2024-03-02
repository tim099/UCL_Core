using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{

    public enum UCL_ModuleEditType
    {
        Builtin,
        Runtime,
    }

    /// <summary>
    /// Responsible for all operations related to Module
    /// </summary>
    public class UCL_ModuleService//: UCLI_FieldOnGUI
    {
        public const string CoreModuleID = "Core";

        #region static
        public static UCL_ModuleService Ins
        {
            get
            {
                if (s_Ins == null)
                {
                    s_Ins = new UCL_ModuleService();
                    s_Ins.Init();
                }
                return s_Ins;
            }
        }

        public const string ReflectKeyModResourcesPath = "ModResourcesPath";
        public static string ModResourcesPath
        {
            get
            {
                return UCL_ModulePath.GetModResourcesPath(PathConfig.GetModulePath(CurEditModuleID));
                //return UCL_ModulePath.GetModulePath(CurEditModuleID);
            }
        }
        public static string GetModResourcesPath(string iID)
        {
            return UCL_ModulePath.GetModResourcesPath(PathConfig.GetModulePath(iID));
        }
        public static string CurEditModuleID
        {
            get
            {
                if(Ins.m_CurEditModule == null)
                {
                    return CoreModuleID;
                }
                return Ins.m_CurEditModule.ID;
            }
        }
        public static UCL_ModuleEditType ModuleEditType
        {
            get => Ins.m_PathConfig.m_ModuleEditType;
            private set => Ins.m_PathConfig.m_ModuleEditType = value;
        }
        public static UCL_AssetType AssetType
        {
            get
            {
                switch(ModuleEditType)
                {
                    case UCL_ModuleEditType.Builtin:
                        {
                            return UCL_AssetType.Addressables;
                        }
                }
                return UCL_AssetType.PersistentDatas;
            }
        }
        public static UCL_Module CurEditModule => Ins.m_CurEditModule;
        protected static UCL_ModuleService s_Ins = null;
        public static bool Initialized
        {
            get
            {
                return Ins.m_Initialized;
            }
        }
        public static UCL_ModulePathConfig PathConfig
        {
            get
            {
                return Ins.m_PathConfig;
            }
        }
        #endregion

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
            /// <summary>
            /// All module in this list will be loaded
            /// </summary>
            public List<string> m_ModulePlayList = new List<string>();

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
                UCL_GUILayout.DrawField(this, iDataDic, "ModuleService Config");
            }
            public override JsonData SerializeToJson()
            {
//#if UNITY_EDITOR
//#endif

                return base.SerializeToJson();
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

        public Config ModuleConfig => m_Config;
        public UCL_ModulePathConfig m_PathConfig = new UCL_ModulePathConfig();

        protected bool m_Initialized = false;
        protected bool m_LoadingConfig = false;
        protected Config m_Config = new Config();

        protected string m_NewModuleName = "New Module";
        protected UCL_Module m_CurEditModule = null;
        protected List<UCL_Module> m_LoadedModules = new List<UCL_Module>();

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
            if (Application.isEditor)
            {
                ModuleEditType = UCL_ModuleEditType.Builtin;
            }
            else
            {
                ModuleEditType = UCL_ModuleEditType.Runtime;
            }
            var aModulesPath = m_PathConfig.ModulesPath;
            //Debug.LogError($"aModulesPath:{aModulesPath},FileSystemRootPath:{m_PathConfig.FileSystemRootPath}");
            if (!Directory.Exists(aModulesPath))//Not Installed
            {
                Directory.CreateDirectory(aModulesPath);
            }
            //Debug.LogError("InitAsync()");
            await LoadConfig();



            foreach(var aModuleID in m_Config.m_BuiltinModules)//Check if BuiltinModules installed
            {
                var aModule = m_Config.LoadModule(aModuleID, UCL_AssetType.StreamingAssets);
                await aModule.CheckAndInstall();
                //Debug.LogError($"CheckAndInstall() aModuleID:{aModuleID}");
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
        public IList<string> GetAllModulesID()
        {
            return UCL_ModulePath.GetAllModulesID(ModuleEditType);
        }
        public List<string> GetAllEditableAssetsID(Type iAssetType)
        {
            string aAssetRelativePath = UCL_ModulePath.GetAssetRelativePath(iAssetType);

            string aFolderPath = GetFolderPath(aAssetRelativePath);
            //Debug.LogError($"aFolderPath:{aFolderPath}");
            return UCL.Core.FileLib.Lib.GetFilesName(aFolderPath, "*.json", SearchOption.TopDirectoryOnly, true).ToList();
        }
        public List<string> GetAllAssetsID(Type iAssetType)
        {
            string aAssetRelativePath = UCL_ModulePath.GetAssetRelativePath(iAssetType);

            if(m_CurEditModule == null)//Not editing module
            {
                string aFolderPath = GetFolderPath(aAssetRelativePath);
                //Debug.LogError($"aFolderPath:{aFolderPath}");
                return UCL.Core.FileLib.Lib.GetFilesName(aFolderPath, "*.json", SearchOption.TopDirectoryOnly, true).ToList();
            }

            {
                string aFolderPath = GetFolderPath(m_CurEditModule.ID, aAssetRelativePath);
                
                List<string> aPaths = UCL.Core.FileLib.Lib.GetFilesName(aFolderPath, "*.json", SearchOption.TopDirectoryOnly, true).ToList();
                var aDependenciesModules = m_CurEditModule.m_Config.m_DependenciesModules;
                //Debug.LogError($"aFolderPath:{aFolderPath},aDependenciesModules:{aDependenciesModules.ConcatString(iDependenciesModule => iDependenciesModule.ID)}");
                if (!aDependenciesModules.IsNullOrEmpty())
                {
                    foreach(var aModule in aDependenciesModules) 
                    {
                        string aModuleID = aModule.ID;
                        //Debug.LogError($"aFolderPath:{aFolderPath}");
                        aFolderPath = GetFolderPath(aModule.ID, aAssetRelativePath);
                        var aIDs = UCL.Core.FileLib.Lib.GetFilesName(aFolderPath, "*.json", SearchOption.TopDirectoryOnly, true);
                        foreach(var aID in aIDs)
                        {
                            aPaths.Add($"{aModuleID}/{aID}");
                        }
                        //aPaths.Append(aIDs);
                    }
                }
                return aPaths;
            }


            //if (m_CurEditModule != null)//Editing Module
            //{

            //}
            
        }
        virtual protected string GetSavePath(UCL_AssetType iAssetType)
        {
            var aPath = UCL_AssetPath.GetPath(iAssetType);
            return Path.Combine(aPath, m_PathConfig.ConfigRelativePath);
        }
        virtual protected void SaveConfig()
        {

            if (Application.isEditor)//Check streamming assets for BuiltinModules
            {
                if(ModuleEditType == UCL_ModuleEditType.Builtin)
                {
                    string aModulesPath = m_PathConfig.ModulesPath;
                    if (Directory.Exists(aModulesPath))
                    {
                        Directory.CreateDirectory(aModulesPath);
                    }
                    
                    //Debug.LogError($"aBuiltinModulesPath:{aBuiltinModulesPath}");
                    //var aDirs = UCL.Core.FileLib.Lib.GetDirectories(aModulesPath, iSearchOption: SearchOption.TopDirectoryOnly, iRemoveRootPath: true);
                    var aAllModulesID = GetAllModulesID();
                    //Debug.LogError($"aDirs:{aDirs.ConcatString()}");


                    m_Config.m_BuiltinModules = aAllModulesID.ToList();
                    if (!m_Config.m_BuiltinModules.Contains(CoreModuleID))//Create Core Module!!
                    {
                        m_Config.CreateModule(CoreModuleID, UCL_AssetType.StreamingAssets);
                        m_Config.m_BuiltinModules.Add(CoreModuleID);
                    }
                }

            }

            var aJson = m_Config.SerializeToJson();
            m_PathConfig.SaveConfig(aJson);
            //Debug.LogError($"Application.isEditor:{Application.isEditor}");
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
                JsonData aConfigJson = await m_PathConfig.LoadConfig();
                if(aConfigJson != null)
                {
                    LoadConfig(aConfigJson);
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
        virtual protected void LoadConfig(JsonData iJson)
        {
            //JsonData aJsonData = JsonData.ParseJson(iJson);
            m_Config.DeserializeFromJson(iJson);
        }
        virtual public string GetFolderPath(string iRelativeFolderPath)
        {
            string aModuleID = CoreModuleID;
            if(m_CurEditModule !=  null)
            {
                aModuleID = m_CurEditModule.ID;
            }
            return GetFolderPath(aModuleID, iRelativeFolderPath);
            //string aPath = PathConfig.GetModulePath(aModuleID);
            //return Path.Combine(aPath, iRelativeFolderPath);
        }
        virtual public string GetFolderPath(string iModuleID, string iRelativeFolderPath)
        {
            string aPath = PathConfig.GetModulePath(iModuleID);
            return Path.Combine(aPath, iRelativeFolderPath);
        }
        virtual public List<string> GetModulesID(UCL_AssetType iAssetType)
        {
            List<string> aIDs = new List<string>();
            switch (iAssetType)
            {
                case UCL_AssetType.StreamingAssets://BuiltIn Modules
                    {
                        aIDs.Append(m_Config.m_BuiltinModules);
                        break;
                    }
                case UCL_AssetType.PersistentDatas:
                    {
                        GetSavePath(iAssetType);
                        break;
                    }
            }
            return aIDs;
        }
        public void ClearCurrentEditModule()
        {
            m_CurEditModule = null;
        }
        virtual public void OnGUI(UCL_ObjectDictionary iDataDic)
        {
            if(Application.isEditor)//ModuleEditType Builtin can only edit in Editor
            {
                using (var aScope = new GUILayout.HorizontalScope("box"))
                {
                    GUILayout.Label("EditType", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    ModuleEditType = UCL_GUILayout.PopupAuto(ModuleEditType, iDataDic.GetSubDic("EditType"));
                }
            }
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
#if UNITY_STANDALONE_WIN
                if (GUILayout.Button(UCL_LocalizeManager.Get($"Open Module Folder"), UCL_GUIStyle.ButtonStyle))
                {
                    UCL.Core.FileLib.WindowsLib.OpenExplorer(m_PathConfig.RootPath);
                }
#endif
            }

            m_Config.OnGUI(iDataDic.GetSubDic("Config"));

            using (var aScope = new GUILayout.HorizontalScope("box"))
            {
                if (GUILayout.Button("Create", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    m_Config.CreateModule(m_NewModuleName, AssetType);
                    SaveConfig();
                    m_Config.m_CurrentEditModule = m_NewModuleName;
                }
                GUILayout.Label("Module ID", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                m_NewModuleName = GUILayout.TextField(m_NewModuleName, UCL_GUIStyle.TextFieldStyle);

            }

            using (var aScope = new GUILayout.HorizontalScope())
            {
                List<string> aModules = new List<string>();
                aModules.Add(string.Empty);//Null
                //aModules.Append(m_Config.m_BuiltinModules);
                aModules.Append(GetAllModulesID());

                
                if (!string.IsNullOrEmpty(m_Config.m_CurrentEditModule))
                {
                    if (GUILayout.Button("Edit", UCL_GUIStyle.ButtonStyle, GUILayout.Width(150)))
                    {
                        m_CurEditModule = m_Config.LoadModule(m_Config.m_CurrentEditModule, AssetType);
                        UCL_ModuleEditPage.Create(m_CurEditModule);
                    }
                }
                GUILayout.Label("Module ID", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                m_Config.m_CurrentEditModule = UCL_GUILayout.PopupAuto(m_Config.m_CurrentEditModule, aModules, iDataDic, "SelectModules");

            }

            UCL_GUILayout.DrawObjectData(m_LoadedModules, iDataDic.GetSubDic("LoadedModules"), "LoadedModules");
        }
    }
}
