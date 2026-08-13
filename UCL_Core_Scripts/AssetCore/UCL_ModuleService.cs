using Cysharp.Threading.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UCL.Core.JsonLib;
using UCL.Core.LocalizeLib;
using UCL.Core.Page;
using UCL.Core.UI;
using UnityEngine;

namespace UCL.Core
{

    /// <summary>
    /// Module edit type: determines the storage location and access method of module data.
    /// Builtin = StreamingAssets (Read-only, packaged with Build);
    /// Runtime = PersistentDataPath (Read-write, supports Mod and user customization).
    /// </summary>
    public enum UCL_ModuleEditType
    {
        /// <summary>Built-in module, data located in StreamingAssets, only editable in Editor.</summary>
        Builtin,
        /// <summary>Runtime module, data located in PersistentDataPath, supports runtime read/write.</summary>
        Runtime,
        /// <summary>
        /// Editor-only template editing mode — 直接編輯 UCL_Core/Templates~/Assets/.BuiltinModules 下的 Asset。
        /// 這些檔案會作為 UCL_Core Bootstrap 安裝到新專案時的預設值；可用此模式在 UI 內維護那些範本。
        /// </summary>
        Template,
        /// <summary>
        /// PC 免安裝直讀模式 — 模組原始檔直接放在 StreamingAssets (streamingAssetsPath/.ModuleService/Modules/{id})，
        /// runtime 用同步 File IO 直讀、不解壓、不複製到 PersistentDataPath，故該模組唯讀。
        /// 僅在 Standalone(PC) build 由 m_PCDirectStreaming opt-in 模組才會被指定此型別；全域 ModuleEditType 不會是此值。
        /// 其他平台不適用 (StreamingAssets 同步 File IO 讀不到)。
        /// </summary>
        StreamingReadOnly,
    }

    /// <summary>
    /// UCL Module Lifecycle Management Service (Singleton).
    /// Responsible for module initialization, loading, installation, Asset path resolution, cache management, and Inspector GUI rendering.
    /// All UCL_Asset access paths are determined by this service, serving as the core entry point for the entire UCL asset framework.
    /// </summary>
    [HelpURL("ucl_core:Docs~/{lang}/UCL_ModuleService/ModuleInstallation.md")]
    public class UCL_ModuleService
    {
        // ─────────────────────────────────────────────
        // [GUI Page State Persistence]
        // CurState records which page the Inspector is currently on across scenes via UCL_PlayerPrefs,
        // allowing it to automatically restore to the last edited page when re-entering Play Mode.
        // ─────────────────────────────────────────────

        // PlayerPrefs key for storing the current GUI page state
        private const string CurStateKey = "UCL_ModuleService.CurState";

        /// <summary>
        /// Current Inspector GUI page state, persisted via UCL_PlayerPrefs.
        /// Default value is State.Main.
        /// </summary>
        public static State CurState
        {
            // Read enum value from PlayerPrefs; return default State.Main if not found
            get => UCL_PlayerPrefs.GetEnum(CurStateKey, State.Main);
            // Serialize enum value and write to PlayerPrefs to ensure page state is not lost after restart
            set => UCL_PlayerPrefs.SetEnum(CurStateKey, value);
        }

        /// <summary>
        /// Page state enumeration for UCL_ModuleService Inspector GUI.
        /// Controls whether the user sees the main menu or the module edit page.
        /// </summary>
        public enum State
        {
            /// <summary>
            /// Main page: Displays a list of all selectable modules for the user to choose from.
            /// </summary>
            Main,
            /// <summary>
            /// Module edit page: Enters the asset editing process for the specified module; m_CurEditModule is not null at this point.
            /// </summary>
            EditModule,
        }

        /// <summary>
        /// Module installation mode enumeration in Editor environment.
        /// Determines the method used to copy Builtin modules to PersistentDataPath.
        /// </summary>
        public enum EditorInstallMode
        {
            /// <summary>
            /// Default mode: Directly copy module folders to the target installation path (fast, suitable for development iterations).
            /// </summary>
            Default,
            /// <summary>
            /// UnZip mode: Simulates installation by extracting from a .zip package (used to verify packaging process correctness).
            /// </summary>
            UnZip,
        }

        // ════════════════════════════════════════════════════════
        // [Static Area] Singleton, Events, Asynchronous Loading Callback Pipeline
        // Responsibility: Provides a global unique entry point (Ins) and manages event broadcasting before and after module loading.
        // ════════════════════════════════════════════════════════
        #region static

        /// <summary>
        /// Lazy-loaded singleton property. Automatically creates an instance and starts the async initialization process on first access.
        /// ⚠ Note: Init() internally calls the async method InitAsync(),
        ///   so initialization completion is not guaranteed after the first access.
        ///   To ensure completion, use WaitUntilInitialized(token).
        /// </summary>
        public static UCL_ModuleService Ins
        {
            get
            {
                // If the singleton hasn't been created yet, perform lazy-loaded initialization
                if (s_Ins == null)
                {
                    s_Ins = new UCL_ModuleService(); // Create instance
                    s_Ins.Init();                    // Start async initialization (InitAsync runs in background)
                }
                return s_Ins; // Return singleton instance
            }
        }

        /// <summary>
        /// Synchronous event triggered before module loading starts.
        /// Subscribers can clear old resources or reset states here.
        /// </summary>
        public static event System.Action OnLoadModule;

        /// <summary>
        /// Event triggered immediately after synchronous module loading is complete (excluding async callbacks).
        /// Subscribers can re-acquire module references here.
        /// </summary>
        public static event System.Action OnLoadedModule;

        // List of asynchronous callback functions after module loading is complete.
        // Each Func receives a CancellationToken and returns a UniTask;
        // All registered Funcs will be executed in parallel within OnLoadedModuleAsync.
        private static List<System.Func<CancellationToken, UniTask>> s_OnLoadedModuleFunc = new();

        /// <summary>
        /// Registers an asynchronous callback to be executed after module loading is complete.
        /// Suitable for systems that require module data to be ready before initialization (e.g., ClimateSim, Earth).
        /// </summary>
        /// <param name="func">Asynchronous callback function receiving a CancellationToken</param>
        public static void AddLoadedModuleFunc(System.Func<CancellationToken, UniTask> func)
        {
            s_OnLoadedModuleFunc.Add(func); // Add to the asynchronous callback list
        }

        /// <summary>
        /// Removes a registered asynchronous callback. Typically called in OnDestroy to avoid memory leaks.
        /// </summary>
        /// <param name="func">Async callback function to remove (must be the same delegate instance as when added)</param>
        public static void RemoveLoadedModuleFunc(System.Func<CancellationToken, UniTask> func)
        {
            s_OnLoadedModuleFunc.Remove(func); // Remove specified callback from the list
        }

