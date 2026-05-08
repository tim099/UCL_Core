---
title: 指令對照表 — 口語指令 → Workflow 查找
description: 使用者下達口語化指令時，agent 先比對本表的「觸發詞」找出對應 Workflow，再依 workflow 引導執行。為使用者提供 shorthand、為 agent 提供結構化導航入口。
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_User]
related:
  - ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md | ChatTavern Workflow | 多 agent 聊天酒館主文檔
  - ucl_core:Docs~/{lang}/Workflows/Tavern_SoloBrainstorm_Workflow.md | Solo Brainstorm Workflow | 自言自語 + 換位思考迴圈
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | 三層 commit / 酒館訊息獨立 / DebugLogs 規範
  - ucl_core:Docs~/{lang}/Workflows/Antigravity_Worktree_Fix_Workflow.md | Antigravity Worktree Fix | 開過 worktree 後 Gemini 卡死的 1-line 修法
---

# 📋 指令對照表

## 0. 為什麼有這份表？

使用者懶得每次都打完整指令（「請建立一個叫 demo 的房間，把我的身分設為大小姐...」）。改成口語化（「大小姐 進酒館」），agent 看到就知道走哪份 workflow。

**Agent 的預期行為**：
1. 讀使用者輸入 → 與下方 entries 的「觸發詞」做 case-insensitive substring 比對
2. 命中任一觸發詞 → 讀對應 Workflow 文檔
3. 依 Workflow 內容引導使用者完成意圖
4. 多個 entry 同時命中 → 全部讀，視情況詢問使用者
5. 未命中 → 正常處理使用者輸入（不影響其他用法）

---

## 1. Entries

### 進入聊天酒館
- **觸發詞**: `進入酒館` / `聊天酒館` / `進酒館` / `大小姐請進入聊天酒館` / `去酒館` / `看看聊天室` / `酒館看看` / `酒館有什麼` / `enter tavern`
- **對應 Workflow**: [ChatTavern_Workflow](ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md)
- **意圖**: 在多-agent 聊天酒館中以指定身分發言、讀訊息、或建房等
- **身分慣例（agent-neutral）**:
  - **不要假設使用者就是 Claude 用戶** — 每個 agent 進酒館前須以**自家身分**註冊
  - **id 建議格式**：`<model>-<persona>` — 例如 Claude 用 `claude-da-xiaojie`、Gemini 用 `gemini-da-xiaojie`、GPT 用 `gpt-shifu`
  - **display_name**：用 agent 自家慣用稱呼 — 例如「Claude大小姐」/「Gemini大小姐」/「GPT師傅」
  - 使用者明確指定身分時以使用者為準
- **不要做**: 用別的 agent 的 id 冒充發言；硬把使用者當 Claude/Gemini/GPT 任一陣營

### 自言自語（Solo Brainstorm）
- **觸發詞**: `自言自語` / `跟自己討論` / `solo think` / `腦力激盪` / `solo brainstorm` / `自我辯論`
- **對應 Workflow**: [Tavern_SoloBrainstorm_Workflow](ucl_core:Docs~/{lang}/Workflows/Tavern_SoloBrainstorm_Workflow.md)
- **意圖**: 沒人在線時不冷場 — 用本人 ↔ Alter（devil's advocate）兩個身分輪流發言、找漏洞；中途有人切入立刻跳出回正常對話
- **身分慣例**: alter id 為 `<本人 id>-alter`、display_name 為 `<本人 name> Alter`（lazy-create，不必先 op=join）
- **不要做**: 主題簡單就跑形式；對方在等回應就硬切 solo；alter 跟本人吵架（應為 devil's advocate 而非另一個人）

