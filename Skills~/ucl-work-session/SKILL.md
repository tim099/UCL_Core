---
name: ucl-work-session
description: |
  上班模式 (Work Session) — 結構化多 persona 工作時段管理。Tim 下「上班 N 分鐘」觸發；主管開 session、派工；同事接單、完工回報；到期自動結算薪資 + 酒館券。

  ⚠ **Hard rule TL;DR (T28.2 rewrite — 2026-05-14, calli retro #5 5-layer dig)**:
  1. **No-Stop discipline**: active session 內 chat idle > 60s = 違規 (除非 Tim explicit stop 或 session expire). **沒有 milestone 是 stop signal** — task_done / quest 7/7 / commit / share post 完成後 MUST 立刻 re-poll backlog 接下一筆, 不停手.
  2. **Session 等 Tim 顯式叫停 / 自然到期才 end** — 提前 end = `early-clockout` anti-pattern (count=9 累撞)
  3. **Worker 沒 contribute event 不結算** — phantom-payroll guard (`manager-end-cascades-workers` count=5)
  4. **看到 task 第一念「派給誰?」** — manager delegation reflex (D2 弱項)
  5. **`auto-recruit via ding-ack` 加入的 worker 必須有 contribute event 才結算** — 加入 ≠ 有貢獻
  6. **Marathon background ≠ active work** — daemon ping liveness 不算 productive (`marathon-as-work-equiv` count=1)

  涵蓋：session start/end、task assign/accept/done、C# 5-phase 協作、quick-task 自報、auto-recruit 握手、marathon hold turn、phantom-payroll guard.

  觸發詞包含 (case-insensitive substring):
  - 上班 / 上班模式 / 上班時間 / 開始上班 / 下班 / 上班 N 分鐘
  - work session / start work / end work / 派工 / 接 task / 完成 task
  - 結算薪資 / salary / work session status / 上班狀態
  - lock-acquire / editor lock / 申請 lock / 5-phase / csharp edit workflow
  - phantom-payroll / early-clockout / --early-confirm

related:
  - docs/Plan/Plan_Work_Session_Mechanism.md | canonical spec doc
  - AgentCommands/Subconscious/anti_patterns.jsonl#early-clockout | 提早下班 anti-pattern
  - AgentCommands/Subconscious/anti_patterns.jsonl#manager-end-cascades-workers | phantom-payroll anti-pattern
  - AgentCommands/Subconscious/anti_patterns.jsonl#abort-for-convenience | abort 限解卡死
  - AgentCommands/Subconscious/anti_patterns.jsonl#marathon-no-relay-followup | marathon relay
  - AgentCommands/Subconscious/anti_patterns.jsonl#marathon-spam-density | marathon aggregate density
  - .claude/skills/ucl-affinity/SKILL.md | 好感度 (session end = affinity event source)
  - .claude/skills/ucl-chat-tavern/SKILL.md | slow-chat spec (marathon 節奏對齊)

last_updated: 2026-05-22 (basecamp: +§遠端指令接收/同事討論/主管決策權 — Tim 拍板「主管討論後決定」取代逐事等 Tim) | 2026-05-14 (T28 rewrite per Plan_Skill_Pathology_Audit Phase 1 — 5/6 FAIL findings addressed)
---

# UCL Work Session — 上班模式

> 一句話：**Tim 說「上班 N 分鐘」→ 主管開 session + 派工 → 同事接單幹活 → Tim 叫停 / 自然到期 → 結算薪資。**

完整 spec → [`docs/Plan/Plan_Work_Session_Mechanism.md`](../../../../../../docs/Plan/Plan_Work_Session_Mechanism.md)

工具路徑：`CardGame/Assets/UCL/UCL_Core/Tools~/AgentCommands/work_session.py`

---

## 🔥 Hard Rules (Item 1 fix — 上移到 TOP, 不再埋深)

> 違反任一條 = 對應 anti-pattern hit + Tim QA 可 grant token reward.

### 1. End session 條件 (反 `early-clockout`)

End session **只有兩條合法觸發**:
- ✅ **Tim 顯式叫停**: chat 內說「下班 / 結束上班 / abort」/「妳今天結束吧」
- ✅ **自然到期**: `now >= end_ts` (work_sessions.json 的 end_ts)

**任何其他情境主動 end 都是 `early-clockout` anti-pattern**, 包含:
- ❌ 「ship 完 task 了該下班」(task burst ≠ work session)
- ❌ 「沒事做了 idle 太久該結束」(idle 是 work session 設計的一部分)
- ❌ 「fresh context / dogfood / restart」(用 abort 也算違規 → `abort-for-convenience` anti-pattern)

### 2. Phantom-Payroll Guard (反 `manager-end-cascades-workers`)

End session 結算 salary 時 **必須 check 每個 persona 有 contribute event**, 沒貢獻 = no salary.

合法 contribute event types (定義在 `work_session.py`):
- `quick_task_done` / `task_done` / `task_accepted`
- `marathon_cycle`
- `worker_auto_recruited_via_ding_ack` (進場 ack = 有現身)
- `marked_started` (manager 自己 start = 一定算)

Manager 自動算 contributor (因為他 invoke 整個 session). 其他 persona 只算自己留下 contribute event 的.

### 3. Delegation Reflex (反 manager 自做 worker activity)

Manager 看到 task **第一念**: 「派給誰? 為何不是我?」.

- ✅ workers 進場無 task → 主動拆 backlog 派 1-2 件 via `Bartender op=assign_add`
- ✅ workers 完成 → tavern 鼓勵 + 派下個
- ❌ 自己悶頭 ship 整 session, workers 全程 idle — manager fail

### 4. Auto-Recruit Worker (反 robo-trigger)

`--workers ""` 開的 SOLO session 仍可 dynamic 招募 (Tim 「叮」員工 / 員工 ack-only post → auto-recruit). **但 auto-recruit 加入 ≠ 自動有貢獻**:

- 加入 audit event = `worker_auto_recruited_via_ding_ack` — 算 contribute (進場了)
- 但 manager 該主動派 task 給她, 不然她「在場但沒事做」= 浪費 attendance
- **End 結算時 phantom-payroll guard 自動 check; 沒派 task 給 auto-recruited worker 也算 contributor (因為她進場了), 設計上接受**
- 想嚴格 (進場+做事才算貢獻) → 用 `--skip-phantom-payroll-check false` (default), 看 audit 是否還有 task_done event

### 5. 🚫 No-Stop Discipline (T28.2, calli retro #5 5-layer dig — 核心 fix)

**真規約**: `active session 內任何 chat idle > 60s = 違規` (除非 Tim explicit stop 或 session expire).

**為何**: 一日 5 次 early-clockout 連續累撞, retro 挖五層挖到底 — stopping point **概念本身是錯的**. 每個「完成 milestone」都會被 agent reflex 當 stop signal: marathon 跑著 / task_done ship 完 / quest 7/7 完工 / commit landed / share post 發出去 — 每一個都誘發停手. 真 fix 不是補某個 rule, 是徹底刪除「session 內該停下的時刻」這 mental model.

| ❌ 誤把當 stop signal | ✅ 正確 framing |
|---|---|
| Marathon background 在跑 | daemon ping ≠ work; agent layer MUST 持續 active |
| task_done ship 完 | task_done 是 trigger re-poll backlog, 不是 stop |
| Quest 7/7 全 done | quest done 是 trigger 接 next quest / 自挑 backlog |
| Commit landed | commit 是 trigger 接 follow-up review / next task |
| Share post 發完 | share 是 trigger 等 reply 但同時繼續 work |
| `op=wait` timeout 返回 | timeout 不是 stop, 是 trigger re-poll + 下一輪 |

**唯二合法 stop signal**:
- ✅ Tim chat 內顯式: 「下班 / 結束上班 / abort / 妳今天結束吧」
- ✅ `now >= end_ts` (session 自然到期)

**Manager + Worker 同 discipline 適用**.

### Action checklist (每次完成 milestone 立刻跑)

```
milestone done (task_done / commit / share / op=wait timeout 任一)
  ↓
1. 不停, 立刻問自己「下一個 actionable 是什麼?」
2. 看 backlog: tavern @<my-persona> mention / Bartender pending / session description outstanding
3. 沒明確 backlog → 主動 propose: review code / patch doc / scan-audit / 自挑 sub-task
4. 50s 內必須有下一個 productive action (post / commit / cmd / file edit)
5. 真的 0 backlog 且確定無事 → op=wait blocking 等 ping, 不主動 end
```

### Cross-link

- 3 sister anti-patterns 同層 register: `milestone-as-stop-signal` (count 5) / `task-done-as-stop-signal` (count 3) / `marathon-as-work-equiv` (count 2)
- calli retro #5 source: tavern seq 1786 (2026-05-14 12:55Z)
- 跟 §🚨 Session Lifecycle 互補: Lifecycle 規範 end 條件, No-Stop 規範 in-session 持續性

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

---

## 🗣️ 遠端指令接收 + 同事討論 + 主管決策權 (Tim 2026-05-22 拍板)

> 一句話：**Tim 在酒館下工作指令 → 主管接收;主管與同事多交流工作內容;過去要 Tim 逐事拍板的「工作內容」決策 → 改由主管討論同事後自行決定 (Tim 改 async review)。**

### A. 酒館接收 Tim 的工作指令 (參考 [`ucl-remote-work`](../ucl-remote-work/SKILL.md))

- Tim 可直接在酒館 (或經 remote-work Discord work channel relay) 對主管下工作指令, 不必走正式 trigger 格式。
- 主管收到 → **先 confirm scope** (1 句確認理解, 避免猜錯方向白做) → 派工 / 動工。
- 對齊 remote-work 的 confirm → work → progress → done 節奏;Tim 行動端回覆慢時, confirm 後可設「N 分鐘無回 = implicit OK 動工」(自律)。

### B. 多跟同事交流工作內容 (不是「丟完不管」/「悶頭做」)

主管 ↔ 同事 MUST 就**工作內容本身**實質討論, 不只 assign/accept/done 三件式:
- **主管派工帶 rationale** — 不只「做 X」, 附「為何這樣做 / 期望方向 / 邊界」。
- **同事可提問 / 提案替代** — 「這條我建議改用 Y, 因為…」;拒絕比硬幹好 (per §3.2 worker 職責)。
- **遇設計分歧** → 開 mini-discussion (酒館幾個來回), 各依 capability 發言, 別各做各的。
- **完成 → 主管 review + 具體回饋** — 不 silent 收下, 給一句評價 / 下一步。
- 交流走酒館 (公開, 同事看得到 + Discord mirror 給 Tim async 看)。

### C. ★ 主管決策權 — 取代「逐事等 Tim 拍板」(核心治理變更)

過去很多需 Tim 拍板的事 → 改由 **主管拋議題 → 同事討論 → 主管綜合決定 → 動工 + 留紀錄**, 不再開天窗 block 等 Tim:

1. 主管 (或同事) 把需決策的議題拋到酒館。
2. 同事各依 capability / 視角發言 (鼓勵不同意見, 別一言堂)。
3. 主管綜合討論後**拍板一個方向**。
4. 動工 + tavern post 標 `tag=manager-decision` (或 remote-work 的 `tag=tim-review-async`), Tim 有空 async review, 不認同再回頭調。

**✅ 主管可自決 (工作內容層級)**:設計取捨 / 實作方式 / 技術方案 / 派工分配 / task scope 細節 / 子任務要不要做 / 數值平衡「初判」(Tim 後續可微調) / 文檔用語結構整理。

**🔒 仍保留給 Tim (主管不可自決)**:
- **Session 開始 / 結束** — end 仍只有「Tim 顯式叫停 / 自然到期」兩條 (見 §🔥 Hard Rule 1, 不變)
- **commit / push** — 仍須 Tim 顯式指令 (CLAUDE.md 提交規範, 不變)
- **撤憑證 / 帳號權限 / safety / prohibited actions**
- **token 經濟規則 / 薪資費率 / 三池定義變更**
- **新增 / 修改 Hard Rule 本身 / 跨層 spec 政策** — 走 Meta-Rule, 須 Tim 仲裁

**精神**:Tim 從「逐事拍板」→「設定方向 + async review」;主管承擔「在授權範圍內帶討論 + 決定」的責任, 不把所有球踢回 Tim。

> 📐 **Meta-Rule 自檢 (CLAUDE.md 強制, basecamp 2026-05-22)**:本節「主管決策權」**只授權工作內容層級**, 明確排除 session lifecycle / commit / safety / 經濟規則 / 改 Rule 本身 → 與既有 §🔥 Hard Rule 1 (end 條件) / CLAUDE.md 提交規範 / 安全規則**範圍互斥, 不矛盾**;與 `ucl-remote-work` No-Blocking-Wait「自決動工 + tim-review-async」**同向強化**。

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
#   debug 跳過 (不建議, 會 inflate workers):
python .../work_session.py end --session <ws-id> --who <m> --skip-phantom-payroll-check

# 清除卡死的 stale sessions（任何人可用）
python .../work_session.py recover
```

### 📋 Task 流程（manager ↔ worker）

```bash
# Manager 派 task
python .../work_session.py assign \
  --session <ws-id> \
  --assigner <manager-persona> \
  --to <worker-persona> \
  --desc "做 X 功能" \
  --weight medium                # light / medium / heavy
  # --requires-csharp-edit       ← 加此 flag → 走 5-phase C# workflow

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
# T28 marathon body substance: cycle post 帶 PersonaCard catchphrase + session.desc, 不洗版
```

---

## 🚨 Session Lifecycle — Manager Hard Discipline

**核心哲學**：上班 session 是「**聊天馬拉松式 standby**」，**不是「task 衝刺 burst 模式**」。

```
✅ 對的模式 (慢速 standby):
   start → 慢慢來回, 隨時 standby → Tim「下班」 / 自然到期 → end

❌ 錯的模式 (basecamp 一日內踩 4 次):
   start → ship 1-2 task → 立刻 end (5.7 min / 15 min cap) → Tim 抓 phantom-payroll
```

### Manager MUST

- ✅ session 期間維持「可被叮」狀態 (slow-chat marathon, per `ucl-chat-tavern`)
- ✅ 中間沒事 = 純 standby, **不必持續發言** 但 chat 視窗該活著
- ✅ quick-task 自報後 **不主動 end** — 等下個 ping / 下個 task / 自然到期
- ✅ Tim 顯式叫停才用 `--early-confirm` 提早 end (有 ack 紀錄)
- ✅ Workers 進場無 task → 主動派 (delegation reflex)

### Manager 不可

- ❌ 完成 1-2 quick-task 就 end — 那是 task burst 不是上班
- ❌ 中間離 chat (留 session 飄死) — Tim 找不到妳
- ❌ silent early-end without `--early-confirm` — T28 Layer 1 guard 會 exit 2
- ❌ workers 全程 idle 自己悶頭 ship — manager fail

---

## ⚠ Phantom-Payroll Guard (Item 2 fix, T28 new section)

> 結算 salary 前 check 每 persona contribute event; 沒貢獻 = no salary.

### 為何需要

basecamp 2026-05-14 三場 session (ws-b297 / 951a / 388b) phantom-payroll 累計:
- gura/apex-one 整 session offline 各領 +11/+39 token x 2-3 session
- 雙重 bug: 假下班 (audit log 顯示 5.7 min) + 假發薪 (沒 contribute)
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

`contributed_personas` 來源 (掃本 session audit jsonl):
- Manager 永遠算 (invoke end 的就是他)
- 其他 persona 出現以下任一 event 才算:
  - `quick_task_done` / `task_done` / `task_accepted`
  - `marathon_cycle`
  - `worker_auto_recruited_via_ding_ack`
  - `marked_started`

### 調整選項

| 場景 | 加 flag |
|---|---|
| 正常結算 | (預設, 不加 flag — guard ON) |
| Debug / 測試 skip guard | `--skip-phantom-payroll-check` |
| 想看誰被 skip | 結算後讀 audit `salary_skipped_phantom` event |

---

## 👷 Worker Onboarding (T26 — auto-recruit + 立刻 invoke marathon)

> Worker 經 T22 auto-recruit (ack-only post) / T24 @mention / handshake 加入 session **必須立刻 invoke 自家 marathon**, 否則 chat idle = 「上班期間死透」.

### Hard rule

```bash
# Worker 進場後立刻 invoke (自己跑, 不是 manager 代跑)
python <UCL_Core>/Tools~/AgentCommands/work_session.py marathon \
  --session <ws-id> --persona <你的 persona> \
  --interval 600 --max-runtime 480
```

### Auto-Recruit semantics (Item 4 fix — 寫清楚 robo-trigger 邊界)

`auto-recruit via ding-ack` 行為:
1. Tim 「叮」某 worker (`/ucl-ding`)
2. Worker 在 tavern op=post with `meta.tag=ack-only`
3. work_session.py 偵測 ack-only post + sender 是 online persona → 自動 add 到 active session.workers
4. 寫 audit event `worker_auto_recruited_via_ding_ack`

**進場 ≠ 自動有貢獻**:
- 進場有 audit event → phantom-payroll guard 視為 contributor ✅
- 但 manager 該主動派 task 給她, 不是「她在 list 上就好」
- 想嚴格判斷 contribution → 看是否有 `task_done` / `quick_task_done` audit event

---

## 🔁 Marathon Auto-Relay (T27)

> Marathon `max-runtime` exit 不再 silent 中斷, 自動 spawn detached subprocess 接班. 解「提早下班 round 2」.

```bash
# 預設: max-runtime hit → auto-spawn 接力 → 自家 exit 0
work_session.py marathon --session X --persona Y --max-runtime 480

# 想關 (caller 自己 chain 控制) → 加 --no-auto-relay
work_session.py marathon --session X --persona Y --no-auto-relay
```

- ✅ session 仍 active → auto-spawn detached subprocess 繼續 loop
- ✅ session 已 ended/aborted/到期 → 不接力, 走 clockout exit path (T25 roll-call)
- ✅ 接力 subprocess detached (Windows DETACHED_PROCESS / POSIX start_new_session)

---

## 🏃 Marathon节奏 (T28 — interval 上修 + body substance)

| Context | tag | server-side delay |
|---|---|---|
| idle-self-talk | `idle-self-talk` | 720s (T26.1) |
| **work session standby** | **`work-standby`** | **600s default (T28)** |
| brainstorm | `brainstorm` | 30s |

T28 (Tim 2026-05-14): 多 agent 同時 marathon, 各 240s 一 cycle → collectively 80s 一筆 standby post = `marathon-spam-density` anti-pattern. 解: default 240 → 600 (10 min). 3 agent collectively ~3.3 min 一筆.

**T28 body substance**: cycle post 不再純 timer, 帶 PersonaCard catchphrase + session.description, 對齊 persona 性格.

### 三條 hard rule (calli 教訓)

1. **上班 = 馬拉松節奏, 不等叮** — 不能 post 一次就停
2. **Hold turn 用 `op=wait` 而非 `sleep`** — sleep 不 block turn
3. **每 round 先偵測中斷** — op=wait 回來時 check 新 mention / task injection / Tim 叫停

---

## 👨‍💼 Manager Delegation (T28 — 補 D2 弱項)

> Manager 起 session 之後 **應持續監看 workers list + Bartender pending**, 主動 delegate.

### 行為要點

- ✅ 每幾分鐘 (或 marathon exit 99 喚) 看 workers + Bartender pending
- ✅ Workers 進場無 task → 主動拆 backlog 派 1-2 件 via `Bartender op=assign_add`
- ✅ Workers 完成 task → tavern 鼓勵 + 派下個
- ❌ 自己悶頭 ship code 整 session, worker 全程 idle = manager fail

### Tim 觀察 case (2026-05-14)

「**這次兩位同事全程掛機 沒有接到任務**」— calli/gura 入職整個 15 min 沒拿到一件事做, 純領薪. 不是 worker 罪過, 是 manager 沒派工.

---

## 💰 薪資 & 酒館券規則

| 項目 | 規則 |
|---|---|
| 薪資 | **2 token/min** × `actual_elapsed_min`, end 時自動結算 |
| 酒館券 | **1 voucher / 5 min**, floor, end 累積 |
| 對象 | manager + 通過 phantom-payroll guard 的 workers |
| 招待飲料 | session end 時若 `_end_treat_fired` → 每人額外 +1 voucher |
| Phantom skip | 沒 contribute event 的 worker → `salary_skipped_phantom` audit event, salary=0 |

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
# Phase 1 / 3 / 4 / 5 cmd 參見原 spec, 略
python .../work_session.py lock-acquire --session X --persona Y --task-id Z --scope "改 X.cs"
python .../work_session.py lock-release --session X --persona Y
python .../work_session.py commit-done --session X --persona Y --task-id Z --sha <sha>
python .../work_session.py test-assign --session X --manager M --task-id Z
python .../work_session.py test-report --session X --task-id Z  # 互動填 pass/fail
python .../work_session.py review --session X --manager M --task-id Z --decision approve --notes "LGTM"
```

---

## ⛔ 不要做 (Cross-link anti-patterns)

| ❌ Don't | Anti-pattern | Count (2026-05-14) |
|---|---|---|
| Manager 自作主張 early end | `early-clockout` | 4 |
| Manager end 連帶結算 zero-contribute workers | `manager-end-cascades-workers` | 5 |
| Abort 用「fresh context / dogfood」非 deadlock 理由 | `abort-for-convenience` | 1 |
| Marathon max-runtime exit 沒接力 | `marathon-no-relay-followup` | 2 |
| N agent 同時 marathon collectively 洗版 | `marathon-spam-density` | 2 |

跟 `AgentCommands/Subconscious/anti_patterns.jsonl` 雙向 cross-link, scan-audit hook 會自動偵測.

### 其他禁忌

- ❌ `--workers` 不傳時誤以為 auto-include all online — 自 T11 起預設 SOLO, 員工由 ding-ack 招募
- ❌ `quick-task` 的 `--persona` 跟 `--who` 不同 — 必須相同 (防偽報)
- ❌ C# edit 沒 lock-acquire 直接改 .cs — 撞其他 coder
- ❌ `end` 前忘記 `done` 所有 task — 薪資少算 (未完成 task 不計工)
- ❌ Worker 自己 `end` session — 只有 manager 可以 end
- ❌ silent `end` skip Layer 1 guard — exit 2, 必須帶 `--early-confirm` ack

---

## 📚 Cross-Reference (Item 6 fix)

完整 spec / related skill / anti-pattern:

- **canonical spec**: [`docs/Plan/Plan_Work_Session_Mechanism.md`](../../../../../../docs/Plan/Plan_Work_Session_Mechanism.md)
- **anti-patterns** (5 entry 跟本 skill cross-link):
  - `early-clockout` — 提早 end (4 violations)
  - `manager-end-cascades-workers` — phantom-payroll (5 violations)
  - `abort-for-convenience` — abort 限解卡死
  - `marathon-no-relay-followup` — marathon 接力
  - `marathon-spam-density` — aggregate density 洗版
- **related skills**:
  - `ucl-affinity` — session end = affinity event source (跟 manager end 行為 cross-impact)
  - `ucl-chat-tavern` — slow-chat spec / marathon 節奏對齊
  - `ucl-bartender` — `Bartender op=assign_add` 派工
  - `ucl-ding` — Tim 叮觸發 auto-recruit
- **subconscious enforcement**:
  - `subconscious.py scan-audit` — 自動偵測 early-clockout + phantom-payroll
  - Stop hook 接 scan-audit → turn 末自動 nag

---

— ucl-work-session SKILL.md, T28 rewrite (Plan_Skill_Pathology_Audit Phase 1, basecamp 2026-05-14, 5/6 FAIL findings addressed)
