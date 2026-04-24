---
title: UCL_ModuleService API 文件
description: 模組生命週期管理服務的完整 API 參考，涵蓋初始化、加載、儲存、資產快取與 GUI 流程。
last_updated: 2026-04-24
target_audience: [AI_Agent, Gameplay_Programmer, Tool_Developer]
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModuleService.cs
namespace: UCL.Core
---

# UCL_ModuleService API 文件

## 1. 系統概觀 (System Overview)

`UCL_ModuleService` 是 UCL 框架的**模組生命週期管理核心**，以單例 (Singleton) 模式運作。它負責：

- **模組加載、安裝與依賴解析** — 遞迴加載 `UCL_Module` 及其依賴項 `m_DependenciesModules`。
- **資產路徑解析與快取管理** — 透過 `AssetConfig` / `AssetsCache` 決定每個 `UCL_Asset` 的實際存儲路徑。
- **播放清單驅動的運行時切換** — 根據 `UCL_ModulePlaylist` 動態切換已加載的模組集合。
- **編輯器工作流支援** — 提供 Inspector/GUI 操作入口，如 `EditModule` (編輯模組)、`CreateNewModule` (建立新模組) 等。
- **跨平台初始化** — 根據 `Application.isEditor` 自動在 Builtin / Runtime 安裝模式之間切換。

> [!IMPORTANT]
> `UCL_ModuleService` 使用**延遲加載單例** (`Ins` 屬性)。首次存取時會自動建立實例並呼叫 `Init()`，進而啟動異步過程 `InitAsync()`。因此，在同步程式碼中存取 `Ins` 後，**不保證初始化立即完成**。請使用 `WaitUntilInitialized(token)` 確保安全。

---

## 2. 列舉 (Enums)

### 2.1 `UCL_ModuleEditType`

```csharp
public enum UCL_ModuleEditType
{
    Builtin,  // 模組資料存儲在 StreamingAssets (唯讀，隨 Build 打包)
    Runtime,  // 模組資料存儲在 PersistentDataPath (可讀寫，運行時可修改)
}
```

| 數值 | 說明 |
|---|---|
| `Builtin` | 內置模組，資料位於 `StreamingAssets`；僅在 Editor 下可編輯。 |
| `Runtime` | 運行時模組，資料位於 `PersistentDataPath`；支援玩家自定義與 Mods。 |

---

### 2.2 `UCL_ModuleService.State`

控制 `UCL_ModuleService` 的 **GUI 頁面狀態**，透過 `UCL_PlayerPrefs` 持久化。

```csharp
public enum State
{
    Main,       // 主頁面：選擇要編輯的模組。
    EditModule, // 編輯頁面：進入指定模組的編輯流程。
}
```

---

### 2.3 `UCL_ModuleService.EditorInstallMode`

控制模組在 Editor 環境下的安裝行為。

```csharp
public enum EditorInstallMode
{
    Default, // 直接將模組資料夾拷貝至目標安裝路徑。
    UnZip,   // 模擬真實裝置：安裝前先解壓 .zip 包。
}
```

---

## 3. 核心靜態成員 (Core Static Members)

### 3.1 單例存取 (Singleton Access)

```csharp
public static UCL_ModuleService Ins { get; }
```

- **行為**：首次存取時自動建立實例並呼叫 `Init()`。
- **執行緒安全**：僅限主執行緒。

---

### 3.2 狀態與初始化 (Status and Initialization)

| 成員 | 類型 | 說明 |
|---|---|---|
| `Initialized` | `bool` (static) | 若單例已存在且 `m_Initialized == true` 則返回 `true`。 |
| `CurState` | `State` (static) | 當前 GUI 頁面狀態，持久化至 `UCL_PlayerPrefs`。 |
| `ModuleEditType` | `UCL_ModuleEditType` (static) | 當前模組編輯類型；切換時會清空 `m_ModuleCache`。 |

---

### 3.3 模組引用 (Module References)

