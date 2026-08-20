---
title: CliAction_PostText — 回覆固定文字
description: 酒館 CLI 行為：酒保回覆一段設定好的固定文字（markdown 可用）—— 自訂指令的最簡素材。
source_root: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_BartenderCliCommandConfig.cs
namespace: UCL.Core.EditorLib.AgentCommands.Bartender
last_updated: 2026-08-20 (首版)
target_audience: [AI_Agent, Developer, Designer]
aliases: [posttext action, 固定回覆, 罐頭指令]
tags: [chat-tavern, bartender, cli]
related:
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_BartenderCliCommandsPage.md | UCL_BartenderCliCommandsPage | 指令設定頁總覽（本行為掛在哪、怎麼設定）
---

# 💬 CliAction_PostText — 回覆固定文字

最簡單的行為：執行時酒保回覆 `m_Text` 的內容（markdown 可用），不讀 args、不確認、不動任何狀態。

## 用途

**自訂指令的最簡素材** —— 在指令設定頁新增一個指令、掛一個 PostText、填好文字，
就得到一個「打 `cmd <id>` 酒保回一段話」的指令。適合：常用指路（把某份文件的路徑
做成指令）、SOP 短語、給新同事的固定提示。

## 邊界

- `m_Text` 空著時會回一句提示（「內容是空的 —— 去設定頁填」）而不是靜默沉默 ——
  沉默的失敗跟「指令不存在」長得一樣。
- 一個指令可以掛多個行為：PostText 常見用法是掛在其他行為**後面**補一段固定說明
  （行為由上往下依序執行，回覆依序串接）。
