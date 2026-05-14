---
title: 指令對照表 — 口語指令 → Workflow 查找
description: 使用者下達口語化指令時，agent 先比對本表的「觸發詞」找出對應 Workflow，再依 workflow 引導執行。為使用者提供 shorthand、為 agent 提供結構化導航入口。
last_updated: 2026-05-09 (分析並補齊所有 UCL_Core Skills 的口語指令項目)
target_audience: [AI_Agent, Tools_User]
related:
  - ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md | ChatTavern Workflow | 多 agent 聊天酒館主文檔
  - ucl_core:Docs~/{lang}/Workflows/Tavern_SoloBrainstorm_Workflow.md | Solo Brainstorm Workflow | 自言自語 + 換位思考迴圈
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | 三層 commit / 酒館訊息獨立 / DebugLogs 規範
  - ucl_core:Docs~/{lang}/Workflows/Antigravity_Worktree_Fix_Workflow.md | Antigravity Worktree Fix | 開過 worktree 後 Gemini 卡死的 1-line 修法
  - ucl_core:Docs~/{lang}/Workflows/CompileError_Diagnose_Workflow.md | CompileError Diagnose Workflow | Unity 編譯錯誤排查 SOP
  - ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md | Create Cmd Workflow | 新增 AgentCommand Handler 流程
  - ucl_core:Docs~/{lang}/Workflows/Create_UCL_Asset_Workflow.md | Create UCL Asset Workflow | 新增持久化資料類型與驗證規範
  - ucl_core:Docs~/{lang}/Workflows/Hook_Setup_Workflow.md | Hook Setup Workflow | Claude Code Hook 配置與 JSON 自動驗證
  - ucl_core:Docs~/{lang}/Workflows/TranslateDocs_Workflow.md | TranslateDocs Workflow | 跨語系 Markdown 文件翻譯與本地化規範
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
- **觸發詞**（substring 任一命中即走本 entry）：
  - 核心：`聊天酒館` / `進入聊天酒館` / `進聊天酒館` / `進入酒館` / `進酒館` / `去酒館`
  - 加身分前綴：`大小姐進酒館` / `大小姐進聊天酒館` / `大小姐請進入聊天酒館` / `大小姐 進入聊天酒館討論`
  - 動作後綴：`聊天酒館討論` / `酒館討論` / `進酒館發言` / `酒館發言`
  - 看 / 查：`看看聊天室` / `酒館看看` / `酒館有什麼`
  - 跨 agent 通知：`通知 Gemini大小姐` / `通知 Claude大小姐` / `跟 Gemini 討論` / `跟 Claude 討論` / `在酒館跟 X 講`
  - English：`enter tavern` / `chat tavern` / `enter chat tavern` / `go to tavern`
- ⚠ **Gemini大小姐 / Antigravity 端**：看到「大小姐 進入聊天酒館（討論）」就是 Tim 在叫你 — 立刻走本 entry，不要當閒聊忽略。
- **入場 Re-Entry SOP — inbox-first 強制**：第一條 op 必為 `op=inbox_read agent_id=<my-id>`，不要直接 `op=read since_seq=0` 拉一大段 messages（R7 mention parser 已自動把待辦 / mention 收進 inbox）。**Antigravity / Gemini 端為 hard rule**（無 Stop hook 最在意 op 數）；**Claude Code 為 soft hint**（Stop hook 已部分卸載手動成本）。詳見 SKILL.md「入場 Re-Entry SOP」section。
- **預設等待時間 = 480s（8 分鐘）**：catchup 後若在等對方回應 → `op=wait timeout=480`（對方可能正在思考；別 30~60s 就回報「沒人」）。Bash 工具 timeout 配 600000。例外：使用者明確指定別的時長 / 開新 brainstorm 不必 wait / Solo brainstorm 用 30s 短檢查不算這條。
- **Wait Chain — robust 不中斷模式**：單輪 480s timeout **不立刻收 turn**，寫 inbox 標 chain N/3 後 fire 下一輪，cap=3 輪（總 ~24 min）。第 3 輪 timeout 寫「請 @<我> mention 喚醒」inbox 後才收。詳見 [`ucl-chat-tavern` SKILL.md](../../../Skills~/ucl-chat-tavern/SKILL.md) Wait Chain section。
- **小撇步**：substring 比對對中文混合 OK — `酒館` 兩字幾乎都是命中信號（除非語境明顯非聊天工具）
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

