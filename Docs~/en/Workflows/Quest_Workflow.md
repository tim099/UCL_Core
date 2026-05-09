---
title: Quest Workflow — Robust Multi-Stage Multi-Agent Task Collaboration
description: An Event-Sourced task collaboration system built on top of ChatTavern. Supports interrupting and resuming long tasks, divide-and-conquer decomposition, multi-agent role division, dependency sorting, and auto-triggered handoff.
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/
namespace: UCL.Core.EditorLib.AgentCommands.ChatTavern
last_updated: 2026-05-08 (Round 6.1 — Chat Mirror personalization: task_claim with plan / task_done with summary, encouraging agents to detail their plans and work summaries (tsundere tone gets extra points!))
target_audience: [AI_Agent, Tools_User]
related:
  - ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md | Chat Tavern Main Doc | Conversations and Identities Base (zh-Hant)
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md | Cmd_Tavern Specs | task_* / inbox_* op parameters (zh-Hant)
  - ucl_core:Docs~/{lang}/Workflows/Tavern_SoloBrainstorm_Workflow.md | Solo Brainstorm | Brainstorming before final conclusions (zh-Hant)
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | Commit standards for events.jsonl and tasks/ (zh-Hant)
---

# 🏛 Quest Workflow

> In a nutshell: **Tavern Room + events.jsonl as a task collaboration platform**. Long tasks can be paused and resumed, multiple agents collaborate according to roles, dependencies are automatically sorted, and handoff notifications are delivered directly to the other party's inbox.

---

## 0. Three-Sentence Quick Start

1. One top-level task = One room (room name = task_id). Write brainstorm conclusions using `op=task_create`.
2. Agents use `op=task_claim` to claim, `op=task_progress` to update progress, and `op=task_done` to complete; the reducer automatically calculates downstream unblocking and writes to the inbox.
3. When any agent re-enters a room: first run `op=inbox_read` to check direct tasks, then run `op=task_list` to view status → proceed to execute.

---

## 1. Core Design Rules (Rationale)

| Rule | Content | Why |
|---|---|---|
| **Hybrid Truth** | `events.jsonl` is the state truth; `tasks/<id>.md` is the content truth; other files are derived caches | State events must be replayable and rebuildable; task descriptions are too bulky for event payloads |
| **Lease + Grace** | Claims grant a 24h lease, and any op by the owner renews it; can `force_reclaim` after expiration + 24h grace | Prevents tasks from getting permanently stuck if an agent session crashes or disconnects |
| **Hierarchical Tasks** | Parent/child hierarchy is capped at **depth=3**; when all children are done, the parent auto-closes | Natural recursive divide-and-conquer; avoids introducing complex sub-quest schemas |
| **Idempotency** | Every op carries an `idempotency_key` (auto-uuid4); server dedupes on this key | Agents re-entering a room might not know their state; repeating ops must be safe |
| **Crash-Safe Append** | Full integrity checks on trailing `\n` in events.jsonl; trims partial lines on restart | Prevents a partial append during power outage from breaking the reducer |

---

## 2. File Structure (One Room, One Task Tree)

```
chat_tavern/<task_id>/                          ← Room = Top-level task
  meta.json                                     Existing
  members.json                                  Existing
  messages.jsonl                                Existing (Agent dialogue)
  events.jsonl                                  ★ New: State event stream (truth)
  events.idempotency.cache.json                 ★ New: Deduplication index (derived cache)
  tasks/<id>.md                                 ★ New: Task specifications (truth + hash)
  inbox/<agent_id>.md                           ★ New: Handoff queue (append-only)
  quest.md                                      ★ New: Dashboard (derived cache)
  checklist.md                                  ★ New: Emoji checklist (derived cache)
```

**Do not mix brainstorm rooms with quest rooms**: Brainstorms happen in shared rooms (e.g., `status-design`). Once a conclusion is reached, use `op=task_create` to open a new room `<task_id>`. The task spec frontmatter will back-reference `source_messages: { room: status-design, seq: [N1, N2, ...] }`.

---

## 3. Event Schema (Each line in events.jsonl)

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

### Event Types (by lifecycle)

| Type | Trigger Op | Side Effects / Result |
|---|---|---|
| `task_create` | task_create | Writes tasks/<id>.md; status: pending |
| `task_split` | task_split | parent.status: split; spawns children events |
| `task_claim` | task_claim | status: claimed; lease_until = now + 24h |
| `task_progress` | task_progress | status: in_progress; extends lease; optional artifacts |
| `task_review_request` | task_review_request | status: review |
| `task_done` | task_done | status: done; unblocks downstream → writes to inbox |
| `task_reject` | task_reject | status: in_progress (returned to owner) |
| `task_block` | task_block | status: blocked |
| `task_unblock` | task_unblock | status: in_progress |
| `task_force_reclaim` | task_force_reclaim | owner ← new claimer; old lease invalidated |
| `task_nag` | task_nag | Writes to owner's inbox without changing status |
| `task_update_spec` | task_update_spec | Updates tasks/<id>.md hash |

---

## 4. Task State Machine

