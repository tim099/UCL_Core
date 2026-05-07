---
title: UCL_WelcomePage
description: UCL_Core 的歡迎/總覽頁；首次安裝或大版本升級時自動彈出，介紹 UCL_Asset / Localize / Agent Commands / Editor Pages 等核心功能與快速跳轉按鈕
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_WelcomePage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [welcome, getting started, 歡迎, 總覽, overview, 入門, 首頁, 自動彈出, auto open, first install]
tags: [editor_page, onboarding, welcome]
---

# UCL_WelcomePage

## 1. 概覽

`UCL_WelcomePage` 是 UCL_Core 跨專案套件的**歡迎 / 總覽頁**，解決「新手第一次接觸 UCL_Core 不知從何入手」的問題。

三種觸發方式：

| # | 觸發 | 用途 |
|---|---|---|
| 1 | **自動彈出**（首次安裝 / 大版本升級）| 由 [`UCL_WelcomeAutoOpen`](../../../UCL_WelcomeAutoOpen.cs) 透過 `[InitializeOnLoad]` 偵測 |
| 2 | EditorMenu 主頁 → 「👋 Welcome / 總覽」按鈕 | 隨時手動回顧 |
| 3 | 選單 `UCL → Welcome` | 不需先開 EditorMenu 視窗 |

## 2. 自動彈出的偵測邏輯

```text
[Domain reload]
       │
       ▼
[InitializeOnLoad] static ctor → EditorApplication.delayCall +=
       │
       ▼
TryAutoOpen():
  if EditorPrefs(AutoOpenDisabled) → 跳過
  if EditorPrefs(ShownVersion) == UCL_WelcomePage.CurrentVersion → 跳過
  else → 寫入 ShownVersion + 呼叫 UCL_WelcomePage.OpenAndShow()
```

控制 EditorPrefs：

| Key（樣板） | 型別 | 預設 | 說明 |
|---|---|---|---|
| `UCL_Core.Welcome.ShownVersion@<projHash>` | string | `""` | 已展示的內容版本；與 `CurrentVersion` 不同則彈出 |
| `UCL_Core.Welcome.AutoOpenDisabled@<projHash>` | bool | `false` | 使用者主動關閉自動彈出（保留手動入口）|

`<projHash>` = `Application.dataPath.GetHashCode()` 16 進位字串。

> [!IMPORTANT]
> EditorPrefs 在 Unity 內是**每使用者 / 每機器**全域共用，per-project hash 後綴把每個專案各自隔離 — A 專案看過 Welcome **不會**再把 B 專案的彈出抑制掉。新人 clone 任何含 UCL_Core 的專案，第一次都會看到 Welcome 一次。

## 3. 內容區塊

| 區塊 | 內容 |
|---|---|
| Header | 歡迎標題 + 當前 Welcome 版本號 |
| 簡介 | 一段話解釋「UCL_Core 是什麼」 |
| 功能卡片 | 核心功能 × 4（UCL_Asset / Localize / Agent Commands / Editor Pages）每張附主按鈕 + 文件連結 |
| 文件總入口 | UCL_Core 的 Architecture / Workflow / API 連結 |
| 腳註 | 「不再自動彈出」勾選 + 「重設首次彈出」按鈕 |

## 4. 升級內容版本

當 Welcome 頁內容有重大變更（例如新增第五張功能卡），改 `UCL_WelcomePage.CurrentVersion`：

```csharp
public const string CurrentVersion = "1";  // → "2"
```

下次 Editor 啟動，已關閉自動彈出的使用者**仍會被尊重不彈**；但 `AutoOpenDisabled=false` 的使用者會看到新版彈一次。

## 5. 跨頁導航設計

`UCL_WelcomePage.OpenAndShow()` 必須跨 `EditorWindow` 與 `UCL_GUIPageController` 兩層：

```csharp
public static void OpenAndShow()
{
    // 1) 設置 hook：下次 EditorMenuPage 第一次繪製時 push WelcomePage
    UCL_EditorMenuPage.s_OnFirstDraw = (controller) =>
    {
        UCL_EditorPage.Create<UCL_WelcomePage>(controller);
    };
    // 2) 開啟視窗（會 trigger OnGUI → EditorMenuPage 繪製）
    UCL_MenuWindow.ShowMenu();
    // 3) 標記已展示
    EditorPrefs.SetString(PrefKey_ShownVersion, CurrentVersion);
}
```

> [!NOTE]
> 為什麼不直接在 `OpenAndShow()` 裡 `UCL_EditorPage.Create<UCL_WelcomePage>()`？
> 因為視窗的 `m_GUIPageController` 是私有的，外部呼叫 `Create<T>()` 會 fall back 到 `UCL_GUIPageController.Ins`（singleton）—— 那個不是視窗實際在用的 controller。所以改用「先 Show 視窗，鉤子等到視窗自己 OnGUI 時才把 page push 進去」的模式，確保 push 到正確的 controller。

## 6. 已知限制 & 設計取捨

| 限制 | 解法 |
|---|---|
| EditorPrefs 是 per-user 不會跟 git；新人 clone 才會看到彈出 | 想要團隊強制觸發可改用 `Library/` 內檔案，但 Library 也是 gitignore，效果一樣。EditorPrefs 是合適的選擇 |
| `[InitializeOnLoad]` 在 domain reload 時都會跑 | 用 `delayCall` 推遲 + EditorPrefs 比對版本號避免重複彈 |
| 內容是硬寫的 IMGUI | 簡單可控；若要 markdown-driven 可改讀 `Docs~` 內 .md 並用簡易 markdown renderer |

## 7. 關聯文件

- [UCL_EditorMenuPage](UCL_EditorMenuPage.md) — EditorMenu 主頁（含 Welcome 按鈕）
- [UCL_AgentCommand_Architecture](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md) — Agent Command 系統架構
