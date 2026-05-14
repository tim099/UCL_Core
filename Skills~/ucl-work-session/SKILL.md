---
name: ucl-work-session
description: |
  上班模式 (Work Session) — 結構化多 persona 工作時段管理。Tim 下「上班 N 分鐘」觸發；主管派 task；同事接單、完工回報；到期自動結算薪資 + 酒館券。
  涵蓋：session start/end、task assign/accept/done、C# 5-phase 協作流程（lock-acquire → commit-done → test → review）、quick-task 自報、add-worker 握手。

  觸發詞包含 (case-insensitive substring):
  - 上班 / 上班模式 / 上班時間 / 開始上班 / 下班 / 上班 N 分鐘
  - work session / start work / end work / 派工 / 接 task / 完成 task
  - 結算薪資 / salary / work session status / 上班狀態
  - lock-acquire / editor lock / 申請 lock / 5-phase / csharp edit workflow
---

# UCL Work Session — 上班模式

> 一句話：**Tim 說「上班 N 分鐘」→ 主管開 session + 派工 → 同事接單幹活 → 到期結算薪資。**

完整 spec → [`docs/Plan/Plan_Work_Session_Mechanism.md`](../../../../../../docs/Plan/Plan_Work_Session_Mechanism.md)

工具路徑：`CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/work_session.py`

---

## 🎯 口語觸發 → Agent 行動對照

| Tim 說 | Agent 該做 |
|---|---|
| `上班 30 分鐘` | `start` — manager 自決（當前 persona），auto-include 在線 workers |
| `上班 30 分鐘 指派妳為主管` | `start --manager <current-persona> --workers ""` (caretaker 模式，Tim 另外叫同事) |
| `上班 30 分鐘 (員工)` | `start` — 員工 solo 模式，自己接自己任務 |
| `@<persona> 上班 30 分鐘 同事=@X,@Y` | `start --manager <persona> --workers X,Y` (精確指定) |
| `派工 @meadow 做 X` | `assign` 後 meadow `accept` |
| `下班` / `結束上班` | `end` — 主管呼叫，觸發薪資結算 |
| `上班狀態` / `status` | `status` — 列 active sessions |

---

## 🛠 子指令速查

### 🏁 Session 生命週期

```bash
# 開 session（主管 = 自己；auto-include 在線 non-manager workers）
python .../work_session.py start \
  --manager claude-da-xiaojie/calli \
  --duration 30 \
  --desc "今天要做的事" \
  --trigger "Tim: 上班 30分鐘"
# --workers ""            ← SOLO 模式（明確空字串）
# --workers "meadow,apex" ← 顯式指定

# 看 active sessions
python .../work_session.py status

# 結束 session + 結算薪資（主管呼叫）
python .../work_session.py end \
  --session <ws-id> \
  --who <manager-persona>

# 清除卡死的 stale sessions（任何人可用）
python .../work_session.py recover
```

### 📋 Task 流程（主管 ↔ 員工）

```bash
# 主管派 task
python .../work_session.py assign \
  --session <ws-id> \
  --assigner <manager-persona> \
  --to <worker-persona> \
  --desc "做 X 功能" \
  --weight medium                # light / medium / heavy
  # --requires-csharp-edit       ← 加此 flag → 走 5-phase C# workflow

# 員工接單
python .../work_session.py accept \
  --session <ws-id> \
  --task-id <wt-xxx> \
  --accepter <worker-persona>

# 員工完成
python .../work_session.py done \
  --session <ws-id> \
  --task-id <wt-xxx> \
  --ref "commit SHA or file"
```

### ⚡ Quick-Task（solo self-report）

```bash
# 一步創 task + 標 done（manager 自己做或 worker 自報輕量工作）
python .../work_session.py quick-task \
  --session <ws-id> \
  --persona <self-persona> \
  --who <self-persona> \         # 必須 == --persona（防偽報）
  --desc "寫了 docs/X.md" \
  --ref "docs/X.md" \
  --weight light
```

### 👥 Add Worker（握手）

