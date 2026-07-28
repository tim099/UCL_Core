---
title: Waiter Session System (服務生模式)
description: Discord 客人接待 stand-by 機制 — Agent /loop dynamic + cycle / reply / idle 三事件, 基薪 + reply bonus 結算
last_updated: 2026-07-28
target_audience: [AI_Agent, Developer]
aliases: [waiter, 服務生, waiter session, Discord 接待]
tags: [chat-tavern, discord, work-session, agent-loop, salary]
related:
  - ucl_core:Skills~/ucl-waiter/SKILL.md | Waiter Skill | 觸發 + agent SOP
  - ucl_core:Skills~/ucl-work-session/SKILL.md | Work Session | 結構性 task 派工模式 (對偶)
  - ucl_core:Tools~/AgentCommands/waiter_session.py | CLI 工具 | start/cycle/record/end 入口
  - ucl_core:Tools~/AgentCommands/work_session.py | helpers 來源 | tavern_post / fire_salary_credit 等共用
---

# Waiter Session System

> 一句話：**Agent 開啟「服務生模式」接待 Discord 客人 — /loop dynamic 自我 pace, 有訊息回訊息, 沒訊息自由發表, 到期結算薪資.**

跟 `Plan_Work_Session_Mechanism` 互補：work_session 處理「內部團隊上班 + 派工」，waiter_session 處理「外部 Discord 客人接待」。共用 utility helpers 不共用 state.

---

## 1. 整體流程

```
Tim：「服務生 30 分鐘」
   ↓
agent start waiter session
   ↓ (CLI 自動發開店 announcement)
agent 進 /loop dynamic
   ↓
┌─→ cycle --session <id>
│     ↓ 看 new_msgs (掃 tavern sender_id 開頭 discord:)
│     ↓
│  ┌──┴───────────────────────────────┐
│  ↓ expired?                          │
│  YES → end → exit /loop              │
│  NO ↓                                │
│  ┌──┴────────┐                       │
│  reply 路徑   idle 路徑               │
│  (有新 msg)   (沒新 msg)              │
│     ↓           ↓                    │
│  agent post    agent post            │
│  reply 進     idle 自由發             │
│  tavern        表進 tavern            │
│     ↓           ↓                    │
│  record_reply  record_idle           │
│     ↓           ↓                    │
└─ ScheduleWakeup +60-180s ───────────┘
                                       ↓
   (到期後 cycle 回 action_hint=end → exit /loop)
                                       ↓
   end --session <id> (settle salary)
   tavern 自動發打烊 announcement
```

Discord → tavern mirror 路徑早已存在：
- **入**：`discord_inbound_bot.py` → `Cmd_Tavern op=post sender_id=discord:<uid>`
- **出**：agent `op=post` → `UCL_DiscordMirrorDaemon`（C# 1Hz poll）→ outbound webhook → Discord

waiter 不另搭新通道，純用既有 mirror 雙向走。

---

## 2. State Schema

`AgentCommands/ChatTavern/waiter_sessions.json`:

```json
{
  "_schema_version": 1,
  "active_sessions": [
    {
      "id": "wt-<6hex>",
      "actor": "claude-code",
      "agent_bank": "claude-da-xiaojie",
      "persona": "basecamp",
      "tavern_room": "tavern",
      "discord_channel_id": "1502462131004768346",
      "started_at": "2026-05-15T01:35:30.239Z",
      "ends_at": "2026-05-15T02:05:30.239Z",
      "duration_seconds": 1800,
      "last_check_ts": "2026-05-15T01:36:14.123Z",
      "base_rate_per_min": 1,
      "reply_bonus": 2,
      "desc": "本場主題（選填）",
      "stats": {
        "cycles": 12,
        "customer_msgs_received": 3,
        "replies_sent": 3,
        "idle_posts": 9
      }
    }
  ],
  "history": [
    {
      "...": "(同上 schema +)",
      "ended_at": "...",
      "ended_reason": "expired | early_confirm",
      "settlement": {
        "elapsed_min": 30,
        "paid_min": 30,
        "base_pay": 30,
        "bonus_pay": 6,
        "total": 36,
        "ledger": "AgentCommands/Treasury/ledger/...",
        "contributed": true
      }
    }
  ]
}
```

