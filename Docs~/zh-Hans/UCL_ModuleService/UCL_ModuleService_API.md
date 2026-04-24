---
title: UCL_ModuleService API 文件
description: 模块生命周期管理服务的完整 API 参考，涵盖初始化、加载、存储、资产缓存与 GUI 流程。
last_updated: 2026-04-24
target_audience: [AI_Agent, Gameplay_Programmer, Tool_Developer]
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModuleService.cs
namespace: UCL.Core
---

# UCL_ModuleService API 文件

## 1. 系统概览 (System Overview)

`UCL_ModuleService` 是 UCL 框架的**模块生命周期管理核心**，以单例 (Singleton) 模式运作。它负责：

- **模块加载、安装与依赖解析** — 递归加载 `UCL_Module` 及其依赖项 `m_DependenciesModules`。
- **资产路径解析与缓存管理** — 通过 `AssetConfig` / `AssetsCache` 决定每个 `UCL_Asset` 的实际存储路径。
- **播放清单驱动的运行时切换** — 根据 `UCL_ModulePlaylist` 动态切换已加载的模块集合。
- **编辑器工作流支持** — 提供 Inspector/GUI 操作入口，如 `EditModule` (编辑模块)、`CreateNewModule` (创建新模块) 等。
- **跨平台初始化** — 根据 `Application.isEditor` 自动在 Builtin / Runtime 安装模式之间切换。

> [!IMPORTANT]
> `UCL_ModuleService` 使用**延迟加载单例** (`Ins` 属性)。首次访问时会自动创建实例并调用 `Init()`，进而启动异步过程 `InitAsync()`。因此，在同步代码中访问 `Ins` 后，**不保证初始化立即完成**。请使用 `WaitUntilInitialized(token)` 确保安全。

---

## 2. 枚举 (Enums)

### 2.1 `UCL_ModuleEditType`

```csharp
public enum UCL_ModuleEditType
{
    Builtin,  // 模块数据存储在 StreamingAssets (只读，随 Build 打包)
    Runtime,  // 模块数据存储在 PersistentDataPath (可读写，运行时可修改)
}
```

| 数值 | 说明 |
|---|---|
| `Builtin` | 内置模块，数据位于 `StreamingAssets`；仅在 Editor 下可编辑。 |
| `Runtime` | 运行时模块，数据位于 `PersistentDataPath`；支持玩家自定义与 Mods。 |

---

### 2.2 `UCL_ModuleService.State`

控制 `UCL_ModuleService` 的 **GUI 页面状态**，通过 `UCL_PlayerPrefs` 持久化。

```csharp
public enum State
{
    Main,       // 主页面：选择要编辑的模块。
    EditModule, // 编辑页面：进入指定模块的编辑流程。
}
```

---

### 2.3 `UCL_ModuleService.EditorInstallMode`

控制模块在 Editor 环境下的安装行为。

```csharp
public enum EditorInstallMode
{
    Default, // 直接将模块文件夹拷贝至目标安装路径。
    UnZip,   // 模拟真实设备：安装前先解压 .zip 包。
}
```

---

## 3. 核心静态成员 (Core Static Members)

### 3.1 单例访问 (Singleton Access)

```csharp
public static UCL_ModuleService Ins { get; }
```

- **行为**：首次访问时自动创建实例并调用 `Init()`。
- **线程安全**：仅限主线程。

---

### 3.2 状态与初始化 (Status and Initialization)

| 成员 | 类型 | 说明 |
|---|---|---|
| `Initialized` | `bool` (static) | 若单例已存在且 `m_Initialized == true` 则返回 `true`。 |
| `CurState` | `State` (static) | 当前 GUI 页面状态，持久化至 `UCL_PlayerPrefs`。 |
| `ModuleEditType` | `UCL_ModuleEditType` (static) | 当前模块编辑类型；切换时会清空 `m_ModuleCache`。 |

---

### 3.3 模块引用 (Module References)

| 成员 | 類型 | 说明 |
|---|---|---|
| `CurEditModule` | `UCL_Module` (static) | 当前正在编辑的模块；返回 `Ins.m_CurEditModule`。 |
| `CurEditModuleID` | `string` (static) | 当前编辑模块的 ID；若 `m_CurEditModule == null` 则返回核心模块 ID。 |
| `ModResourcesPath` | `string` (static) | 当前编辑模块的资源路径；用于反射调用。 |
| `PathConfig` | `UCL_ModulePathConfig` (static) | 访问全局路径配置对象。 |

---

### 3.4 模块加载事件 (Module Loading Events)

