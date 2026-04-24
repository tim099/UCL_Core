# UCL_ModulePath & 路徑管理 API

## 1. UCL_ModulePath (靜態類別)
用於定義資料夾結構與管理模組發佈（壓縮/安裝）的核心工具。

### 關鍵特性
- **壓縮 (Zipping)**：`ZipAllModules()` 將所有標記為導出的模組壓縮至 `StreamingAssets`。
- **預建置處理 (Pre-build Processing)**：`OnPreprocessBuild()` 在觸發 Unity Build 前自動打包模組。

---

## 2. UCL_ModulePath.PersistantPath.ModulesEntry
管理特定 `UCL_ModuleEditType` 下的一組模組。

### 核心屬性 (Core Properties)
- `RootFolder`：根路徑（例如 `persistentDataPath/.Modules`）。
- `ModulesPath`：包含各個模組目錄的子資料夾。
- `ConfigPath`：該根目錄下全域 `Config.json` 的路徑。

### 方法 (Methods)
- `LoadConfig()`：加載全域模組配置。
- `GetModulePath(string id)`：返回特定模組資料夾的絕對路徑。
- `GetModuleEntry(string id)`：為特定模組返回一個 `ModuleEntry` 物件以進行路徑操作。
- `ZipAllModules()`：將各個模組打包成 `.zip` 檔案並存放於 `StreamingAssets`。

---

## 3. UCL_ModulePath.PersistantPath.ModuleEntry
作用範圍僅限於**單個模組**的路徑操作。

### 關鍵方法 (Key Methods)
- `Install()`：將模組從 Builtin 同步至 Runtime。支援資料夾拷貝與 `.zip` 解壓。
- `UnInstall()`：從 Runtime 路徑刪除模組。
- `GetAssetPath(Type type, string id)`：返回特定資產 `.json` 檔案的路徑。
- `GetAssetFolderPath(Type type)`：返回模組內特定資產類型的目錄。
- `ZipModule(string targetFolder)`：將模組壓縮成 `.zip` 檔案。

---

## 4. 路徑配置邏輯 (Path Configuration Logic)
系統依賴 `UCL_ModulePathConfig` 來定義相對路徑。

### 標準結構：
- **Root**: `ModulesRoot`
    - **Config**: `Config.json`
    - **Modules**: `Modules/`
        - `{ModuleID}/`
            - `Config.json`
            - `Resources/`
                - `{AssetType}/`
                    - `{AssetID}.json`
                    - `.CommonDataMeta` (資產元數據)
