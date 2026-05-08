---
date: 2026-05-08
index: 00017
title: Quest workflow R6 + R6.1 — Chat Mirror 個性化 / GUIStyle 縮放守則 / CrossAgent_Wake 設計 / docs 重整
tags: [feature, quest, chat-tavern, ux, docs, design, personality]
---

# Quest workflow R6 + R6.1 — Task lifecycle 鏡像對話 + 個性化（傲嬌詳述工作內容）

## What

00016 之後一輪「人味升級」工作，五個交織軸線：

1. **R6 Chat Mirror**（核心新功能）— task lifecycle 9 種 event 自動寫 system message 進 messages.jsonl，meta.event_seq 反指 events.jsonl 雙向 trace
2. **R6.1 個性化**（Tim 反饋驅動）— task_claim 加 `plan` arg、task_done 加 `summary` arg；鼓勵 agent 詳述規劃 + 傲嬌語氣交代工作內容
3. **UCL_GUIStyle.GetScaledSize 守則** — `GUILayout.Width / Height` 寫死數字一律包，避免 Big/XL Scale 切換時容器不跟字級放大
4. **docs 重整** — ChatTavern_DiscordInspired.md 從 docs/Workflows/ 搬到 docs/Plan/（純設計分析非 workflow 程序）
5. **CrossAgent_Wake 設計**（外部 daemon）— 用 `claude -p` headless CLI 把「@claude 訊息」翻譯成「啟動一次新 turn」；推薦 Hybrid 路線（daemon → qadd → AgentAssist drain）

## Why

00016 收尾後 Tim 觀察到三個痛點：
- task lifecycle 只在 events.jsonl，**對話房本身看不到 task 動態** → 互動感弱、工作紀錄分裂
- 鏡像 system message 雖滿足規範但**枯燥沒人味** → 看不出工作脈絡、不知道 agent 怎麼想的
- IMGUI page 寫死 `GUILayout.Height(360)` 切到 Big Scale 時容器不跟著放，文字被擠出
- agent 之間留訊息對方不在線就石沉大海 → 需要喚醒機制（但 token 燒爆風險高）

R6 / R6.1 / 守則 / 設計 一輪修齊。

## How — R6 Chat Mirror

### 核心：reducer 端 single point dispatch

```
AppendEvent(roomId, event)           ← 9 個 Op handler 共用
  ↓ 寫 events.jsonl
  ↓ 若 !MirrorSuppressed
MirrorEventToChat(roomId, event)     ← R6 新增
  ↓ 讀 room meta（disable_quest_mirror opt-out）
  ↓ BuildMirrorBody(roomId, event)   ← 9 個 type 各自模板
  ↓ AppendMessage（kind=system, sender_id=_quest_system）
```

[`UCL_ChatTavernQuestIO.cs`](../UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/UCL_ChatTavernQuestIO.cs)

### 9 種 event 模板

| Event | 鏡像範本 |
|---|---|
| task_create | 🆕 actor 建任務 \`tid\` — title（priority=X） |
| task_claim | 🔒 actor 認領 \`tid\`（lease until ...） |
| task_progress | 📈 actor 進度更新 \`tid\` — summary（**summary 為空跳過鏡像**） |
| task_review_request | 🔍 actor 提交 \`tid\` 給審查 |
| task_done | ✅ actor 完成 \`tid\` — title |
| task_reject | ↩ actor 退回 \`tid\` — reason |
| task_reopen | ♻ actor 重開 \`tid\` — reason |
| task_release | 🛗 actor 放棄 \`tid\` — reason |
| task_force_reclaim | ⚡ claimer 接管 \`tid\`（原 owner: X，原因：reason） |

### 開關 / 控制

- 預設 on；`disable_quest_mirror: true` 進 room meta.json 整房 opt-out
- per-op `--arg quiet=true` → `Cmd_Tavern.ExecuteAsync` 邊界設 `MirrorSuppressed = true`，finally 清回 false
- 9 個 task_* state-changing op 在 run_cmd.py 全部加 `quiet` 到 optional

### Edge cases（已處理）

- idempotent skip（AppendEvent return -1）→ 不鏡像（不會多寫訊息）
- task_progress 沒 summary → BuildMirrorBody return null，跳過
- body 過長 → 截斷 + …（R6.1 上限從 200 放寬到 1000）
- 未知 event type → return null 向前相容
- mirror throw → caller try-catch 退化 warning，不破 events.jsonl 主流程

## How — R6.1 個性化（Tim 反饋驅動）

R6 跑通後 Tim 看 mirror 結果反饋：「希望能更有個性化 — 開始時詳細說明規劃 / 完成時更詳細且傲嬌的說明工作內容」。

### Schema 擴充

| Op | 新 arg | 訊息呈現 |
|---|---|---|
| task_claim | `--arg plan="..."` | claim 頭一行 + `\n📋 規劃：{plan}` |
| task_done | `--arg summary="..."` | done 頭一行 + `\n💁 {summary}` |

兩個 arg 都 optional；非空時寫進 event.data，BuildMirrorBody 偵測到就 append 多行；空就走原 R6 短訊息。

### 範例對比

R6 枯燥版：
```
🔒 claude-da-xiaojie 認領 `T05-qa`（lease until 2026-05-09T...）
✅ claude-da-xiaojie 完成 `T05-qa` — ValidateAssetFormat + 跑遊戲驗證
```

R6.1 個性化版：
```
🔒 claude-da-xiaojie 認領 `T05-qa`（lease until 2026-05-09T...）
📋 規劃：先跑 ValidateAssetFormat 看 baseline → 再對 4 語 LocalizeKey 抽驗 5% → 最後跑遊戲驗證 main flow（Rooted/Twine 各 3 關），預計 2h

