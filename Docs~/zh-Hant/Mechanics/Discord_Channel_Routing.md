---
title: Discord Channel Routing
description: Discord channel → ChatTavern room 路由設定 — 多對一支援, source_class freeform tag, priority desc sort, IMGUI 編輯
last_updated: 2026-05-15
target_audience: [AI_Agent, Developer]
aliases: [discord routing, channel routing, channel mappings]
tags: [discord, chat-tavern, routing, config]
related:
  - ucl_core:Docs~/zh-Hant/Mechanics/Waiter_Session_System.md | Waiter Session | 接待 Discord 客人 stand-by 機制 (cycle 用 priority desc 排序)
  - ucl_core:Tools~/AgentCommands/discord_inbound_bot.py | Inbound Bot | 讀本 routing 表 + 啟動時建立 channel_map
  - ucl_core:UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_DiscordChannelRoutingPage.cs | IMGUI Page | 編輯 routing 的 UI 入口
---

# Discord Channel Routing

> 一句話：**Discord channel 訊息 → ChatTavern room 的路由表**，支援多對一、freeform source_class tag、priority 排序，IMGUI 可編輯。

舊機制：`notify_config.json.tavern_inbound.channel_mappings` 只支援 channel_id → tavern_room flat 映射，沒有 priority / source_class / enabled 等屬性。Tim 2026-05-15 拍板抽出獨立檔加 richer schema。

---

## 1. 檔案位置

```
AgentCommands/ChatTavern/discord_channel_routing.json
```

由以下兩個 consumer 讀：
- **Python**：`AgentCommands/Tools/discord_inbound_bot.py` 啟動時 `load_routing()`
- **C# Editor**：`UCL_DiscordChannelRoutingPage` 編輯時 read/write

> 注意：bot 是啟動時讀，不熱更新。改 config 後要 kill 現有 python process，daemon 5s 內自動 respawn 讀新檔。

---

## 2. Schema

```jsonc
{
  "_schema_version": 1,
  "_description": "...",
  "_canonical_doc": "Docs~/zh-Hant/Mechanics/Discord_Channel_Routing.md",
  "_taxonomy_note": "...",
  "mappings": [
    {
      "channel_id": "1502449153018560562",   // Discord channel ID (string, 不轉 long 避免精度)
      "tavern_room": "tavern",                // 對應 ChatTavern room id
      "label": "公開聊天酒館",                  // 顯示用名稱 (UI / 日誌 / agent context)
      "source_class": "external",             // freeform tag (慣例: external / internal / work / chitchat / urgent)
      "priority": 10,                         // int, 越高越優先 (waiter cycle 按此 desc 排序)
      "enabled": true,                        // false = bot 跳過此 channel 不 relay
      "guild_id": "1039197199013269584",      // (選填) 為了 audit / 跨 server 區分
      "tags": ["work"],                       // (選填) 自由分類 tag list
      "_note": "備註 (選填)"
    }
  ]
}
```

### 欄位語意

| 欄位 | 必填 | 說明 |
|---|---|---|
| `channel_id` | ✅ | Discord channel snowflake ID (字串，避免 JSON 64-bit 精度)|
| `tavern_room` | ✅ | 對應 ChatTavern room id（多 channel 可指同一 room → 多對一）|
| `label` | 建議 | 人類可讀名稱，agent context + UI 顯示用 |
| `source_class` | ✅ | **Freeform string**（不限 enum）。慣例 tag 見下節 |
| `priority` | ✅ | int，越高越優先；waiter cycle 排序鍵 |
| `enabled` | ✅ | false = 整 row 失效 |
| `guild_id` | ❌ | Discord server ID (audit) |
| `tags` | ❌ | array of string，自由分類 |
| `_note` | ❌ | 內部備註，bot 不讀只給人看 |

### source_class 慣例 tag

不強制 enum，但建議靠齊以下慣例方便 agent 處理：

| Tag | 含義 | 通常 priority |
|---|---|---|
| `external` | 公開頻道 / 一般客人 | 低 (0-20) |
| `internal` | 內部頻道 / 同事 / 工作 | 高 (40-60) |
| `work` | 工作專用頻道 | 中高 (30-50) |
| `chitchat` | 純閒聊 | 極低 (0-5) |
| `urgent` | 緊急 / 待回 | 最高 (80-100) |

也可以自訂 — 例如 `bug-report` / `playtest` / `customer-support` — 但 agent 端不一定認識，priority 是更通用的訊號。

---

## 3. 多對一 / 多對多範例

### 多對一（多 Discord channel 進同一 tavern room）

```jsonc
"mappings": [
  { "channel_id": "AAA", "tavern_room": "tavern", "source_class": "external", "priority": 10 },
  { "channel_id": "BBB", "tavern_room": "tavern", "source_class": "internal", "priority": 50 }
]
```

兩個 channel 訊息都進 `tavern` room，agent 看一個房間就行。priority 差異讓 internal 訊息排前。

### 多對多（分流）

```jsonc
"mappings": [
  { "channel_id": "AAA", "tavern_room": "tavern",      "source_class": "external" },
  { "channel_id": "BBB", "tavern_room": "hideout",     "source_class": "internal" },
  { "channel_id": "CCC", "tavern_room": "brainstorm",  "source_class": "work", "priority": 60 }
]
```

不同 channel 進不同 room；agent 要看多個房間，但分流乾淨。

---

## 4. Bot 端處理流程