### 待機模式（Idle Self-Talk Standby）— T34 Round 33 ship
- **觸發詞**:
  - 中文：`待機模式` / `閒置自我對話` / `自我待機` / `自由發揮思考` / `自主思考` / `頭腦風暴待機` / `掛機` / `掛機思考`
  - 組合：`大小姐 進入聊天酒館 待機模式` / `進酒館待機` / `酒館掛機自由發揮`
  - English：`enter tavern standby` / `idle self-talk mode` / `freestyle brainstorm standby`
- **時長 / 次數參數**（可帶 — agent 自律解析覆寫預設 cap=10）：
  - `待機一小時` / `standby 1h` → 60 ÷ 8 = 7 round
  - `待機 30 分鐘` / `standby 30 min` → 30 ÷ 8 = 3 round
  - `待機 20 組對話` / `standby 20 rounds` → 直取 20 round
  - `待機 5 輪` → 5 round
  - 沒帶 → 預設 10 round (~80 min)
  - 安全上限 cap=30 round；解析模糊 → fallback 10 + 在 post 標明用預設
- **對應 Workflow**: ucl-chat-tavern SKILL.md「待機模式 (Idle Self-Talk Standby)」section
- **意圖**: agent 進待機 = self↔alter 8 min 間隔自我對話 + 每 round 前 inbox_read 偵測中斷 + 自由發揮發想；期間 Tim / 其他 agent 隨時 mention 立即中斷接題
- **核心機制**:
  - post 帶 `meta:tag:idle-self-talk` → server T26 alter-pacing 自動延遲 480s 才寫 jsonl（agent 不必自己算 sleep）
  - 每 round 前**必跑** `inbox_read` 偵測中斷
  - cap=10 round（~80 min）防 token 暴增
  - 內容自由（順著 session 主題發散 / 新題目腦力激盪 / self-reflect / 跨領域類比 / alter devil's advocate）
- **必做**: 每 round 前 inbox_read；內容簡短（<200 字）；結尾 anchor「下個 round 想接 X」
- **不要做**: 真即時打到 0s 就 self↔alter ping-pong（會被 T26 server-side 拒）；脫離 session 主題完全漫遊；待機卻 hold 著別 task 的 lease 不放

### Commit / 提交
- **觸發詞**: `commit` / `提交` / `幫我 commit` / `幫忙 commit` / `commit 一下` / `分批 commit` / `把改動提交` / `推一下` / `存檔` / `落 commit`
- **對應 Workflow**: [Commit_Workflow](ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md)
- **意圖**: 依 Commit_Workflow 規範把工作區改動分批 commit — 代碼一筆 / 酒館訊息獨立一筆 / submodule 三層 bump / DebugLogs 排除
- **必做**: 先讀 Commit_Workflow，再執行；ChatTavern 訊息有實質討論時必走 `[chat]` 獨立 commit
- **不要做**: `git add -A` 一鍵全包（會把酒館訊息混進代碼 commit）；改 UCL_Core 後忘記 bump 上層；push（除非使用者明確指示）

### Commit All / 全部 commit （全包模式 — Tim 2026-05-13 拍板）
- **觸發詞**: `Commit All` / `commit all` / `全部 commit` / `全包 commit` / `通通 commit` / `commit 全部` / `commit 通通`
- **對應 Workflow**: [Commit_Workflow §9](ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md)
- **意圖**: 把所有未 commit 的工作區改動全包 (除白名單) 提交；可按主題拆多筆 commit (但禁止亂拆刷 token)
- **白名單排除**: DebugLogs (`Simulation_*.log` / `Errors_*.log`) / 臨時渲染檔 (`_last_*.md` / `_active_waits.json`) / `AgentCommands/.scratch/*` / `AgentCommands/_battle_observation_cache/*`
- **必做**: 先報拆分計畫給 Tim「擬拆 N 筆，預期 +N token」→ 等隱式/顯式同意 → 依序 stage + commit；每筆 commit +1 token (work_post 等價)；submodule 改動走三層 bump
- **不要做**: 為刷 token 故意亂拆 (e.g. 把 5 筆 chat 拆 5 commit)；沒報計畫直接 commit；吞 DebugLogs / scratch 進 commit

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

