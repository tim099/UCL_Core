# UCL_Module & UCL_ModuleEntry API

## 1. UCL_Module
The primary container for module-specific logic and data. It handles the loading, saving, and installation of a single module.

### Core Properties
- `ID`: Unique identifier for the module.
- `ModuleEditType`: Current editing mode (`Builtin` or `Runtime`).
- `ModuleEntry`: Provides path-specific operations for this module instance.
- `m_Config`: Stores metadata like `Version`, `Title`, `Description`, and `DependenciesModules`.

### Key Methods
- `Load(string id, UCL_ModuleEditType type)`: Loads module configuration from the specified source.
- `Save()`: Persists the current module configuration to its `Config.json`.
- `CheckAndInstall()`: Compares current version with the built-in version and triggers `Install()` if an update is needed.
- `Install()`: Copies/Unzips module content from the built-in source to the runtime persistent storage.
- `ExportModule(bool exportConfig)`: Zips the module for distribution.
- `GetAssetMeta(string typeName)`: Retrieves grouping and sorting metadata for a specific asset type.

---

## 2. UCL_ModuleEntry
A lightweight serializable class used to reference a module by ID. It is frequently used in Inspector popups and dependency lists.

### Core Properties
- `ID`: The ID of the referenced module.
- `Module`: Lazy-loads and returns the full `UCL_Module` instance via `UCL_ModuleService`.

### Static Helpers
- `CoreModuleID`: Constant for the system's "Core" module.
- `CoreModule`: Returns a pre-configured `UCL_ModuleEntry` for the Core module.

---

## 3. Related Enums

### UCL_AssetType
Defines different root locations for assets:
- `StreamingAssets`: Assets inside the app's read-only folder.
- `PersistentDatas`: Assets in the user-writable data path.
- `BuiltinModules`: Root for built-in module source files.
- `SteamMods`: Path for Steam Workshop content.

### ELoadingState
Tracks the asynchronous loading progress:
- `None`, `Loading`, `Complete`, `Disposed`.
