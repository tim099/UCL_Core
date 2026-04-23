---
title: UCL_ModuleService API Documentation
description: Complete API reference for the module lifecycle management service, covering initialization, loading, saving, asset caching, and GUI flows.
last_updated: 2026-04-23
target_audience: [AI_Agent, Gameplay_Programmer, Tool_Developer]
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModuleService.cs
namespace: UCL.Core
---

# UCL_ModuleService API Documentation

## 1. System Overview

`UCL_ModuleService` is the **Module Lifecycle Management Core** of the UCL framework, operating as a singleton. It is responsible for:

- **Module Loading, Installation, and Dependency Resolution** — Recursively loads `UCL_Module` and its `m_DependenciesModules`.
- **Asset Path Resolution and Cache Management** — Determines the actual storage path for each `UCL_Asset` via `AssetConfig` / `AssetsCache`.
- **Playlist-Driven Runtime Switching** — Dynamically switches the set of loaded modules based on `UCL_ModulePlaylist`.
- **Editor Workflow Support** — Provides Inspector/GUI operation entry points such as `EditModule`, `CreateNewModule`, etc.
- **Cross-Platform Initialization** — Automatically switches between Builtin / Runtime installation modes based on `Application.isEditor`.

> [!IMPORTANT]
> `UCL_ModuleService` uses a **lazy-loaded singleton** (`Ins` property). The first access automatically creates the instance and calls `Init()`, which starts the asynchronous process `InitAsync()`. Therefore, **initialization is not guaranteed to be complete** immediately after accessing `Ins` in synchronous code. Use `WaitUntilInitialized(token)` to ensure safety.

---

## 2. Enumerations (Enums)

### 2.1 `UCL_ModuleEditType`

```csharp
public enum UCL_ModuleEditType
{
    Builtin,  // Module data stored in StreamingAssets (Read-only, packaged with Build)
    Runtime,  // Module data stored in PersistentDataPath (Read-write, modified at runtime)
}
```

| Value | Description |
|---|---|
| `Builtin` | Internal module, data located in `StreamingAssets`; only editable in Editor. |
| `Runtime` | Runtime module, data located in `PersistentDataPath`; supports player customization and Mods. |

---

### 2.2 `UCL_ModuleService.State`

Controls the **GUI page state** of `UCL_ModuleService`, persisted via `UCL_PlayerPrefs`.

```csharp
public enum State
{
    Main,       // Main Page: Select the module to edit.
    EditModule, // Edit Page: Enter the editing process for a specified module.
}
```

---

### 2.3 `UCL_ModuleService.EditorInstallMode`

Controls the installation behavior of modules in the Editor environment.

```csharp
public enum EditorInstallMode
{
    Default, // Directly copy module folders to the target installation path.
    UnZip,   // Simulate actual device: Extract .zip package before installation.
}
```

---

## 3. Core Static Members

### 3.1 Singleton Access

```csharp
public static UCL_ModuleService Ins { get; }
```

- **Behavior**: Automatically creates an instance and calls `Init()` on first access.
- **Thread Safety**: Main thread only.

---

### 3.2 Status and Initialization

| Member | Type | Description |
|---|---|---|
| `Initialized` | `bool` (static) | `true` if the singleton exists and `m_Initialized == true`. |
| `CurState` | `State` (static) | Current GUI page state, persisted to `UCL_PlayerPrefs`. |
| `ModuleEditType` | `UCL_ModuleEditType` (static) | Current module edit type; switching clears `m_ModuleCache`. |

---

### 3.3 Module References

| Member | Type | Description |
|---|---|---|
| `CurEditModule` | `UCL_Module` (static) | Module currently being edited; returns `Ins.m_CurEditModule`. |
| `CurEditModuleID` | `string` (static) | Current edit module ID; returns `UCL_ModuleEntry.CoreModuleID` if `m_CurEditModule == null`. |
| `ModResourcesPath` | `string` (static) | Resources path of the current edit module; used for reflection calls. |
| `PathConfig` | `UCL_ModulePathConfig` (static) | Access the global path configuration object. |

---

### 3.4 Module Loading Events

```csharp
public static event System.Action OnLoadModule;    // Triggered before module starts loading.
public static event System.Action OnLoadedModule;  // Triggered immediately after synchronous module loading is complete.
```

