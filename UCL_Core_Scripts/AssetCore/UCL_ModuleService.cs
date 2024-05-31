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
        /// <summary>
        /// for reflection
        /// </summary>
        public static string ModResourcesPath => CurEditModule.ModuleEntry.ModResourcesPath;

        public static string GetModResourcesPath(string iID)
        {
            return UCL_ModulePath.PersistantPath.GetModulesEntry(ModuleEditType).GetModuleEntry(iID).ModResourcesPath;
            //return UCL_ModulePath.GetModResourcesPath(PathConfig.GetModulePath(iID));
        }
        public static string CurEditModuleID
        {
            get
            {
                if(Ins.m_CurEditModule == null)
                {
                    return UCL_ModuleEntry.CoreModuleID;
                }
                return Ins.m_CurEditModule.ID;
            }
        }
        public static UCL_ModuleEditType ModuleEditType
        {
            get => Ins.m_PathConfig.m_ModuleEditType;
            private set => Ins.m_PathConfig.m_ModuleEditType = value;
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
            public UCL_ModulePlaylist m_Playlist = new UCL_ModulePlaylist();

            /// <summary>
            /// Editor中 強制每次啟動時安裝所有模組
            /// </summary>
            public bool m_ForceInstallInEditor = false;

            public string m_Version = "1.0.0";

            public void OnGUI(UCL_ObjectDictionary iDataDic)
            {

                UCL_GUILayout.DrawObjExSetting aDrawObjExSetting = new UCL_GUILayout.DrawObjExSetting();
                aDrawObjExSetting.OnShowField = () =>
                {
                    //GUILayout.Label("Config", UCL_GUIStyle.LabelStyle);
                    if (GUILayout.Button("Zip all modules"))
                    {
                        Debug.LogError($"{UCL_AssetPath.GetPath(UCL_AssetType.BuiltinModules)}");
                        UCL_ModulePath.ZipAllModules();
                    }
                    if (GUILayout.Button("Remove all Zip modules"))
                    {
                        UCL_ModulePath.RemoveAllZipAllModules();
                    }
                };
                UCL_GUILayout.DrawField(this, iDataDic, "ModuleService Config", iDrawObjExSetting: aDrawObjExSetting);

            }
            public override JsonData SerializeToJson()
            {
//#if UNITY_EDITOR
//#endif

                return base.SerializeToJson();
            }
            public UCL_Module CreateModule(string iID, UCL_ModuleEditType iModuleEditType)
            {
                UCL_Module aModule = new UCL_Module();
                aModule.Init(iID, iModuleEditType);
                aModule.Save();
                return aModule;
            }
            public UCL_Module LoadModule(string iID, UCL_ModuleEditType iModuleEditType)
            {
                UCL_Module aModule = new UCL_Module();
                aModule.Load(iID, iModuleEditType);
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
        //protected List<UCL_Module> m_CurEditModuleDependenciesModules = new List<UCL_Module>();

        #region Enviroment
        public class AssetConfig
        {
            
            public UCL_Module p_Module;

            public bool Inited { get; private set; }
            public string ID { get; private set; }
            public Type AssetType { get; private set; }
            public object AssetCache { get; set; }

            public bool Exist => p_Module != null;
            public string AssetPath => p_Module.ModuleEntry.GetAssetPath(AssetType, ID);

            public void Init(UCL_Module iModule, Type iAssetType, string iID)
            {
                Inited = true;
                p_Module = iModule;
                ID = iID;
                AssetType = iAssetType;
            }

            public JsonData GetJsonData()
            {
                string aPath = AssetPath;//UCL_ModuleService.Ins.GetAssetPath(aType, iID);

                if (!File.Exists(aPath))
                {
                    string aLog = $"AssetConfig.GetJsonData AssetType:{AssetType} ID: {ID},!File.Exists,aPath:{aPath}";
                    Debug.LogError(aLog);
                    //throw new System.Exception(aLog);
                    return null;
                }
                string aJsonData = File.ReadAllText(aPath);
                return JsonData.ParseJson(aJsonData);
            }
            public void SaveAsset(JsonData iJson)
            {
                System.IO.File.WriteAllText(AssetPath, iJson.ToJsonBeautify());
            }
            public void DeleteAsset()
            {
                string aPath = AssetPath;
                if (File.Exists(aPath))
                {
                    File.Delete(aPath);
                }
            }
        }
        public class AssetsCache
        {
            public Dictionary<string, AssetConfig> m_AssetConfigDic = new Dictionary<string, AssetConfig>();

            public AssetsCache() { }


            public AssetConfig GetAssetConfig(string iID)
            {
                if (!m_AssetConfigDic.ContainsKey(iID))
                {
                    m_AssetConfigDic[iID] = new AssetConfig();
                }
                return m_AssetConfigDic[iID];
            }
            public void ClearAssetsCache(string iID)
            {
                if (m_AssetConfigDic.ContainsKey(iID))
                {
                    m_AssetConfigDic.Remove(iID);
                }
            }
            
        }
        /// <summary>
        /// Loaded Modules
        /// 當前已載入的模組 讀取UCL_Assets時會按照順序判斷(若ID相同則選取排序在前面的模組中的Asset)
        /// </summary>
        protected List<UCL_Module> m_LoadedModules = new List<UCL_Module>();
        protected Dictionary<Type, AssetsCache> m_AssetsCacheDic = new Dictionary<Type, AssetsCache>();
        #endregion
        /// <summary>
        /// Init UCL_ModuleService
        /// 暫定固定會有一個核心設定模組 放在StreammingAssets
        /// 只有在Editor內能夠編輯 Build出來的版本只能夠讀取(用UnityWebRequest讀取StreammingAssets 避免跨平台出問題)
        /// </summary>
        virtual protected void Init()
        {
            InitAsync().Forget();
        }

        virtual protected async UniTask InitAsync()
        {
            //await UCL.Core.Page.UCL_OptionPage.ShowAlertAsync("UCL_ModuleService.InitAsync()", "");
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

            bool aForceInstall = m_Config.m_ForceInstallInEditor;

            if (aForceInstall)
            {
                foreach (var aModuleID in m_Config.m_BuiltinModules)//Check if all builtin modules installed
                {
                    var aModule = m_Config.LoadModule(aModuleID, UCL_ModuleEditType.Builtin);
                    await aModule.Install();
                }
            }
            else
            {
                //List<UniTask> aTasks = new List<UniTask>();
                foreach (var aModuleID in m_Config.m_BuiltinModules)//Check if all builtin modules installed
                {
                    var aModule = m_Config.LoadModule(aModuleID, UCL_ModuleEditType.Builtin);
                    await aModule.CheckAndInstall();
                    //aTasks.Add(aModule.CheckAndInstall());
                }
                //await UniTask.WhenAll(aTasks);
            }

            m_Config.m_Playlist.LoadPlaylist();


            //if (m_Config.m_ModulePlayList.IsNullOrEmpty())
            //{
            //    m_Config.m_ModulePlayList.Add(new UCL_ModuleEntry(UCL_ModuleEntry.CoreModuleID));
            //}
            //HashSet<string> aLoadedModule = new HashSet<string>();
            //foreach (var aModuleID in m_Config.m_ModulePlayList)
            //{
            //    string aID = aModuleID.ID;
            //    if (aLoadedModule.Contains(aID))//Loaded
            //    {
            //        continue;
            //    }
            //    aLoadedModule.Add(aID);
            //    try
            //    {
            //        var aModule = m_Config.LoadModule(aModuleID.ID, UCL_ModuleEditType.Runtime);
            //        m_LoadedModules.Add(aModule);
            //    }
            //    catch (Exception ex)
            //    {
            //        Debug.LogException(ex);
            //    }
            //}


            m_LoadedModules.Reverse();//Modules that are loaded later will overwrite the previous modules(if asset have same ID)
            //Cheack and Install all Builtin Module to PersistantData path
            m_Initialized = true;
        }
        /// <summary>
        /// get all modules ID of current ModuleEditType
        /// </summary>
        /// <returns></returns>
        public IList<string> GetAllModulesID()
        {
            return UCL_ModulePath.PersistantPath.GetModulesEntry(ModuleEditType).GetAllModulesID();
            //return UCL_ModulePath.GetAllModulesID(ModuleEditType);
        }


        public IList<string> GetAllEditableAssetsID(Type iAssetType)
        {
            if(m_CurEditModule == null)
            {
                return Array.Empty<string>();//new List<string>();
            }
            return m_CurEditModule.ModuleEntry.GetAllAssetsID(iAssetType);
        }
        /// <summary>
        /// All assets ID(include assets in dependencies modules)
        /// </summary>
        /// <param name="iAssetType"></param>
        /// <returns></returns>
        public List<string> GetAllAssetsID(Type iAssetType)
        {
            var aModules = LoadedModules;
            //Debug.Log($"GetAllAssetsID aModules:{aModules.ConcatString(iModule => iModule.ID)}");
            var aIDSet = new HashSet<string>();
            foreach (var aModule in aModules)
            {
                var aIDs = aModule.ModuleEntry.GetAllAssetsID(iAssetType);

                foreach (var aID in aIDs)
                {
                    aIDSet.Add(aID);
                }
            }

            return aIDSet.ToList();
        }

        public List<UCL_Module> LoadedModules => m_LoadedModules;

        private AssetsCache GetAssetsCache(Type iAssetType)
        {
            if (!m_AssetsCacheDic.ContainsKey(iAssetType))
            {
                m_AssetsCacheDic[iAssetType] = new AssetsCache();
            }
            return m_AssetsCacheDic[iAssetType];
        }
        public void ClearAssetsCache(Type iAssetType)
        {
            if (m_AssetsCacheDic.ContainsKey(iAssetType))
            {
                m_AssetsCacheDic.Remove(iAssetType);
            }
        }
        public void ClearAssetsCache(Type iAssetType, string iID)
        {
            if (!m_AssetsCacheDic.ContainsKey(iAssetType))
            {
                return;
            }
            var aAssetsCache = m_AssetsCacheDic[iAssetType];
            aAssetsCache.ClearAssetsCache(iID);
        }
        public AssetConfig GetAssetConfig(Type iAssetType, string iID)
        {
            var aAssetsCache = GetAssetsCache(iAssetType);
            var aConfig = aAssetsCache.GetAssetConfig(iID);
            if (!aConfig.Inited)
            {
                foreach (UCL_Module aModule in LoadedModules)
                {
                    if (aModule.ModuleEntry.ContainsAsset(iAssetType, iID))
                    {
                        aConfig.Init(aModule, iAssetType, iID);
                        return aConfig;//return config
                    }
                }
                //Debug.LogError($"GetAssetConfig iAssetType:{iAssetType},iID:{iID}, Asset not exist!!");
                aConfig.Init(m_CurEditModule, iAssetType, iID);
            }
            return aConfig;
        }
        /// <summary>
        /// Check if asset exist
        /// </summary>
        /// <param name="iID">ID of asset</param>
        /// <returns>true if asset exist</returns>
        public bool ContainsAsset(Type iAssetType, string iID)// => FileDatas.FileExists(iID);
        {
            var aAssetConfig = GetAssetConfig(iAssetType, iID);
            return aAssetConfig.Exist;
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
                    if (!m_Config.m_BuiltinModules.Contains(UCL_ModuleEntry.CoreModuleID))//Create Core Module!!
                    {
                        m_Config.CreateModule(UCL_ModuleEntry.CoreModuleID, UCL_ModuleEditType.Builtin);
                        m_Config.m_BuiltinModules.Add(UCL_ModuleEntry.CoreModuleID);
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
        virtual public string GetCurEditModuleFolder(string iRelativeFolderPath)
        {
            string aModuleID = UCL_ModuleEntry.CoreModuleID;
            if(m_CurEditModule !=  null)
            {
                aModuleID = m_CurEditModule.ID;
            }
            return GetFolderPath(aModuleID, iRelativeFolderPath);
        }
        virtual public string GetFolderPath(string iModuleID, string iRelativeFolderPath)
        {
            string aPath = PathConfig.GetModulePath(iModuleID);
            return Path.Combine(aPath, iRelativeFolderPath);
        }
        public void ClearCurrentEditModule()
        {
            m_CurEditModule = null;
            //m_LoadedModules.Clear();
        }

        public Dictionary<string, UCL_Module> LoadModulePlaylist(UCL_ModulePlaylist iModulePlayist)
        {
            m_LoadedModules.Clear();
            m_AssetsCacheDic.Clear();

            var aLoadedModules = new Dictionary<string, UCL_Module>();
            foreach (var aModule in iModulePlayist.m_Playlist)
            {
                LoadModuleAndDependencies(aModule.ID, aLoadedModules);
            }
            return aLoadedModules;
        }
        protected void SetCurrentEditModule(string iModuleID)
        {
            ClearCurrentEditModule();
            UCL_ModulePlaylist aModulePlaylist = new UCL_ModulePlaylist(iModuleID);
            var aLoadedModules = aModulePlaylist.LoadPlaylist();
            m_CurEditModule = aLoadedModules[iModuleID];
            //if(!m_LoadedModules.IsNullOrEmpty())
            //{
            //    m_CurEditModule = m_LoadedModules[0];
            //}
            //m_CurEditModule = LoadModuleAndDependencies(iModuleID, new HashSet<string>());//m_Config.LoadModule(iModuleID, AssetType);

            Debug.LogWarning($"m_LoadedModules:{m_LoadedModules.ConcatString(iModule => iModule.ID)}");
        }
        protected UCL_Module LoadModuleAndDependencies(string iModuleID, Dictionary<string, UCL_Module> iLoadedModules)
        {
            if (iLoadedModules.ContainsKey(iModuleID))//Already in dependencies
            {
                return iLoadedModules[iModuleID];
            }
            

            var aModule = m_Config.LoadModule(iModuleID, ModuleEditType);
            iLoadedModules[iModuleID] = aModule;
            m_LoadedModules.Add(aModule);

            List<UCL_ModuleEntry> aDependenciesModules = aModule.m_Config.m_DependenciesModules;
            if (aDependenciesModules.IsNullOrEmpty())//No extra dependencies modules
            {
                return aModule;
            }
            for (int i = aDependenciesModules.Count - 1; i >= 0; i--)
            {
                var aModuleEntry = aDependenciesModules[i];
                LoadModuleAndDependencies(aModuleEntry.ID, iLoadedModules);
            }

            return aModule;
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
            using (var aScope = new GUILayout.HorizontalScope("box"))
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
                    m_Config.CreateModule(m_NewModuleName, ModuleEditType);
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
                bool aCanEdit = !string.IsNullOrEmpty(m_Config.m_CurrentEditModule);
                if (GUILayout.Button("Edit", UCL_GUIStyle.GetButtonStyle(aCanEdit? Color.white : Color.red), GUILayout.Width(150)))
                {
                    if (aCanEdit)
                    {
                        SetCurrentEditModule(m_Config.m_CurrentEditModule);
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