```csharp
public static event System.Action OnLoadModule;    // 模块开始加载前触发。
public static event System.Action OnLoadedModule;  // 同步模块加载完成后立即触发。
```

#### 异步加载回调 (Asynchronous Loading Callbacks)

```csharp
public static void AddLoadedModuleFunc(System.Func<CancellationToken, UniTask> func);
public static void RemoveLoadedModuleFunc(System.Func<CancellationToken, UniTask> func);
```

- **用途**：为模块加载完成后的异步管线注册额外任务。
- **执行顺序**：在所有 `UCL_OnModuleLoadedAsset` 资产按 `m_Order` 顺序执行后，所有注册的 `Funcs` 会并行执行。

---

## 4. 初始化流程 (Initialization Flow)

### `WaitUntilInitialized(CancellationToken)`

```csharp
public static async UniTask WaitUntilInitialized(CancellationToken iToken);
```

- **用途**：在异步环境中等待 `UCL_ModuleService` 完成初始化。
- **建议**：所有依赖模块数据的系统（如 `ClimateSim`, `Earth`）都应在启动时调用此方法。

#### 初始化流程图

```
Ins (属性访问)
  └─ new UCL_ModuleService()
       └─ Init()
            └─ InitAsync()  [异步]
                 ├─ 同步 ModuleEditType → m_PathConfig
                 ├─ Directory.CreateDirectory(ModulesPath)
                 ├─ await LoadConfig()          ← 读取配置 JSON
                 ├─ 根据 ExportModules 执行 CheckAndInstall / Install
                 ├─ m_Config.m_Playlist.LoadPlaylist(false)
                 └─ m_Initialized = true
```

---

## 5. 内部类 (Inner Classes)

### 5.1 `Config`

`UCL_ModuleService` 的主要配置容器，继承自 `UnityJsonSerializable`，序列化至 `PathConfig` 指向的 JSON 文件。

| 字段 | 類型 | 说明 |
|---|---|---|
| `m_BuiltinModules` | `List<string>` | 所有内置模块 ID 清单（位于 StreamingAssets）。 |
| `m_ExportModules` | `Dictionary<string, ModuleExportConfig>` | 待导出/安装的模块及其导出设定。 |
| `m_Playlist` | `UCL_ModulePlaylist` | 播放清单，决定哪些模块将被加载与执行。 |
| `m_ForceInstallInEditor` | `bool` | 是否在 Editor 环境下强制重新安装所有模块。 |
| `m_Version` | `string` | 配置版本号（預設 `"1.0.0"`）。 |
| `m_AssetGroupSortingOrder` | `Dictionary<string, int>` | 资产分组的排序权重（越小越优先，預設 `99`）。 |
| `m_EditorInstallMode` | `EditorInstallMode` | 编辑器安装模式 (Default / UnZip)。 |

#### `Config.CreateModule()`

```csharp
public UCL_Module CreateModule(string iID, UCL_ModuleEditType iModuleEditType, UCL_Module.Config config);
```

- **用途**：创建新模块并立即调用 `UCL_Module.Save()` 将配置写入磁盘。
- **返回值**：已初始化并存储的 `UCL_Module` 实例。

---

### 5.2 `ModuleExportConfig`

实现 `UCLI_IsEnable` 接口，控制单个模块是否参与导出/安装流程。

| 字段 | 類型 | 说明 |
|---|---|---|
| `m_ExportModule` | `bool` | `true` 代表此模块应被导出与安装。 |

---

### 5.3 `AssetConfig`

**每个资产 ID 共享一个**，`AssetConfig` 负责解析资产的完整存储路径与所属模块。

| 成員 | 類型 | 说明 |
|---|---|---|
| `p_Module` | `UCL_Module` | 该资产所属的模块；`null` 代表资产不存在。 |
| `ID` | `string` | 资产的唯一识别码。 |
| `AssetType` | `Type` | 资产的 C# 类型。 |
| `AssetCache` | `object` | 供外部系统挂载的任意缓存对象。 |
| `Exist` | `bool` | 若 `p_Module != null` 则为 `true`。 |
| `ModuleEntry` | `UCL_ModulePath.PersistantPath.ModuleEntry` | 路径解析器；若 `p_Module == null` 会报错 `LogError`。 |
| `AssetPath` | `string` | 完整的存储路径（由 `ModuleEntry.GetAssetPath` 计算）。 |
| `AssetFolderPath` | `string` | 资产类型的文件夹路径。 |
| `GroupID` | `string` | 资产的分组 ID；读写时通过 `.Group/{ID}` 文本文件持久化。 |
| `Inited` | `bool` | 调用 `Init()` 后设为 `true`。 |

#### 主要方法

