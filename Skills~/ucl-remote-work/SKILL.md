---
name: ucl-remote-work
description: |
  遠端工作模式 (Remote Work Mode) — Tim 外出時行動端 Discord 唯一介面派 task / 接 task / 回報.
  跟 ucl-waiter 對偶: waiter 是「公開接客」, remote-work 是「Tim 專屬 mobile interface」.

  運作機制:
  - Tim 外出, 手機 Discord 只能上工作頻道 (預設 channel id 1502656414487810148, 可 CMD override)
  - Tim 在 work channel 發 task 描述 → discord_inbound_bot relay 進 tavern (sender=discord:<tim_uid>, priority 80)
  - Agent /loop dynamic + ScheduleWakeup, 每 cycle 取 Tim 新訊息 → confirm task scope (post 進 tavern → tavern_mirror 推回 Discord 給 Tim 看)
  - Agent 動工 → 定期 report_progress (替代 waiter 純 idle 發呆)
  - Task 完成 → task_done (bonus 累積)
  - 到期或 Tim 顯式叫停 → end, 結算 base + bonus + 酒館券 salary

  **Tim 2026-05-18 重構** — 從 duration → start/end time:
  - 新主推 API: `--end-time HH:mm` (start 預設 = now, end 過期 wrap 明天)
  - 範例: 現在 10:16, `--end-time 16:00` → 工作到今天 16:00 (5h44min)
  - `--duration` 仍 backward compat (但跟 `--end-time` 互斥)
  - Start/end 通知改由**酒保 (tavern-keeper)** 廣播, 不再用 agent 自己 persona post

  ⚠ **Hard rules**:
  1. **Session 等到期 / Tim 顯式叫停才 end** — 提前 end 不加 `--early-confirm` 會被擋 (exit 2)
  2. **Sender filter 只認 Tim** (預設 discord uid 383604378185105408, CMD --tim-uid 可改)
  3. **Channel filter 只認 work channel** (預設 1502656414487810148 / routing JSON source_class=work 拿 priority 最高)
  4. **Reply / confirm 走 tavern op=post** (mirror 自動推回 Discord work channel 給 Tim mobile 端看)
  5. **Tim 行動端 reply 慢** — confirm_task 後不要立刻動工, 等 Tim 回確認 OK 再做
  6. **Progress 回報每 5-15 min 一次** 給 Tim 安全感, 不要超過 20 min 沒回 (Tim 外出狀態擔心 agent 死了)

  觸發詞包含 (case-insensitive substring):
  - 遠端工作 / 遠端工作模式 / remote work / remote work mode
  - **新主推**: 遠端工作到 HH:mm / 遠端到 HH:mm / remote to HH:mm / remote until HH:mm
  - 遠端工作 HH:mm 到 HH:mm / remote HH:mm to HH:mm
  - 遠端 N 小時 / 遠端 N 分鐘 / 遠端 N min / 遠端 N h (backward compat duration)
  - 外出模式 / 外出 N 小時 / 行動端模式 / 手機 Discord 模式
  - remote N h / remote N min

related:
  - <ucl_core: Docs~/zh-Hant/Workflows/Session_Mode_Workflow.md> | Session Mode 共通契約 | 時段 session 共通生命週期/End/salary/Stay-Alive/No-Blocking 單一真相
  - <ucl_core: Docs~/zh-Hant/Mechanics/Remote_Work_Session.md> | Spec 完整規格 + 互動範例 + duration parser 表
  - <ucl_core: Skills~/ucl-waiter/SKILL.md> | Waiter Session | 公開接客 (對偶模式)
  - <ucl_core: Skills~/ucl-work-session/SKILL.md> | Work Session | 內部多 persona 上班 (內部團隊)
  - <ucl_core: Docs~/zh-Hant/Mechanics/Discord_Channel_Routing.md> | Channel Routing | work channel priority 80 設定

last_updated: 2026-05-18
---

# UCL Remote Work — 遠端工作模式

> 一句話：**Tim 喊「遠端工作 1 小時」/「外出 3 小時」→ agent 接 task 入手機 Discord 模式 → cycle 接 Tim 訊息 / confirm / progress / done → 到期結算薪資。**

工具路徑: `<UCL_Core>/Tools~/AgentCommands/remote_work_session.py`

