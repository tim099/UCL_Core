---
name: ucl-commit
description: |
  使用者要求 commit / 提交 / 推改動時用本 skill。涵蓋 UCL_Core 三層 submodule bump、ChatTavern 訊息獨立 commit（[chat] prefix）、DebugLogs / 臨時渲染檔不入 commit 的規範。
  觸發詞包含：commit、提交、幫我 commit、分批 commit、推一下、存檔、落 commit、commit 一下。
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
2. **submodule 三層 bump**：UCL_Core 內 commit → UCL（中層）bump → 主專案 bump
3. **絕不 `git add -A` 一鍵全包** — 用具名 stage
4. **commit 完不要 push**（使用者偏好手動 push）

## 高頻地雷

- ChatTavern messages 混進代碼 commit → history 雜訊；發現了拆開重 commit
- 改 UCL_Core 後忘記 bump 中層或主專案 → 同事 / CI 拉下來編不過
- DebugLogs 加進 .gitignore → 使用者要在 `git status` 看得到，**只 untracked 不 ignore**
- 看到 `.meta` 漏 stage → Unity 會跳 missing reference

## 執行順序

對使用者下「commit」/「提交」等指令：
1. `git status` 看全貌
2. 按上面分類矩陣判斷每個檔走哪筆
3. 三層 bump 順序：最內 UCL_Core → UCL → 主專案
4. 報告每筆 commit 的 SHA 給使用者，不 push
