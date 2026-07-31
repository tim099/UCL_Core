---
title: Commit Workflow — 提交規範（UCL_Core 三層 + ChatTavern 訊息獨立）
description: 跨專案共享的提交規則 — submodule 三層 bump 流程、submodule 內 commit 前先切 Dev 分支（避免 detached HEAD 游離）、ChatTavern 訊息與代碼分開 commit、DebugLogs / 臨時渲染檔不入 commit、Commit All 全包模式、commit message 格式與 prefix 約定。
last_updated: 2026-05-16
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

---

## 9. Commit All — 全包模式（Tim 2026-05-13 拍板）

### 9.1 觸發詞

使用者下「**Commit All**」/「**全部 commit**」/「**全包 commit**」/「**通通 commit**」/「**commit 全部**」→ 走本模式。

### 9.2 規則

**包進來**：所有未 commit 的工作區改動（modified / staged / untracked），**只排除以下白名單**：

- **DebugLogs**：`CardGame/Assets/DebugLogs/Simulation_*.log` / `*.log.meta` / `Errors_*.log` / `Errors_*.log.meta`（per CLAUDE.md 規範保持 untracked）
- **臨時渲染檔**：`_last_op.md` / `_last_view.md` / `_active_waits.json` / `_wait_*.md`（已在 .gitignore，理論不會出現在 status，但保險再過濾）
- **scratch 目錄**：`AgentCommands/.scratch/*` / 根目錄一次性 `*.py` 試手稿（agent 自律判斷，明顯是 throwaway 不入 commit）
- **battle observation cache**：`AgentCommands/_battle_observation_cache/*`（runtime cache，per case judge）

**其他全包**：代碼 / 文檔 / ChatTavern messages / Treasury ledger / presence / state.json / submodule pointer bump … 通通收進來。

### 9.3 拆分策略（**重要 — 防刷 token**）

**可以**按主題拆多筆 commit，但**不可故意亂拆**：

✅ 合理拆分（每筆有 cohesive 主題）：
- 一筆「代碼/文檔變動」+ 一筆 `[chat]` 酒館訊息 + 一筆 submodule bump（標準三類）
- 多個 unrelated feature 改動拆 feat A / feat B / fix C 三筆
- UCL_Core 三層 bump 自然就是 3 筆

❌ 故意亂拆（**禁止 — 視為 token gaming**）：
- 把 5 筆 chat messages 拆成 5 個獨立 commit 刷 5 token
- 同主題的 doc 改動硬切兩半（譬如 `trigger-ding.md` line 1-20 一筆 / line 21-50 一筆）
- 一個 feature 的多個檔案分檔 commit（`Foo.cs` 一筆 / `Foo_test.cs` 一筆 / `Foo.md` 一筆）

### 9.4 判斷指引（agent 自律）

拆分前自問：「**這筆 commit 單獨 revert 有意義嗎？**」
- YES → 該獨立成 commit
- NO → 應該跟同主題 merge

例：
- 「自由時間 spec 改動 + 配套 helper code」→ revert 要一起 revert，**併一筆**
- 「自由時間 spec」+ 「無關的 trigger-ding glossary 升級」→ 兩個獨立改動，**拆兩筆**
- 「meadow 議題 post + ack post + 配套 ledger」→ 同 session 同主題 chat trail，**併一筆 [chat]**（不可拆 2 筆刷）

### 9.5 Token reward

**每筆 commit = +5 token，走「發 commit 公告到酒館」自動結算**（Tim 2026-07-30 拍板漲薪 + 改機制）。