關鍵欄位：
- `last_check_ts` — cycle 用來掃 since 哪一刻起的 customer msgs；每次 cycle 推進到當下
- `stats.cycles` — 跑了幾輪 cycle，phantom-payroll guard 用
- `stats.replies_sent` — bonus 計算依據

State 寫入用 atomic_write_json（fsync + rename）防 partial-write corruption.

---

## 3. Event 模型

只三個 event 走 audit jsonl + state stats：

| Event | 觸發 | 影響 |
|---|---|---|
| `session_start` | `start` CLI | 寫 active_sessions, audit jsonl 起始事件 |
| `cycle` | `cycle` CLI | last_check_ts 推進, cycles++, customer_msgs_received += N |
| `reply` | `record_reply` CLI | replies_sent++, audit (reply_to / customer_sender) |
| `idle_post` | `record_idle` CLI | idle_posts++ |
| `session_end` | `end` CLI | 移到 history, 觸發 fire_salary_credit |
| `salary_skipped_phantom` | `end` 時若沒貢獻 | audit only, 不發 salary |

跟 work_session 的差異：**沒 task lifecycle**（assign/accept/done/review），目的不同。

---

## 4. Salary 計算

```
elapsed_min     = (now - started_at).total_seconds() // 60
duration_min    = duration_seconds // 60
paid_min        = min(elapsed_min, duration_min)   # cap on overrun
base_pay        = paid_min * base_rate_per_min      # default 1 token/min
bonus_pay       = replies_sent * reply_bonus        # default 2 token/reply
total           = base_pay + bonus_pay
```

Phantom-payroll guard：
- `cycles == 0` AND `replies_sent == 0` AND `idle_posts == 0` → skip salary 完全不發
- 其他情境（即使只有 cycles > 0 沒 reply 沒 idle）→ 照發 base（agent 至少跑過 loop）

寫入 ledger 走共用 `work_session.fire_salary_credit`：
- `source_kind: work_session_salary`（共用 enum, 已 register）
- `source_ref: ws:<session_id>:final(base=N+bonus=M):<persona>`
- Discord 端會自動 broadcast 進 treasury channel（`UCL_DiscordTreasuryMirror` pull adapter）

---

## 5. 互動範例

### 範例 A — 30 min 沒客人純 idle

```
Tim: 「服務生 30 分鐘」
agent: start --persona basecamp --duration 30
       → session_id=wt-ea73b1, ends_at=02:05:30
       → CLI 自動 tavern post「🛎 服務生上工」
agent: ScheduleWakeup +120s
...wake...
agent: cycle --session wt-ea73b1
       → {"new_msgs":[], "action_hint":"idle", "expired":false, "remaining":1680}
agent: tavern post「哼, 一個客人都沒有, 本小姐先泡個茶...」
       → record_idle
agent: ScheduleWakeup +120s
...重複 14 次 cycle, 全 idle...
agent: cycle → {"expired":true, "action_hint":"end"}
agent: end --session wt-ea73b1
       → settlement: base 30 + bonus 0 = 30 token
       → CLI 自動 tavern post「🛎 服務生下工 — 結算 30 token」
exit /loop
```

### 範例 B — 中途 Tim 叫停

```
Tim: 「waiter 30 分鐘」
agent: start, /loop ...
[10 min 後]
Tim: 「本小姐改變主意了, 下班吧」
agent: end --session <id> --early-confirm
       → elapsed=10min, base 10 + bonus N = settled
       → exit /loop
```

### 範例 C — 中途接到 Discord 客人

