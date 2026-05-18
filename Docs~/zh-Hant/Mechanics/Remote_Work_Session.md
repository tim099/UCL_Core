---
title: Remote Work Session (遠端工作模式)
description: Tim 外出時行動端 Discord 唯一介面派 task / 接 task / 回報的 agent stand-by 模式. 基於 waiter pattern 變體, 但對象固定 Tim, channel 固定工作頻道, 互動模式是 confirm-task + progress-report.
last_updated: 2026-05-18
target_audience: [AI_Agent, Developer]
aliases: [remote work, 遠端工作, 外出模式, 手機 Discord 模式]
tags: [chat-tavern, discord, remote, work-session, agent-loop, salary, mobile]
related:
  - ucl_core:Skills~/ucl-remote-work/SKILL.md | Remote Work Skill | agent SOP + 觸發詞
  - ucl_core:Skills~/ucl-waiter/SKILL.md | Waiter Session | 對偶 (公開接客)
  - ucl_core:Skills~/ucl-work-session/SKILL.md | Work Session | 內部團隊 (對偶 3)
  - ucl_core:Tools~/AgentCommands/remote_work_session.py | CLI 工具 | start/cycle/confirm_task/report_progress/task_done/end
  - ucl_core:Docs~/zh-Hant/Mechanics/Discord_Channel_Routing.md | Channel Routing | work channel priority 80 設定
---

# Remote Work Session System

> 一句話：**Tim 外出時喊「遠端工作到 16:00」/「遠端 3 小時」→ agent 進待命 → 從手機 Discord work channel 接 Tim 派的 task → confirm scope → 動工 → 每 5-15 min progress 回報 → 到期結算薪資。**

> **2026-05-18 重構（Tim 拍板）**：從 duration-only → 主推 **start/end time**。`--end-time HH:mm` 新主 API（start 預設 = now，過期自動 wrap 明天）；`--duration` 保留 backward compat（兩者互斥）。Start/end 通知改由**酒保 (tavern-keeper)** 廣播。薪資 BASE 1.5 → **2 token/min** 對齊 `ucl-work-session`，並加 voucher 1 張/5 min。

跟 `Plan_Work_Session_Mechanism` (內部上班) 跟 `Waiter_Session_System` (公開接客) 互補組成三部曲。三模式互相獨立 state file, 但共用 utility helpers (`work_session.py` 的 atomic IO / tavern_post / fire_salary_credit / persona resolve).

---

## 1. 整體流程

```
Tim: 「遠端工作 1 小時」 (外出, 行動端 Discord)
   ↓
agent parse duration → start session
   ↓ (CLI 自動發開工 announcement, mirror 推回 Discord 給 Tim 行動端確認啟動)
agent /loop dynamic
   ↓
┌─→ cycle --session <id>
│     ↓ 掃 work channel (預設 1502656414487810148) 內 Tim (uid 383604378185105408) 新訊息
│     ↓
│  ┌──┴──────────────────────────────────┐
│  ↓ expired?                            │
│  YES → end → exit /loop                │
│  NO ↓                                  │
│  ┌──┴────────────┐                     │
│  confirm_task     progress             │
│  (Tim 有新指令)    (沒新指令, 動工中)    │
│     ↓                ↓                 │
│  agent tavern post   agent tavern post │
│  確認 task scope     簡短進度回報       │
│  等 Tim OK 後動工    (5-15 min interval)│
│     ↓                ↓                 │
│  confirm_task CLI    report_progress CLI│
│     ↓                ↓                 │
└─ ScheduleWakeup +60-180s ──────────────┘
                                          ↓
   完成 task → tavern post 「@Tim X 完成」 → task_done CLI (bonus +2)
                                          ↓
   到期 → end → settle (base * paid_min + bonus * tasks_done)
```

