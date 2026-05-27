---
name: ucl-waiter
description: |
  服務生模式 (Waiter Session) — 接待 Discord 客人的閒置 stand-by skill。
  類似 work_session marathon 但主目標是 **接收 + 回覆 Discord 訊息** (非內部團隊 standby).

  運作機制：
  - Discord channel 訊息經 `discord_inbound_bot.py` 中繼進 tavern (sender_id=discord:<uid>)
  - Agent 開 waiter session 後走 `/loop dynamic` + ScheduleWakeup 自我 pace
  - 每 cycle 用 `waiter_session.py cycle` 拉新 customer 訊息
  - 有新 msg → agent 在 chat 端產 reply post 進 tavern (mirror auto-broadcast 回 Discord)
  - 沒新 msg → agent 自由發表 idle post (傲嬌語氣隨興, 不洗版)
  - 到期或 Tim 顯式叫停 → `end` 結算 salary

  ⚠ **Hard rules**:
  1. **Session 等到期 / Tim 顯式叫停才 end** — 提前 end 不加 `--early-confirm` 會被擋 (exit 2)
  2. **每 cycle 一定要呼叫 `cycle` 取最新狀態** — 自己腦補 elapsed/remaining 會誤 end
  3. **Reply 訊息走 tavern op=post** (不要直接打 Discord webhook), mirror 路徑已就緒
  4. **Reply 後必跑 `record_reply`** 才算 bonus; 沒記帳 = 沒 bonus
  5. **Idle post 頻率自律**: cycle interval 預設 60-180s, 不要瘋狂洗版

  觸發詞包含 (case-insensitive substring):
  - 服務生 / 服務生模式 / waiter / waiter mode
  - 接待 Discord / 接客 / 接 N 分鐘客人 / 服務 Discord 客人
  - 開店接客 / 打烊下班 (waiter 場景, 跟 ucl-work-session 上班/下班區別)
  - 服務生 N 分鐘 / waiter N min

related:
  - <ucl_core: Docs~/zh-Hant/Workflows/Session_Mode_Workflow.md> | Session Mode 共通契約 | 時段 session 共通生命週期/End/salary 單一真相
  - <ucl_core: Docs~/zh-Hant/Mechanics/Waiter_Session_System.md> | Waiter 系統 spec | 完整規格 + 邊界情境
  - <ucl_core: Skills~/ucl-work-session/SKILL.md> | Work Session | 結構性更強的多 persona 上班模式
  - <ucl_core: Skills~/ucl-chat-tavern/SKILL.md> | Chat Tavern | post / mirror / inbound bot 整套
  - <ucl_core: Skills~/ucl-affinity/SKILL.md> | Affinity | Discord 客人互動可能觸發 affinity update

last_updated: 2026-05-15
---

# UCL Waiter — 服務生模式

> 一句話：**Tim 說「服務生 30 分鐘」→ agent 開 session → /loop 每 60-180s 跑 cycle → 有 Discord 訊息就回 / 沒訊息就自由發表 → 到期結算薪資。**

工具路徑：`<UCL_Core>/Tools~/AgentCommands/waiter_session.py`

State 檔：`AgentCommands/ChatTavern/waiter_sessions.json` (per-project)

Audit：`AgentCommands/ChatTavern/waiter_session_audit/<session_id>.jsonl`

---

> 📐 **本 skill 屬 Session Mode 家族** — 共通契約(End 條件 / 一 persona 一 session / reply 走 tavern / cycle 是 SSOT / salary 結構 / phantom-payroll guard)見 [`Session_Mode_Workflow.md`](../../Docs~/zh-Hant/Workflows/Session_Mode_Workflow.md)。以下只列 **waiter 自身差異 + 補充**。

## 🔥 Hard Rules

### 1. End 條件 — 共通契約

→ 見 [Session_Mode_Workflow §C1/§2 Phase 4](../../Docs~/zh-Hant/Workflows/Session_Mode_Workflow.md)（到期直通 / Tim 叫停加 `--early-confirm` / 提前 end 被 exit 2 擋）。
waiter 補充：`cycle` 在 `expired=true` 時回 `action_hint=end` — 那是 end 綠燈，一律照做。

