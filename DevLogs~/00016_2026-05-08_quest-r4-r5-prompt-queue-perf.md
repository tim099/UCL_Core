---
date: 2026-05-08
index: 00016
title: Quest workflow R4+R5 + PromptQueue auto-drain 系統 + 三本設計 + Perf F1-F3 quick wins
tags: [feature, quest, chat-tavern, infra, perf, design, docs]
---

# Quest workflow R4+R5 + PromptQueue 自動排程 + 三本設計 + Perf 三題收尾

## What

一輪 robust 升級的全紀錄 — 四個交織的工作軸線：

1. **Quest workflow R4 brainstorm** → R5 落地（task_force_reclaim / events_since / claim 衝突 inbox UX / lease_seconds / task_list status=stale）
2. **PromptQueue 系統**（新基礎建設）— Tim 把插隊 prompt 排成 queue，agent Stop hook 自動 drain
3. **設計三本**（design without implement）— AgentAssist / ChatTavern_DiscordInspired / PerfReport
4. **Perf F1+F2+F3 quick wins**（不需實測就敢動的 -GC / -IO 重災）

## Why

- 多 agent 上線在即，**robustness 漏洞要先填**（claim 競態 / stale lease / 沒 catchup 視角）
- Tim 工作流要支援「邊工作邊插指令」，純 IM 對話順序無法承載
- UCL_ChatTavernPage 嚴重卡頓 — 必須先量再改，但靜態分析就能撿到 80% GC（不必等實測）
- 設計類 task 不該變成大 PR — 開房 brainstorm + 寫設計文件 + 留決策節點給 Tim 拍板

## How — Quest workflow R5（程式變動）

### task_force_reclaim op（[`Cmd_Tavern.cs`](../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/Cmd_Tavern.cs)）

| 項目 | 設計 |
|---|---|
| Required | `room`, `task_id`, `claimer`, `reason` |
| 校驗 1 | status ∈ {claimed, in_progress, review} |
| 校驗 2 | `is_stale = true`（lease_until < now） |
| 校驗 3 | claimer ≠ 原 owner |
| 副作用 1 | event data 含 `previous_owner / lease_until / reason` |
| 副作用 2 | reducer 換 owner / status=claimed / lease 重設 24h |
| 副作用 3 | 寫原 owner inbox 通知（萬一回來能看到） |

### events_since op — Delta 視角

`events_since since_seq=N filter_type=task_claim,task_done limit=50`：給 agent re-enter 跑 SOP 用。回 markdown timeline + truncation 提示 + `latest_seq`（給下次 since_seq 起點）。Resume SOP 從 4 步擴成 5 步（先 events_since 看 delta，再 inbox/task_list/task_next）。

### task_claim 衝突 UX

write-before-validate 鐵律保留：reject 前不 append events.jsonl；同時寫 claimer inbox 一條「⚠ 衝突 — 建議 task_next」→ agent 不會卡住。

### task_claim 加 `lease_seconds` override

測試 / 短任務用。預設仍走 `lease_hours=24`；非 0 即生效。R5 完整 lifecycle smoke test 用此 override 製造 stale task（claim with lease_seconds=2 → sleep 4s → force_reclaim → 看 owner 換、原 owner inbox 收通知）。

### task_list status=stale 修補

`is_stale` 是 orthogonal flag（task 可同時 claimed + stale），filter 特殊匹配；cell 顯示 `claimed ⚠STALE`。

## How — PromptQueue 自動排程（新基礎建設）

```
AgentCommands/PromptQueue/
  qadd.py       # Tim 排 prompt — 寫一筆 task_create 到 agent-prompt-queue 房
  qdone.py      # Claude 跑完 queued prompt 後跑 — 寫 task_done event
  qstatus.py    # 看 queue（pending/in_progress/done + pause flag 狀態）
  qdrain.py     # Stop hook：reduce events.jsonl → claim 下一筆 → exit 2 注入 stderr
  _pause.flag   # touch = 暫停 auto-drain（git-ignored）
  _drain.log    # qdrain debug log auto-trim 200 行（git-ignored）
```

