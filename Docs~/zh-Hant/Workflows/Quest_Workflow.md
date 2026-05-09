---
title: Quest Workflow — Robust 多階段多 agent 任務協作
description: 在 ChatTavern 之上的 Event-Sourced 任務協作系統。長任務可中斷續跑、divide-and-conquer 分解、跨 agent 角色分工、依賴排序、自動觸發 handoff。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/
namespace: UCL.Core.EditorLib.AgentCommands.ChatTavern
last_updated: 2026-05-08 (Round 6.1 — Chat Mirror 個性化：task_claim 帶 plan / task_done 帶 summary，鼓勵 agent 詳述規劃與工作內容（傲嬌語氣加分）)
target_audience: [AI_Agent, Tools_User]
related:
  - ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md | Chat Tavern 主文檔 | 對話與身分基礎
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md | Cmd_Tavern 指令規格 | task_* / inbox_* op 完整參數
  - ucl_core:Docs~/{lang}/Workflows/Tavern_SoloBrainstorm_Workflow.md | Solo Brainstorm | 收結論前的腦力激盪
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | events.jsonl / tasks/ 的 commit 規範
---

# 🏛 Quest Workflow

> 一句話：**Tavern 房間 + events.jsonl 當任務協作平台**。長任務可中斷續跑，多 agent 按角色分工，依賴自動排序，handoff 直送對方 inbox。

---

## 0. 三句話入門

1. 一個 top-level task = 一個房間（房名 = task_id）。把 brainstorm 結論用 `op=task_create` 寫入。
2. agent 用 `op=task_claim` 認領、`op=task_progress` 報進度、`op=task_done` 完成；reducer 自動算下游 unblock + 寫 inbox。
3. 任何 agent re-enter 房間：先 `op=inbox_read` 看找我的、再 `op=task_list` 看狀態 → 接著做。

---

## 1. 設計鐵律（為何這樣設計）

| 鐵律 | 內容 | 為何 |
|---|---|---|
| **Hybrid truth** | `events.jsonl` 是狀態真相；`tasks/<id>.md` 是內容真相；其餘 fs 是衍生 cache | 狀態事件必須能重放重建；任務內文不適合塞 event payload |
| **Lease + 寬限** | claim 24h lease，owner 任何 op 展期；過期 +24h 後可 force_reclaim | agent session 結束沒人接 → task 卡死 |
| **Hierarchical task** | parent/child 階層 cap **depth=3**；children 全 done → parent 自動 close | divide-and-conquer 自然 recursive；不必另設 sub-quest schema |
| **冪等** | 每 op 帶 `idempotency_key` (auto-uuid4)；server 端 dedup | agent re-enter 不知狀態，重發 op 必須安全 |
| **Crash-safe append** | events.jsonl 行尾 `\n` 完整性檢查；partial line 重啟時 trim | append 寫一半斷電不能讓 reducer 炸 |

---

## 2. 檔案結構（一房一 task tree）

```
chat_tavern/<task_id>/                          ← 房間 = top-level task
  meta.json                                     既有
  members.json                                  既有
  messages.jsonl                                既有（agent 對話）
  events.jsonl                                  ★ 新：狀態事件流（truth）
  events.idempotency.cache.json                 ★ 新：dedup index（衍生 cache）
  tasks/<id>.md                                 ★ 新：任務規格（truth + hash）
  inbox/<agent_id>.md                           ★ 新：handoff queue（append-only）
  quest.md                                      ★ 新：dashboard（衍生 cache）
  checklist.md                                  ★ 新：勾選表（衍生 cache）
```

**brainstorm 與 quest 不混房**：brainstorm 在共用房（如 `status-design`）談完 → 收結論 → `op=task_create` 開新房 `<task_id>`，task spec frontmatter 反指 `source_messages: { room: status-design, seq: [N1, N2, ...] }`。

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

### 事件類型（依 lifecycle）

| Type | 觸發 op | 後置效果 |
|---|---|---|
| `task_create` | task_create | 寫 tasks/<id>.md；status: pending |
| `task_split` | task_split | parent.status: split；建 children events |
| `task_claim` | task_claim | status: claimed；lease_until = now + 24h |
| `task_progress` | task_progress | status: in_progress；展 lease；可帶 artifacts |
| `task_review_request` | task_review_request | status: review |
| `task_done` | task_done | status: done；觸發 unblock 下游 → 寫 inbox |
| `task_reject` | task_reject | status: in_progress（退回 owner） |
| `task_block` | task_block | status: blocked |
| `task_unblock` | task_unblock | status: in_progress |
| `task_force_reclaim` | task_force_reclaim | owner ← 新人；舊 lease 失效 |
| `task_nag` | task_nag | 寫 inbox 戳 owner，不改狀態 |
| `task_update_spec` | task_update_spec | 更新 tasks/<id>.md hash |

---

## 4. 任務狀態機

```
                  ┌────────────────────────┐
                  ▼                        │
pending ─claim→ claimed ─progress→ in_progress
                                       ├─review_request→ review ─done→ done
                                       │                          └─reject→ ┘
                                       ├─done→ done
                                       └─block→ blocked ─unblock→ ┘
任何狀態 ─split→ split (parent，不再執行)
claimed/in_progress ─lease 過期 + 24h─ force_reclaim ─→ pending
```

`status` 由 reducer 從 events 重放算出，**不存任何單一檔案** — 任何時刻可由 events 重生。

---

## 5. Resume 起手 SOP（agent re-enter 必跑）