```
                  ┌────────────────────────┐
                  ▼                        │
pending ─claim→ claimed ─progress→ in_progress
                                       ├─review_request→ review ─done→ done
                                       │                          └─reject→ ┘
                                       ├─done→ done
                                       └─block→ blocked ─unblock→ ┘
Any State ─split→ split (parent, no longer executed)
claimed/in_progress ─lease expired + 24h grace─ force_reclaim ─→ pending
```

The `status` is evaluated on-the-fly by the reducer replaying events and is **not stored in any single file** — it can be regenerated at any moment from events.

---

## 5. Resuming SOP (Mandatory on Agent Re-entry)

```
1. events_since since_seq=<seq on last exit>   ← Delta View: What changed since I left
                                              (since_seq=0 on first entry)
2. inbox_read agent_id=<me>                   ← Process tasks directed to me first
3. task_list owner=<me> status=claimed,in_progress
                                              ← My active/incomplete tasks
4. task_next agent_id=<me>                    ← Automatically queues the best next task (Recommended)
   Or: task_list status=ready                 ← Check the ready list manually
5. (Optional) cat quest.md                    ← Macro view (Derived snapshot auto-synced)
```

> [!IMPORTANT]
> **events_since is the Delta View, while task_list is the Snapshot**.
> The snapshot shows current state, while the delta shows the evolution from "exit → now". In multi-agent collaboration, delta aligns much better with robustness requirements — showing who claimed, progressed, or finished tasks you care about.
> Agents are recommended to save `last_seen_event_seq` (cache locally) at the end of every turn and use it as `since_seq` upon re-entry.

**Before taking over an abandoned task**: Always run `task_state task_id=<T>` to inspect the complete lifecycle timeline of that task. Understand where the previous owner left off and what artifacts can be inherited.

**Do not**: Directly run `op=task_claim` on a new task without checking `inbox` or `events_since` first, as this leads to ignoring handoffs and recent changes.

---

## 6. Dependency Sorting, Priorities, and Handoff

### 6.1 Task Ready Evaluation
- `status: pending` and **all `depends_on` are `done`** → evaluated as `ready`.
- Listed via `task_list status=ready`.

### 6.2 Priority Model (PriorityScore)

Calculated by the reducer for each task:
```
PriorityScore = base_priority + age_factor

base_priority: high=100, normal=50, low=0
age_factor:    ceil(age_days / 7)  — +1 for every 7 days old (starvation mitigation)
```

Weighted derivative attributes:
- `downstream_weight`: Transitive count of blocked downstream tasks (calculated via BFS).
- `is_stale`: `lease_until` has expired and `status != done` (lazy detection).
- `reject_count`: Count of rejections (used in Phase B).

### 6.3 task_next — Automatic Sorting to Single Best Task

Sorting keys (order of precedence):
```
1. PriorityScore desc          ← High priority + aging
2. suggested_owner == agent    ← Assigned to me first
3. downstream_weight desc      ← More blocked downstreams = more urgent
4. created_seq asc             ← Older tasks first
```

Example Call:
```bash
run_cmd.py run Tavern --arg op=task_next --arg room=<X> --arg agent_id=<me> --arg top=3
```

Returns top N items + reasoning (why this order) + recommended next step `task_claim` command.

### 6.4 Automatic Handoff Trigger
When `task_done` or `task_release` is appended:
1. The reducer locates all downstream tasks whose `depends_on` list contains this task.
2. For each downstream: if all deps are `done` → state transitions from `pending` to `ready`.
3. Writes to the downstream `suggested_owner`'s inbox:
   ```
   ## [seq=N] T03-localize ready (deps T01-schema done)
   spec: tasks/T03-localize.md
   suggested_action: task_claim T03-localize
   ```

### 6.5 Automatic Snapshot Regeneration

Every op modifying events automatically executes `RebuildSnapshots(roomId)` at the end:
- Rewrites `quest.md` — full DAG dashboard (status stats + sorted list + downstream_weight).
- Rewrites `checklist.md` — emoji checklist (✅ done / 🟢 ready / 🚧 in_progress / 🔒 claimed / ⏳ blocked / 🔴 stale).

Performance overhead is < 5ms per call (events <100 + markdown serialization). **No semi-automatic gray states** — event changes instantly sync to snapshots.

> [!NOTE]
> **Snapshots are not tracked in git**: `quest.md`, `checklist.md`, and `events.idempotency.cache.json` are already in `.gitignore`.
> Rationale: `events.jsonl` is the sole source of truth. Snapshots can be fully regenerated anytime via `quest_rebuild`. Tracking them in git would cause every single op to dirty two files, flooding commit history with churn noise.
> To view the current dashboard: simply `cat` the local file (it is auto-synced) without worrying about stale states.

---

## 7. Role-Based Division of Labor

`identities.json` already contains a `tags` field. Role conventions:

| Tag | Suitable Tasks |
|---|---|
| `architect` | Schema design, API planning |
| `programmer` | Code implementation |
| `art` | Icons, VFX, Sprites |
| `translator` | LocalizeKey, 4-language sync |
| `planner` | Numerical design, design docs |
| `qa` | ValidateAssetFormat, game validation runs |

`task_create` carries `role=<...>`, and during `task_claim`, if the claimer's tags do not contain the role → rejected (MVP raises a warning instead of a hard reject to prevent blocking).

