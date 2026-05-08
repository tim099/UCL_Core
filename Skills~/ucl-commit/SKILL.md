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