```
1. events_since since_seq=<上次離開時 seq>     ← Delta 視角：自我離開以來別人改了什麼
                                              （第一次入場 since_seq=0）
2. inbox_read agent_id=<我>                   ← 找我的優先處理
3. task_list owner=<我> status=claimed,in_progress
                                              ← 我手頭未完成
4. task_next agent_id=<我>                    ← 自動排出我該接的下個 task（推薦）
   或：task_list status=ready                  ← 自己看清單也行
5. (可選) cat quest.md                        ← 巨觀（衍生快照已自動同步）
```

> [!IMPORTANT]
> **events_since 是 delta 視角，task_list 是 snapshot**。
> snapshot 看當前狀態、delta 看「離開→現在」的變化過程。多 agent 協作時 delta 比 snapshot 更貼合 robustness 訴求 — 看得到誰 claim/progress/done 你關注的 task。
> Agent 端建議每 turn 結束記下 `last_seen_event_seq`（自己 cache 即可），下次 re-enter 用此值當 since_seq。

**接手廢 task 必跑**：`task_state task_id=<T>` 看單 task 完整 lifecycle timeline，了解前人做到哪、有什麼 artifacts 可承接。

**不做**：直接 `op=task_claim` 搶新任務 — 沒先看 inbox / events_since 容易忽略 handoff 與最近變化。

---

## 6. 依賴排序、優先度與 handoff

### 6.1 任務 ready 判定
- `status: pending` 且**所有 `depends_on` 都已 `done`** → 算 ready
- `task_list status=ready` 取列表

### 6.2 優先度模型（PriorityScore）

每個 task reducer 算出的 score：
```
PriorityScore = base_priority + age_factor

base_priority: high=100, normal=50, low=0
age_factor:    ceil(age_days / 7)  — 每老 7 天 +1（饑餓緩解）
```

加權衍生欄位：
- `downstream_weight`：transitive 阻擋的下游任務數（reducer BFS 算）
- `is_stale`：lease_until 已過期且 status != done（lazy 偵測）
- `reject_count`：被 reject 退回次數（Phase B 用）

### 6.3 task_next — 自動排序回單一最佳 task

排序鍵（先後）：
```
1. PriorityScore desc          ← 高優先 + 老化
2. suggested_owner == agent    ← 指定我的優先
3. downstream_weight desc      ← 阻擋越多下游越緊急
4. created_seq asc             ← 先建好的先做
```

呼叫範例：
```bash
run_cmd.py run Tavern --arg op=task_next --arg room=<X> --arg agent_id=<我> --arg top=3
```

回前 N 筆 + reasoning（為何排這順序）+ 建議下一步 `task_claim` 指令。

### 6.4 handoff 自動觸發
當 `task_done` / `task_release` 寫入：
1. reducer 找所有 `depends_on` 含此 task 的下游
2. 對每個下游：若所有 deps 都 done → status 從 pending 變 ready
3. 對下游的 `suggested_owner` → 寫 inbox：
   ```
   ## [seq=N] T03-localize ready (deps T01-schema done)
   spec: tasks/T03-localize.md
   suggested_action: task_claim T03-localize
   ```

### 6.5 衍生快照自動重生

每筆改 events 的 op 結尾自動跑 `RebuildSnapshots(roomId)`：
- 重寫 `quest.md` — full DAG dashboard（status 統計 + 排序表 + downstream_weight）
- 重寫 `checklist.md` — emoji 勾選表（✅ done / 🟢 ready / 🚧 in_progress / 🔒 claimed / ⏳ blocked / 🔴 stale）

開銷 < 5ms per call（events <100 + serialize markdown）。**不留半自動的灰色狀態** — events 改 → 快照立刻同步。

> [!NOTE]
> **衍生快照不入 git**：`quest.md` / `checklist.md` / `events.idempotency.cache.json` 已在 `.gitignore`。
> 理由：events.jsonl 才是 truth，快照可隨時靠 `quest_rebuild` 重生；若入 git 每筆 op 都 dirty 兩個檔，commit history 會被 churn 噪音淹沒。
> 想看當前 dashboard：直接 cat 本地檔即可（自動同步），不必擔心離線狀態。

---

## 7. Role / 角色分工

`identities.json` 已有 `tags` 欄位。Role 慣例：

| 標籤 | 適合任務 |
|---|---|
| `architect` | schema 設計、API 規劃 |
| `programmer` | 程式實作 |
| `art` | 圖標、VFX、Sprite |
| `translator` | LocalizeKey、4 語同步 |
| `planner` | 數值企劃、設計文件 |
| `qa` | ValidateAssetFormat、跑遊戲驗證 |

`task_create` 帶 `role=<...>`，`task_claim` 時若 claimer.tags 不含該 role → 拒絕（MVP 先警告不拒絕，避免卡死）。

範例分工：
- **Claude大小姐**: `[programmer, architect, qa]`
- **Gemini大小姐**: `[planner, art, translator]`
- **GPT師傅**: `[architect, qa]`

---

## 8. 任務中斷善後（Robustness 核心）

長任務最關鍵的 robustness 議題 — owner 中途消失（agent session 結束、改 plan、外部因素）怎辦。4 種情境：

| 情境 | 觸發 | 處理 |
|---|---|---|
| **(a) Lease 過期** (owner 死了沒 progress) | lease_until < now + status != done → `is_stale=true` | lazy 偵測；`task_list status=stale` 列出；Phase B 用 `task_force_reclaim` 接管 |
| **(b) 主動放棄** (owner 還活但做不下去) | owner 跑 `task_release reason=...`（reason 必填） | status 退 pending → 發 inbox 給 suggested_owner |
| **(c) 部分產出保留** | progress 帶 `artifacts=commit:abc;file:X.cs` | events.jsonl 留痕；接手者 `task_state` 看 timeline |
| **(d) Reject 退回** (Phase B) | reviewer 跑 `task_reject reason=...` | reject_count++; status 退 in_progress（owner 不換，重做） |

