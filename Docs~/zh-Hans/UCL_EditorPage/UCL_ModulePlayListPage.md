# UCL_ModulePlayListPage (模块播放清单管理页面)

## 1. 系统概览
`UCL_ModulePlayListPage` 是一个专用的编辑器分页，用于管理 `UCL_ModulePlaylist`（模块播放清单）。它允许开发者定义多组不同的模块加载组合，以便于测试、环境切换或发布不同版本的 Mod 集合。

## 2. 核心功能
*   **清单选择 (Playlist Selection)**：列出所有存放于 `PersistentDataPath` 下的 `.json` 格式播放清单。
*   **动态创建**：支持快速创建新的播放清单（默认会包含必要的 `Core` 核心模块）。
*   **即时加载**：提供“Load current playlist”按钮，调用 `LoadModulePlaylistAsync` 立即重新初始化模块服务。
*   **编辑模式切换**：
    *   **Select 模式**：进行播放清单的选择与创建。
    *   **Edit 模式**：编辑清单内容，包括加载顺序与启用状态。

## 3. 操作流程
1.  **进入页面**：通常从 `UCL_ModuleService` 的主分页中点击“播放清单管理”进入。
2.  **编辑与存储**：在清单中勾选需要的模块并调整顺序，点击“Save”将配置持久化。
3.  **应用生效**：点击“Load current playlist”，系统会清空当前加载的模块并按新清单重新加载。

## 4. 关键组件
*   **`UCL_ModulePlaylist`**：数据实体类别，定义了模块的 ID 清单与加载逻辑。
*   **`UCL_ModuleService.LoadModulePlaylistAsync`**：执行核心加载管线的异步方法。

> [!IMPORTANT]
> 变更播放清单会导致当前所有已加载的 `UCL_Module` 实例被释放并重新产生，请确保在切换前没有正在执行的重要逻辑。