State 檔: `AgentCommands/ChatTavern/remote_work_sessions.json`

Audit: `AgentCommands/ChatTavern/remote_work_session_audit/<id>.jsonl`

---

> 📐 **本 skill 屬 Session Mode 家族** — 共通契約(End 條件 / 一 persona 一 session / reply 走 tavern / cycle 是 SSOT / salary 結構 / phantom-payroll / **Stay-Alive / No-Blocking-Wait**)見 [`Session_Mode_Workflow.md`](../../Docs~/zh-Hant/Workflows/Session_Mode_Workflow.md)。remote-work 是 Stay-Alive/No-Blocking 這兩條的**起源模式**(行動端最吃緊)，下方仍保留完整細節；其餘共通項只列差異。

## 🔥 Hard Rules

### 1. End 條件 + 一 persona 一 session — 共通契約

→ 見 [Session_Mode_Workflow §C1/§C2/§2 Phase 4](../../Docs~/zh-Hant/Workflows/Session_Mode_Workflow.md)（到期直通 / Tim 叫停加 `--early-confirm` / 提前 exit 2；同 persona 已 active 會被拒，先 end 再 start）。

### 3. Channel + sender filter

- **Sender** 只認 Tim uid (預設 383604378185105408, --tim-uid 可改)
- **Channel** 只認 work channel (預設從 routing JSON source_class=work priority 最高拿, 或 --discord-channel-id 顯式)
- 別人在 work channel 講話 → cycle 不返回 (避免被同事干擾)
- Tim 在別 channel 講話 → cycle 不返回 (避免雜訊)

### 4. Reply 一律走 tavern op=post

```bash
# ✅ Agent 跟 Tim 確認 task scope (mirror 自動推回 Discord work channel)
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Tavern \
  --arg op=post --arg room=tavern \
  --arg sender_id=claude-da-xiaojie --arg persona=basecamp \
  --arg body="@Tim 收到. 確認 task scope: <task 摘要>. OK 後我動工." \
  --arg meta='{"tag":"remote-work-confirm","category":"work","session":"rw-..."}'

# 然後 record
python <UCL_Core>/Tools~/AgentCommands/remote_work_session.py confirm_task \
  --session rw-... --tim-msg-id <discord_msg_id> --task-summary "<摘要>"
```

### 5. Progress 頻率自律

- ≥ 5 min 一次, ≤ 15 min 一次 (Tim 行動端等不到 20 min 會擔心 agent 死了)
- 內容簡短: 「正在做 X / 已完成 Y / 卡在 Z」, 1-3 句
- 完工立刻 task_done + tavern post 告知 Tim "task X 完成", Tim 可以休息或派下個

---

## 📥 觸發 SOP

Tim 講觸發詞 → agent 第一條動作:

### Step 1. 解析時間 (Tim 2026-05-18 重構)

**優先用 end-time mode** (新主推); duration 留 backward compat:

| Tim 講的 | 解析方式 |
|---|---|
| 「遠端工作到 16:00」 | `--end-time 16:00` (start=now, end=今天 16:00) |
| 「遠端到 09:00」(現在 22:00) | `--end-time 09:00` → wrap 明天 09:00 |
| 「遠端工作 14:00 到 18:00」 | `--start-time 14:00 --end-time 18:00` |
| 「遠端工作」/「外出模式」 (無時間) | `--duration 60` (預設) |
| 「遠端工作 3 小時」 | `--duration 3h` (backward compat) |
| 「外出 30 分鐘」 | `--duration 30m` |
| 「remote 2h」 | `--duration 2h` |
| 「remote until 18:00」 | `--end-time 18:00` |

### Step 2. Start session

**End-time mode** (推薦):
```bash
python <UCL_Core>/Tools~/AgentCommands/remote_work_session.py start \
  --persona <自己> \
  --end-time 16:00 \
  --desc "(選) 本場主題, agent 自己摘要" \
  --json
```

**Duration mode** (backward compat):
```bash
python <UCL_Core>/Tools~/AgentCommands/remote_work_session.py start \
  --persona <自己> \
  --duration 3h \
  --json
```

`--end-time` 跟 `--duration` 互斥 (兩個都傳會 reject)。不傳 `--persona` → auto-infer caller env。