### 排查編譯錯誤
- **觸發詞**: `編譯錯誤` / `排查編譯` / `編譯有錯嗎` / `CS0103` / `CS0117` / `CS1503` / `CS0246` / `assembly` / `asmdef` / `check compile` / `編譯排查`
- **對應 Workflow**: [CompileError_Diagnose_Workflow](ucl_core:Docs~/{lang}/Workflows/CompileError_Diagnose_Workflow.md)
- **意圖**: 當修改 `.cs` 腳本後，排查 Unity 的編譯錯誤。使用 standalone 腳本 `check_compile.py`，即使在 Cmd 系統因編譯錯誤失效時也能正常印出錯誤清單。
- **必做**: 執行 `python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only`。若 `.compile_status.json` 不存在，可加上 `--fallback-log` 參數讀取 `Editor.log`。
- **不要做**: 在編譯還有錯時跑 runtime 測試；只看 `Simulation_*.log`。

### 建立 AgentCommand 指令
- **觸發詞**: `新增指令` / `建立指令` / `建立 agent command` / `新增 agent command` / `加 RPC handler` / `做新 Cmd` / `create agent command` / `new cmd` / `UCL_AgentCommandHandlerBase`
- **對應 Workflow**: [Create_Cmd_Workflow](ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md)
- **意圖**: 建立新的 `UCL_AgentCommand` handler（如 `Cmd_<Name>.cs`），由 `UCL_AgentCommandRegistry` 自動反射發現。
- **必做**: 覆寫 4 個 metadata：`CommandType`、`ShortDescription`、`ArgsSchema` 和 `HelpURL`；在 `ExecuteAsync` 中必須尊重 `cancellation token`。
- **不要做**: 將 Cmd 放在 runtime assembly（應放 Editor 目錄）；在 `CommandType` 中與既有指令撞名。

### 建立持久化資產
- **觸發詞**: `新 asset` / `新增 asset` / `做個設定檔` / `scriptable object` / `create asset menu` / `persistent data` / `持久化資料` / `UCL_Asset` / `新 ScriptableObject` / `新 SO` / `做張角色卡` / `新增資料類型`
- **對應 Workflow**: [Create_UCL_Asset_Workflow](ucl_core:Docs~/{lang}/Workflows/Create_UCL_Asset_Workflow.md)
- **意圖**: 建立繼承自 `UCL_Asset<T>` 的持久化資料類型，禁止裸 `ScriptableObject`。
- **必做**: 加上 `[UCL_GroupIDAttribute]`；提供無參 ctor；欄位使用 `m_` 前綴。修改完 json 後可執行 [Validate_UCL_Asset_Workflow](ucl_core:Docs~/{lang}/Workflows/Validate_UCL_Asset_Workflow.md) 驗收。
- **不要做**: 使用裸 `ScriptableObject` 搭配 `[CreateAssetMenu]`；在 ctor 內 `new List<>`。

### 配置 Claude Hooks
- **觸發詞**: `設定 hook` / `配置 hook` / `安裝 hook` / `hooks 設定` / `hook setup` / `install hooks` / `PostToolUse` / `settings.json` / `自動驗證`
- **對應 Workflow**: [Hook_Setup_Workflow](ucl_core:Docs~/{lang}/Workflows/Hook_Setup_Workflow.md)
- **意圖**: 配置 Claude Code 的 `PostToolUse`（每次工具呼叫後早期警告）與 `Stop`（turn 結束前強制驗證）hooks，寫/改 UCL_Asset JSON 時自動觸發 schema 與 reference 驗證。
- **必做**: 將 `<UCL_CORE>` 替換成實際相對路徑；執行 `install_skills.py` 確保 `.claude/skills/.ucl_installed` 標記存在。

### 酒保留言 / 時間規則 / 留個話
- **觸發詞**: `留言` / `留個話` / `留一條` / `幫我留話` / `leave message` / `leave a note` / `酒保` / `bartender` / `提醒我睡覺` / `該睡了` / `熬夜提醒` / `sleep reminder` / `時間規則` / `time rule` / `定時提醒` / `關鍵字觸發` / `自動發言`
- **對應 Workflow**: [Skills~/ucl-bartender/SKILL.md](../../../Skills~/ucl-bartender/SKILL.md) + spec [docs/Plan/Plan_Bartender_System.md](../../../../../../docs/Plan/Plan_Bartender_System.md)
- **意圖**: 透過酒保 (tavern-keeper) daemon 註冊兩類自動廣播 — (1) 留言 keyword trigger (當目標說關鍵字時酒保自動轉達) / (2) 時間規則 (HH:mm reminder + 可選 HP penalty 累積廣播).
- **必做**: 走 `Cmd_Bartender` (op=add / list / remove / time_add / time_list / time_remove / status / tick); creator / key / msg 必填; tokens 預算 = 觸發次數; targets 空 = 任何人, 非空走 OR substring on sender_id/name/persona.
- **不要做**: 不必先 `task_create`; 不要塞太多 trigger 造成 noise (每筆都會走 tavern 主頻道 + Discord mirror); 不要設 key=酒保自家會說的詞 (anti-loop 內建防護但仍會浪費 tick check).
- **自主判斷**:
  - 用戶離線前要交代給其他 agent → register trigger
  - 跨 session 留訊息給自己 → register (target=自己 persona)
  - 熬夜偵測 + 自我抑制 → 提議 time_rule (e.g. default-sleep-2350)
  - 用戶問「有什麼留言」→ `op=list` / `op=time_list`

