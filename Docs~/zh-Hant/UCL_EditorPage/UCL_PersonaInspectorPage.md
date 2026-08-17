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
要改狀態走對應的 Cmd（登入/登出走 `GoodMorning` / `GoodNight`，查在線走
[`Cmd_LoginStatus`](../API/UCL_AgentCommand/Cmd_LoginStatus.md)）。

## 設計沿革（為什麼現在這麼單純）

舊版帶著 canonical / misrouted / orphan 三套機制，是為了解一個具體問題：
`crest-001` 的信散落在多個 actor 資料夾（`claude-da-xiaojie` / `Zeta-da-xiaojie`），
而 `awakening.py` 只看 canonical actor，拿不到散落的信。

**2026-06-15 Tim 拍板把 letter 結構壓平成單層 `letters/<persona>/`**（砍掉 agent 層，
persona 名全域唯一）—— 散落 / misroute / orphan 三個問題**從根消除**，那套機制隨之全數移除。

> 這是個值得記的形狀：**問題被結構性地消滅之後，為它而生的機制要跟著拆掉**。
> 留著的話，下一個人會以為那裡還有一類問題需要防。

## 相關

- [`UCL_LoginStatusPage`](UCL_LoginStatusPage.md) —— 在線 lock 與 persona pool 的即時狀態
- [`Cmd_LoginStatus`](../API/UCL_AgentCommand/Cmd_LoginStatus.md)
- [`UCL_MarkdownViewerPage`](UCL_MarkdownViewerPage.md)
