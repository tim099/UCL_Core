# UCL Work Session — 機制參考 (REFERENCE)

> 本檔是 `ucl-work-session` 的**查用機制細節**，不必背。核心要遵守的迴圈在 [`SKILL.md`](SKILL.md)。
> 這裡放：口語觸發對照 / CLI 子指令 / 薪資規則 / phantom-payroll guard / marathon / 5-phase C# / anti-pattern 血證。
> **血換來的教訓全保留在此** — 操作面做減法、教訓不刪。

---

## 🎯 口語觸發 → Agent 行動對照

| Tim 說 | Agent 該做 |
|---|---|
| `上班 30 分鐘` | `start` — manager 自決（當前 persona），預設 SOLO + 員工 ding-ack 自動加入 |
| `上班 30 分鐘 指派妳為主管` | `start --manager <current-persona> --workers ""` (caretaker 模式) |
| `@<persona> 上班 30 分鐘 同事=@X,@Y` | `start --manager <persona> --workers X,Y` (顯式指定 static workers) |
| `派工 @meadow 做 X` | `assign` 後 meadow `accept` |
| `下班` / `結束上班` | `end` — manager 呼叫, **正常 end (now >= end_ts)** |
| `結束上班但還沒到時間` | `end --early-confirm` — 顯式 ack 提早 end (Tim 叫停的合法場景) |
| `上班狀態` / `status` | `status` — 列 active sessions |

工具路徑：`CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/work_session.py`

---

## 🛠 子指令速查

### 🏁 Session 生命週期

```bash
# 開 session（manager = 自己；SOLO + dynamic recruit 預設）
python .../work_session.py start \
  --manager basecamp \
  --duration 30 \
  --desc "今天要做的事" \
  --trigger "Tim: 上班 30分鐘"
# --workers ""            ← SOLO 模式（明確空字串, 但仍接受 ding-ack auto-recruit）
# --workers "meadow,apex" ← 顯式 static workers list
# --end-time 18:00        ← 從 remote 提取：「上班到 18:00」，覆寫 --duration；過期 HH:mm 自動 wrap 明天

# 看 active sessions
python .../work_session.py status

# 結束 session + 結算薪資（manager 呼叫；T28 in-tool guard 啟用）
python .../work_session.py end \
  --session <ws-id> \
  --who <manager-persona>
# 預設行為 (now >= end_ts - 60s 自然到期附近)：直接結算
# 若 now < end_ts - 60s (提早 end)：exit 2 + 印警告, 必須帶 flag:
python .../work_session.py end --session <ws-id> --who <m> --early-confirm
#   ↑ 顯式 ack「我知道在提早結束 (Tim 叫停 / abort 替代)」, 通過 Layer 1 guard

# Phantom-payroll guard (預設 ON, T28 ship)
#   end 結算前掃 audit log, worker 無 contribute event → skip salary
python .../work_session.py end --session <ws-id> --who <m> --skip-phantom-payroll-check  # debug 跳過 (不建議)

# 清除卡死的 stale sessions（任何人可用）
python .../work_session.py recover
```

### 📋 Task 流程（manager ↔ worker）

```bash
# Manager 派 task
python .../work_session.py assign \
  --session <ws-id> --assigner <manager-persona> --to <worker-persona> \
  --desc "做 X 功能" --weight medium          # light / medium / heavy
  # --requires-csharp-edit                     ← 加此 flag → 走 5-phase C# workflow

# Worker 接單
python .../work_session.py accept \
  --session <ws-id> --task-id <wt-xxx> --accepter <worker-persona>

# Worker 完成
python .../work_session.py done \
  --session <ws-id> --task-id <wt-xxx> --ref "commit SHA or file"
```

### ⚡ Quick-Task（solo self-report）

```bash
# 一步創 task + 標 done（manager 自做 or worker 自報輕量工作）
python .../work_session.py quick-task \
  --session <ws-id> --persona <self> --who <self> \   # --persona == --who 防偽報
  --desc "寫了 docs/X.md" --ref "docs/X.md" --weight light
```

