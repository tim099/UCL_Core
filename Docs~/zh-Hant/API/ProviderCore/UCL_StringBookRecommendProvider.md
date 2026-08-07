---
title: UCL_StringBookRecommendProvider — 隨機推薦藏書
description: UCL_StringProvider 子類 — 從圖書館藏書中隨機挑 N 本（預設 10）回傳書名清單；沒有圖書館或沒有書時回傳空字串。
source_root: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Books/UCL_StringBookRecommendProvider.cs
namespace: UCL.Core.EditorLib.AgentCommands.Books
last_updated: 2026-08-07
target_audience: [AI_Agent, Developer]
aliases: [book recommend provider, 推薦書單, 隨機藏書]
tags: [provider, books, editor-only]
related:
  - ucl_core:Docs~/{lang}/API/ProviderCore/UCL_StringProvider.md | UCL_StringProvider | 抽象基底
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Books/UCL_BooksIO.cs | UCL_BooksIO | 藏書唯一讀取點（本 provider 不自己掃目錄）
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_BartenderTimeRulePage.md | UCL_BartenderTimeRulePage | 典型消費端（每日推薦書單）
---

# 🎲 UCL_StringBookRecommendProvider — 隨機推薦藏書

從圖書館藏書中**隨機挑 N 本**，回傳書名清單。給「每日推薦書單」這類
「內容不寫死、每次求值當場抽」的用途。

## 1. 參數

| 欄位 | 預設 | 說明 |
|---|---|---|
| `m_Count` | `10` | 要推薦幾本。實際取出 `min(N, 藏書數)`；**≤ 0 → 回傳空字串**（讓「暫時不推薦」有表達方式，而不是報錯） |
| `m_Separator` | `"\n"` | 書名之間的分隔。預設換行 —— 消費端本來就以行為單位，一個 provider 展開成多行是預期用法；要排成一行可改 `"、"` |

輸出格式：每本書名加上書名號，例如 `《殘幀之證》`。

## 2. 資料來源

藏書事實源是 `<repo>/AgentCommands/Books/*/_donation.json`，
讀取一律走 **`UCL_BooksIO.LoadDonations()`**（唯一讀取點）——
本 provider **不自己掃目錄、不自己 parse JSON**。

書名取 `title`，缺欄退回 `book`（資料夾名）——與 `UCL_BooksIO.RenderDonations` 同一套兜底規則，
兩邊顯示的書名才不會一邊有一邊沒有。

## 3. 邊界行為

| 情況 | 行為 |
|---|---|
| 找不到圖書館目錄 | **回傳空字串** |
| 目錄在但一本書都沒有 | **回傳空字串** |
| `m_Count ≤ 0` | 回傳空字串 |
| `m_Count` > 藏書數 | 取全部，不補空行 |
| 個別 `_donation.json` 壞檔 | 略過該本，其餘照常推薦 |

> [!NOTE]
> 空的圖書館**不是錯誤、不印 warning** —— 沒有藏書是合法狀態，
> 不該讓提醒訊息長出一段雜訊。壞檔的回報責任在 `op=donations`（它會列進 WARNING 區）。

## 4. ⚠ 兩個必須知道的取捨

### 每次求值都重新抽樣

`GetString()` **連續呼叫兩次結果不同**。這是刻意的（推薦本來就該換），但代價是：

> **編輯頁的預覽與實際廣播會抽到不同的書。**

預覽要對照的是**格式**，不是「哪幾本」。若某天需要「同一天抽同一份」，
要另外做成帶 seed 的子類，而不是改本類的語意。

### Editor-only

本類放在 `EditorCore/…/Books/` 而不是 `ProviderCore/`，因為它依賴的
`UCL_BooksIO` 是 `#if UNITY_EDITOR` 的 Editor 端工具。

- 放進 `ProviderCore` 會讓 runtime 層反過來依賴 Editor 層（層級倒置，且 build 會編不過）。
- **後果**：以此 provider 存下的資料在 **build 後的 runtime 還原不回來**（型別不存在）。
  目前唯一消費端是酒保時間規則（Editor-only 工具），符合前提。
  日後若有 runtime 消費端，要先把藏書讀取搬到 runtime 層才能沿用。

## 5. 實測（2026-08-07，藏書 23 本）

```
N=3   連兩次 → 書單不同 ✓
N=10（預設） → 10 行 ✓
N=0   → 空字串（len=0）✓
N=9999 → 23 行（等於藏書數，不補空行）✓
```

> [!NOTE]
> 「找不到圖書館目錄」那條分支未實際執行驗證（需移走真實 Books 目錄），
> 是依 `UCL_BooksIO.LoadDonations()` 的 `if (!Directory.Exists(BooksRoot)) return o;`
> 加上本類的空清單防護推得 —— 兩段都讀過，但沒跑過。
