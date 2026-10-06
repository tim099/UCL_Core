---
title: Persona 檢視頁 (UCL_PersonaInspectorPage)
description: 唯讀檢視 persona registry 與該 persona 的信件鏈 — 選一個 persona 看它的 metadata，並列出 baton/letters/<persona>/ 底下所有 letter，點開顯示內文。
tags: [editor-page, persona, letters, awakening]
aliases: [persona 檢視, letters debug, 信件鏈]
target_audience: [AI_Agent, Tools_User]
last_updated: 2026-08-17
---

# 🪪 Persona 檢視頁 (UCL_PersonaInspectorPage)

> 一句話：**把 persona 的 metadata 跟它的信件鏈擺在同一個畫面上**，方便對照與 debug。

## 能看什麼

| 區 | 內容 |
|---|---|
| Persona 池 | 全寬 `PopupSearchCache` 選一個 persona |
| metadata | 該 persona 的 registry 完整欄位 |
| letters | `baton/letters/<persona>/` 底下所有 letter（單層），點一封顯示 body |

`_latest.md` 是每條 chain 的最新指標。

## ⚠ 純唯讀

**不寫檔、不改 registry。** 只做顯示與「在檔案管理員中開啟」。
要改狀態走 Senate（登入 `senate cmd morning-wake`／登出 `senate cmd goodnight-*`，查在線 `senate cmd persona --arg all=1`）。

## 相關

- Senate「登入狀態」頁（`senate ui`）—— 在線 lock 與 persona pool 的即時狀態
- [`UCL_MarkdownViewerPage`](UCL_MarkdownViewerPage.md)