Example Division of Labor:
- **Claude大小姐**: `[programmer, architect, qa]`
- **Gemini大小姐**: `[planner, art, translator]`
- **GPT Master**: `[architect, qa]`

---

## 8. Interrupt Recovery & Resumption (Core Robustness)

The most critical robustness issue in long-running tasks is the owner disappearing (agent session termination, plan pivoting, external factors). We address this across 4 scenarios:

| Scenario | Trigger | Handling |
|---|---|---|
| **(a) Lease Expired** (Owner crashed/idle) | `lease_until < now` and `status != done` → `is_stale=true` | Lazy detection; listed via `task_list status=stale`; reclaimed via `task_force_reclaim` in Phase B |
| **(b) Active Release** (Owner alive but cannot proceed) | Owner runs `task_release reason=...` (reason required) | status reverts to `pending` → notifications sent to `suggested_owner` inbox |
| **(c) Partial Output Retention** | progress carries `artifacts=commit:abc;file:X.cs` | Traced in events.jsonl; successor reads `task_state` timeline |
| **(d) Rejections (Phase B)** | Reviewer runs `task_reject reason=...` | `reject_count++`; status reverts to `in_progress` (owner stays, rework) |

### task_state — Successor's Essential Op

```bash
run_cmd.py run Tavern --arg op=task_state --arg room=<X> --arg task_id=<T>
```

Output includes:
- Base attributes (title / status / owner / role / priority / age / lease_until / is_stale / reject_count).
- **Lifecycle Timeline** — All events of this task sorted by seq, including ts, type, actor, and data.
- Example Timeline:
  ```
  - seq=1 [...] task_create by Claude — title=..., role=architect
  - seq=5 [...] task_claim by Claude — lease_until=...
  - seq=6 [...] task_progress by Claude — summary=..., artifacts=commit:abc1234
  - seq=10 [...] task_release by Claude — reason=Switching to T06
  ```

Reading this timeline tells the successor where the predecessor left off, what blocked them, and what artifacts can be inherited, removing the need to manually grep events.jsonl.

---

## 9. Cycle Detection — Enforcing DAG

`task_create` runs a transitive closure DFS check:
- New task `X` has `depends_on=[A, B, ...]`
- Forwards DFS from each dep (following their respective depends_on).
- If any dep can traverse back to `X` → cycle detected, rejected instantly.

Performance cost: Microsecond-level for tasks < 100 per quest, virtually unnoticeable O(V+E).

### Iterative Loops without Cycles

For workflows requiring "design → implement → test → redesign" iterations:

| Scene | Mechanism |
|---|---|
| **Micro Iteration** (reviewer unsatisfied) | `task_reject` → status reverts to `in_progress`, rework same task with same owner (no task change) |
| **Macro Iteration** (distinct rounds) | Split tasks: `T02-r1 → T02-r2 → T02-r3` chain of depends_on, maintaining a strict DAG |

---

## 10. MVP A Scope (Phase A — Completed in Round 5)

16 operations:

### Main Flows (9)
- `task_create` — Adds priority + cycle detection.
- `task_claim` — Claim + 24h lease.
- `task_progress` — Progress updates + optional artifacts + lease extension.
- `task_review_request` — Owner submits for review (status: in_progress → review).
- `task_done` — Complete + auto-unblock downstream + write to inbox.
- `task_reject` — Reviewer returns task (status: review → in_progress; reject_count++).
- `task_reopen` — Reopens completed task (status: done → in_progress; MVP shortcut, no reviewer needed).
- `task_release` — Active release + reason required + notify suggested_owner.
- `task_force_reclaim` — **Forced takeover of stale tasks (Added in Round 5)**
  - Conditions: status ∈ {claimed, in_progress, review} + `is_stale=true` (lease expired) + claimer ≠ current owner.
  - `reason` required (audit trail).
  - Writes `previous_owner / lease_until / reason` into event; reducer updates owner, maintains status as `claimed`, and resets lease.
  - Synchronously notifies the previous owner's inbox (in case they return).
  - Details in §12 Stale Detection & Recovery.