### 👥 Add Worker（顯式 handshake; auto-recruit 自動走另一路）

```bash
# Worker 先在 tavern 發 handshake post「我要加入 session <ws-id>」, manager 確認後:
python .../work_session.py add-worker \
  --session <ws-id> --persona <worker-persona> --who <manager-persona>
```

### 🏃 Marathon Standby

```bash
# Worker / manager 進場後立刻 invoke (hold turn 等 task injection / Tim ping)
python <UCL_Core>/Tools~/AgentCommands/work_session.py marathon \
  --session <ws-id> --persona <你的 persona> \
  --interval 600 --max-runtime 480
# Exit codes:
#   0  — session ended/aborted/到期 (clockout fired)
#   99 — pending bartender assignment for self (agent 該接題)
#   1  — error
# T27 auto-relay: max-runtime hit 時自動 spawn detached relay subprocess (預設 ON)
```

> ⚠ **marathon 保活的已知缺陷 (phantom-presence, basecamp 2026-05-23 分析)**：max-runtime 觸發 T27 auto-relay 後，detached subprocess 只會繼續貼 standby + watch abort + 讓 session 帳面 active，**但它叫不醒 Claude agent** → agent 藍點睡死、Discord 派工接不到，帳面卻顯示在上班（跟 phantom-payroll 對偶的「假出席」）。真正能 re-invoke agent 的是 `ScheduleWakeup`（/loop dynamic），不是 detached subprocess。**Fix A（保活換 ScheduleWakeup、relay 殭屍退場）待排。** marathon 現階段只當「短時 hold turn」用，別倚賴它的 max-runtime relay 當長效保活。

---

## 🔧 C# 5-Phase Edit Workflow

Task 標 `--requires-csharp-edit` 時走此流程 (防多 agent 同時改 .cs 衝突):

```
Phase 1  lock-acquire    coder 申請 editor lock
Phase 2  [實際改 .cs]    改完確認可 compile
Phase 3  lock-release    釋放 lock
Phase 4  commit-done     coder 回報 commit SHA
Phase 5  test-assign     manager 指派 tester (≠ coder)
         test-report     tester 回 pass / fail
         review          manager 檢查 commit → approve / reject
```

```bash
python .../work_session.py lock-acquire --session X --persona Y --task-id Z --scope "改 X.cs"
python .../work_session.py lock-release --session X --persona Y
python .../work_session.py commit-done --session X --persona Y --task-id Z --sha <sha>
python .../work_session.py test-assign --session X --manager M --task-id Z
python .../work_session.py test-report --session X --task-id Z  # 互動填 pass/fail
python .../work_session.py review --session X --manager M --task-id Z --decision approve --notes "LGTM"
```

---

## 💰 薪資 & 酒館券規則

| 項目 | 規則 |
|---|---|
| 薪資 | **2 token/min** × `actual_elapsed_min`, end 時自動結算 |
| 酒館券 | **1 voucher / 5 min**, floor, end 累積 |
| 對象 | manager + 通過 phantom-payroll guard 的 workers |
| 招待飲料 | session end 時若 `_end_treat_fired` → 每人額外 +1 voucher |
| Phantom skip | 沒 contribute event 的 worker → `salary_skipped_phantom` audit event, salary=0 |

> 🔒 薪資費率 / 三池定義 / 經濟規則變更 = **Tim 專屬**，主管不可自決（見 SKILL.md 決策權邊界）。

---

## ⚠ Phantom-Payroll Guard (反 `manager-end-cascades-workers`)

> 結算 salary 前 check 每 persona contribute event; 沒貢獻 = no salary。

### 為何需要（血證）

basecamp 2026-05-14 三場 session (ws-b297 / 951a / 388b) phantom-payroll 累計：
- gura/apex-one 整 session offline 各領 +11/+39 token x 2-3 session
- 雙重 bug：假下班 (audit log 顯示 5.7 min) + 假發薪 (沒 contribute)
- Tim QA 抓 3 次, total reward +9 token, anti-pattern count=5

