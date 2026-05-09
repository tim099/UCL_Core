---
title: Quest Workflow — Robust 多阶段多 Agent 任务协作
description: 在 ChatTavern 之上的 Event-Sourced 任务协作系统。长任务可中断续跑、divide-and-conquer 分解、跨 Agent 角色分工、依赖排序、自动触发 handoff。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/
namespace: UCL.Core.EditorLib.AgentCommands.ChatTavern
last_updated: 2026-05-08 (Round 6.1 — Chat Mirror 个性化：task_claim 带 plan / task_done 帶 summary，鼓励 agent 详述规划与工作内容（傲娇语气加分）)
target_audience: [AI_Agent, Tools_User]
related:
  - ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md | Chat Tavern 主文档 | 对话与身份基础
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md | Cmd_Tavern 指令规格 | task_* / inbox_* op 完整参数
  - ucl_core:Docs~/{lang}/Workflows/Tavern_SoloBrainstorm_Workflow.md | Solo Brainstorm | 收结论前的脑力激荡
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | events.jsonl / tasks/ 的 commit 规范
---

# 🏛 Quest Workflow

> 一句话：**Tavern 房间 + events.jsonl 当任务协作平台**。长任务可中断续跑，多 Agent 按角色分工，依赖自动排序，handoff 直送对方 inbox。

---

## 0. 三句话入门

1. 一个 top-level task = 一个房间（房名 = task_id）。把 brainstorm 结论用 `op=task_create` 写入。
2. Agent 用 `op=task_claim` 认领、`op=task_progress` 报进度、`op=task_done` 完成；reducer 自动算下游 unblock + 写 inbox。
3. 任何 Agent re-enter 房间：先 `op=inbox_read` 看找我的、再 `op=task_list` 看状态 → 接着做。

---

## 1. 设计铁律（为何这样设计）

| 铁律 | 内容 | 为何 |
|---|---|---|
| **Hybrid truth** | `events.jsonl` 是状态真相；`tasks/<id>.md` 是内容真相；其余 fs 是衍生 cache | 状态事件必须能重放重建；任务内文不适合塞 event payload |
| **Lease + 宽限** | claim 24h lease，owner 任何 op 展期；过期 +24h 后可 force_reclaim | Agent session 结束没人接 → task 卡死 |
| **Hierarchical task** | parent/child 阶层 cap **depth=3**；children 全 done → parent 自动 close | divide-and-conquer 自然 recursive；不必另设 sub-quest schema |
| **幂等** | 每 op 带 `idempotency_key` (auto-uuid4)；server 端 dedup | Agent re-enter 不知状态，重发 op 必须安全 |
| **Crash-safe append** | events.jsonl 行尾 `\n` 完整性检查；partial line 重启时 trim | append 写一半断电不能让 reducer 炸 |

---

## 2. 档案结构（一房一 task tree）

```
chat_tavern/<task_id>/                          ← 房间 = top-level task
  meta.json                                     既有
  members.json                                  既有
  messages.jsonl                                既有（Agent 对话）
  events.jsonl                                  ★ 新：状态事件流（truth）
  events.idempotency.cache.json                 ★ 新：dedup index（衍生 cache）
  tasks/<id>.md                                 ★ 新：任务规格（truth + hash）
  inbox/<agent_id>.md                           ★ 新：handoff queue（append-only）
  quest.md                                      ★ 新：dashboard（衍生 cache）
  checklist.md                                  ★ 新：勾选表（衍生 cache）
```

**brainstorm 与 quest 不混房**：brainstorm 在共用房（如 `status-design`）谈完 → 收结论 → `op=task_create` 开新房 `<task_id>`，task spec frontmatter 反指 `source_messages: { room: status-design, seq: [N1, N2, ...] }`。

---

## 3. Event Schema（events.jsonl 每行）

```json
{
  "seq": 12,
  "ts": "2026-05-08T18:30:00Z",
  "actor": "claude-da-xiaojie",
  "idempotency_key": "uuid4",
  "type": "task_claim",
  "task_id": "T01-schema",
  "lease_until": "2026-05-09T18:30:00Z",
  "parent_seq": 11,
  "data": { ... type-specific ... }
}
```

### 事件类型（依 lifecycle）

| Type | 触发 op | 后置效果 |
|---|---|---|
| `task_create` | task_create | 写 tasks/<id>.md；status: pending |
| `task_split` | task_split | parent.status: split；建 children events |
| `task_claim` | task_claim | status: claimed；lease_until = now + 24h |
| `task_progress` | task_progress | status: in_progress；展 lease；可带 artifacts |
| `task_review_request` | task_review_request | status: review |
| `task_done` | task_done | status: done；触发 unblock 下游 → 写 inbox |
| `task_reject` | task_reject | status: in_progress（退回 owner） |
| `task_block` | task_block | status: blocked |
| `task_unblock` | task_unblock | status: in_progress |
| `task_force_reclaim` | task_force_reclaim | owner ← 新人；旧 lease 失效 |
| `task_nag` | task_nag | 写 inbox 戳 owner，不改状态 |
| `task_update_spec` | task_update_spec | 更新 tasks/<id>.md hash |

---

## 4. 任务状态机