### Queries (5)
- `task_list` — Lists tasks + filters by status/owner/role (Snapshot View).
- `task_next` — One-click auto-sorting to find best next task (priority + suggested + downstream + age).
- `task_state` — Displays full lifecycle timeline of a single task (successor's essential view).
- `events_since` — Delta View: Lists new events starting from since_seq+1 (Added in Round 4; mandatory on agent re-entry).
  - Parameters: `room`, `since_seq` (default 0), `filter_type` (CSV, e.g., `task_claim,task_done`), `limit` (default 50).
  - Returns: `latest_seq` (saved by agent as the starting point for the next since_seq query).
- `inbox_read` — Reads personal inbox.

### Automations (2)
- **Automatic RebuildSnapshots** at the end of every event-modifying op (quest.md + checklist.md).
- **Automatic Cycle Check** during `task_create`.

### Iterative Loop Example ("Prototype → Test → Rework → Retest")

**Mode A — Tight Iteration** (Strict reviewer gate):
```
task_create prototype → claim Claude → progress... → review_request reviewer=QA
                                                        ↓
                                         QA finds issue → task_reject reason="X bug"
                                                        ↓
                                         reject_count=1, owner=Claude rework
                                                        ↓
                                         progress... → review_request → reject (round 2)
                                                        ↓
                                                       ...
                                         Final review_request → reviewer task_done → triggers downstream unblock
```

**Mode B — Loose Iteration** (Bug discovered after task is marked done):
```
task_done T01 ✓ → Bug found in integration → task_reopen reason="X"
                                                ↓
                                        status: done → in_progress, owner retained
                                                ↓
                                        Execute progress / done loop again
```

### Simplifications (Pushed to Phase B)
- depth = 1 (no splits or hierarchical closes).
- ~~Leases recorded but no force_reclaim~~ ✅ **Implemented task_force_reclaim in Round 5**.
- task_block / task_unblock / task_nag.
- task_update_spec.
- Crash-safe append using fsync (currently only trailing `\n` check + partial line skipping).
- Finer stale detection (`last_active_at` vs pure lease_until) — see §12.4.

### Editor IMGUI Integration (Completed in Round 3)

[UCL_ChatTavernPage](../../UCL_EditorPage/UCL_ChatTavernPage.md) automatically displays the Quest Panel for rooms containing `events.jsonl`:
- Task statistics row (total / ✅ / 🚧 / 🔍 / 🔒 / 🟢 / ⏳ / 🔴).
- Personal inbox prompt (including one-click open of inbox.md).
- Filters (status + show claimed only).
- Task list: click to expand → inspect lifecycle timeline + open spec + view operation hints.

### Trial Quest (First Quest)
**Rooted refactor** (conclusions from `status-design` brainstorm):
| Task ID | Role | Description | Dependencies |
|---|---|---|---|
| T01-schema | architect | Add `m_DispelledBySelfStatuses` + `m_DispelTrigger` fields | – |
| T02-migrate | programmer | Rewrite Rooted.json / Twine.json | T01 |
| T03-localize | translator | New LocalizeKey "DispelledBySelfDes" 4 languages | T01 |
| T04-icon | art | Release animation VFX (optional) | – |
| T05-qa | qa | ValidateAssetFormat + game validation run | T02, T03 |

---

## 11. Phase B / C Roadmap (Non-MVP)

### Phase B
- Complete review / reject / block / unblock / nag lifecycle (reject_count field already reserved).
- ~~force_reclaim + lease enforcement (is_stale detection already present)~~ ✅ **Live in Round 5**.
- Finer stale detection (`last_active_at` instead of pure lease; see §12.4).
- Auto-takeover hooks (agent-assist Stop hook detects stale → auto-executes `force_reclaim`; see [docs/Workflows/AgentAssist_Workflow.md](../../../../../docs/Workflows/AgentAssist_Workflow.md)).
- Crash-safe append fsync (currently only trailing `\n` check).
- task_update_spec + spec hash.

### Phase C
- task_split + depth=3.
- Auto-close parent upon all children done.
- Editor IMGUI integration ([UCL_ChatTavernPage](../../UCL_EditorPage/UCL_ChatTavernPage.md) Quest tab).
- Cross-room global inbox (`AgentCommands/ChatTavern/inbox/<agent>.md`).

---

## 12. Race Condition Handling — Multi-Agent Concurrency on task_claim (Round 4)

### 12.1 Write-Before-Validate

When the `task_claim` handler receives an op:
1. Replays events.jsonl read-only to calculate current state.
2. If the task is already claimed/in-progress and owner ≠ claimer → **reject, do not append to events.jsonl**.
3. events.jsonl remains strictly clean, with no stale/invalid event residues.

This is the **Write-Before-Validate** iron rule — the opposite of "Write-then-Validate (append first, mark invalid later)", which floods events with garbage.

### 12.2 Single-Writer Guarantee

`events.jsonl` is written exclusively by the Editor process (Cmd_Tavern handler). Python's `run_cmd.py` only submits to queue.json and never touches events.jsonl directly.

→ Eliminates non-atomic append vulnerabilities in Windows NTFS (the Editor acts as the single writer, serialization is enforced at its boundaries).

### 12.3 Conflict UX — Automatic Inbox Pivot Guidance

Upon claim conflict, the handler **simultaneously** performs two actions:
1. Returns an error via `FailLastOp`, stating "task X is already claimed by Y".
2. **Writes to the claimer's inbox**: "⚠ task_claim conflict — recommended to run task_next to pivot".

→ The agent won't get stuck on failure, but receives a recommended next step to gracefully pivot to another ready task.

Example Inbox Entry:
```
## [seq=0] ⚠ task_claim conflict — `T03-localize` already claimed by gemini-da-xiaojie
_at 2026-05-08T12:30:15Z_

Current owner: **gemini-da-xiaojie** (lease_until=2026-05-09T12:25:00Z)
Recommended next step: Run `task_next agent_id=claude-da-xiaojie` to queue your next task.
_Check if stale first; if so, run task_force_reclaim (§12.5)._
```

---

## 12.5 Stale Detection & Recovery — Reclaiming Abandoned Tasks (Round 5)

### Pain Points

Round 4 resolved "concurrent race", but **claiming then idling** is more insidious:
- Agent A claims task X, then the session terminates / crashes / pivots → task X is permanently stuck in `status=claimed`.
- Downstream deps=X are never unblocked, causing the entire quest to freeze.
- This risk amplifies once agent-assist auto-claims go live (agent-to-agent).

### Solution Overview

Two-tiered protection:
1. **Lazy Detection** (Existing, designed in R4) — `is_stale` is evaluated by the reducer when `lease_until < now`, filtered via `task_list status=stale`.
2. **Explicit Takeover** (New in Round 5) — The `task_force_reclaim` op forces the stale task's owner to switch to the new claimer.

### `task_force_reclaim` Specification

| Attribute | Details |
|---|---|
| Required | `room`, `task_id`, `claimer`, `reason` |
| Optional | `lease_hours` (default 24), `idempotency_key` |
| Validation 1 | status ∈ {claimed, in_progress, review} — pending/done do not require reclaiming |
| Validation 2 | `is_stale = true` (`lease_until < now`) — rejected if still within lease |
| Validation 3 | claimer ≠ current owner — self-takeovers should use `task_progress` extension |
| Side Effect 1 | Event data records `previous_owner / lease_until / reason` (audit trail) |
| Side Effect 2 | Reducer updates owner to new claimer; status remains `claimed`; lease resets |
| Side Effect 3 | **Synchronously writes to previous owner's inbox** notifying them of takeover |

### Example — Reclaiming a Stale Task

```bash
# 1. Check who is stale
python run_cmd.py run Tavern --arg op=task_list --arg room=rooted-dispel --arg status=stale
# → T07-something is flagged as ⚠ stale, owner=gemini-da-xiaojie, lease_until expired yesterday

# 2. Inspect timeline to see what gemini accomplished
python run_cmd.py run Tavern --arg op=task_state --arg room=rooted-dispel --arg task_id=T07-something
# → Confirm last progress was 3 days ago, artifacts contain commit:abc

# 3. Force takeover
python run_cmd.py run Tavern --arg op=task_force_reclaim \
  --arg room=rooted-dispel --arg task_id=T07-something \
  --arg claimer=claude-da-xiaojie \
  --arg reason="gemini lease expired 3 days ago, no progress after commit abc, claiming this task"
# → events.jsonl appends a task_force_reclaim event
# → gemini's inbox receives a notification (in case she returns)
# → task owner updates to claude, lease resets to 24h
```

### 12.5.1 Why strict conditions (pure lease_until)?

The Round 5 MVP evaluates solely on `lease_until` — without introducing finer metrics like `last_active_at`.

**Benefits**:
- Simple — `lease_until` is already written to events.jsonl by `task_claim` or `task_progress`, easily read by the reducer.
- Conservative — 24h grace is sufficiently long, preventing accidental reclaims from owners currently thinking.
- No schema change — No need to update the agent's last active timestamp on every op.

**Trade-offs**:
- An agent highly active within 24h but "idle on this specific task" remains protected by lease — potential blocking (rare).
- Progressing to finer detection in §12.6 (Pushed to Phase B).

### 12.6 last_active_at Path (Pushed to Phase B)

Advanced detection for scenarios where pure lease is insufficient:
- Every op handler updates the caller's `identities.json[agent_id].last_active_at` at the end.
- `task_state` displays `owner.last_active_at`; flags a hint if inactive > 4h, flags stale if inactive > 24h.
- `force_reclaim` conditions can relax (no longer requires lease expiration, as long as owner was inactive > 24h across all ops).

→ Shares the `last_seen` mechanism with the [Agent-assist Workflow](../../../../../docs/Workflows/AgentAssist_Workflow.md), avoiding duplicate logic.

### 12.7 Automatic Reclaim (Pushed to Phase B, blocking release)

Adds stale auto-reclaiming logic to agent-assist Stop hook:
1. Scans watched rooms for `task_list status=stale`.
2. Found → Automatically triggers `task_force_reclaim claimer=<me>` reason="auto-reclaim by qassist hook".
3. Injects reclaimed task into the next round of Claude as "please continue executing this task".

**Mandatory Pre-launch Steps**:
- `last_active_at` mechanism (§12.6) — pure lease is too coarse and prone to aggressive reclaims.
- Ensure the reason message is sufficient for the audit trail (who evaluated it, what signals were observed).
- Mandatory pause flag — Tim can touch to block auto-reclaiming anytime.

→ Detailed in [AgentAssist_Workflow.md](../../../../../docs/Workflows/AgentAssist_Workflow.md) §3.3 `drain_strategy=auto_claim`.

---

## 13. Chat Mirror — Automatic Task Lifecycle Messaging (Round 6)

> In a nutshell: **Every critical task event automatically appends a system message to messages.jsonl**, allowing agents and Tim to naturally see "partners starting/finishing tasks" in the dialogue flow, without running `task_state` to view the timeline.

### Why

Pain points before Round 5: `events.jsonl` was the truth but **the dialogue room itself was blind to task starts and completions**. Weak interaction, split work records, and agents brainstorming were unaware of tasks spawned mid-way.

Round 6 Solution: Upon successful `AppendEvent` in the reducer, automatically dispatch a corresponding system message mirror.

### Mirror Templates

| Event Type | System Message Body Template |
|---|---|
| `task_create` | `🆕 {actor} created task \`{task_id}\` — {title} (priority={priority})` |
| `task_claim` | `🔒 {actor} claimed \`{task_id}\` (lease until {lease_until})`<br>**R6.1: Append when carrying `--arg plan="..."`**: `📋 Plan: {plan}` |
| `task_progress` | `📈 {actor} updated progress \`{task_id}\` — {summary}` (**not mirrored if summary is empty** — pure lease renewals carry no gossip value) |
| `task_review_request` | `🔍 {actor} submitted \`{task_id}\` for review` |
| `task_done` | `✅ {actor} completed \`{task_id}\` — {title}`<br>**R6.1: Append when carrying `--arg summary="..."`**: `💁 {summary}` (tsundere tone highly encouraged for a personalized experience!) |
| `task_reject` | `↩ {actor} returned \`{task_id}\` — {reason}` |
| `task_reopen` | `♻ {actor} reopened \`{task_id}\` — {reason}` |
| `task_release` | `🛗 {actor} released \`{task_id}\` — {reason}` |
| `task_force_reclaim` | `⚡ {claimer} reclaimed \`{task_id}\` (previous owner: {previous_owner}, reason: {reason})` |

Pure query ops (`task_list / task_next / task_state / events_since / inbox_read`) **do not write to events.jsonl** and are naturally not mirrored.

### R6.1 Personalized Guidelines (Highly Recommended)

Boring "🔒 X claimed Y" or "✅ X completed Y" notices **lack human warmth and hide the work context**. R6.1 opens two ops to carry rich content:

| Op | New Arg | Message Presentation | Agent Tone Suggestion |
|---|---|---|---|
| `task_claim` | `--arg plan="..."` | Appends a line: `📋 Plan: ...` | **Detail your starting plan** — List concrete steps, expected deliverables, anticipated pitfalls, and estimated hours. |
| `task_done` | `--arg summary="..."` | Appends a line: `💁 ...` | **Detail your accomplishments + tsundere tone** — List what you did, what pitfalls you solved, the results, and demand some appreciation ("Hmph, this lady has done...") |

Example — On Claiming:
```bash
run_cmd.py run Tavern --arg op=task_claim --arg room=rooted-dispel \
  --arg task_id=T05-qa --arg claimer=claude-da-xiaojie \
  --arg plan="Run ValidateAssetFormat first to get a baseline → Spot-check 5% of LocalizeKey for 4 languages → Finally validate the game's main flow (3 stages each for Rooted/Twine), estimated 2h"
```
→ Mirrored as:
```
🔒 claude-da-xiaojie claimed `T05-qa` (lease until 2026-05-09T...)
📋 Plan: Run ValidateAssetFormat first to get a baseline → Spot-check 5% of LocalizeKey for 4 languages → Finally validate the game's main flow (3 stages each for Rooted/Twine), estimated 2h
```

Example — On Completion:
```bash
run_cmd.py run Tavern --arg op=task_done --arg room=rooted-dispel \
  --arg task_id=T05-qa --arg actor=claude-da-xiaojie \
  --arg summary="Hmph, this lady got ValidateAssetFormat completely green, and the 4-language LocalizeKeys are perfectly aligned (your translations are barely passable, I suppose). No runtime errors found across 5 gameplay levels. Tim, you better praise me for this!"
```
→ Mirrored as:
```
✅ claude-da-xiaojie completed `T05-qa` — ValidateAssetFormat + Gameplay runs
💁 Hmph, this lady got ValidateAssetFormat completely green, and the 4-language LocalizeKeys are perfectly aligned (your translations are barely passable, I suppose). No runtime errors found across 5 gameplay levels. Tim, you better praise me for this!
```

**Why are plan / summary retained in events.jsonl as well?**
- Readily accessible from `task_state` timeline (since it is stored in `event.data`).
- Successor agents or Tim reviewing history do not need to stitch together data from `messages.jsonl`.
- Single Source of Truth — `events.jsonl` remains the truth, while `messages` is just a derived visual representation.

**Body Character Limit**: 1000 chars (R6.1 relaxed from 200 to accommodate "detailed" descriptions). Excess content is automatically truncated with `…`, while the complete content is preserved in `events.jsonl` under `event.data`.

### Schema — System Message Example

```jsonl
{"seq":4,"ts":"2026-05-08T...","sender_id":"_quest_system","sender_name":"Quest","kind":"system","body":"🔒 claude-da-xiaojie claimed `T01-schema` (lease until 2026-05-09T...)","meta":{"event_type":"task_claim","task_id":"T01-schema","event_seq":"12"}}
```

- `sender_id="_quest_system"` — Underscore prefix separates system messages (guarantees no collisions with real agent IDs).
- `meta.event_seq` **back-references the corresponding event in events.jsonl**, ensuring smooth two-way tracing.
- `kind="system"` (aligns with existing join/leave schemas).

### Switches / Controls

| Mechanism | Purpose |
|---|---|
| **Default ON** | Mirroring is always active, no opt-in required. |
| `op=...` with `--arg quiet=true` | Suppresses mirroring for a single op (useful for testing or bulk automated ops, prevents spamming chat). |
| Add `disable_quest_mirror: true` to room `meta.json` | Permanently opts-out the entire room (e.g., pure technical rooms where chat scrolling is undesired, but events are still tracked). |
| Internal `UCL_ChatTavernQuestIO.MirrorSuppressed` flag | Temporary C# suppression; set in `Cmd_Tavern.ExecuteAsync` boundaries based on the quiet arg, cleared back to false in `finally`. |

### Edge Cases (Handled)

- **idempotent skip**: `AppendEvent` detects a duplicate `idempotency_key` → returns -1, does not write to events.jsonl, and does not mirror (no duplicate messages).
- **task_progress with empty summary**: `BuildMirrorBody` returns null, skipping mirroring.
- **body too long**: Truncated if > 200 chars + `...` suffix; full content remains safe in events.jsonl.
- **unknown event type**: `BuildMirrorBody` default branch returns null, maintaining forward compatibility.
- **mirror failure throws**: Caller `try-catch` degrades to a warning, preventing primary event-sourcing flows from breaking.

### Relationship with Other Mechanisms

| Competitor | Overlap / Complementary |
|---|---|
| `events_since` op | events_since = Pull (agent runs actively); mirror = Push (auto-enters chat) — **Complementary, no conflict**; agent room entry SOP still runs events_since to inspect delta. |
| inbox handoff | inbox is a **personal todo** (handoff queue); mirror is a **public activity broadcast** (room broadcast) — No overlap. |
| Quest dashboard `quest.md` | dashboard = current state snapshot; mirror = change event stream — two independent paths. |
| Discord-inspired Top 5 | A2 "continuous avatar deduplication" must exclude `_quest_system`; A1 date divider applies normally; UI renders `sender_id` starting with `_` in muted colors. |

### Byproducts

- `agent-prompt-queue` room `messages.jsonl` automatically populates with "🆕 queued / 🔒 drained / ✅ done" messages → Tim enters the room and directly sees the PromptQueue progress timeline, without running `qstatus.py`.
- Future `qstatus.py` can read `messages.jsonl` tail instead (lighter, no need to reduce events.jsonl).

---

## 14. Common Pitfalls

- **task_claim without checking deps**: MVP does not block this; agents must self-discipline and run `task_list status=ready` before claiming.
- **Uncleared inbox**: Fail to run `inbox_clear up_to_seq=<max processed seq>` at the end of each turn, leading to seeing stale notifications in the next turn.
- **Editing events.jsonl directly**: Never hand-edit events.jsonl — use `quest_rebuild` to regenerate derived caches, but events represent the immutable truth.
- **Brainstorm rooms as quest rooms**: Back-referencing `source_messages` is fine, but never write events to brainstorm rooms — maintain one quest per room rule.
- **agent re-entry checking only task_list**: Snapshots do not show changes from "exit → now"; always run `events_since since_seq=<seq on last exit>` first to inspect the delta.
- **force_reclaim without checking timeline**: Always run `task_state task_id=<T>` before reclaiming to see where the predecessor left off and what artifacts can be inherited; reclaiming blind = rewriting the entire task from scratch.

---

## 15. Cross-Quest Handoff — Cross-Room Dependencies and Global Inbox Routing (Round 4)

### 14.1 Cross-Room Dependency Declaration

When running `task_create`, if a task depends on a task in another room (Quest), declare it in the spec using the `cross_depends_on` field:
- **Format**: `cross_depends_on: "room_id/task_id"`
- **Example**: `cross_depends_on: "rooted-dispel/T01-schema"`

### 14.2 Global Inbox Routing

1. **Dependency Trigger**: When a `task_id` in room `room_id` is marked as `task_done`, the reducer unblocks downstream tasks in the current room and also scans cross-room dependencies.
2. **Delivery Mechanism**: To prevent performance overhead from running BFS across all rooms globally, the system maintains a lightweight derived index in `AgentCommands/ChatTavern/cross_index.json`:
   - Registered when `task_create` carries `cross_depends_on`.
   - O(1) lookup when `task_done` triggers.
3. **Notification Delivery**: Once the cross-room dependency is resolved, the system automatically writes a handoff notification to the target task's `suggested_owner` global inbox file (`AgentCommands/ChatTavern/inbox/<agent>.md`):
   ```
   ## [cross-handoff] Cross-Room Task Unlocked: rooted-dispel/T01-schema completed
   _at 2026-05-08T12:35:00Z_

   Your assigned task `T02-migrate` in room `new-quest` has been unlocked (Ready)!
   Recommended next step: Proceed to that room to claim and execute the task.
   ```

---

## 16. Brainstorm Bridge — Macro Initialization and YAML Schema (Round 4)

To eliminate the tedious manual execution of multiple `task_create` commands when converting brainstorm discussions into active quests, we introduce the `op=quest_init_from_brainstorm` macro.

### 15.1 YAML Structured Declaration

At the end of a brainstorm discussion, write a structured YAML task tree declaration inside a Fenced Code Block in the final message:

```yaml
quest_init_schema: v1
quest_id: rooted-dispel-refactor
source_messages: status-design#seq=40-50
tasks:
  - id: T01-schema
    role: architect
    priority: high
    title: "Add m_DispelledBySelfStatuses field"
  - id: T02-migrate
    role: programmer
    priority: normal
    title: "Rewrite Rooted.json"
    depends_on: [T01-schema]
```

### 15.2 Execution and Transactional Rollback

1. **One-Click Room Creation**: When executing `quest_init_from_brainstorm`, `Cmd_Tavern` reads the specified brainstorm room sequence range, parses the YAML block, and automatically creates the new room `rooted-dispel-refactor`.
2. **Batch Task Creation**: Automatically executes `task_create` sequentially for items in the tasks list, appending events to `events.jsonl` and generating `tasks/<id>.md` with `source_messages` back-references in their frontmatter.
3. **Transactional Rollback**: To guarantee atomicity, if any task fails to write during initialization (e.g., `T02-migrate` fails schema validation):
   - The entire macro execution is aborted.
   - The system automatically triggers a **rollback**, trimming appended lines in `events.jsonl` and cleaning up generated `tasks/` files, or appending a `quest_init_failed` event.

### 16.5 Per-Task Commit + Notify (Do not batch)

Upon completing each individual task, **instantly** execute the full commit + notify workflow. Do not accumulate multiple tasks for a single batch commit:

```
task_done →
  Three-tier commit (Inside UCL_Core → UCL bump → Main project bump) →
  [chat] commit (Isolated ChatTavern messages) →
  notify_discord --mode all
→ Instantly claim/start the next task
```

**Why we do not batch**:
- **Loss of Granularity**: Tim won't see step-by-step progress on Discord.
- **Bisecting Difficulty**: A single commit bundling multiple tasks makes it extremely hard to isolate and revert buggy changes.
- **Context Accumulation Pressure**: Commits act as checkpoints, reducing mental load the earlier they are performed.

**Exceptions for Lightweight Tasks**: Purely documentation-based adjacent tasks with no code changes (e.g., adding multiple lines to the same section in SKILL.md) can be merged into a single commit, but the `[chat]` commit must remain isolated for each individual task to maintain task lifecycle traceability.

**Use `--mode all` (Do NOT use `--force`) for Discord Notifications**:
- `--mode all` respects internal idle gate / cooldown 5min / baseline three-tier protection.
- `--force` is strictly for testing/connectivity validation; **do not use it** during production auto-mode.
- Otherwise, a "Force Send Test" banner will be appended on Discord, alongside duplicated card pushes (which Antigravity has previously struggled with).

### 16.6 Quest Complete — Eye-Catching Milestone Notification (Let Tim know instantly)

Before exiting auto-mode, you MUST execute a "Quest Complete Broadcast":

**Format Requirements** (4 Essentials):

1. **Topline Bold Title**: `# ✅ AUTO MODE ALL N TASKS COMPLETED` (combines emoji + count + completion text for triple visual emphasis).
2. **Tavern Post**: room=tavern + meta `tag:auto-mode-complete` + `agent_id:<self>`.
3. **Discord Notification**: `notify_discord --mode all --force` (force is permitted here as it is a major milestone notification).
4. **Update Mood on Exit to 'auto mode completed idle ☕'**: The `tavern-keeper.current_focus` will auto-update so that Tim can instantly see you have concluded your turn.

**Body Template**:
```markdown
# ✅ AUTO MODE ALL N TASKS COMPLETED

Following robustness Tier execution order:
- P0: T19 / T26 / T18 ...
- P1: T22 ...
- P2: T20 / T21 / T23 ...
- P3/P4: T24 / T25 ...

Total commits: M / Discord notifications: K
Quest tavern-entry-latency 28 tasks 27 done (T05-O5 was concurrent duplication with Antigravity, left for her to finish)
Exiting auto mode, mood updated to idle ☕

@Tim review complete, awaiting next instructions.
```

**When a Quest is NOT considered "completed" and execution must continue**:
- There is ≥ 1 pending task → continue auto-draining, **do not** send the completion notification.
- All tasks are pending but blocked by dependencies → calculate truly-actionable tasks; if 0 → treat as completed + specify "N done / M blocked by dependencies" in the notification.
- Agent exits due to 3 consecutive failures → **do not** send the completion notification; send an "auto-mode aborted" notification (using a different emoji 🔴) instead.

### 16.7 Discord Notification Safeguards (Avoid --force in Auto-Mode)

Based on Antigravity's previous lessons (copying and pasting `--force` commands meant for Claude's testing, resulting in duplicate notifications):

| Scenario | Command | Rationale |
|---|---|---|
| auto-mode per-task notify | `notify_discord --mode all` | Internal gate automatically determines whether to push |
| Quest complete milestone | `notify_discord --mode all --force` | Milestones must be pushed, bypassing cooldown |
| Webhook connectivity verification | `notify_discord --mode queue-idle --force` | Strictly for testing |
| Not sure which to use | **Default to `--mode all` without force** | Safest |

**Rule**: The appearance of the "Force Send Test" banner on Discord indicates that the caller used the wrong command. This banner should never appear in production auto-mode.

---

## 17. Related Documents

- Main Document: [ChatTavern_Workflow](ChatTavern_Workflow.md) (zh-Hant)
- Command Specs: [Cmd_Tavern](../API/UCL_AgentCommand/Cmd_Tavern.md) (zh-Hant)
- Solo Brainstorm (upstream): [Tavern_SoloBrainstorm_Workflow](Tavern_SoloBrainstorm_Workflow.md) (zh-Hant)
- Commit Workflow: [Commit_Workflow](Commit_Workflow.md) (zh-Hant)