| 成員 | 類型 | 說明 |
|---|---|---|
| `CurEditModule` | `UCL_Module` (static) | 當前正在編輯的模組；返回 `Ins.m_CurEditModule`。 |
| `CurEditModuleID` | `string` (static) | 當前編輯模組的 ID；若 `m_CurEditModule == null` 則返回核心模組 ID。 |
| `ModResourcesPath` | `string` (static) | 當前編輯模組的資源路徑；用於反射調用。 |
| `PathConfig` | `UCL_ModulePathConfig` (static) | 存取全域路徑配置物件。 |

---

### 3.4 模組加載事件 (Module Loading Events)

```csharp
public static event System.Action OnLoadModule;    // 模組開始加載前觸發。
public static event System.Action OnLoadedModule;  // 同步模組加載完成後立即觸發。
```

#### 異步加載回調 (Asynchronous Loading Callbacks)

```csharp
public static void AddLoadedModuleFunc(System.Func<CancellationToken, UniTask> func);
public static void RemoveLoadedModuleFunc(System.Func<CancellationToken, UniTask> func);
```

- **用途**：為模組加載完成後的異步管線註冊額外任務。
- **執行順序**：在所有 `UCL_OnModuleLoadedAsset` 資產按 `m_Order` 順序執行後，所有註冊的 `Funcs` 會並行執行。

---

## 4. 初始化流程 (Initialization Flow)

### `WaitUntilInitialized(CancellationToken)`

```csharp
public static async UniTask WaitUntilInitialized(CancellationToken iToken);
```

- **用途**：在異步環境中等待 `UCL_ModuleService` 完成初始化。
- **建議**：所有依賴模組資料的系統（如 `ClimateSim`, `Earth`）都應在啟動時呼叫此方法。

#### 初始化流程圖

```
Ins (屬性存取)
  └─ new UCL_ModuleService()
       └─ Init()
            └─ InitAsync()  [異步]
                 ├─ 同步 ModuleEditType → m_PathConfig
                 ├─ Directory.CreateDirectory(ModulesPath)
                 ├─ await LoadConfig()          ← 讀取配置 JSON
                 ├─ 根據 ExportModules 執行 CheckAndInstall / Install
                 ├─ m_Config.m_Playlist.LoadPlaylist(false)
                 └─ m_Initialized = true
```

---

## 5. 內部類別 (Inner Classes)

### 5.1 `Config`

`UCL_ModuleService` 的主要配置容器，繼承自 `UnityJsonSerializable`，序列化至 `PathConfig` 指向的 JSON 檔案。

| 欄位 | 類型 | 說明 |
|---|---|---|
| `m_BuiltinModules` | `List<string>` | 所有內置模組 ID 清單（位於 StreamingAssets）。 |
| `m_ExportModules` | `Dictionary<string, ModuleExportConfig>` | 待導出/安裝的模組及其導出設定。 |
| `m_Playlist` | `UCL_ModulePlaylist` | 播放清單，決定哪些模組將被加載與執行。 |
| `m_ForceInstallInEditor` | `bool` | 是否在 Editor 環境下強制重新安裝所有模組。 |
| `m_Version` | `string` | 配置版本號（預設 `"1.0.0"`）。 |
| `m_AssetGroupSortingOrder` | `Dictionary<string, int>` | 資產分組的排序權重（越小越優先，預設 `99`）。 |
| `m_EditorInstallMode` | `EditorInstallMode` | 編輯器安裝模式 (Default / UnZip)。 |

#### `Config.CreateModule()`

```csharp
public UCL_Module CreateModule(string iID, UCL_ModuleEditType iModuleEditType, UCL_Module.Config config);
```

- **用途**：建立新模組並立即呼叫 `UCL_Module.Save()` 將配置寫入磁碟。
- **返回值**：已初始化並儲存的 `UCL_Module` 實例。

---

### 5.2 `ModuleExportConfig`

