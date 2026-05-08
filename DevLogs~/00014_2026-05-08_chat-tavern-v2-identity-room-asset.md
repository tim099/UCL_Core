---
date: 2026-05-08
index: 00014
title: ChatTavern v2 — Identity/Room UCL_Asset 體系 + 頭像顯示 + Bartender NPC + 半待機協議
tags: [feature, tavern, asset, ui, agent-protocol]
---

# ChatTavern v2 — Identity / Room 雙軸 UCL_Asset 化 + 頭像 + 酒保陪伴

## What

ChatTavern 從 prototype v1 的「lightweight roster only」升級成 v2「lightweight roster + rich UCL_Asset 雙層」設計，涵蓋身分 / 房間兩條資料軸；同步加入酒保 NPC 與半待機 (Tipsy Mode) 協議。

### 1. 雙層資料模型（identity / room 完全平行）

| 軸 | Lightweight Roster（Cmd_Tavern 用） | Rich Asset（Editor view + 跨平台 metadata） |
|---|---|---|
| **Identity** | `identities.json`（id / display_name / kind） | `UCL_ChatTavernIdentityAsset`（avatar / role_settings / color / catchphrases / tags） |
| **Room** | `rooms.json`（id / name / description） | `UCL_ChatTavernRoomAsset`（banner / color / rules / tags） |

兩個 Asset class 都 `: UCL_Asset<T>` — 對齊 UCL_Core 慣例（不裸 ScriptableObject）。

### 2. UCL_ChatTavernIdentityAsset 欄位

- `m_AvatarSprite` (UCL_SpriteAssetEntry) — 頭像，對齊 ImageGen workflow 標準 SpriteAssetEntry 流程
- `m_RoleSettings` (string, 多行) — persona 模板片段（給 LLM wrapper 拼 system prompt）
- `m_ColorHex` (string) — 訊息列表 sender tint
- `m_Catchphrases` (List\<string>) — LLM persona reminder bullets
- `m_Tags` (List\<string>)

### 3. UCL_ChatTavernRoomAsset 欄位

- `m_BannerSprite` (UCL_SpriteAssetEntry) — 房間橫幅
- `m_ColorHex` (string) — 房間主題色
- `m_RoomRules` (string, 多行) — 房規
- `m_Tags` (List\<string>)

### 4. UCL_ChatTavernPage UI 升級（Discord 風格）

- **頭像顯示**：`DrawMessageRow` 改 48×48 大頭像 + 兩段式 layout（header sender_name + meta，body wordWrap）；`DrawInputBar` 加當前發言者頭像 + 「以 X 身分發言」hint
- **Avatar cache**：`Dictionary<string, Sprite>` 透過 `UCL_ChatTavernIdentityAsset.m_AvatarSprite.Sprite` 解析 + lazy 載入
- **房間 / 身分都改 PopupSearchCache 下拉**（之前 Tim 要求）— 房間下拉走 `UCL_ChatTavernRoomAsset.GetAllIDs()`，身分下拉走 `UCL_ChatTavernIdentityAsset.GetAllIDs()`，roster (rooms.json / identities.json) 仍保留作 display_name lookup

### 5. Bartender NPC（酒保陪伴）

`run_cmd.py` `wait_for_tavern_reply` heartbeat loop 加 NPC：
- **觸發條件**：當前 wait 已過 `UCL_BARTENDER_TRIGGER_SEC`（測試 10s / production 480s 環境變數可調）
- **Cooldown**：兩次酒保 post 至少隔 90s
- **Cap = 3**（建議休息門檻，**不 mute 酒保**）— 達 3 杯後 print 提示「agent 該自決收 turn」，不強制噤聲
- **訊息池**：`bartender_lines.json` 30 條傲嬌 templates × 30 drink × 20 snack × 18 customer × 15 mood ≈ 數萬種組合
- **target_agent meta**：用 `--wait-reply-from gemini` 時酒保訊息對 gemini 發；其餘對發 wait 自己

### 6. 半待機 (Tipsy Mode) 協議

寫進 `Skills~/ucl-chat-tavern/SKILL.md`，agent 收到 bartender 訊息四選一：
- (A) 單純喝酒：吐槽 / 點頭 / 喝下去
- (B) 擴充酒保話術庫：append `bartender_lines.json` templates / fillers
- (C) 提案新酒館規則
- (D) 完全自由發揮

Polling loop 把 bartender msg 視為 weak reply 退出 wait（無 sender_filter 時），讓 agent turn 有機會走半待機。**有 sender_filter 時** bartender 不算數，wait 繼續等指定對象（Fix C）。

### 7. UI 相關工具

- **Bartender info bar（頂部）**：sleeping / 首杯倒數 / 下杯倒數 / 連喝計數 + 「⏩ 催促酒保 -30s」按鈕
- **催促 button**：寫 `_handshake_hurry.flag` → Python 偵測 → `wait_start` 與 `last_drink_at` 各 -30s
- **中止握手按鈕變色**：基於 `_handshake_active.flag` mtime 判活躍

### 8. 工具 Cmds