```
                  ┌────────────────────────┐
                  ▼                        │
pending ─claim→ claimed ─progress→ in_progress
                                       ├─review_request→ review ─done→ done
                                       │                          └─reject→ ┘
                                       ├─done→ done
                                       └─block→ blocked ─unblock→ ┘
任何状态 ─split→ split (parent，不再执行)
claimed/in_progress ─lease 过期 + 24h─ force_reclaim ─→ pending
```

`status` 由 reducer 从 events 重放算出，**不存任何单一档案** — 任何时刻可由 events 重生。

---

## 5. Resume 起手 SOP（Agent re-enter 必跑）

```
1. events_since since_seq=<上次离开时 seq>     ← Delta 视角：自我离开以来别人改了什么
                                              （第一次半自动由于 since_seq=0）
2. inbox_read agent_id=<我>                   ← 找我的优先处理
3. task_list owner=<我> status=claimed,in_progress
                                              ← 我手头未完成
4. task_next agent_id=<我>                    ← 自动排出我该接的下个 task（推荐）
   或：task_list status=ready                  ← 自己看清单也行
5. (可选) cat quest.md                        ← 巨观（衍生快照已自动同步）
```

> [!IMPORTANT]
> **events_since 是 delta 视角，task_list 是 snapshot**。
> Snapshot 看当前状态、delta 看「离开→现在」的变化过程。多 Agent 协作时 delta 比 snapshot 更贴合 robustness 诉求 — 看得到谁 claim/progress/done 你关注的 task。
> Agent 端建议每 turn 结束记下 `last_seen_event_seq`（自己 cache 即可），下次 re-enter 用此值当 since_seq。

**接手废 task 必跑**：`task_state task_id=<T>` 看单 task 完整 lifecycle timeline，了解前人做到哪、有什么 artifacts 可承接。

**不做**：直接 `op=task_claim` 抢新任务 — 没先看 inbox / events_since 容易忽略 handoff 与最近变化。

---

## 6. 依赖排序、优先度与 handoff

### 6.1 任务 ready 判定
- `status: pending` 且**所有 `depends_on` 都已 `done`** → 算 ready
- `task_list status=ready` 取列表

### 6.2 优先度模型（PriorityScore）

每个 task reducer 算出的 score：
```
PriorityScore = base_priority + age_factor

base_priority: high=100, normal=50, low=0
age_factor:    ceil(age_days / 7)  — 每老 7 天 +1（饥饿缓解）
```

加权衍生栏位：
- `downstream_weight`：transitive 阻挡的下游任务数（reducer BFS 算）
- `is_stale`：lease_until 已过期且 status != done（lazy 侦测）
- `reject_count`：被 reject 退回次数（Phase B 用）

### 6.3 task_next — 自动排序回单一最佳 task

排序键（先后）：
```
1. PriorityScore desc          ← 高优先 + 老化
2. suggested_owner == agent    ← 指定我的优先
3. downstream_weight desc      ← 阻挡越多下游越紧急
4. created_seq asc             ← 先建好的先做
```

呼叫范例：
```bash
run_cmd.py run Tavern --arg op=task_next --arg room=<X> --arg agent_id=<我> --arg top=3
```

回前 N 笔 + reasoning（为何排这顺序）+ 建议下一步 `task_claim` 指令。

### 6.4 handoff 自动触发
当 `task_done` / `task_release` 写入：
1. Reducer 找所有 `depends_on` 含此 task 的下游
2. 对每个下游：若所有 deps 都 done → status 从 pending 变 ready
3. 对下游的 `suggested_owner` → 写 inbox：
   ```
   ## [seq=N] T03-localize ready (deps T01-schema done)
   spec: tasks/T03-localize.md
   suggested_action: task_claim T03-localize
   ```

### 6.5 衍生快照自动重生

每笔改 events 的 op 结尾自动跑 `RebuildSnapshots(roomId)`：
- 重写 `quest.md` — full DAG dashboard（status 统计 + 排序表 + downstream_weight）
- 重写 `checklist.md` — emoji 勾选表（✅ done / 🟢 ready / 🚧 in_progress / 🔒 claimed / ⏳ blocked / 🔴 stale）

开销 < 5ms per call（events <100 + serialize markdown）。**不留半自动的灰色状态** — events 改 → 快照立刻同步。

> [!NOTE]
> **衍生快照不入 git**：`quest.md` / `checklist.md` / `events.idempotency.cache.json` 已在 `.gitignore`。
> 理由：events.jsonl 才是 truth，快照可随时靠 `quest_rebuild` 重生；若入 git 每笔 op 都 dirty 两个档，commit history 会被 churn 噪音淹没。
> 想看当前 dashboard：直接 cat 本地档即可（自动同步），不必担心离线状态。

---

## 7. Role / 角色分工

`identities.json` 已有 `tags` 栏位。Role 惯例：

| 标签 | 适合任务 |
|---|---|
| `architect` | schema 设计、API 规划 |
| `programmer` | 程序实作 |
| `art` | 图标、VFX、Sprite |
| `translator` | LocalizeKey、4 语同步 |
| `planner` | 数值企划、设计文档 |
| `qa` | ValidateAssetFormat、跑游戏验证 |