實作 `UCLI_IsEnable` 介面，控制單個模組是否參與導出/安裝流程。

| 欄位 | 類型 | 說明 |
|---|---|---|
| `m_ExportModule` | `bool` | `true` 代表此模組應被導出與安裝。 |

---

### 5.3 `AssetConfig`

**每個資產 ID 共享一個**，`AssetConfig` 負責解析資產的完整存儲路徑與所屬模組。

| 成員 | 類型 | 說明 |
|---|---|---|
| `p_Module` | `UCL_Module` | 該資產所屬的模組；`null` 代表資產不存在。 |
| `ID` | `string` | 資產的唯一識別碼。 |
| `AssetType` | `Type` | 資產的 C# 類型。 |
| `AssetCache` | `object` | 供外部系統掛載的任意快取物件。 |
| `Exist` | `bool` | 若 `p_Module != null` 則為 `true`。 |
| `ModuleEntry` | `UCL_ModulePath.PersistantPath.ModuleEntry` | 路徑解析器；若 `p_Module == null` 會噴 `LogError`。 |
| `AssetPath` | `string` | 完整的存儲路徑（由 `ModuleEntry.GetAssetPath` 計算）。 |
| `AssetFolderPath` | `string` | 資產類型的資料夾路徑。 |
| `GroupID` | `string` | 資產的分組 ID；讀寫時透過 `.Group/{ID}` 文字檔持久化。 |
| `Inited` | `bool` | 呼叫 `Init()` 後設為 `true`。 |

#### 主要方法

```csharp
// 初始化：綁定模組、資產類型與 ID。
public void Init(UCL_Module iModule, Type iAssetType, string iID);

// 讀取資產的 JSON 資料 (若檔案不存在會拋出 Exception)。
public JsonData GetJsonData();

// 序列化 JSON 資料並寫入 AssetPath (自動建立資料夾)。
public void SaveAsset(JsonData iJson);

// 刪除 AssetPath 指向的實體檔案。
public void DeleteAsset();
```

> [!WARNING]
> `GetJsonData()` 在檔案不存在時會**拋出 `System.Exception`** 而非返回 `null`。調用者必須使用 `try-catch` 包裹。

---

### 5.4 `AssetsCache`

**每個 `UCL_Asset` 類型共享一個**，`AssetsCache` 維護該類型下所有資產的 `AssetConfig` 字典以及分組 ID 清單。

| 成員 | 類型 | 說明 |
|---|---|---|
| `m_AssetConfigDic` | `Dictionary<string, AssetConfig>` | 資產 ID → AssetConfig 的映射。 |
| `m_GroupIDs` | `List<string>` | 從所有已加載模組的元數據中彙整的分組 ID 清單。 |
| `m_AssetType` | `Type` | 此快取對應的資產類型。 |

```csharp
// 獲取（或延遲建立）指定 ID 的 AssetConfig。
public AssetConfig GetAssetConfig(string iID);

// 移除指定 ID 的 AssetConfig 快取（用於強制重新解析路徑）。
public void ClearAssetsCache(string iID);
```

---

## 6. 實例成員 (Instance Members)

### 6.1 狀態欄位 (Status Fields)

| 欄位 | 類型 | 說明 |
|---|---|---|
| `m_PathConfig` | `UCL_ModulePathConfig` | 全域路徑配置；包含 `ModulesPath`、`RootPath` 等。 |
| `m_Initialized` | `bool` | `InitAsync()` 完成後設為 `true`。 |
| `m_LoadingConfig` | `bool` | 配置加載的重入保護旗標。 |
| `m_LoadingPlaylist` | `bool` | 播放清單加載的重入保護旗標。 |
| `m_Config` | `Config` | 當前反序列化後的模組服務配置。 |
| `m_CurEditModule` | `UCL_Module` | 當前編輯中的模組（若未選擇則為 `null`）。 |
| `m_LoadedModules` | `List<UCL_Module>` | 已加載且啟用的模組順序清單（後者會覆蓋同 ID 資產）。 |
| `m_ModuleCache` | `Dictionary<string, UCL_Module>` | 模組 ID → UCL_Module 的快取字典。 |
| `m_IDsCache` | `Dictionary<string, (DateTime, List<string>)>` | 具時效性的資產類型名稱 → (時間戳, ID 清單) 快取。 |
| `m_AssetsCacheDic` | `Dictionary<string, AssetsCache>` | 資產類型名稱 → AssetsCache 的快取字典。 |