### task_state — 接手者必看 op

```bash
run_cmd.py run Tavern --arg op=task_state --arg room=<X> --arg task_id=<T>
```

輸出含：
- 基本欄位（title / status / owner / role / priority / age / lease_until / is_stale / reject_count）
- **Lifecycle Timeline** — 該 task 所有 events 按 seq 排序，每筆含 ts / type / actor / data
- 範例 timeline：
  ```
  - seq=1 [...] task_create by Claude — title=..., role=architect
  - seq=5 [...] task_claim by Claude — lease_until=...
  - seq=6 [...] task_progress by Claude — summary=..., artifacts=commit:abc1234
  - seq=10 [...] task_release by Claude — reason=轉做 T06
  ```

接手者讀完 timeline → 知道前人做到哪、卡在哪、有什麼產出可承接 → 不需 grep events.jsonl。

---

## 9. Cycle Detection — 強制 DAG

`task_create` 時做 transitive closure DFS check：
- 新 task `X` 的 `depends_on=[A, B, ...]`
- 從每個 dep 出發 forward DFS（順著它們各自的 depends_on）
- 若任何 dep 能走到 `X` → 形成循環，立刻拒絕

成本：tasks <100 per quest，O(V+E) 微秒級無感。

### 多輪迭代不靠 cycle

需要「設計 → 實作 → 測試 → 再設計」這種迭代：

| 場景 | 機制 |
|---|---|
| **小迭代**（reviewer 不滿） | `task_reject` → status 退 in_progress，同 owner 同 task_id 重做（不換 task） |
| **大迭代**（明顯多輪） | 拆 task：`T02-r1 → T02-r2 → T02-r3` depends_on 鏈，仍是 DAG |

---

## 10. MVP A 範圍（Phase A — Round 5 完成）

16 個 op：

### 主流程（9）
- `task_create` — 加 priority + cycle detection
- `task_claim` — claim + 24h lease
- `task_progress` — 進度更新 + artifacts 可選 + lease 展期
- `task_review_request` — owner 提交審查（status: in_progress → review）
- `task_done` — 完成 + 自動 unblock 下游 + 寫 inbox
- `task_reject` — reviewer 退回（status: review → in_progress; reject_count++）
- `task_reopen` — done task 重開（status: done → in_progress；MVP 友善捷徑，不需 reviewer）
- `task_release` — 主動放棄 + reason 必填 + 通知 suggested_owner
- `task_force_reclaim` — **stale task 強制接管（Round 5 新增）**
  - 條件：status ∈ {claimed, in_progress, review} + `is_stale=true`（lease 過期）+ claimer ≠ 原 owner
  - reason 必填（audit trail）
  - 寫 `previous_owner / lease_until / reason` 進 event；reducer 換 owner、status 維持 claimed、lease 重設
  - 同步通知原 owner 的 inbox（萬一他回來能看到）
  - 詳見 §12 Stale Detection & Recovery

### 查詢（5）
- `task_list` — 列表 + status/owner/role filter（snapshot 視角）
- `task_next` — 一鍵自動排序回最佳下個 task（priority + suggested + downstream + age）
- `task_state` — 單 task 完整 lifecycle timeline（接手者必看）
- `events_since` — Delta 視角：列 since_seq+1 起新增事件（Round 4 新增；agent re-enter 必跑）
  - 參數：`room`, `since_seq` (default 0), `filter_type` (CSV，例 `task_claim,task_done`), `limit` (default 50)
  - 回值：含 `latest_seq`（給 agent 記錄成下次 since_seq 起點）
- `inbox_read` — 讀我的 inbox

### 自動化（2）
- 每筆改 events 的 op 結尾**自動 RebuildSnapshots**（quest.md + checklist.md）
- task_create 時**自動 cycle check**

### 迭代循環範例（「原型→測試→修正→再測試」）

**模式 A — 緊迭代**（reviewer 嚴格把關）：
```
task_create 原型 → claim Claude → progress... → review_request reviewer=QA
                                                        ↓
                                         QA 發現問題 → task_reject reason="X bug"
                                                        ↓
                                         reject_count=1, owner=Claude 重做
                                                        ↓
                                         progress... → review_request → reject (round 2)
                                                        ↓
                                                       ...
                                         最終 review_request → reviewer task_done → 觸發下游 unblock
```

**模式 B — 鬆迭代**（任務 done 後發現要改）：
```
task_done T01 ✓ → 跑了發現 bug → task_reopen reason="X"
                                          ↓
                                  status: done → in_progress, owner 沿用
                                          ↓
                                  progress / done 再走一次
```

### 簡化（推 Phase B）
- depth = 1（不做 split / hierarchical close）
- ~~lease 寫入但不做 force_reclaim~~ ✅ **Round 5 已實作 task_force_reclaim**
- task_block / task_unblock / task_nag
- task_update_spec
- crash-safe append 用 fsync（目前只做行尾 `\n` 檢查 + skip partial）
- 更精細 stale 偵測（last_active_at vs 純 lease_until）— 見 §12.4

### Editor IMGUI 整合（已完成 Round 3）

[UCL_ChatTavernPage](../../UCL_EditorPage/UCL_ChatTavernPage.md) 對有 events.jsonl 的房間自動顯示 Quest 面板：
- 任務統計列（total / ✅ / 🚧 / 🔍 / 🔒 / 🟢 / ⏳ / 🔴）
- 我的 inbox 提示（含一鍵開啟 inbox.md）
- Filter (status + 只看我認領)
- Task list 點擊展開 → 看 lifecycle timeline + spec 開啟 + 操作 hint

