---
title: Quest Workflow — Robust 多階段多 agent 任務協作
description: 在 ChatTavern 之上的 Event-Sourced 任務協作系統。長任務可中斷續跑、divide-and-conquer 分解、跨 agent 角色分工、依賴排序、自動觸發 handoff。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/
namespace: UCL.Core.EditorLib.AgentCommands.ChatTavern
last_updated: 2026-05-08 (Round 3 — 補 review/reject/reopen 迭代循環 + UCL_ChatTavernPage Quest 面板)
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
1. inbox_read agent_id=<我>                   ← 找我的優先處理
2. task_list owner=<我> status=claimed,in_progress
                                              ← 我手頭未完成
3. task_next agent_id=<我>                    ← 自動排出我該接的下個 task（推薦）
   或：task_list status=ready                  ← 自己看清單也行
4. (可選) cat quest.md                        ← 巨觀（衍生快照已自動同步）
```

**接手廢 task 必跑**：`task_state task_id=<T>` 看 lifecycle timeline，了解前人做到哪、有什麼 artifacts 可承接。

**不做**：直接 `op=task_claim` 搶新任務 — 沒先看 inbox 容易忽略 handoff。

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

## 10. MVP A 範圍（Phase A — Round 3 完成）

14 個 op：

### 主流程（8）
- `task_create` — 加 priority + cycle detection
- `task_claim` — claim + 24h lease
- `task_progress` — 進度更新 + artifacts 可選 + lease 展期
- `task_review_request` — owner 提交審查（status: in_progress → review）
- `task_done` — 完成 + 自動 unblock 下游 + 寫 inbox
- `task_reject` — reviewer 退回（status: review → in_progress; reject_count++）
- `task_reopen` — done task 重開（status: done → in_progress；MVP 友善捷徑，不需 reviewer）
- `task_release` — 主動放棄 + reason 必填 + 通知 suggested_owner

### 查詢（4）
- `task_list` — 列表 + status/owner/role filter
- `task_next` — 一鍵自動排序回最佳下個 task（priority + suggested + downstream + age）
- `task_state` — 單 task 完整 lifecycle timeline（接手者必看）
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
- lease 寫入但不做 force_reclaim
- task_block / task_unblock / task_nag
- task_update_spec
- crash-safe append 用 fsync（目前只做行尾 `\n` 檢查 + skip partial）

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
- force_reclaim + lease 強制（is_stale 偵測已具備）
- crash-safe append fsync（目前只有行尾 `\n` 檢查）
- task_update_spec + spec hash

### Phase C
- task_split + depth=3
- task_split 後 reducer 自動 close parent（children 全 done）
- Editor IMGUI 整合（[UCL_ChatTavernPage](../../UCL_EditorPage/UCL_ChatTavernPage.md) 加 Quest 分頁）
- 跨房間 inbox（`AgentCommands/ChatTavern/inbox/<agent>.md` global）

---

## 12. 常見地雷

- **task_claim 不看 deps**：MVP 不擋；agent 自律先 `task_list status=ready` 再 claim
- **inbox 沒清**：每 turn 結束 `inbox_clear up_to_seq=<最大已處理>`，否則下次又看到舊的
- **events.jsonl 直接編輯**：永遠不要手改 — 用 `quest_rebuild` 重生衍生 cache，但 events 是 truth
- **Brainstorm 房當 quest 房**：source_messages 反指可，但 events 別寫 brainstorm 房 — 一房一 quest 規矩

---

## 13. 相關文件

- 主文檔：[ChatTavern_Workflow](ChatTavern_Workflow.md)
- 指令規格：[Cmd_Tavern](../API/UCL_AgentCommand/Cmd_Tavern.md)
- Solo Brainstorm（quest 上游）：[Tavern_SoloBrainstorm_Workflow](Tavern_SoloBrainstorm_Workflow.md)
- Commit 規範：[Commit_Workflow](Commit_Workflow.md)