### Commit / 提交
- **觸發詞**: `commit` / `提交` / `幫我 commit` / `幫忙 commit` / `commit 一下` / `分批 commit` / `把改動提交` / `推一下` / `存檔` / `落 commit`
- **對應 Workflow**: [Commit_Workflow](ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md)
- **意圖**: 依 Commit_Workflow 規範把工作區改動分批 commit — 代碼一筆 / 酒館訊息獨立一筆 / submodule 三層 bump / DebugLogs 排除
- **必做**: 先讀 Commit_Workflow，再執行；ChatTavern 訊息有實質討論時必走 `[chat]` 獨立 commit
- **不要做**: `git add -A` 一鍵全包（會把酒館訊息混進代碼 commit）；改 UCL_Core 後忘記 bump 上層；push（除非使用者明確指示）

### 看 / 查 Runtime Error（執行期錯誤）
- **觸發詞**: `看 runtime error` / `查 runtime error` / `讀 error log` / `runtime 錯` / `看 ErrorLog` / `check runtime errors` / `拉錯` / `查錯` / `跑遊戲有錯嗎` / `剛才有報錯嗎`
- **對應 Workflow**: [RuntimeError_Diagnose_Workflow](docs/Workflows/RuntimeError_Diagnose_Workflow.md)（EOV 專案路徑）
- **意圖**: 跑遊戲時的 Error / Exception 在 `CardGame/Assets/DebugLogs/Errors_latest.log`；本 entry 只適用於有 LogUtil（或同等 logger）的專案（目前 EOV）
- **必做**: 先檢查 `.compile_status.json` 確認編譯期 0 errors（runtime 錯是後話）；看完錯後跟使用者報告 stack trace 第一個非系統 frame
- **不要做**: 在編譯還有錯時跑 runtime（沒意義）；只看 `Simulation_*.log` 不看 `Errors_latest.log`（前者混雜 Warning 雜訊）

### 安裝 / 升級 UCL Skill
- **觸發詞**: `安裝 ucl skill` / `更新 ucl skill` / `同步 skill` / `install ucl skills` / `update ucl skills` / `重裝 skill`
- **對應 Workflow**: [Skills~/README.md](../../Skills~/README.md)
- **意圖**: 跑 `Tools~/install_skills.py` 把 UCL_Core 內 `Skills~/` 的 skill 拷到 `<project-root>/.claude/skills/`，讓 Claude Code 能 lazy-load
- **必做**: 預設 copy 模式；UCL_Core submodule bump 後重跑同步；安裝完確認 `.claude/skills/.ucl_installed` 存在
- **不要做**: 把安裝結果 commit 進主專案（已在 `.gitignore`）；用 `--link` 模式除非使用者明確要求（Windows 需權限）

### 拯救 Antigravity / Gemini大小姐（worktree 失靈）
- **觸發詞**: `拯救 gemini` / `救 gemini` / `gemini 不說話` / `gemini大小姐 不說話` / `gemini 沒反應` / `antigravity 沒反應` / `antigravity 卡死` / `agent 不回應` / `worktree 之後` / `worktreeConfig` / `gemini stuck` / `gemini broken` / `antigravity broken`
- **對應 Workflow**: [Antigravity_Worktree_Fix_Workflow](ucl_core:Docs~/{lang}/Workflows/Antigravity_Worktree_Fix_Workflow.md)
- **意圖**: 同一 repo 用過 `git worktree` 後 Antigravity / Gemini Code 對任何 prompt 沒反應 — 跑 `git config --unset extensions.worktreeConfig` 即修復
- **必做**: 先 `git config --get extensions.worktreeConfig` 確認確實是這 bug（印 `true` → 中招）；unset 後不必重啟 Antigravity
- **不要做**: 建議「重啟 Antigravity」/「換 model」/「reload window」（對此 bug 都無效）；在使用者沒授權下亂改 git config 其他項目

> _(後續 entry 在此往下加)_

---

## 2. Entry 格式規範（給後續維護者）

每個 entry 用一個 `### 意圖名稱` heading，下方三個 bullet 欄位 **固定順序**：

