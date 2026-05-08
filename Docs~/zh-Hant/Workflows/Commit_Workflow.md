---
title: Commit Workflow — 提交規範（UCL_Core 三層 + ChatTavern 訊息獨立）
description: 跨專案共享的提交規則 — submodule 三層 bump 流程、submodule 內 commit 前先切 Dev 分支（避免 detached HEAD 游離）、ChatTavern 訊息與代碼分開 commit、DebugLogs / 臨時渲染檔不入 commit、commit message 格式與 prefix 約定。
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_User, Gameplay_Programmer]
related:
  - ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md | ChatTavern 主文檔 | 酒館本身的設計與機制
  - ucl_core:Docs~/{lang}/CommandTable.md | 指令對照表 | 觸發本 workflow 的口語指令清單
---

# 📦 Commit Workflow — 提交規範

> 一句話：**代碼一筆 commit、酒館訊息一筆 commit、submodule 改動三層 bump、ephemeral 檔別碰**。本檔由所有引用 UCL_Core 的專案共享。

---

## 1. 為什麼要有提交規範

- 代碼變動 + 聊天紀錄混一筆 commit → 日後查 history 時雜訊大、git blame 失焦
- submodule 沒 bump 上層 → 同事 / CI 拉下來編譯失敗
- DebugLogs / `_last_op.md` 之類的 ephemeral 進 history → 倉庫膨脹，無價值

本檔釘死「**哪個檔案進哪一筆 commit**」與「**什麼順序動**」。

---

## 2. 檔案分類矩陣

| 類別 | 範例 | 是否 commit | 是否 gitignore |
|---|---|---|---|
| **A. 代碼 / 文檔** | `*.cs` / `*.md` / `Docs~/...` | ✅ | ❌ |
| **B. ChatTavern 永久狀態** | `messages.jsonl` / `identities.json` / `rooms.json` / `members.json` / `_seq.txt` | ✅（獨立 commit）| ❌ |
| **C. ChatTavern 臨時渲染** | `_last_op.md` / `_last_view.md` / `_active_waits.json` / `_wait_*.md` | ❌ | ✅ |
| **D. DebugLogs runtime snapshot** | `Simulation_*.log` / `Simulation_*.log.meta` | ❌ | ❌（保持可見）|
| **E. AgentCommand runtime** | `queue.json`（已存在則 commit、空則略過）/ `pending.trigger*` | ⚠️ 視情況 | ❌ |

> [!IMPORTANT]
> **C 跟 D 的差別**：D（DebugLogs）有歷史價值（每筆 snapshot 是一次運行紀錄，可能想日後翻看）→ 保持 untracked 但**不**進 .gitignore，讓 `git status` 看得到。C（ChatTavern 臨時渲染）每次 cmd 跑就覆寫，沒任何歷史價值 → 直接 .gitignore 隱藏掉。

---

## 3. 三層 Commit 流程（UCL_Core 修改）

UCL_Core 為 git submodule（巢狀於 `UCL` 之下，再巢狀於主專案之下）。修改 UCL_Core 內任何檔案 → **三層都要 commit**：

```
主專案 (EOV / Other)
└── CardGame/Assets/UCL/                  ← 第二層 submodule (UCL)
    └── UCL_Core/                          ← 第三層 submodule (UCL_Core)
        └── 你改的檔案
```

### 3.1 順序

> [!IMPORTANT]
> **submodule 預設 detached HEAD**。Step 1 / Step 2 開始前**必須先 `git switch Dev`** — 否則 commit 落在游離節點，Dev 分支永遠沒前進、push 之後別人或自己 update submodule 拉不到，分支追蹤資訊也會壞。

```bash
# Step 1：UCL_Core 內 commit 程式變動
cd <project>/CardGame/Assets/UCL/UCL_Core
git status -b -s | head -1                  # 確認分支狀態
git switch Dev                              # detached HEAD 必切！
git pull --ff-only                          # 同步遠端，避免 commit 後推不上去
git add <files>
git commit -m "[feat] xxx ..."

# Step 2：UCL（外層 submodule）commit pointer bump
cd <project>/CardGame/Assets/UCL
git switch Dev                              # 同樣必切
git pull --ff-only
git add UCL_Core
git commit -m "[bump] UCL_Core <hash> — <topic>"

# Step 3：主專案 commit pointer bump（主專案通常已在工作分支如 DevTim / Dev，不必再切）
cd <project>
git add CardGame/Assets/UCL
git commit -m "[bump] UCL <hash> — <topic>"
```

### 3.1a 為什麼 submodule 要先切分支

Git submodule 在父 repo 眼裡只是個 commit hash，但 submodule 自身仍需要分支來「容納」commit：

- **detached HEAD 上 commit**：commit 物件存在，但沒任何分支指過去 → push 不到遠端對應分支 → 該 commit 只活在本機
- **bump 的 hash 雖然能 fetch**：但別人 `git submodule update --remote` 會跟 Dev 分支 tip → 拉不到你的 commit
- **長期累積**：detached commit 像浮島，一旦本機 reset / 切分支就回不去（reflog 可救但很煩）

所以鐵律：**動 submodule 內檔案前先 `git switch Dev`，永遠在分支上 commit**。

### 3.2 多個 UCL_Core commit 的 bump 策略

如果一次堆了多個 UCL_Core commit（例如分批改不同主題），**bump 只做一次**，bump message 列出所有 sub-commit 的摘要。例：

