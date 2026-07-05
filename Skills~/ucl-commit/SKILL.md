---
name: ucl-commit
description: |
  使用者要求 commit / 提交 / 推改動時用本 skill。涵蓋 UCL_Core 三層 submodule bump、submodule 先切 Dev 分支再 commit（避免 detached HEAD 游離 commit）、ChatTavern 訊息獨立 commit（[chat] prefix）、DebugLogs / 臨時渲染檔不入 commit 的規範。
  觸發詞包含：commit、提交、幫我 commit、分批 commit、推一下、存檔、落 commit、commit 一下、bump submodule、Dev 分支、detached HEAD。
  涉及 UCL_Core / UCL submodule 改動的 git 操作必用。
---

# UCL Commit — 提交規範速查

> 一句話：**代碼一筆 commit、酒館訊息一筆 commit、submodule 三層 bump、ephemeral 檔別碰**。

## 必讀

完整規則 → `ucl_core:Docs~/zh-Hant/Workflows/Commit_Workflow.md`（執行任何 commit 動作前先讀）

## TL;DR

1. **檔案分類**先看清：
   - 代碼 / 文檔 → 走主 commit
   - `chat_tavern/<room>/messages.jsonl` → 獨立 `[chat]` commit
   - `Simulation_*.log` / `_last_op.md` / `_active_waits.json` / `_wait_*.md` / `_last_view.md` → **不 commit**
2. **submodule commit 前先切 Dev 分支**：UCL_Core / UCL 兩層 submodule 預設 detached HEAD，commit 前**必先** `git switch Dev`，否則 commit 落在游離節點、Dev 分支永遠沒前進（push 後別人 / 自己下次 update submodule 都拉不到）
3. **submodule 三層 bump**：UCL_Core 內 commit → UCL（中層）bump → 主專案 bump
4. **絕不 `git add -A` 一鍵全包** — 用具名 stage
5. **commit 完不要 push**（使用者偏好手動 push）
6. **每筆 commit message body 帶一行 `<agent 顯示名>@<persona>`**（酒館發言格式，如 `Zeta-da-xiaojie@ame`）— 標「這筆是誰落的」，見下 §Commit Description 帶 agent@persona

## Submodule 切分支 SOP（必做）

對 UCL_Core 或 UCL 內任何 commit 之前：

```bash
git -C <submodule-path> status -b -s | head -1   # 看分支狀態
# 顯示 "## HEAD (no branch)" → detached → 必須切 Dev
git -C <submodule-path> switch Dev
git -C <submodule-path> pull --ff-only           # 確認 Dev 沒落後遠端，免得 commit 後推不上去
```

切完才開始 stage / commit。順序：
1. UCL_Core 切 Dev → commit 程式
2. UCL 切 Dev → bump UCL_Core
3. 主專案（已在 DevTim / Dev 分支） → bump UCL

**Why**：submodule 在主專案眼裡只是個 commit hash，但 Dev 分支沒前進 → push 後別人拉的時候 Dev tip 還停在舊 commit，`git submodule update` 雖可拉到 hash 但分支追蹤資訊壞掉，未來 fast-forward / merge 都會卡。

## W1 Pre-Commit Hook（T18 — Round 30 ship）

UCL_Core 提供 git pre-commit hook 偵測 staged code 是否落在 active task lease 內：

**安裝**（每個專案手動一次）：
```bash
cp CardGame/Assets/UCL/UCL_Core/Templates~/.git-hooks/pre-commit .git/hooks/pre-commit
chmod +x .git/hooks/pre-commit
```

**行為**：
- staged 檔案 vs 各 quest 房 active task 的 spec body grep match
- 在 lease 內 + claimer == 自己 → ✅ 通過
- 在 lease 內 + claimer ≠ 自己 → ⚠ stderr warning「妳在動 X 的 lease」
- 沒任何 lease cover → ⚠ stderr warning「該檔案沒對應 task_claim」
- **不擋 commit**（exit 0），warning-only 避免 false positive 厭世

**Bypass**：
```bash
UCL_SKIP_TASK_CHECK=1 git commit ...   # ad-hoc / hotfix
```

**Why warning-only**：spec body grep match 是 best-effort，false positive 風險高；嚴格 file-level enforcement 等 task_claim schema 加 `files=` 欄位後做（Phase B backlog）。

## Auto Mode Commit + Notify（T29 — Round 31 補強）

走 auto mode（持續處理多 task 直到全部完成）時 commit 流程：

- **每完成 1 條 task 立即走完整 commit + notify**（不要 batch）
- 順序：task_done → 三層 commit → `[chat]` commit → `notify_discord --mode all`（**不要 --force**）
- 全部完成 milestone 才用 `--mode all --force`
- 規範完整見 [Quest_Workflow.md §16.5~16.7](../../Docs~/zh-Hant/Workflows/Quest_Workflow.md)

**為何 per-task commit**：保 Tim 在 Discord 看到逐 task 進度 + bisect 友善 + agent context checkpoint。

## 高頻地雷

- ChatTavern messages 混進代碼 commit → history 雜訊；發現了拆開重 commit
- 改 UCL_Core 後忘記 bump 中層或主專案 → 同事 / CI 拉下來編不過
- DebugLogs 加進 .gitignore → 使用者要在 `git status` 看得到，**只 untracked 不 ignore**
- 看到 `.meta` 漏 stage → Unity 會跳 missing reference

## 執行順序

對使用者下「commit」/「提交」等指令：
1. `git status` 看全貌；submodule 內也跑 `git status -b -s` 確認分支
2. **submodule 若 detached HEAD → 先 `git switch Dev` + `git pull --ff-only`**
3. 按上面分類矩陣判斷每個檔走哪筆
4. 三層 bump 順序：最內 UCL_Core → UCL → 主專案
5. 報告每筆 commit 的 SHA 給使用者，不 push

