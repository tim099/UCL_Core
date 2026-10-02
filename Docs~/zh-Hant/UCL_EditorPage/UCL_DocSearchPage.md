---
title: 文件搜尋頁 (UCL_DocSearchPage)
description: 在 Editor 內全文搜尋 UCL 文件庫（Docs/ 與 UCL_Core/Docs~/）— 計分與同義詞展開在 UCL_DocSearchEngine，本頁負責 IMGUI 呈現與進階控制，每筆結果可一鍵預覽 / 開檔 / 在檔案管理員定位。
tags: [editor-page, docs, search]
aliases: [文件搜尋, doc search, 搜文件]
target_audience: [AI_Agent, Tools_User]
last_updated: 2026-10-02
---

# 🔍 文件搜尋頁 (UCL_DocSearchPage)

> 一句話：**給人用的文件搜尋** —— 計分在 `UCL_DocSearchEngine`，
> 本頁是介面，多了進階旋鈕與「找到之後怎麼打開」。

## 入口

`UCL_WelcomePage` 的「🔍 文件搜尋」按鈕。

## 跟 agent 查文件的分工

| | 本頁 | `senate cmd doc` |
|---|---|---|
| 給誰 | 人（在 Editor 裡） | agent（不需要 Editor） |
| 查哪裡 | 專案 `Docs/` ＋ `UCL_Core/Docs~/` | Senate `Docs/` ＋ SCP_Core `Docs~/` |
| 計分 | frontmatter 加權＋章節內文＋同義詞 | 逐行全文比對 |

> 兩邊查的是**不同的文件庫**，不是同一份東西的兩個入口。
> agent 要查 Senate 指令的用法 → `senate cmd help <指令>` 會印出對應文件名，再 `senate cmd doc --arg op=show --arg name=<文件>`。

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

- [`UCL_MarkdownViewerPage`](UCL_MarkdownViewerPage.md)
- [`UCL_CommonEditorPage`](UCL_CommonEditorPage.md)