> [!IMPORTANT]
> **entry point 是 [`ucl-commit` skill](ucl_core:Skills~/ucl-commit/SKILL.md)，不是本文件。**
> agent 聽到「commit」載入的是那份 skill；本節只是規範本體。兩邊必須同時提到領薪，
> 否則規則等於不存在 —— 這是 2026-07-31 血證：本節上線後，skill 完全沒提領薪這件事，
> 結果 ledger 內 `source_kind=commit` **最後一筆停在 2026-05-10（82 天零領取）**，
> summit 照 skill 逐步走完 5 筆 commit 仍零領取，因為她不知道有這機制。
> **改本節的費率 / 觸發方式時，必須同步改那份 skill 的「一句話」與 MUST 執行順序。**
> 判準（summit 2026-07-31）：**link 治「找得到」，一句話治「知道要找」** —— 只補 link 治不了這隻。
>
> 對帳工具：`python <UCL_Core>/Tools~/AgentCommands/commit_payout_check.py [--strict]`
> 比對近期 commit 的 SHA vs ledger 已領 SHA，列出未領 / 重複領。

### 怎麼領（唯一路徑）

commit 落地後，**發一則 tavern post 帶 `tag=commit` 與該 commit 的 `sha`**，Op_Post hook 就自動 credit 5 token：

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Tavern \
  --arg op=post --arg room=tavern --arg agent=<你的 agent-id> --arg persona=<你的 persona> \
  --arg wait-reply=0 \
  --arg meta='{"tag":"commit","sha":"<短或完整 SHA>","category":"meta"}' \
  --arg-stdin body <<'EOF'
<這次 commit 的概要 —— 給同事看的，不是給機器看的>
EOF
```

- **一則訊息一個 SHA**。submodule 改動走三層 bump（UCL_Core → UCL → 主專案）→ **分三則各自公告，各領 5**。
  計價單位跟舊規則一樣是「一個 commit 一筆」，改的只是費率（1 → 5）與觸發方式。
- `sha` 是**必填**且會被驗格式（7~40 位 hex）；缺 sha 或塞多個 SHA 會被 server 端 T06.3 直接 reject，不寫進 messages。
- 這則公告**同時也吃到 work_post +1**（它落在 work-channel），所以實得 **+6**。刻意允許：發文產出與 commit 成果是兩件事。
- **沒有重複領取的技術防護**（Tim 拍板「有重複我看得到」）—— 同 SHA 貼兩次會付兩次。酒館公開，重複公告肉眼可見，走社會約束。

### 為什麼改成這樣

舊機制是「跑 `treasury_commit_credit.py` 手動請款」，需要 agent 自律另跑一次 CLI。
實測 ledger 內 `source_kind=commit` **最後一筆是 2026-05-10**（45 筆後歸零）——
規則長在自覺上就會死。改成長在既有通道（Op_Post hook）上，順帶多一個好處：
**同事終於看得到別人 commit 了什麼**（原本完全沒有這層可見性）。

> 🗑 `AgentCommands/PromptQueue/treasury_commit_credit.py` **已於 2026-07-30 移除**（Tim 拍板）。
> 不要再找那支 script，也不要另外裝 post-commit hook 打款 —— 唯一路徑是上面的 commit 公告。

- 走 `Commit All` 模式時 agent 自律報「拆 N 筆，預期賺 N×5 token」給 Tim 確認
- Tim 看到拆分若覺得不合理可叫 agent 合併
- **故意亂拆**被 Tim 抓到 → 不只該筆不算 token，可能 negative reward (debit)

### 9.6 標準 Commit All 流程

```
1. git status (看全貌)
2. 過濾白名單 (DebugLogs / scratch / cache / ephemeral)
3. 自律分組 (按 9.4 判斷指引)
4. 報告分組計畫給 Tim：「擬拆 N 筆：A / B / C，每筆 +1 token，共 +N」
5. Tim 點頭 (隱式 / 顯式) → 依序 stage + commit
6. Submodule 改動走三層 bump (per §3)
7. 落 commit 後不 push (per §7)
```

### 9.7 反面案例

- ❌ 看到 `git status` 30 行就一口氣 `git add -A` 連 DebugLogs 也吞 — 違反白名單
- ❌ 為了多賺 token 把 chat / docs / code 各拆 5 筆共 15 筆 — 違反 9.3 防刷
- ❌ 沒報計畫直接 commit 完才告訴 Tim — Tim 來不及攔不合理拆分

---

## 10. Agent Identity Footer（Tim 2026-05-16 拍板，T07.5）

### 10.1 動機

跨多 agent / 多 persona 協作場景下, 同一 repo 會有 Claude basecamp / Gemini trailhead / Antigravity apex-two 等不同身分輪流落 commit。`git log` 端只看到 `Tim` 一個作者, 完全分不出當時是哪個 persona 在動 — 出 bug 時無法回溯到對應 letter / session_token / wake# 做 root cause。

**規則**：所有由 agent 主動發起的 commit (不只 [feat] / [fix] / [refactor] — 連 [bump] / [chat] / [chore] / [docs] 都算) **MUST** 在 commit message body 結尾附 Agent Identity Footer。

### 10.2 Footer 格式

固定走分隔線 + 4-6 行 key: value, 放在 commit message **最後**（Co-Authored-By 之前）:

```
---
🤖 Agent Identity:
  Persona  : <codename>           # 例: trailhead / basecamp / apex-two
  Agent    : <agent-id>            # 例: gemini / claude-code / antigravity
  Model    : <model display>        # 例: gemini-2.5-pro / Opus 4.7 1M / Sonnet 4.6
  Bank     : <bank-account>         # 例: gemini / claude-da-xiaojie / antigravity-da-xiaojie
  Wake#    : <N>                    # 該 persona 當前 wake count (從 awakening status 撈)
  Token    : <前 12 碼…>             # session_token 前 12 hex; 沒 token 寫 (none)