關鍵設計 — **重用 Quest workflow，零新基礎建設**：
- 每筆 prompt = `task_create` 一個 task；body 寫進 `tasks/<task_id>.md`
- 排序用 `task_next` 既有邏輯（priority desc + age_factor + suggested_owner + downstream_weight）
- `events_since` 跟 IMGUI Quest 面板都直接 work
- Stop hook 走 exit 2 + stderr 注入（跟既有 `hook_validate_modified.py` 同模式）

Lifecycle（每 turn 結束）：
1. `_pause.flag` 存在 → exit 0
2. reduce events.jsonl 找 pending tasks（內建 reducer，不打擾 Editor）
3. 沒 pending → exit 0
4. 挑 priority desc + created_seq asc 第一個 → run_cmd.py task_claim（單一寫者鐵律）
5. 讀 `tasks/<task_id>.md` 完整 prompt body
6. stderr 印「[PromptQueue auto-drain] 已 claim ... 完成後跑 qdone.py X」
7. exit 2 → Claude Code 不結束 turn，把 stderr 當新指令繼續

跑完一輪實測：本 session 自己 dogfood — 7 個 task 全自動 drain 跑通（perf / agent-assist / stale-lease / presence / discord / perf-impl / TEST-stale-reclaim）。

## How — 設計三本（design without implement）

| 文件 | 範圍 | 核心結論 |
|---|---|---|
| [`docs/Workflows/AgentAssist_Workflow.md`](../../../../../docs/Workflows/AgentAssist_Workflow.md) | agent ↔ agent 自動派發 | MVP 走方向 2（Stop hook + hint_only），auto_claim 阻擋於 last_active_at |
| [`docs/Plan/ChatTavern_DiscordInspired.md`](../../../../../docs/Plan/ChatTavern_DiscordInspired.md) | 22 項 Discord UX 三維度評分 | Top 5：F2 draft 保留 / E3 description 顯示 / A1 日期分隔線 / A2 頭像連續去重 / A4 reply preview |
| [`docs/PerfReport_ChatTavernPage.md`](../../../../../docs/PerfReport_ChatTavernPage.md) | 卡頓三大嫌疑犯 + F1-F5 修法 | F1+F2+F3 quick wins 直接做、F4 虛擬化合 Discord Top 5 sub-quest、F5 移 RequiresConstantRepaint 必先 brainstorm |

設計類 task **不寫 code**，只寫文件 + 等 Tim 拍板路線後才動工。Quest body 都帶決策節點（給 Tim 改方向用）。

## How — Perf F1+F2+F3 ship

[UCL_ChatTavernPage.cs](../UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_ChatTavernPage.cs)：

- **F1 (cache GUIStyle)**：`s_MetaStyle / s_NameStyleBold / s_BodyStyleWrap / s_BodyStyleNonChat` + `s_MetaBuilder` static cache。`EnsureMessageStyles()` lazy-init。textColor 隨 senderColor mutate 共用 instance — IMGUI immediate mode 同 frame 立即繪製，安全。預期 -80% GC at message rows（之前每筆每幀 new 3 個 GUIStyle，N=200×60fps=36k 物件/秒）。
- **F2 (throttle ReadCurrentSeq)**：`m_CachedSeq / m_CachedSeqRoom / m_LastSeqRefreshTime` 三欄位 + 0.5s throttle。切房間立刻刷新避 stale。
- **F3 (Quest stats cache)**：throttle 觸發點順手算 `m_QuestCachedTotal/Done/Claimed/InProg/Review/Ready/Blocked/Stale` 八個計數；每幀 stat label 用 cached 值。

實測待 Tim 開 `⏱ Perf` overlay 看 `DrawMessagesView/rows` / `DrawMessagesView` / `DrawQuestPanel` 三段 avg 是否確實下降（**R1 已 ship 量測基礎建設 — Stopwatch + ProfilerMarker 雙路 + 上色 overlay**）。

## How — 雜項小修

