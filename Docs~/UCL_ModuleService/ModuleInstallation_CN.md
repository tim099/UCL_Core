# UCL 模組安裝機制 (Module Installation Mechanism)

## 1. 架構概述
UCL (Unity Core Library) 的模組系統設計旨在支持「內置資源保護」與「運行時動態擴充」。模組主要分為兩種編輯類型 (`UCL_ModuleEditType`)：

*   **Builtin (內置)**：
    *   **路徑**：開發階段位於 `Assets/../.BuiltinModules`；發佈後打包為 `.zip` 存放於 `StreamingAssets`。
    *   **物理意義**：作為遊戲發佈時的原始資源包，通常為唯讀狀態。
*   **Runtime (運行時)**：
    *   **路徑**：裝置的 `PersistentDataPath` (持久化資料夾)。
    *   **物理意義**：模組實際被讀取與執行的位置，支持玩家自定義 Mod 或編輯器即時修改。

## 2. 安裝流程 (Installation Flow)
當遊戲啟動並初始化 `UCL_ModuleService` 時，會觸發自動安裝與版本比對機制。

### 2.1 檢查階段 (`CheckAndInstall`)
系統會遍歷配置中所有標記為需要輸出的模組：
1.  **初始安裝**：若 `Runtime` 目錄下不存在該模組，則判定需要安裝。
2.  **版本與時間戳比對**：
    *   讀取 `Builtin` 端 (StreamingAssets) 與 `Runtime` 端 (PersistentData) 的 `Config.json`。
    *   **強制更新**：若 `Builtin` 的 `m_Version` 或 `m_UTC_TimeStamp` 較新，則執行覆蓋安裝，確保核心邏輯更新。
    *   **保留用戶修改**：若 `Runtime` 端的時間戳較新，系統會判定玩家或開發者在本地編輯過，為了保護數據，會跳過自動安裝。

### 2.2 執行安裝 (`Install`)
1.  **清理舊資料**：為了防止檔案殘留導致的邏輯錯誤，安裝前會刪除 `Runtime` 下該模組的舊資料夾。
2.  **資料還原**：
    *   **發佈版本**：從 `StreamingAssets` 非同步讀取 `.zip` 壓縮檔，並解壓至目標路徑。
    *   **編輯器模式**：根據 `EditorInstallMode` 設定，可選擇直接「拷貝資料夾」以加速開發，或模擬「壓縮/解壓」流程以驗證發佈穩定性。

## 3. 關鍵組件
*   **`UCL_ModuleService`**：全局服務，負責初始化與調度各模組的安裝任務。
*   **`UCL_Module`**：封裝單個模組的邏輯，提供 `CheckAndInstall()` 方法。
*   **`UCL_ModulePath.PersistantPath`**：統一管理不同環境下的路徑映射規則。

## 4. 常見問題與限制
*   **模組相依性**：目前在安裝階段是並行或依序處理，相依模組（如 `Core`）通常會優先載入。
*   **非同步處理**：所有安裝行為均為 `async UniTask`，避免在大型模組解壓時造成遊戲畫面凍結。
*   **平台差異**：在 Android 平台上，讀取 `StreamingAssets` 需要特殊的非同步流處理，此邏輯已封裝在 `UCL_StreamingAssets` 中。