#### Asynchronous Loading Callbacks

```csharp
public static void AddLoadedModuleFunc(System.Func<CancellationToken, UniTask> func);
public static void RemoveLoadedModuleFunc(System.Func<CancellationToken, UniTask> func);
```

- **Purpose**: Register extra tasks for the asynchronous pipeline after module loading is complete.
- **Execution Order**: After all assets in `UCL_OnModuleLoadedAsset` are executed sequentially according to `m_Order`, all registered `Funcs` are executed in parallel.

---

## 4. Initialization Flow

### `WaitUntilInitialized(CancellationToken)`

```csharp
public static async UniTask WaitUntilInitialized(CancellationToken iToken);
```

- **Purpose**: Wait for `UCL_ModuleService` to fully initialize in an asynchronous context.
- **Recommendation**: All systems relying on module data (e.g., `ClimateSim`, `Earth`) should call this method at startup.

#### Initialization Flowchart

```
Ins (Property Access)
  └─ new UCL_ModuleService()
       └─ Init()
            └─ InitAsync()  [Asynchronous]
                 ├─ Sync ModuleEditType → m_PathConfig
                 ├─ Directory.CreateDirectory(ModulesPath)
                 ├─ await LoadConfig()          ← Read Config JSON
                 ├─ Perform CheckAndInstall / Install based on ExportModules
                 ├─ m_Config.m_Playlist.LoadPlaylist(false)
                 └─ m_Initialized = true
```

---

## 5. Inner Classes

### 5.1 `Config`

The primary configuration container for `UCL_ModuleService`, inheriting from `UnityJsonSerializable`, serialized to the JSON file pointed to by `PathConfig`.

| Field | Type | Description |
|---|---|---|
| `m_BuiltinModules` | `List<string>` | List of all built-in module IDs (located in StreamingAssets). |
| `m_ExportModules` | `Dictionary<string, ModuleExportConfig>` | Modules to be exported/installed and their export settings. |
| `m_Playlist` | `UCL_ModulePlaylist` | Playlist determining which modules will be loaded and executed. |
| `m_ForceInstallInEditor` | `bool` | Whether to force re-installation of all modules in the Editor environment. |
| `m_Version` | `string` | Configuration version number (default `"1.0.0"`). |
| `m_AssetGroupSortingOrder` | `Dictionary<string, int>` | Sorting weight for asset groups (smaller values prioritized, default `99`). |
| `m_EditorInstallMode` | `EditorInstallMode` | Editor installation mode (Default / UnZip). |

#### `Config.CreateModule()`

```csharp
public UCL_Module CreateModule(string iID, UCL_ModuleEditType iModuleEditType, UCL_Module.Config config);
```

- **Purpose**: Creates a new module and immediately calls `UCL_Module.Save()` to write configuration to disk.
- **Return Value**: An initialized and saved `UCL_Module` instance.

---

### 5.2 `ModuleExportConfig`

Implements the `UCLI_IsEnable` interface, controlling whether a single module participates in the export/installation process.

| Field | Type | Description |
|---|---|---|
| `m_ExportModule` | `bool` | `true` indicates this module should be exported and installed. |

---

### 5.3 `AssetConfig`

**Shared per Asset ID**, `AssetConfig` is responsible for resolving the full storage path and owning module of the asset.

| Member | Type | Description |
|---|---|---|
| `p_Module` | `UCL_Module` | The module this asset belongs to; `null` means the asset does not exist. |
| `ID` | `string` | Unique identifier of the asset. |
| `AssetType` | `Type` | C# type of the asset. |
| `AssetCache` | `object` | Arbitrary cache object for external systems to attach. |
| `Exist` | `bool` | `true` if `p_Module != null`. |
| `ModuleEntry` | `UCL_ModulePath.PersistantPath.ModuleEntry` | Path resolver; logs `LogError` if `p_Module == null`. |
| `AssetPath` | `string` | Full storage path (calculated by `ModuleEntry.GetAssetPath`). |
| `AssetFolderPath` | `string` | Folder path for the asset type. |
| `GroupID` | `string` | Group ID of the asset; persisted via `.Group/{ID}` text file during read/write. |
| `Inited` | `bool` | Set to `true` after `Init()` is called. |

