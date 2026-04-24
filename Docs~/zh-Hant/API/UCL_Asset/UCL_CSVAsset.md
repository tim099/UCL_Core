# UCL_CSVAsset (CSV 數據資產)

## 1. 系統概觀
`UCL_CSVAsset` 是一個專門用於處理模組化 CSV 檔案的資產類別。它繼承自 `UCL_Asset` 體系，利用 `UCL_ModResourcesData` 定位 Mod 資料夾中的實體檔案，並整合了 `UCL.Core.CsvLib` 來提供結構化的表格數據訪問。

## 2. 核心功能
*   **模組化檔案讀取**：支援從特定 Mod 的 `ModResources` 目錄下讀取 `.csv` 檔案。
*   **即時解析**：提供 `GetCSVData()` 方法，將原始 CSV 文字轉換為具備行（Row）與列（Column）操作介面的 `CSVData` 物件。
*   **異步支援**：內建 `GetCSVTextAsync`，支援在背景執行緒進行大體量文字讀取，避免 UI 凍結。
*   **內容摘要預覽**：在 Unity Inspector 或 UCL 編輯器分頁中，自動顯示 CSV 檔案的前 5 行內容，方便快速檢視數據結構。

## 3. 使用方法
### 在 C# 腳本中引用
```csharp
[SerializeField] private UCL_CSVEntry m_ConfigTable;

public void LoadConfig()
{
    CSVData data = m_ConfigTable.GetCSVData();
    if (data != null)
    {
        // 取得第一行第二列的數據
        string val = data.GetData(0, 1);
        Debug.Log($"Config Value: {val}");
    }
}
```

## 4. 資料結構
該資產內部封裝了 `UCL_ModResourcesData`：
*   `m_ModuleID`：檔案所屬的 Mod 識別碼。
*   `m_FolderPath`：相對於模組資源根目錄的路徑。
*   `m_FileName`：CSV 檔案名稱（包含 `.csv` 副檔名）。

## 5. 注意事項
> [!TIP]
> 建議檔案使用 `UTF-8` 編碼以確保中文字元在各平台上都能正確解析。