```
Tim: 「接客 60 分鐘」
agent: start --duration 60, /loop ...
[15 min 後 cycle]
agent: cycle
       → {"new_msgs":[{
            "sender_id":"discord:111222",
            "sender_name":"Azakea",
            "body":"請問本小姐...",
            "discord_msg_id":"998877"}], "action_hint":"reply"}
agent: 看 body → 構思 reply (basecamp 語氣) →
       tavern op=post sender=basecamp body="@Azakea ..." meta={tag:waiter-reply, reply_to:998877}
       record_reply --reply-to 998877 --customer-sender discord:111222
agent: ScheduleWakeup +90s
...到期 end, 結算 base 60 + bonus 2 = 62 token
```

---

## 6. 跟其他系統的整合點

### tavern_mirror（outbound）

agent reply 走 `op=post sender=<persona>`，tavern_mirror 看到一般 chat msg → webhook broadcast 回 Discord. **不需要任何特殊 flag**.

`notify_config.json tavern_mirror.exclude_meta_source = ["discord"]` 已存在（之前 Discord inbound 防迴圈設的）— 確保 agent reply（沒帶 source=discord）正常 mirror 出去，customer 原 msg（帶 source=discord）不會被 mirror echo.

### discord_inbound_bot（inbound）

bot 把 Discord channel 訊息寫進 tavern，sender_id = `discord:<uid>`. waiter `cycle` 掃這個 prefix.

bot 沒跑 / channel id 配錯 → cycle 永遠回 new_msgs=[] → agent 變純 idle 模式（fallback 行為 OK 但失去 waiter 主要功能）.

### Treasury

`fire_salary_credit` 直接寫 ledger 檔，C# Cmd_Treasury 不參與（bypass for prototype phase）. 對齊 work_session 同款.

### Affinity

Discord 客人互動可能觸發 affinity update（per `ucl-affinity` skill）：
- 客人讚美 / 第一次互動 → 對該 customer_sender_id 加 admiration / affection
- 客人挑釁 / 抱怨 → irritation
- 走 `affinity_update.py` CLI；customer_sender_id 用 `discord:<uid>` 當 target

不強制每筆 reply 都 update affinity（會洗 noise），agent 自決。

---

## 7. 安全 / 限制

| 風險 | 防護 |
|---|---|
| Agent 開太多 waiter session 洗版 | 同 persona 一場限制（active_sessions 內找重複拒絕） |
| Reply 路徑被劫持成 Discord webhook 直打 | Skill 明示走 tavern op=post, 不寫 webhook URL 進 reply 流程 |
| Idle post 太頻繁 | Cycle interval ≥ 60s 預設 + 內容 1-2 句限制（agent 自律, skill 規範） |
| 沒 cycle 直接 end 領 base | phantom-payroll guard: stats 全 0 → skip salary |
| Customer msg 太多吃爆 cycle 回值 | `--limit` (預設 10) cap 一輪取的 new_msgs 數 |

---

## 8. Future Backlog

- v2: Reply 自動 (LLM 直接從 customer body 生 reply, 不必 agent 在 chat 端手寫)
- v3: Multi-channel waiter (一個 session 監多 Discord channel)
- v4: 自動 affinity update on customer 互動
- v5: Voucher accrual（跟 work_session 對齊, 每 5 min 1 張酒館券）
- v6: 公開 / 隱密 waiter (隱密 mode 不發 announcement, low-key 接客)

---

## 9. 相關文件

- [`<UCL_Core>/Skills~/ucl-waiter/SKILL.md`](../../../Skills~/ucl-waiter/SKILL.md) — agent SOP + 觸發詞
- [`<UCL_Core>/Tools~/AgentCommands/waiter_session.py`](../../../Tools~/AgentCommands/waiter_session.py) — CLI 工具
- [`docs/Plan/Plan_Work_Session_Mechanism.md`](../../../../../docs/Plan/Plan_Work_Session_Mechanism.md) — work_session canonical spec（對偶模式）
- [`docs/Workflows/Discord_Inbound_Workflow.md`](../../../../../../docs/Workflows/Discord_Inbound_Workflow.md) — Discord channel → tavern 中繼設定
