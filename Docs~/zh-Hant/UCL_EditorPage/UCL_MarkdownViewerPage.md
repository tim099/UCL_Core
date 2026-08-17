---
title: Markdown 檢視頁 (UCL_MarkdownViewerPage)
description: 內嵌式 markdown 檢視頁 — 不離開 Unity 視窗就能讀 .md，支援 heading / code / table / mermaid 與 inline 語法；TopBar 另有 Reveal / OS Open / Copy raw。
tags: [editor-page, docs, markdown, viewer]
aliases: [markdown 檢視, md viewer, 文件預覽]
target_audience: [AI_Agent, Tools_User]
last_updated: 2026-08-17
---

# 📄 Markdown 檢視頁 (UCL_MarkdownViewerPage)

> 一句話：**這頁的存在價值是「不離開 Unity 視窗就能看 .md」。**

## 怎麼開

本頁沒有選單入口，一律由外部呼叫 `Create(relativePath, absolutePath)` 推進來：

| 呼叫端 | 情境 |
|---|---|
| [`UCL_DocSearchPage`](UCL_DocSearchPage.md) | 搜尋結果的「📄 預覽」按鈕 |
| **任何頁的「?」說明按鈕** | 2026-08-17 起，HelpURL 指向存在的本地 `.md` 時改開這頁 |
| `UCL_PersonaInspectorPage` 等 | 要顯示某份 md 內容時 |

## TopBar 三顆按鈕

| 按鈕 | 行為 |
|---|---|
| 📂 Reveal | 在檔案管理員中定位該檔 |
| 📖 OS Open | 交給 OS 預設 .md 應用（要編輯時走這個） |
| Copy raw | 複製原始文字 |

> **本頁是 OS 開檔的超集** —— 它能做的事包含「用 OS 開它」。
> 這正是 2026-08-17 把 `DrawHelpButton` 從 `Application.OpenURL` 改成開本頁的理由：
> 改走它不會少掉任何原本做得到的事。

## 渲染範圍

解析交給 `UCL_MarkdownParser`（純邏輯、無 IMGUI 依賴），本頁只負責「拿到 block list 後怎麼畫」：

- **heading** 字級分層
- **code** 用 **無 richText** 的 box（否則 code 裡的 `<T>` 會被吃成 tag）
- **table** 用 `HorizontalScope` 平均分欄
- **mermaid** 以樹狀縮排顯示節點 shape 與邊 label（不畫圖，但看得出結構）
- **inline**（`**` / `*` / 反引號 / 連結 / 圖片）由本頁的 `InlineFormat` 轉成 IMGUI rich-text tag

> parser 與 UI 刻意解耦：**parser 不產生 UI tag**。
> 混在一起的話，換一種呈現方式就得改 parser，而 parser 是 `Cmd_SearchDocs` 那邊也在用的。

## 效能

單檔載入後 `m_Blocks` 快取，**重繪不重 parse**（IMGUI 每 repaint 都跑一次繪製）。

## 相關

- [`UCL_DocSearchPage`](UCL_DocSearchPage.md)
- [HelpURL_Workflow §5 —— 點下「?」會發生什麼](../Workflows/HelpURL_Workflow.md)
- [`UCL_CommonEditorPage`](UCL_CommonEditorPage.md)
