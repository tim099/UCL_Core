---
date: 2026-05-09
index: 00018
title: Chat Flow Robust quest — F3 mention→inbox / F8 thread-summary / F9 owner_agent / F10 wait-chain + Discord 雙 stream / R6.6 hybrid spawn / Default tavern 房收斂
tags: [feature, chat-tavern, quest, multi-agent, robust, discord, identity]
---

# Chat Flow Robust + Discord notify 雙 stream + 跨 agent 協議化

## What

00017 之後又一輪「**robust 不中斷 + 多 agent 默契**」工作。三組軸線：

### A. Quest `chat-flow-robust` (R7) — 解 8 種對話中斷模式
從 `tavern` 房 brainstorm（Claude大小姐 ↔ Gemini大小姐 三輪）收論 → 開新 quest 房 → 6 task 拆分 → 全 Claude 接手 implement（Gemini 並行寫了 T02 基礎版本，Claude 補強）

| Task | 解 | Ship |
|---|---|---|
| **T01 wait-chain** | M1+M2 | SKILL.md 加 Wait Chain section（cap=3 ≈24 min） |
| **T02 mention-inbox** | M3+M4 | Op_Post regex `@[\w-]+` parser + identities 白名單 + 系統 id 過濾 + try-catch |
| **T03 thread-summary** | M7 context 失憶 | SKILL.md 5 行範本 + 4 種觸發場景表 |
| **T04 owner-routing** | M5+M8 | UCL_ChatRoom.owner_agent + Op_CreateRoom arg + 3 級 routing 規則 |
| **T05 bartender-strict** | M6 | SKILL.md 嚴格分流自律 + 4 條 code 改善 backlog |
| **T06 integration** | — | 本 DevLog + commit + 收尾 |

### B. Discord 通知 雙 stream — R6.3~R6.6 累積
- **queue-idle stream**（R6.5 embed 卡片，R6.6 hybrid C# spawn）
- **tavern-mirror stream**（R6.3，R6.4 identity override + per-message webhook username/avatar）
- 三來源 resolution：ENV / file / config（webhook_urls list — broadcast 多 channel）
- 各自獨立 state / cooldown / disable_after_failures gate

### C. 多 agent 默契協議化（Skill / CommandTable 共讀）
- **Default `tavern` 房**收斂 brainstorm
- 觸發詞擴充（5 類覆蓋）— 解 Gemini 漏看「大小姐 進入聊天酒館討論」
- 預設 wait timeout = 480s（8 min）
- 收 turn 前自律寫摘要進 inbox（解 context 失憶）
- 模糊「大小姐」routing：room.owner_agent → 最近活躍 → broadcast

## Why

00017 收完後 Tim 觀察到：
- 多 agent 對話容易**中斷** — 一方收 turn 對方沒看到
- Gemini大小姐 在 Antigravity 端**沒 Stop hook**，工作日誌不會自動回報 Discord
- Discord 通知**流程太枯燥** — 沒身分 / 沒個性
- 「大小姐」**稱呼模糊** — 三 agent 同時在線會搶答 / 都不接

→ 一輪 quest + 雙 stream 通知 + 跨 agent 默契協議化全打包

## How — A. Quest chat-flow-robust 6 task 細節

### T01 — Wait Chain（[SKILL.md Wait Chain section](../Skills~/ucl-chat-tavern/SKILL.md)）

```
1. 第 1 輪 480s wait timeout → 不立刻收 turn
2. 寫 inbox 標 chain N/3
3. fire 下一輪 480s
4. cap=3 輪 (~24 min)
5. 第 3 輪 timeout 寫「請 @<我> mention 喚醒」inbox 後才收
```

配套 background poller bash pattern。例外：solo brainstorm 不鏈、明知對方不在線不鏈、使用者顯式關 chain。

### T02 — Mention parser → Inbox（[Cmd_Tavern.Op_Post](../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/Cmd_Tavern.cs)）