#### Main Methods

```csharp
// Initialization: Bind module, asset type, and ID.
public void Init(UCL_Module iModule, Type iAssetType, string iID);

// Read JSON data of the asset (Throws Exception if file does not exist).
public JsonData GetJsonData();

// Serialize JSON data and write to AssetPath (Automatically creates folders).
public void SaveAsset(JsonData iJson);

// Delete the physical file pointed to by AssetPath.
public void DeleteAsset();
```

> [!WARNING]
> `GetJsonData()` **throws `System.Exception`** when the file does not exist, rather than returning `null`. Callers must wrap it in a `try-catch` block.

---

### 5.4 `AssetsCache`

**Shared per `UCL_Asset` type**, `AssetsCache` maintains a dictionary of `AssetConfig` for all assets under that type and a list of group IDs.

| Member | Type | Description |
|---|---|---|
| `m_AssetConfigDic` | `Dictionary<string, AssetConfig>` | Mapping of Asset ID → AssetConfig. |
| `m_GroupIDs` | `List<string>` | List of group IDs aggregated from the metadata of all loaded modules. |
| `m_AssetType` | `Type` | The asset type corresponding to this cache. |

```csharp
// Get (or lazy-create) AssetConfig for the specified ID.
public AssetConfig GetAssetConfig(string iID);

// Remove AssetConfig for the specified ID (used to force path re-resolution).
public void ClearAssetsCache(string iID);
```

---

## 6. Instance Members

### 6.1 Status Fields

| Field | Type | Description |
|---|---|---|
| `m_PathConfig` | `UCL_ModulePathConfig` | Global path configuration; includes `ModulesPath`, `RootPath`, etc. |
| `m_Initialized` | `bool` | Set to `true` after `InitAsync()` is completed. |
| `m_LoadingConfig` | `bool` | Re-entrancy protection flag for configuration loading. |
| `m_LoadingPlaylist` | `bool` | Re-entrancy protection flag for playlist loading. |
| `m_Config` | `Config` | Current deserialized module service configuration. |
| `m_CurEditModule` | `UCL_Module` | Currently edited module (`null` if none selected). |
| `m_LoadedModules` | `List<UCL_Module>` | Ordered list of loaded and enabled modules (later ones override assets with same ID). |
| `m_ModuleCache` | `Dictionary<string, UCL_Module>` | Cache dictionary of Module ID → UCL_Module. |
| `m_IDsCache` | `Dictionary<string, (DateTime, List<string>)>` | Time-sensitive cache of Asset Type Name → (Timestamp, ID List). |
| `m_AssetsCacheDic` | `Dictionary<string, AssetsCache>` | Cache dictionary of Asset Type Name → AssetsCache. |

---

### 6.2 Properties

```csharp
public Config ModuleConfig => m_Config;
public bool LoadingPlaylist => m_LoadingPlaylist;
public List<UCL_Module> LoadedModules => m_LoadedModules;

// Returns m_CurEditModule; if null, returns the last module in m_LoadedModules.
public UCL_Module CurModule { get; }
```

---

## 7. Public Methods

### 7.1 Asset ID Query

#### `GetAllEditableAssetsID(Type)`

```csharp
public IList<string> GetAllEditableAssetsID(Type iAssetType);
```

- **Scope**: **Only** assets within `m_CurEditModule`.
- **If `m_CurEditModule == null`**: Returns `Array.Empty<string>()`.
- **Purpose**: List "editable" assets in the Inspector (excluding read-only assets from dependency modules).

#### `GetAllAssetIDs(Type, bool)`

```csharp
public List<string> GetAllAssetIDs(Type iAssetType, bool iUseCache = false);
```

- **Scope**: Assets across all `m_LoadedModules` (aggregated, de-duplicated).
- **Caching**: Results are cached by type name, valid for **0.3 seconds**; `iUseCache = true` forces a refresh.

> [!NOTE]
> The semantics of `iUseCache` are counter-intuitive: `false` (default) means "**use time-sensitive cache**", while `true` means "**force refresh**".

---

### 7.2 Module Query