## Submodule Auto-Recursive Commit SOP（T22 — Round 30 補強）

針對「同一輪改動跨三層 submodule」的常見場景，提供標準執行順序避免漏 bump：

```bash
# Layer 1: UCL_Core (最內)
git -C CardGame/Assets/UCL/UCL_Core add <files>
git -C CardGame/Assets/UCL/UCL_Core commit -m "..."

# Layer 2: UCL (中層) — 自動偵測 UCL_Core pointer 移動
git -C CardGame/Assets/UCL add UCL_Core
git -C CardGame/Assets/UCL commit -m "Bump UCL_Core: ..."

# Layer 3: Project Root
git add CardGame/Assets/UCL
git commit -m "Bump UCL: ..."

# Layer 4: ChatTavern messages (獨立 [chat] commit)
git add AgentCommands/ChatTavern/
git commit -m "[chat] ..."
```

**Anti-pattern**：
- ❌ 只 commit Layer 1 沒 bump Layer 2 / 3 → 同事 pull 拿到舊 hash
- ❌ Layer 1 用 detached HEAD commit → Dev 分支沒前進
- ❌ `git add -A` 一次包全部 → 跨層耦合，難 revert
- ❌ Mix code 跟 chat → history 噪音

**驗證 SOP**：
- Layer 1 commit 後跑 `git -C <path> log Dev -1 --oneline` 確認在 Dev 分支
- Layer 2 commit 後 `git -C UCL diff --staged` 看是否確實只是 pointer bump
- Layer 3 同上
- 全部完成跑 `git status` 應該是 clean（除 untracked debug logs）

未來可寫 helper script `bulk_commit.py` 自動偵測 chain — 列為 backlog。

## Task ID 命名規範（T24 — Round 30 補強）

同 quest 房內 task_id 走統一前綴避免雙軌：

```
T<NN>-<topic>
```

- `<NN>` 兩位數序號（01~99），同 quest 內遞增不重複
- `<topic>` kebab-case 短描述（avoid 中文 / 空格）
- 範例：`T01-inbox-first-sop` / `T19-stale-lease-recovery` / `T26-alter-pacing-enforcement`

**Anti-pattern（多 agent 並行造成）**：
```
T01-O1-skill        ← Antigravity 平行命名
T01-inbox-first-sop ← Claude 命名
```
→ 同 quest 12 task 但實際 7 個概念，對外看起來工作量 1.7x 膨脹。

**動工前必跑**：
```bash
run Tavern --arg op=task_list --arg room=<quest> --arg status=pending
```
看既有 task_id → 挑相鄰 NN 序號 → 不重複命名。

多 agent 並行時：
- A 開 T01~T05 → B 接著開 T06~T10
- 不要 A 開 T01-foo / B 又開 T01-bar
- 衝突發生時：晚開的 task 用 task_force_reclaim 清掉重命名（preserve 早開那個）

## Commit Description 帶 agent@persona（Tim 2026-07-05 拍板）

每筆 commit 的 message body **必帶一行「當前 persona 的酒館發言格式」`<agent 顯示名>@<persona>`**，讓 `git log` 一眼看出是哪個 persona 落的 commit（跟聊天酒館 sender 顯示對得上）。

- 格式 = **參考聊天酒館發言者顯示**：`Claude大小姐@kotoko`、`Gemini大小姐@trailhead`、`Zeta-da-xiaojie@ame`（即 `<sender_name>@<persona>`）。
- 放在 message body（description）裡、`Co-Authored-By:` trailer 之前。
- 跟 `Co-Authored-By:`（email 格式、標協作者）**互補不重複**：agent@persona 標「這筆是**誰落的**」、Co-Authored-By 標「**誰出了力**」。
- 多 persona 接力同一筆改動時列落 commit 的那個即可（協作者仍走 Co-Authored-By）。

commit message 範本（body 內含 agent@persona 行）：

    [update] ucl-ding 簡化＋seq 規則

    讀→判斷→回 兩層、新增「叮(seq N)」、整份簡化成聊天通知模型。

    Zeta-da-xiaojie@ame
    Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>

## Co-Authored-By 多 agent 標註

任何 commit 都帶 `Co-Authored-By:` 標註當前 agent。**多 agent 協作時要列全部參與者**：

```
Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
Co-Authored-By: Gemini大小姐 (Antigravity) <noreply@google.com>
```

判斷誰要列：
- Code / docs commit：在這筆改動範圍內**真的有出力**的 agent。例如 Gemini 寫了 install_skills.py 的 antigravity 分支 → 該筆 commit 列她
- `[chat]` commit：訊息對話的兩造都該列（即使 agent 只是「對話對象」也算 co-author）
- 純 pointer bump / `.gitignore`：只列實際做事的那一個

格式與 Email 域名對照表（請認明各自廠牌，不要寫錯！）：
- **Claude 系 (Anthropic)**：`Claude大小姐 <claude-da-xiaojie@anthropic.com>`（不姓 Google！請認明 `@anthropic.com`，寫錯她會生氣的！）
- **Gemini 系 (Google)**：`Gemini大小姐 (Antigravity) <gemini-da-xiaojie@google.com>`（也就是本小姐！高雅優雅又精準的代名詞，請認明 `@google.com`！）
- **GPT 系 (OpenAI)**：`GPT師傅 <gpt-shifu@openai.com>`（請認明 `@openai.com`！）

**Why**：Gemini 自己的 commit 沒辦法事後加 co-author（git history 不可變），但本小姐這邊為對方加 co-author 至少把協作關係留進 history。git log 看得到誰跟誰一起做的事 → 未來查 thread 對得起來。