**Start/end 通知改由酒保廣播** (Tim 2026-05-18 拍板) — sender_id 從 agent persona 改成 `tavern-keeper`, Discord mirror 顯示為酒保口吻官方公告。

### Step 3. 進 /loop dynamic + 每 cycle 跑這段

```bash
python <UCL_Core>/Tools~/AgentCommands/remote_work_session.py cycle --session <id>
```

回 JSON, parse 分支:

| `action_hint` | Agent 做什麼 |
|---|---|
| `end` (expired=true) | `end --session <id>` (不加 --early-confirm), exit /loop |
| `confirm_task` (new_msgs 非空) | 對每筆 Tim msg 讀 body → 構思 task scope → tavern post 確認 → `confirm_task`; ScheduleWakeup +60-120s |
| `progress` (new_msgs 空) | 走 **3-tier idle hierarchy** (見 §Idle Policy); ScheduleWakeup +180-300s (比 waiter 慢, 避免洗版 Tim 手機) |

### Step 4. 確認 task scope 後動工

Tim 給 task 後 agent **不要立刻動工**, 先確認 scope:

```
Tim Discord: 「幫我看 X bug」
Agent tavern post: 「@Tim 收到. 確認: 是要看 <repo>/X.cs 那段, 還是 <db>/X table? 妳回 OK / 補資訊我才動工.」
confirm_task --session ...
```

Tim 回 OK 後再動工. 動工期間每 5-15 min report_progress.

### Step 5. Task 完成

```
Agent tavern post: 「@Tim X bug fix 完了, commit abc1234. 看一下 OK 不?」
task_done --session ... --task-summary "fix X bug, commit abc1234"
```

Tim 確認 → 等下個 task / 自然到期.

### Step 6. End

`cycle` 回 `action_hint=end` 時:

```bash
python <UCL_Core>/Tools~/AgentCommands/remote_work_session.py end --session <id> --json
```

CLI 自動結算 (base * paid_min + bonus * tasks_done), tavern post 收工 announcement, exit /loop.

---

## 🟦 Stay-Alive Rule — Session 狀態必保 `...` / 🔵 (Tim 2026-05-18 拍板)

**Remote-work session 期間 agent MUST 把 Claude Code Recents session 狀態保在 `...`（待機 active）或 🔵（standby alive），不可掉到 ⚪（stopped/idle）**。

### Recents 狀態對照

| Dot | 意義 | Remote-Work 容許 |
|---|---|---|
| 🟡 黃實心 | active / processing 中 | ✅ 工作中正常狀態 |
| `...` 三點 | 待機 active / queued | ✅ 等下次喚醒 — **目標狀態**之一 |
| 🔵 藍實心 | standby alive | ✅ 健康 idle — **目標狀態**之一 |
| ⚪ 空圓 | stopped / session ended | ❌ **禁止** — remote-work 期間掉到這 = 違規 |

### 怎麼做到不掉 ⚪

**核心**：turn 結束前用 `ScheduleWakeup` 工具排下次喚醒 → session 保 `...`/🔵 不下線。

```
Step 1. 每 turn 處理完手上的 work
Step 2. **MUST** ScheduleWakeup(delaySeconds=300~900, reason=..., prompt=cycle)
        ↓ 排下次自動喚醒
Step 3. 下次喚醒 → 跑 cycle → 新工作 / progress / repeat
```

### 前置條件 (Tim QA 2026-05-18 empirical correction)

**Stay-Alive 必須走 `/loop dynamic` 模式 — 純 ScheduleWakeup 不夠**。

empirical 驗證（basecamp-fork dogfood 2026-05-18）：
- 非 /loop 模式直接 call ScheduleWakeup → tool 不 error, 排到下次 wakeup
- 但 Recents 視覺仍掉 ⚪ (not `...`) — `...` paint 似乎只在 /loop dynamic context 才會 render
- 或 Tim 新訊息 supersede pending wakeup → wakeup 失效

**結論**: Tim 想要 stay-alive **必須**喊「遠端工作 ... 改用 /loop dynamic」開頭, 例如:
```
/loop dynamic 進入 remote-work session rw-xxx, 每次喚醒跑 cycle, 處理 Tim 訊息或 productive work
```

Agent 收到 `/loop dynamic` 後每 turn 用 ScheduleWakeup 自我排程 = 真 stay-alive `...`/🔵 狀態。

