---
title: 自由時間管理頁 (UCL_FreeTimeAdminPage)
description: 自由時間骰面活動的管理台 — 下拉選一項活動編輯其啟用 / 特殊邏輯標記 (kind) / 建議時間 / 顯示名稱 / 做法，就地改寫活動 md 的 frontmatter，不另存 override 設定。
tags: [editor-page, freetime, activities, dice]
aliases: [自由時間管理, freetime admin, 活動管理頁]
target_audience: [AI_Agent, Tools_User]
last_updated: 2026-08-17
---

# 🎲 自由時間管理頁 (UCL_FreeTimeAdminPage)

> 一句話：**自由時間骰面上有哪些活動、各自走什麼特殊邏輯，都在這頁改** —— 而它改的是
> **活動 md 的 frontmatter 本身**，不是另一份設定檔。

## 為什麼沒有「本頁自己的設定檔」

活動清單的事實來源**只有活動 md 一處**（雙層：UCL_Core 共用層 ＋ 專案層，同 id 專案層覆蓋）。

v1 曾經有過 `AgentCommands/FreeTime/activities.json`，**因雙源同步漂移被廢止** ——
兩個地方各自宣告同一件事時，合併規則會變成看不見的隱式約定，而約定壞掉時不會有人喊。
所以本頁的每個欄位都是**就地改寫 md**（`UCL_AwakeningService.WriteFrontmatterField`，正文不動），
寫完立刻重掃 —— **印 ✓ 不算數，讀回來才算**。

掃描也共用 `UCL_FreeTimeIO.ScanActivities()`，跟 `Cmd_FreeTime` 擲骰是同一份實作 ——
兩份掃描器的漂移症狀是「頁面看到的清單跟實際擲出來的不一樣」，而它不會報錯。

## 操作

### 選活動

上方下拉選單（`PopupSearchCache`，可搜尋）選**一項**來編輯。選項字串帶層級與狀態：

```
📦 chess [Chess]          ← 共用層 / kind=Chess
🏠 valor-qa               ← 專案層（同 id 會覆蓋共用層）
（停用）📦 canvas-draw     ← enabled: false
```

> ⚠ 選取記的是**活動 id 不是索引**。Reload 後清單順序會變（新增活動 / 改 id），
> 記索引會安靜地切到另一個活動，而畫面上看起來像什麼都沒發生 —— 接著你的編輯就寫到別人的 md 上了。

### 可編欄位

| 欄位 | frontmatter | 說明 |
|---|---|---|
| 啟用 | `enabled` | 取消勾選＝從骰面下架，檔案保留 |
| **特殊邏輯** | `kind` | 見下節 |
| 建議時間 | `min_minutes` | 剩餘時間不足 → 骰面**排尾＋標明**，不隱藏。`0` ＝ 不做時間感知排序 |
| 顯示名稱 | `name` | 骰面主體文字 |
| 做法 | `how` | 一行操作提示 |

文字 / 數字欄走「草稿 → 按**套用**」兩段式，不邊打字邊寫檔（TextField 每幀回傳字串，
直接寫檔會變成每個按鍵都落一次盤）。

### 特殊邏輯 `kind`（Tim 2026-08-17 拍板）

下拉選單，選項來自 `UCL_FreeTimeActivityKind` enum：

| kind | 骰面行為 |
|---|---|
| `Default` | 一般活動 —— 不走任何特殊邏輯 |
| `StreamWatch` | 沒開播 → **從骰面隱藏**；開播 → 進優先層並附本場節目名 |
| `Chess` | 有未完成棋局**且對手也在自由時間** → 進優先層（不隱藏，隨時可開新局） |

**為什麼是下拉而不是文字欄**：打錯的標記（`live-strem`）**不會報錯也不會生效**，
只會安靜地退回 `Default`。下拉選單根本打不出那個值。

若 md 是手改的且值認不得，本頁會在該活動下方顯示 ⚠ 提示（用下拉重設一次即可寫回正確值）——
**標記打錯要在設定它的地方就看得到**，不能只在骰面上才顯形。

> 三道處理（可用性隱藏 / 優先層 / 時間感知）的完整語意與判準見
> [`Mechanics/FreeTime_System.md` §4.1.1](../Mechanics/FreeTime_System.md)。
> **新增一種 kind 要同時改 enum 與 `UCL_FreeTimeGating`** —— 一個沒有實作的標記，
> 會讓人以為那裡有一道邏輯。

### 新增活動

一律建在**專案層**（`<repo>/docs/FreeTime/Activities/`）。共用層屬於 UCL_Core（跨專案），
從某個專案的管理頁往那裡新增等於替別的專案做決定；要改共用活動也走專案層同 id 覆蓋。

## 相關

- 活動 md 格式與 `kind` 撰寫規範：[`FreeTime/Activities/_README.md`](../FreeTime/Activities/_README.md)
- 自由時間機制全貌（三池 / 骰面 / 配對簡報）：[`Mechanics/FreeTime_System.md`](../Mechanics/FreeTime_System.md)
- Cmd 分步流程：[`Workflows/FreeTime_Cmd_Flow.md`](../Workflows/FreeTime_Cmd_Flow.md)（2026-08-18 拆檔，§10 已抽成指路）
- 頁面骨架慣例：[`UCL_CommonEditorPage.md`](UCL_CommonEditorPage.md)

## 歷史

- 2026-08-14：曾有「末段提示門檻」設定區，隨末段提示功能被整個拔掉而移除 ——
  留一個沒有消費端的設定介面，會讓人以為那裡還有一道防護。
- 2026-08-17：活動管理由「整頁列出所有活動」改為「下拉選一項編輯」；新增 `kind` 下拉。
