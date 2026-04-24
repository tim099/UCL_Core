# UCL_SelectAssetPage (資產選擇頁面)

## 1. 系統概觀
`UCL_SelectAssetPage` 是 UCL 框架中用於管理特定類型資產（Asset）的核心導航頁面。它提供了一個結構化的列表，讓開發者可以快速搜尋、預覽、編輯或刪除模組中的各種數據資產。

## 2. 介面功能詳解

### 2.1 頂部功能列 (Top Bar)
*   **建立 [資產名稱]**：點擊後進入 `UCL_CreateAssetPage` 以建立該類型的新資產。
*   **RefreshData**：重新掃描磁碟檔案，確保列表中的數據與實體檔案同步。
*   **OpenFolder**：直接開啟該類型資產在檔案總管中的儲存目錄。
*   **幫助按鈕 (?)**：若資產類別定義了 `[HelpURL]`，會在此顯示連結按鈕。
*   **Copy 類別名稱**：一鍵複製當前資產類型的完整 C# 類別名稱。

### 2.2 資產列表與搜尋 (Asset List & Search)
*   **搜尋欄 (Search)**：支援正則表達式 (Regex) 進行模糊搜尋。符合條件的文字會以紅色標記。
*   **分頁控制 (Pagination)**：
    *   每頁預設顯示 10 個資產。
    *   提供「上一頁/下一頁」與直接輸入頁碼跳轉的功能。

### 2.3 列表項操作 (List Item Actions)
*   **資產 ID/名稱**：顯示資產的唯一識別碼或本地化名稱。
*   **Edit (編輯)**：開啟該資產的專屬編輯頁面 (`UCL_CommonEditPage`)。
*   **Preview (預覽)**：在頁面右側顯示該資產的內容快照，無需進入編輯頁面。
*   **Delete (刪除)**：彈出確認視窗後刪除資產檔案。
*   **分組編輯 (Group Edit)**：若啟用了 Meta 管理，可在列表中直接修改資產的分組資訊。

### 2.4 右側預覽區 (Preview Area)
當點擊列表中的「Preview」按鈕時，右側會根據資產實作的 `Preview` 邏輯顯示其詳細內容（如 CSV 表格內容、Sprite 圖像等）。

## 3. 開發者設定

### 3.1 關聯說明文件 (HelpURL)
開發者可以透過為資產類別添加 `[HelpURL]` 屬性，讓該頁面的幫助按鈕指向特定文檔：

```csharp
[HelpURL("ucl_core:Docs~/{lang}/API/UCL_Asset/MyCustomAsset.md")]
public class MyCustomAsset : UCL_CSVAsset { ... }
```

## 4. 注意事項
> [!TIP]
> 當資產數量龐大時，善用 **Search** 與 **Pagination** 可以大幅提升管理效率。若發現列表未顯示新建立的檔案，請點擊 **RefreshData**。
