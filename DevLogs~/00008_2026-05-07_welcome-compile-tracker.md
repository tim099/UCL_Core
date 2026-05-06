---
date: 2026-05-07
index: 00008
title: UCL_WelcomePage + UCL_CompileErrorTracker + check_compile.py — 新人引導 + 編譯錯誤排查
tags: [feature]
---

# UCL_WelcomePage + UCL_CompileErrorTracker + check_compile.py

## What

兩條獨立但配合度高的新功能：

### 1. UCL_WelcomePage — 跨專案歡迎頁

[`UCL_WelcomePage`](../UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_WelcomePage.cs) 解決「新人裝完 UCL_Core 不知從哪開始」問題。

**三種觸發**：
1. 首次安裝自動彈出（[`UCL_WelcomeAutoOpen`](../Editor/UCL_WelcomeAutoOpen.cs) 透過 `[InitializeOnLoad]` + EditorPrefs 比對版本號偵測）
2. EditorMenu 主頁「👋 Welcome」按鈕
3. 選單 `UCL → Welcome`

**頁面內容**（4 語系 i18n via `UCL_CodeLocalize`）：
- Header 標題 + 版本號
- **🌐 語言切換列** — 直接從 Welcome 切，不必跳 LocalizeEditPage
- **🔍 開啟文件搜尋頁** 按鈕 → push UCL_DocSearchPage
- 4 張功能卡：UCL_Asset / Localize / Agent Commands / Editor Pages（每張附跳轉按鈕 + 文件連結）
- 📚 文件總入口（含 Architecture / Create_Cmd / Validate / **CompileError 排查** 連結）
- 腳註：「不再自動彈出」勾選 + 「重設首次彈出」按鈕

EditorPrefs 控制（per-user / per-machine）：
- `UCL_Core.Welcome.ShownVersion`
- `UCL_Core.Welcome.AutoOpenDisabled`

### 2. UCL_CompileErrorTracker + check_compile.py — Compile Error 排查雞生蛋解法

[`UCL_CompileErrorTracker`](../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/UCL_CompileErrorTracker.cs) 訂閱 `CompilationPipeline.assemblyCompilationFinished`，把每次 compile 的 errors / warnings 序列化到 `<gitRoot>/AgentCommands/.compile_status.json`。

[`check_compile.py`](../Tools~/AgentCommands/check_compile.py) 是 **standalone Python 工具** — 直接讀 JSON，**完全不依賴 Cmd 系統**：

```bash
# 任何狀態下都能跑（包括 Cmd handler 因 compile error 載不進來時）
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only

# Tracker 沒跑過 → fallback 解 Editor.log（含 session 邊界偵測）
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only --fallback-log

# 等下次 compile 結束才印
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --watch --watch-timeout 60
```

配套 [`Cmd_GetCompileErrors`](../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_GetCompileErrors.cs) 給 healthy 狀態的 batch 用。

## Why

### Welcome page

跨專案插件對「沒跟過開發過程的新使用者」非常不友善 — 翻 Docs~ 才知道有什麼功能、不知道優先級、不知道怎麼啟動 Editor 頁。Welcome 把「新人需要的入口」集中呈現，並透過 [InitializeOnLoad] 強迫展示一次。

### Compile Error Tracker

雞生蛋情境：
- agent 改 .cs 後 Unity 編譯失敗 → assembly 載不進來 → Cmd handler 不在 Registry → **沒辦法用任何 Cmd 查錯誤**

最需要查錯誤的時候反而沒工具。Tracker 的 JSON + standalone Python 工具繞過此限制：
- Tracker 自己住在 UCL_Core asm，相對穩定（被 game-side 錯誤拖累機率低）
- check_compile.py 完全 standalone，連 Tracker 都掛了還能 fallback 解 Editor.log

## How to use

### 體驗 Welcome

選單 `UCL → Welcome` 或 EditorMenu 主頁點按鈕。第一次安裝 Editor 啟動會自動彈一次。

### 排查 Compile Error 4 步 SOP

1. 跑 `check_compile.py --errors-only` 看 dedupe 後的錯誤
2. **交叉驗證 stale vs fresh** — 打開檔案對應行，確認錯誤訊息描述仍吻合
3. 找 root cause（cascade 錯誤別一個一個修）
4. 修後 focus Unity 觸發 recompile，循環直到 `Errors: 0`

詳細 SOP（含 8 大常見 CS 錯誤對照、asmdef 跨界 / namespace 陷阱）見 [CompileError_Diagnose_Workflow](../Docs~/zh-Hant/Workflows/CompileError_Diagnose_Workflow.md)。

## 設計決策（簡）

### Welcome 自動彈出的 chicken-and-egg

`[InitializeOnLoad]` 必須先在 domain reload 跑 ctor — 也就是說「前一次編譯成功才會跑」。第一次安裝編譯成功後 reload → AutoOpen 跑 → 自動彈窗。若首次裝就帶 compile error，彈窗不會出現（這時應該先去看 Console / 用 check_compile.py）。

### Tracker 寫入路徑

落在 `<gitRoot>/AgentCommands/.compile_status.json`（與 `queue.json` 同目錄），方便 agent 工具一致讀取；隱藏檔（`.` 起頭）但 git status 看得到，不入 commit。

### Editor.log fallback 的 session 邊界

Editor.log 累積多次 compile attempt，靠「`Asset Pipeline Refresh ... Total: N seconds`」+ `CompileScripts:` 兩條配對 marker 偵測「最後一次真正 compile」的 session 視窗。fallback 永遠標 `⚠ stale` 提醒使用者 Tracker 可用後優先信 Tracker。

## Breaking changes

無。

## 相關文件

- 👋 [UCL_WelcomePage](../Docs~/zh-Hant/UCL_EditorPage/UCL_WelcomePage.md)
- 🔧 [CompileError_Diagnose_Workflow](../Docs~/zh-Hant/Workflows/CompileError_Diagnose_Workflow.md) — 完整排查 SOP + 案例研究
- [DevLog 00007](00007_2026-05-07_docs-catalog-fuzzy-search.md) — Welcome 用到的 DocSearchPage 出自此
