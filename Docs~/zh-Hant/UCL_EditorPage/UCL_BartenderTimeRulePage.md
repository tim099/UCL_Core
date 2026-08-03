---
title: UCL_BartenderTimeRulePage — 時間規則編輯頁
description: 從酒保管理頁抽離的 TimeRule 專用編輯器 — 每條規則可就地修改時間與內文，顯式存檔（沒按存檔不寫回 json）。
source_root: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_BartenderTimeRulePage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-08-03
target_audience: [AI_Agent, Developer, Designer]
aliases: [time rule editor, 時間規則編輯, 酒保報時編輯]
tags: [chat-tavern, bartender, editor]
related:
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_BartenderAdminPage.md | UCL_BartenderAdminPage | 入口父頁（時間規則區的「✏️ 開啟時間規則編輯頁」按鈕）
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_BartenderIO.cs | UCL_BartenderIO | time_rules.json 的唯一讀寫點
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_BartenderDaemon.cs | UCL_BartenderDaemon | 觸發端 — reminder_msg 照稿廣播、格式錯誤的 time_hhmm 靜默跳過
---

# ⏰ UCL_BartenderTimeRulePage — 時間規則編輯頁

入口：酒保管理頁 → 時間規則區 →「✏️ 開啟時間規則編輯頁」。

## 職責（與 AdminPage 的分工）

| 頁 | 能做什麼 |
|---|---|
| AdminPage 時間規則區 | 唯讀總覽 + 跳轉本頁；「🕐 報時」批次開關（Daemon 區） |
| **本頁** | 每條規則就地編輯 **time_hhmm** 與 **reminder_msg（多行 TextArea）**、enabled 開關、刪除、新增 |

## 存檔語意（本頁的核心設計）

- **所有編輯只動記憶體工作副本**（time_rules.json 的 deep copy）。
- **按 TopBar「💾 存檔」才寫回 json**；沒按就不寫（標題顯示 `*未存檔`）。
- 「↻ 重新載入」捨棄未存修改重讀檔案。
- 有未存修改按 Back → 彈三選一：存檔離開 / 取消 / 捨棄修改離開 — 丟失必須是看得見的選擇。

## 存檔前驗證（擋在寫檔前的理由）

daemon 端 `TryParseHHmm` 對格式錯誤的時間**靜默跳過**（規則永不觸發、不報錯）——所以本頁在寫檔前擋：

| 檢查 | 不過的後果（若放行） |
|---|---|
| `time_hhmm` 必為合法 `HH:mm` | 規則悄悄死掉（daemon 跳過, 零訊息） |
| `id` 非空、全清單不重複 | `fired_today` 去重靠 id, 重複 id 互吃觸發 |
| `reminder_msg` 非空 | 廣播空訊息 |

任一不過 → **整份不寫**、紅字定位到規則 id。

## 注意

- 清單依 `time_hhmm` 排序**僅為顯示**，存檔保持底層順序（diff 穩定）。
- 新增的規則帶佔位內文，建立後直接在上方卡片編輯；同樣等存檔才落地。
- `reminder_msg` 是靜態字串，daemon 照稿廣播（動態組裝的只有 ⏰ 標頭、@target 前綴與 penalty 尾註）。內嵌「條數 / 清單快照」類內容會漂移——寫指路不寫復誦（2026-07-31 Hard Rules 幽靈廣播血證）。