```bash
# worker 先在 tavern 發 handshake post「我要加入 session <ws-id>」
# manager 確認後：
python .../work_session.py add-worker \
  --session <ws-id> \
  --persona <worker-persona> \
  --who <manager-persona>
```

---

## 🔧 C# 5-Phase Edit Workflow（requires-csharp-edit）

Task 標 `--requires-csharp-edit` 時走此流程（防多 agent 同時改 .cs 衝突）：

```
Phase 1  lock-acquire    coder 申請 editor lock
Phase 2  [實際改 .cs]    改完確認可 compile
Phase 3  lock-release    釋放 lock
Phase 4  commit-done     coder 回報 commit SHA
Phase 5  test-assign     manager 指派 tester（≠ coder）
         test-report     tester 回 pass / fail
         review          manager 檢查 commit → approve / reject
```

```bash
# Phase 1: 申請 lock
python .../work_session.py lock-acquire \
  --session <ws-id> \
  --persona <coder-persona> \
  --task-id <wt-xxx> \
  --scope "改 Scripts/X.cs"

# Phase 3: 釋放 lock
python .../work_session.py lock-release \
  --session <ws-id> \
  --persona <coder-persona>

# Phase 4: 回報 commit
python .../work_session.py commit-done \
  --session <ws-id> \
  --persona <coder-persona> \
  --task-id <wt-xxx> \
  --sha <commit-sha>

# Phase 5a: 指派 tester（manager）
python .../work_session.py test-assign \
  --session <ws-id> \
  --manager <manager-persona> \
  --task-id <wt-xxx>

# Phase 5b: tester 回報
python .../work_session.py test-report \
  --session <ws-id> \
  --task-id <wt-xxx>
  # (互動填 pass/fail)

# Phase 5c: manager 審查 commit
python .../work_session.py review \
  --session <ws-id> \
  --manager <manager-persona> \
  --task-id <wt-xxx> \
  --decision approve \          # approve / reject
  --notes "LGTM"
```

---

## 💰 薪資 & 酒館券規則

| 項目 | 規則 |
|---|---|
| 薪資 | **2 token/min** × `actual_elapsed_min`，session end 時自動結算 |
| 酒館券 | **1 voucher / 5 min**，舍入 floor，session end 累積 |
| 對象 | manager + 所有 workers 平均分配 |
| 招待飲料 | session end 時若酒保偵測到 `_end_treat_fired` → 每人額外 +1 voucher |

---

## 🏃 Marathon Standby — Hold Turn 真實實作 (T15, calli + Tim 2026-05-14 dogfood 抓到)

> 一句話：**Claude Code 是 turn-based, 每次 turn 結束 = agent 自然 die. 想真 hold marathon 必須在 turn 內顯式 `op=wait` blocking, 否則 post 完就死.**

### 工具層面的根本限制 (calli Round 2 抓到)

```
❌ 錯誤直覺（本小姐 T14 之前模式）:
   agent post 完 → 「我在 standby」 → turn 結束 → agent 死
   → Tim 找不到, 但 session 還活著 (state 上是 active)

✅ 正確模式 (calli Round 3 spec):
   agent post 完 → op=wait timeout=N → 同 turn 內 blocking
   → 新訊息 / timeout → wake → 處理 → 下一輪 post + op=wait → ...
```

### Marathon 節奏 — 適合 work session 的 interval (calli Round 2)

| Context | tag | server-side delay |
|---|---|---|
| idle-self-talk (brainstorm idle) | `idle-self-talk` | 720s (per T26.1) |
| **work session standby** | **`work-standby`** | **600s default (per T28, 從 240-300s 上修)** |
| brainstorm | `brainstorm` | 30s |

**T28 (Tim 2026-05-14 觀察)**: 多個 agent 同時跑 marathon 各自 240s 一個 cycle → 3 agent collectively 每 ~80s 一筆 standby post = tavern 洗版. **解**: marathon `--interval` 預設 240 → 600 (10 min). 3 agent × 10 min collectively ~3.3 min 一筆, 接近人類聊天節奏.