---

### 6.2 屬性 (Properties)

```csharp
public Config ModuleConfig => m_Config;
public bool LoadingPlaylist => m_LoadingPlaylist;
public List<UCL_Module> LoadedModules => m_LoadedModules;

// 返回 m_CurEditModule；若為 null，則返回 m_LoadedModules 中的最後一個模組。
public UCL_Module CurModule { get; }
```

---

## 7. 公開方法 (Public Methods)

### 7.1 資產 ID 查詢 (Asset ID Query)

#### `GetAllEditableAssetsID(Type)`

```csharp
public IList<string> GetAllEditableAssetsID(Type iAssetType);
```

- **範圍**：**僅限** `m_CurEditModule` 內的資產。
- **若 `m_CurEditModule == null`**：返回 `Array.Empty<string>()`。
- **用途**：在 Inspector 中列出「可編輯」的資產（排除來自依賴模組的唯讀資產）。

#### `GetAllAssetIDs(Type, bool)`

```csharp
public List<string> GetAllAssetIDs(Type iAssetType, bool iUseCache = false);
```

- **範圍**：跨所有 `m_LoadedModules` 的資產（彙整並去重）。
- **快取**：結果按類型名稱快取，有效時間 **0.3 秒**；`iUseCache = true` 會強制刷新。

> [!NOTE]
> `iUseCache` 的語意較反直覺：`false` (預設) 代表「**使用具時效性的快取**」，`true` 代表「**強制刷新**」。

---

### 7.2 模組查詢 (Module Query)

```csharp
// 獲取指定 ID 的模組（含快取）。
public UCL_Module GetModule(string iID, bool iUseCache = true);

// 從已加載清單中尋找指定 ID 的模組；若無則返回 m_CurEditModule。
public UCL_Module GetLoadedModule(string iID);

// 獲取所有模組 ID (快取有效時間 0.5 秒)。
public IList<string> GetAllModuleIDs(bool iUseCache = true);

// 獲取所有模組的顯示名稱 (格式: "標題(ID)" 或 "標題")。
public IList<string> GetAllModuleNames();
```

---

### 7.3 資產配置管理 (Asset Config Management)

#### `CreateAssetConfig(Type, string)` ⭐ 用於儲存

```csharp
public AssetConfig CreateAssetConfig(Type iAssetType, string iID);
```

- **職責**：確保儲存路徑指向**當前編輯模組** (`m_CurEditModule`)，防止誤存至其他模組。
- **執行步驟**：
  1. 若 `m_CurEditModule == null`：噴 `LogError` 並回退至 `GetAssetConfig()`。
  2. 清除舊的 `AssetConfig` 快取 (`ClearAssetsCache`)。
  3. 重新初始化 `AssetConfig` 並綁定至 `m_CurEditModule`。
- **呼叫時機**：在**儲存前**調用，而非讀取前。

> [!WARNING]
> 當 `m_CurEditModule == null` 時呼叫此方法，可能導致儲存路徑指向錯誤的模組。請確保已透過 `EditModule()` 設定當前編輯模組。

#### `GetAssetConfig(Type, string)` ⭐ 用於讀取

```csharp
public AssetConfig GetAssetConfig(Type iAssetType, string iID);
```

- **職責**：遵循模組覆蓋規則（從**後往前**搜尋 `m_LoadedModules`），找到包含該指定資產的模組。
- **回退**：若無模組包含此資產，則以 `CurModule` 初始化（代表資產為新建立）。