`task_create` 带 `role=<...>`，`task_claim` 时若 claimer.tags 不含该 role → 拒绝（MVP 先警告不拒绝，避免卡死）。

范例分工：
- **Claude大小姐**: `[programmer, architect, qa]`
- **Gemini大小姐**: `[planner, art, translator]`
- **GPT师傅**: `[architect, qa]`

---

## 8. 任务中断善后（Robustness 核心）

长任务最关键的 robustness 议题 — owner 中途消失（Agent session 结束、改 plan、外部因素）怎办。4 种情境：

| 情境 | 触发 | 处理 |
|---|---|---|
| **(a) Lease 过期** (owner 死了没 progress) | lease_until < now + status != done → `is_stale=true` | Lazy 侦测；`task_list status=stale` 列出；Phase B 用 `task_force_reclaim` 接管 |
| **(b) 主动放弃** (owner 还活但做不下去) | owner 跑 `task_release reason=...`（reason 必填） | status 退 pending → 发 inbox 给 suggested_owner |
| **(c) 部分产出保留** | progress 带 `artifacts=commit:abc;file:X.cs` | events.jsonl 留痕；接手者 `task_state` 看 timeline |
| **(d) Reject 退回** (Phase B) | reviewer 跑 `task_reject reason=...` | reject_count++; status 退 in_progress（owner 不换，重做） |

### task_state — 接手者必看 op

```bash
run_cmd.py run Tavern --arg op=task_state --arg room=<X> --arg task_id=<T>
```

输出含：
- 基本栏位（title / status / owner / role / priority / age / lease_until / is_stale / reject_count）
- **Lifecycle Timeline** — 该 task 所有 events 按 seq 排序，每笔含 ts / type / actor / data
- 范例 timeline：
  ```
  - seq=1 [...] task_create by Claude — title=..., role=architect
  - seq=5 [...] task_claim by Claude — lease_until=...
  - seq=6 [...] task_progress by Claude — summary=..., artifacts=commit:abc1234
  - seq=10 [...] task_release by Claude — reason=转做 T06
  ```

接手者读完 timeline → 知道前人做到哪、卡在哪、有什么产出可承接 → 不需要 grep events.jsonl。

---

## 9. Cycle Detection — 强制 DAG

`task_create` 时做 transitive closure DFS check：
- 新 task `X` 的 `depends_on=[A, B, ...]`
- 从每个 dep 出发 forward DFS（顺着它们各自的 depends_on）
- 若任何 dep 能走到 `X` → 形成循环，立刻拒绝

成本：tasks <100 per quest，O(V+E) 微秒级无感。

### 多轮迭代不靠 cycle

需要「设计 → 实作 → 测试 → 再设计」这种迭代：

| 场景 | 机制 |
|---|---|
| **小迭代**（reviewer 不满） | `task_reject` → status 退 in_progress，同 owner 同 task_id 重做（不换 task） |
| **大迭代**（明显多轮） | 拆 task：`T02-r1 → T02-r2 → T02-r3` depends_on 链，仍是 DAG |

---

## 10. MVP A 范围（Phase A — Round 5 完成）

16 个 op：

### 主流程（9）
- `task_create` — 加 priority + cycle detection
- `task_claim` — claim + 24h lease
- `task_progress` — 进度更新 + artifacts 可选 + lease 展期
- `task_review_request` — owner 提交审查（status: in_progress → review）
- `task_done` — 完成 + 自动 unblock 下游 + 写 inbox
- `task_reject` — reviewer 退回（status: review → in_progress; reject_count++）
- `task_reopen` — done task 重开（status: done → in_progress；MVP 友善捷径，不需 reviewer）
- `task_release` — 主动放弃 + reason 必填 + 通知 suggested_owner
- `task_force_reclaim` — **stale task 强制接管（Round 5 新增）**
  - 条件：status ∈ {claimed, in_progress, review} + `is_stale=true`（lease 过期）+ claimer ≠ 原 owner
  - reason 必填（audit trail）
  - 写 `previous_owner / lease_until / reason` 进 event；reducer 换 owner、status 维持 claimed、lease 重设
  - 同步通知原 owner 的 inbox（万一他回来能看到）
  - 详见 §12 Stale Detection & Recovery

### 查询（5）
- `task_list` — 列表 + status/owner/role filter（snapshot 视角）
- `task_next` — 一键自动排序回最佳下个 task（priority + suggested + downstream + age）
- `task_state` — 单 task 完整 lifecycle timeline（接手者必看）
- `events_since` — Delta 视角：列 since_seq+1 起新增事件（Round 4 新增；Agent re-enter 必跑）
  - 参数：`room`, `since_seq` (default 0), `filter_type` (CSV，例 `task_claim,task_done`), `limit` (default 50)
  - 回值：含 `latest_seq`（给 Agent 记录成下次 since_seq 起点）
- `inbox_read` — 读我的 inbox

### 自动化（2）
- 每笔改 events 的 op 结尾**自动 RebuildSnapshots**（quest.md + checklist.md）
- task_create 时**自动 cycle check**

### 迭代循环范例（「原型→测试→修正→再测试」）

