# UCL_Module & UCL_ModuleEntry API

## 1. UCL_Module
模块特定逻辑与数据的主要容器。负责单个模块的加载、存储与安装。

### 核心属性 (Core Properties)
- `ID`：模块的唯一识别码。
- `ModuleEditType`：当前编辑模式（`Builtin` 或 `Runtime`）。
- `ModuleEntry`：为此模块实例提供特定于路径的操作。
- `m_Config`：存储元数据，如 `Version` (版本)、`Title` (标题)、`Description` (描述) 以及 `DependenciesModules` (依赖模块)。

### 关键方法 (Key Methods)
- `Load(string id, UCL_ModuleEditType type)`：從指定来源加载模块配置。
- `Save()`：将当前模块配置持久化存储至其 `Config.json`。
- `CheckAndInstall()`：将当前版本与内置版本进行比较，若需要更新则触发 `Install()`。
- `Install()`：将模块内容从内置来源拷贝或解压至运行时系统的持久化存储中。
- `ExportModule(bool exportConfig)`：将模块压缩以供发布。
- `GetAssetMeta(string typeName)`：检索特定资产类型的分组与排序元数据。

---

## 2. UCL_ModuleEntry
一个轻量级的可序列化类，用于通过 ID 引用模块。常用於 Inspector 弹出窗口与依赖清单。

### 核心属性 (Core Properties)
- `ID`：引用模块的 ID。
- `Module`：通过 `UCL_ModuleService` 延迟加载并返回完整的 `UCL_Module` 实例。

### 静态辅助工具 (Static Helpers)
- `CoreModuleID`：系统“核心” (Core) 模块的常量。
- `CoreModule`：为核心模块返回预先配置好的 `UCL_ModuleEntry`。

---

## 3. 相关枚举 (Related Enums)

### UCL_AssetType
定义资产的不同根目录位置：
- `StreamingAssets`：应用程序只读文件夹内的资产。
- `PersistentDatas`：用户可写入数据路径中的资产。
- `BuiltinModules`：内置模块源文件的根目录。
- `SteamMods`：Steam 创意工坊内容的路徑。

### ELoadingState
追踪异步加载进度：
- `None`, `Loading`, `Complete`, `Disposed`。