        // ─────────────────────────────────────────────
        // [Module Loading Completion Async Pipeline]
        // Execution order:
        //   1. Execute all UCL_OnModuleLoadedAsset in ascending order of m_Order (await each to ensure sequence)
        //   2. Execute all registered s_OnLoadedModuleFunc in parallel (UniTask.WhenAll for performance)
        // ─────────────────────────────────────────────
        private static async UniTask OnLoadedModuleAsync(CancellationToken token)
        {
            // Get utility instance for UCL_OnModuleLoadedAsset to enumerate all registered assets
            var util = UCL_OnModuleLoadedAsset.Util;
            // Get ID list for all UCL_OnModuleLoadedAsset
            var ids = util.GetAllIDs();

            if (!ids.IsNullOrEmpty()) // Only execute if there are registered OnModuleLoaded assets
            {
                // Sort by m_Order field ascending to ensure predictable execution sequence (lower values execute first)
                var onModuleLoadedAssets = ids.Select(id => util.GetData(id)).OrderBy(data => data.m_Order);

                // Wait for each asset's OnModuleLoaded to complete sequentially (ensures execution order)
                foreach (var asset in onModuleLoadedAssets)
                {
                    try
                    {
                        // Wait for asset's module loading callback to finish
                        await asset.OnModuleLoaded(token);
                    }
                    catch (System.OperationCanceledException) { } // Silently skip if task is canceled
                    catch (System.Exception e)
                    {
                        // Failure of a single asset does not interrupt the overall flow; log error and continue
                        Debug.LogException(e);
                        Debug.LogError($"OnModuleLoaded asset:{asset.ID}, Exception:{e}");
                    }
                }
            }

            // Execute all registered async callback functions in parallel (performance first, order not guaranteed)
            if(!s_OnLoadedModuleFunc.IsNullOrEmpty())
            {
                List<UniTask> tasks = new(); // Collect all UniTasks to be executed
                foreach (var func in s_OnLoadedModuleFunc)
                {
                    if (func != null) // Prevent NullReferenceException from null delegates
                    {
                        tasks.Add(func.Invoke(token)); // Start async task and add to wait list
                    }
                }
                if (!tasks.IsNullOrEmpty())
                {
                    // Parallel wait for all tasks to complete; any task exception will propagate here
                    await UniTask.WhenAll(tasks);
                }
            }
        }
        /// <summary>
        /// Resources path of the current edited module.
        /// Provided for reflection use to avoid direct access to the CurEditModule property.
        /// Returns string.Empty if retrieval fails (e.g., m_CurEditModule is null).
        /// </summary>
        public static string ModResourcesPath
        {
            get
            {
                try
                {
                    // Get Resources directory path via the current edited module's ModuleEntry
                    return CurEditModule.ModuleEntry.ModResourcesPath;
                }catch(System.Exception ex)
                {
                    // Log exception and return empty string if CurEditModule is null or ModuleEntry is invalid
                    Debug.LogException(ex);
                }
                return string.Empty; // Fallback: Return empty string to prevent NullReference at caller
            }
        }

        /// <summary>
        /// Gets the corresponding Resources path based on the module ID.
        /// Uses per-module resolved edit type (ResolveEditType) to determine data source.
        /// </summary>
        /// <param name="iID">Module ID to query</param>
        /// <returns>ModResourcesPath of the module, i.e., [ModulesRoot]/{iID}/Resources</returns>
        public static string GetModResourcesPath(string iID)
        {
            // *關鍵修正*：必須用 per-module ResolveEditType(iID) 而非全域 ModuleEditType。
            // 物理意義：免安裝(StreamingReadOnly)模組的 ModResources 圖檔在 StreamingAssets，但全域 ModuleEditType 在 build = Runtime
            //          → 用全域會去 PersistentDataPath 找圖 → 直讀模組(如 Core)的新增道具/裝備圖讀不到(資料讀取混用)。
            // 數值影響：改用 Ins.ResolveEditType(iID) 後，Core(StreamingReadOnly) → StreamingAssets 正確路徑；非 override 模組仍走全域。
            return UCL_ModulePath.PersistantPath.GetModulesEntry(Ins.ResolveEditType(iID)).GetModuleEntry(iID).ModResourcesPath;
        }
        /// <summary>
        /// ID of the module currently being edited.
        /// Returns Core module ID if m_CurEditModule is null (no module selected).
        /// </summary>
        public static string CurEditModuleID
        {
            get
            {
                if(Ins.m_CurEditModule == null)
                {
                    // Not in edit state yet, return Core module as Fallback
                    return UCL_ModuleEntry.CoreModuleID;
                }
                return Ins.m_CurEditModule.ID; // Return ID of current edited module
            }
        }

        /// <summary>
        /// Module ID selected by user in GUI Popup, persisted via PlayerPrefs.
        /// Different from m_CurEditModule: This is "selected for editing" rather than "currently editing" ID.
        /// Default value is CoreModuleID.
        /// </summary>
        protected static string CurrentEditModuleID
        {
            // Read last selected module ID from PlayerPrefs
            get => PlayerPrefs.GetString(nameof(CurrentEditModuleID), UCL_ModuleEntry.CoreModuleID);
            // Write selected module ID to PlayerPrefs to restore after restart
            set => PlayerPrefs.SetString(nameof(CurrentEditModuleID), value);
        }

        /// <summary>
        /// Current module edit type (Builtin / Runtime).
        /// Persisted via UCL_PlayerPrefs.
        /// Setting this property has side effects: Clears m_ModuleCache and syncs to m_PathConfig.
        /// </summary>
        public static UCL_ModuleEditType ModuleEditType
        {
            // Read from UCL_PlayerPrefs; return Builtin (safe default) if not found
            get => UCL_PlayerPrefs.GetEnum(nameof(ModuleEditType), UCL_ModuleEditType.Builtin);
            private set
            {
                Ins.m_ModuleCache.Clear(); // Old module instances are invalid after type switch, must clear
                UCL_PlayerPrefs.SetEnum(nameof(ModuleEditType), value); // Write to PlayerPrefs for persistence
                Ins.m_PathConfig.m_ModuleEditType = value; // Sync to PathConfig to ensure correct path calculation
            }
        }

        /// <summary>
        /// Module currently being edited (equivalent to Ins.m_CurEditModule).
        /// </summary>
        public static UCL_Module CurEditModule => Ins.m_CurEditModule;

        /// <summary>
        /// Property for the current active module (with Fallback mechanism).
        /// Priority: m_CurEditModule > m_LoadedModules.Last() > null
        /// </summary>
        public UCL_Module CurModule
        {
            get
            {
                if(m_CurEditModule != null)
                {
                    return m_CurEditModule; // Return editing module as top priority
                }
                if(!m_LoadedModules.IsNullOrEmpty())
                {
                    // Return the last loaded module (highest priority, can overwrite previous assets with same ID)
                    return m_LoadedModules.Last();
                }
                return null; // No modules loaded
            }
        }

        // Static storage field for the singleton instance, managed by lazy loading in the Ins property
        protected static UCL_ModuleService s_Ins = null;

        /// <summary>
        /// Returns whether UCL_ModuleService has completed initialization.
        /// Returns false if the instance is not created yet, otherwise returns the m_Initialized flag.
        /// </summary>
        public static bool Initialized
        {
            get
            {
                if (s_Ins == null) return false; // Instance not yet created
                return Ins.m_Initialized;         // Return whether InitAsync() has completed
            }
        }

        /// <summary>
        /// Static access property for the global path configuration object.
        /// All module-related path calculations are handled by this object.
        /// </summary>
        public static UCL_ModulePathConfig PathConfig
        {
            get
            {
                return Ins.m_PathConfig; // Return the path configuration object of the instance
            }
        }
        #endregion

        // ════════════════════════════════════════════════════════
        // [ModuleExportConfig] Module export configuration
        // Responsibility: Controls whether a single module participates in export and installation processes.
        // Implements UCLI_IsEnable interface for unified enable/disable control via IsEnable.
        // ════════════════════════════════════════════════════════
        public class ModuleExportConfig : UCL.Core.JsonLib.UnityJsonSerializable, UCLI_IsEnable
        {
            // UCLI_IsEnable implementation: directly mapped to the m_ExportModule field
            public bool IsEnable { get => m_ExportModule; set => m_ExportModule = value; }

            /// <summary>true means this module should be exported and installed to PersistentDataPath. Defaults to true.</summary>
            public bool m_ExportModule = true;

            public ModuleExportConfig() { } // Default constructor for JSON deserialization
        }

        public class Config : UCL.Core.JsonLib.UnityJsonSerializable
        {
            //public State m_State = State.Main;
            //public string m_CurrentEditModule = string.Empty;

            /// <summary>
            /// All BuiltinModules are in StreammingAssets
            /// </summary>
            public List<string> m_BuiltinModules = new List<string>();
            /// <summary>
            /// Modules to be exported
            /// </summary>
            public Dictionary<string, ModuleExportConfig> m_ExportModules = new();


            /// <summary>
            /// All module in this list will be loaded
            /// </summary>
            public UCL_ModulePlaylist m_Playlist = new UCL_ModulePlaylist();