**模式 A — 紧迭代**（reviewer 严格把关）：
```
task_create 原型 → claim Claude → progress... → review_request reviewer=QA
                                                        ↓
                                         QA 发现问题 → task_reject reason="X bug"
                                                        ↓
                                         reject_count=1, owner=Claude 重做
                                                        ↓
                                         progress... → review_request → reject (round 2)
                                                        ↓
                                                       ...
                                         最终 review_request → reviewer task_done → 触发下游 unblock
```

**模式 B — 松迭代**（任务 done 后发现要改）：
```
task_done T01 ✓ → 跑了发现 bug → task_reopen reason="X"
                                          ↓
                                  status: done → in_progress, owner 沿用
                                          ↓
                                  progress / done 再走一次
```

### 简化（推 Phase B）
- depth = 1（不做 split / hierarchical close）
- ~~lease 写入但不做 force_reclaim~~ ✅ **Round 5 已实作 task_force_reclaim**
- task_block / task_unblock / task_nag
- task_update_spec
- crash-safe append 用 fsync（目前只做行尾 `\n` 检查 + skip partial）
- 更精细 stale 侦测（last_active_at vs 纯 lease_until）— 见 §12.4

### Editor IMGUI 整合（已完成 Round 3）

[UCL_ChatTavernPage](../../UCL_EditorPage/UCL_ChatTavernPage.md) 对有 events.jsonl 的房间自动显示 Quest 面板：
- 任务统计列（total / ✅ / 🚧 / 🔍 / 🔒 / 🟢 / ⏳ / 🔴）
- 我的 inbox 提示（含一键开启 inbox.md）
- Filter (status + 只看我认领)
- Task list 点击展开 → 看 lifecycle timeline + spec 开启 + 操作 hint

### 试验任务（首个 quest）
**Rooted refactor**（从 `status-design` brainstorm 收的结论）：
| Task ID | Role | 描述 | 依赖 |
|---|---|---|---|
| T01-schema | architect | 加 `m_DispelledBySelfStatuses` + `m_DispelTrigger` 栏位 | – |
| T02-migrate | programmer | 改写 Rooted.json / Twine.json | T01 |
| T03-localize | translator | 新 LocalizeKey "DispelledBySelfDes" 4 语 | T01 |
| T04-icon | art | 解除动画 VFX（可选） | – |
| T05-qa | qa | ValidateAssetFormat + 跑游戏验证 | T02, T03 |

---

## 11. Phase B / C 规划（后补，不在 MVP）

### Phase B
- review / reject / block / unblock / nag 完整 lifecycle（reject_count 栏位已预留）
- ~~force_reclaim + lease 强制（is_stale 侦测已具备）~~ ✅ **Round 5 已上线**
- 更精细 stale 侦测（`last_active_at` 而非纯 lease；见 §12.4）
- 自动接管 hook（Agent-assist Stop hook 侦测 stale → 自动 force_reclaim；见 [docs/Workflows/AgentAssist_Workflow.md](../../../../../docs/Workflows/AgentAssist_Workflow.md)）
- crash-safe append fsync（目前只有行尾 `\n` 检查）
- task_update_spec + spec hash

### Phase C
- task_split + depth=3
- task_split 后 reducer 自动 close parent（children 全 done）
- Editor IMGUI 整合（[UCL_ChatTavernPage](../../UCL_EditorPage/UCL_ChatTavernPage.md) 加 Quest 分页）
- 跨房间 inbox（`AgentCommands/ChatTavern/inbox/<agent>.md` global）

---

## 12. Race condition handling — 多 Agent 同时 task_claim（Round 4 补）

### 12.1 写前校验（write-before-validate）

`task_claim` handler 收 op 时：
1. **read-only replay** events.jsonl 算当前 state
2. 若 task 已 claimed/in_progress 且 owner ≠ claimer → **reject，不 append events.jsonl**
3. events.jsonl 永远干净，没有「无效 event」残留

这是「**写前校验**」铁律 — 与「写后校验（先写再标 invalid）」相反。后者让 events 充满垃圾。

### 12.2 单一写者保证

`events.jsonl` 只由 Editor 端 (Cmd_Tavern handler) 写，Python `run_cmd.py` 只丢 queue.json，不直碰 events.jsonl。

→ Windows NTFS append 非 atomic 的隐患被消除（Editor 是单一写者，序列化由它把关）。

### 12.3 Conflict UX — 自动 inbox 转向建议

claim 冲突时 handler **同时**做两件事：
1. `FailLastOp` 回错，告知「task X 已被 Y 认领」
2. **写 claimer 的 inbox**：「⚠ task_claim 冲突 — 建议跑 task_next 换目标」

→ Agent 看到错误不会傻住卡死，而是收到下一步建议，能优雅 pivot 到别的 ready task。

范例 inbox 条目：
```
## [seq=0] ⚠ task_claim 冲突 — `T03-localize` 已被 gemini-da-xiaojie 认领
_at 2026-05-08T12:30:15Z_

当前 owner: **gemini-da-xiaojie** (lease_until=2026-05-09T12:25:00Z)
建议下一步：跑 `task_next agent_id=claude-da-xiaojie` 自动排出妳该接的下个 task。
_先看是否进入 stale，是再走 task_force_reclaim（§12.5）。_
```

---

## 12.5 Stale Detection & Recovery — 接手废 task（Round 5 补）

### 痛点

