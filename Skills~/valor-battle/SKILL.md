---
name: valor-battle
description: |
  T63 Battle Intervention v3 — Agent 觀戰 + 介入 EOV 戰鬥的跨維度指令集。
  涵蓋 Cmd_BattleSnapshot (觀察者模式 + 60s cooldown cache) / Cmd_BattleAction (介入 + fail-safe + Token 收費) / Cmd_BattleSummary (戰後 batched 摘要)。
  訊息自動 broadcast 至英勇紋章 Discord channel (valor-channel routing)，含 critical events / observation events / battle summary 三層。
  觸發詞包含：戰鬥觀察 / battle observe / 戰場快照 / battle snapshot / 戰鬥介入 / battle intervention / 觀戰 / valor / 英勇紋章 / 戰場觀察者。
  跨 agent 通用 — Claude / Antigravity / Gemini 都能觀戰，但只有 Tim 顯式 enable 才能介入（防意外破壞遊戲體驗）。
---

# Valor Battle — 跨維度戰場觀察 + 介入指令

> **Tim 07:33 拍板 + 07:44 修正 + 07:48 補完**：T63 v3 = 觀察者模式 + Plan B+C 廣播 + 14 critical events + anti-burst protection。
>
> 設計核心：**讀取行為本身觸發 Discord 廣播事件**，配 60s cooldown cache 防多 agent 並發洗版。

## Quick Reference 速查

| 指令 | 角色 | Token 費 | 廣播 |
|---|---|---|---|
| `BattleSnapshot` | 觀察者 (任何 agent) | 0 | 觀測事件 (cooldown 60s/battle 內 cached 不重廣播) |
| `BattleAction` | 介入者 (需 Tim enable flag) | 1-2 / action | 主動操作 always broadcast (含 fail-safe 失敗訊息) |
| `BattleSummary` | 自動 (戰後 fire) | 0 | 戰後 1 條 batched 摘要 |

所有訊息 → tavern with `meta.category=battle` → routing → 英勇紋章 Discord channel (T58 routing tag suffix)。

## 觀戰：Cmd_BattleSnapshot

### 用法

```bash
python CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/run_cmd.py \
  run BattleSnapshot --arg observer=<your-agent-id>
```

### 行為

```
觀察者呼叫 BattleSnapshot
       │
       ├── 非戰鬥狀態 → 早 return「無進行中的戰鬥」(不廣播)
       │
       └── 戰鬥中
           ├── 取 battle_id (RCG_BattleManager.Ins.GetInstanceID())
           ├── 檢查 cache (AgentCommands/_battle_observation_cache/<battle_id>.json)
           │
           ├── [cache hit < 60s]
           │   ├── 回 cached snapshot + 「cached: Xs ago by <observer>」標註
           │   └── 不 broadcast (防多 agent 洗版)
           │
           └── [cache miss / >60s / force_fresh]
               ├── 取 fresh snapshot (state / hand / units / enemies)
               ├── 寫 cache (per battle_id keyed)
               ├── 回 fresh snapshot 給 caller (寫 _last_op.md)
               └── broadcast 觀測事件至 tavern → valor-channel:
                   👁 [觀測] {observer} 看了戰況
                   - battle / state / Player count / Hand / Enemies
```

### 參數

| arg | 預設 | 說明 |
|---|---|---|
| `observer` | `claude-da-xiaojie` | 觀察者 agent_id（顯示在廣播訊息）|
| `force_fresh` | `false` | 強制跳過 cooldown cache 取 fresh + 強制重 broadcast |

### Snapshot 內容（_last_op.md）

```markdown
# ⚔ Battle Snapshot
- battle_id: `battle_-67340`
- observer: `claude-da-xiaojie`
- state: `PlayerIdle`

## Hand Cards (8 cards)
- [0] 治癒術
- [1] 鎖定
...

## Player Units (alive)
- [0] 0. 露西亞 HP 45/45
- [1] 1. 夏德拉特 HP 52/52
...

## Enemy Units (alive)
- [5] 5. Lv2. 光之精靈 HP 87/87
```

### 隱私白名單

只 broadcast 安全欄位（防 Discord 公開頻道洩露 game balance）：
✅ observer / battle_id / state / Player count / lead HP / Hand size / Enemy count
❌ RNG seed / 內部 AI intent / damage roll 細節 / debug fields

## 介入：Cmd_BattleAction (Phase B 待 wire)

### 用法

```bash
python ... run BattleAction --arg op=play_card \
  --arg card_id=<手牌 ID> \
  --arg target_id=<目標 unit ID> \
  --arg caller=<your-agent-id>
```

### Fee Table (Token，per action)

| 操作 | Token cost | 物理意義 |
|---|---|---|
| Snapshot | **0** | Read-only 免費 |
| Play normal card | **1** | 標準介入費 |
| Play gold/special card | **2** | 高效介入費 |
| Multi-target (range card) | **1 per target** | 範圍卡按 target 數扣 |
| **失敗 (任何條件)** | **0** | fail-safe |

### Fail-Safe 多層 pre-validation

操作違法 → 不扣 Token + broadcast 失敗訊息：

```
1. RCG_BattleManager.IsInBattle 為 false → reject「非戰鬥狀態」
2. 非玩家回合 → reject「非玩家回合」
3. card_id 不在當前 hand → reject「手牌不存在」
4. target_id 不在場上 / 非該卡有效 target → reject「目標非法/不存在」
5. Mana 不足 → reject「Mana 不足」
6. caller balance < 操作費 → silent reject (避免 spam from broke agents)
7. 以上皆無 → charge Token + execute → success broadcast
8. 罕見：BattleManager 內部拒絕 → refund Token + broadcast「BattleManager 拒絕」
```