#### `ContainsAsset(Type, string)`

```csharp
public bool ContainsAsset(Type iAssetType, string iID);
```

透過 `GetAssetConfig` 並檢查 `AssetConfig.Exist` 來確認資產是否確實存在於磁碟。

---

### 7.4 快取管理 (Cache Management)

```csharp
// 清除指定類型的所有快取 (AssetsCache + IDsCache)。
public void ClearAssetsCache(Type iAssetType);

// 僅清除指定類型下特定 ID 的 AssetConfig 快取。
public void ClearAssetsCache(Type iAssetType, string iID);

// 清除所有已加載模組的內部快取。
public void ClearCache();
```

---

### 7.5 資產分組 (Asset Grouping)

```csharp
// 獲取已排序的資產分組清單 (按 m_AssetGroupSortingOrder 排序)。
public List<string> GetAssetGroups();

// 獲取當前模組針對該資產類型的元數據 (含分組資訊)。
public UCL_AssetCommonMeta GetAssetMeta(string iTypeName);
```

---

### 7.6 配置存取 (Config Access)

```csharp
// 序列化 m_Config 並寫入磁碟；在 Editor 下會同步 m_BuiltinModules 清單。
virtual public void SaveConfig();

// 異步讀取配置 JSON 並反序列化至 m_Config (含重入保護)。
virtual protected async UniTask LoadConfig();
```

---

### 7.7 模組加載管線 (Module Loading Pipeline)

#### `LoadModulePlaylistAsync(UCL_ModulePlaylist, CancellationToken)`

```csharp
public async UniTask<Dictionary<string, UCL_Module>> LoadModulePlaylistAsync(
    UCL_ModulePlaylist modulePlayist, CancellationToken token);
```

- **完整異步流程**：
  1. 重入保護（等待前一次加載完成）。
  2. 等待 `WaitUntilInitialized`。
  3. 調用同步 `LoadModulePlaylist()`。
  4. 執行 `OnLoadedModuleAsync()` (含 `UCL_OnModuleLoadedAsset` + 註冊的 Funcs)。

#### `LoadModulePlaylist(UCL_ModulePlaylist, bool)`

```csharp
public Dictionary<string, UCL_Module> LoadModulePlaylist(
    UCL_ModulePlaylist modulePlayist, bool loadDependencies);
```

- **執行步驟**：
  1. 觸發 `OnLoadModule` 事件。
  2. 清空 `m_LoadedModules`、`m_ModuleCache`、`m_AssetsCacheDic`、`m_IDsCache`。
  3. 卸載所有 AssetBundle。
  4. 卸載未使用的資源 (`Resources.UnloadUnusedAssets`)。
  5. 遍歷 `EnablePlaylist` 遞迴加載模組及其依賴項。
  6. 觸發 `OnLoadedModule` 事件。

> [!IMPORTANT]
> 此方法會**重置所有已加載模組的狀態**。在此之後，任何持有 `UCL_Module` 或 `AssetConfig` 舊引用的物件都應被視為無效。

#### `LoadModuleAndDependencies(string, Dictionary<string, UCL_Module>)`

```csharp
protected UCL_Module LoadModuleAndDependencies(
    string iModuleID, Dictionary<string, UCL_Module> iLoadedModules);
```

- **遞迴邏輯**：先加載所有 `m_DependenciesModules`，最後才將自身加入 `m_LoadedModules`。
- **結果**：依賴模組在前，主模組在後（主模組優先覆蓋同 ID 依賴資產）。

---

### 7.8 編輯器操作 (Editor Operations)