            /// <summary>
            /// Force install all modules every time starting in Editor
            /// </summary>
            public bool m_ForceInstallInEditor = false;


            public string m_Version = "1.0.0";

            /// <summary>
            /// Used to set AssetGroup sorting
            /// </summary>
            public Dictionary<string, int> m_AssetGroupSortingOrder = new ();



            public EditorInstallMode m_EditorInstallMode = EditorInstallMode.Default;

            public void OnGUI(UCL_ObjectDictionary iDataDic)
            {
                foreach(var group in UCLI_Asset.GetAssetGroups())
                {
                    if (!m_AssetGroupSortingOrder.ContainsKey(group))
                    {
                        m_AssetGroupSortingOrder[group] = 99;
                    }
                }
                UCL_GUILayout.DrawObjExSetting aDrawObjExSetting = new UCL_GUILayout.DrawObjExSetting();
                aDrawObjExSetting.OnShowField = () =>
                {
                    //GUILayout.Label("Config", UCL_GUIStyle.LabelStyle);
                    if (GUILayout.Button("Zip all modules", UCL_GUIStyle.ButtonStyle))
                    {
                        Debug.LogError($"{UCL_AssetPath.GetPath(UCL_AssetType.BuiltinModules)}");
                        UCL_ModulePath.ZipAllModules();
                    }
                    if (GUILayout.Button("Remove all Zip modules", UCL_GUIStyle.ButtonStyle))
                    {
                        UCL_ModulePath.RemoveAllZipAllModules();
                    }
                };

                var aParams = new UI.UCL_GUILayout.DrawObjectParams(iDataDic, UCL_CodeLocalize.Get("ModuleService Config"),
                    iDrawObjExSetting: aDrawObjExSetting);
                UCL_GUILayout.DrawField(this, aParams);
                //UCL_GUILayout.DrawField(this, iDataDic, "ModuleService Config", iDrawObjExSetting: aDrawObjExSetting);

            }
            public override JsonData SerializeToJson()
            {
//#if UNITY_EDITOR
//#endif

                return base.SerializeToJson();
            }
            public UCL_Module CreateModule(string iID, UCL_ModuleEditType iModuleEditType, UCL_Module.Config config)
            {
                UCL_Module aModule = new UCL_Module();
                aModule.Init(iID, iModuleEditType, config);
                aModule.Save();
                return aModule;
            }
        }

        public Config ModuleConfig => m_Config;
        public UCL_ModulePathConfig m_PathConfig = new UCL_ModulePathConfig();
        public bool LoadingPlaylist => m_LoadingPlaylist;


        protected bool m_Initialized = false;
        protected bool m_LoadingConfig = false;
        protected bool m_LoadingPlaylist = false;
        protected Config m_Config = new Config();

        protected string m_NewModuleName = "New Module";
        protected UCL_Module m_CurEditModule = null;
        //protected List<UCL_Module> m_CurEditModuleDependenciesModules = new List<UCL_Module>();

        #region Enviroment
        /// <summary>
        /// Each UCL_Asset will have one copy (shared for UCL_Assets with the same ID)
        /// </summary>
        public class AssetConfig
        {
            public static AssetConfig s_CurCreateDataConfig = null;


            /// <summary>
            /// Belongs to UCL_Module
            /// </summary>
            public UCL_Module p_Module;

            public bool Inited { get; private set; }
            public string ID { get; private set; }
            public Type AssetType { get; private set; }
            public object AssetCache { get; set; }

            public bool Exist => p_Module != null;
            /// <summary>
            /// ModuleEntry of this asset
            /// All path-related settings
            /// </summary>
            public UCL_ModulePath.PersistantPath.ModuleEntry ModuleEntry {
                get
                {
                    if(p_Module == null)
                    {
                        Debug.LogError($"{GetType().Name}.ModuleEntry p_Module == null,AssetType:{AssetType.Name}, ID:{ID}");
                        return null;
                    }
                    return p_Module.ModuleEntry;
                }
            }
            /// <summary>
            /// Save path
            /// </summary>
            public string AssetPath => ModuleEntry.GetAssetPath(AssetType, ID);
            public string AssetFolderPath => ModuleEntry.GetAssetFolderPath(AssetType);

            virtual protected string GroupFolderPath => Path.Combine(AssetFolderPath, $".Group");
            virtual protected string GroupIDPath => Path.Combine(GroupFolderPath, ID);
            private string m_GroupID = null;
            /// <summary>
            /// GroupID of this asset
            /// Group for this asset
            /// </summary>
            virtual public string GroupID
            {
                get
                {
                    if (m_GroupID == null)
                    {
                        if (File.Exists(GroupIDPath))
                        {
                            m_GroupID = File.ReadAllText(GroupIDPath);
                        }
                        else
                        {
                            m_GroupID = string.Empty;
                        }
                    }
                    return m_GroupID;
                }
                set
                {
                    ModuleEntry.ThrowIfBuildReadOnly("set_GroupID");//build 唯讀守衛 (StreamingReadOnly 模組不可寫，放 try 外確保 fail-loud throw 不被吞)
                    try
                    {
                        m_GroupID = value;
                        if (string.IsNullOrEmpty(m_GroupID))
                        {
                            if (File.Exists(GroupIDPath))
                            {
                                File.Delete(GroupIDPath);
                            }
                        }
                        else
                        {
                            Directory.CreateDirectory(GroupFolderPath);
                            File.WriteAllText(GroupIDPath, m_GroupID);
                        }
                    }
                    catch(System.Exception ex)
                    {
                        Debug.LogError($"{GetType().Name}.set GroupID,ID:{ID},GroupID:{value},Exception:{ex}");
                        Debug.LogException(ex);
                    }
                }
            }