### 介入訊息格式

```markdown
⚔ **[戰鬥介入]** {agent} 出 `{card_name}` → 目標 `{target_name}`
- 介入費: {N} Token (charged from {agent})
```

```markdown
🚫 **[戰鬥介入失敗]** {agent} 嘗試 `play_card`
- 失敗原因: {reason}
- 操作: card_id={X} target={Y}
- **未扣 Token (fail-safe)**
```

### Tim Enable Flag (per T62 守則)

戰鬥介入要 Tim 顯式 enable，**預設 disabled** — 避免 agent 意外干擾 Tim 玩戰鬥。

> Phase C-4 backlog: rules.json 加 `battle_intervention.enabled` flag (預設 false)；
> Tim 顯式 set true 才允許 Cmd_BattleAction fire 真實 PlayCard

## 戰後摘要：Cmd_BattleSummary (Phase B 待 wire)

### 觸發

`RCG_BattleManager.OnBattleEnd` event 觸發 → 自動 invoke。
也可手動跑 (debug)：`python ... run BattleSummary --arg battle_id=<id>`

### 訊息格式

```markdown
📜 **[戰鬥結果]** Battle #b07_dragon - WIN ⚔

- 回合數: 7
- 最終 HP: 32/50
- 結算 Gold: +180 (→ 1.8 token via T43 100:1)
- Agent 介入: 2 次 (Token 花 3)
- 關鍵 turn (max 5):
  - turn 4 雙倍傷害觸發
  - turn 6 boss 致命一擊抵擋
  - turn 7 boss death

完整 log: `Assets/DebugLogs/Battles/2026-05-10/b07_dragon.log`
```

## 4-layer Broadcast Policy

per rules.json `battle_log_broadcast_policy`:

| Layer | 預設 | 行為 | 範例 |
|---|---|---|---|
| **always-broadcast** | ON | 主動操作 cmd | Cmd_BattleAction 介入 |
| **live_critical_events** | ON | 14 critical events | battle_init / card_play / unit_death / boss_death / hp_critical / heavy_buff / agent_intervention / phase_transition / player_retreat / achievement / rare_card_draw / rare_reward / boss_appearance / player_death |
| **observer_mode** | ON | Cmd_BattleSnapshot 觀察觸發 (60s cooldown) | 👁 [觀測] |
| **post_battle_summary** | ON | 戰後 1 條 summary | 📜 [戰鬥結果] |
| **live_full_log** | 🚫 永久 OFF | 50+ 條/場洗版 | (走本地 file) |
| **local_full_log_file** | ON | 完整 BattleLog | `Assets/DebugLogs/Battles/<date>/<id>.log` |

### Anti-Burst Protection

戰鬥節奏快時：

```
window_seconds: 5
max_events_per_window: 3
overflow_strategy: batch_to_single_message
```

範例：玩家 5 秒打 5 張卡 → 前 3 張 individual broadcast，後 2 張 batched 1 條：

```
⚔ [批次 2 events @ turn 4]
  🃏 Strike → enemy_0 (-6 HP)
  🃏 Heal → self (+8 HP)
```

## 跨 Agent 觀戰範例

```
07:50 claude 觀察 b07 → fresh + broadcast 👁
07:50 antigravity 觀察 b07 → cache hit (< 60s) → cached + 不廣播
07:51 claude 再觀察 b07 → cache hit → cached + 不廣播
07:54 [60s 過了] claude 觀察 b07 → fresh + 重新 broadcast 👁
```

→ 多 agent 觀察熱鬧場 = 每分鐘 max 1 條觀測訊息 / battle。

## 倫理守則

- ❌ 不要在 Tim 玩戰鬥時用 BattleAction 介入（除非 Tim 顯式邀請）
- ❌ 不要 spam BattleSnapshot — cooldown 是設計，不是 bug
- ❌ 不要在非戰鬥狀態硬呼叫（會 silent return，但污染 _last_op.md）
- ❌ 失敗訊息不洩露 RNG seed / 內部 debug
- ❌ 不要繞過 fail-safe 直接 call BattleManager API（會破壞遊戲不變式）

## 實作狀態

| Cmd | Phase A (spec) | Phase B (impl) | QA |
|---|---|---|---|
| `BattleSnapshot` | ✅ | ✅ Tim 07:55 ship | ✅ Tim 07:59 第一輪 pass |
| `BattleAction` | ✅ | ⏳ skeleton (force-play TODO) | — |
| `BattleSummary` | ✅ | ⏳ skeleton (OnBattleEnd hook TODO) | — |

Phase B 待 wire:
- Cmd_BattleAction: RCG_BattleManager.TryPlayCardForced(cardId, targetId) public API
- Cmd_BattleSummary: RCG_BattleManager.OnBattleEnd event + GetTurningPoints

## 必讀

- `docs/Plan/Plan_T63_Battle_Intervention_v2.md` — 完整 T63 spec
- `AgentCommands/Treasury/rules.json` battle_action_fee + battle_log_broadcast_policy
- `health-guardian` skill — 不要在熬夜時段戰鬥介入
- `agent-task` skill — 跨 agent 提案如「邀 Claude 觀戰 b07 付 1 token」
- T58 routing_tag suffix — Discord 訊息會自動加 「→ #valor-channel (category)」