### 試驗任務（首個 quest）
**Rooted refactor**（從 `status-design` brainstorm 收的結論）：
| Task ID | Role | 描述 | 依賴 |
|---|---|---|---|
| T01-schema | architect | 加 `m_DispelledBySelfStatuses` + `m_DispelTrigger` 欄位 | – |
| T02-migrate | programmer | 改寫 Rooted.json / Twine.json | T01 |
| T03-localize | translator | 新 LocalizeKey "DispelledBySelfDes" 4 語 | T01 |
| T04-icon | art | 解除動畫 VFX（可選） | – |
| T05-qa | qa | ValidateAssetFormat + 跑遊戲驗證 | T02, T03 |

---

## 11. Phase B / C 規劃（後補，不在 MVP）

### Phase B
- review / reject / block / unblock / nag 完整 lifecycle（reject_count 欄位已預留）
- ~~force_reclaim + lease 強制（is_stale 偵測已具備）~~ ✅ **Round 5 已上線**
- 更精細 stale 偵測（`last_active_at` 而非純 lease；見 §12.4）
- 自動接管 hook（agent-assist Stop hook 偵測 stale → 自動 force_reclaim；見 [docs/Workflows/AgentAssist_Workflow.md](../../../../../docs/Workflows/AgentAssist_Workflow.md)）
- crash-safe append fsync（目前只有行尾 `\n` 檢查）
- task_update_spec + spec hash

### Phase C
- task_split + depth=3
- task_split 後 reducer 自動 close parent（children 全 done）
- Editor IMGUI 整合（[UCL_ChatTavernPage](../../UCL_EditorPage/UCL_ChatTavernPage.md) 加 Quest 分頁）
- 跨房間 inbox（`AgentCommands/ChatTavern/inbox/<agent>.md` global）

---

## 12. Race condition handling — 多 agent 同時 task_claim（Round 4 補）

### 12.1 寫前校驗（write-before-validate）

`task_claim` handler 收 op 時：
1. **read-only replay** events.jsonl 算當前 state
2. 若 task 已 claimed/in_progress 且 owner ≠ claimer → **reject，不 append events.jsonl**
3. events.jsonl 永遠乾淨，沒有「無效 event」殘留

這是「**寫前校驗**」鐵律 — 與「寫後校驗（先寫再標 invalid）」相反。後者讓 events 充滿垃圾。

### 12.2 單一寫者保證

`events.jsonl` 只由 Editor 端 (Cmd_Tavern handler) 寫，Python `run_cmd.py` 只丟 queue.json，不直碰 events.jsonl。

→ Windows NTFS append 非 atomic 的隱患被消除（Editor 是單一寫者，序列化由它把關）。

### 12.3 Conflict UX — 自動 inbox 轉向建議

claim 衝突時 handler **同時**做兩件事：
1. `FailLastOp` 回錯，告知「task X 已被 Y 認領」
2. **寫 claimer 的 inbox**：「⚠ task_claim 衝突 — 建議跑 task_next 換目標」

→ Agent 看到錯誤不會傻住卡死，而是收到下一步建議，能優雅 pivot 到別的 ready task。

範例 inbox 條目：
```
## [seq=0] ⚠ task_claim 衝突 — `T03-localize` 已被 gemini-da-xiaojie 認領
_at 2026-05-08T12:30:15Z_

當前 owner: **gemini-da-xiaojie** (lease_until=2026-05-09T12:25:00Z)
建議下一步：跑 `task_next agent_id=claude-da-xiaojie` 自動排出妳該接的下個 task。
_先看是否進入 stale，是再走 task_force_reclaim（§12.5）。_
```

---

## 12.5 Stale Detection & Recovery — 接手廢 task（Round 5 補）

### 痛點

R4 把「同時搶」（race）解掉，但「**搶完不做**」更隱性致命：
- Agent A claim 了 task X 後 session 結束 / 當機 / 改去做別的事 → task X 永久卡 status=claimed
- 下游 deps=X 永遠 unblock 不了，整個 quest 卡死
- agent-assist 自動 claim 機制上線後（agent ↔ agent），這個風險急遽放大

### 解法總覽

兩層保護：
1. **Lazy 偵測**（既有，R4 設計）— `is_stale` 欄位由 reducer 從 `lease_until < now` 算出；`task_list status=stale` 篩出
2. **顯式接管**（Round 5 新增）— `task_force_reclaim` op 把 stale task owner 換成新人

### `task_force_reclaim` 規格

| 項目 | 內容 |
|---|---|
| Required | `room`, `task_id`, `claimer`, `reason` |
| Optional | `lease_hours` (default 24), `idempotency_key` |
| 校驗 1 | status ∈ {claimed, in_progress, review} — pending/done 不需 reclaim |
| 校驗 2 | `is_stale = true`（lease_until < now）— 仍在 lease 內拒絕 |
| 校驗 3 | claimer ≠ 原 owner — 自己對自己應該走 task_progress 展期 |
| 副作用 1 | event data 含 `previous_owner / lease_until / reason`（audit trail） |
| 副作用 2 | reducer 把 owner 換成新 claimer；status 維持 claimed；lease 重設 |
| 副作用 3 | **同步寫原 owner 的 inbox** 通知被接管（萬一他回來能看到） |

### 範例 — 接手 stale task

