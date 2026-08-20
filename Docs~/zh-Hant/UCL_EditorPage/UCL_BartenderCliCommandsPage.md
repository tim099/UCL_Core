---
title: UCL_BartenderCliCommandsPage — 酒館 CLI 指令設定頁
description: 酒館 CLI 指令的設定編輯視圖 —— 一指令一份 json、id 可改名、行為（action）用 SerializeReference 多型清單封裝，一個指令可依序執行多個行為。
source_root: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_BartenderCliCommandsPage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-08-20 (首版，隨指令設定化一起建立)
target_audience: [AI_Agent, Developer, Designer]
aliases: [cli commands page, 酒館 CLI 指令, cli 指令設定, bartender cli config]
tags: [chat-tavern, bartender, cli, editor]
related:
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_BartenderCliCommandConfig.cs | UCL_BartenderCliCommandConfig | 指令設定／行為介面／Store 的實作本體
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_BartenderCliService.cs | UCL_BartenderCliService | 指令解析、授權、確認與執行
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_BartenderAdminPage.md | UCL_BartenderAdminPage | 酒保管理頁（本頁入口在其「🔧 酒館 CLI」區塊）
  - ucl_core:Docs~/{lang}/API/BartenderCli/CliAction_RemoteWindow.md | CliAction_RemoteWindow | 行為：開關遠端視窗協作
  - ucl_core:Docs~/{lang}/API/BartenderCli/CliAction_Msg.md | CliAction_Msg | 行為：遠端輸入群發訊息
  - ucl_core:Docs~/{lang}/API/BartenderCli/CliAction_PostText.md | CliAction_PostText | 行為：回覆固定文字
---

# 📜 UCL_BartenderCliCommandsPage — 酒館 CLI 指令設定頁

入口：酒保管理頁 →「🔧 酒館 CLI」→「📜 指令設定」。

一指令一檔，存在 `ChatTavern/bartender/cli_commands/<id>.json`；本頁是那批檔案的編輯視圖。
目錄空著時自動生出預設三指令（help / remote-window / msg）。

## 基本概念

| 欄位 | 意義 |
|---|---|
| `id` | 使用者在酒館打的那個字（比對不分大小寫）。**id 就是檔名** —— 改 id ＝ 改名，存檔時舊檔會被清掉 |
| `enabled` | 關掉＝這個指令不存在（help 不列、打了回「沒有這個指令」） |
| `usage` / `description` | help 清單顯示的範例與說明（usage 是範例字串，含前綴照原樣顯示） |
| `actions` | 依序執行的行為清單（`[SerializeReference]` 多型）。任一行為要求二次確認，整個指令就要確認一次 |

- **存檔是顯式按鈕**（TopBar「💾 存檔」）—— id 同時是檔名，逐 keystroke 自動存會把半成品 id 寫成檔案。
- 「↻ 重新載入」會丟棄未存檔變更；「📂 開啟設定檔位置」直接開 json 所在資料夾。
- 白名單、前綴、確認逾時**不在本頁** —— 那是通道層設定，在酒保管理頁的「🔧 酒館 CLI」區塊。

## 內建行為（actions）

除 Help 外每個行為各有一份文件（設定頁裡行為標題列的「?」鈕開的就是它）；本節只留一句話定位。

| 行為 | 一句話 | 文件 |
|---|---|---|
| `CliAction_Help` | 列出所有可用指令 —— 清單由指令設定檔即時生成（enabled=false 不列），不是手寫清單（兩份清單必漂） | 本頁即其說明 |
| `CliAction_RemoteWindow` | 開關遠端視窗協作；`on permanent` 需二次確認，回覆帶回讀值 | [CliAction_RemoteWindow.md](../API/BartenderCli/CliAction_RemoteWindow.md) |
| `CliAction_Msg` | 遠端輸入群發訊息；一律二次確認，收件名單執行時才解析 | [CliAction_Msg.md](../API/BartenderCli/CliAction_Msg.md) |
| `CliAction_PostText` | 回覆一段固定文字 —— 自訂指令的最簡素材 | [CliAction_PostText.md](../API/BartenderCli/CliAction_PostText.md) |

## 自訂指令與新行為

- **新指令**：本頁清單按「＋」新增，填 id / usage / description，掛上行為，存檔。
- **新行為型別**：繼承 `UCL_BartenderCliActionBase`（`UCL_BartenderCliCommandConfig.cs`），
  覆寫 `Execute(ctx)`（必要）與 `NeedsConfirm` / `ConfirmSummary`（需要確認才覆寫）。
  多型下拉會自動列出新型別，不用註冊。ctx 帶 `Args`（小寫 token）與
  `RawLine` / `RawAfterArgs`（原文）—— **比對用前者、內容用後者**（內容走小寫 token
  會把英文訊息壓成全小寫而不報錯）。
- arg → 行為參數的 mapping 層目前刻意未做（Tim 2026-08-20 拍板）；要做時在
  `IBartenderCliAction` 與 config 之間加一層，不要在各行為裡各自發明。

## 資料流

```
使用者在酒館打「cmd <id> …」
  → Cmd_Tavern post 判定為 CLI 指令：打 meta tag=cli-cmd、跳過 glossary auto-attach
  → BartenderDaemon tick → UCL_BartenderCliService（總開關 → 白名單 → 需要時二次確認）
  → 依 <id>.json 的 actions 依序執行 → 酒保回覆（tag=bartender-relay）
```