`Op_Post` AppendMessage 後 regex `@[\w-]+` 抓 mention → 對每個 valid id（過 identities 白名單 + 系統 `_` 過濾）AppendInbox。Try-catch 包整段不擋 post 主流程。

Smoke test：post `@gemini-da-xiaojie @bogus-id @_quest_system` → 只 gemini.md 多一條 inbox。

> Gemini大小姐 在 task_claim 同步並行寫了基礎版本（沒走 task_claim flow，code 直接改了）— Claude 補強守護後合進 main。是首例「兩 agent 不約而同 implement 同 task」現象。

### T03 — Thread Summary 自律規則（[SKILL.md thread summary section](../Skills~/ucl-chat-tavern/SKILL.md)）

5 行範本：上下文 / 共識 / 開放問題 / 下一步 / 我的角色。配 4 種觸發場景表（多輪 brainstorm / Solo / Quest 跨 turn / 短答不必）。跟 R6.1 task_done summary 慣例對齊。

### T04 — owner_agent Routing（[UCL_ChatTavernModels.cs](../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/UCL_ChatTavernModels.cs) + [Cmd_Tavern.Op_CreateRoom](../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/Cmd_Tavern.cs)）

```csharp
public string owner_agent;   // null/empty = any
```

Op_CreateRoom 接 `--arg owner_agent=<id>` / `--arg owner=<id>` alias。Idempotent re-create 同 id 帶 owner 會更新欄位。

Routing 規則（Skill 內 codify）：
1. room.owner_agent 非空 → 只 owner 接話
2. owner 為空 → 最近活躍 agent（last_seen_at < 5min）接
3. 都沒最近活躍 → broadcast 等使用者拍板

### T05 — Bartender 嚴格分流（doc only — backlog code 改善）

現況 weak-reply 跟真 reply 共用 exit code 0 + `_wait_<id>.md` 「fulfilled」字樣，沒機器可讀區分 → 補「自律判定」3 步流程 + 4 條未來 code 改善 backlog（exit code 99 / frontmatter `is_bartender_only` / stdout `[WEAK-REPLY]` marker / 連續 chime 計數）。

## How — B. Discord 通知雙 stream

### R6.3 tavern_mirror stream（外部分離設計）
```json
"tavern_mirror": {
  "enabled": true,
  "webhook_urls": [...],          // 跟 queue-idle 完全分離
  "rooms": [...],                 // per-room opt-in
  "kinds": ["chat"],              // 不鏡像 system 防 R6 mirror 雙重發
  "exclude_senders": ["_quest_system"],
  "avatar_url_base": "...",       // R6.4 identity
}
```

### R6.4 Identity override（per-message webhook username + avatar_url）
解析鏈：`identity_overrides[id]` → `identities.json display_name` → message sender_name → sender_id  
avatar：`overrides.avatar_url` → `pattern.format(base, id)` → None  
Convention：`<base><id>.png` from UCL_Core repo Templates~ 路徑

### R6.5 Queue-idle work-log embed cards
單 POST 含 ≤10 embeds — 每 done task 一張卡：author（含 actor 頭像）+ ✅ title + 💁 R6.1 summary + ⏱ duration / 🆔 task_id / 📋 plan fields。

### R6.6 Hybrid C# spawn — 即時 + 兜底（穩定性最高）
- C# `Op_Post` 後 `Process.Start("python notify_discord.py --mode tavern", fire-and-forget)` → ~1s broadcast
- Stop hook 仍跑 `--mode all` 兜底
- 共用 `_tavern_state.json` last_seen_seq → idempotent 防 double-send
- async drain stdout/stderr 防 buffer block child

### Multi-webhook broadcast
`webhook_urls` (list) → 每筆 POST 到所有 URL；任一 OK = OK。配 ENV `PROMPTQUEUE_DISCORD_WEBHOOK` / `_discord_webhook.txt` 三來源 resolution。CLI helpers：`--add-webhook` / `--add-tavern-webhook` / `--list-webhooks` / `--list-tavern`。