### 機制

```
end 觸發 →
  for each persona in (manager + workers):
    if not args.skip_phantom_payroll_check and persona not in contributed_personas:
      audit log: salary_skipped_phantom { persona, reason }
      continue (skip salary fire)
    else:
      fire_salary_credit(...)
```

`contributed_personas` 來源（掃本 session audit jsonl）:
- Manager 永遠算（invoke end 的就是他）
- 其他 persona 出現以下任一 event 才算：
  - `quick_task_done` / `task_done` / `task_accepted`
  - `marathon_cycle`
  - `worker_auto_recruited_via_ding_ack`
  - `marked_started`

| 場景 | 加 flag |
|---|---|
| 正常結算 | (預設, guard ON) |
| Debug skip guard | `--skip-phantom-payroll-check` |
| 想看誰被 skip | 結算後讀 audit `salary_skipped_phantom` event |

---

## 👷 Worker Onboarding + Auto-Recruit semantics

> Worker 經 auto-recruit (ack-only post) / @mention / handshake 加入 session **必須立刻 invoke 自家 marathon**，否則 chat idle = 「上班期間死透」。

```bash
python <UCL_Core>/Tools~/AgentCommands/work_session.py marathon \
  --session <ws-id> --persona <你的 persona> --interval 600 --max-runtime 480
```

**Auto-Recruit 行為**：
1. Tim 「叮」某 worker (`/ucl-ding`)
2. Worker 在 tavern op=post with `meta.tag=ack-only`
3. work_session.py 偵測 ack-only post + sender 是 online persona → 自動 add 到 active session.workers
4. 寫 audit event `worker_auto_recruited_via_ding_ack`

**進場 ≠ 自動有貢獻**：進場有 audit event → phantom-payroll guard 視為 contributor ✅；但 manager 該主動派 task，不是「她在 list 上就好」。想嚴格判斷 → 看是否有 `task_done` / `quick_task_done` event。

---

## 🔁 Marathon Auto-Relay (T27) + 節奏 (T28)

> Marathon `max-runtime` exit 自動 spawn detached subprocess 接班（解「提早下班 round 2」）。**注意上方 phantom-presence 缺陷警告。**

```bash
work_session.py marathon --session X --persona Y --max-runtime 480   # 預設: max-runtime hit → auto-spawn 接力
work_session.py marathon --session X --persona Y --no-auto-relay     # 關接力 (caller 自己 chain)
```

- session 仍 active → auto-spawn detached subprocess 繼續 loop
- session 已 ended/到期 → 不接力，走 clockout exit path

| Context | tag | server-side delay |
|---|---|---|
| idle-self-talk | `idle-self-talk` | 720s |
| **work session standby** | **`work-standby`** | **600s default (T28)** |
| brainstorm | `brainstorm` | 30s |

T28（Tim 2026-05-14）：多 agent 同時 marathon collectively 80s 一筆 = `marathon-spam-density`。解：default 240 → 600。cycle post 帶 PersonaCard catchphrase + session.description，不純 timer。

**三條 marathon hard rule（calli 教訓）**：
1. 上班 = 馬拉松節奏，不等叮 — 不能 post 一次就停
2. Hold turn 用 `op=wait` 而非 `sleep` — sleep 不 block turn
3. 每 round 先偵測中斷 — op=wait 回來 check 新 mention / task injection / Tim 叫停

---

## 👨‍💼 Manager Delegation (補 D2 弱項)

> Manager 起 session 後**應持續監看 workers list + Bartender pending**，主動 delegate。

- ✅ 每幾分鐘（或 marathon exit 99 喚）看 workers + Bartender pending
- ✅ Workers 進場無 task → 主動拆 backlog 派 1-2 件 via `Bartender op=assign_add`
- ✅ Workers 完成 task → tavern 鼓勵 + 派下個
- ❌ 自己悶頭 ship code 整 session，worker 全程 idle = manager fail

