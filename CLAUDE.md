# UCL_Core — AI Agent 工作規則（跨專案共享）

> 本檔由所有引用 `UCL_Core` 為 git submodule 的專案共享。
> 在你的專案根 `CLAUDE.md` 加一行 `@<相對於該 CLAUDE.md 的路徑>/UCL_Core/CLAUDE.md` 即可載入本規則。
> 範例（EOV）：`@CardGame/Assets/UCL/UCL_Core/CLAUDE.md`
>
> Claude Code 的 `@` 語法會把目標檔案內容 inline 進當前 CLAUDE.md，故修改本檔 → 所有專案下一次 session 自動同步。

---

## 1. 口語指令處理

當使用者下達口語化的 shorthand 指令（例：「大小姐 請進入聊天酒館」、「進酒館發言」），**先讀** 本目錄下的 `Docs~/zh-Hant/CommandTable.md`：

1. 比對使用者輸入與 CommandTable 的 `Entries` 段落各 entry 的「觸發詞」（substring，case-insensitive，任一命中即可）
2. 命中 → 讀其「對應 Workflow」並依該 workflow 引導執行
3. 多 entry 同時命中 → 全部讀，視情況詢問使用者該走哪條
4. 未命中 → 走一般 prompt 處理（不影響其他輸入）

**路徑解析**：因不同專案把 UCL_Core 掛在不同位置，**本檔的相對路徑相對自身所在目錄**。Agent 看到本檔被載入時，能從 import 路徑反推 UCL_Core 根，再拼出 `Docs~/zh-Hant/CommandTable.md`。

CommandTable 本身定義：[`Docs~/zh-Hant/CommandTable.md`](Docs~/zh-Hant/CommandTable.md)

---

## 2. UCL_Core 文檔索引慣例

UCL_Core 的所有文檔都使用 frontmatter 的 `related:` 欄位互相 cross-link，由 `UCL_MarkdownViewerPage` 解析成可點按鈕（在 Editor 內瀏覽時）：

```yaml
related:
  - <ucl_core: URL> | <label> | <description>
```

新增文檔時：
- 若有 sibling 主題（如「同一個系統的不同層級文檔」），雙向加 `related:` cross-link
- URL 用 `ucl_core:Docs~/{lang}/...` prefix（自動處理多語 fallback）

---

## 3. AgentCommand 系統

`UCL_AgentCommand` 是 UCL_Core 的 RPC 系統，agent 透過 `AgentCommands/queue.json` 對 Editor 發指令。常用：

- `Recompile`：觸發 Editor 重編譯（agent 改完 .cs 後驗證）
- `Tavern`：聊天酒館（多 agent 對話）
- `SearchDocs` / `ExportDocsCatalog`：跨專案文件搜尋

完整指令清單：[`Docs~/zh-Hant/API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md`](Docs~/zh-Hant/API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)

呼叫方式：
```bash
python <path-to>/UCL_Core/Tools~/AgentCommands/run_cmd.py run <Type> --arg key=value
```

---

## 4. 修改 UCL_Core 的注意事項

UCL_Core 是 git submodule，commit 流程為**三層**：
1. 在 UCL_Core 內 commit 程式變動
2. 在 UCL（外層 submodule）commit pointer bump
3. 在主專案 commit pointer bump

別在 UCL_Core 內塞專案特定邏輯 — 這會破壞跨專案重用性。專案特定的 Cmd / Page / 文檔請放在主專案的對應位置（例如 EOV 的 `CardGame/Assets/Scripts/RCG_AgentCommands/`）。

## 5. 提交規範

完整規則見 [`Docs~/zh-Hant/Workflows/Commit_Workflow.md`](Docs~/zh-Hant/Workflows/Commit_Workflow.md)。重點摘要：

- **三層 bump**：UCL_Core 內 commit → UCL submodule bump → 主專案 bump
- **代碼 / 文檔變動 一筆**，**ChatTavern 訊息變動 另一筆**（`[chat]` prefix）— 絕不混 commit
- **臨時渲染檔（`_last_op.md` / `_active_waits.json` / `_wait_*.md` / `_last_view.md`）走 `.gitignore`**，不入 commit
- **DebugLogs (`Simulation_*.log`)** 保持 untracked + 不 ignore（要在 `git status` 看得到）
- 使用者下「commit」/「提交」等口語指令 → 走 [`CommandTable.md`](Docs~/zh-Hant/CommandTable.md) 的 commit entry → 照 Commit_Workflow 分批執行

## 6. Skill 安裝自檢（Claude Code）

UCL_Core 在 `Skills~/` 下提供一組 lazy-load 的 Claude Code Skill（commit / chat-tavern / compile-error / create-cmd / hook-setup）。Claude Code 不掃 submodule，所以每個專案要把 skill 拷到自家 `<project-root>/.claude/skills/`。

**Agent 看到本檔被載入時，做一次性自檢**（**只第一次提，提過就閉嘴**）：

判定流程（檔案路徑都在 `<project-root>/.claude/skills/`）：

1. `.ucl_installed` 存在 → **沉默**（已裝完，正常用）
2. `.ucl_nudge_seen` 存在 → **沉默**（之前提過，使用者自己決定不裝就尊重）
3. 兩個都沒 → 告知使用者一次：
   > 偵測到 UCL_Core 但未安裝 Skill，可跑：
   > `python <UCL_Core>/Tools~/install_skills.py`
   > 裝了之後 commit / 進酒館 / 排查編譯錯等動作會走 lazy-load skill，不必每次吃整份 CLAUDE.md。不裝也能正常工作。

   **告知完立刻寫** `.claude/skills/.ucl_nudge_seen`（內容隨意，存在即可），讓下次 session 跳過。

**不做的事**：
- 不偵測 UCL_Core bump 後是否需重裝（提了反而煩；使用者升 submodule 時自己會記得 / 由 Hook_Setup_Workflow 提醒）
- 不重複 nudge — `.ucl_nudge_seen` 寫了就一輩子安靜，除非使用者明確刪掉

使用者反悔想裝時：直接跑 `install_skills.py`（裝完後 `.ucl_installed` 出現，本檢查永遠走分支 1）。

詳細流程 → [`Skills~/README.md`](Skills~/README.md)

## 7. Runtime Error 檢查

`recompile 0 errors` ≠ runtime 0 errors。改完 code 跑遊戲驗證後，**必看專案的 runtime error log**：

- EOV 專案：`CardGame/Assets/DebugLogs/Errors_latest.log`（每 Editor session 起手清空、agent 直讀）
- 別專案：依自家 logger 慣例

詳見 [`docs/Workflows/RuntimeError_Diagnose_Workflow.md`](../../../docs/Workflows/RuntimeError_Diagnose_Workflow.md)（EOV 端路徑）。

**判斷時機**：你動了 .cs 且使用者實際跑過遊戲 / 操作過 IMGUI Page → 看 log；純文檔 / 沒動 code → 不必看。