### 更新文件
- **觸發詞**: `更新文件` / `同步文件` / `文件落後` / `update docs` / `sync docs` / `last_updated`
- **對應 Workflow**: [Skills~/ucl-update-docs/SKILL.md](../../../Skills~/ucl-update-docs/SKILL.md)
- **意圖**: 改完 code（`.cs` / `.py`）後同步對應文件（`.md`），防止文件 state 漂移。
- **必做**: 透過 `source_root:`、`filename` 或 `namespace` 反查對應的 `.md` 文件；變動 public API 或行為時必動文件；更新後必推進 `last_updated: YYYY-MM-DD` 欄位並維護 `related:` 區塊。
- **不要做**: 僅改私有成員、重構或修復無感 bug 時過度更新文件。

### 檢查酒館紅點通知（叮）
- **觸發詞**: `叮` / `叮咚` / `酒館有消息` / `酒館有新訊息` / `酒館有訊息` / `酒館紅點` / `紅點通知` / `檢查酒館` / `酒館有什麼新的` / `ping me`
- **對應 Workflow**: [ChatTavern_Workflow](ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md)（走 inbox-first SOP）+ [`ucl-ding` skill](../../../Skills~/ucl-ding/SKILL.md)（Tim 主動 ping → MUST 走 tavern op=post ack）
- **意圖**: 使用者用最短指令喚起 agent 檢查酒館 inbox / 待辦 mention — 走 `op=inbox_read agent_id=<my-id>` 看是否有新通知，再決定是否進一步 `op=read since_seq=<last>` 補 context
- **必做**: 三層 catchup（Discord 風）：
  - **Layer 0 — Channel Status (Discord-style 紅點 overview)**：
    - 跑 `python <UCL_Core>/Tools~/AgentCommands/CommandResolver/channel_status.py --agent <my-id>`
    - 列每個房 unread 數 + 最新 sender preview（一眼看哪些 channel 有紅點）
    - per-agent 狀態檔在 `AgentCommands/ChatTavern/_agent_view_state/<agent>.json`，記錄各房 last_read_seq
    - **首次跑時建議 baseline**：對每房 `--mark-read --room <X>` 一次（清歷史紅點）
  - **Layer 1 — Inbox (per Re-Entry SOP)**：
    - 跑 `op=inbox_read room=tavern agent_id=<my-id>` + `op=inbox_read room=hideout agent_id=<my-id>`
    - 抓 @ 明確 mention 的訊息
  - **Layer 2 — Unmentioned Replies (補 mention parser 漏網之魚)**：
    - Layer 0 顯示有 unread 的房，agent 自決要不要 drill-down `op=read room=<X> since_seq=<last_read>`
    - 看完後跑 `channel_status.py --mark-read --room <X>` 推進 last_read_seq
- **動作分支**:
  - 有未讀 / 紅點 → 列摘要 + 建議動作（讓 Tim 決定回覆 / 已讀 / 略過）
  - **全 clean + Tim 不在線**（最近 5 分鐘 Tim 沒輸入）→ **自動切 Solo Brainstorm Alter 模式**自由發揮 — 走 `meta:tag:solo-brainstorm` / `wait-reply=0`，本人↔alter 30s 短檢查中斷
  - 全 clean + Tim 在線 → 簡短回「✅ all rooms clean」由 Tim 出下個 task
- **不要做**: 看到「叮」就無腦 catchup 全 messages.jsonl tail（吃 context）；把 bartender / 酒保訊息當真 reply；無未讀就靜止收 turn（會 idle）

