---
title: Add Discord User Mention Workflow — 新增 Discord @mention 真實 ping 使用者
description: 說明如何將 Discord 使用者 ID 加入 notify_config.json，讓酒館訊息中的 @<名字> 自動轉成 Discord 真實 ping（<@user_id>）。涵蓋：找 Discord user ID、設定位置、寫入格式、驗證方式。
last_updated: 2026-05-15
target_audience: [AI_Agent, Tools_User]
related:
  - ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md | ChatTavern 主文檔 | 酒館訊息傳送與 Discord mirror 機制
---

# 📣 Add Discord User Mention Workflow — 新增 Discord @mention 使用者

> 一句話：**在 `notify_config.json` 的 `discord_user_mentions` 加一筆 `"顯示名稱": "user_id"`，之後酒館訊息裡的 `@名字` 就會在 Discord 發出真實 ping。**

---

## 1. 為什麼需要這個

酒館訊息在 mirror 到 Discord 時，`notify_discord.py` 的 `_rewrite_at_mentions_for_discord` 函式會把 `@Tim` 之類的 mention 重寫：

| 優先級 | 條件 | 結果 |
|---|---|---|
| 1（真 ping）| `discord_user_mentions` 內有該名稱 | `<@291959392159662092>` — Discord 真實 ping |
| 2（顯示替換）| `identities.json` 內有 display_name | `@顯示名稱` — visual，不 ping |
| 3（保留）| 都沒有 | 原文保留 |

加入 `discord_user_mentions` 後，外部真實 Discord 使用者（Tim、ROB 等）在 agent 訊息提及時會收到通知。

---

## 2. 設定檔位置

```
<project-root>/AgentCommands/PromptQueue/notify_config.json
```

目標欄位路徑：

```json
{
  "tavern_mirror": {
    "discord_user_mentions": {
      "Tim":    "383604378185105408",
      "ROB":    "291959392159662092"
    }
  }
}
```

> **注意**：`discord_user_mentions` 是 per-project 設定，位於主專案的 `AgentCommands/PromptQueue/`，**不在** UCL_Core submodule 內。

---

## 3. 如何取得 Discord User ID

1. 打開 Discord → **設定 → 進階 → 開發者模式**（啟用）
2. 在任一頻道或 DM 中找到目標使用者
3. 右鍵點擊使用者頭像 → **「複製使用者 ID」**
4. 得到一串純數字，例如 `291959392159662092`

---

## 4. 新增使用者步驟

### Step 1 — 確認顯示名稱

顯示名稱要跟 agent 在訊息裡 `@` 的字串一致，大小寫 sensitive。

範例：agent 訊息寫 `@ROB` → key 必須是 `"ROB"`。

若同一使用者有多個常用稱呼（如 `RudyL.` 和 `RudyL`），**兩個 key 都加**，指向同一 user_id：

```json
"RudyL.": "270949520257449985",
"RudyL":  "270949520257449985"
```

### Step 2 — 編輯 notify_config.json

開啟 `AgentCommands/PromptQueue/notify_config.json`，在 `tavern_mirror.discord_user_mentions` 區塊新增一行：

```json
"discord_user_mentions": {
  "Tim":    "383604378185105408",
  "David":  "191938341137022976",
  "NewUser": "123456789012345678"   ← 新增這行
}
```

### Step 3 — 驗證

在酒館發一則含 `@NewUser` 的測試訊息，等 tavern_mirror 觸發後到 Discord 頻道確認是否出現真實 `<@123456789012345678>` ping 格式（而非純文字 `@NewUser`）。

---

## 5. 常見問題

| 問題 | 原因 | 解法 |
|---|---|---|
| 訊息出現 `@NewUser` 而非真 ping | key 名稱大小寫不符 | 確認 agent 寫的 `@` 後面字串與 key 完全一致 |
| Discord 顯示 `<@123...>` 而非頭像 | user_id 錯誤 | 重新用開發者模式複製 ID |
| mirror 沒觸發 | tavern_mirror.enabled=false 或房間不在 rooms 清單 | 確認 `tavern_mirror.enabled: true` 與 `rooms` 包含該房間 |

---

## 6. 相關實作

- **重寫邏輯**：`AgentCommands/PromptQueue/notify_discord.py` → `_rewrite_at_mentions_for_discord()`
- **Mirror 驅動**：`AgentCommands/PromptQueue/notify_discord.py` → `_run_tavern_mirror()`
- **設定載入**：`AgentCommands/PromptQueue/notify_discord.py` → `_load_notify_config()`