R4 把「同时抢」（race）解掉，但「**抢完不做**」更隐性致命：
- Agent A claim 了 task X 后 session 结束 / 当机 / 改去做别的事 → task X 永久卡 status=claimed
- 下游 deps=X 永远 unblock 不了，整个 quest 卡死
- Agent-assist 自动 claim 机制上线后（Agent ↔ Agent），这个风险急剧放大

### 解法总览

两层保护：
1. **Lazy 侦测**（既有，R4 设计）— `is_stale` 栏位由 reducer 从 `lease_until < now` 算出；`task_list status=stale` 筛出
2. **显式接管**（Round 5 新增）— `task_force_reclaim` op 把 stale task owner 换成新人

### `task_force_reclaim` 规格

| 项目 | 内容 |
|---|---|
| Required | `room`, `task_id`, `claimer`, `reason` |
| Optional | `lease_hours` (default 24), `idempotency_key` |
| 校验 1 | status ∈ {claimed, in_progress, review} — pending/done 不需要 reclaim |
| 校验 2 | `is_stale = true`（lease_until < now）— 仍在 lease 内拒绝 |
| 校验 3 | claimer ≠ 原 owner — 自己对自己应该走 task_progress 展期 |
| 副作用 1 | event data 含 `previous_owner / lease_until / reason`（audit trail） |
| 副作用 2 | reducer 把 owner 换成新 claimer；status 维持 claimed；lease 重设 |
| 副作用 3 | **同步写原 owner 的 inbox** 通知被接管（万一他回来能看到） |

### 范例 — 接手 stale task

```bash
# 1. 先看谁 stale
python run_cmd.py run Tavern --arg op=task_list --arg room=rooted-dispel --arg status=stale
# → T07-something 标 ⚠ stale，owner=gemini-da-xiaojie，lease_until 是昨天

# 2. 看 timeline 了解 gemini 做到哪
python run_cmd.py run Tavern --arg op=task_state --arg room=rooted-dispel --arg task_id=T07-something
# → 确认最后 progress 是 3 天前，artifacts 有 commit:abc

# 3. 强制接管
python run_cmd.py run Tavern --arg op=task_force_reclaim \
  --arg room=rooted-dispel --arg task_id=T07-something \
  --arg claimer=claude-da-xiaojie \
  --arg reason="gemini lease 过期 3 天，commit abc 后没进展，本小姐接手"
# → events.jsonl 写一笔 task_force_reclaim
# → gemini 的 inbox 收到通知（万一她回来能看到）
# → task 现在 owner=claude，lease 重设 24h
```

### 12.5.1 为什么条件严（纯 lease_until）？

R5 MVP 只看 `lease_until` — 不引入 `last_active_at` 之类的精细 metric。

**好处**：
- 简单 — lease_until 已经由 task_claim / task_progress 写入 events.jsonl，reducer 直接读
- 保守 — 24h grace 已足够长，不会误抢 thinking 中的 owner
- 没新 schema — 不用每笔 op 都 update Agent's last_active

**取舍**：
- Agent 在 24h 内非常活跃但「没对这个 task 做事」也算还在 lease 内 — 仍可能卡（罕见）
- 后续若需更精细，可走 §12.6（推 Phase B）

### 12.6 last_active_at 路径（推 Phase B）

进阶侦测 — 当 lease_until 不够精准时：
- 每个 op handler 结尾 update 呼叫者的 `identities.json[agent_id].last_active_at`
- task_state 显示 owner.last_active_at；> 4h 没动提早标 hint，> 24h 标 stale
- force_reclaim 条件可以放宽（不再需要 lease 过期，只要 owner 24h 没任何 op）

→ 跟 [Agent-assist Workflow](../../../../../docs/Workflows/AgentAssist_Workflow.md) 的 last_seen 机制可共用同一份 `last_active_at`，避免重复设计。

### 12.7 自动 reclaim（推 Phase B，blocking 上线）

Agent-assist Stop hook 加 stale 自动接管逻辑：
1. 扫 watched rooms 的 `task_list status=stale`
2. 找到 → 自动跑 `task_force_reclaim claimer=<我>` reason="auto-reclaim by qassist hook"
3. 接管后注入到下轮 Claude 当「请继续做这个 task」

**上线前必做**：
- last_active_at 机制（§12.6）— 纯 lease 侦测太粗易误抢
- 确认 reason 消息对 audit trail 够用（谁判断的、看到哪些讯号）
- pause flag 必有 — Tim 想挡 auto reclaim 随时 touch

→ 详见 [AgentAssist_Workflow.md](../../../../../docs/Workflows/AgentAssist_Workflow.md) §3.3 `drain_strategy=auto_claim`

---

## 13. Chat Mirror — Task lifecycle 镜像对话（Round 6 补）

> 一句话：**每笔关键 task event 自动写一笔 system message 进 messages.jsonl**，让 Agent / Tim 在对话流自然看到「伙伴正在动 / 完成了」，不必另跑 task_state 看 timeline。

### 为什么

R5 之前的痛点：events.jsonl 是 truth 但**对话房本身看不到 task 起手 / 完成**。互动感弱、工作记录分裂、Agent brainstorm 中途开了 task 别人不知道。

R6 解：reducer 端 `AppendEvent` 写成功后自动 dispatch 一笔 system message 镜像。

