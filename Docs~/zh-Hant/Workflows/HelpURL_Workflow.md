# HelpURL 系統與工作流 (HelpURL System & Workflow)

## 1. 核心概念
UCL 擴充了 Unity 原生的 `HelpURLAttribute`，建立了一套支援「跨環境解析」與「多國語言支援」的幫助系統。

### 1.1 特殊前綴：`ucl_core:`
為了確保模組在不同專案中移動、或是發佈成 Build 版本後連結依然有效，我們引入了相對路徑解析：
*   **格式**：`ucl_core:Docs~/{lang}/YourDoc.md`
*   **解析邏輯 (`UCL_URL`)**：
    *   **Editor 模式**：自動解析為本地路徑 `[UCL_Core根目錄]/Docs~/{lang}/YourDoc.md`。支援離線閱讀。
    *   **Build 模式**：自動轉換為 GitHub 上的對應連結，確保玩家也能存取雲端文件。

### 1.2 本地化佔位符：`{lang}`
*   **用途**：根據當前語系自動切換文件。
*   **計算邏輯**：系統會自動將 `{lang}` 替換為 `UCL_LocalizeService.CurLang`（例如 `en`, `zh-Hans`, `ja`）。
*   **Editor 回退機制**：若當前語系文件不存在，系統在 Editor 下會嘗試尋找 `en` 版本作為回退，避免 404。

### 1.3 隱藏資料夾：`Docs~`
*   **物理意義**：Unity 會自動忽略以 `~` 結尾的資料夾。因此我們將文件放在 `Docs~` 下，這樣既能保存在模組目錄內，又不會產生 `.meta` 檔案。

---

## 2. 工作流 (Workflow)

### 步驟 A：編寫說明文件
1.  在 `Assets/UCL/UCL_Core/Docs~/{lang}/` 目錄下建立 Markdown 檔案。
    - 範例：`Docs~/zh-Hant/MyFeature.md`
2.  編寫相關功能的技術說明或操作指南。

> [!IMPORTANT]
> 若文件是針對特定的 Class，檔案命名**必須**與 Class 名稱一致（例如 `UCL_ModuleServiceEditPage.md` 對應 `class UCL_ModuleServiceEditPage`）。

### 步驟 B：掛載屬性 (HelpURL)
#### 情況 1：對於一般的資產或資料類別
直接在類別宣告上方加上 `[HelpURL]`，務必使用 `{lang}`：
```csharp
[HelpURL("ucl_core:Docs~/{lang}/API/MyFeatureAsset.md")]
public class MyFeatureAsset : UCL_ModResourceAsset { ... }
```

#### 情況 2：對於編輯器頁面 (`UCL_EditorPage`)
同樣加上 `[HelpURL]`：
```csharp
[HelpURL("ucl_core:Docs~/{lang}/UCL_EditorPage/MyFeatureEditPage.md")]
public class MyFeatureEditPage : UCL_EditorPage { ... }
```

---

## 3. 系統組件說明
*   **`UCL_URL.cs`**：負責解析 URL 字串，處理 `{lang}` 替換。
*   **`UCL_GUILayoutDrawObject.DrawHelpButton`**：GUI 層級的封裝，繪製 `?` 按鈕並呼叫 `UCL_URL.OpenURL`。
*   **`UCL_EditorPage.cs`**：頁面基類，自動快取 `HelpURL` 屬性並在 TopBar 繪製。