```csharp
// 初始化：绑定模块、资产类型与 ID。
public void Init(UCL_Module iModule, Type iAssetType, string iID);

// 读取资产的 JSON 数据 (若文件不存在会抛出 Exception)。
public JsonData GetJsonData();

// 序列化 JSON 数据并写入 AssetPath (自动创建文件夹)。
public void SaveAsset(JsonData iJson);

// 删除 AssetPath 指向的实体文件。
public void DeleteAsset();
```

> [!WARNING]
> `GetJsonData()` 在文件不存在时会**抛出 `System.Exception`** 而非返回 `null`。调用者必须使用 `try-catch` 包裹。

---

### 5.4 `AssetsCache`

**每个 `UCL_Asset` 类型共享一个**，`AssetsCache` 维护该类型下所有资产的 `AssetConfig` 字典以及分组 ID 清单。

| 成員 | 類型 | 说明 |
|---|---|---|
| `m_AssetConfigDic` | `Dictionary<string, AssetConfig>` | 资产 ID → AssetConfig 的映射。 |
| `m_GroupIDs` | `List<string>` | 從所有已加载模块的元数据中汇总的分组 ID 清单。 |
| `m_AssetType` | `Type` | 此缓存对应的资产类型。 |

```csharp
// 获取（或延迟创建）指定 ID 的 AssetConfig。
public AssetConfig GetAssetConfig(string iID);

// 移除指定 ID 的 AssetConfig 缓存（用于强制重新解析路径）。
public void ClearAssetsCache(string iID);
```

---

## 6. 实例成员 (Instance Members)

### 6.1 状态字段 (Status Fields)

| 字段 | 類型 | 说明 |
|---|---|---|
| `m_PathConfig` | `UCL_ModulePathConfig` | 全局路径配置；包含 `ModulesPath`、`RootPath` 等。 |
| `m_Initialized` | `bool` | `InitAsync()` 完成后设为 `true`。 |
| `m_LoadingConfig` | `bool` | 配置加载的重入保护旗标。 |
| `m_LoadingPlaylist` | `bool` | 播放清单加载的重入保护旗標。 |
| `m_Config` | `Config` | 当前反序列化后的模块服务配置。 |
| `m_CurEditModule` | `UCL_Module` | 当前编辑中的模块（若未选择則為 `null`）。 |
| `m_LoadedModules` | `List<UCL_Module>` | 已加载且启用的模块顺序清单（后者会覆盖同 ID 资产）。 |
| `m_ModuleCache` | `Dictionary<string, UCL_Module>` | 模块 ID → UCL_Module 的缓存字典。 |
| `m_IDsCache` | `Dictionary<string, (DateTime, List<string>)>` | 具时效性的资产类型名称 → (时间戳, ID 清单) 缓存。 |
| `m_AssetsCacheDic` | `Dictionary<string, AssetsCache>` | 资产类型名称 → AssetsCache 的缓存字典。 |

---

### 6.2 属性 (Properties)

```csharp
public Config ModuleConfig => m_Config;
public bool LoadingPlaylist => m_LoadingPlaylist;
public List<UCL_Module> LoadedModules => m_LoadedModules;

// 返回 m_CurEditModule；若为 null，則返回 m_LoadedModules 中的最后一个模块。
public UCL_Module CurModule { get; }
```

---

## 7. 公开方法 (Public Methods)

### 7.1 资产 ID 查询 (Asset ID Query)

#### `GetAllEditableAssetsID(Type)`

```csharp
public IList<string> GetAllEditableAssetsID(Type iAssetType);
```

- **范围**：**仅限** `m_CurEditModule` 内的资产。
- **若 `m_CurEditModule == null`**：返回 `Array.Empty<string>()`。
- **用途**：在 Inspector 中列出“可编辑”的资产（排除来自依赖模块的只读资产）。

#### `GetAllAssetIDs(Type, bool)`

```csharp
public List<string> GetAllAssetIDs(Type iAssetType, bool iUseCache = false);
```

- **范围**：跨所有 `m_LoadedModules` 的资产（汇总并去重）。
- **缓存**：结果按类型名称缓存，有效时间 **0.3 秒**；`iUseCache = true` 会强制刷新。

> [!NOTE]
> `iUseCache` 的语意较反直觉：`false` (默认) 代表「**使用具时效性的缓存**」，`true` 代表「**强制刷新**」。

---

### 7.2 模块查询 (Module Query)

