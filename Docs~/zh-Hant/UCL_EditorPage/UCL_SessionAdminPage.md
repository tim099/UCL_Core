---
title: Session 管理頁 (UCL_SessionAdminPage)
description: 各 persona 的 session 現況（自由時間…）與處置台 — 標出超時未收工、active 仍為 true 的殘留，並提供二段確認的補收工。
tags: [editor-page, session, freetime, admin]
aliases: [Session 管理, session admin, 場次管理頁]
target_audience: [AI_Agent, Tools_User]
last_updated: 2026-08-18
---

# 🗂 Session 管理頁 (UCL_SessionAdminPage)

> 一句話：**「誰現在在自由時間」「誰超時沒回來收工」的可視化與處置台。**

**入口**：Editor 控制台 → 🧰 ToolBox → 🗂 Session 管理

## 為什麼需要這一頁

`active` 這個欄位**只在有人真的跑收工步驟時**才被翻成 false —— 超時就消失的人會把 `true`
留在檔案裡。而 python 端判「在不在自由時間」會先看 `active`，只靠 `end_ts` 過期才擋下來。

⇒ **超時殘留不是 cosmetic**：少一層防護就會叫人去 `@` 一個早就下線的人。
本頁把那種殘留單獨標成 ⚠ 並排在前面 —— **需要動手的東西不該要人自己找。**

## 三種狀態（顏色與排序都跟著它）

| 顯示 | 條件 | 排序 |
|---|---|---|
| 🟢 進行中 | `active` 且未過 `end_ts` | 最前 |
| ⚠ 殘留（active 但已過期） | `active=true` 但已過 `end_ts` | 次之 |
| ⚪ 已收工 | `active=false` | 最後 |

判準走 `UCL_SessionBase.IsRunningAt` —— 與 `Cmd_SessionStatus`、`Cmd_FreeTime` 同一條。
兩份判準的漂移症狀是「頁面說在線、Cmd 說不在」，而它不會報錯。

## 每一列有什麼

- persona / kind / 狀態 / 進行中的剩餘分鐘
- `session_id`、開場與預定收工時刻（`until_local` 與 `end_ts`）
- 已收工的另附 `ended_at` 與 `end_reason`（未記原因會明說「（未記）」而不是留白）
- **📄 開啟檔案** —— 走 `UCL_SessionService.SessionPath`，不自己拼路徑

## 🧹 補收工（只對「殘留」開放，二段確認）

第一次點 = arm（按鈕變紅並改字），**5 秒內**再點同一列才真的寫；逾時自動解除。

- 為什麼要二段：補收工會改**別人的** session 檔。誤點的後果是把一場**真的在跑**的 session 關掉，
  而那個人不會收到通知 —— 他只會在下一次 `step=next` 撞到「沒有進行中的 session」。
- 為什麼逾時要自動解除 arm：不解除的話「五分鐘前點過一次」會變成一鍵寫入。
- 寫入走 `UCL_SessionService.Close`（翻 `active` ＋ 記 `end_reason` ＋ 記 `ended_at`，**三件一起**），
  不手改檔案。原因記成 `expired-closed-by-admin-page`。

> ⛔ **進行中的場不給從這裡關。** 要收工請走該 session 的 Cmd（自由時間是 `step=end`）——
> 那條路會發收工宣告、結算免費像素。從後台直接關會跳過那些結算，留下對不上的帳。

## ⚠ 空清單的語意

頁面上印的「掃描範圍（已登記 kind）」**是讀數的一部分，不是說明文字**：
清單為空的意思是「**已登記的種類**裡沒有」，不是「系統裡沒有任何 session」。
未登記的種類（見 `UCL_SessionKind.Kinds` 註解）根本沒被看過。

## 效能

顯示走 2 秒快取（`REFRESH_INTERVAL_SEC`），不每 `OnGUI` 都掃檔 ——
session 多時每幀重讀會明顯拖慢 IMGUI。2 秒的顯示延遲對「誰在自由時間」這種分鐘級判斷不影響決策。
要即時可按「🔄 立即重新整理」。

## 相關

- CLI 版查詢：[`API/UCL_AgentCommand/Cmd_SessionStatus.md`](../API/UCL_AgentCommand/Cmd_SessionStatus.md)
- 自由時間活動設定：[`UCL_FreeTimeAdminPage.md`](UCL_FreeTimeAdminPage.md)
- 自由時間流程：[`Workflows/FreeTime_Cmd_Flow.md`](../Workflows/FreeTime_Cmd_Flow.md)
- 頁面骨架慣例：[`UCL_CommonEditorPage.md`](UCL_CommonEditorPage.md)