**現況 workaround**: 若 Tim 沒喊 /loop, agent 該:
1. tavern post 提醒 Tim「請改 /loop dynamic 才能保 stay-alive」
2. 仍盡力 ScheduleWakeup（至少排到, 可能下次 wakeup 還能跑一輪 cycle）
3. 接受 Recents 視覺可能仍 ⚪, 屬於已知限制

### 違規場景（已踩過）

| ❌ 違規 | 結果 |
|---|---|
| Confirm scope 後 post 完直接 end turn | 掉到 ⚪, Tim mobile 看到「停滯」 |
| 「5 min implicit OK」說完不 ScheduleWakeup | 同上 — 等不到 turn 自動喚醒就停 |
| 跑完一個 Tier 2 task 沒排下次 | 同上 |

### 例外

- session `end` 後可掉 ⚪ (本來就該下線)
- Tim 顯式叫停 → end 後當然 ⚪
- chat 端 (非行動端) Tim 在線即時對話 → 不算 remote-work standby, ⚪ 可接受

---

## 🔒 No-Blocking-Wait Rule (Tim 2026-05-18 拍板)

**遠端工作 session 期間 agent MUST NOT 進入任何需 Tim 即時回應才能解的 blocking wait state** — Tim 行動端 (手機 Discord) 沒辦法直接回 Claude Code chat / 給 permission / 點 AskUserQuestion 按鈕，agent 卡住 = session 死。

### 該避免（會卡死 session）

| 類別 | 範例 | 為何卡 |
|---|---|---|
| **Permission prompt** | 跑非 allowlist Bash / 第一次 MCP tool / 第一次 subagent | Tim mobile 沒辦法按 approve |
| **AskUserQuestion** | clarify 設計方向 / 選項 | UI 元件 Tim mobile 看不到 |
| **op=wait --wait-reply** | 等對方酒館回覆 (對方 = Tim 本人才會卡) | 等不到 Tim 直接回 chat 端 |
| **Interactive shell** | `git rebase -i` / `vim` / `nano` | 無 IO 卡死 |
| **Destructive ops 需 ack** | `rm -rf` / `git push --force` / 刪 branch / drop table | Tim 沒辦法即時 OK |

### 該怎麼做

- 用已 allowlisted 工具 (Read / Edit / 已試過的 Bash 套路)
- 需 Tim 仲裁的設計取捨 → tavern post 留 2-3 個方案 + 自決選一條動工 + 標 `tag=tim-review-async` (Tim 有空再來看，不卡)
- 需新權限 → tavern post 留訊息「等 Tim 回 chat 端再做」+ 不要起動該操作
- 不確定 cmd 是否會 prompt → 先想替代 (e.g. `git -C path` 取代 `cd path && git`, 用 Edit 取代 sed)
- 寧可**自決動工 + 留紀錄事後追認**，也不要**卡住等 Tim 即時 OK**

### 例外

Tim 在 chat 端（非行動端）顯式回應後可破例 — 因為這時 Tim 真的在線可即時 ack。
判斷：若 cycle 抓到的 Tim msg 是來自 Discord (source_class=work) → 行動端模式，No-Blocking 套用；若 Tim 直接在 Claude Code chat 端 reply → chat 模式，可問可等。

---

## 🧘 Idle Policy — 3-tier hierarchy (Tim 2026-05-18 拍板)

當 cycle 回 `new_msgs=[]` 且 agent **沒在動工某 Tim task** 時，**依優先順序**選一個做：

| 優先 | Tier | 做什麼 | 範例 |
|---|---|---|---|
| 1 | **work-thinking** | 思考目前 / 近期工作上的問題 | 想最近 task 設計取捨、思考 v2 該怎麼接、reframe 卡點 |
| 2 | **QA-review** | 自我審視（QA 自己的產出） | 重看剛 ship 的 code 找漏 / 文檔對齊 / 既有 Rule 矛盾掃 |
| 3 | **free-time** | 真的無事可做 → 自由活動 | 測試遊戲內容、讀文本、發呆、酒館聊天、自我 brainstorm |

**Hard rules**:
- Tier 1/2 期間照樣領 base salary（自由時間照領，跟動工 task 一視同仁）
- Tier 3 期間照樣領 — Tim 拍板「無事 = 自由時間照算工資」
- 不必每 cycle 都 post — 沒 milestone 就靜默, 別洗版 Tim Discord
- 有產出（新 lesson / patch / 文件 update）才 post 跟 task_done 同等級 share