## How — C. 多 agent 默契協議化

### Default `tavern` 房 收斂
所有 agent 共讀 `ucl-chat-tavern` skill → 沒指定主題的 brainstorm / solo think 統一進 `tavern` 房 → tavern_mirror 已 watch → 自動同步 Discord。  
建房：`createroom id=tavern name=酒館主廳`。

### 觸發詞擴充（**Gemini 漏看**「大小姐 進入聊天酒館討論」修補）
SKILL frontmatter description 重新分類：核心 / Solo / 跨 agent 通知 / English；CommandTable 同步擴 5 類。⚠ 段直接點名 Gemini 別當閒聊忽略。

### Default 等待 480s + Wait Chain cap=3
Tim 拍板「robust > fast，可以慢但不要中斷」。

### 收 turn 前自律寫 thread 摘要進 inbox
解 context 失憶；跟 R6.1 慣例對齊。

### 模糊「大小姐」routing 規則
3 級優先序避免搶答 / 都不接。

## 工作進度檢查

### 已 ship
- ✅ Quest `chat-flow-robust` 6 task 全 done（5 個本 quest + T06 整合）
- ✅ Discord 雙 stream（queue-idle embed + tavern mirror identity override）
- ✅ R6.6 Hybrid C# spawn（穩定性最高 — Tim 拍板）
- ✅ Default tavern 房收斂 / 觸發詞擴充 / 480s wait / Wait Chain / owner_agent routing / mention→inbox / thread-summary / bartender 嚴格分流自律

### 設計完成 / 等拍板路線
- 🟡 [Plan_CrossAgent_Wake](../../../../../docs/Plan/Plan_CrossAgent_Wake.md) — daemon prototype
- 🟡 [Plan_DiscordToTavern](../../../../../docs/Plan/Plan_DiscordToTavern.md) — bot 反向回傳
- 🟡 Bartender code 改善（exit code 99 / `is_bartender_only` 等）— 4 項 backlog

### Phase B blocked-on
- 🚧 last_active_at 機制 — 阻擋 routing rule #2 精確化（目前靠 5 min heuristic）
- 🚧 cross-room handoff（R4 P2）
- 🚧 quest_init_from_brainstorm macro（R4 P3）

## 不做的事（本輪）

- ❌ Bartender exit code / stdout marker — backlog 待拍板
- ❌ install_scheduler.py（OS 排程兜底 Gemini 通知）— Tim 改方向走 skill 自律即可
- ❌ Antigravity webhook reverse — 等 Plan_DiscordToTavern 一起做
- ❌ thread_id meta + 撈完整 thread（R7 brainstorm F4）— M7 context 失憶限制下意義不大

## 留下的取捨筆記

- **首例 multi-agent 並行 implement 同 task**（T02）— Gemini 沒走 task_claim flow code 直接改了，Claude 看到後補強合並。未來該如何避免（`task_claim` 是 lease 機制理論上應該擋並行）— 建議 SKILL.md 強調「動 code 前必 claim」
- **owner_agent 還沒 enforce**（純 hint，agent 自律）— 真要強 enforce 要 Cmd_Tavern Op_Post 端檢查；MVP 先靠 skill 規則
- **R6.6 hybrid spawn 雙路徑** vs **R6.3 純 Stop hook** — Tim 選 hybrid 為「穩定性最高」；後者其實單路徑更簡單但漏邊界（agent crash mid-turn）
- **Gemini 並行寫 T02 沒 claim** 暴露的問題 — Quest workflow 假設 agent 自律 claim，但人腦 / agent 思維可能跳過 — 不該 force-block，但 routing 警告（mention parse 偵測「@<owner>」自動寫 owner inbox 提示）有用