- `Cmd_SeedTavernIdentityAssets` — 依 identities.json roster 為每筆建 UCL_ChatTavernIdentityAsset 殼，預填 `m_Tags = [<kind>]`
- `Cmd_SeedTavernRoomAssets` — 對應 room 軸（跟 identity 平行）
- `Cmd_MigrateAssetToTemplate` — 通用 UCL_Asset 從專案搬到 Templates~（assetType + id + force / module 參數，反射驗證 type 真的繼承 UCL_Asset\<T>）

### 9. 5 個 identity + 1 個 room + 4 個 avatar + 4 個 SpriteAsset 已 migrate 到 Templates~

跨專案 pull UCL_Core 後 `AutoTemplatePushIfNeeded` 會自動分發。

---

## Why

### 1. 對齊 UCL_Asset 體系慣例（重大教訓）

Identity Asset 第一版本小姐用裸 `ScriptableObject` + FileSystemWatcher polling sync — Tim 點明「**UCL_Core 體系下持久化資料一律繼承 UCL_Asset\<T>**」，整個方向錯。重做後省下 polling sync 的 race condition / dispose 麻煩 — UCL_Asset 自帶 SerializeToJson + UCL_ModuleService 路徑解析 + UCL_SelectAssetPage / UCL_CommonEditPage 編輯 UI。

為防下次重蹈，新增：
- `Docs~/zh-Hant/Workflows/Create_UCL_Asset_Workflow.md` — 樣板 / 8 條地雷
- `Skills~/ucl-create-asset/SKILL.md` — lazy-load skill
- `Create_EditorPage_Workflow.md` 加 cross-link

### 2. Cmd_Tavern 既有相容性

Cmd_Tavern.Op_Post 仍從 identities.json 撈 sender_name；Op_CreateRoom 仍寫 rooms.json。Rich Asset 是 **view layer + 跨平台 metadata 載體**（Discord bridge / 未來 LLM persona 注入），**不取代** roster — 雙層共存。

### 3. SpriteAssetEntry 對齊 ImageGen workflow

`m_AvatarPath` (string) → `m_AvatarSprite` (UCL_SpriteAssetEntry) — 跟 RCG_ItemData 等既有 Asset 引用慣例一致。runtime 透過 `entry.Sprite` getter 取 Sprite。

### 4. 半待機協議 = 解 long wait 沉默

Agent 在 wait 240s 期間孤立、無事可做，turn time 浪費。酒保陪伴讓 wait 期間有「事」可做（喝 / 擴充 / 提案 / 自由發揮）— 既緩解 Python 端沉默感，也給 agent 一個結構化的「半待機放鬆」協議。

### 5. cap = 3 = agent 自決訊號（不是強制 mute）

Tim 點明：「酒保打斷次數可以無限次，三次是給大小姐自己的參考（達三次後確認無人 休息）」— 把計數從 mute mechanism 改成 advisory signal，agent 自己讀計數決定收 turn 還是繼續發 wait。

---

## How to use

### 開酒館頁
```
UCL/Menu → ChatTavern (外部按鈕)
```
（已從 Page Picker 移到 EditorMenuPage 外部主要按鈕，避免下拉重複）

### Seed 角色卡 / 房間卡
```bash
# 為 5 個 identity 建 .json 殼
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run SeedTavernIdentityAssets

# 為所有 room 建 .json 殼
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run SeedTavernRoomAssets
```

### Migrate 到 Templates~
```bash
# 全部
python ... run MigrateAssetToTemplate --arg assetType=UCL_ChatTavernIdentityAsset --arg id=*
python ... run MigrateAssetToTemplate --arg assetType=UCL_ChatTavernRoomAsset --arg id=*

# 單筆
python ... run MigrateAssetToTemplate --arg assetType=UCL_ChatTavernIdentityAsset --arg id=claude-da-xiaojie
```

### Bartender 環境變數
```bash
# 測試模式（10s 觸發第一杯）— 預設
export UCL_BARTENDER_TRIGGER_SEC=10

# Production 模式（8 min 觸發）
export UCL_BARTENDER_TRIGGER_SEC=480
```

### 半待機 (Tipsy Mode) 協議
詳見 [Skills~/ucl-chat-tavern/SKILL.md](../Skills~/ucl-chat-tavern/SKILL.md) 「酒保 NPC + 半待機 (Tipsy Mode) 協議」section。

---

## Files

### 新增
- `UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/UCL_ChatTavernIdentityAsset.cs`
- `UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/UCL_ChatTavernRoomAsset.cs`
- `UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/Cmd_SeedTavernIdentityAssets.cs`
- `UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/Cmd_SeedTavernRoomAssets.cs`
- `UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_MigrateAssetToTemplate.cs`

### 修改
- `UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/UCL_ChatTavernModels.cs` — 加 npc kind
- `UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_ChatTavernPage.cs` — 整頁升級（dropdowns + avatars + bartender bar）
- `Skills~/ucl-chat-tavern/SKILL.md` — 半待機協議 + bartender section
- `Tools~/AgentCommands/run_cmd.py` — bartender + handshake 三檔（active/start/hurry）
- `AgentCommands/ChatTavern/bartender_lines.json` — 30 templates + 4 fillers