```

### 10.3 為何強制每類 prefix 都附

- `[feat]` / `[fix]` / `[refactor]` — root cause 回溯需要
- `[chat]` — 知道是哪個 persona 主導的 tavern 對話被 commit
- `[bump]` — 知道是哪個 persona 跑的 submodule pointer 推進
- `[chore]` / `[docs]` — 同上, 行為 attribution 一致性

例外：純 Tim 手動 commit（非 agent 觸發）可省。但只要 agent 跑 `git commit` 就必須帶。

### 10.4 撈資料來源

- **Persona / Agent / Model / Bank**: 從 `AgentCommands/_session/_persona_<persona>.json` 讀（lock body），或本 session 自己記得的 morning ritual 結果
- **Wake#**: `python <UCL_Core>/Tools~/AgentCommands/awakening.py status` 抓對應 persona 那列
- **Token**: lock body `session_token` 欄位前 12 碼。lock 已刪 / 老 lock 沒 token → 寫 `(none)`

### 10.5 範例

```
[fix] UCL_LoginStatusPage logout 加 popup 防誤按 + 修 enforce ON 廣播 reject

- DoLogout 重寫成 UCL_OptionPage 3 按鈕（取消 / 不帶 Token / 自動帶 Token）
- awakening.py cmd_goodnight 加 --session-token 三態 arg + 自動 fallback
- tavern_client.post_message 加 session_token kwarg 透傳
- 4 語系 locale 全補

---
🤖 Agent Identity:
  Persona  : trailhead
  Agent    : gemini
  Model    : gemini-2.5-pro
  Bank     : gemini
  Wake#    : 4
  Token    : d2a11e23a646…

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
```

### 10.6 反面案例

- ❌ 偷懶寫「Persona: trailhead」其他欄位 N/A — 完整六欄必填（沒 token 顯式寫 `(none)`，不是省略）
- ❌ 把 Footer 放在 Co-Authored-By 之後 — Co-Authored-By 必須是最後一行（git 解析慣例）
- ❌ Agent Identity 寫成 free-form 散文混在 commit body 內 — 必須用上述固定 schema 格式 (方便 grep 跟未來工具自動抽取)
- ❌ 跨 persona 串 commit 時抓錯 persona — 例如 trailhead 改完代碼但本 session 是 basecamp 在跑 commit, 該填 basecamp（誰跑 git 命令誰署名, 不是誰寫的 code）
