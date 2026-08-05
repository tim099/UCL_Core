---
name: ucl-coding
description: |
  UCL_Core C# 撰寫規範入口 — 動 C# 之前該知道的硬規則與慣例。
  涵蓋：外部 Process 一律走 UCL_ProcessRegistryService（防屍潮）、設定與 JSON 資料的 typed model 原則、
  字串 key 常數化、註解規範、以及「該用哪個既有基建而不是自己重造」的指路。
  觸發詞（case-insensitive substring，任一命中即 lazy-load）：
  - coding 規範 / coding standard / 撰寫規範 / 程式規範 / code style / 命名規範
  - 開 Process / Process.Start / spawn process / 子行程 / daemon / 屍潮 / 殭屍行程 / process 卡死
  - JsonData / typed model / 設定檔欄位 / EditorPrefs key / const string / 字串 key
  - 註解怎麼寫 / 區塊職責 / 物理意義 / 數值影響
  - 我要新增 C# 檔 / 要改 UCL_Core 的 code / 這段該放哪
---

# UCL Coding — C# 撰寫規範入口

> 一句話：**動 C# 之前先確認「這件事有沒有既有基建」** —— UCL_Core 最常見的錯不是寫錯，
> 是自己重造一套已經存在的東西，而重造出來的那套通常少了原版踩過坑之後補上的防護。

## 規範本體（本 skill 只是指路，細節不在這裡重抄）

| 主題 | 文件 |
|---|---|
| C# 撰寫規範（設定/JSON、字串 key、**外部 Process**） | `ucl_core:Docs~/{lang}/Agent/Coding_Standards.md` |
| 程式碼註解規範（區塊職責 / 物理意義 / 數值影響） | `ucl_core:Docs~/{lang}/Agent/Code_Comment_Standards.md` |
| 文件撰寫與 AI 可讀性 | `ucl_core:Docs~/{lang}/Agent/AI_READABILITY_GUIDELINES.md` |
| UCL_Core 路徑解析（不要寫死安裝路徑） | skill `ucl-core-paths` |
| 新 Asset（持久化資料一律 `UCL_Asset<T>`） | skill `ucl-create-asset` |
| 新 AgentCommand handler | skill `ucl-create-cmd` |
| 改完 .cs 怎麼確認真的編過 | skill `ucl-compile-error` |

## 🖥 寫 Editor 頁 / 任何 IMGUI

**不要直接堆 `GUILayout` 原生 API** —— UCL_Core 有一整層封裝，處理了 DPI 縮放、樣式一致性、
搜尋式下拉、折疊狀態快取等等，而那些是原生 API 沒有的。

| 要做什麼 | 走哪裡 |
|---|---|
| 頁面骨架（`WindowName` / `ContentOnGUI` / `TopBarButtons` / `HelpURL`） | `ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_CommonEditorPage.md` |
| 建新頁的完整流程與地雷 | `ucl_core:Docs~/{lang}/Workflows/Create_EditorPage_Workflow.md` |
| 版面元件（popup / 搜尋下拉 / 各種 field） | `ucl_core:Docs~/{lang}/API/UCL_GUILayout/UCL_GUILayout_Overview.md` |
| 樣式與 DPI 縮放（`ButtonStyle` / `LabelStyle` / `TextFieldStyle` / `GetScaledSize`） | `ucl_core:Docs~/{lang}/API/UCL_GUIStyle/UCL_GUIStyle_Overview.md` |

踩過的具體幾條：
- **`ContentOnGUI` 內不要再開 ScrollView** —— base 已經包好，再包一層是雙捲軸。
- 寬度用 `UCL_GUIStyle.GetScaledSize(n)`，不要寫死像素（高 DPI 下會壞）。
- `TextField` 用 `UCL_GUIStyle.TextFieldStyle`，不是 `LabelStyle`（外觀對但行為不對）。
- `UCL_GUILayout.PopupSearchCache` **選項為 0 時會 LogError** → 沒選項就整區隱藏。
- 折疊狀態的 `UCL_ObjectDictionary` **不要跟 PopupSearchCache 共用** ——
  資料重載路徑上的 `Clear()` 會把折疊值一併清掉（症狀是「收不起來」，看起來像 key 撞名）。

## ⛔ 三條最常被違反的硬規則

**① 開外部 Process 一律登記 `UCL_ProcessRegistryService`。**
domain reload 會清掉 C# 的 `Process` 物件，但 OS 層的 process **不會跟著死** ——
每次重編再生一顆，舊的變孤兒，累積起來就是**屍潮**（重複開 process 直到電腦卡死）。
`KillAllByTag` → `Start` → `Register` → 結束時 `Unregister`。
參考實作 `UCL_ScreenStreamDaemon`。細節見 Coding_Standards.md「外部 Process」。

**② 持久化資料一律繼承 `UCL_Asset<T>`**，禁止裸 `ScriptableObject` 或自寫存檔（見 `ucl-create-asset`）。

**③ 不要寫死 UCL_Core 的安裝路徑** —— 各專案掛載位置不同，寫死跨專案必壞，
而且通常是**靜默壞**（`File.Exists` 失敗後 fail-soft return，連 warning 都沒有）。見 `ucl-core-paths`。

## 判準：什麼時候該停下來找既有基建

動手前先問一次：**「這件事聽起來像不像已經有人做過？」** 以下全部都有既有基建，
自己寫一套的代價是少掉原版踩坑後補的防護：

| 你想做的事 | 既有基建 |
|---|---|
| 開外部 process | `UCL_ProcessRegistryService` |
| 找 repo root / Unity project root / AgentCommands 目錄 | `UCL_RepoPath` |
| 用檔案管理器開啟路徑 | `UCL_ExplorerUtil` |
| 存持久化資料 | `UCL_Asset<T>` |
| 頁面設定記住上次的值 | `EditorPrefs`（key 用 `const string`） |
| 搜尋式下拉選單 | `UCL_GUILayout.PopupSearchCache`（⚠ 選項為 0 時會 LogError，要先擋） |
| 二次確認彈窗 | `UCL_OptionPage.Create(title, msg, ButtonData…)` |
| 多語系字串 | `UCL_CodeLocalize.Get(key)`（**四語系檔都要加**；少鍵不會編譯錯，只會顯示成鍵名） |
| 非阻塞跑外部工具 | `Task.Run` + `BeginOutputReadLine`/`BeginErrorReadLine`（單讀一個 stream 會 deadlock） |

## 延伸

改完 code 要同步文件 → skill `ucl-update-docs`；提交 → skill `ucl-commit`。
