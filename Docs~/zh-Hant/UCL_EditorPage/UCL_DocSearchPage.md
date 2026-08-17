---
title: 文件搜尋頁 (UCL_DocSearchPage)
description: 在 Editor 內全文搜尋 UCL 文件庫 — 與 Cmd_SearchDocs 共用同一套計分與同義詞展開邏輯，差別在 IMGUI 呈現與進階控制，每筆結果可一鍵預覽 / 開檔 / 在檔案管理員定位。
tags: [editor-page, docs, search]
aliases: [文件搜尋, doc search, 搜文件]
target_audience: [AI_Agent, Tools_User]
last_updated: 2026-08-17
---

# 🔍 文件搜尋頁 (UCL_DocSearchPage)

> 一句話：**`Cmd_SearchDocs` 的 GUI 版** —— 同一套 `UCL_DocSearchEngine`，
> 換成人看的介面，多了進階旋鈕與「找到之後怎麼打開」。

## 入口

`UCL_WelcomePage` 的「🔍 文件搜尋」按鈕。

## 跟 Cmd_SearchDocs 的關係

**計分與同義詞展開共用 `UCL_DocSearchEngine` 同一份實作** —— 這是刻意的：
兩份搜尋邏輯的漂移症狀是「agent 搜到的跟人搜到的不一樣」，而它不會報錯。

差別只在外圍：

| | Cmd_SearchDocs | 本頁 |
|---|---|---|
| 呈現 | 回傳檔（給 agent 讀） | IMGUI 清單 |
| 進階控制 | 參數 | `mode` / `limit` / 同義詞路徑 / `includeArchived` 都在畫面上 |
| 開檔 | 回路徑 | 每筆可**預覽 / 開啟 / 定位** |

## 每筆結果的三個出口

| 按鈕 | 行為 |
|---|---|
| **📄 預覽** | Push 一頁 [`UCL_MarkdownViewerPage`](UCL_MarkdownViewerPage.md) 內嵌渲染，**不離開 Unity 視窗**；按 Back 回搜尋結果 |
| **📖 Open** | 走 OS 預設 .md 應用 |
| 定位 | 在檔案管理員中選取該檔 |

> 預覽與 Open **刻意並存**：內嵌頁看得快，OS 應用能編輯。
> 2026-08-17 起頁面 TopBar 的「?」說明按鈕也改用同一套內嵌預覽
> （見 [HelpURL_Workflow §5](../Workflows/HelpURL_Workflow.md)）。

## 效能

冷啟動掃 200+ 篇 markdown（SSD < 200ms）；結果 **cache 在 page 實例內**，
重繪不會重掃 —— IMGUI 每次 repaint 都會跑 `ContentOnGUI`，不快取等於每幀掃磁碟。

## 相關

- [`Cmd_SearchDocs`](../API/UCL_AgentCommand/Cmd_SearchDocs.md)
- [`UCL_MarkdownViewerPage`](UCL_MarkdownViewerPage.md)
- [`UCL_CommonEditorPage`](UCL_CommonEditorPage.md)