```csharp
// Get the module for a specified ID (with caching).
public UCL_Module GetModule(string iID, bool iUseCache = true);

// Find the module with specified ID from the loaded list; returns m_CurEditModule if not found.
public UCL_Module GetLoadedModule(string iID);

// Get all module IDs (cache valid for 0.5 seconds).
public IList<string> GetAllModuleIDs(bool iUseCache = true);

// Get display names of all modules (format: "Title(ID)" or "Title").
public IList<string> GetAllModuleNames();
```

---

### 7.3 Asset Config Management

#### `CreateAssetConfig(Type, string)` ⭐ For Saving

```csharp
public AssetConfig CreateAssetConfig(Type iAssetType, string iID);
```

- **Responsibility**: Ensure the save path points to the **current edit module** (`m_CurEditModule`), preventing accidental saves to other modules.
- **Execution Steps**:
  1. If `m_CurEditModule == null`: Log `LogError` and fallback to `GetAssetConfig()`.
  2. Clear old `AssetConfig` cache (`ClearAssetsCache`).
  3. Re-initialize `AssetConfig` and bind it to `m_CurEditModule`.
- **Call Timing**: Invoke **before saving**, not before reading.

> [!WARNING]
> Calling this method when `m_CurEditModule == null` may result in the save path pointing to an incorrect module. Ensure the current edit module has been set via `EditModule()`.

#### `GetAssetConfig(Type, string)` ⭐ For Reading

```csharp
public AssetConfig GetAssetConfig(Type iAssetType, string iID);
```

- **Responsibility**: Follow module override rules (searching `m_LoadedModules` from **back to front**) to find the module containing the specified asset.
- **Fallback**: If no module contains this asset, initialize with `CurModule` (indicating the asset is new).

#### `ContainsAsset(Type, string)`

```csharp
public bool ContainsAsset(Type iAssetType, string iID);
```

Checks if the asset actually exists on disk via `GetAssetConfig` followed by `AssetConfig.Exist`.

---

### 7.4 Cache Management

```csharp
// Clear all caches for a specified type (AssetsCache + IDsCache).
public void ClearAssetsCache(Type iAssetType);

// Clear only the AssetConfig cache for a specific ID under a specified type.
public void ClearAssetsCache(Type iAssetType, string iID);

// Clear internal caches of all loaded modules.
public void ClearCache();
```

---

### 7.5 Asset Grouping

```csharp
// Get a sorted list of asset groups (sorted by m_AssetGroupSortingOrder).
public List<string> GetAssetGroups();

// Get Asset Meta (including grouping info) for the current module.
public UCL_AssetCommonMeta GetAssetMeta(string iTypeName);
```

---

### 7.6 Config Access

```csharp
// Serialize m_Config and write to disk; sync m_BuiltinModules list in Editor.
virtual public void SaveConfig();

// Asynchronously read Config JSON and deserialize into m_Config (with re-entrancy protection).
virtual protected async UniTask LoadConfig();
```

---

### 7.7 Module Loading Pipeline

#### `LoadModulePlaylistAsync(UCL_ModulePlaylist, CancellationToken)`

```csharp
public async UniTask<Dictionary<string, UCL_Module>> LoadModulePlaylistAsync(
    UCL_ModulePlaylist modulePlayist, CancellationToken token);
```

- **Full Async Flow**:
  1. Re-entrancy protection (wait for previous loading to finish).
  2. Wait for `WaitUntilInitialized`.
  3. Call synchronous `LoadModulePlaylist()`.
  4. Execute `OnLoadedModuleAsync()` (includes `UCL_OnModuleLoadedAsset` + registered Funcs).

#### `LoadModulePlaylist(UCL_ModulePlaylist, bool)`

```csharp
public Dictionary<string, UCL_Module> LoadModulePlaylist(
    UCL_ModulePlaylist modulePlayist, bool loadDependencies);
```

- **Execution Steps**:
  1. Trigger `OnLoadModule` event.
  2. Clear `m_LoadedModules`, `m_ModuleCache`, `m_AssetsCacheDic`, `m_IDsCache`.
  3. Unload all AssetBundles.
  4. Unload unused resources (`Resources.UnloadUnusedAssets`).
  5. Traverse `EnablePlaylist` and recursively load modules and their dependencies.
  6. Trigger `OnLoadedModule` event.

