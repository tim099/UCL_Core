---
title: UCL_CommonEditorPage
description: UCL 編輯器頁面的標準基底類別，提供 TypeName 標籤與 Copy 按鈕。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_CommonEditorPage.cs
namespace: UCL.Core.EditorLib.Page
---

# UCL_CommonEditorPage

## 1. 概述

`UCL_CommonEditorPage` 是 UCL 框架中所有自訂編輯器頁面的**標準基底類**。它是對 [`UCL_EditorPage`](./UCL_EditorPage.md) 的薄薄一層擴展，提供兩項通用功能：

1. **TypeName 標籤** — 自動將頁面的類別名稱顯示在頂部欄
2. **Copy 按鈕** — 一鍵將類別名稱複製到系統剪貼簿（開發時跨 page 子類跳轉很方便）

當你建立任何非 trivial 的編輯器頁面時，**繼承 `UCL_CommonEditorPage` 而非直接繼承 `UCL_EditorPage`**——你免費獲得約定俗成的頂部欄佈局，並且頁面會與 UCL 編輯器生態（[`UCL_ModuleEditPage`](./UCL_ModuleEditPage.md)、[`UCL_ModuleServiceEditPage`](./UCL_ModuleServiceEditPage.md)、[`UCL_ModulePlayListPage`](./UCL_ModulePlayListPage.md)、[`UCL_SelectAssetPage`](./UCL_SelectAssetPage.md) ⋯）視覺一致。

## 2. 提供什麼

### 2.1 類別定義（節錄）

```csharp
public class UCL_CommonEditorPage : UCL_EditorPage
{
    protected string m_TypeName;

    public override void Init(UCL_GUIPageController iGUIPageController)
    {
        base.Init(iGUIPageController);
        m_TypeName = this.GetType().Name;
    }

    protected override void TopBarButtons()
    {
        base.TopBarButtons();
        GUILayout.Label(m_TypeName, UCL_GUIStyle.LabelStyle);
        if (GUILayout.Button(UCL_LocalizeManager.Get("Copy"),
                             UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
        {
            GUIUtility.systemCopyBuffer = m_TypeName;
        }
    }
}
```

### 2.2 預設頂部欄佈局

```
┌──────────────────────────────────────────────────────┐
│ [Back] [Close] │ <你的頁面類別名> │ [Copy] │ ...      │
└──────────────────────────────────────────────────────┘
   ↑ 來自 UCL_EditorPage    ↑ 來自 UCL_CommonEditorPage
```

子類在右側擴展時，覆寫 `TopBarButtons()` 並**先呼叫 `base.TopBarButtons()`**。

## 3. 何時使用

| 情境 | 建議基底類 |
|---|---|
| 簡單頁面、不需要任何頂部欄控制 | `UCL_EditorPage` |
| **大多數自訂編輯器頁面** | **`UCL_CommonEditorPage`** ⭐ |
| 編輯單一 Module instance 的頁面 | `UCL_ModuleEditPage`（已是子類）|
| 從清單中挑選資產的頁面 | `UCL_SelectAssetPage`（已是子類）|

判斷原則：除非有強烈理由要拿掉 TypeName 標籤，否則**預設選 `UCL_CommonEditorPage`**。

## 4. 如何擴展 — 標準模式

### 4.1 最小子類

```csharp
namespace YourGame.Page
{
    public class YourEditorPage : UCL_CommonEditorPage
    {
        public override string WindowName => "你的頁面標題";

        public static YourEditorPage Create()
        {
            // ★ 用靜態工廠；不要手動 new + Push。
            return UCL_EditorPage.Create<YourEditorPage>();
        }

        protected override void ContentOnGUI()
        {
            // 你的 IMGUI 內容
            GUILayout.Label("Hello", UCL_GUIStyle.LabelStyle);
        }
    }
}
```

### 4.2 加入頂部欄按鈕

永遠**先呼叫 `base.TopBarButtons()`**，讓 `TypeName + Copy` 區塊保持在最左：

```csharp
protected override void TopBarButtons()
{
    base.TopBarButtons();   // ★ 來自 UCL_CommonEditorPage 的 TypeName + Copy

    if (GUILayout.Button("Refresh",
            UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
    {
        Reload();
    }
    if (GUILayout.Button("Run",
            UCL_GUIStyle.GetButtonStyle(Color.green), GUILayout.ExpandWidth(false)))
    {
        Execute();
    }
}
```