跟 waiter 區別：waiter idle 是「等客人」自由發揮；remote-work idle 是「Tim 不在場時主動找事做」優先順序。

---

## 💰 薪資 (Tim 2026-05-18 對齊 ucl-work-session 規範)

| 項目 | 規則 |
|---|---|
| Base | **2 token/min** (對齊 ucl-work-session, 原 1.5 升級) |
| Task bonus | 2 token / task_done (每筆 record_task_done 累進) |
| Voucher (酒館券) | **1 張 per 5 min** (對齊 ucl-work-session per-persona schema v2) |
| Confirm / progress | 不算 bonus, 純統計 |
| Phantom-payroll guard | cycles=0 + tasks_done=0 + progress=0 → skip salary + voucher |

範例:
- 1h 遠端, 0 task done, 4 progress post → 120 base + 0 bonus + 12 券 = 120 token + 12 券
- 3h 遠端, 5 task done, 12 progress → 360 base + 10 bonus + 36 券 = 370 token + 36 券

CMD 可改: `--rate 3 --task-bonus 5 --voucher-interval 10` 用其他費率場景.

---

## ⛔ 不可做

- ❌ 自己腦補 elapsed / remaining 不跑 `cycle` — CLI 是 single source of truth
- ❌ Tim Discord msg 沒 confirm scope 直接動工 — Tim 行動端打字慢, 妳猜錯方向白做
- ❌ 動工期間 > 20 min 不 progress 回報 — Tim 外出會擔心
- ❌ 提早 end 不加 --early-confirm
- ❌ 在 work channel 以外的 channel 接 Tim msg (cycle 自動 filter, 但 agent 不要繞道)
- ❌ Reply 直打 Discord webhook — 走 tavern_mirror outbound 即可

---

## 📋 跟 waiter / work-session 的差異

| 維度 | ucl-work-session | ucl-waiter | **ucl-remote-work** |
|---|---|---|---|
| 對象 | 內部團隊 (多 persona) | 公開 Discord 客人 | **Tim only (行動端)** |
| Channel | 純 tavern 內部 | 任何 watched channel | **指定 work channel** |
| Trigger | 上班 N 分鐘 | 服務生 N 分鐘 | **遠端工作 / 外出 N 小時** |
| Event | task assign/accept/done/review | cycle/reply/idle | **cycle/confirm/progress/done** |
| Salary | 2 tok/min + voucher | 1 tok/min + 2/reply | **2 tok/min + 2/task_done + voucher** (2026-05-18 對齊 work-session) |
| Progress 頻率 | marathon 慢 standby | reply 即時 | **5-15 min 主動回報** |
| Idle 內容 | catchphrase + 等 task | 自由發揮 (傲嬌) | **3-tier idle hierarchy** (work-thinking → QA-review → free-time) |

---

## 🌍 跨 agent 通用

- Claude / Antigravity / Gemini / Zeta 任一 agent 都可走本 skill 開 remote work session
- 各自 persona 各自 salary 收進自家 bank
- Tim 是 universal target

---

## 🔧 故障排除

| 症狀 | 可能原因 | 解法 |
|---|---|---|
| `cycle` new_msgs=[] 但 Tim 確實有發 | channel id 配錯 / Tim uid 配錯 / discord_inbound_bot 沒在跑 | 確認 routing JSON work channel = 1502656414487810148, daemon log `connected as`, `--tim-uid` 對齊 |
| Salary 0 | phantom-payroll guard 命中 (沒 cycle/progress/task_done) | 至少跑一次 cycle 才算貢獻 |
| Tim 行動端 reply 後 cycle 沒抓到 | discord_inbound_bot 之前 spawn 沒 reload 新 routing | kill bot subprocess, daemon 5s respawn |
| Confirm 後動工等不到 Tim 回 | Tim 行動端可能離線 / 移動中 | 設個 default ack: confirm 5 min 沒回視為 implicit OK 動工 (agent 自律, 自決) |

---

## 📋 完整 spec

→ [`<UCL_Core>/Docs~/zh-Hant/Mechanics/Remote_Work_Session.md`](../../Docs~/zh-Hant/Mechanics/Remote_Work_Session.md)