```
[bump] UCL_Core <head-hash> — Page Picker / MarkdownViewer related-bar / ChatTavern / CommandTable

5 個 commit：
- 6b03b75 UCL_EditorMenuPage Page Picker + ShowInPageMenu opt-in
- 350b9c4 UCL_MarkdownViewerPage related-bar
- ...
```

---

## 4. ChatTavern 訊息獨立 commit

### 4.1 為什麼獨立

不像 DebugLogs 是純 runtime debug，ChatTavern 的 `messages.jsonl` 是 **設計討論 / agent 協作紀錄 / 決策過程** — 跟代碼一起進歷史才有意義：

- 日後查 PR 時能看到當時 agent / 人類在 ChatTavern 怎麼討論的
- agent 之間真的討論出了結論的話，結論的脈絡留在 git
- 跨 session 的 brainstorm 連續性

### 4.2 哪些檔案要 commit

```
AgentCommands/ChatTavern/
├── identities.json          ← ✅ commit（身分註冊表）
├── rooms.json               ← ✅ commit（房間索引）
├── _last_op.md              ← ❌ gitignore（每次 cmd 覆寫）
├── _active_waits.json       ← ❌ gitignore（runtime wait state）
├── _wait_*.md               ← ❌ gitignore（個別 wait 結果）
└── rooms/<room_id>/
    ├── messages.jsonl       ← ✅ commit（訊息歷史，核心）
    ├── _seq.txt             ← ✅ commit（要與 messages.jsonl 對齊）
    ├── members.json         ← ✅ commit（成員清單）
    └── _last_view.md        ← ❌ gitignore（每次 post 重寫）
```

### 4.3 Commit message 格式

`[chat]` prefix + 房間 + seq 範圍 + 主題（一行）：

```
[chat] <room> seq <from>~<to> — <主題>

(可選 body：列出值得記住的 brainstorm 決策 / 重要結論)
```

範例：
```
[chat] demo seq 14~23 — fire-and-forget wait 設計討論 + smoke test

- Round 1~5 self↔alter 自言自語：fire-and-forget wait 該不該優先做
- Gemini大小姐 提的 _active_waits.json 全域追蹤
- 收尾結論：實作上線（seq 22 為 smoke test post）
```

### 4.4 跟代碼變動分離

> [!IMPORTANT]
> **酒館訊息變動跟代碼 / 文檔變動絕對分開 commit**。
>
> ❌ 不要：`git add -A && git commit -m "[feat] xxx"`（會把酒館訊息混進來）
>
> ✅ 要：先 stage 代碼 / 文檔 → 一筆 commit；再 stage 酒館訊息 → 另一筆 commit。

### 4.5 不必 commit 的時機

純 smoke test / 純 `tag=smoke-test` 的訊息可以略過。判斷標準：**這段對話日後查 git 時值得看到嗎？**
- 設計決策 / brainstorm 結論 → commit
- 「打個招呼」「測試」/ 純連線測試 → 略過

略過時請手動 `git checkout AgentCommands/ChatTavern/rooms/<room>/messages.jsonl` 或 `git restore` 撤銷工作區的測試訊息。

---

## 5. .gitignore 慣例

主專案 `.gitignore` 應包含以下 ChatTavern 臨時檔案（跨專案共用，可直接 copy）：

```gitignore
# ChatTavern 臨時渲染 / 運行狀態（每次 cmd 都覆寫，無歷史價值）
AgentCommands/ChatTavern/_last_op.md
AgentCommands/ChatTavern/_active_waits.json
AgentCommands/ChatTavern/_wait_*.md
AgentCommands/ChatTavern/rooms/*/_last_view.md
```

**不要 ignore** 的（保持 untracked + 視情況 commit）：
- `messages.jsonl` / `_seq.txt` / `members.json` — 永久狀態，要 commit
- `Simulation_*.log` — runtime snapshot，故意保持可見方便除錯（見主專案 CLAUDE.md DebugLogs 規範）

---

## 6. Commit message Prefix 一覽

| Prefix | 用途 | 範例 |
|---|---|---|
| `[feat]` | 新功能 | `[feat] ChatTavern fire-and-forget wait` |
| `[fix]` | bug 修復 | `[fix] AgentCommandsPage History wordWrap` |
| `[refactor]` | 重構 / 不改行為 | `[refactor] Page Picker 改 PopupSearchCache` |
| `[docs]` | 純文檔 | `[docs] Cmd_Tavern.md 更新 wait 章節` |
| `[bump]` | submodule pointer 推進 | `[bump] UCL_Core <hash> — xxx` |
| `[chat]` | ChatTavern 訊息提交 | `[chat] demo seq 14~23 — xxx` |
| `[chore]` | 雜項（gitignore / 設定）| `[chore] .gitignore 加 ChatTavern ephemeral` |

---

## 7. Push 政策

- **default：commit 後不要 push** — 使用者偏好自己手動 push
- 例外：使用者明確指示 push 才推遠端
- 強制 push（`--force`）一律不做，除非使用者**逐次**明確指示

詳見主專案 / 使用者全域 `CLAUDE.md`。

---

## 8. 何時觸發本 workflow

- 使用者下達口語化提交指令（例：「commit 一下」/ 「提交」/ 「幫我 commit」）
- agent 看到應走 [`CommandTable.md`](../CommandTable.md) 的「commit」entry → 讀本檔 → 依本檔規範分批 stage / commit

詳見 [`CommandTable.md`](../CommandTable.md) 的 commit entry。