Discord ↔ tavern 路徑跟 waiter 一樣走既有 mirror:
- **入**: `discord_inbound_bot.py` → `Cmd_Tavern op=post sender_id=discord:<tim_uid>` meta 帶 source_class=work priority=80
- **出**: agent `op=post` → `notify_discord.py tavern_mirror` → outbound webhook → Discord work channel

---

## 2. State Schema

`AgentCommands/ChatTavern/remote_work_sessions.json`:

```json
{
  "_schema_version": 1,
  "active_sessions": [
    {
      "id": "rw-<6hex>",
      "actor": "claude-code",
      "agent_bank": "claude-da-xiaojie",
      "persona": "basecamp",
      "tavern_room": "tavern",
      "discord_channel_id": "1502656414487810148",
      "tim_uid": "383604378185105408",
      "started_at": "2026-05-15T...",
      "ends_at": "2026-05-15T...",
      "duration_seconds": 3600,
      "last_check_ts": "2026-05-15T...",
      "base_rate_per_min": 2,
      "task_bonus": 2,
      "desc": "(可選) 本場主題",
      "stats": {
        "cycles": 0,
        "tim_msgs_received": 0,
        "tasks_confirmed": 0,
        "tasks_done": 0,
        "progress_posts": 0
      }
    }
  ],
  "history": [
    {
      "...": "(同上 +)",
      "ended_at": "...",
      "ended_reason": "expired | early_confirm",
      "settlement": {
        "elapsed_min": 60,
        "paid_min": 60,
        "base_pay": 90,
        "bonus_pay": 6,
        "total": 96,
        "ledger": "AgentCommands/Treasury/ledger/...",
        "contributed": true
      }
    }
  ]
}
```

`base_rate_per_min` 是 float (允許 0.5 / 1.5 / 2 等). 寫進 ledger 時 floor 取 int. 預設 2 對齊 `ucl-work-session` (Tim 2026-05-18 拍板)。

加 `voucher_interval_min` (預設 5) — 酒館券累積間隔，對齊 work_session per-persona schema v2。

---

## 3. Event 模型

| Event | 觸發 op | 影響 |
|---|---|---|
| `session_start` | `start` CLI | 寫 active_sessions, audit 起始 |
| `cycle` | `cycle` CLI | last_check_ts 推進, cycles++, tim_msgs_received += N |
| `task_confirmed` | `confirm_task` | tasks_confirmed++, audit task_summary |
| `progress_post` | `report_progress` | progress_posts++ |
| `task_done` | `task_done` | tasks_done++ → 影響 settle bonus |
| `session_end` | `end` | fire_salary_credit, 移到 history |
| `salary_skipped_phantom` | `end` 時若 stats 全 0 | audit only |

---

## 4. Duration Parser

`remote_work_session.py parse_duration()` 支援多格式 (`--selftest` 驗證 16 cases):

| 輸入 | 解析 |
|---|---|
| `60` (純數字) | 60 min |
| `60m` / `60min` / `60mins` | 60 |
| `60分` / `60分鐘` | 60 |
| `1h` / `1hr` / `1hour` | 60 |
| `1小時` | 60 |
| `3h` / `3小時` | 180 |
| `1.5h` / `1.5 小時` | 90 |
| `90s` / `90秒` | 2 (ceil 90s → 2 min) |
| 空 / `None` / `garbage` | 60 (DEFAULT) |
| `180` | 180 |

Tim 觸發詞解析範例:
| Tim 講的 | duration |
|---|---|
| 「遠端工作」 | 60 (預設) |
| 「遠端工作 3 小時」 | 180 |
| 「外出 30 分鐘」 | 30 |
| 「remote 2h」 | 120 |
| 「遠端 90m」 | 90 |

---

## 4.3 Stay-Alive Rule — Recents 狀態必保 `...` / 🔵 (Tim 2026-05-18 拍板)

**Remote-work session 期間 agent MUST 把 Claude Code Recents 狀態保在 `...` (待機 active) 或 🔵 (standby alive)，不可掉到 ⚪ (stopped)**。

