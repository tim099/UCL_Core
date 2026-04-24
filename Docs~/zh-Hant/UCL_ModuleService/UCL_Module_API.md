# UCL_Module & UCL_ModuleEntry API

## 1. UCL_Module
模組特定邏輯與資料的主要容器。負責單個模組的加載、儲存與安裝。

### 核心屬性 (Core Properties)
- `ID`：模組的唯一識別碼。
- `ModuleEditType`：當前編輯模式（`Builtin` 或 `Runtime`）。
- `ModuleEntry`：為此模組實例提供特定於路徑的操作。
- `m_Config`：儲存元數據，如 `Version` (版本)、`Title` (標題)、`Description` (描述) 以及 `DependenciesModules` (依賴模組)。

### 關鍵方法 (Key Methods)
- `Load(string id, UCL_ModuleEditType type)`：從指定來源加載模組配置。
- `Save()`：將當前模組配置持久化儲存至其 `Config.json`。
- `CheckAndInstall()`：將當前版本與內置版本進行比較，若需要更新則觸發 `Install()`。
- `Install()`：將模組內容從內置來源拷貝或解壓至運行時的持久化存儲中。
- `ExportModule(bool exportConfig)`：將模組壓縮以供發佈。
- `GetAssetMeta(string typeName)`：檢索特定資產類型的分組與排序元數據。

---

## 2. UCL_ModuleEntry
一個輕量級的可序列化類別，用於透過 ID 引用模組。常用於 Inspector 彈出視窗與依賴清單。

### 核心屬性 (Core Properties)
- `ID`：引用模組的 ID。
- `Module`：透過 `UCL_ModuleService` 延遲加載並返回完整的 `UCL_Module` 實例。

### 靜態輔助工具 (Static Helpers)
- `CoreModuleID`：系統「核心」 (Core) 模組的常數。
- `CoreModule`：為核心模組返回預先配置好的 `UCL_ModuleEntry`。

---

## 3. 相關列舉 (Related Enums)

### UCL_AssetType
定義資產的不同根目錄位置：
- `StreamingAssets`：應用程式唯讀資料夾內的資產。
- `PersistentDatas`：使用者可寫入資料路徑中的資產。
- `BuiltinModules`：內置模組原始檔的根目錄。
- `SteamMods`：Steam 工作坊內容的路徑。

### ELoadingState
追蹤異步加載進度：
- `None`, `Loading`, `Complete`, `Disposed`。
