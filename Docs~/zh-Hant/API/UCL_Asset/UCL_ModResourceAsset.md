# UCL_ModResourceAsset (模組資源資產)

## 1. 系統概觀
`UCL_ModResourceAsset` 是 Ringworld 專案中用於處理「模組化外部資源」的核心資產類別。它允許開發者透過 ID 系統引用存放於 Mod 資料夾內的外部圖片（Sprite 或 Texture2D），並提供異步載入與自動資源釋放機制。

## 2. 核心功能
*   **動態路徑對應**：自動根據 `ModuleID` 與資產配置定位實體檔案路徑。
*   **異步載入機制**：支援 `UniTask` 異步讀取地表、道具或角色貼圖，避免主執行緒卡頓。
*   **生命週期管理**：實作 `IDisposable` 介面，確保當資產不再使用時能正確釋放記憶體中的貼圖資源。
*   **預覽功能**：在編輯器介面中提供即時預覽與編輯入口。

## 3. 資料結構 (UCL_ModResourcesData)
資產核心資料存放於 `m_ModResourcesData` 成員中，包含：
*   `m_ModuleID`：所屬模組的唯一識別碼。
*   `m_FolderPath`：相對於模組資源根目錄的子資料夾路徑。
*   `m_FileName`：目標檔案名稱（包含副檔名）。

## 4. 使用範例 (C#)
```csharp
// 從資源條目中異步獲取 Sprite
public async UniTask SetupIcon(UCL_ModResourceEntry iEntry, CancellationToken iToken)
{
    Sprite icon = await iEntry.GetData().GetSpriteAsync(iToken);
    m_IconImage.sprite = icon;
}
```

## 5. 注意事項
> [!IMPORTANT]
> 確保模組資源存放於正確的 `ModResources/` 子目錄下，否則系統將無法根據路徑定位檔案。
