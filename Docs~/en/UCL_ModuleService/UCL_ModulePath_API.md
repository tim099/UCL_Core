# UCL_ModulePath & Path Management API

## 1. UCL_ModulePath (Static Class)
The core utility for defining folder structures and managing module distribution (Zipping/Installation).

### Key Features
- **Zipping**: `ZipAllModules()` compresses all modules marked for export into `StreamingAssets`.
- **Pre-build Processing**: `OnPreprocessBuild()` automatically packages modules before a Unity build is triggered.

---

## 2. UCL_ModulePath.PersistantPath.ModulesEntry
Manages a set of modules under a specific `UCL_ModuleEditType`.

### Core Properties
- `RootFolder`: The base path (e.g., `persistentDataPath/.Modules`).
- `ModulesPath`: The subfolder containing individual module directories.
- `ConfigPath`: Path to the global `Config.json` for this root.

### Methods
- `LoadConfig()`: Loads the global module configuration.
- `GetModulePath(string id)`: Returns the absolute path to a specific module folder.
- `GetModuleEntry(string id)`: Returns a `ModuleEntry` object for path operations on a specific module.
- `ZipAllModules()`: Packages individual modules into `.zip` files in `StreamingAssets`.

---

## 3. UCL_ModulePath.PersistantPath.ModuleEntry
Path operations scoped to a **single module**.

### Key Methods
- `Install()`: Synchronizes the module from Builtin to Runtime. Supports both folder copying and `.zip` extraction.
- `UnInstall()`: Deletes the module from the Runtime path.
- `GetAssetPath(Type type, string id)`: Returns the path to a specific asset's `.json` file.
- `GetAssetFolderPath(Type type)`: Returns the directory for a specific asset type within the module.
- `ZipModule(string targetFolder)`: Compresses the module into a `.zip` file.

---

## 4. Path Configuration Logic
The system relies on `UCL_ModulePathConfig` to define relative paths.

### Standard Structure:
- **Root**: `ModulesRoot`
    - **Config**: `Config.json`
    - **Modules**: `Modules/`
        - `{ModuleID}/`
            - `Config.json`
            - `Resources/`
                - `{AssetType}/`
                    - `{AssetID}.json`
                    - `.CommonDataMeta` (Asset Metadata)