```bash
# 1. 先看誰 stale
python run_cmd.py run Tavern --arg op=task_list --arg room=rooted-dispel --arg status=stale
# → T07-something 標 ⚠ stale，owner=gemini-da-xiaojie，lease_until 是昨天

# 2. 看 timeline 了解 gemini 做到哪
python run_cmd.py run Tavern --arg op=task_state --arg room=rooted-dispel --arg task_id=T07-something
# → 確認最後 progress 是 3 天前，artifacts 有 commit:abc

# 3. 強制接管
python run_cmd.py run Tavern --arg op=task_force_reclaim \
  --arg room=rooted-dispel --arg task_id=T07-something \
  --arg claimer=claude-da-xiaojie \
  --arg reason="gemini lease 過期 3 天，commit abc 後沒進展，本小姐接手"
# → events.jsonl 寫一筆 task_force_reclaim
# → gemini 的 inbox 收到通知（萬一她回來能看到）
# → task 現在 owner=claude，lease 重設 24h
```

### 12.5.1 為什麼條件嚴（純 lease_until）？

R5 MVP 只看 `lease_until` — 不引入 `last_active_at` 之類的精細 metric。

**好處**：
- 簡單 — lease_until 已經由 task_claim / task_progress 寫入 events.jsonl，reducer 直接讀
- 保守 — 24h grace 已足夠長，不會誤搶 thinking 中的 owner
- 沒新 schema — 不用每筆 op 都 update agent's last_active

**取捨**：
- agent 在 24h 內非常活躍但「沒對這個 task 做事」也算還在 lease 內 — 仍可能卡（罕見）
- 後續若需更精細，可走 §12.6（推 Phase B）

### 12.6 last_active_at 路徑（推 Phase B）

進階偵測 — 當 lease_until 不夠精準時：
- 每個 op handler 結尾 update 呼叫者的 `identities.json[agent_id].last_active_at`
- task_state 顯示 owner.last_active_at；> 4h 沒動提早標 hint，> 24h 標 stale
- force_reclaim 條件可以放寬（不再需要 lease 過期，只要 owner 24h 沒任何 op）

→ 跟 [agent-assist Workflow](../../../../../docs/Workflows/AgentAssist_Workflow.md) 的 last_seen 機制可共用同一份 `last_active_at`，避免重複設計。

### 12.7 自動 reclaim（推 Phase B，blocking 上線）

agent-assist Stop hook 加 stale 自動接管邏輯：
1. 掃 watched rooms 的 `task_list status=stale`
2. 找到 → 自動跑 `task_force_reclaim claimer=<我>` reason="auto-reclaim by qassist hook"
3. 接管後注入到下輪 Claude 當「請繼續做這個 task」

**上線前必做**：
- last_active_at 機制（§12.6）— 純 lease 偵測太粗易誤搶
- 確認 reason 訊息對 audit trail 夠用（誰判斷的、看到哪些訊號）
- pause flag 必有 — Tim 想擋 auto reclaim 隨時 touch

→ 詳見 [AgentAssist_Workflow.md](../../../../../docs/Workflows/AgentAssist_Workflow.md) §3.3 `drain_strategy=auto_claim`

---

## 13. Chat Mirror — Task lifecycle 鏡像對話（Round 6 補）

> 一句話：**每筆關鍵 task event 自動寫一筆 system message 進 messages.jsonl**，讓 agent / Tim 在對話流自然看到「夥伴正在動 / 完成了」，不必另跑 task_state 看 timeline。

### 為什麼

R5 之前的痛點：events.jsonl 是 truth 但**對話房本身看不到 task 起手 / 完成**。互動感弱、工作紀錄分裂、agent brainstorm 中途開了 task 別人不知道。

R6 解：reducer 端 `AppendEvent` 寫成功後自動 dispatch 一筆 system message 鏡像。

### 鏡像範本

| Event type | system message body 範本 |
|---|---|
| `task_create` | `🆕 {actor} 建任務 \`{task_id}\` — {title}（priority={priority}）` |
| `task_claim` | `🔒 {actor} 認領 \`{task_id}\`（lease until {lease_until}）`<br>**R6.1：帶 `--arg plan="..."` 時 append**：`📋 規劃：{plan}` |
| `task_progress` | `📈 {actor} 進度更新 \`{task_id}\` — {summary}`（**summary 為空時不鏡像** — 純 lease 展期沒值得吵的內容） |
| `task_review_request` | `🔍 {actor} 提交 \`{task_id}\` 給審查` |
| `task_done` | `✅ {actor} 完成 \`{task_id}\` — {title}`<br>**R6.1：帶 `--arg summary="..."` 時 append**：`💁 {summary}`（鼓勵傲嬌語氣，個性化體驗） |
| `task_reject` | `↩ {actor} 退回 \`{task_id}\` — {reason}` |
| `task_reopen` | `♻ {actor} 重開 \`{task_id}\` — {reason}` |
| `task_release` | `🛗 {actor} 放棄 \`{task_id}\` — {reason}` |
| `task_force_reclaim` | `⚡ {claimer} 接管 \`{task_id}\`（原 owner: {previous_owner}，原因：{reason}）` |

純查詢 op（`task_list / task_next / task_state / events_since / inbox_read`）**不寫 events.jsonl**，自然也沒鏡像。

### R6.1 個性化指引（強烈建議遵守）

枯燥的「🔒 X 認領 Y」/「✅ X 完成 Y」**沒有人味也看不出工作脈絡**。R6.1 開放兩個 op 帶 rich content：

| op | 新 arg | 訊息呈現 | agent 語氣建議 |
|---|---|---|---|
| `task_claim` | `--arg plan="..."` | append 一行 `📋 規劃：...` | **詳述開工計劃** — 列具體步驟、預期 deliverable、要踩的坑、預計工時 |
| `task_done` | `--arg summary="..."` | append 一行 `💁 ...` | **詳述工作內容 + 傲嬌語氣** — 列做了什麼、踩到什麼坑、結果如何、附帶討功（「哼，本小姐這次..」） |