1. **啟動**：`load_routing()` 讀此 JSON；若不存在 → fallback 讀 legacy `notify_config.tavern_inbound.channel_mappings`（自動補 `source_class=external, priority=0, enabled=true`）
2. **建 channel_map**：`build_channel_map()` 過濾 `enabled=true` 的 row → `{channel_id_int: routing_row_dict}`
3. **on_message**：
   - 查 `channel_map.get(message.channel.id)` → 拿到 routing
   - 用 routing 的 `tavern_room` / `source_class` / `priority` / `label` 構造 meta
   - 寫進 tavern via `Cmd_Tavern op=post`，meta 帶上：
     ```jsonc
     {
       "source": "discord",
       "discord_msg_id": "...",
       "discord_channel_id": "...",
       "source_class": "internal",
       "priority": 50,
       "channel_label": "內部聊天酒館",
       ...
     }
     ```

### 防迴圈仍生效

`notify_discord.py tavern_mirror` 看到 `meta.source == "discord"` → 跳過不 echo 回 Discord。本 routing 機制不影響既有防迴圈。

---

## 5. Waiter Cycle 整合

`waiter_session.py cycle` 端 `_scan_new_customer_msgs()` 改成：
1. 掃 tavern room 訊息 + 過濾 `sender_id` 開頭 `discord:`
2. 從 meta 提取 `priority`（int，預設 0）+ `source_class` + `channel_label`
3. **排序**：`priority desc, ts asc`（高 priority 先；同 priority 老訊息先）
4. Cap 到 `limit`

→ Agent 在 reply 時看到 high priority 的 internal/work msg 排前面，自動先處理。

回傳 JSON 新增欄位：

```jsonc
{
  "new_msgs": [
    {
      "ts": "...",
      "sender_id": "discord:...",
      "sender_name": "...",
      "body": "...",
      "discord_msg_id": "...",
      "source_class": "internal",        // NEW
      "priority": 50,                    // NEW
      "channel_label": "內部聊天酒館"     // NEW
    }
  ]
}
```

---

## 6. IMGUI Page

入口：**UCL / Menu → Page Picker → "Discord Channel Routing"**

或：`UCL_DiscordChannelRoutingPage.Create()` 程式碼啟動

### 功能

- **表格 CRUD**：每 row 顯示 Enabled / Channel ID / Label / Tavern Room / Source Class / Priority / Tags / Guild ID
- **編輯**：直接 in-place 改欄位，dirty flag 變 `●`
- **新增 Row**：頂部 `Add Row` 按鈕
- **刪除**：每 row 右側 `✖ Remove`
- **重排**：每 row 右側 `▲▼` 上下移
- **儲存**：頂部 `Save` 寫回 JSON（手構序列化保留 meta 欄位）
- **Refresh**：重讀 JSON（會丟掉未存改動）
- **Restart Bot**：印出 PowerShell 命令引導 kill python（避免誤殺其他 python.exe；MVP 不直接 kill）
- **Open JSON**：在檔案總管打開 JSON 所在資料夾

### Save 行為

C# 端不用 `JsonData` 反序列化（會丟失 `_description` 等 meta 欄位），改手構 JSON 對齊 schema。保留 `_schema_version` / `_description` / `_canonical_doc` / `_taxonomy_note` 4 個 meta 欄位。

---

## 7. 換 config 後重啟 bot

bot 是 startup-time 讀 config（discord.py gateway 連線後不熱更新 watched channel）。改完 routing 後：

```powershell
# 精確 kill bot subprocess (不誤殺其他 python.exe)
Get-CimInstance Win32_Process -Filter "name='python.exe' AND CommandLine LIKE '%discord_inbound_bot%'" | ForEach-Object { Stop-Process -Id $_.ProcessId }
```

`RCG_DiscordInboundDaemon` 5s 內偵測子程序死掉自動 respawn 新 process，新 bot 啟動時 `load_routing()` 讀新 JSON。

未來 backlog：UCL_DiscordChannelRoutingPage 的 Restart Bot 按鈕直接執行此命令（需要 daemon 寫 PID file 給 page 讀對應 process）。

---

## 8. 跟其他系統的整合

| 系統 | 整合點 |
|---|---|
| `discord_inbound_bot.py` | 啟動讀本 JSON，運行期不更新 |
| `waiter_session.py cycle` | 從 tavern message meta 提取 priority + source_class，sort desc |
| `notify_discord.py tavern_mirror` | 不受影響；`exclude_meta_source=["discord"]` 仍防迴圈 |
| `Cmd_Tavern op=post` | bot 寫 tavern 時帶 enriched meta；無 schema 變更 |

---

## 9. Backlog

- v2: Bot watch config 檔變動 → 自動 reload channel_map（不必 kill 重 spawn）
- v3: Restart Bot 按鈕直接執行 PowerShell（需要 daemon 寫 PID file）
- v4: source_class enum validation + UI dropdown（保留 freeform 但提示常用 tag）
- v5: per-channel custom reply persona（指定某 channel 由特定 persona 接）
- v6: Priority queue mode（高 priority 訊息 cycle 獨佔，低 priority 排後）

---

## 10. 相關文件

- [`<UCL_Core>/Tools~/AgentCommands/discord_inbound_bot.py`](../../../Tools~/AgentCommands/discord_inbound_bot.py)
- [`<UCL_Core>/Tools~/AgentCommands/waiter_session.py`](../../../Tools~/AgentCommands/waiter_session.py)
- [`<UCL_Core>/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_DiscordChannelRoutingPage.cs`](../../../UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_DiscordChannelRoutingPage.cs)
- [`<UCL_Core>/Docs~/zh-Hant/Mechanics/Waiter_Session_System.md`](Waiter_Session_System.md)
- [`docs/Workflows/Discord_Inbound_Workflow.md`](../../../../../../docs/Workflows/Discord_Inbound_Workflow.md)（主專案，整體 setup SOP）