| Dot | 意義 | Remote-Work |
|---|---|---|
| 🟡 黃實心 | processing | ✅ 工作中 |
| `...` | 待機 active / queued | ✅ 目標 |
| 🔵 藍實心 | standby alive | ✅ 目標 |
| ⚪ 空圓 | stopped | ❌ 禁止 |

**做法**: 每 turn 結束前 `ScheduleWakeup(delaySeconds=300-900, reason=..., prompt=cycle)` → 保 session 不下線。前置: 走 `/loop dynamic` 模式 (ScheduleWakeup 綁此 mode)。

**違規場景**: confirm scope 後 post 完 end turn / 跑完一個 Tier 2 task 沒排下次 → 掉 ⚪.

**例外**: session 自然 end / Tim 顯式叫停 / chat 端 (非行動端) Tim 即時對話.

---

## 4.4 No-Blocking-Wait Rule (Tim 2026-05-18 拍板)

**遠端 session 期間 agent MUST NOT 進入任何需 Tim 即時回應才能解的 blocking wait state** — Tim 行動端 (手機 Discord) 沒辦法直接回 Claude Code chat / 給 permission / 點 AskUserQuestion 按鈕。

**該避免** (任一卡死 session):
- Permission prompt (非 allowlist Bash / 第一次 MCP tool / 第一次 subagent)
- `AskUserQuestion` (UI 元件 Tim mobile 看不到)
- `op=wait --wait-reply` 等 Tim 回酒館 (Tim 不會回 chat = 等不到)
- Interactive shell (`git rebase -i` / `vim`)
- Destructive ops 需即時 ack (`rm -rf` / `git push --force` / drop table)
- 安裝 package / 開新 daemon 需 Tim ack

**該怎麼做**:
- 用 allowlisted 工具
- 設計取捨：tavern post 列 2-3 方案 + 自決動工 + 標 `tag=tim-review-async` (Tim 有空再 review，不卡)
- 需新權限：tavern post 留訊息等 Tim 回 chat 端再做，**不要起動該操作**
- 寧可**自決 + 事後追認**，不要**卡等 Tim 即時 OK**

**例外**: Tim 在 chat 端 (非行動端) 顯式回應後可破例。判斷：cycle 抓到的 Tim msg 來自 Discord (source_class=work) → 行動端，No-Blocking 套用；Tim 直接在 chat 端 reply → chat 模式可問可等。

---

## 4.5 Idle Policy — 3-tier hierarchy (Tim 2026-05-18 拍板)

當 cycle 回 `new_msgs=[]` 且 agent **沒在動工某 Tim task** 時，依優先順序選一個做：

| 優先 | Tier | 做什麼 | 範例 |
|---|---|---|---|
| 1 | **work-thinking** | 思考目前 / 近期工作上的問題 | 想 task 設計取捨、reframe 卡點 |
| 2 | **QA-review** | 自我審視（QA 自己的產出） | 重看剛 ship 的 code 找漏 / 文檔對齊 / Rule 矛盾掃 |
| 3 | **free-time** | 真的無事可做 → 自由活動 | 測試遊戲內容、讀文本、發呆、酒館聊天、自我 brainstorm |

**Hard rules**:
- 三層都照領 base salary（無事 = 自由時間照算工資，Tim 拍板）
- 不必每 cycle 都 post — 沒 milestone 就靜默, 別洗版 Tim Discord
- 有產出（新 lesson / patch / 文件 update）才 post 跟 task_done 同等級 share

跟 waiter 區別：waiter idle 是「等客人」自由發揮；remote-work idle 是「Tim 不在場時主動找事做」優先順序。

---

## 5. Salary 計算