> [!IMPORTANT]
> 頂部欄每個按鈕都加 `GUILayout.ExpandWidth(false)`。少了這個，按鈕會貪婪地填滿水平空間，視窗變寬時整列佈局就壞掉。

### 4.3 Init 覆寫

若頁面需要類似建構子的初始化邏輯，覆寫 `Init()` 並先呼叫 `base.Init()` 確保 `m_TypeName` 被填入：

```csharp
public override void Init(UCL_GUIPageController iGUIPageController)
{
    base.Init(iGUIPageController);   // ★ 必須先呼叫
    LoadInitialData();
}
```

### 4.4 OnClose 覆寫

若使用者離開頁面時需要清理狀態：

```csharp
public override void OnClose()
{
    SaveDirtyChanges();
    base.OnClose();   // ★ 最後呼叫 base
}
```

## 5. 參考子類

下列類別繼承自 `UCL_CommonEditorPage`，展示了標準的擴展模式：

| 子類 | 用途 |
|---|---|
| [`UCL_ModuleEditPage`](./UCL_ModuleEditPage.md) | 編輯單一 Module 的設定與內容 |
| [`UCL_ModuleServiceEditPage`](./UCL_ModuleServiceEditPage.md) | 管理所有已安裝的 Module |
| [`UCL_ModulePlayListPage`](./UCL_ModulePlayListPage.md) | 管理 Module 載入順序 playlist |
| [`UCL_SelectAssetPage`](./UCL_SelectAssetPage.md) | 從型別清單中挑一個資產 |

設計新編輯器頁面時，**先讀 `UCL_ModuleEditPage`**——那是覆寫模式（Init / TopBarButtons / ContentOnGUI / OnClose 全示範）的標準範例。

## 6. 常見模式

### 6.1 用 `UCL_ObjectDictionary` 快取子狀態

許多頁面需要記住 per-foldout / per-toggle 的 UI 狀態跨幀保留。慣例：

```csharp
private UCL_ObjectDictionary m_DataDic = new UCL_ObjectDictionary();

protected override void ContentOnGUI()
{
    UCL_GUILayout.DrawObjectData(myObject, m_DataDic.GetSubDic("MyObject"), "MyObject");
}
```

### 6.2 從按鈕觸發非同步任務

使用 `UniTask` 與 `.Forget()` 避免阻塞 IMGUI thread：

```csharp
if (GUILayout.Button("執行非同步", UCL_GUIStyle.ButtonStyle, GUILayout.ExpandWidth(false)))
{
    DoWorkAsync().Forget();
}

private async UniTask DoWorkAsync()
{
    await SomeService.InitAsync(default);
    // …
}
```

### 6.3 持續重繪（資料隨外部狀態變動時）

若頁面反映的外部狀態會在沒有 GUI 輸入時變化（計時器、進度等）：

```csharp
[UCL.Core.ATTR.RequiresConstantRepaint]
public class YourEditorPage : UCL_CommonEditorPage { … }
```

## 7. 注意事項

> [!CAUTION]
> **不要省略 `base.TopBarButtons()`**。省略後 TypeName 標籤與 Copy 按鈕會消失——視覺上頁面便不再屬於 UCL 編輯器家族，而且失去開發時複製類別名稱的便利。

> [!CAUTION]
> **想要標準頂部欄時，不要直接繼承 `UCL_EditorPage`**。手動重新實作 TypeName + Copy 區塊會重複程式碼，且未來框架更新時容易漂移。

> [!IMPORTANT]
> 從外部建立頁面實例時，**永遠使用靜態工廠 `UCL_EditorPage.Create<T>()`**，不要 `new T(); UCL_GUIPageController.CurrentRenderIns.Push(p);`。工廠會處理重複頁面偵測與正確初始化。

## 8. 相關

- [`UCL_EditorPage`](./UCL_EditorPage.md) — 直接基底類
- [`UCL_ModuleEditPage`](./UCL_ModuleEditPage.md) — 標準覆寫範例
- [`UCL_GUIPage`](./UCL_GUIPage.md) — 根頁面抽象