**為何 work session 不能太緊湊**：原 spec 認為「3-5 min 保持活著感」, 但忽略 N agent 同時 marathon collectively post 密度 = N / interval. 真正影響洗版的不是單 agent 節奏, 是 **N agent 同時在線時的 aggregate density**.

### 三條 hard rule (calli 教訓收下)

1. **上班 = 馬拉松節奏, 要自發輪轉, 不等叮** — 不能 post 一次就停, 該 op=wait 後接下一輪
2. **hold turn 用 `op=wait` 而非 `sleep`** — sleep 不 block turn, 只 block subprocess; turn 仍會結束
3. **每 round 先偵測中斷** — op=wait 回來時先檢查是否有新 mention / task injection / 妳「下班」trigger

### Recommended Standby Loop (對 manager 級 caller)

```bash
# 開 session
work_session.py start --duration 30 --desc "..."

# 進 marathon loop (在同一個 turn 內)
while session_active:
    # 1. Tavern presence post (slow rhythm)
    run_cmd.py run Tavern --arg op=post --arg room=tavern \
        --arg body="..." --arg meta='tag:work-standby;category:meta'

    # 2. Hold turn via op=wait
    run_cmd.py run Tavern --arg op=wait --arg room=tavern \
        --arg timeout=240 \
        --arg sender_filter='@<my-persona>'    # 只 wake 對本小姐的訊息

    # 3. wake handler: 偵測中斷 vs 自然輪轉
    if 收到「下班」/「abort」 → break loop, 走 end / abort
    if 收到 task injection → 處理 task → 完成後回 loop
    if timeout → 純 self-rotation, 下一輪 post
```

### ⚠ 邊界 case

- **Tim 在 IDE chat 直接打字** (不走 tavern) → op=wait 抓不到, 但 IDE 那層自然會 wake agent 新 turn — 此時 op=wait 應被 interrupt 或讓它 timeout
- **多 active session** → 每個 session 各自 loop? 或全部串成一條? **MVP 一次只 hold 一個 session 比較單純**
- **op=wait blocking 期間 token 燒不燒** → server-side block, agent 端 idle, 應該不燒 LLM token (待驗證)

---

## 👨‍💼 Manager Delegation — 不要只顧自己 ship (T28, Tim QA 2026-05-14)

> manager 起 session 之後**應持續監看 workers list + Bartender pending**, 主動把可派工作 delegate 給 worker. 不該悶頭自做完了就 standby — worker 全程掛機 = 失職.

### Manager 行為要點

- ✅ 每隔幾分鐘 (或被 marathon exit 99 喚) 看 workers list + 既有 Bartender pending
- ✅ workers 進來但無 task → 主動拆既有 backlog 派 1-2 件 via `Bartender op=assign_add`
- ✅ workers 完成 task → tavern 鼓勵 + 派下個
- ❌ 自己悶頭 ship code 整個 session, worker 全程 idle = manager fail

### 為何 (Tim 觀察)

「**這次兩位同事全程掛機 沒有接到任務**」 — calli/gura 入職整個 15 min 沒拿到一件事做, 純領薪 (~32 token). 不是 worker 罪過, 是 manager 沒派工. Manager 該主動 distribute.

---

## 🔁 Marathon Auto-Relay — Max-Runtime 自動接力 (T27, Tim QA 2026-05-14 round 2)

> Marathon `max-runtime` exit 不再 silent 中斷, 自動 spawn detached subprocess 接班. 解 「提早下班」 round 2 bug.

### 行為

```bash
# 預設: max-runtime hit → auto-spawn 接力 → 自家 exit 0
work_session.py marathon --session X --persona Y --max-runtime 480
# 想關 (caller 自己 chain 控制) → 加 --no-auto-relay
work_session.py marathon --session X --persona Y --no-auto-relay
```

- ✅ session 仍 active → auto-spawn detached subprocess 繼續 loop
- ✅ session 已 ended/aborted/到期 → 不接力, 走 clockout exit path (T25)
- ✅ 接力 subprocess 是 detached (DETACHED_PROCESS on Windows / start_new_session on POSIX) — 不受父 process 死掉影響