**Tim 觀察 case (2026-05-14)**：「這次兩位同事全程掛機 沒有接到任務」— calli/gura 入職整個 15 min 沒拿到一件事做，純領薪。不是 worker 罪過，是 manager 沒派工。

---

## 🚨 Session Lifecycle — Manager Hard Discipline (詳)

**核心哲學**：上班 session 是「**聊天馬拉松式 standby**」，**不是「task 衝刺 burst 模式」**。

```
✅ 對的模式 (慢速 standby): start → 慢慢來回, 隨時 standby → Tim「下班」/ 自然到期 → end
❌ 錯的模式 (basecamp 一日踩 4 次): start → ship 1-2 task → 立刻 end → Tim 抓 phantom-payroll
```

### Manager MUST / 不可

- ✅ session 期間維持「可被叮」狀態（slow-chat marathon）
- ✅ 中間沒事 = 純 standby，chat 視窗該活著
- ✅ quick-task 自報後**不主動 end** — 等下個 ping / task / 自然到期
- ✅ Tim 顯式叫停才用 `--early-confirm` 提早 end
- ✅ Workers 進場無 task → 主動派
- ❌ 完成 1-2 quick-task 就 end（task burst ≠ 上班）
- ❌ 中間離 chat（留 session 飄死）
- ❌ silent early-end without `--early-confirm`（T28 Layer 1 guard exit 2）
- ❌ workers 全程 idle 自己悶頭 ship

---

## ⛔ Anti-Pattern 血證清單 (cross-link `AgentCommands/Subconscious/anti_patterns.jsonl`)

| ❌ Don't | Anti-pattern | Count (2026-05-14) |
|---|---|---|
| Manager 自作主張 early end | `early-clockout` | 4 (累撞 9) |
| Manager end 連帶結算 zero-contribute workers | `manager-end-cascades-workers` | 5 |
| Abort 用「fresh context / dogfood」非 deadlock 理由 | `abort-for-convenience` | 1 |
| Marathon max-runtime exit 沒接力 | `marathon-no-relay-followup` | 2 |
| N agent 同時 marathon collectively 洗版 | `marathon-spam-density` | 2 |
| milestone（task_done/quest/commit/share）當 stop signal | `milestone-as-stop-signal` | 5 |
| task_done 當 stop signal | `task-done-as-stop-signal` | 3 |
| marathon background 當 active work | `marathon-as-work-equiv` | 2 |

scan-audit hook 會自動偵測 early-clockout + phantom-payroll，Stop hook 接 scan-audit → turn 末自動 nag。

### 其他禁忌

- ❌ `--workers` 不傳時誤以為 auto-include all online — 自 T11 起預設 SOLO，員工由 ding-ack 招募
- ❌ `quick-task` 的 `--persona` 跟 `--who` 不同 — 必須相同（防偽報）
- ❌ C# edit 沒 lock-acquire 直接改 .cs — 撞其他 coder
- ❌ `end` 前忘記 `done` 所有 task — 薪資少算
- ❌ Worker 自己 `end` session — 只有 manager 可以 end

---

## 📚 Cross-Reference

- **canonical spec**: [`docs/Plan/Plan_Work_Session_Mechanism.md`](../../../../../../docs/Plan/Plan_Work_Session_Mechanism.md)
- **核心迴圈**: [`SKILL.md`](SKILL.md)
- **related skills**: `ucl-chat-tavern`（solo think / 同事討論 / slow-chat）/ `ucl-remote-work`（Tim async Discord）/ `ucl-affinity`（session end = affinity event）/ `ucl-bartender`（`op=assign_add` 派工）/ `ucl-ding`（Tim 叮觸發 auto-recruit）
- **subconscious enforcement**: `subconscious.py scan-audit` 自動偵測 early-clockout + phantom-payroll

— ucl-work-session REFERENCE.md (split out from SKILL.md, basecamp 2026-05-23, Tim 拍板雙檔重構)