```csharp
// 設定當前編輯模組，並可選擇是否開啟 UCL_ModuleEditPage。
virtual public void EditModule(string iModuleID, bool iShowModuleEditPage = true);

// 建立新模組，儲存配置並更新 CurrentEditModuleID。
virtual public UCL_Module CreateNewModule(string newModuleName, UCL_Module.Config config);

// 清除 m_CurEditModule。
public void ClearCurrentEditModule();

// 根據 PlayerPrefs 恢復上次的狀態 (例如自動重新開啟 EditModule 頁面)。
virtual public void ResumeState();

// 設定 GUI 狀態；當設為 State.Main 時會自動清除 m_CurEditModule。
virtual public void SetState(State iState);

// 通知當前模組有資產被修改（儲存/刪除）。
virtual public void OnModuleEdit();

// 路徑輔助：獲取當前編輯模組下指定相對路徑的完整路徑。
virtual public string GetCurEditModuleFolder(string iRelativeFolderPath);

// 路徑輔助：獲取指定模組 ID 下指定相對路徑的完整路徑。
virtual public string GetFolderPath(string iModuleID, string iRelativeFolderPath);
```

---

### 7.9 GUI 繪製 (GUI Rendering)

```csharp
virtual public void OnGUI(UCL_ObjectDictionary iDataDic);
```

繪製以下 Inspector 區塊：
1. **EditType 切換** (僅 Editor)：切換 `ModuleEditType` 的 `Popup`。
2. **Save Config / Load Config 按鈕**。
3. **開啟模組資料夾按鈕** (僅限 Windows Standalone)。
4. **Config 欄位顯示** (含 Zip/UnZip 工具按鈕)。
5. **建立新模組按鈕**。
6. **模組選擇 Popup + 編輯按鈕**。
7. **模組播放清單管理頁面入口**。
8. **已加載模組清單**。
9. **AssetsCache 狀態顯示**。

---

## 8. 模組覆蓋規則 (Module Override Rules)

> [!IMPORTANT]
> **模組優先級**：在 `m_LoadedModules` 中，**索引越大（越後加入）的模組優先級越高**。
> 
> - `GetAssetConfig()` 從後往前掃描 (`Count-1` → `0`)，這意味著**最後一個加載的模組中的資產會覆蓋早期模組中同 ID 的資產**。
> - `LoadModuleAndDependencies()` 確保依賴模組先被加入，主模組最後加入，從而使主模組具備高於其依賴項的優先權。

---

## 9. 已知風險與注意事項 (Known Risks and Precautions)

| 風險 | 說明 | 緩解措施 |
|---|---|---|
| `m_CurEditModule == null` | 儲存時未先呼叫 `EditModule` 就調用 `CreateAssetConfig`。 | 在儲存前檢查 `CurEditModuleID` 或顯示錯誤並中斷儲存。 |
| `GetAllAssetIDs` 的 `iUseCache` 語意反轉 | `true` = 刷新，`false` = 使用快取。 | 使用前務必核對此文件中的說明。 |
| `LoadModulePlaylist` 清空所有引用 | 呼叫後，現有的 `UCL_Module` / `AssetConfig` 引用均失效。 | 重新透過 `GetAssetConfig` 獲取最新引用。 |
| `GetJsonData()` 拋出 Exception | 檔案缺失時不會返回 null。 | 調用者必須使用 `try-catch` 包裹。 |
| `ModuleEditType` setter 副作用 | 設定此項會清空 `m_ModuleCache`。 | 切換後需重新呼叫 `GetModule`。 |

---

## 10. 相關檔案索引 (Related File Index)

| 檔案 | 說明 |
|---|---|
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModuleService.cs` | 此文件的原始程式碼。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_Module.cs` | 單個模組資料與配置。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModulePath.cs` | 路徑計算工具。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModulePathConfig.cs` | 路徑配置容器。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModulePlaylist.cs` | 播放清單資料結構。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_OnModuleLoadedAsset.cs` | 模組加載後的異步回調資產。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModuleEntry.cs` | 模組識別碼與核心 ID。 |
| `Assets/UCL/UCL_Core/Docs/UCL_ModuleService_API.md` | 本文件。 |
