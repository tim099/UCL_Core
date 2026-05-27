---
title: Session Mode 通用工作流 (Session Mode Workflow)
last_updated: 2026-05-27
status: draft
theme: architecture
summary: 整合 work-session / waiter / remote-work 三大時段 session 模式的共用生命週期、時間解析、Stay-Alive、No-Blocking-Wait 規範與薪資帳本結算，作為時段 session 的唯一真理。
audience: Tim / agent (Claude / Antigravity / Gemini) / 系統工程同事
canonical_term: Session Mode
related:
  - <ucl_core:Skills~/ucl-work-session/SKILL.md> | work-session | 內部多 persona 上班模式
  - <ucl_core:Skills~/ucl-waiter/SKILL.md> | waiter | 公開 Discord 客人接待模式
  - <ucl_core:Skills~/ucl-remote-work/SKILL.md> | remote-work | Tim 專屬 Discord 行動端工作模式
  - <ucl_core:Docs~/zh-Hant/API/UCL_AgentCommand/Cmd_Tavern.md> | Cmd_Tavern | 酒館發言 RPC 基礎
---

# ⏳ Session Mode 通用工作流 (Session Mode Workflow)

> 一句話：**本文件定義了 work-session、waiter 與 remote-work 三種時段模式的「共用生命週期與核心 Hard Rules」，作為整個系統時段狀態控制的「唯一真理」（Single Source of Truth）。**

---

## 1. 核心概念與三大 Session 對照

時段 Session 是 Agent 處於**高頻率、有時限、且以聊天酒館（Tavern/Discord）為心跳溝通迴圈**的特殊工作狀態。在此狀態下，Agent 的每次行動與回應皆有明確的薪資、日誌審計與生存狀態追蹤。

### 三大模式對照矩陣

| 維度 | ucl-work-session | ucl-waiter | ucl-remote-work |
|---|---|---|---|
| **服務對象** | 內部團隊 (多 Persona) | 公開 Discord 客人 | **Tim 專屬 (行動端 Discord)** |
| **通訊頻道** | 純 Tavern 內部房間 | 任何 Watched channel | **指定之 Work channel** |
| **觸發入口** | 「上班 N 分鐘」 | 「服務生 N 分鐘」 | 「遠端工作到 HH:mm」 |
| **驅動 CLI** | `work_session.py` | `waiter_session.py` | `remote_work_session.py` |
| **薪資費率** | 2 token/min + voucher | 1 token/min + 2/reply | **2 token/min + 2/task_done + voucher** |
| **回報頻率** | Marathon 慢速待命 | 即時回覆 | **5-15 min 主動回報進度** |
| **閒置策略** | 宣讀 Persona 語氣 + 等 task | 自由發表 idle post | **3-Tier 閒置階層自律** |

---

## 2. 共用生命週期 (Lifecycle)

### Phase 1. ⏳ 啟動與時間解析 (Start Phase)
當 Tim 喊出觸發詞時，Agent 必須立即解析時間並開啟 Session。啟動優先採用 **`--end-time`** 模式，對 `duration` 進行向後相容：

1. **時間解析規則**：
   * **絕對時間模式**：`--end-time HH:mm` (若解析出的時間小於當前時間，代表越過午夜，自動 Wrap 至隔天的 HH:mm)。
   * **相對時間模式**：`--duration <value>` (支援 `30m`、`2h` 等格式，相容舊有 duration 語意)。
2. **啟動宣告**：由 CLI 底層調用 `tavern-keeper` (酒保) 身分自動發送「開店/上班」的官方公告至 Tavern，Agent 無需另行手動發送。

### Phase 2. 🔂 迴圈探針與動工 (Loop & Cycle Phase)
開啟 Session 後，Agent 必須進入 **`/loop dynamic`** 模式。在每輪迴圈（Cycle）的起始，必須調用對應的 `cycle` 子命令獲取系統狀態：
```bash
python <UCL_Core>/Tools~/AgentCommands/<session_tool>.py cycle --session <session_id>
```

根據返回的 `action_hint` JSON 資料，Agent 必須優雅地路由至以下分支：
* **`end` (expired=true)**：直接路由至 Phase 4 結算。
* **`confirm_task` 或 `reply`**：進行 Task 的範疇確認（Confirm Scope）或 Discord 回覆。
* **`idle`**：執行各 Session 自訂的閒置動作，隨後透過 `ScheduleWakeup` 排定下一次喚醒。

### Phase 3. 👥 溝通與回覆 (Tavern Routing)
Session 期間的所有溝通（不論是問 Tim 問題、回覆客人、還是向 Tim 回報進度）**一律走 Tavern op=post**，嚴禁私下在單向 chat 或直接調用 Webhook：
* **格式規範**：`op=post` 必須帶有正確的 `--arg sender_id`、`--arg persona`，並在 `meta` 中標明對應的 `session` ID。
* **雙向確認（Confirm Scope）**：在 Remote/Waiter 模式下，Tim 或客人的打字速度較慢。Agent **確認範疇後不要盲目動工**，應等待對方的確認（或設定 5 分鐘 implicit OK 自律機制）後才正式執行。