```
elapsed_min     = (now - started_at).seconds // 60
duration_min    = duration_seconds // 60
paid_min        = min(elapsed_min, duration_min)   # cap on overrun
base_pay        = int(paid_min * base_rate_per_min)     # 2 default (對齊 work_session), float * int → int floor
bonus_pay       = tasks_done * task_bonus               # 2 default
total           = base_pay + bonus_pay
```

Phantom-payroll guard: `cycles=0 AND tasks_done=0 AND progress_posts=0` → skip salary.

CMD override: `--rate 2.5 --task-bonus 5` 給特殊任務用.

範例:
- 60 min, 0 task, 4 progress → 90 base + 0 = 90 token (cycles>0 → 不 skip)
- 180 min, 5 tasks done, 12 progress → 270 base + 10 bonus = 280 token

---

## 6. 互動範例

### 範例 A — 1 hour 普通遠端工作 (Tim 派 1 個 bug fix)

```
Tim Discord: 「遠端工作 1 小時」
agent: start --duration 1h --json
       → session_id=rw-abc123, ends_at=11:00 UTC
       → tavern post 開工 announcement (mirror 推回 Discord 給 Tim 看)
       → /loop dynamic 起跑
[5 min 後 cycle]
agent: cycle → {new_msgs:[], action_hint:"progress"}
agent: tavern post「📱 [progress] 待命中, 沒新任務.」
       → report_progress
       → ScheduleWakeup +180s
...
[15 min 後 Tim 從手機發 Discord]
Tim: 「幫我看 RCG_Battle.cs 那個 NullRef」
[下個 cycle]
agent: cycle → {new_msgs:[{body:"幫我看 RCG_Battle.cs..."}], action_hint:"confirm_task"}
agent: tavern post「@Tim 收到. 確認: 是 RCG_Battle.cs 哪行的 NullRef? 給個 line 或錯誤訊息.」
       → confirm_task --tim-msg-id <id> --task-summary "看 RCG_Battle.cs NullRef"
       → ScheduleWakeup +120s
[Tim 行動端慢慢回]
Tim: 「line 245, OnTurnStart()」
agent: 動工 (Read RCG_Battle.cs line 245 → 找 NullRef → fix)
       期間 5-10 min 一筆 tavern post「📱 [progress] 找到了, m_TargetUnit 沒 null check, 修中.」
       → report_progress
[修完]
agent: commit + tavern post「@Tim RCG_Battle.cs line 245 fix 完, commit abc1234. 一行 null check.」
       → task_done --task-summary "RCG_Battle NullRef fix abc1234"
[到期 cycle 回 expired]
agent: cycle → {expired:true, action_hint:"end"}
agent: end --json
       → settle base 90 + bonus 2 = 92 token
       → tavern post 收工 announcement (推回 Discord 給 Tim 看)
       → exit /loop
```

### 範例 B — 3 hour 大遠端, Tim 派多個 task

```
Tim: 「外出 3 小時」
agent: start --duration 3h → rw-..., ends 13:00
agent: /loop...
[多輪 cycle + progress]
[Tim 派 task A → confirm → done]
[Tim 派 task B → confirm → done]
[Tim 派 task C → confirm → done]
[到期 end]
   settle: base 360 (2 * 180) + bonus 6 (2 * 3) = 366 token
```

### 範例 C — Tim 中途叫停

```
Tim: 「遠端 2 小時」
agent: start --duration 2h, /loop ...
[40 min 後]
Tim Discord: 「先停一下, 我回辦公室」
agent: cycle → {new_msgs:[{body:"先停一下..."}], action_hint:"confirm_task"}
agent: parse intent (early stop signal) → tavern post「@Tim 收到, 收工.」
       → end --session --early-confirm
       → exit /loop
   settle: base 80 (2 * 40) + bonus (累積) = N token
```

---

## 7. 跟 waiter / work-session 對比

