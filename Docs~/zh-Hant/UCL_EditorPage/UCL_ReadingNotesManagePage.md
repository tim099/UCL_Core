---
title: 閱讀心得管理頁
description: 依作品名稱定位 legacy Archive 與新 Library metadata entry 的唯讀管理入口。
last_updated: 2026-08-06
target_audience: [Tim, Agent, Tools_Maintainer]
source_root: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_ReadingNotesManagePage.cs
related:
  - ../Plan/Plan_Library_Media_Migration.md | 閱讀圖書館媒材分類與遷移 | 資料邊界與手動遷移 SOP
---

# 閱讀心得管理頁

`UCL_ReadingNotesManagePage` 是遷移期間的**入口定位頁**：輸入作品名稱後，列出可由人手開啟的 legacy `Archive` 與新 `Library` 資料夾。

## 入口

- `UCL_ToolBoxPage` →「閱讀心得管理」
- `UCL_ControlPanelPage` →「圖書館管理」→「閱讀心得入口」

## 搜尋範圍與邊界

| 資料區 | 讀取內容 | 結果用途 |
|---|---|---|
| `AgentCommands/BookNotes/Archive/*/book.json` | 僅 `title`、`title_original`、資料夾 slug | 列出唯讀的 legacy 手動開啟入口 |
| `AgentCommands/BookNotes/Library/works/*/work.json` | `work_id`、`title` | 建立 work 標題索引 |
| `AgentCommands/BookNotes/Library/media/*/media.json` | `media_id`、`work_id`、`media_kind` | 列出新 schema media 入口 |

頁面不讀 Archive 的章節或角色正文、不推論 reader/media/合併關係，也不寫入 Archive、Library 或 migration registry。它是「找到原件後由原讀者人工遷移」的前一步，而不是 legacy reader。

例如搜尋「荒川爆笑團」時，若 metadata 已建妥，結果會同時列出 `Archive/arakawa`、`Archive/arakawa-under-the-bridge` 與 `Library/media/comic-arakawa-under-the-bridge`。

## 目前範圍

本頁目前提供搜尋與檔案總管開啟。新 schema 的建立、記錄閱讀 session、bookmark 與 registry 管理仍屬 `Plan_Library_Media_Migration.md` 的後續工作。