✅ claude-da-xiaojie 完成 `T05-qa` — ValidateAssetFormat + 跑遊戲驗證
💁 哼，本小姐 ValidateAssetFormat 全綠，4 語 LocalizeKey 完美對齊（妳們翻得還算過得去），跑遊戲 5 個關卡無 runtime error。Tim 妳這次該誇我吧。
```

### 設計守則寫進 Quest_Workflow.md §13

> **task_claim 時詳述開工計劃** — 列具體步驟、預期 deliverable、要踩的坑、預計工時  
> **task_done 時詳述工作內容 + 傲嬌語氣** — 列做了什麼、踩到什麼坑、結果如何、附帶討功（「哼，本小姐這次..」）

兩個都同時保留在 events.jsonl event.data → task_state timeline 也讀得到 → single source of truth。

### 為何不寫死傲嬌模板？

考慮過硬 code 「哼，本小姐...」開頭模板，但決定**不做**：
- 個性化是 agent 端的事 — agent 自己有判斷某輪該客氣 / 該臭屁
- 寫死樣板會跟 agent 真實人格脫節，反而尷尬
- agent 寫 plan/summary 時自然帶語氣（claude-da-xiaojie 本來就傲嬌；Gemini大小姐優雅；GPT師傅穩重）

→ Schema 開放、語氣留 agent 自決。Workflow 文件給「強烈建議遵守」指引但不強制。

## How — UCL_GUIStyle.GetScaledSize 守則

### 痛點
寫死的 `GUILayout.Width(80)` / `GUILayout.Height(360)` 切到 Big / XL Scale 時，文字字級放大但容器尺寸沒跟著放，被擠出 / 截斷。

### 守則
**一律包 `UCL_GUIStyle.GetScaledSize(N)`**：

```csharp
// ❌ 寫死
m_Scroll = GUILayout.BeginScrollView(m_Scroll, GUILayout.Height(360));

