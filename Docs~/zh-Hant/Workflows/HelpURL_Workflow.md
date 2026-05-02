# HelpURL 系統與工作流 (HelpURL System & Workflow)

## 1. 核心概念
UCL 擴充了 Unity 原生的 `HelpURLAttribute`，建立了一套支援「跨環境解析」、「多國語言支援」與「下游模組擴充」的幫助系統。

### 1.1 特殊前綴與 Prefix Resolver 機制
UCL_URL 採用 **Resolver 註冊表** 架構。任何一段 `xxx:RelativePath` 形式（且冒號後不接 `//`）的 URL，都會去查詢已註冊的 Resolver：

*   **格式**：`{prefix}:Docs~/{lang}/YourDoc.md`
*   **解析邏輯 (`UCL_URL`)**：
    *   **命中 prefix**：呼叫該 Resolver 的 `Resolve`。Editor / Build 的差異由 **Resolver 註冊端** 在 `#if UNITY_EDITOR` 中決定要傳入哪一個委派，介面本身只暴露單一 `Resolve` 方法。
    *   **未命中 prefix**：保留原 URL，續流走 `{lang}` 替換與本地路徑補全。

> [!NOTE]
> UCL_Core 自身的 `ucl_core:` prefix 也是透過註冊機制掛上去的，沒有特例。下游模組要新增自家 prefix（例如 `eov_docs:`）只要在啟動時註冊一次即可，**不需要修改 UCL_Core**。

### 1.2 本地化佔位符：`{lang}`
*   **用途**：根據當前語系自動切換文件。
*   **計算邏輯**：系統會自動將 `{lang}` 替換為 `UCL_LocalizeService.CurLang`（例如 `en`, `zh-Hans`, `ja`）。
*   **Editor 回退機制**：若當前語系文件不存在，系統在 Editor 下會嘗試尋找 `en` 版本作為回退，避免 404。
*   **歸屬**：`{lang}` 由 `UCL_URL` 共用層處理，**Resolver 端不必各自重複實作**。

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

## 3. 為下游模組擴充自定 Prefix

### 3.1 何時需要擴充？
當你的下游專案（例如非開源的遊戲本體、但文件本身為公開的開源 repo）希望讓 `[HelpURL]` 同時支援自家文件，又不能在 UCL_Core 內寫死自家 URL 時。

### 3.2 註冊方式：Lambda 版（推薦）
最常見的情境只需要 `Path.Combine` / 字串拼接，使用 `UCL_UrlPrefixResolver` 即可，免實作介面：

```csharp
using UCL.Core;
using UnityEngine;

/// <summary>
/// [職責] 為遊戲本體（EoV）註冊 "eov_docs:" 前綴，將 HelpURL 導向專用的 EmblemOfValorDocuments 文件 repo。
/// [物理意義] Editor 端指向本地 submodule 路徑、Build 端指向 GitHub blob 連結。
/// </summary>
public static class EoV_DocsResolverBootstrap
{
    // [常數] 雲端文件根 URL；Build 版本由此 GitHub repo 提供。
    private const string BUILD_BASE_URL = "https://github.com/tim099/EmblemOfValorDocuments/blob/main/";

    /// <summary>
    /// [職責] 在 Runtime 啟動初期註冊 Resolver，確保任何 OpenURL 呼叫前已就緒。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoadMethod]
#endif
    private static void Register()
    {
        // 區塊職責：建立並註冊 EoV 專屬的 Prefix Resolver。
        // 物理意義：Editor 端組合本地 submodule 路徑；Build 端拼接雲端 URL。
        // 數值影響：影響所有以 "eov_docs:" 起頭的 HelpURL 連結最終目標。
        UCL_URL.RegisterResolver(new UCL_UrlPrefixResolver(
            prefix: "eov_docs",
#if UNITY_EDITOR
            // [Editor] 直接接於本地 submodule 路徑之後，便於離線閱讀。
            resolver: (aRelativePath) => System.IO.Path.Combine(EoV_DocsPath.Root, aRelativePath)
#else
            // [Build] 拼接 GitHub blob 連結，玩家可以直接用瀏覽器開啟。
            resolver: (aRelativePath) => BUILD_BASE_URL + aRelativePath
#endif
        ));
    }
}
```

### 3.3 註冊方式：實作介面版
若 Resolver 邏輯較複雜（需要狀態、條件分支），可直接實作 `IUCL_UrlPrefixResolver`：

```csharp
public sealed class MyComplexResolver : IUCL_UrlPrefixResolver
{
    public string Prefix => "my_proj";
    public string Resolve(string relativePath)
    {
#if UNITY_EDITOR
        // [Editor] 解析為本地路徑
        return /* ... */;
#else
        // [Build] 解析為雲端 URL
        return /* ... */;
#endif
    }
}
```

### 3.4 使用註冊後的 Prefix
與 `ucl_core:` 完全一致：

```csharp
[HelpURL("eov_docs:Docs~/{lang}/Mechanics/CombineSetting.md")]
public class CombineSettingAsset { ... }
```

> [!IMPORTANT]
> 註冊時機坑：若 `UCL_URL.OpenURL` 可能在你的 Resolver 註冊之前被呼叫，連結會解析失敗。請務必同時掛 `[InitializeOnLoadMethod]`（Editor）與 `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`（Runtime），兩條都要。

> [!NOTE]
> 同 prefix 後註冊勝出，UCL_URL 會輸出 Warning 但允許覆寫；下游可藉此替換 UCL 預設的雲端 URL（例如指向自家 fork）。

---

## 4. 系統組件說明
*   **`UCL_URL.cs`**：URL 解析主流程，擁有 prefix → resolver 註冊表，並負責 `{lang}` 替換與 en 回退。
*   **`IUCL_UrlPrefixResolver`**：Resolver 契約介面（與 `UCL_URL` 同檔），只定義 `Prefix` 與單一 `Resolve` 方法；Editor / Build 差異由註冊端負責切換。
*   **`UCL_UrlPrefixResolver`**：以 Lambda 委派為策略的 Resolver 輕量實作，省去下游為單一 prefix 開新類別。
*   **`UCL_GUILayoutDrawObject.DrawHelpButton`**：GUI 層級的封裝，繪製 `?` 按鈕並呼叫 `UCL_URL.OpenURL`。
*   **`UCL_EditorPage.cs`**：頁面基類，自動快取 `HelpURL` 屬性並在 TopBar 繪製。
