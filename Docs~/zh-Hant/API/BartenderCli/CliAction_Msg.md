---
title: CliAction_Msg — 遠端輸入群發訊息
description: 酒館 CLI 行為：把訊息透過自動通知的遠端輸入打進指定 persona（或全部在線者）的輸入框並送出；一律二次確認，收件名單在執行時才解析。
source_root: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_BartenderCliCommandConfig.cs
namespace: UCL.Core.EditorLib.AgentCommands.Bartender
last_updated: 2026-08-20 (首版)
target_audience: [AI_Agent, Developer]
aliases: [msg action, cli 群發, 遠端群發訊息]
tags: [chat-tavern, bartender, cli, remote-notify]
related:
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_BartenderCliCommandsPage.md | UCL_BartenderCliCommandsPage | 指令設定頁總覽（本行為掛在哪、怎麼設定）
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_RemoteNotifyService.cs | UCL_RemoteNotifyService.DeliverTextTo | 實體送出序列的實作（定位→點擊→貼上→Enter）
---

# 📤 CliAction_Msg — 遠端輸入群發訊息

預設掛在指令 `msg` 上。用法：`cmd msg <persona|all> <訊息>`（`all`＝所有**在線**的 persona）。

## 硬規則

- **一律二次確認** —— 這個行為會打進別人的視窗並按 Enter，沒有不問的版本。
  確認訊息**回顯完整訊息原文**與當下在線名單 —— 確認要擋的不只是「要不要送」，
  還有「送出去的是不是我想打的那句」（錯字與被小寫化的內容只有回顯看得出來）。
- **訊息內容取指令原文**（保留大小寫與換行）——
  走小寫 token 的話 `Free Time` 會變 `free time` 而沒有任何一層報錯。
- **收件名單在執行時才解析**，不在確認時 —— 確認到執行之間有人上下線，
  用確認當下的名單會送給已經不在的人、漏掉剛上線的人。
- **遠端視窗協作是總閘**：沒開直接拒絕、一個都不送（`cmd remote-window on` 先開）。
- 逐人送出，**一個人失敗不影響其他人**，但每個人的結果都出現在群發報告裡。
- 流程中使用者動鍵鼠會中斷該目標的送出（abort-on-user-input）——
  中斷點若在貼上之後、Enter 之前，報告會照實寫「文字已輸入但未送出」。

## 已知邊界

送出序列與自動通知的 `RunOnceCore` 重複（已知債，兩份會漂）——
改序列時兩邊都要看：`DeliverTextTo` 與 `RunOnceCore`。