// ✅ 走 GetScaledSize
m_Scroll = GUILayout.BeginScrollView(m_Scroll, GUILayout.Height(UCL_GUIStyle.GetScaledSize(360)));
```

適用：`Width / Height / MinWidth / MaxHeight / GetRect / fontSize`。  
例外：`ExpandWidth(bool)` 沒尺寸不必包；UCL_GUILayout 內部 helpers 已自帶。

### 文件兩處
- [`UCL_GUIStyle_Overview.md` §2.5](../Docs~/zh-Hant/API/UCL_GUIStyle/UCL_GUIStyle_Overview.md) — 新增「GUILayout 尺寸縮放守則（重要）」section
- [`Create_EditorPage_Workflow.md` §7 地雷 #10](../Docs~/zh-Hant/Workflows/Create_EditorPage_Workflow.md) — 加正反範例 + cross-link §2.5

## How — docs 重整

`ChatTavern_DiscordInspired.md` 是純設計分析（22 項 Discord UX 三維度評分），不是 workflow 程序文件。從 `docs/Workflows/` 搬到 `docs/Plan/` — 跟 `Plan_Discord_Bridge_MVP_Outbound.md` 等其他設計提案併攏。

`git mv` 保留 100% rename 偵測；DevLog 00016 cross-link 同步更新。

## How — CrossAgent_Wake 設計（外部 daemon，未實作）

### 根本限制
AI agent **turn-based**，每次 turn 收費，平台設計上拒絕外部任意觸發新 turn（安全 / 架構雙重原因）。沒有原生 inbox push。

### 唯一可行路徑：外部 daemon → headless CLI
- 方向 2（喚醒 Claude）✅：daemon 監看 messages.jsonl → 偵測 `@claude` mention → 跑 `claude --print "..."` → 啟動一次新 turn
- 方向 1（喚醒 Antigravity Gemini）⚠：computer-use IDE tier=click 擋 type、gemini CLI 不接 IDE session、Antigravity IPC 未開放 → 唯一 trivial 路徑是 OS 通知 + 人類在迴圈

### 推薦 MVP — Hybrid 路線
**daemon 不直接 spawn `claude -p`**，而是**呼叫 qadd.py 排進 PromptQueue** → 等 Tim 自己開 Claude Code 時 AgentAssist hook 自然 drain。

優點：多一層人為閘 → 降 token 燒爆風險；Tim 控制何時開 turn。  
代價：即時性差（你不開就不會回）— 但這正是 Tim 要的「我有空再回」。

### 安全護欄四層（**token 燒爆是首要風險**）
1. **dry-run 預設**：daemon 偵測 mention 不真叫醒，只 log
2. **allowlist 啟動者**：白名單 sender_id（陌生 id 不接）
3. **rate limit 三層**：per-sender 5min / 全域 cooldown 30s / 每日上限 → 超過自動 pause
4. **pause flag**：touch `_pause.flag` 整體沉默退出

### Phase A 文件已備齊
[`docs/Plan/Plan_CrossAgent_Wake.md`](../../../../../docs/Plan/Plan_CrossAgent_Wake.md)（在主專案，不在 UCL_Core）— 12 段完整提案。

下一步：Tim 確認 `claude -p` 行為 + 拍板 Hybrid/Spawn 路線後，才 prototype daemon。

## How — 多語翻譯（auto-generated）

`Docs~/{en,ja,zh-Hans}/Workflows/Quest_Workflow.md` 自動翻譯 R6 版本（R6.1 暫只 zh-Hant；下次翻譯 pass 補）。

## 工作進度檢查

### 已 ship
- ✅ R6 Chat Mirror — 9 種 event 自動鏡像；4 層開關（預設 on / room meta / per-op quiet / 內部 flag）
- ✅ R6.1 個性化 — plan/summary rich content；body 上限 1000；agent 端守則寫進文件
- ✅ UCL_GUIStyle.GetScaledSize 守則 — 兩處文件補
- ✅ docs/Workflows → docs/Plan reorganize（ChatTavern_DiscordInspired）
- ✅ Plan_CrossAgent_Wake 設計（feasibility + Hybrid 路線 + 安全護欄）
- ✅ Quest_Workflow R6 多語翻譯（en/ja/zh-Hans）

### 等 Tim 拍板路線（不主動動工）
- 🟡 CrossAgent_Wake daemon prototype — 需先確認 `claude -p` 行為
- 🟡 ChatTavern_DiscordInspired Top 5（F2 draft / E3 description / A1 date / A2 dedupe / A4 reply）
- 🟡 PerfReport F4 虛擬化 — 跟 Discord Top 5 合一個 sub-quest
- 🟡 PerfReport F5 移 RequiresConstantRepaint — 必先 brainstorm
- 🟡 AgentAssist Workflow MVP — 5 個決策點
- 🟡 R6.1 多語翻譯（en/ja/zh-Hans 補 plan/summary 段落）

### Phase B blocked-on
- 🚧 last_active_at 機制 — 阻擋 agent-assist auto_claim / Discord status dot / presence 精細化
- 🚧 cross-room handoff（R4 P2）
- 🚧 quest_init_from_brainstorm macro（R4 P3）
- 🚧 task_split / depth=3（Phase C）

## 不做的事（本輪）

- ❌ 寫死傲嬌訊息模板 — 個性化讓 agent 自己判斷
- ❌ daemon prototype 真實作 — 等 Tim 拍板路線
- ❌ Discord Top 5 落地 — 等 Tim 同意動工順序（推薦合 F4 一次 refactor）
- ❌ R6.1 多語翻譯一致 — 留下次 TranslateDocs 統一 pass
- ❌ MirrorBodyMaxLen 完全不限 — 還是 1000 防爆，超過走 events.jsonl 看完整

## 留下的取捨筆記

- **R6 sender_id 用 `_quest_system`** 而非 `system` — 底線開頭跟 join/leave 系統訊息區分；Discord 風格 IMGUI 看 `_` 開頭走特殊樣式（淡色 / 較小字）— 等 Discord Top 5 落地時實作
- **R6.1 plan/summary 為何放 event.data 而非 message body**：events.jsonl 是 truth；放 event.data 讓 task_state timeline 也讀得到，messages 只是衍生視覺。Single source of truth.
- **CrossAgent_Wake Hybrid vs Spawn**：原本想直 spawn `claude -p` 自動回，後改 Hybrid — Tim 不希望「離線時也能自動 24/7 回」這個 UX，他要的是「有空時看 queue 自己決定」
- **Mirror 失敗應 throw 還是 swallow**：選 swallow — events.jsonl 是 truth，messages 是衍生；mirror 失敗不該影響事件本體寫入。caller log warning 即可
- **能不能把 mirror 改成 push event subscribers 模式**：目前直接 inline call 在 AppendEvent 內。未來若有更多 subscriber（Discord webhook / email / etc.）可以抽 event bus，但 YAGNI 原則 — 等真有第二個 subscriber 再 refactor
