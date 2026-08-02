---
title: UCL_BartenderAdminPage — 酒保管理頁
description: 集中管理酒保報時、時間提醒、關鍵字留言與 daemon 執行狀態的 Editor 後台。
source_root: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_BartenderAdminPage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-08-02
target_audience: [AI_Agent, Developer, Designer]
aliases: [bartender admin, 酒保後台, 酒保報時, time rules]
tags: [chat-tavern, bartender, editor]
related:
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_BartenderDaemon.cs | UCL_BartenderDaemon | 酒保常駐掃描與發話實作
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Bartender/UCL_BartenderIO.cs | UCL_BartenderIO | triggers/time_rules/state 的唯一讀寫點
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_ChatTavernAdminPage.md | UCL_ChatTavernAdminPage | Discord 雙向同步管理頁；與本頁的酒保自動廣播分工
---

# 🍺 UCL_BartenderAdminPage — 酒保管理頁

控制台 →「🍺 酒保後台」→「開啟酒保管理頁」。本頁管酒保的自動發言；Discord webhook 與 inbound 不在此處設定。

四個管理區塊預設皆收合，避免規則列表壓過頁面入口；「常駐酒保」標頭保留總開關、報時、立即檢查與重新載入等高頻操作，其他明細需展開後才顯示。

## 可管理項目

| 區塊 | 操作 | 資料來源 |
|---|---|---|
| 常駐酒保 | 酒館系統開關、立即 tick、重新載入 | `UCL_ChatTavernSystemControl` / `UCL_BartenderDaemon` |
| 酒保報時 | 一鍵切換每日／每小時 `announce-rules-*` 報時規則 | `ChatTavern/bartender/time_rules.json` |
| 時間規則 | 逐條開關、刪除、新增單次時間提醒 | `time_rules.json` |
| 關鍵字留言 | 檢視剩餘觸發額度、刪除、新增全域 keyword trigger | `triggers.json` |
| 執行狀態 | 各 room 已掃 seq、今天已觸發數、跨日檢查日期 | `state.json` |

## 報時的範圍

「🕐 報時」只影響 rule id 以 `announce-rules-` 開頭的每日／每小時規則；睡眠提醒、HP penalty、
關鍵字留言與跨日保管費仍按各自設定運行。這是可逆的 `enabled` 切換，不會刪除任何規則。

## 注意事項

- 酒保總開關與控制台的「聊天酒館系統」是同一個開關；關閉後所有酒保自動掃描與廣播停止。
- 「立即檢查」只執行一次既有判定，不會強制重發今天已在 `fired_today_keys` 登記的時間規則。
- 本頁新增的 keyword trigger 為全域 sender 比對、目標 room 為 `tavern`；需要指定 target 的進階規則可繼續用 `Cmd_Bartender` 或 inline 指令。