            public AssetConfig() { }
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
                    //Debug.LogError(aLog);
                    throw new System.Exception(aLog);
                    //return null;
                }
                string aJsonData = File.ReadAllText(aPath);
                return JsonData.ParseJson(aJsonData);
            }
            public void SaveAsset(JsonData iJson)
            {
                ModuleEntry.ThrowIfBuildReadOnly(nameof(SaveAsset));//build 唯讀守衛 (StreamingReadOnly 模組不可寫)
                var path = AssetPath;
                string folderPath = System.IO.Path.GetDirectoryName(path);
                //Debug.LogError($"path:{path},folderPath:{folderPath}");
                Directory.CreateDirectory(folderPath);
                System.IO.File.WriteAllText(path, iJson.ToJsonBeautify());
            }
            public void DeleteAsset()
            {
                ModuleEntry.ThrowIfBuildReadOnly(nameof(DeleteAsset));//build 唯讀守衛 (StreamingReadOnly 模組不可寫)
                string aPath = AssetPath;
                if (File.Exists(aPath))
                {
                    File.Delete(aPath);
                }
            }
        }

        /// <summary>
        /// Shared for each type of UCL_Asset
        /// </summary>
        public class AssetsCache
        {
            public Dictionary<string, AssetConfig> m_AssetConfigDic = new Dictionary<string, AssetConfig>();
            /// <summary>
            /// All GroupIDs within this UCL_Asset
            /// </summary>
            public List<string> m_GroupIDs = new List<string>();

            public Type m_AssetType;
            public AssetsCache() { }
            public AssetsCache(Type iAssetType, List<UCL_Module> iLoadedModules)
            {
                m_AssetType = iAssetType;
                HashSet<string> aGroupIDs = new HashSet<string>();
                foreach (var aModule in iLoadedModules)
                {
                    var aMeta = aModule.GetAssetMeta(iAssetType.Name);
                    foreach(var groupID in aMeta.m_Groups.Keys)
                    {
                        aGroupIDs.Add(groupID);
                    }
                }
                m_GroupIDs = aGroupIDs.ToList();
            }

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
        /// Currently loaded modules. When reading UCL_Assets, modules are checked in order (if IDs are the same, the asset from the module with higher priority is selected).
        /// </summary>
        protected List<UCL_Module> m_LoadedModules = new List<UCL_Module>();
            /// <summary>
            /// Cached module information
            /// </summary>
            protected Dictionary<string, UCL_Module> m_ModuleCache = new();
            /// <summary>
            /// ID Cache
            /// </summary>
            protected Dictionary<string, (System.DateTime timeStamp, List<string> list)> m_IDsCache = new();
        protected Dictionary<string, AssetsCache> m_AssetsCacheDic = new ();
        /// <summary>
        /// Per-module ModuleEditType 覆寫表 (PC 免安裝直讀用)。
        /// 物理意義：載入鏈預設用全域 ModuleEditType 解析路徑；此表存在某模組 ID 時改用表內型別 (StreamingReadOnly)。
        /// 數值影響：僅 build 啟動且 PC + opt-in 模組會被填入；空表 (其他平台 / 沒 opt-in) → 載入鏈行為與今天逐位元一致。
        /// </summary>
        protected Dictionary<string, UCL_ModuleEditType> m_EditTypeOverride = new();
        #endregion

        /// <summary>
        /// 解析某模組 ID 實際該用的 ModuleEditType：有 per-module 覆寫 (m_EditTypeOverride) 則用之，否則回全域 ModuleEditType。
        /// </summary>
        /// <param name="iModuleID">模組 ID</param>
        /// <returns>該模組載入時使用的 ModuleEditType</returns>
        protected UCL_ModuleEditType ResolveEditType(string iModuleID)
        {
            if (iModuleID != null && m_EditTypeOverride.TryGetValue(iModuleID, out var aOverride))
            {
                return aOverride;
            }
            return ModuleEditType;
        }

        /// <summary>
        /// 此模組是否為唯讀（內建免安裝直讀模組）。
        /// 物理意義：模組解析型別為 StreamingReadOnly 時為唯讀 — 僅 build + opt-in 免安裝模組會成立 (Editor 內 override 為空 → 永遠可編輯，保留 dev 維護源檔流程)。
        /// 數值影響：唯讀模組在編輯頁禁用 Save/Install/Zip/Delete、改提供 Fork；write guard (ThrowIfBuildReadOnly) 為第二層兜底。
        /// </summary>
        /// <param name="iModuleID">模組 ID</param>
        /// <returns>true = 唯讀（StreamingReadOnly）</returns>
        public bool IsModuleReadOnly(string iModuleID)
        {
            return ResolveEditType(iModuleID) == UCL_ModuleEditType.StreamingReadOnly;
        }

        /// <summary>
        /// 取得清單頁該顯示的所有可選模組 ID：合併「Runtime(persistentDataPath) 掃描」與「StreamingReadOnly 免安裝模組(m_EditTypeOverride)」並去重。
        /// 物理意義：免安裝模組(如 Core)的原始檔在 StreamingAssets、不在 persistentDataPath，原本 GetAllModuleIDs 掃不到 → 清單漏掉它。
        ///          這裡把免安裝模組也納入(供模組製作者唯讀參考)；同 ID 雙來源(Runtime 舊副本 + StreamingReadOnly)只呈現一筆，
        ///          read-only 與否一律由 ResolveEditType 決定(override 優先)，故去重後仍正確判讀唯讀。
        /// 數值影響：override 為空(其他平台 / Editor) → 等同原 GetAllModuleIDs，逐位元相容。
        /// </summary>
        public IList<string> GetAllListableModuleIDs(bool iUseCache = true)
        {
            var aResult = new List<string>();
            var aSeen = new HashSet<string>();
            void AddID(string iID)
            {
                if (!string.IsNullOrEmpty(iID) && aSeen.Add(iID)) aResult.Add(iID);
            }
            // 先納入免安裝(StreamingReadOnly)模組 — 確保它們即使不在 persistentDataPath 也會出現在清單
            foreach (var aID in m_EditTypeOverride.Keys) AddID(aID);
            // 再納入 Runtime(persistentDataPath) 掃描結果
            var aRuntimeIDs = GetAllModuleIDs(iUseCache);
            if (aRuntimeIDs != null)
            {
                foreach (var aID in aRuntimeIDs) AddID(aID);
            }
            return aResult;
        }

        /// <summary>
        /// 一鍵 Fork：把唯讀(StreamingReadOnly)模組整包複製成 persistentDataPath 裡一份新的可編輯 Runtime 模組。
        /// 物理意義：來源 = 唯讀模組 RootFolder(直讀路徑，只讀)；目的 = Runtime.GetModuleEntry(newID).RootFolder(persistentDataPath，可寫)。
        ///          複製後改寫新模組 Config 的 ID + 強制 m_PCDirectStreaming=false(衍生模組是可寫 Runtime，不繼承免安裝唯讀旗標)。
        /// 數值影響：產生一份可編輯模組；不自動掛進 PlayList(只建檔)。
        /// </summary>
        /// <param name="iSrcID">來源模組 ID(唯讀)</param>
        /// <param name="iNewID">新模組 ID</param>
        /// <returns>fork 出的可編輯模組；失敗回 null</returns>
        virtual public UCL_Module ForkModule(string iSrcID, string iNewID)
        {
            if (string.IsNullOrEmpty(iNewID))
            {
                Debug.LogError($"{GetType().Name}.ForkModule iNewID 為空");
                return null;
            }
            var aRuntimeEntry = UCL_ModulePath.PersistantPath.Runtime.GetModuleEntry(iNewID);
            string aDst = aRuntimeEntry.RootFolder;
            if (Directory.Exists(aDst))//新 ID 與既有 Runtime 模組衝突
            {
                Debug.LogError($"{GetType().Name}.ForkModule 新模組 ID '{iNewID}' 已存在於 persistentDataPath，請改名。aDst:{aDst}");
                return null;
            }
            // 來源用 ResolveEditType 解析(唯讀模組 → StreamingReadOnly RootFolder)
            var aSrcEntry = UCL_ModulePath.PersistantPath.GetModulesEntry(ResolveEditType(iSrcID)).GetModuleEntry(iSrcID);
            string aSrc = aSrcEntry.RootFolder;
            if (!Directory.Exists(aSrc))
            {
                Debug.LogError($"{GetType().Name}.ForkModule 來源模組 '{iSrcID}' RootFolder 不存在:{aSrc}");
                return null;
            }
            try
            {
                UCL.Core.FileLib.Lib.CopyDirectory(aSrc, aDst);//整包複製進 persistentDataPath
                // 載入 fork 出的模組(Runtime 可寫)，改寫 Config
                var aModule = LoadModule(iNewID, UCL_ModuleEditType.Runtime);
                aModule.ID = iNewID;
                aModule.m_Config.m_ID = iNewID;
                aModule.m_Config.m_PCDirectStreaming = false;//衍生模組是可寫 Runtime，清掉免安裝旗標
                aModule.Save();//寫回 persistentDataPath(Runtime → write guard 不擋)
                m_ModuleIDs = null;//強制刷新清單快取
                m_ModuleNames = null;
                Debug.Log($"{GetType().Name}.ForkModule OK: '{iSrcID}' → '{iNewID}' @ {aDst}");
                return aModule;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"{GetType().Name}.ForkModule FAILED src:{iSrcID},new:{iNewID},Exception:{ex}");
                Debug.LogException(ex);
                return null;
            }
        }

        /// <summary>
        /// 當前 runtime 平台是否支援「PC 免安裝直讀 StreamingAssets」。
        /// 物理意義：僅 Standalone(Windows/OSX/Linux) Player 的 streamingAssetsPath 是真實磁碟資料夾、可同步 File IO 直讀；
        ///          Android/iOS/WebGL 等 StreamingAssets 壓在安裝包內，同步 File IO 讀不到 → 不支援、必須走原安裝流程。
        /// 數值影響：false 時 InitAsync 一律忽略 m_PCDirectStreaming 旗標 (其他平台 100% 原邏輯)。
        /// </summary>
        /// <returns>true = PC(Standalone) build，可走免安裝直讀</returns>
        protected static bool IsPCDirectStreamingPlatform()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.LinuxPlayer:
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Wait for UCL_ModuleService initialization to complete
        /// </summary>
        /// <param name="iToken"></param>
        /// <returns></returns>
        // 區塊職責：等 ModuleService 初始化完成。
        // 物理意義：`m_Initialized` 只在 InitAsync 的**最後一行**才設，所以 InitAsync 只要沒走完
        //          （throw / 卡在某個 await），這裡就是無限期等待。舊版無聲無息 ——
        //          呼叫端只會表現成「那個功能沒動」，而沒有任何一行字說明它在等什麼
        //          （血證：UCL_AgentCommandWatcher 曾把 update 訂閱掛在本函式之後，
        //           結果整條 AgentCommand 通道靜默死亡，2026-08-13）。
        // 數值影響：**語意不變 —— 逾時只出聲、不放棄等待**（提早返回會讓呼叫端拿到未初始化的
        //          ModuleService 去做事，那是把一個看得見的等待換成一個看不見的錯誤）。
        //          預設 30s 警告一次，之後每 60s 再叫一次，避免一次性警告被洗掉後就再也沒人知道。
        const double WaitInitWarnSeconds = 30.0;
        const double WaitInitRepeatWarnSeconds = 60.0;

        public static async UniTask WaitUntilInitialized(CancellationToken iToken)
        {
            if (Initialized)
            {
                return;
            }
            var moduleService = UCL_ModuleService.Ins;
            // ⚠ 本檔在 AssetCore（runtime 組件）—— 時間源不可用 UnityEditor.EditorApplication，
            //   否則 build 編不過。Time.realtimeSinceStartup 在 Editor（含非播放中）與 runtime 都會前進。
            double startTime = Time.realtimeSinceStartup;
            double nextWarnAt = startTime + WaitInitWarnSeconds;
            await UniTask.WaitUntil(() =>
            {
                if (moduleService.m_Initialized) return true;
                double now = Time.realtimeSinceStartup;
                if (now >= nextWarnAt)
                {
                    nextWarnAt = now + WaitInitRepeatWarnSeconds;
                    Debug.LogError($"[UCL_ModuleService] WaitUntilInitialized 已等待 {now - startTime:0}s 仍未初始化完成"
                        + " —— InitAsync 沒走完（m_Initialized 在其最後一行才設）。"
                        + " 等待會繼續，但依賴它的功能在此之前都不會動作。");
                }
                return false;
            }, cancellationToken: iToken);
        }
        /// <summary>
        /// Init UCL_ModuleService
        /// Currently assumed to have a core configuration module located in StreamingAssets.
        /// Editable only in the Editor; the built version can only be read (using UnityWebRequest to read StreamingAssets to avoid cross-platform issues).
        /// </summary>
        virtual protected void Init()
        {
            InitAsync().Forget();
        }

        virtual protected async UniTask InitAsync()
        {
            Debug.Log($"{GetType().Name}.InitAsync");
            //await UCL.Core.Page.UCL_OptionPage.ShowAlertAsync("UCL_ModuleService.InitAsync()", "");
            if (Application.isEditor)
            {//Edit mode can be dynamically adjusted in Editor
                //ModuleEditType = UCL_ModuleEditType.Builtin;
                //ModuleEditType = UCL_ModuleEditType.Runtime;
                Ins.m_PathConfig.m_ModuleEditType = ModuleEditType;//Sync to PathConfig
            }
            else//not in Editor, always set to Runtime
            {
                ModuleEditType = UCL_ModuleEditType.Runtime;
            }
            var aModulesPath = m_PathConfig.ModulesPath;
            //Debug.LogError($"aModulesPath:{aModulesPath},FileSystemRootPath:{m_PathConfig.FileSystemRootPath}");
            //if (!Directory.Exists(aModulesPath))//Not Installed
            //{
            //    Directory.CreateDirectory(aModulesPath);
            //}
            Directory.CreateDirectory(aModulesPath);

            //Debug.LogWarning($"1 m_Config.m_BuiltinModules:{m_Config.m_BuiltinModules.AllFieldToString()}");
            //Debug.LogError("InitAsync()");
            await LoadConfig();
            var exportModules = m_Config.m_ExportModules;
            Debug.Log($"exportModules:{exportModules.AllFieldToString()}");

            if (Application.isEditor)
            {
                if (m_Config.m_ForceInstallInEditor)
                {
                    foreach (var aModuleID in exportModules.Keys)//Check if all builtin modules installed
                    {
                        var exportConfig = exportModules[aModuleID];
                        if (exportConfig.m_ExportModule)
                        {
                            var aModule = LoadModule(aModuleID, UCL_ModuleEditType.Runtime);
                            await aModule.Install();
                        }
                    }
                }
                else
                {
                    switch (ModuleEditType)
                    {
                        case UCL_ModuleEditType.Runtime:
                            {//check and install if in Runtime mode
                                foreach (var aModuleID in exportModules.Keys)//Check if all builtin modules installed
                                {
                                    var exportConfig = exportModules[aModuleID];
                                    if (exportConfig.m_ExportModule)
                                    {
                                        //Debug.LogWarning($"ModuleID:{aModuleID}, CheckAndInstall");
                                        var aModule = LoadModule(aModuleID, UCL_ModuleEditType.Runtime);
                                        await aModule.CheckAndInstall();
                                    }
                                    //aTasks.Add(aModule.CheckAndInstall());
                                }
                                break;
                            }
                        case UCL_ModuleEditType.Builtin:
                            {
                                break;
                            }
                    }
                }

            }
            else
            {
                // 區塊職責：build (非 Editor) 啟動時對每個 export 模組分流 — 「PC 免安裝直讀」 vs 「原本 zip+install」。
                // 物理意義：僅在 PC(Standalone) 平台 + 該模組 opt-in (m_PCDirectStreaming) 時，登記 per-module override 為 StreamingReadOnly
                //          並「跳過」CheckAndInstall(不解壓、不複製到 PersistentDataPath)，原始檔已由 build 前置複製進 StreamingAssets；
                //          其餘所有情況 (非 PC 平台 / 沒 opt-in) 一律走原本 LoadModule(Runtime)+CheckAndInstall，行為與今天逐位元一致。
                // 數值影響：m_EditTypeOverride 在此填入，必須早於下方 playList.LoadPlaylist，載入鏈才會用對的 per-module 型別解析路徑。
                bool aPCDirectSupported = IsPCDirectStreamingPlatform();
                //List<UniTask> aTasks = new List<UniTask>();
                foreach (var aModuleID in exportModules.Keys)//Check if all builtin modules installed
                {
                    var exportConfig = exportModules[aModuleID];
                    if (exportConfig.m_ExportModule)
                    {
                        // 免安裝旗標在「模組自身 Config」。免安裝模組的原始 Config.json 已由 build 前置複製進 StreamingAssets，
                        // 故從 StreamingReadOnly 路徑讀其 Config (檔不存在 → GetConfig 回預設 false → 視為非免安裝)。
                        bool aIsDirect = false;
                        if (aPCDirectSupported)
                        {
                            var aSroConfig = UCL_ModulePath.PersistantPath.StreamingReadOnly.GetModuleEntry(aModuleID).GetConfig();
                            aIsDirect = (aSroConfig != null && aSroConfig.m_PCDirectStreaming);
                        }
                        // 只有「PC 平台 + 該模組開啟 m_PCDirectStreaming」才走免安裝；其餘走原安裝流程 (鐵則：其他平台 100% 原邏輯)
                        if (aIsDirect)
                        {
                            // 登記 per-module 型別覆寫；跳過 CheckAndInstall (原始檔直接在 StreamingAssets 直讀)
                            m_EditTypeOverride[aModuleID] = UCL_ModuleEditType.StreamingReadOnly;
                            Debug.Log($"{GetType().Name}.InitAsync ModuleID:{aModuleID} 走 PC 免安裝直讀 (StreamingReadOnly)，跳過 CheckAndInstall");
                        }
                        else
                        {
                            //Debug.LogWarning($"ModuleID:{aModuleID}, CheckAndInstall");
                            var aModule = LoadModule(aModuleID, UCL_ModuleEditType.Runtime);
                            await aModule.CheckAndInstall();
                        }
                    }
                    //aTasks.Add(aModule.CheckAndInstall());
                }
                //await UniTask.WhenAll(aTasks);
            }
            //TODO Clear module information cache after installation
            //m_ModuleCache.Clear();
            m_ModuleIDs = null;
            m_ModuleNames = null;
            //LoadModuleAndDependencies(UCL_ModuleEntry.CoreModuleID, new());//Load Core

            var playList = m_Config.m_Playlist;
            playList.LoadPlaylist(false);

            //m_LoadedModules.Reverse();//Modules that are loaded later will overwrite the previous modules(if asset have same ID)
            //Cheack and Install all Builtin Module to PersistantData path
            m_Initialized = true;
        }

        private List<string> m_AssetGroups = null;
        public List<string> GetAssetGroups()
        {
            if (m_AssetGroups == null)
            {
                var assetGroupSortingOrder = m_Config.m_AssetGroupSortingOrder;
                foreach (var group in UCLI_Asset.GetAssetGroups())
                {
                    if (!assetGroupSortingOrder.ContainsKey(group))
                    {
                        assetGroupSortingOrder[group] = 99;
                    }
                }
                int GetGroupIndex(string iGroup)
                {
                    if (!assetGroupSortingOrder.ContainsKey(iGroup))
                    {
                        return int.MaxValue;
                    }
                    return assetGroupSortingOrder[iGroup];
                }
                m_AssetGroups = UCLI_Asset.GetAssetGroups();
                m_AssetGroups.Sort((a, b) =>
                {
                    return GetGroupIndex(a).CompareTo(GetGroupIndex(b));
                });
            }


            return m_AssetGroups;
        }
        public UCL_AssetCommonMeta GetAssetMeta(string iTypeName)
        {
            return CurModule.GetAssetMeta(iTypeName);
        }


        private IList<string> m_ModuleIDs = null;
        private IList<string> m_ModuleNames = null;
        private System.DateTime m_ModuleCacheTime = System.DateTime.MinValue;
        /// <summary>
        /// get all modules ID of current ModuleEditType
        /// Get all module IDs (based on ModuleEditType)
        /// </summary>
        /// <returns></returns>
        public IList<string> GetAllModuleIDs(bool iUseCache = true)
        {
            const float UpdateInterval = 0.5f;
            if (m_ModuleIDs == null || !iUseCache || (System.DateTime.Now - m_ModuleCacheTime).TotalSeconds > UpdateInterval)
            {
                m_ModuleNames = null;//Clear cache
                m_ModuleIDs = UCL_ModulePath.PersistantPath.GetModulesEntry(ModuleEditType).GetAllModulesID();
                m_ModuleCacheTime = System.DateTime.Now;
            }
            return m_ModuleIDs;
            //return UCL_ModulePath.GetAllModulesID(ModuleEditType);
        }
        /// <summary>
        /// All module names
        /// </summary>
        /// <returns></returns>
        public IList<string> GetAllModuleNames()
        {
            if (m_ModuleNames == null)
            {
                m_ModuleNames = new List<string>();
                foreach (var id in GetAllModuleIDs())
                {
                    var module = GetModule(id);
                    string name = module.m_Config.m_Title;
                    if (!name.Equals(id))
                    {
                        name = $"{name}({id})";
                    }
                    m_ModuleNames.Add(name);
                }
            }
            return m_ModuleNames;
        }
        /// <summary>
        /// Get currently loaded module by ID
        /// </summary>
        /// <param name="iID"></param>
        /// <returns></returns>
        public UCL_Module GetLoadedModule(string iID)
        {
            foreach(var aModule in m_LoadedModules)
            {
                if(aModule.ID == iID)
                {
                    return aModule;
                }
            }
            return m_CurEditModule;
        }
        /// <summary>
        /// Returns all editable Asset IDs
        /// (Only includes assets from the currently edited module)
        /// </summary>
        /// <param name="iAssetType"></param>
        /// <returns></returns>
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
        public List<string> GetAllAssetIDs(Type iAssetType, bool iUseCache = false)
        {
            var aModules = LoadedModules;
            string key = iAssetType.Name;

            bool refresh = iUseCache;
            if (!refresh)
            {
                if (m_IDsCache.ContainsKey(key))
                {
                    var timeStamp = m_IDsCache[key].timeStamp;
                    if ((System.DateTime.Now - timeStamp).TotalSeconds >= 0.3f)
                    {
                        refresh = true;
                    }
                }
                else
                {
                    refresh = true;
                }
            }

            if (refresh)
            {
                var aIDSet = new HashSet<string>();
                foreach (var aModule in aModules)
                {
                    var aIDs = aModule.ModuleEntry.GetAllAssetsID(iAssetType);

                    foreach (var aID in aIDs)
                    {
                        aIDSet.Add(aID);
                    }
                }
                m_IDsCache[key] = (System.DateTime.Now, aIDSet.ToList());
            }
            //Debug.Log($"GetAllAssetsID aModules:{aModules.ConcatString(iModule => iModule.ID)}");


            return m_IDsCache[key].list;
        }

        public List<UCL_Module> LoadedModules => m_LoadedModules;
        /// <summary>
        /// Fetch AssetsCache for specified UCL_Asset
        /// </summary>
        /// <param name="iAssetType">typeof(UCL_Asset)</param>
        /// <returns></returns>
        public AssetsCache GetAssetsCache(Type iAssetType)
        {
            string key = iAssetType.Name;
            if (!m_AssetsCacheDic.ContainsKey(key))
            {
                m_AssetsCacheDic[key] = new AssetsCache(iAssetType, m_LoadedModules);
            }
            return m_AssetsCacheDic[key];
        }
        public void ClearAssetsCache(Type iAssetType)
        {
            string key = iAssetType.Name;
            if (m_AssetsCacheDic.ContainsKey(key))
            {
                m_AssetsCacheDic.Remove(key);
            }
            if (m_IDsCache.ContainsKey(key))
            {
                m_IDsCache.Remove(key);
            }
        }
        public void ClearAssetsCache(Type iAssetType, string iID)
        {
            string key = iAssetType.Name;
            if (!m_AssetsCacheDic.ContainsKey(key))
            {
                return;
            }
            var aAssetsCache = m_AssetsCacheDic[key];
            aAssetsCache.ClearAssetsCache(iID);
        }
        /// <summary>
        /// Create Config for the current edited module to ensure save location is correct
        /// </summary>
        /// <param name="iAssetType"></param>
        /// <param name="iID"></param>
        /// <returns></returns>
        public AssetConfig CreateAssetConfig(Type iAssetType, string iID)
        {
            if(m_CurEditModule == null)
            {
                Debug.LogError($"{GetType().Name}.CreateAssetConfig, m_CurEditModule == null");
                return GetAssetConfig(iAssetType, iID);
            }
            
            //ClearAssetsCache(iAssetType, iID);
            var aAssetsCache = GetAssetsCache(iAssetType);
            // Clear current Config before saving to ensure the save location is the current edited module
            aAssetsCache.ClearAssetsCache(iID);

            var aConfig = aAssetsCache.GetAssetConfig(iID);
            aConfig.Init(m_CurEditModule, iAssetType, iID);
            return aConfig;
        }
        /// <summary>
        /// Fetch AssetConfig for specified UCL_Asset
        /// </summary>
        /// <param name="iAssetType">typeof(UCL_Asset)</param>
        /// <param name="iID">ID of UCL_Asset</param>
        /// <returns></returns>
        public AssetConfig GetAssetConfig(Type iAssetType, string iID)
        {
            var aAssetsCache = GetAssetsCache(iAssetType);
            var aConfig = aAssetsCache.GetAssetConfig(iID);
            if (!aConfig.Inited)
            {
                for(int i = m_LoadedModules.Count - 1; i >= 0; i--)// According to module override rules, search from the last modules first
                {
                    var aModule = m_LoadedModules[i];
                    if (aModule.ModuleEntry.ContainsAsset(iAssetType, iID))
                    {
                        aConfig.Init(aModule, iAssetType, iID);
                        return aConfig;//return config
                    }
                }
                //foreach (UCL_Module aModule in LoadedModules)
                //{
                //    if (aModule.ModuleEntry.ContainsAsset(iAssetType, iID))
                //    {
                //        aConfig.Init(aModule, iAssetType, iID);
                //        return aConfig;//return config
                //    }
                //}
                //Debug.LogError($"GetAssetConfig iAssetType:{iAssetType},iID:{iID}, Asset not exist!!");
                aConfig.Init(CurModule, iAssetType, iID);
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
        public void ClearCache()
        {
            foreach (UCL_Module aModule in LoadedModules)
            {
                aModule.ClearCache();
            }
        }
        virtual public void SaveConfig()
        {
            m_AssetGroups = null;//Clear cache
            if (Application.isEditor)//Check streamming assets for BuiltinModules
            {
                if(ModuleEditType == UCL_ModuleEditType.Builtin)
                {
                    string aModulesPath = m_PathConfig.ModulesPath;
                    Directory.CreateDirectory(aModulesPath);

                    //Debug.LogError($"aBuiltinModulesPath:{aBuiltinModulesPath}");
                    //var aDirs = UCL.Core.FileLib.Lib.GetDirectories(aModulesPath, iSearchOption: SearchOption.TopDirectoryOnly, iRemoveRootPath: true);
                    var aAllModulesID = GetAllModuleIDs();
                    //Debug.LogError($"aDirs:{aDirs.ConcatString()}");


                    m_Config.m_BuiltinModules = aAllModulesID.ToList();
                    if (!m_Config.m_BuiltinModules.Contains(UCL_ModuleEntry.CoreModuleID))//Create Core Module!!
                    {
                        m_Config.CreateModule(UCL_ModuleEntry.CoreModuleID, UCL_ModuleEditType.Builtin, null);
                        m_Config.m_BuiltinModules.Add(UCL_ModuleEntry.CoreModuleID);
                    }
                    foreach(var moduleId in m_Config.m_BuiltinModules)
                    {
                        if (!m_Config.m_ExportModules.ContainsKey(moduleId))
                        {
                            m_Config.m_ExportModules[moduleId] = new ModuleExportConfig();
                        }
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

        /// <summary>
        /// Load Modules and its dependencies
        /// Load the specified module and its dependent modules
        /// </summary>
        /// <param name="modulePlayist"></param>
        /// <returns></returns>
        public async UniTask<Dictionary<string, UCL_Module>> LoadModulePlaylistAsync(UCL_ModulePlaylist modulePlayist, CancellationToken token)
        {
            Dictionary<string, UCL_Module> result = null;
            try
            {
                if (m_LoadingPlaylist)//Loading
                {
                    await UniTask.WaitUntil(() => m_LoadingPlaylist, cancellationToken: token);
                    token.ThrowIfCancellationRequested();
                }
                m_LoadingPlaylist = true;
                await UCL_ModuleService.WaitUntilInitialized(token);
                token.ThrowIfCancellationRequested();

                result = LoadModulePlaylist(modulePlayist, false);
                await OnLoadedModuleAsync(token);
            }
            catch (OperationCanceledException)
            {

            }catch(System.Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                m_LoadingPlaylist = false;
            }

            return result;
        }


        /// <summary>
        /// Load Modules and its dependencies
        /// Load the specified module and its dependent modules
        /// </summary>
        /// <param name="modulePlayist"></param>
        /// <param name="loadDependencies">Whether to load dependent modules (currently only loaded during editing)</param>
        /// <returns></returns>
        public Dictionary<string, UCL_Module> LoadModulePlaylist(UCL_ModulePlaylist modulePlayist, bool loadDependencies)
        {
            OnLoadModule?.Invoke();

            m_LoadedModules.Clear();
            m_ModuleCache.Clear();
            m_AssetsCacheDic.Clear();
            m_IDsCache.Clear();

            UCL_BundleService.UnloadAllAssetBundles(true);//Unload All AssetBundles
            Resources.UnloadUnusedAssets();

            var aLoadedModules = new Dictionary<string, UCL_Module>();
            var playList = modulePlayist.EnablePlaylist.ToList();
            //playList.Reverse();
            foreach (var aModule in playList)
            {
                LoadModuleAndDependencies(aModule.ID, aLoadedModules);
            }
            //m_LoadedModules.Reverse();//reverse

            OnLoadedModule?.Invoke();
            return aLoadedModules;
        }
        protected void SetCurrentEditModule(string iModuleID)
        {
            ClearCurrentEditModule();
            UCL_ModulePlaylist aModulePlaylist = new UCL_ModulePlaylist(iModuleID);
            var aLoadedModules = aModulePlaylist.LoadPlaylist(true);
            m_CurEditModule = aLoadedModules[iModuleID];
            //if(!m_LoadedModules.IsNullOrEmpty())
            //{
            //    m_CurEditModule = m_LoadedModules[0];
            //}
            //m_CurEditModule = LoadModuleAndDependencies(iModuleID, new HashSet<string>());//m_Config.LoadModule(iModuleID, AssetType);

            Debug.Log($"m_LoadedModules:{m_LoadedModules.ConcatToString(iModule => iModule.ID)}");
        }

        /// <summary>
        /// Returns the UCL_Module corresponding to the ID
        /// </summary>
        /// <returns></returns>
        public UCL_Module GetModule(string iID, bool iUseCache = true)
        {
            if (!iUseCache)
            {
                return LoadModule(iID, ResolveEditType(iID));
            }
            if (!m_ModuleCache.ContainsKey(iID))
            {
                m_ModuleCache[iID] = LoadModule(iID, ResolveEditType(iID));
            }
            return m_ModuleCache[iID];
            //m_ModuleCache
            //UCL_ModuleEditType type = Application.isEditor
            //var aModule = LoadModule(iID, ModuleEditType);

            //return aModule;
        }

        protected UCL_Module LoadModule(string iID, UCL_ModuleEditType iModuleEditType)
        {
            UCL_Module aModule = new UCL_Module();
            aModule.Load(iID, iModuleEditType);
            return aModule;
        }
        /// <summary>
        /// Load module (excluding dependencies)
        /// </summary>
        /// <param name="iModuleID"></param>
        /// <param name="iLoadedModules"></param>
        /// <returns></returns>
        protected UCL_Module LoadModule(string iModuleID, Dictionary<string, UCL_Module> iLoadedModules)
        {
            if (iLoadedModules.ContainsKey(iModuleID))//Already loaded
            {
                return iLoadedModules[iModuleID];
            }
            var aModule = LoadModule(iModuleID, ResolveEditType(iModuleID));
            m_LoadedModules.Add(aModule);
            return aModule;
        }
        /// <summary>
        /// Load module and its dependencies
        /// </summary>
        /// <param name="iModuleID"></param>
        /// <param name="iLoadedModules"></param>
        /// <returns></returns>
        protected UCL_Module LoadModuleAndDependencies(string iModuleID, Dictionary<string, UCL_Module> iLoadedModules)
        {
            try
            {
                if (iLoadedModules.ContainsKey(iModuleID))//Already loaded
                {
                    return iLoadedModules[iModuleID];
                }
                //Debug.LogError($"LoadModuleAndDependencies:{iModuleID},iLoadedModules:{iLoadedModules.Keys.ConcatToString()}");

                var aModule = LoadModule(iModuleID, ResolveEditType(iModuleID));
                iLoadedModules[iModuleID] = aModule;
                //m_LoadedModules.Add(aModule);

                List<UCL_ModuleEntry> aDependenciesModules = aModule.m_Config.m_DependenciesModules;
                if (aDependenciesModules.IsNullOrEmpty())//No extra dependencies modules
                {
                    m_LoadedModules.Add(aModule);
                    return aModule;
                }
                //for (int i = aDependenciesModules.Count - 1; i >= 0; i--)//Load dependencies modules first
                for (int i = 0; i < aDependenciesModules.Count; i++)
                {
                    var aModuleEntry = aDependenciesModules[i];
                    LoadModuleAndDependencies(aModuleEntry.ID, iLoadedModules);
                }
                m_LoadedModules.Add(aModule);
                //m_LoadedModules.Reverse();
                return aModule;
            }
            catch(System.Exception ex)
            {
                Debug.LogError($"LoadModuleAndDependencies iModuleID:{iModuleID}, Exception:{ex}");
                Debug.LogException(ex);
            }
            return null;
        }
        
        virtual public void ResumeState()
        {

            //var aStateStr = PlayerPrefs.GetString(CurStateKey, string.Empty);
            //if (string.IsNullOrEmpty(aStateStr))
            //{
            //    return;
            //}
            //if(Enum.TryParse(typeof(State), aStateStr, out var aState))
            {
                //Debug.LogError($"ResumeState aState:{aState}");
                switch (CurState)
                {
                    case State.EditModule:
                        {
                            EditModule(CurrentEditModuleID);
                            break;
                        }
                }
            }

            //EditModule(m_Config.m_CurrentEditModule);
        }
        virtual public void EditModule(string iModuleID, bool iShowModuleEditPage = true)
        {
            if (string.IsNullOrEmpty(iModuleID))//not Editable
            {
                return;
            }
            SetCurrentEditModule(iModuleID);
            if (iShowModuleEditPage)
            {
                UCL_ModuleEditPage.Create(m_CurEditModule);
            }
            SetState(State.EditModule);
        }
        
        virtual public void SetState(State iState)
        {
            //Debug.LogError($"SetState:{iState}");
            
            CurState = iState;
            //PlayerPrefs.SetString(CurStateKey, $"{iState}");//Record current page state
            //m_Config.m_State = iState;
            switch (iState) 
            {
                case State.Main:
                    {
                        ClearCurrentEditModule();
                        break;
                    }
            }
        }
        /// <summary>
        /// Triggered when the current module is edited
        /// (e.g., when saving or deleting UCL_Asset)
        /// </summary>
        virtual public void OnModuleEdit()
        {
            if(m_CurEditModule == null)
            {
                Debug.LogError($"{GetType().FullName}.OnModuleEdit, m_CurEditModule == null");
                return;
            }
            m_CurEditModule.OnModuleEdit();
        }
        virtual public UCL_Module CreateNewModule(string newModuleName, UCL_Module.Config config)
        {
            var module = m_Config.CreateModule(newModuleName, ModuleEditType, config);
            SaveConfig();
            CurrentEditModuleID = newModuleName;
            return module;
        }
        virtual public void OnGUI(UCL_ObjectDictionary iDataDic)
        {
            if(Application.isEditor)//ModuleEditType Builtin can only edit in Editor
            {
                using (var aScope = new GUILayout.HorizontalScope("box"))
                {
                    
                    GUILayout.Label(UCL_CodeLocalize.Get("EditType"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                    ModuleEditType = UCL_GUILayout.PopupAuto(ModuleEditType, iDataDic.GetSubDic("EditType"));
                }
            }
            using (var aScope = new GUILayout.HorizontalScope("box"))
            {
                //GUILayout.Label("UCL_ModuleService", UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                if(GUILayout.Button(UCL_CodeLocalize.Get("Save Config"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    SaveConfig();
                }
                if (GUILayout.Button(UCL_CodeLocalize.Get("Load Config"), UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
                {
                    LoadConfig().Forget();
                }
#if UNITY_STANDALONE_WIN
                if (GUILayout.Button(UCL_CodeLocalize.Get("Open Module Folder"), UCL_GUIStyle.ButtonStyle))
                {
                    UCL.Core.FileLib.WindowsLib.OpenExplorer(m_PathConfig.RootPath);
                }
#endif
            }
            m_Config.OnGUI(iDataDic.GetSubDic(nameof(m_Config)));

            GUILayout.Box(UCL_CodeLocalize.Get("EditModulewarning"), UCL_GUIStyle.BoxStyle);
            if (GUILayout.Button(UCL_CodeLocalize.Get("Create new module"), UCL_GUIStyle.ButtonStyle))
            {
                UCL_CreateNewModulePage.Create();
            }

            using (var aScope = new GUILayout.HorizontalScope())
            {
                // 區塊職責：模組選單 — 來源改用 GetAllListableModuleIDs(含免安裝 StreamingReadOnly 模組、去重)
                // 物理意義：免安裝模組(如 Core)也會列出供唯讀參考；用 index-based popup 才能在顯示名後綴 [唯讀] 標記又保留正確 ID 選取
                // 數值影響：override 為空(其他平台/Editor) → 等同原本只列 Runtime 模組
                List<string> aModules = new List<string>();
                aModules.Add(string.Empty);//Null
                foreach (var aMID in GetAllListableModuleIDs()) aModules.Add(aMID);

                // 顯示名清單(唯讀模組後綴標記)，與 aModules 同 index
                List<string> aDisplayNames = new List<string>();
                foreach (var aMID in aModules)
                {
                    if (string.IsNullOrEmpty(aMID)) { aDisplayNames.Add(string.Empty); continue; }
                    aDisplayNames.Add(IsModuleReadOnly(aMID) ? $"{aMID}  [{UCL_CodeLocalize.Get("ReadOnly")}]" : aMID);
                }

                bool aCanEdit = !string.IsNullOrEmpty(CurrentEditModuleID);
                if (GUILayout.Button(UCL_CodeLocalize.Get("Edit"), UCL_GUIStyle.GetButtonStyle(aCanEdit? Color.white : Color.red), GUILayout.Width(150)))
                {
                    if (aCanEdit)
                    {
                        EditModule(CurrentEditModuleID);
                        //SetCurrentEditModule(m_Config.m_CurrentEditModule);
                        //UCL_ModuleEditPage.Create(m_CurEditModule);
                    }
                }

                GUILayout.Label(UCL_CodeLocalize.Get("Module ID"), UCL_GUIStyle.LabelStyle, GUILayout.ExpandWidth(false));
                int aCurIndex = aModules.IndexOf(CurrentEditModuleID);
                if (aCurIndex < 0) aCurIndex = 0;
                int aNewIndex = UCL_GUILayout.PopupAuto(aCurIndex, aDisplayNames, iDataDic, "SelectModules");
                if (aNewIndex < 0 || aNewIndex >= aModules.Count) aNewIndex = 0;
                var aID = aModules[aNewIndex];
                if(aID != CurrentEditModuleID)
                {
                    CurrentEditModuleID = aID;
                    SaveConfig();
                }
            }
            GUILayout.Space(UCL_GUIStyle.GetScaledSize(10));

            GUILayout.Box(UCL_CodeLocalize.Get("ModulePlayListTip"), UCL_GUIStyle.BoxStyle);
            if (GUILayout.Button(UCL_CodeLocalize.Get("UCL_ModulePlayListPage"), UCL_GUIStyle.ButtonStyle))
            {
                UCL_ModulePlayListPage.Create();
            }
            GUILayout.Space(UCL_GUIStyle.GetScaledSize(10));
            UCL_GUILayout.DrawObjectData(m_LoadedModules, iDataDic.GetSubDic("LoadedModules"), UCL_CodeLocalize.Get("Loaded modules"));
            UCL_GUILayout.DrawObjectData(m_AssetsCacheDic, iDataDic.GetSubDic("AssetsCache"), "AssetsCache");
        }
    }
}