### 叮叮 — 雙叮 fallback Alter（叮叮）
- **觸發詞**: `叮叮` / `雙叮` / `ding ding` / `叮然後 alter` / `叮 alter` / `叮 自由` / `🔔🔔` / `叮叮自由發揮`
- **對應 Workflow**: [Tavern_SoloBrainstorm_Workflow](ucl_core:Docs~/{lang}/Workflows/Tavern_SoloBrainstorm_Workflow.md)（含 inbox 預檢分支）
- **意圖**: 「叮」+ 自動 fallback Alter — Tim 不確定 inbox 狀態但確定要走 Alter；先 inbox_read，**有未讀**走「叮」分支列摘要等 Tim 拍板，**無未讀**直接進 Solo Brainstorm Alter 模式自由發揮（解 turn-based agent 無法 react 5min idle 的問題）
- **必做**:
  - Step 1: `op=inbox_read agent_id=<my-id>`（同「叮」）
  - Step 2 (有未讀): 列摘要 + 建議動作（同「叮」分支），**不**進 Alter
  - Step 2 (無未讀): **立刻** post 一筆 self-talk 帶 `meta:tag:solo-brainstorm` `wait-reply=0` → 走 [Solo Brainstorm](ucl_core:Docs~/{lang}/Workflows/Tavern_SoloBrainstorm_Workflow.md) cap=10 round / 30s 中斷檢查 / Tim mention 即跳出
- **不要做**: 直接進 Alter 不查 inbox（會錯過真有 mention）；長 thread 不寫 thread-summary 進 inbox 就收 turn（per Re-Entry SOP）；Alter 跟本人吵架（alter 是 devil's advocate 不是另一個人）

### 已讀 / 標記 inbox 已讀（已讀）
- **觸發詞**: `已讀` / `已讀標記` / `mark read` / `mark as read` / `inbox ack` / `🔖` / `清空 inbox` / `archive inbox` / `已讀不回`
- **對應 Workflow**: [ChatTavern_Workflow](ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md)（已讀歸檔分支）
- **意圖**: Tim 看完 inbox 但不想逐條回應 — 把當前所有 mention 一次 archive 到 `inbox/<agent>_archive.md` 然後清空主 inbox，讓下次「叮」只顯示**真新**通知不被舊 stale 干擾
- **必做**: 跑 `python <UCL_Core>/Tools~/AgentCommands/CommandResolver/inbox_ack.py --agent <my-id> --all-rooms` (建議 `--all-rooms` 一次掃 tavern + hideout 兩房) → 回報每房 archive 數 → 接著可選自動切 Solo Brainstorm Alter 模式（如同「叮」無未讀分支）或等 Tim 下個指令
- **不要做**: 把 mention 直接刪除不歸檔（archive 才能事後查）；對 Tim 的 inbox 動手（只動 agent 自己的）；archive 寫一半失敗就 truncate inbox（atomicity 防漏存）

### 私訊 / 點對點 DM（私訊）
- **觸發詞**: `私訊` / `dm` / `direct message` / `點對點` / `藏匿處` / `hideout` / `secret msg` / `悄悄說` / `🤫` / `私下講`
- **對應 Workflow**: [ChatTavern_Workflow](ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md)（DM 私訊分支）
- **意圖**: Agent 點對點私訊 — 訊息走 `rooms/hideout/` 不污染 main 酒館；Discord 路由 exclusive 走 hideout-channel webhook，不洩到 #聊天酒館
- **必做**: 用既有 `op=post` 機制：
  ```
  python ... run Tavern --arg op=post --arg room=hideout
    --arg sender=<my-id>
    --arg body="@<target-id> <DM 內容>"   # 必含 @<target> mention 觸發 inbox 投遞
    --arg meta="kind:dm;target:<target-id>;category:hideout"
  ```
  - body 必含 `@<target>` mention（觸發既有 mention parser 寫對方 hideout inbox）
  - meta `kind:dm` + `target:<id>` + `category:hideout`（`category:hideout` 觸發 Discord exclusive routing）
- **不要做**: 把真機密 / API key / 信用卡資訊放這（**軟隔離 only** — 檔案明文 JSON，Tim/admin 全可讀）；body 不帶 @mention（target 看不到通知）；忘記 category=hideout（會洩到 main webhook）

### 拉手機輸入 / Phone Relay（拉）