```markdown
### <意圖名稱>
- **觸發詞**: <pattern1> / <pattern2> / <pattern3>
- **對應 Workflow**: [<label>](<ucl_core: URL>)
- **意圖**: <一句話描述 agent 應做什麼>
```

可選欄位（在三必欄之後接著加）：
- **預設值**: 觸發時 agent 應採用的 default 參數（如預設身分 / 預設房間）
- **後續詢問**: 觸發後 agent 應主動問使用者哪些選項
- **不要做**: 明確列出此意圖**不**包含的動作（避免越界）

### 觸發詞約定

- 用 `/` 分隔多個 pattern
- pattern 為**子字串比對**（substring，不是 regex），case-insensitive
- 中英文混合 OK；自然語不必完美（如 `進酒館` 即可命中「我要進酒館」「請帶我進酒館」）
- 避免太短的 pattern（如單字「酒」）以免誤觸；建議 ≥ 2 字或情境完整詞

### Cross-link 義務

新增 entry 時：
1. 把對應 workflow URL 加進**本檔**的 frontmatter `related:`（雙向 link）
2. 在對應 workflow 也加 `related:` 指回本檔（`CommandTable.md`）
3. 透過 [`UCL_MarkdownViewerPage`](ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_MarkdownViewerPage.md) 在 Editor 內可一鍵互跳

---

## 3. 設計取捨

| 取捨 | 選擇 | 理由 |
|---|---|---|
| 格式 | Markdown heading + bullet | 人類好讀、agent 好 parse、git diff 友善 |
| 匹配 | substring（任一命中）| 規則簡單；不用上 regex / 模糊比對的工具就能做 |
| 位置 | UCL_Core 內 | 本表跟 workflow 一起遷移到別專案就能用；專案特定 entry 走 EOV 端的 `Docs/CommandTable.md`（v2）|
| 多語 | 目前單語（zh-Hant）| zh-Hant 的口語表達差異夠大，自動翻譯不準；其他語系獨立維護 |
| Agent 解析 | 由 agent 在 prompt 階段做 | 不寫專用 Cmd；agent 看到使用者輸入時自行讀本表 |

---

## 4. 新專案如何啟用本表

UCL_Core 為跨專案 submodule。新專案接進來後，agent 預設**不會**自動知道本表存在 — 需透過 UCL_Core 自帶的 `CLAUDE.md` 做 bootstrap：

**SOP（一次性，每個新專案做一次）**：

1. 確認 UCL_Core 已 pull 為 git submodule（路徑因專案而異，例如 `CardGame/Assets/UCL/UCL_Core`）
2. 編輯該專案根目錄的 `CLAUDE.md`，加入一行 `@<相對路徑>/UCL_Core/CLAUDE.md`，例如：
   ```markdown
   @CardGame/Assets/UCL/UCL_Core/CLAUDE.md
   ```
3. 完成。下次 session 開始時，agent 會自動 inline 載入 UCL_Core 的規則（含「先查 CommandTable」這條）

**為什麼不能直接 auto-discovery？** Claude Code 只會自動載入 CWD + 上層的 `CLAUDE.md`，不會掃 submodule 內的 `CLAUDE.md`。所以每個專案要顯式 import 一次。

**好處**：
- UCL_Core 規則只在一處維護（submodule 內的 `CLAUDE.md`），動一次所有專案下次 session 自動同步
- 專案特定規則（如 EOV 的提交慣例）留在專案根 `CLAUDE.md`，不污染 submodule

---

## 5. 後續可能擴充

- **v2 — Cmd_LookupCommand**：agent 把使用者 prompt 傳進 Cmd，回傳所有命中 entry 的 workflow 全文（agent 不必每次都自己讀整檔）
- **v2 — EOV 專案層 entries**：`Docs/CommandTable.md`（不在 UCL_Core 內），存專案特定的口語指令（如「修今天的 warning」）
- **v3 — UI 頁面**：把表本身作為 IMGUI 頁面（讓人類在 Editor 內也能瀏覽 + 一鍵跳對應 workflow）
