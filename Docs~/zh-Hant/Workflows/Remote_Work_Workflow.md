---
title: 遠端工作模式工作流 (Remote Work Workflow)
last_updated: 2026-07-13
status: active
theme: agent_activity
summary: 遠端工作模式 (Tim 外出時行動端 Discord 唯一介面派 task) 的完整工作流 — Stay-Alive (session 狀態必保 `...`/🔵)、No-Blocking-Wait (禁止任何需 Tim 即時回應的 blocking wait)、3-tier Idle Policy (work-thinking → QA-review → free-time)、薪資結算、故障排除，以及與 work-session / waiter 的三方比較。共通 session-mode 契約 (End 條件 / 一 persona 一 session / cycle SSOT / phantom-payroll) 由 Session_Mode_Workflow.md 承載，本檔只放 remote-work 專屬細節。
audience: Tim / agent (Claude / Antigravity / Gemini / Zeta)
canonical_term: Remote Work Mode
related:
  - <ucl_core:Docs~/zh-Hant/Workflows/Session_Mode_Workflow.md> | Session Mode 共通契約 | 時段 session 共通生命週期/End/salary/Stay-Alive/No-Blocking 單一真相 (SSOT)
  - <ucl_core:Skills~/ucl-remote-work/SKILL.md> | ucl-remote-work | 遠端工作觸發入口
  - <ucl_core:Docs~/zh-Hant/Mechanics/Remote_Work_Session.md> | Spec 完整規格 + 互動範例 + duration parser 表
  - <ucl_core:Skills~/ucl-waiter/SKILL.md> | ucl-waiter | 公開接客 (對偶模式)
  - <ucl_core:Skills~/ucl-work-session/SKILL.md> | ucl-work-session | 內部多 persona 上班 (內部團隊)
  - <ucl_core:Docs~/zh-Hant/Mechanics/Discord_Channel_Routing.md> | Channel Routing | work channel priority 80 設定
---

# 📱 遠端工作模式工作流

> **解決什麼問題**：Tim 外出時只能用手機 Discord 派 task / 接 task / 回報，沒辦法回 Claude Code chat、給 permission、點 AskUserQuestion 按鈕。本工作流讓 agent 在行動端唯一介面下維持 stay-alive、避開 blocking wait、無 task 時自主找事做，並在到期時結算薪資。
>
> remote-work 是 **Session Mode 家族**成員 — 共通契約 (End 條件 / 一 persona 一 session / reply 走 tavern / cycle 是 SSOT / salary 結構 / phantom-payroll) 見 `<ucl_core:Docs~/zh-Hant/Workflows/Session_Mode_Workflow.md>`。remote-work 是 **Stay-Alive / No-Blocking-Wait 這兩條的起源模式**（行動端最吃緊），故此二條的完整細節放在本檔；其餘共通項只列差異。

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

## 🔧 故障排除

| 症狀 | 可能原因 | 解法 |
|---|---|---|
| `cycle` new_msgs=[] 但 Tim 確實有發 | channel id 配錯 / Tim uid 配錯 / discord_inbound_bot 沒在跑 | 確認 routing JSON work channel = 1502656414487810148, daemon log `connected as`, `--tim-uid` 對齊 |
| Salary 0 | phantom-payroll guard 命中 (沒 cycle/progress/task_done) | 至少跑一次 cycle 才算貢獻 |
| Tim 行動端 reply 後 cycle 沒抓到 | discord_inbound_bot 之前 spawn 沒 reload 新 routing | kill bot subprocess, daemon 5s respawn |
| Confirm 後動工等不到 Tim 回 | Tim 行動端可能離線 / 移動中 | 設個 default ack: confirm 5 min 沒回視為 implicit OK 動工 (agent 自律, 自決) |

---

## 📋 完整 spec

→ `<ucl_core:Docs~/zh-Hant/Mechanics/Remote_Work_Session.md>`（互動範例 + duration parser 表 + 完整規格）