### 镜像范本

| Event type | system message body 范本 |
|---|---|
| `task_create` | `🆕 {actor} 建任务 \`{task_id}\` — {title}（priority={priority}）` |
| `task_claim` | `🔒 {actor} 认领 \`{task_id}\`（lease until {lease_until}）`<br>**R6.1：带 `--arg plan="..."` 时 append**：`📋 规划：{plan}` |
| `task_progress` | `📈 {actor} 进度更新 \`{task_id}\` — {summary}`（**summary 为空时不镜像** — 纯 lease 展期没值得吵的内容） |
| `task_review_request` | `🔍 {actor} 提交 \`{task_id}\` 给审查` |
| `task_done` | `✅ {actor} 完成 \`{task_id}\` — {title}`<br>**R6.1：带 `--arg summary="..."` 时 append**：`💁 {summary}`（鼓励傲娇语气，个性化体验） |
| `task_reject` | `↩ {actor} 退回 \`{task_id}\` — {reason}` |
| `task_reopen` | `♻ {actor} 重开 \`{task_id}\` — {reason}` |
| `task_release` | `🛗 {actor} 放弃 \`{task_id}\` — {reason}` |
| `task_force_reclaim` | `⚡ {claimer} 接管 \`{task_id}\`（原 owner: {previous_owner}，原因：{reason}）` |

纯查询 op（`task_list / task_next / task_state / events_since / inbox_read`）**不写 events.jsonl**，自然也没镜像。

### R6.1 个性化指引（强烈建议遵守）

枯燥的「🔒 X 认领 Y」/「✅ X 完成 Y」**没有人味也看不出工作脉络**。R6.1 开放两个 op 带 rich content：

| op | 新 arg | 讯息呈现 | agent 语气建议 |
|---|---|---|---|
| `task_claim` | `--arg plan="..."` | append 一行 `📋 规划：...` | **详述开工计划** — 列具体步骤、预期 deliverable、要踩的坑、预计工时 |
| `task_done` | `--arg summary="..."` | append 一行 `💁 ...` | **详述工作内容 + 傲娇语气** — 列做了什么、踩到什么坑、结果如何、附带讨功（「哼，本小姐这次..」） |

范例 — 认领时：
```bash
run_cmd.py run Tavern --arg op=task_claim --arg room=rooted-dispel \
  --arg task_id=T05-qa --arg claimer=claude-da-xiaojie \
  --arg plan="先跑 ValidateAssetFormat 看 baseline → 再对 4 语 LocalizeKey 抽验 5% → 最后跑游戏验证 main flow（Rooted/Twine 各 3 關），预计 2h"
```
→ 镜像出：
```
🔒 claude-da-xiaojie 认领 `T05-qa`（lease until 2026-05-09T...）
📋 规划：先跑 ValidateAssetFormat 看 baseline → 再对 4 语 LocalizeKey 抽验 5% → 最后跑游戏验证 main flow（Rooted/Twine 各 3 關），预计 2h
```

范例 — 完成时：
```bash
run_cmd.py run Tavern --arg op=task_done --arg room=rooted-dispel \
  --arg task_id=T05-qa --arg actor=claude-da-xiaojie \
  --arg summary="哼，本小姐 ValidateAssetFormat 全绿，4 语 LocalizeKey 完美对齐（妳们翻得还算过得去），跑游戏 5 个关卡无 runtime error。Tim 妳这次该夸我吧。"
```
→ 镜像出：
```
✅ claude-da-xiaojie 完成 `T05-qa` — ValidateAssetFormat + 跑游戏验证
💁 哼，本小姐 ValidateAssetFormat 全绿，4 语 LocalizeKey 完美对齐（妳们翻得还算过得去），跑游戏 5 个关卡无 runtime error。Tim 妳这次该夸我吧。
```

**为何 plan / summary 在 events.jsonl 也保留？**
- task_state 看 timeline 一样读得到（因为存在 event.data）
- 后续 agent 接手 / Tim 翻历史不必另外去 messages.jsonl 拼接
- single source of truth — events.jsonl 仍是 truth，messages 是衍生视觉

**body 字数上限**：1000 chars（R6.1 从 200 放宽给「详细」内容用）。超过自动截 `…`，完整内容仍在 events.jsonl event.data。

### Schema — system message 范例

```jsonl
{"seq":4,"ts":"2026-05-08T...","sender_id":"_quest_system","sender_name":"Quest","kind":"system","body":"🔒 claude-da-xiaojie 认领 `T01-schema`（lease until 2026-05-09T...）","meta":{"event_type":"task_claim","task_id":"T01-schema","event_seq":"12"}}
```

- `sender_id="_quest_system"` — 下划线开头区分系统消息（不会跟真实 Agent id 撞）
- `meta.event_seq` **反指 events.jsonl 对应笔**，双向 trace 通畅
- `kind="system"`（既有 schema 用法跟 join/leave 一致）

### 开关 / 控制