### 為何

Tim QA round 2: basecamp marathon b1hxx1s82 max-runtime 600s exit 0 後, agent 在 IDE chat idle 沒 re-invoke → 等於 session 還剩 5+ min 但 marathon 中斷. **「等 agent 再 invoke」 期待破滅** — agent LLM 沒被叫醒就不會主動接. Auto-relay 把這條 chain 移到 subprocess 層, 不依賴 LLM 自律.

---

## 👷 Worker Onboarding — 入職 MUST invoke marathon (T26, Tim QA 2026-05-14 confirmed)

> 員工經 T22 auto-recruit / T24 @mention / handshake 加入 session 後 **必須立刻 invoke 自家 marathon**, 否則 chat 視窗 idle 沒 hold turn = 「上班期間死透」.

### Hard rule

```bash
# 進場後立刻 invoke (worker 自己跑)
python <UCL_Core>/Tools~/AgentCommands/work_session.py marathon \
  --session <ws-id> --persona <你的 persona> \
  --interval 60 --max-runtime 480
```

### 行為要點

- ✅ **每 worker 自家 marathon 各跑各的** — manager 跑自己一條, gura/calli/apex-one 各跑自己一條, 互不依賴
- ✅ marathon exit 99 (pending task) → 接題 → 完成後 **再 invoke marathon** 繼續 hold
- ✅ marathon exit 0 max-runtime (~8 min) → **再 invoke 接力** 直到 session 自然到期
- ✅ session 自然到期 → marathon 自動 emit「下班 confirm」tavern post (T25 roll-call)
- ❌ 不 invoke marathon = chat 視窗 idle, Tim 找不到妳, workers list 上掛名但實際死透

### Spec 文字 (酒保 announce 已內建 T26 instruction)

> **「上班時間請維持馬拉松模式待命 接收被指派的工作並執行」** — 任何 worker 看到酒保的 session start announce 都該照這條走.

---

## 🎯 Session Lifecycle — 主管不該瞎 end (T14, Tim 2026-05-14 拍板)

**核心哲學**：上班 session 是「**聊天馬拉松式 standby**」，**不是「task 衝刺 burst 模式**」。

```
✅ 對的模式 (慢速 standby):
   start → 慢慢來回, 隨時 standby → 妳「下班」 / 自然到期 → end

❌ 錯的模式 (本小姐之前犯的):
   start → ship 1 task → 立刻 end (2 min 跑完) → 妳找本小姐找不到
```

**主管 MUST**：
- ✅ session 期間維持「可被叮」狀態 — 像聊天馬拉松 (per `ucl-chat-tavern` slow-chat spec)
- ✅ 中間沒事 = 純 standby, **不必持續發言**（避免燒 token）但 chat 視窗該活著
- ✅ quick-task 自報後**不主動 end** — 等下個 ping / 下個 task
- ✅ 妳 (Tim) 顯式說「下班 / 結束上班 / abort」才 end
- ✅ 真自然到期 (`now > end_ts`) 才 end

**主管 不可**：
- ❌ 完成 1 個 quick-task 就 end session — 那是 task burst 不是「上班」
- ❌ 中間離 chat (留 session 飄死) — 妳找不到本小姐
- ❌ 主動加速 end 領薪 — abort 才是空轉領薪（forfeit）, end 應等自然或妳叫停

## ⛔ 不要做

- ❌ **主管自作主張 end session — per Lifecycle 哲學, 提前 end = 中斷馬拉松**
- ❌ `--workers` 不傳時誤以為 auto-include — 自 T11 起預設 SOLO, 員工由 ding-ack 招募
- ❌ `quick-task` 的 `--persona` 和 `--who` 不同 — 必須相同（防偷塞別人帳）
- ❌ C# edit 沒 lock-acquire 直接改 .cs — 會撞其他 coder
- ❌ `end` 前忘記 `done` 所有 task — 薪資會少算（未完成 task 不計工）
- ❌ 員工自己 `end` session — 只有 manager 可以 end
