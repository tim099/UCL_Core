# 硬編碼多國語言流程 (UCL_CodeLocalize)

## 1. 概觀
`UCL_CodeLocalize` 是一個高效能的硬編碼多國語言工具，旨在將核心 UI 字串直接儲存在 C# 程式碼中。它作為外部 JSON/CSV 本地化檔案的可靠後備方案（Fallback）以及高速替代方案。

### 為什麼使用硬編碼本地化？
*   **安全性**：關鍵 UI 字串（如「存檔」、「取消」、「錯誤」）永遠可用，即使外部資產檔案遺失也不會顯示原始 ID。
*   **效能**：使用 C# `switch` 表達式實現 O(1) 或接近 O(1) 的查詢速度，且在運行時零記憶體分配。
*   **維護性**：利用 `partial class` 將各語言的翻譯拆分到獨立檔案中。

## 2. 架構
系統由一個核心邏輯檔案與多個語系專屬的 partial 檔案組成：
*   `UCL_CodeLocalize.cs`：核心調度邏輯，基於 `UCL_LocalizeManager.s_LangName`。
*   `UCL_CodeLocalize.en.cs`：英文翻譯（最終後備）。
*   `UCL_CodeLocalize.zh-Hant.cs`：繁體中文翻譯。
*   ...（其他語言）

## 3. 如何使用

### 3.1 獲取翻譯字串
只需在代碼中調用 `UCL_CodeLocalize.Get(key)`：
```csharp
string windowTitle = UCL_CodeLocalize.Get("UCL_ModuleServiceEditPage");
```

### 3.2 後備邏輯 (Fallback Logic)
1.  系統透過 `UCL_LocalizeManager.s_LangName` 識別當前語系。
2.  嘗試在對應的語言檔案中尋找 Key。
3.  若找不到（回傳 `null`），則後退至 **英文 (en)** 版本。
4.  若英文版也找不到，則回傳 **Key** 本身。

## 4. 如何新增詞條

### 步驟 1：在語系檔案中新增內容
開啟對應的語言檔案（例如 `UCL_CodeLocalize.zh-Hant.cs`），並將鍵值對新增至 `switch` 表達式中：

```csharp
static public string Get_zhHant(string iKey)
{
    return iKey switch
    {
        "My_New_Key" => "我的新詞條",
        // ... 現有詞條
        _ => null
    };
}
```

### 步驟 2：確保英文後備
務必在 `UCL_CodeLocalize.en.cs` 中也新增該詞條，以確保其他語系的使用者在缺失翻譯時至少能看到英文說明。

```csharp
static public string Get_en(string iKey)
{
    return iKey switch
    {
        "My_New_Key" => "My New Key",
        _ => iKey // 英文分支應始終以 iKey 作為預設回傳
    };
}
```

## 5. 最佳實踐
> [!IMPORTANT]
> 請將 `UCL_CodeLocalize` 用於 **核心 UI** 與 **框架字串**。對於需要非程式人員頻繁更新的遊戲內容（如道具名稱、劇情對白），請繼續使用 `UCL_LocalizeAsset`（外部 CSV/Text 檔案）。