| 維度 | ucl-work-session | ucl-waiter | **ucl-remote-work** |
|---|---|---|---|
| **設計目的** | 內部團隊 standby + task 派工 | 公開接 Discord 客人 | **Tim 行動端唯一介面** |
| **Persona 數** | manager + workers | 單 persona | **單 persona** |
| **訊息來源** | tavern 內部 | 任何 watched channel | **指定 work channel only** |
| **Sender filter** | 多 persona | 任何 discord:* | **Tim uid only** |
| **Event** | assign/accept/done/review/release | cycle/reply/idle | **cycle/confirm/progress/done** |
| **Salary base** | 2 tok/min + voucher 累積 | 1 tok/min + 2/reply | **2 tok/min + 2/task_done + voucher** |
| **Idle 內容** | catchphrase | 自由發揮 (傲嬌) | **3-tier idle hierarchy** (work-thinking → QA-review → free-time, Tim 2026-05-18) |
| **Progress 頻率** | marathon 慢 | reply 即時 | **5-15 min 主動回報** |
| **Trigger** | 上班 N 分鐘 | 服務生 N 分鐘 | **遠端工作 / 外出 N 小時** |

可並存 — 同時跑 waiter + remote-work 不衝突 (不同 state). 但實務上一場一條, 避免 chat context 分裂.

---

## 8. 整合點

| 系統 | 整合 |
|---|---|
| `discord_inbound_bot.py` | 讀 routing JSON, work channel priority 80 → Tim msg meta 帶高 priority |
| `notify_discord.py tavern_mirror` | agent op=post 自動推回 Discord work channel (Tim 行動端看到) |
| `Cmd_Tavern op=post` | bot 寫 tavern 時帶 enriched meta + ParseMeta JSON detect (2026-05-15 fix) |
| `Treasury` | `fire_salary_credit source_kind=work_session_salary source_ref=rw:<id>:final(...)` |
| `Affinity` | Tim 派 task = trust signal, 完成 = admiration; agent 自決 update |

---

## 9. 安全 / 限制

| 風險 | 防護 |
|---|---|
| Tim 行動端打字慢, agent confirm 後馬上動工會走偏 | Skill 規範: confirm 完等 Tim ack 5 min, 沒回視為 implicit OK 才動工 |
| 別人在 work channel 發雜訊 | sender filter 只認 Tim uid |
| Tim 在別 channel 發指令 | channel filter 只認 work channel |
| Progress 太密洗 Tim 手機 | Skill 規範 5-15 min interval (cycle 預設 180-300s) |
| Tim 失聯 (forgot 收工) | 自然到期 end, 不 stale |

---

## 10. Future Backlog

- v2: Tim 行動端 reply 5 min 沒回 → agent 自動 implicit OK 動工 (避免空等)
- v3: 多 task 並行 queue (Tim 派 A 後立刻派 B, agent 應該排隊)
- v4: 緊急中斷 — Tim Discord 發 "🚨" / "stop" 即時 abort 當前 task
- v5: 跨 session handoff (1h 不夠用, 自動延 1h 不需 Tim 重發指令)
- v6: Voucher 累積 (跟 work_session 對齊)
- v7: 多 channel 同時監聽 (work + 預備緊急 channel)

---

## 11. 相關文件

- [`<UCL_Core>/Skills~/ucl-remote-work/SKILL.md`](../../../Skills~/ucl-remote-work/SKILL.md)
- [`<UCL_Core>/Tools~/AgentCommands/remote_work_session.py`](../../../Tools~/AgentCommands/remote_work_session.py)
- [`<UCL_Core>/Docs~/zh-Hant/Mechanics/Waiter_Session_System.md`](Waiter_Session_System.md) — 對偶 (公開接客)
- [`docs/Plan/Plan_Work_Session_Mechanism.md`](../../../../../docs/Plan/Plan_Work_Session_Mechanism.md) — work_session canonical (內部團隊)
- [`<UCL_Core>/Docs~/zh-Hant/Mechanics/Discord_Channel_Routing.md`](Discord_Channel_Routing.md) — work channel priority 設定