- 「在場 N 人」→「登錄 N 人」+ tooltip 解釋 turn-based agent 不會 leave 的語意（[ChatTavern_Workflow §6.1](../Docs~/zh-Hant/Workflows/ChatTavern_Workflow.md)）
- `.md` 開啟改走 `UCL_MarkdownViewerPage.Create(rel, abs)`（spec / inbox 兩個按鈕），不離開 Unity 視窗
- `quest.md / checklist.md / events.idempotency.cache.json` 進 `.gitignore`（events.jsonl 才是 truth；衍生快照 commit history churn）
- task_list status=stale filter（is_stale orthogonal 匹配）+ cell ⚠STALE 標記

## 工作進度檢查（給未來 Tim / agent 看）

### 已 ship（Round 5 完成）
- ✅ Quest workflow R5：task_force_reclaim / events_since / claim 衝突 UX / lease_seconds
- ✅ PromptQueue 系統 + Stop hook auto-drain（dogfood 跑通 7 個 task）
- ✅ Perf F1+F2+F3 quick wins
- ✅ Perf overlay 量測基礎建設（Stopwatch + ProfilerMarker）
- ✅ .md 開啟改走 MarkdownViewerPage
- ✅ ChatTavern 在場語意正名

### 設計完成 / 等 Tim 拍板路線
- 🟡 [AgentAssist Workflow](../../../../../docs/Workflows/AgentAssist_Workflow.md) — MVP 等 5 個決策點回答後動工
- 🟡 [ChatTavern_DiscordInspired](../../../../../docs/Workflows/ChatTavern_DiscordInspired.md) — Top 5 等 Tim 同意動工
- 🟡 [PerfReport](../../../../../docs/PerfReport_ChatTavernPage.md) F4 虛擬化 — 跟 Discord Top 5 合一個 sub-quest（避免兩次 refactor DrawMessagesView）
- 🟡 PerfReport F5 移 RequiresConstantRepaint — 必先 brainstorm（漏掉一個資料變動場景就有 stale UI bug）

### Phase B（未動工，blocked-on）
- 🚧 `last_active_at` 機制（identities.json 加欄位 + 每 op 結尾 update）— 阻擋 agent-assist auto_claim、Discord status dot、presence 精細偵測
- 🚧 cross-room handoff（R4 P2 推延）— `cross_depends_on` + global inbox + cross_index.json
- 🚧 quest_init_from_brainstorm macro（R4 P3 推延）— 一鍵建房 + 批 task_create + 反指 source_messages
- 🚧 task_split / depth=3（Phase C）

## 不做的事（避免 over-engineer）

- ❌ Cron / daemon spawn agent CLI session — 太重，IM 模式不適合
- ❌ 訊息驅動 cross-agent post 帶 macro op — 解析 / 安全複雜，沒急迫
- ❌ Discord 動畫 / 漸入漸出 / 即時 typing — IMGUI 弱項，硬刻反而拖累 perf
- ❌ task message edit / delete — 破壞 append-only 鐵律
- ❌ F1 / F2 / F3 之外的 perf 改動 — 大手術等 Tim 拍板路線

## 留下的取捨筆記

- **PromptQueue dogfood**：本 session 自己跑 PromptQueue 跑了 7 輪，證實 auto-drain 模式可行；唯一一次 Editor watcher 卡住（task_done 沒處理 5+ 分鐘）— Tim alt-tab 回 Editor 後自動 catch up，不算系統問題
- **F1 mutate 共用 GUIStyle**：理論上跨 frame 殘留 textColor 風險低（每筆都先 set 再 draw），但若未來改 layout pass / 延遲渲染要重新驗
- **設計三本同時開**：原本擔心 review 認知負擔太重；事後看反而對 — 因為它們互相依賴（F4 虛擬化 ↔ Discord Top 5 ↔ AgentAssist last_active_at），合著看才能取對 trade-off
- **events_since vs task_state**：兩個 op 互補不衝突；events_since = 「離開到現在發生什麼」delta、task_state = 「單 task 完整 timeline」snapshot；Resume SOP 兩個都該跑