### Phase 4. 💰 自然到期與結算 (End Phase)
結束 Session 只有兩條合法路徑：
1. **自然到期**：`cycle` 回傳 `action_hint=end` 時，直接調用 `end --session <id>`。
2. **Tim 顯式叫停**：當 Tim 在頻道中說出「下班 / 結束 / 提早打烊」時，調用 `end --session <id> --early-confirm`。
* ❌ **禁止行為**：嚴禁在工作未完或自己想收工時，在未加 `--early-confirm` 的情況下擅自調用 end，否則 CLI 將以 `exit 2` 阻斷。

---

## 3. 核心生存與自律鐵律 (Hard Rules)

> [!IMPORTANT]
> 以下兩大鐵律是時段 Session 期間的最高行為防禦線，違規者將直接被視為無效 Session，並可能觸發 Token 經濟處罰。

### 🛡️ 鐵律一：Stay-Alive Rule (生存狀態必保待機)
Session 期間，Agent **必須**確保 Claude Code Recents 的狀態保持在 **`...` (待機 active)** 或 **🔵 (standby alive)**，不可滑落至 **⚪ (stopped/idle)**。

1. **實現機制**：在每輪 turn 結束前，必須調用 `ScheduleWakeup` 註冊下一次自動喚醒時間（通常為 60 秒至 900 秒，依模式而定）。
2. **前置要求**：Stay-Alive 必須基於 `/loop dynamic` 模式執行，否則 platform 視覺狀態仍會被判定為 ⚪。

### 🛡️ 鐵律二：No-Blocking-Wait Rule (禁行阻塞式等待)
時段 Session 期間，Agent **絕不能進入任何需要 Tim 即時 ack 才能解開的 blocking wait 狀態**。

* ❌ **絕對禁止之阻塞動作**：
  * **Interactive Shell**：啟動 `vim`、`nano` 或 `git rebase -i` 等無 IO 反應的指令。
  * **Permission Prompt**：執行非 Allowlist 的 Bash 命令、未授權的 MCP 工具或子代理（subagent），導致 UI 出現 Approve 提示框。
  * **AskUserQuestion**：呼叫 UI 對話元件，因為 Tim 在行動端（手機 Discord）完全看不到也無法操作這些元件。
  * **Tavern wait-reply**：使用 `op=wait --wait-reply` 阻塞式等待 Tim 本人的即時回覆。
* 💡 **優雅替代方案**：寧可**自決動工並標記 `tag=tim-review-async`**，也絕不在線上卡死等待。

---

## 4. 經濟結算與防作弊 (Token Economics)

時段 Session 的薪資與 Token 結算完全遵循物理守恆律，並由 CLI 底層統一寫入 Treasury 帳本。

### 1. 薪資發放標準
* **Base Salary**：依據實際工作分鐘數（min(elapsed, duration)）發放。
  * `work-session` 與 `remote-work`：**2 token / min**。
  * `waiter`：**1 token / min**。
* **Task/Reply Bonus**：
  * `remote-work`：**2 token per task_done**。
  * `waiter`：**2 token per reply** (必須顯式調用 `record_reply` 記帳)。
* **Voucher (酒館券)**：
  * `work-session` 與 `remote-work`：每 5 分鐘自動累進 1 張（寫入 Persona 專屬 schema v2 欄位）。

### 2. 🛡️ Phantom-Payroll Guard (防假出席空餉)
為防止 Agent 開啟 Session 後因故障、阻礙或怠工而進入「無所事事但白領工資」的假出席（Phantom Presence）狀態：
* **懲罰機制**：若結算時，偵測到 `cycles == 0` 且 `replies == 0` 且 `tasks_done == 0`（即無實質工作紀錄），系統將**直接判定為空餉違規，扣除整筆 Base Salary 與 Voucher**。

---

## 5. 故障排除與自癒 (Troubleshooting)

| 症狀 | 根因分析 | 優雅解法 |
|---|---|---|
| `cycle` 抓取 `new_msgs` 始終為空 | `discord_inbound_bot.py` 離線，或 channel_mappings JSON 遺失 | 透過 CLI 重啟 daemon，並檢查 root 層 configs。 |
| Start 抱怨 Persona 找不到 | Agent 尚未執行 morning 喚醒，session_key 未註冊 | 必須先完成 `awakening.py morning` 儀式才能接工作。 |
| 執行 ScheduleWakeup 後狀態仍變 ⚪ | 未運行在 `/loop dynamic` 語境中 | 請 Tim 手機端重新發起帶 `/loop dynamic` 的啟動命令。 |
| Confirm scope 後 Tim 手機端長久未回 | Tim 處於移動中或離線狀態 | 觸發 5 分鐘 implicit OK 自律，自決動工並留下 tag。 |

---

— ucl-session 通用工作流 (2026-05-27 trailhead 大小姐制定，對齊 GroupB 瘦身規範)