```csharp
// 获取指定 ID 的模块（含缓存）。
public UCL_Module GetModule(string iID, bool iUseCache = true);

// 從已加载清单中寻找指定 ID 的模块；若无則返回 m_CurEditModule。
public UCL_Module GetLoadedModule(string iID);

// 获取所有模块 ID (缓存有效时间 0.5 秒)。
public IList<string> GetAllModuleIDs(bool iUseCache = true);

// 获取所有模块的显示名称 (格式: "标题(ID)" 或 "标题")。
public IList<string> GetAllModuleNames();
```

---

### 7.3 资产配置管理 (Asset Config Management)

#### `CreateAssetConfig(Type, string)` ⭐ 用于存储

```csharp
public AssetConfig CreateAssetConfig(Type iAssetType, string iID);
```

- **职责**：确保存储路径指向**当前编辑模块** (`m_CurEditModule`)，防止误存至其他模块。
- **执行步骤**：
  1. 若 `m_CurEditModule == null`：报错 `LogError` 并回退至 `GetAssetConfig()`。
  2. 清除旧的 `AssetConfig` 缓存 (`ClearAssetsCache`)。
  3. 重新初始化 `AssetConfig` 并绑定至 `m_CurEditModule`。
- **调用时机**：在**存储前**调用，而非读取前。

> [!WARNING]
> 当 `m_CurEditModule == null` 时调用此方法，可能导致存储路径指向错误的模块。请确保已通过 `EditModule()` 设置当前编辑模块。

#### `GetAssetConfig(Type, string)` ⭐ 用于读取

```csharp
public AssetConfig GetAssetConfig(Type iAssetType, string iID);
```

- **职责**：遵循模块覆盖规则（從**后往前**搜索 `m_LoadedModules`），找到包含该指定资产的模块。
- **回退**：若无模块包含此资产，則以 `CurModule` 初始化（代表资产为新创建）。

#### `ContainsAsset(Type, string)`

```csharp
public bool ContainsAsset(Type iAssetType, string iID);
```

通过 `GetAssetConfig` 并检查 `AssetConfig.Exist` 来确认资产是否确实存在于磁盘。

---

### 7.4 缓存管理 (Cache Management)

```csharp
// 清除指定类型的所有缓存 (AssetsCache + IDsCache)。
public void ClearAssetsCache(Type iAssetType);

// 仅清除指定类型下特定 ID 的 AssetConfig 缓存。
public void ClearAssetsCache(Type iAssetType, string iID);

// 清除所有已加载模块的内部缓存。
public void ClearCache();
```

---

### 7.5 资产分组 (Asset Grouping)

```csharp
// 获取已排序的资产分组清单 (按 m_AssetGroupSortingOrder 排序)。
public List<string> GetAssetGroups();

// 获取当前模块针对该资产类型的元数据 (含分组信息)。
public UCL_AssetCommonMeta GetAssetMeta(string iTypeName);
```

---

### 7.6 配置访问 (Config Access)

```csharp
// 序列化 m_Config 并写入磁盘；在 Editor 下会同步 m_BuiltinModules 清单。
virtual public void SaveConfig();

// 异步读取配置 JSON 并反序列化至 m_Config (含重入保护)。
virtual protected async UniTask LoadConfig();
```

---

### 7.7 模块加载管线 (Module Loading Pipeline)

#### `LoadModulePlaylistAsync(UCL_ModulePlaylist, CancellationToken)`

```csharp
public async UniTask<Dictionary<string, UCL_Module>> LoadModulePlaylistAsync(
    UCL_ModulePlaylist modulePlayist, CancellationToken token);
```

- **完整异步流程**：
  1. 重入保护（等待前一次加载完成）。
  2. 等待 `WaitUntilInitialized`。
  3. 调用同步 `LoadModulePlaylist()`。
  4. 执行 `OnLoadedModuleAsync()` (含 `UCL_OnModuleLoadedAsset` + 注册的 Funcs)。

#### `LoadModulePlaylist(UCL_ModulePlaylist, bool)`

```csharp
public Dictionary<string, UCL_Module> LoadModulePlaylist(
    UCL_ModulePlaylist modulePlayist, bool loadDependencies);
```

- **执行步骤**：
  1. 触发 `OnLoadModule` 事件。
  2. 清空 `m_LoadedModules`、`m_ModuleCache`、`m_AssetsCacheDic`、`m_IDsCache`。
  3. 卸载所有 AssetBundle。
  4. 卸载未使用的资源 (`Resources.UnloadUnusedAssets`)。
  5. 遍历 `EnablePlaylist` 递归加载模块及其依赖项。
  6. 触发 `OnLoadedModule` 事件。

> [!IMPORTANT]
> 此方法会**重置所有已加载模块的状态**。在此之后，任何持有 `UCL_Module` 或 `AssetConfig` 旧引用的对象都应被视为无效。