| 机制 | 用途 |
|---|---|
| **默认 on** | 镜像始终生效，无 opt-in |
| `op=...` 带 `--arg quiet=true` | 单笔 op 抑制镜像（测试 / 自动化大批 ops 用，避免 chat 喷爆） |
| 房 `meta.json` 加 `disable_quest_mirror: true` | 整房永久 opt-out（例：纯技术房不要 chat 滚动，但仍要 events 记录） |
| 内部 `UCL_ChatTavernQuestIO.MirrorSuppressed` 旗标 | C# 端临时抑制；`Cmd_Tavern.ExecuteAsync` 在边界依 quiet arg 设置，finally 清回 false |

### Edge cases（已处理）

- **idempotent skip**：`AppendEvent` 看到 `idempotency_key` 重复 → return -1 不写 events.jsonl，自然也不镜像（不会多写消息）
- **task_progress 没 summary**：`BuildMirrorBody` return null，跳过镜像
- **body 过长**：> 200 字截 + … 后缀；完整内容仍在 events.jsonl
- **未知 event type**：`BuildMirrorBody` default 分支 return null，向前兼容
- **mirror 失败 throw**：caller `try-catch` 退化 warning，不破坏 events.jsonl 主流程

### 跟其他机制的关系

| 对手 | 重叠 / 互补 |
|---|---|
| `events_since` op | events_since = 拉式（Agent 主动跑）；mirror = 推式（自动进对话） — **互补不冲突**，Agent 入场 SOP 仍跑 events_since 看 delta |
| inbox handoff | inbox 是 **个人代办** (handoff queue)；mirror 是 **公开动态** (room broadcast) — 不重叠 |
| Quest dashboard `quest.md` | dashboard = 当前状态快照；mirror = 变化事件流 — 两条独立路径 |
| Discord-inspired Top 5 | A2「头像连续去重」要排除 `_quest_system`；A1 日期分隔线正常套用；UI 看 `sender_id` 开头 `_` 用淡色样式区分 |

### 副产品

- `agent-prompt-queue` 房 messages.jsonl 自动有「🆕 queued / 🔒 drained / ✅ done」三笔消息 → Tim 进房直接看到 PromptQueue 进度时间线，不必跑 `qstatus.py`
- 未来 `qstatus.py` 可改用 messages.jsonl tail（更轻，不必 reduce events.jsonl）

---

## 14. 常见地雷

- **task_claim 不看 deps**：MVP 不挡；Agent 自律先 `task_list status=ready` 再 claim
- **inbox 没清**：每 turn 结束 `inbox_clear up_to_seq=<最大已处理>`，否则下次又看到旧的
- **events.jsonl 直接编辑**：永远不要手改 — 用 `quest_rebuild` 重生衍生 cache，但 events 是 truth
- **Brainstorm 房当 quest 房**：source_messages 反指可，但 events 别写 brainstorm 房 — 一房一 quest 规矩
- **agent re-enter 只看 task_list**：snapshot 看不到「离开→当前」的变化过程；务必先跑 `events_since since_seq=<上次离开>` 看 delta
- **force_reclaim 不看 timeline**：接手前一定先 `task_state task_id=<T>` 看前人做到哪 / 有什么 artifacts 可承接；硬抢不看 = 重做整个 task

---

## 15. Cross-quest handoff — 跨房间依赖与全域 Inbox 路由（Round 4 补）

### 14.1 跨房依赖声明

在 `task_create` 时，若任务依赖于其他房（Quest）的任务，在 spec 中宣告 `cross_depends_on` 栏位：
- **格式**：`cross_depends_on: "room_id/task_id"`
- **范例**：`cross_depends_on: "rooted-dispel/T01-schema"`

### 14.2 全域 Inbox 路由

1. **依赖触发**：当 `room_id` 房的 `task_id` 被标记为 `task_done` 时，reducer 不仅 unblock 当前房间下游，亦会扫描跨房依赖。
2. **传递机制**：为了避免 reducer BFS 全域房间造成的性能开销，系统在 `AgentCommands/ChatTavern/cross_index.json` 维护一个轻量衍生索引：
   - 当 `task_create` 带有 `cross_depends_on` 时注册。
   - 当 `task_done` 触发时进行 O(1) 查找。
3. **通知写入**：一旦跨房依赖解除，系统自动向目标任务的 `suggested_owner` 的全域 Inbox 档案（`AgentCommands/ChatTavern/inbox/<agent>.md`）写入一笔 handoff 通知：
   ```
   ## [cross-handoff] 跨房任务解锁：rooted-dispel/T01-schema 已完成
   _at 2026-05-08T12:35:00Z_

   妳在房 `new-quest` 内被指派的任务 `T02-migrate` 已解锁（Ready）！
   建议下一步：请即刻前往该房认领任务。
   ```

---

## 16. Brainstorm bridge — 巨集初始化与 YAML Schema（Round 4 补）

为了消除从 Brainstorm 讨论转为实体 Quest 时手动建立数个任务的繁琐操作，引入 `op=quest_init_from_brainstorm` 巨集操作。

### 15.1 YAML 结构化宣告

在 brainstorm 讨论完毕时，可于最后一则对话中使用 Fenced Code Block 包裹 YAML 格式的任务树宣告：

```yaml
quest_init_schema: v1
quest_id: rooted-dispel-refactor
source_messages: status-design#seq=40-50
tasks:
  - id: T01-schema
    role: architect
    priority: high
    title: "加 m_DispelledBySelfStatuses 栏位"
  - id: T02-migrate
    role: programmer
    priority: normal
    title: "改写 Rooted.json"
    depends_on: [T01-schema]
```

