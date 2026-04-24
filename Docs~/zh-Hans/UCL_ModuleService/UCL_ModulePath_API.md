# UCL_ModulePath & 路径管理 API

## 1. UCL_ModulePath (静态类)
用于定义文件夹结构与管理模块发布（压缩/安装）的核心工具。

### 关键特性
- **压缩 (Zipping)**：`ZipAllModules()` 将所有标记为导出的模块压缩至 `StreamingAssets`。
- **预构建处理 (Pre-build Processing)**：`OnPreprocessBuild()` 在触发 Unity Build 前自动打包模块。

---

## 2. UCL_ModulePath.PersistantPath.ModulesEntry
管理特定 `UCL_ModuleEditType` 下的一组模块。

### 核心属性 (Core Properties)
- `RootFolder`：根路径（例如 `persistentDataPath/.Modules`）。
- `ModulesPath`：包含各个模块目录的子文件夹。
- `ConfigPath`：该根目录下全局 `Config.json` 的路径。

### 方法 (Methods)
- `LoadConfig()`：加载全局模块配置。
- `GetModulePath(string id)`：返回特定模块文件夹的绝对路径。
- `GetModuleEntry(string id)`：为特定模块返回一个 `ModuleEntry` 对象以进行路径操作。
- `ZipAllModules()`：将各个模块打包成 `.zip` 文件并存放于 `StreamingAssets`。

---

## 3. UCL_ModulePath.PersistantPath.ModuleEntry
作用范围仅限于**单个模块**的路径操作。

### 关键方法 (Key Methods)
- `Install()`：将模块从 Builtin 同步至 Runtime。支持文件夹拷贝与 `.zip` 解压。
- `UnInstall()`：从 Runtime 路径删除模块。
- `GetAssetPath(Type type, string id)`：返回特定资产 `.json` 文件的路径。
- `GetAssetFolderPath(Type type)`：返回模块内特定资产类型的目录。
- `ZipModule(string targetFolder)`：将模块压缩成 `.zip` 文件。

---

## 4. 路径配置逻辑 (Path Configuration Logic)
系统依赖 `UCL_ModulePathConfig` 来定义相对路径。

### 标准结构：
- **Root**: `ModulesRoot`
    - **Config**: `Config.json`
    - **Modules**: `Modules/`
        - `{ModuleID}/`
            - `Config.json`
            - `Resources/`
                - `{AssetType}/`
                    - `{AssetID}.json`
                    - `.CommonDataMeta` (资产元数据)