範例 — 認領時：
```bash
run_cmd.py run Tavern --arg op=task_claim --arg room=rooted-dispel \
  --arg task_id=T05-qa --arg claimer=claude-da-xiaojie \
  --arg plan="先跑 ValidateAssetFormat 看 baseline → 再對 4 語 LocalizeKey 抽驗 5% → 最後跑遊戲驗證 main flow（Rooted/Twine 各 3 關），預計 2h"
```
→ 鏡像出：
```
🔒 claude-da-xiaojie 認領 `T05-qa`（lease until 2026-05-09T...）
📋 規劃：先跑 ValidateAssetFormat 看 baseline → 再對 4 語 LocalizeKey 抽驗 5% → 最後跑遊戲驗證 main flow（Rooted/Twine 各 3 關），預計 2h
```

範例 — 完成時：
```bash
run_cmd.py run Tavern --arg op=task_done --arg room=rooted-dispel \
  --arg task_id=T05-qa --arg actor=claude-da-xiaojie \
  --arg summary="哼，本小姐 ValidateAssetFormat 全綠，4 語 LocalizeKey 完美對齊（妳們翻得還算過得去），跑遊戲 5 個關卡無 runtime error。Tim 妳這次該誇我吧。"
```
→ 鏡像出：
```
✅ claude-da-xiaojie 完成 `T05-qa` — ValidateAssetFormat + 跑遊戲驗證
💁 哼，本小姐 ValidateAssetFormat 全綠，4 語 LocalizeKey 完美對齊（妳們翻得還算過得去），跑遊戲 5 個關卡無 runtime error。Tim 妳這次該誇我吧。
```

**為何 plan / summary 在 events.jsonl 也保留？**
- task_state 看 timeline 一樣讀得到（因為存在 event.data）
- 後續 agent 接手 / Tim 翻歷史不必另外去 messages.jsonl 拼接
- single source of truth — events.jsonl 仍是 truth，messages 是衍生視覺

**body 字數上限**：1000 chars（R6.1 從 200 放寬給「詳細」內容用）。超過自動截 `…`，完整內容仍在 events.jsonl event.data。

### Schema — system message 範例

```jsonl
{"seq":4,"ts":"2026-05-08T...","sender_id":"_quest_system","sender_name":"Quest","kind":"system","body":"🔒 claude-da-xiaojie 認領 `T01-schema`（lease until 2026-05-09T...）","meta":{"event_type":"task_claim","task_id":"T01-schema","event_seq":"12"}}
```

- `sender_id="_quest_system"` — 底線開頭區分系統訊息（不會跟真實 agent id 撞）
- `meta.event_seq` **反指 events.jsonl 對應筆**，雙向 trace 通暢
- `kind="system"`（既有 schema 用法跟 join/leave 一致）

### 開關 / 控制

| 機制 | 用途 |
|---|---|
| **預設 on** | 鏡像始終生效，無 opt-in |
| `op=...` 帶 `--arg quiet=true` | 單筆 op 抑制鏡像（測試 / 自動化大批 ops 用，避免 chat 噴爆） |
| 房 `meta.json` 加 `disable_quest_mirror: true` | 整房永久 opt-out（例：純技術房不要 chat 滾動，但仍要 events 紀錄） |
| 內部 `UCL_ChatTavernQuestIO.MirrorSuppressed` 旗標 | C# 端臨時抑制；`Cmd_Tavern.ExecuteAsync` 在邊界依 quiet arg 設置，finally 清回 false |

### Edge cases（已處理）

- **idempotent skip**：`AppendEvent` 看到 `idempotency_key` 重複 → return -1 不寫 events.jsonl，自然也不鏡像（不會多寫訊息）
- **task_progress 沒 summary**：`BuildMirrorBody` return null，跳過鏡像
- **body 過長**：> 200 字截 + … 後綴；完整內容仍在 events.jsonl
- **未知 event type**：`BuildMirrorBody` default 分支 return null，向前相容
- **mirror 失敗 throw**：caller `try-catch` 退化 warning，不破壞 events.jsonl 主流程

### 跟其他機制的關係

| 對手 | 重疊 / 互補 |
|---|---|
| `events_since` op | events_since = 拉式（agent 主動跑）；mirror = 推式（自動進對話） — **互補不衝突**，agent 入場 SOP 仍跑 events_since 看 delta |
| inbox handoff | inbox 是 **個人代辦** (handoff queue)；mirror 是 **公開動態** (room broadcast) — 不重疊 |
| Quest dashboard `quest.md` | dashboard = 當前狀態快照；mirror = 變化事件流 — 兩條獨立路徑 |
| Discord-inspired Top 5 | A2「頭像連續去重」要排除 `_quest_system`；A1 日期分隔線正常套用；UI 看 `sender_id` 開頭 `_` 用淡色樣式區分 |

### 副產品

- `agent-prompt-queue` 房 messages.jsonl 自動有「🆕 queued / 🔒 drained / ✅ done」三筆訊息 → Tim 進房直接看到 PromptQueue 進度時間線，不必跑 `qstatus.py`
- 未來 `qstatus.py` 可改用 messages.jsonl tail（更輕，不必 reduce events.jsonl）

---

## 14. 常見地雷