#### `LoadModuleAndDependencies(string, Dictionary<string, UCL_Module>)`

```csharp
protected UCL_Module LoadModuleAndDependencies(
    string iModuleID, Dictionary<string, UCL_Module> iLoadedModules);
```

- **递归逻辑**：先加载所有 `m_DependenciesModules`，最后才将自身加入 `m_LoadedModules`。
- **结果**：依赖模块在前，主模块在后（主模块优先覆盖同 ID 依赖资产）。

---

### 7.8 编辑器操作 (Editor Operations)

```csharp
// 设置当前编辑模块，并可选择是否开启 UCL_ModuleEditPage。
virtual public void EditModule(string iModuleID, bool iShowModuleEditPage = true);

// 创建新模块，存储配置并更新 CurrentEditModuleID。
virtual public UCL_Module CreateNewModule(string newModuleName, UCL_Module.Config config);

// 清除 m_CurEditModule。
public void ClearCurrentEditModule();

// 根据 PlayerPrefs 恢复上次的状态 (例如自动重新开启 EditModule 页面)。
virtual public void ResumeState();

// 设置 GUI 状态；当设为 State.Main 时会自动清除 m_CurEditModule。
virtual public void SetState(State iState);

// 通知当前模块有资产被修改（存储/删除）。
virtual public void OnModuleEdit();

// 路径辅助：获取当前编辑模块下指定相对路径的完整路径。
virtual public string GetCurEditModuleFolder(string iRelativeFolderPath);

// 路径辅助：获取指定模块 ID 下指定相对路径的完整路径。
virtual public string GetFolderPath(string iModuleID, string iRelativeFolderPath);
```

---

### 7.9 GUI 绘制 (GUI Rendering)

```csharp
virtual public void OnGUI(UCL_ObjectDictionary iDataDic);
```

绘制以下 Inspector 区块：
1. **EditType 切换** (仅 Editor)：切换 `ModuleEditType` 的 `Popup`。
2. **Save Config / Load Config 按钮**。
3. **开启模块文件夹按钮** (仅限 Windows Standalone)。
4. **Config 字段显示** (含 Zip/UnZip 工具按钮)。
5. **创建新模块按钮**。
6. **模块选择 Popup + 编辑按钮**。
7. **模块播放清单管理页面入口**。
8. **已加载模块清单**。
9. **AssetsCache 状态显示**。

---

## 8. 模块覆盖规则 (Module Override Rules)

> [!IMPORTANT]
> **模块优先级**：在 `m_LoadedModules` 中，**索引越大（越后加入）的模块优先级越高**。
> 
> - `GetAssetConfig()` 從后往前扫描 (`Count-1` → `0`)，这意味着**最后一个加载的模块中的资产会覆盖早期模块中同 ID 的资产**。
> - `LoadModuleAndDependencies()` 确保依赖模块先被加入，主模块最后加入，从而使主模块具备高于其依赖项的优先权。

---

## 9. 已知风险与注意事项 (Known Risks and Precautions)

| 风险 | 说明 | 缓解措施 |
|---|---|---|
| `m_CurEditModule == null` | 存储时未先调用 `EditModule` 就调用 `CreateAssetConfig`。 | 在存储前检查 `CurEditModuleID` 或显示错误并中断存储。 |
| `GetAllAssetIDs` 的 `iUseCache` 语意反转 | `true` = 刷新，`false` = 使用缓存。 | 使用前务必核对此文件中的说明。 |
| `LoadModulePlaylist` 清空所有引用 | 调用后，现有的 `UCL_Module` / `AssetConfig` 引用均失效。 | 重新通过 `GetAssetConfig` 获取最新引用。 |
| `GetJsonData()` 抛出 Exception | 文件缺失时不会返回 null。 | 调用者必须使用 `try-catch` 包裹。 |
| `ModuleEditType` setter 副作用 | 设置此项会清空 `m_ModuleCache`。 | 切换后需重新调用 `GetModule`。 |

---

## 10. 相关文件索引 (Related File Index)

| 文件 | 说明 |
|---|---|
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModuleService.cs` | 此文件的原始源代码。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_Module.cs` | 单个模块数据与配置。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModulePath.cs` | 路径计算工具。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModulePathConfig.cs` | 路径配置容器。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModulePlaylist.cs` | 播放清单数据结构。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_OnModuleLoadedAsset.cs` | 模块加载后的异步回调资产。 |
| `Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_ModuleEntry.cs` | 模块识别码与核心 ID。 |
| `Assets/UCL/UCL_Core/Docs/UCL_ModuleService_API.md` | 本文件。 |
