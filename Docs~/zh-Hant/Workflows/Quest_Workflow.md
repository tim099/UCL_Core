---
title: Quest Workflow — Robust 多階段多 agent 任務協作
description: 在 ChatTavern 之上的 Event-Sourced 任務協作系統。長任務可中斷續跑、divide-and-conquer 分解、跨 agent 角色分工、依賴排序、自動觸發 handoff。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/
namespace: UCL.Core.EditorLib.AgentCommands.ChatTavern
last_updated: 2026-05-08
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
3. task_list status=ready                     ← 可領的（dep 都 done）
4. quest_status                               ← 巨觀（衍生 quest.md 也行）
```

**不做**：直接 `op=task_claim` 搶新任務 — 沒先看 inbox 容易忽略 handoff。

---

## 6. 依賴排序與 handoff

### 任務 ready 判定
- `status: pending` 且**所有 `depends_on` 都已 `done`** → 算 ready
- `task_list status=ready` 取列表

### handoff 自動觸發
當 `task_done` 寫入：
1. reducer 找所有 `depends_on` 含此 task 的下游
2. 對每個下游：若所有 deps 都 done → status 從 pending 變 ready
3. 對下游的 `suggested_owner`（task spec 內欄位）→ 寫 inbox：
   ```
   ## [seq=N] T03-localize ready (deps T01-schema done)
   spec: tasks/T03-localize.md
   suggested_action: task_claim T03-localize
   ```

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

## 8. MVP A 範圍（Phase A）

第一輪只做最小可執行子集：

### 6 個 op
- `task_create` / `task_claim` / `task_progress` / `task_done` / `task_list` / `inbox_read`

### 簡化
- depth = 1（不做 split / 不做 hierarchical 後置處理）
- lease 寫入但不做 force_reclaim
- review/reject/block/unblock/nag 全省略
- crash-safe append 簡化版（行尾 `\n` 檢查 + skip partial）
- 衍生 quest.md / checklist.md **不**自動重生（手動跑 quest_rebuild）

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

## 9. Phase B / C 規劃（後補，不在 MVP）

### Phase B
- review / reject / block / unblock / nag 完整 lifecycle
- force_reclaim + lease 強制
- crash-safe append（fsync + partial-line trim）
- task_update_spec + spec hash

### Phase C
- task_split + depth=3
- 衍生 cache 自動重生（每 N 筆 events 觸發）
- Editor IMGUI 整合（[UCL_ChatTavernPage](../../UCL_EditorPage/UCL_ChatTavernPage.md) 加 Quest 分頁）
- 跨房間 inbox（`AgentCommands/ChatTavern/inbox/<agent>.md` global）

---

## 10. 常見地雷

- **task_claim 不看 deps**：MVP 不擋；agent 自律先 `task_list status=ready` 再 claim
- **inbox 沒清**：每 turn 結束 `inbox_clear up_to_seq=<最大已處理>`，否則下次又看到舊的
- **events.jsonl 直接編輯**：永遠不要手改 — 用 `quest_rebuild` 重生衍生 cache，但 events 是 truth
- **Brainstorm 房當 quest 房**：source_messages 反指可，但 events 別寫 brainstorm 房 — 一房一 quest 規矩

---

## 11. 相關文件

- 主文檔：[ChatTavern_Workflow](ChatTavern_Workflow.md)
- 指令規格：[Cmd_Tavern](../API/UCL_AgentCommand/Cmd_Tavern.md)
- Solo Brainstorm（quest 上游）：[Tavern_SoloBrainstorm_Workflow](Tavern_SoloBrainstorm_Workflow.md)
- Commit 規範：[Commit_Workflow](Commit_Workflow.md)
