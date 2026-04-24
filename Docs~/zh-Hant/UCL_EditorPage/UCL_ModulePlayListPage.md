# UCL_ModulePlayListPage (模組播放清單管理頁面)

## 1. 系統概覽
`UCL_ModulePlayListPage` 是一個專用的編輯器分頁，用於管理 `UCL_ModulePlaylist`（模組播放清單）。它允許開發者定義多組不同的模組加載組合，以便於測試、環境切換或發佈不同版本的 Mod 集合。

## 2. 核心功能
*   **清單選擇 (Playlist Selection)**：列出所有存放於 `PersistentDataPath` 下的 `.json` 格式播放清單。
*   **動態建立**：支援快速建立新的播放清單（預設會包含必要的 `Core` 核心模組）。
*   **即時加載**：提供「Load current playlist」按鈕，呼叫 `LoadModulePlaylistAsync` 立即重新初始化模組服務。
*   **編輯模式切換**：
    *   **Select 模式**：進行播放清單的選擇與建立。
    *   **Edit 模式**：編輯清單內容，包括加載順序與啟用狀態。

## 3. 操作流程
1.  **進入頁面**：通常從 `UCL_ModuleService` 的主分頁中點擊「播放清單管理」進入。
2.  **編輯與儲存**：在清單中勾選需要的模組並調整順序，點擊「Save」將配置持久化。
3.  **應用生效**：點擊「Load current playlist」，系統會清空當前加載的模組並按新清單重新加載。

## 4. 關鍵組件
*   **`UCL_ModulePlaylist`**：資料實體類別，定義了模組的 ID 清單與加載邏輯。
*   **`UCL_ModuleService.LoadModulePlaylistAsync`**：執行核心加載管線的異步方法。

> [!IMPORTANT]
> 變更播放清單會導致當前所有已加載的 `UCL_Module` 實例被釋放並重新產生，請確保在切換前沒有正在執行的重要邏輯。