> [!IMPORTANT]
> This method **clears the state of all loaded modules**. Any objects holding references to `UCL_Module` or `AssetConfig` after this should be considered invalid.

#### `LoadModuleAndDependencies(string, Dictionary<string, UCL_Module>)`

```csharp
protected UCL_Module LoadModuleAndDependencies(
    string iModuleID, Dictionary<string, UCL_Module> iLoadedModules);
```

- **Recursive Logic**: Load all `m_DependenciesModules` first, then add itself to `m_LoadedModules`.
- **Result**: Dependency modules are placed at the front, main module at the back (main module overrides dependency assets with the same ID).

---

### 7.8 Editor Operations

```csharp
// Set the current edit module and optionally open UCL_ModuleEditPage.
virtual public void EditModule(string iModuleID, bool iShowModuleEditPage = true);

// Create a new module, save Config, and update CurrentEditModuleID.
virtual public UCL_Module CreateNewModule(string newModuleName, UCL_Module.Config config);

// Clear m_CurEditModule.
public void ClearCurrentEditModule();

// Restore the last State according to PlayerPrefs (e.g., auto-reopen EditModule page).
virtual public void ResumeState();

// Set GUI state; automatically clears m_CurEditModule when State.Main.
virtual public void SetState(State iState);

// Notify the current module that an asset has been modified (saved/deleted).
virtual public void OnModuleEdit();

// Path Helper: Get the full path for a specified relative path under the current edit module.
virtual public string GetCurEditModuleFolder(string iRelativeFolderPath);

// Path Helper: Get the full path for a specified relative path under a specified module ID.
virtual public string GetFolderPath(string iModuleID, string iRelativeFolderPath);
```

---

### 7.9 GUI Rendering

```csharp
virtual public void OnGUI(UCL_ObjectDictionary iDataDic);
```

Renders the following Inspector blocks:
1. **EditType Toggle** (Editor only): `Popup` to switch `ModuleEditType`.
2. **Save Config / Load Config Buttons**.
3. **Open Module Folder Button** (Windows Standalone only).
4. **Config Field Display** (includes Zip/UnZip tool buttons).
5. **Create New Module Button**.
6. **Module Selection Popup + Edit Button**.
7. **Module Playlist Management Page Entry**.
8. **Loaded Modules List**.
9. **AssetsCache Status Display**.

---

## 8. Module Override Rules

> [!IMPORTANT]
> **Module Priority**: Within `m_LoadedModules`, **modules with higher indices (placed later) have higher priority**.
> 
> - `GetAssetConfig()` scans from back to front (`Count-1` → `0`), meaning **assets in the last loaded module can override those with the same ID in earlier modules**.
> - `LoadModuleAndDependencies()` ensures dependency modules are added first and the main module last, giving the main module higher priority than its dependencies.

---

## 9. Known Risks and Precautions

| Risk | Description | Mitigation |
|---|---|---|
| `m_CurEditModule == null` | `CreateAssetConfig` called during saving without first calling `EditModule`. | Check `CurEditModuleID` or display error before aborting save. |
| `GetAllAssetIDs` `iUseCache` inverted | `true` = refresh, `false` = use cache. | Verify semantics in this document before use. |
| `LoadModulePlaylist` clears all refs | Existing `UCL_Module` / `AssetConfig` references become invalid after call. | Re-acquire latest references via `GetAssetConfig`. |
| `GetJsonData()` throws Exception | Does not return null if file is missing. | Caller must wrap in a `try-catch` block. |
| `ModuleEditType` setter side effect | Setting this clears `m_ModuleCache`. | Re-call `GetModule` after switching. |

---

## 10. Related File Index

| File | Description |
|---|---|
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModuleService.cs` | Source code for this documentation. |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_Module.cs` | Single module data and configuration. |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModulePath.cs` | Path calculation utilities. |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModulePathConfig.cs` | Path configuration container. |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModulePlaylist.cs` | Playlist data structure. |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_OnModuleLoadedAsset.cs` | Async callback asset after module loading. |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModuleEntry.cs` | Module identifier and core ID. |
| `Assets/UCL/UCL_Core/Docs/UCL_ModuleService_API.md` | This document. |