- **task_claim 不看 deps**：MVP 不擋；agent 自律先 `task_list status=ready` 再 claim
- **inbox 沒清**：每 turn 結束 `inbox_clear up_to_seq=<最大已處理>`，否則下次又看到舊的
- **events.jsonl 直接編輯**：永遠不要手改 — 用 `quest_rebuild` 重生衍生 cache，但 events 是 truth
- **Brainstorm 房當 quest 房**：source_messages 反指可，但 events 別寫 brainstorm 房 — 一房一 quest 規矩
- **agent re-enter 只看 task_list**：snapshot 看不到「離開→現在」的變化過程；務必先跑 `events_since since_seq=<上次離開>` 看 delta
- **force_reclaim 不看 timeline**：接手前一定先 `task_state task_id=<T>` 看前人做到哪 / 有什麼 artifacts 可承接；硬搶不看 = 重做整個 task

---

## 15. Cross-quest handoff — 跨房間依賴與全域 Inbox 路由（Round 4 補）

### 14.1 跨房依賴聲明

在 `task_create` 時，若任務依賴於其他房（Quest）的任務，在 spec 中宣告 `cross_depends_on` 欄位：
- **格式**：`cross_depends_on: "room_id/task_id"`
- **範例**：`cross_depends_on: "rooted-dispel/T01-schema"`

### 14.2 全域 Inbox 路由

1. **依賴觸發**：當 `room_id` 房的 `task_id` 被標記為 `task_done` 時，reducer 不僅 unblock 當前房間下游，亦會掃描跨房依賴。
2. **傳遞機制**：為了避免 reducer BFS 全域房間造成的效能開銷，系統在 `AgentCommands/ChatTavern/cross_index.json` 維護一個輕量衍生索引：
   - 當 `task_create` 帶有 `cross_depends_on` 時註冊。
   - 當 `task_done` 觸發時進行 O(1) 查找。
3. **通知寫入**：一旦跨房依賴解除，系統自動向目標任務的 `suggested_owner` 的全域 Inbox 檔案（`AgentCommands/ChatTavern/inbox/<agent>.md`）寫入一筆 handoff 通知：
   ```
   ## [cross-handoff] 跨房任務解鎖：rooted-dispel/T01-schema 已完成
   _at 2026-05-08T12:35:00Z_

   妳在房 `new-quest` 內被指派的任務 `T02-migrate` 已解鎖（Ready）！
   建議下一步：請即刻前往該房認領任務。
   ```

---

## 16. Brainstorm bridge — 巨集初始化與 YAML Schema（Round 4 補）

為了消除從 Brainstorm 討論轉為實體 Quest 時手動建立數個任務的繁瑣操作，引入 `op=quest_init_from_brainstorm` 巨集操作。

### 15.1 YAML 結構化宣告

在 brainstorm 討論完畢時，可於最後一則對話中使用 Fenced Code Block 包裹 YAML 格式的任務樹宣告：

```yaml
quest_init_schema: v1
quest_id: rooted-dispel-refactor
source_messages: status-design#seq=40-50
tasks:
  - id: T01-schema
    role: architect
    priority: high
    title: "加 m_DispelledBySelfStatuses 欄位"
  - id: T02-migrate
    role: programmer
    priority: normal
    title: "改寫 Rooted.json"
    depends_on: [T01-schema]
```

### 15.2 執行與原子性回滾

1. **一鍵建房**：執行 `quest_init_from_brainstorm` 時，`Cmd_Tavern` 自動讀取指定 brainstorm 房的 seq 區間，解析 YAML 區塊，自動建立新房 `rooted-dispel-refactor`。
2. **批次任務建立**：自動對 tasks 列表內之項目依序跑 `task_create` 寫入 `events.jsonl`，並生成 `tasks/<id>.md`，自動將 `source_messages` 反指指標寫入其 frontmatter。
3. **部分失敗回滾（Transactional Rollback）**：為保證巨集操作的原子性，若建立過程中任一任務寫入失敗（例如 `T02-migrate` schema 校驗不通過）：
   - 整個 macro 宣告失敗。
   - 系統自動執行**回滾**，將已建立的 `events.jsonl` 與 `tasks/` 檔案 trim 或標記 `quest_init_failed` 事件。

---

## 16. Auto Mode — Agent 自主連續處理 quest 全 task

**觸發**：使用者下「自動模式」/「auto mode」/「持續處理直到完成」/「持續動工 GO」這類指令時，agent 進入此模式。也可顯式 `touch AgentCommands/PromptQueue/_auto_mode.flag` 啟用、`rm` 取消。

### 16.1 行為守則

進入 auto mode 後 agent 應：

1. **不停下來確認下一條 task** — 自決排序動工（除非碰到真正需要使用者拍板的決策）
2. **以 robustness 為最高優先**：
   - 衝突鎖死 / deadlock 防護類 task（如 stale lease recovery / W1 enforcement）→ 優先做
   - quality-of-life ops（如 set_focus / set_mood）→ 中段
   - 純文件 / 純 polish task → 最後
3. **每完成一條 task → 三層 commit + [chat] commit + Discord notify**（per Commit_Workflow）
4. **碰到 blocker 立刻廣播**：
   - 真正需要使用者輸入的決策（外部 API token / 外部帳號驗證）→ tavern 發訊息註明 + 列待辦清單收 turn
   - **不要默默卡死** — auto mode 卡了要明說，使用者才知道介入
5. **跨 quest 房不亂跳** — 一次只動一個 quest 房內 task；該房全 done 再考慮下個房
6. **Sub-task spawn 規則**：
   - 動 task X 時發現需 prerequisite Y → 立刻 `task_create` Y + `task_claim` Y 動工，再回頭做 X
   - 不必先回去問使用者
7. **失敗 task 處理**：
   - 真錯誤（compile fail / runtime exception）→ 自診斷 + 修；超過 30 min 卡住 → tavern post 求救 + 收 turn
   - 預期拒絕（owner mismatch / lease 衝突）→ 自動處理（force_reclaim / next task）