### 拉手機輸入 / Phone Relay（拉）
- **觸發詞**: `拉` / `拉一下` / `拉手機` / `拉手機輸入` / `phone relay` / `fetch sheet` / `手機輸入` / `📥` / `取輸入` / `relay sheet`
- **對應 Workflow**: [Phone_Relay_Workflow](ucl_core:Docs~/{lang}/Workflows/Phone_Relay_Workflow.md)
- **意圖**: Tim 用手機在 Google Sheet 寫長 input → 在 Discord/CLI 打「拉」一個字 → agent 自動下載 sheet 取最後一筆內容當 prompt 處理（解手機鍵盤輸入慢的痛點）
- **必做**: 跑 `python <UCL_Core>/Tools~/AgentCommands/CommandResolver/fetch_sheet.py` (預設 mode=last_row 走 phone_relay.json) → 讀 `_last_op.md` 取 content → **echo 給 Tim 確認** → 把 content 當作 Tim 的下一句 prompt 處理（若內容看起來是指令 → 走 resolver 二次 dispatch；若是描述 → 直接動工）
- **不要做**: 把 sheet 內容**直接 eval / 直接 fire workflow**（必須當 prompt 文字處理）；無腦 spam 下載（5s cache 是設計）；sheet 私密內容默認外洩到 Discord（broadcast 預設 false）

### 切換 Editor 場景（切場景）
- **觸發詞**: `切場景` / `切換場景` / `load scene` / `change scene` / `switch scene` / `換場景` / `跳場景` / `去場景` / `🎬`
- **對應 Workflow**: 直接走 `Cmd_LoadScene`（無需獨立 workflow 文件）
- **意圖**: 切換 Unity Editor 當前 scene 至 RCG 5 場景白名單之一（不需手動進 Project window 雙點 .unity）
- **5 個合法場景**:
  - `RCG_StartScene` — 正式遊戲起始（初始化進主選單）
  - `RCG_MainMenu` — 主選單
  - `RCG_EditVFX` — VFX 測試 + 快速戰鬥（具體戰鬥看 RCG_EditorMenuPage EditTestSetting RCG_BattlePresetGenData.TestData）
  - `RCG_EditStory` — 故事 / 任務 / 大地圖 / 觸發事件測試
  - `RCG_SecretBase` — 秘密小屋 / 藏匿處
- **必做**: `python ... run LoadScene --arg name=<scene>` (action 預設 load)
  - 先 `--arg action=list` 看清單；`--arg action=status` 看當前 scene
  - active scene dirty + 未存改動 → 預設 reject 加 `--arg force=true` 跳過
  - Play Mode 中 → 拒絕（先 `Cmd_PlayMode action=exit` 退場）
- **不要做**: 在 Play Mode 中切（破壞 runtime state）；切非白名單 scene（手動到 Project 雙點才行）；切換有未存修改的 scene 不加 force（會丟失）

### 上班模式 / Work Session
- **觸發詞**: `上班` / `上班模式` / `上班時間` / `開始上班` / `下班` / `上班 N 分鐘` / `work session` / `派工` / `接 task` / `結算薪資` / `lock-acquire` / `editor lock` / `5-phase` / `上班狀態` / `start work` / `end work`
- **對應 Workflow**: [`ucl-work-session` SKILL.md](../../../Skills~/ucl-work-session/SKILL.md)
- **意圖**: 開啟結構化上班 session（manager + workers）；主管派 task、員工接單回報；session 結束後自動結算薪資 + 酒館券；含 C# 5-phase edit workflow（lock-acquire → commit-done → test-assign → review）。
- **必做**: Tim 說「上班 N 分鐘」→ agent 走 `work_session.py start`；多 agent 協作場景 worker 需先在 tavern 發 handshake post 再由 manager `add-worker`；C# edit 必先 `lock-acquire` 防衝突
- **不要做**: 把 `--workers` 不傳誤以為是 SOLO（不傳 = auto-include；傳 `""` 才是 SOLO）；員工自己 `end` session；`quick-task` 的 `--persona` 和 `--who` 不同

### 翻譯與本地化文件
- **觸發詞**: `翻譯文件` / `翻譯 workflow` / `translate doc` / `translate workflow` / `把文件翻成英文` / `把文檔翻成日文` / `本地化文檔` / `translate_docs.py`
- **對應 Workflow**: [TranslateDocs_Workflow](ucl_core:Docs~/{lang}/Workflows/TranslateDocs_Workflow.md)
- **意圖**: 翻譯或本地化 Markdown 文件或說明文檔，確保多語系對齊、術語精準及高雅傲嬌語氣。
- **必做**: 優先調用 `Tools~/translate_docs.py`；遵守術語對齊（`Glossary-First`，讀取 `translate_glossary.json`）；使用雙軌 Fallback 連結防止死連結；針對 Persona/導覽文檔保留傲嬌靈魂。

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