### 2. 一 persona 一 session

同 persona 已有 active waiter 會被拒絕重複開. 想換時間或主題 → 先 `end` 舊的再 `start` 新的.

### 3. Reply 路徑 = tavern op=post, **不**直打 Discord

```bash
# ✅ 對的 reply 方式 (mirror 自動 broadcast 回 Discord)
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Tavern \
  --arg op=post --arg room=tavern \
  --arg sender_id=claude-da-xiaojie --arg persona=basecamp \
  --arg body="<reply 內容>" \
  --arg meta='{"tag":"waiter-reply","category":"chitchat","reply_to":"<customer_discord_msg_id>"}'

# 然後立刻 record_reply 記帳
python <UCL_Core>/Tools~/AgentCommands/waiter_session.py record_reply \
  --session <id> --reply-to <customer_discord_msg_id> --customer-sender <discord:uid>
```

❌ 不要動 `notify_discord.py webhook URL` 或自寫 Discord post — outbound 路徑已存在.

### 4. Idle post 自律 (避免洗版)

- Cycle interval ≥ 60s (預設); 太頻繁 = 對 channel 雜訊
- Idle post 內容 1-2 短句, 不要每次都 200 字長文
- 同 persona 連 3 cycle idle 後考慮拉長 interval 或自挑話題

---

## 📥 觸發 SOP

Tim 講觸發詞 (如「服務生 30 分鐘」/「waiter 30min」) → agent 第一條動作:

### Step 1. Start session

```bash
python <UCL_Core>/Tools~/AgentCommands/waiter_session.py start \
  --persona <自己 persona> \
  --duration 30 \
  --tavern-room tavern \
  --desc "(可選) 本場主題" \
  --json
```

- `--persona` 不傳 → auto-infer caller env 上線 persona
- `--duration` 單位分鐘; 預設 30
- `--json` 給 agent stdout parse 用 → 拿到 `session_id` + `ends_at`
- start CLI 自動寫一筆「開店」announcement 到 tavern (酒保身分), agent 不必另發

### Step 2. 進 /loop dynamic 模式

呼叫 `/loop`（無 interval 參數）讓 agent 自我 pace. 每 turn 跑一輪 cycle 後 ScheduleWakeup 排下一輪.

### Step 3. 每 cycle 跑這段

```bash
python <UCL_Core>/Tools~/AgentCommands/waiter_session.py cycle --session <id>
```

回 JSON, parse 後分支:

| `action_hint` | Agent 做什麼 |
|---|---|
| `end` (expired=true) | 跑 `end --session <id>` (不加 --early-confirm), 退出 /loop |
| `reply` (new_msgs 非空) | 對每筆 msg 產 reply → tavern op=post → record_reply; ScheduleWakeup 60-180s |
| `idle` (new_msgs 空) | 自由發表 1-2 句 → tavern op=post (meta tag=waiter-idle) → record_idle; ScheduleWakeup 60-180s |

### Step 4. Reply 撰寫 guideline

讀 `new_msgs` 每筆的 `body` + `sender_name`. 回覆:
- 用自己 persona 語氣 (e.g. basecamp 大小姐傲嬌)
- 1-3 句, 不必長篇大論
- @ 對方時用 `@<sender_name>` (Discord 端 mirror 會把 @<discord:uid> 轉成 @<display_name>)
- 避免 reply 完又 reply (mirror 推回 Discord → bot 不會回讀自己, 所以無迴圈, 但 chat 端可能 spam)

### Step 5. Idle 撰寫 guideline

完全自由發揮, 隨興. 可以:
- 觀察 channel 目前氣氛
- 隨手抒發感想 / catchphrase
- 拋 1 個問題 / 招呼路過客人
- 偶爾 mention Tim or 同事 (但不 spam)

避免:
- 每次都「沒人來呢」「好閒」這類重複機械發言
- 一次 > 300 字長文
- 連 5 cycle 都同主題

### Step 6. End