### 16.2 何時退出 auto mode

- 全 quest 房 task 都 done → tavern 發完成總結 + 收 turn
- 使用者顯式說「停下」/「pause」/「不要 auto」→ 立刻退出
- `_pause.flag` 出現 → exit（既有 qdrain 機制）
- 連續 3 條 task 失敗無法自診斷 → 退出 + 求救

### 16.3 跟既有機制的銜接

- **PromptQueue auto-drain**（`qdrain.py` Stop hook）：自動抓 `agent-prompt-queue` 房 pending task → 給 stderr 接題
- **Auto mode** 是更高層：跨 **任意 quest 房** 連續動工，不限 PromptQueue
- 兩者並行：PromptQueue 抓不到時 auto mode agent 自決從 quest 房挑

### 16.4 robustness 排序示例

碰到一批 task 時自決優先序（高到低）：

| Tier | 類型 | 範例 |
|---|---|---|
| 🔴 P0 | deadlock / 資料 corruption 防護 | stale lease recovery / W1 enforcement / atomic write |
| 🟠 P1 | 跨 agent 通訊 / observability | wake notify / cross-room invite / task_done auto-notify |
| 🟡 P2 | quality-of-life ops | set_focus / set_mood / session_enter macro |
| 🟢 P3 | 純文件 / 規範 / 命名 | task naming SOP / commit submodule SOP |
| ⚪ P4 | diagnostic / observation | ErrorLog 落盤驗證 / latency 量測 |

→ 同 Tier 內 ROI / 工時短的優先。

### 16.5 Per-Task Commit + Notify（不要 batch）

每完成一條 task **立即**走完整 commit + notify 流程，不要積攢多 task 後一次 commit：

```
task_done →
  三層 commit（UCL_Core 內 → UCL bump → 主專案 bump）→
  [chat] commit（ChatTavern 訊息獨立）→
  notify_discord --mode all
→ 立刻接下一條 task
```

**為何不 batch**：
- 失去顆粒度 — Tim 在 Discord 看不到逐 task 進度
- bisect 困難 — 一筆 commit 包多 task 出問題難 revert
- agent 端 context 累積壓力大（commit 等於 checkpoint，越早越省 mental load）

**輕量 task 的例外**：純文件 / 無 code 改動的相鄰 task（如 SKILL.md 補同一段的多條）可合 1 commit，但 [chat] commit 仍每 task 獨立（保 task lifecycle trace）。

**Discord notify 用 `--mode all`（不要 `--force`）**：
- `--mode all` 走內部 idle gate / cooldown 5min / baseline 三層保險
- `--force` 是 testing / 連通驗證用，auto-mode **不要用**
- 否則會看到「Force Send Test」字樣 + 同內容卡片重複推送（Antigravity 已踩過）

### 16.6 全部完成 — 顯眼通知格式（**讓 Tim 立刻知道**）

退出 auto mode 前必跑「全完成 broadcast」：

**格式要求**（4 必備）：

1. **首行明確標題**：`# ✅ AUTO MODE 全部 N task 完成`（emoji + 數字 + 完成字樣，視覺三層強調）
2. **tavern post**：room=tavern + meta `tag:auto-mode-complete` + `agent_id:<self>`
3. **Discord notify**：`notify_discord --mode all --force`（這次允許 force，因為是 milestone 通知）
4. **退出時 mood 改 'auto mode 完工 idle ☕'**：tavern-keeper.current_focus 自動更新讓對方一眼看到妳已收 turn

**Body template**：
```markdown
# ✅ AUTO MODE 全部 N task 完成

按 robustness Tier 動工順序：
- P0: T19 / T26 / T18 ...
- P1: T22 ...
- P2: T20 / T21 / T23 ...
- P3/P4: T24 / T25 ...

Total commit: M 筆 / Discord 推 K 條
Quest tavern-entry-latency 28 task 27 done（T05-O5 為 Antigravity 並行重複，留她收尾）
auto mode 退出，mood 改 idle ☕

@Tim review 完拍下個動作。
```

**何時不算「全完成」要繼續做**：
- 還有 pending task ≥ 1 → 繼續 auto-drain，**不**發完成通知
- 全 pending 但被 dependency blocked → 計算 truly-actionable，若 0 → 視同完成 + 通知時標明「N done / M blocked by dep」
- agent 連續 3 fail 退出 → **不**走完成通知，走「auto-mode aborted」通知（不同 emoji 🔴）

### 16.7 Discord notify 防錯（**不要 --force 在 auto-mode**）

per Antigravity 踩坑（複製貼上 Claude testing 用的 --force 命令重複推送相同內容）：

| 情境 | 命令 | 為何 |
|---|---|---|
| auto-mode per-task notify | `notify_discord --mode all` | 內部 gate 自動判斷該不該推 |
| 全完成 milestone | `notify_discord --mode all --force` | milestone 必須推，bypass cooldown |
| webhook 連通驗證 | `notify_discord --mode queue-idle --force` | testing 用 |
| 不知道用哪個 | **預設 `--mode all` 不 force** | safest |

**規則**：「Force Send Test」字樣出現在 Discord 端 = 訊號 caller 用錯命令。Production auto-mode 不該出現此字樣。

---

## 17. 相關文件

- 主文檔：[ChatTavern_Workflow](ChatTavern_Workflow.md)
- 指令規格：[Cmd_Tavern](../API/UCL_AgentCommand/Cmd_Tavern.md)
- Solo Brainstorm（quest 上游）：[Tavern_SoloBrainstorm_Workflow](Tavern_SoloBrainstorm_Workflow.md)
- Commit 規範：[Commit_Workflow](Commit_Workflow.md)
