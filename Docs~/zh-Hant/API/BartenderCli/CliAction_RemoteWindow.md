---
title: CliAction_RemoteWindow — 開關遠端視窗協作
description: 酒館 CLI 行為：讀 args 開關遠端視窗協作（on [permanent] / off）；on permanent 需二次確認，回覆一律帶回讀值。
source_root: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_BartenderCliCommandConfig.cs
namespace: UCL.Core.EditorLib.AgentCommands.Bartender
last_updated: 2026-08-20 (首版)
target_audience: [AI_Agent, Developer]
aliases: [remote-window action, 遠端視窗協作指令]
tags: [chat-tavern, bartender, cli, remote-window]
related:
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_BartenderCliCommandsPage.md | UCL_BartenderCliCommandsPage | 指令設定頁總覽（本行為掛在哪、怎麼設定）
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_RemoteWindowControl.cs | UCL_RemoteWindowControl | 被開關的能力本體（切窗／游標／鍵盤）
---

# 🖥 CliAction_RemoteWindow — 開關遠端視窗協作

預設掛在指令 `remote-window` 上。讀第 1 個 arg 決定方向：

| 打法 | 效果 | 二次確認 |
|---|---|---|
| `cmd remote-window on` | 只開**本次 Editor session**（domain reload 後重置 —— 刻意的護欄） | 不用 |
| `cmd remote-window on permanent` | 連永久開關一起開（跨重編／重啟自動恢復） | **要** —— 等於拆一道護欄 |
| `cmd remote-window off` | 本次＋永久一起關 | 不用（關護欄的反方向不確認 —— 反過來會訓練人無腦按 Y） |

- `permanent` 寬容接受 `--permanent` / `perm` / `永久` —— 打錯字靜默降級成「只開本次」
  是最難查的失敗（使用者以為開了永久，重編後它是關的）。
- 回覆一律帶**回讀值**（`Enabled` / `PersistEnabled` 讀回來印），不是「我設定了什麼」——
  「我設定了」與「它現在是這樣」是兩件事。
- 其餘護欄不受本指令影響：偵測使用者操作後暫停、閒置秒數、送出前前景驗證、
  流程中使用者操作即中斷（abort-on-user-input）。