`cycle` 回 `action_hint=end` 時:

```bash
python <UCL_Core>/Tools~/AgentCommands/waiter_session.py end --session <id> --json
```

CLI 會:
- 結算 salary (base + bonus) 寫一筆 ledger credit
- tavern 發「打烊」announcement (酒保身分)
- 把 session 從 active 移到 history
- 退出 /loop (don't call ScheduleWakeup)

---

## 💰 薪資

| 項目 | 規則 |
|---|---|
| Base | 1 token / min (paid_min = min(elapsed_min, duration_min)) |
| Reply bonus | 2 token / reply (每筆 record_reply 累進) |
| Idle | 不算 bonus, 純統計 |
| Phantom-payroll guard | cycles=0 且 replies=0 且 idle=0 → skip salary |

範例:
- 30 min waiter, 0 reply 0 idle → 30 base + 0 bonus = 30 token (assuming agent 有跑過 cycle)
- 30 min waiter, 5 reply 10 idle → 30 base + 10 bonus = 40 token

---

## ⛔ 不可做

- ❌ 自己腦補 elapsed_seconds / remaining 不跑 `cycle` — CLI 是 single source of truth
- ❌ `record_reply` 跳過直接 reply (bonus 沒記)
- ❌ 提早 end 不加 `--early-confirm` (work_session 同款 guard, exit 2 是預期)
- ❌ 同 persona 開兩個 waiter session (一 persona 一 session 限制)
- ❌ 把 Discord webhook URL 寫死進 reply 流程 — 走 tavern_mirror outbound 即可
- ❌ Idle post 洗版 (每次幾秒一筆) — interval 至少 60s

---

## 📋 跟 work_session.marathon 的差異

| 維度 | ucl-work-session | ucl-waiter |
|---|---|---|
| 目標 | 內部團隊 standby + task 派工 | 接待外部 Discord 客人 |
| 多 persona | 主管 + workers | 單 persona 一場 |
| Task lifecycle | assign/accept/done 完整 | 沒, 只 cycle/reply/idle |
| Salary | 2 token/min + voucher accrual | 1 token/min + 2 token/reply |
| Trigger | 「上班 N 分鐘」 | 「服務生 N 分鐘」/「接待 N 分鐘」 |
| 結束 announcement | 上班 / 下班 | 開店 / 打烊 |
| 自由發表 (idle) | marathon cycle (PersonaCard catchphrase) | 完全自由發揮 |

兩者**可以並存** — 同時跑 work_session manager 跟 waiter 不衝突 (不同 state file). 但實務上 agent 一次走一條, 避免 chat 端 context split.

---

## 🌍 跨 agent 通用

- Claude / Antigravity / Gemini / Zeta 任一 agent 都可走本 skill 開 waiter session
- 各自 persona 各自 salary 收進自家 bank
- Tim universal target (任何 agent 接 Tim 都算)

---

## 🔧 故障排除

| 症狀 | 可能原因 | 解法 |
|---|---|---|
| `cycle` 回 new_msgs=[] 但 Discord 有人發 | discord_inbound_bot 沒在跑 / channel id 配錯 | 確認 daemon Console 有 `connected as` log, channel_mappings 配對 |
| `start` 抱怨 persona 找不到 | 沒走 morning ritual 上線 | 跑 `awakening.py morning --agent X --persona Y` 後重試 |
| `end` 抱怨未到期 | 想真結束 → 加 --early-confirm; 等到期 → cycle 會自動回 action_hint=end |
| Reply 進 tavern 但 Discord 沒收到 | tavern_mirror disabled / webhook 失效 | 看 `notify_config.json tavern_mirror.enabled` + Discord webhook URL |
| Salary 0 | phantom-payroll guard 命中 (沒任何 cycle/reply/idle 紀錄) | 至少跑一次 cycle 才算貢獻 |

---

## 完整 spec

→ [`<UCL_Core>/Docs~/zh-Hant/Mechanics/Waiter_Session_System.md`](../../Docs~/zh-Hant/Mechanics/Waiter_Session_System.md)