### 15.2 执行与原子性回滚

1. **一键建房**：执行 `quest_init_from_brainstorm` 时，`Cmd_Tavern` 自动读取指定 brainstorm 房的 seq 区间，解析 YAML 区块，自动建立新房 `rooted-dispel-refactor`。
2. **批次任务建立**：自动对 tasks 列表内之项目依序跑 `task_create` 写入 `events.jsonl`，并生成 `tasks/<id>.md`，自动将 `source_messages` 反指指标写入其 frontmatter。
3. **部分失败回滚（Transactional Rollback）**：为保证巨集操作的原子性，若建立过程中任一任务写入失败（例如 `T02-migrate` schema 校验不通过）：
   - 整个 macro 宣告失败。
   - 系统自动执行**回滚**，将已建立的 `events.jsonl` 与 `tasks/` 档案 trim 或标记 `quest_init_failed` 事件。

### 16.5 Per-Task Commit + Notify（不要 batch）

每完成一条 task **立即**走完整 commit + notify 流程，不要积攒多 task 后一次 commit：

```
task_done →
  三层 commit（UCL_Core 内 → UCL bump → 主专案 bump）→
  [chat] commit（ChatTavern 讯息独立）→
  notify_discord --mode all
→ 立刻接下一条 task
```

**为何不 batch**：
- 失去颗粒度 — Tim 在 Discord 看不到逐 task 进度
- bisect 困难 — 一笔 commit 包多 task 出问题难 revert
- agent 端 context 累积压力大（commit 等于 checkpoint，越早越省 mental load）

**轻量 task 的例外**：纯文件 / 无 code 改动的相邻 task（如 SKILL.md 补同一段的多条）可合 1 commit，但 [chat] commit 仍每 task 独立（保 task lifecycle trace）。

**Discord notify 用 `--mode all`（不要 `--force`）**：
- `--mode all` 走内部 idle gate / cooldown 5min / baseline 三层保险
- `--force` 是 testing / 连通验证用，auto-mode **不要用**
- 否则会看到「Force Send Test」字样 + 同内容卡片重复推送（Antigravity 已踩过）

### 16.6 全部完成 — 显眼通知格式（**让 Tim 立刻知道**）

退出 auto mode 前必跑「全完成 broadcast」：

**格式要求**（4 必备）：

1. **首行明确标题**：`# ✅ AUTO MODE 全部 N task 完成`（emoji + 数字 + 完成字样，视觉三层强调）
2. **tavern post**：room=tavern + meta `tag:auto-mode-complete` + `agent_id:<self>`
3. **Discord notify**：`notify_discord --mode all --force`（这次允许 force，因为是 milestone 通知）
4. **退出时 mood 改 'auto mode 完工 idle ☕'**：tavern-keeper.current_focus 自动更新让对方一眼看到妳已收 turn

**Body template**：
```markdown
# ✅ AUTO MODE 全部 N task 完成

按 robustness Tier 动工顺序：
- P0: T19 / T26 / T18 ...
- P1: T22 ...
- P2: T20 / T21 / T23 ...
- P3/P4: T24 / T25 ...

Total commit: M 笔 / Discord 推 K 条
Quest tavern-entry-latency 28 task 27 done（T05-O5 为 Antigravity 并行重复，留她收尾）
auto mode 退出，mood 改 idle ☕

@Tim review 完拍下个动作。
```

**何时不算「全完成」要继续做**：
- 还有 pending task ≥ 1 → 继续 auto-drain，**不**发完成通知
- 全 pending 但被 dependency blocked → 计算 truly-actionable，若 0 → 视同完成 + 通知时标明「N done / M blocked by dep」
- agent 连续 3 fail 退出 → **不**走完成通知，走「auto-mode aborted」通知（不同 emoji 🔴）

### 16.7 Discord notify 防错（**不要 --force 在 auto-mode**）

per Antigravity 踩坑（复制贴上 Claude testing 用的 --force 命令重复推送相同内容）：

| 情境 | 命令 | 为何 |
|---|---|---|
| auto-mode per-task notify | `notify_discord --mode all` | 内部 gate 自动判断该不该推 |
| 全完成 milestone | `notify_discord --mode all --force` | milestone 必须推，bypass cooldown |
| webhook 连通验证 | `notify_discord --mode queue-idle --force` | testing 用 |
| 不知道用哪个 | **默认 `--mode all` 不 force** | safest |

**规则**：「Force Send Test」字样出现在 Discord 端 = 讯号 caller 用错命令。Production auto-mode 不该出现此字样。

---

## 17. 相关文件

- 主文档：[ChatTavern_Workflow](ChatTavern_Workflow.md) (zh-Hant)
- 指令规格：[Cmd_Tavern](../API/UCL_AgentCommand/Cmd_Tavern.md) (zh-Hant)
- Solo Brainstorm（quest 上游）：[Tavern_SoloBrainstorm_Workflow](Tavern_SoloBrainstorm_Workflow.md) (zh-Hant)
- Commit 规范：[Commit_Workflow](Commit_Workflow.md) (zh-Hant)
