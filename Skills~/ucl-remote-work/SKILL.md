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

工具: `<UCL_Core>/Tools~/AgentCommands/remote_work_session.py` ｜ State: `AgentCommands/ChatTavern/remote_work_sessions.json` ｜ Audit: `AgentCommands/ChatTavern/remote_work_session_audit/<id>.jsonl`

## 必讀

- 遠端專屬細節（Stay-Alive / No-Blocking-Wait / 3-tier Idle Policy / 薪資結算 / 故障排除 / 三方比較）→ `ucl_core:Docs~/zh-Hant/Workflows/Remote_Work_Workflow.md`
- **共通契約 SSOT**（End 條件 / 一 persona 一 session / cycle 是 SSOT / salary 結構 / phantom-payroll）→ `ucl_core:Docs~/zh-Hant/Workflows/Session_Mode_Workflow.md`
- 完整 spec（互動範例 + duration parser 表）→ `ucl_core:Docs~/zh-Hant/Mechanics/Remote_Work_Session.md`

> 📐 本 skill 屬 **Session Mode 家族**；remote-work 是 **Stay-Alive / No-Blocking-Wait 的起源模式**（行動端最吃緊），其完整細節在 workflow doc，其餘共通項見 SSOT。

## 🔥 Hard Rules（load-bearing 摘要）

1. **End 條件** — 到期直通 end；Tim 顯式叫停才提前 end 且須加 `--early-confirm`（提前 end 不加會 exit 2）。
2. **一 persona 一 session** — 同 persona 已 active 會被拒，先 end 再 start。
3. **Channel + sender filter** — Sender 只認 Tim uid（預設 `383604378185105408`, `--tim-uid` 可改）；Channel 只認 work channel（預設 `1502656414487810148` / routing JSON source_class=work priority 最高，或 `--discord-channel-id`）。別人 / 別 channel 的訊息 cycle 不返回；agent 也不要繞道。
4. **Reply 一律走 tavern op=post**（mirror 自動推回 Discord work channel），禁止直打 Discord webhook；post 完再 `confirm_task` record。
5. **Stay-Alive** — session 期間狀態必保 `...`/🔵，不可掉 ⚪；每 turn 結束前 `ScheduleWakeup`，且須走 `/loop dynamic`（純 ScheduleWakeup 不夠）。細節見 workflow doc。
6. **No-Blocking-Wait** — 禁止任何需 Tim 即時回應才能解的 blocking wait（permission prompt / AskUserQuestion / op=wait / interactive shell / 需 ack 的 destructive op）。寧可自決動工 + 留紀錄事後追認。
7. **Progress 自律** — ≥5min、≤15min 一次；動工期 > 20 min 不回報 Tim 會擔心。

## 📥 觸發 SOP（compact）

**Step 1. 解析時間** — 優先 end-time mode，duration 留 backward compat：
「遠端到 16:00」→ `--end-time 16:00`（start=now，過午夜 wrap 明天）；「14:00 到 18:00」→ `--start-time 14:00 --end-time 18:00`；「遠端 3 小時 / 30 分鐘」→ `--duration 3h`/`30m`；無時間 → `--duration 60`（預設）。`--end-time` 與 `--duration` 互斥。

**Step 2. Start** — `remote_work_session.py start --persona <自己> --end-time 16:00 [--desc "..."] --json`（不傳 `--persona` 則 auto-infer caller env）。Start/end 通知由**酒保 (tavern-keeper)** 廣播，非 agent persona post。

**Step 3. 進 `/loop dynamic`，每 cycle 跑** `remote_work_session.py cycle --session <id>` → parse `action_hint`：
- `end`（expired）→ `end --session <id>`（不加 `--early-confirm`），exit /loop
- `confirm_task`（new_msgs 非空）→ 讀 body → 構思 scope → tavern post 確認 → `confirm_task`；ScheduleWakeup +60-120s
- `progress`（new_msgs 空）→ 走 3-tier idle hierarchy（見 workflow）；ScheduleWakeup +180-300s（比 waiter 慢，避免洗版 Tim 手機）

**Step 4. 確認 scope 後才動工** — Tim 給 task 不要立刻動工，先 tavern post 確認 scope，Tim 回 OK 再做；動工期每 5-15 min `report_progress`。

**Step 5. Task 完成** — tavern post 告知 + `task_done --session ... --task-summary "..."`（bonus 累進），等下個 task / 自然到期。

**Step 6. End** — cycle 回 `action_hint=end` → `end --session <id> --json`，CLI 自動結算 (base×paid_min + bonus×tasks_done)，tavern post 收工，exit /loop。

## ⛔ 不可做

- ❌ 自己腦補 elapsed / remaining 不跑 `cycle` — CLI 是 single source of truth
- ❌ Tim Discord msg 沒 confirm scope 直接動工 — 行動端打字慢，猜錯方向白做
- ❌ 動工期間 > 20 min 不 progress 回報 — Tim 外出會擔心
- ❌ 提早 end 不加 `--early-confirm`
- ❌ 在 work channel 以外的 channel 接 Tim msg（cycle 自動 filter，但別繞道）
- ❌ Reply 直打 Discord webhook — 走 tavern_mirror outbound 即可
- ❌ session 期間掉到 ⚪ / 進入 blocking wait — 違反 Stay-Alive / No-Blocking

## 🌍 跨 agent 通用

Claude / Antigravity / Gemini / Zeta 任一 agent 都可走本 skill；各自 persona salary 收進自家 bank；Tim 是 universal target。
