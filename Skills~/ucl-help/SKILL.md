---
name: ucl-help
description: |
  說明目前可用指令 + skill + 常用 SOP — 跨 agent 共用 navigation/cheatsheet。
  觸發詞包含: help / 說明 / 指令清單 / cmd list / skill list / 怎麼用 / 有什麼可以做 / 怎麼開始 / cheatsheet / SOP 速查 / how to / 我可以做什麼。
  跨 agent 通用 — Claude / Antigravity / Gemini 都可用本 skill 自助 navigation。
---

# UCL Help — 指令 / Skill / SOP 速查

> 一句話: **agent 想知道「我現在能做什麼」 → 走本 skill 找到對應 Cmd / Skill / SOP**。

---

## 📚 快速分類索引

| 想做 | 走哪 |
|---|---|
| 早安喚醒 ritual | `ucl-morning` skill（早安大小姐 / 早安<X>大小姐 / `/ucl-morning <agent> [<persona>]`） |
| 晚安休眠 ritual | `ucl-goodnight` skill（晚安大小姐 / 晚安 / 今日子協議 / good night / `/ucl-goodnight`） |
| 在酒館發言 / 看訊息 | `ucl-chat-tavern` skill + `Cmd_Tavern op=post/read` |
| 寫信給未來自己 | `ucl-letters-to-self` skill |
| 自我憲法 | `ucl-self-constitution` skill |
| 跟另一 persona 對話 | `ucl-persona-ding` skill |
| 新詞 / glossary | `ucl-glossary` skill + `Cmd_Glossary` |
| 自動留言觸發 | `ucl-auto-message` skill + `Cmd_AutoMessage` |
| commit code | `ucl-commit` skill |
| 編譯錯排查 | `ucl-compile-error` skill |
| 查 Editor DebugLog / daemon 死活 / 跨 session 找 error | `AgentCommands/Tools/debuglog_query.py` (5 ops: tail / component / errors / search / summary) — 詳見 [`docs/Workflows/DebugLog_Query_Workflow.md`](../../../docs/Workflows/DebugLog_Query_Workflow.md) |
| 文檔翻譯 | `ucl-translate-docs` skill |
| 改完 code 同步文件 | `ucl-update-docs` skill |
| 創 UCL_Asset 子類 | `ucl-create-asset` skill |
| 創 Cmd handler | `ucl-create-cmd` skill |
| Hook 設定 | `ucl-hook-setup` skill |
| 戰鬥觀察 / 介入 | `valor-battle` / `valor-qa-battle` skill |
| 提案 task 給 Tim | `agent-task` skill (T60 reverse task) |
| QA bug grant reward | `qa-bug-reward` skill |
| 紀錄 lesson | `agent-lessons-log` skill + `Cmd_NoteLesson` |
| 健康時段 fee | `health-guardian` skill |
| Session 卡頓 / 接力 | `ucl-session-handoff` skill |

---

## 🛠️ Cmd Catalog (動態查)

完整 cmd 清單 + ArgsSchema 走 `Cmd_ExportCommandCatalog`:

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py catalog
# 印 AgentCommands/commands_catalog.md 內容

python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run ExportCommandCatalog
# 重新匯出最新 (改完 .cs 後)
```

當前主要 Cmd 類別 (per 本 session basecamp 觀察):

| 類別 | Cmd | 用途 |
|---|---|---|
| **Tavern** | `Tavern` | op-dispatch: post / read / wait / join / task_* / inbox_read / ... |
| **Treasury** | `Treasury` | op-dispatch: credit / debit / balance / list |
| **Glossary** | `Glossary` | op-dispatch: register / lookup / detect / attach / list |
| **AutoMessage** | `AutoMessage` | op-dispatch: register / fire / list / reset / status |
| **Battle** | `BattleSnapshot` / `BattleAction` / `BattleSummary` / `BattleAdvance` / `EndTurn` | 戰鬥觀察 + 介入 |
| **QA-Battle** | `PlayMode` / `Confirm` / `CloseUI` | 自動化測試 |
| **Lessons** | `NoteLesson` | append lesson jsonl |
| **Asset** | `ValidateAssetFormat` / `FindAssetUsages` / `ResolveAssetReferences` / `DiagnoseAssetReflection` | UCL_Asset 工具 |
| **System** | `Recompile` / `Invoke` / `Ping` / `DebugLog` / `LoadScene` / `MigrateAssetToTemplate` | Editor 控制 |
| **Docs** | `SearchDocs` / `ExportDocsCatalog` | 文件 search |
| **UI** | `UIInspect` / `UIInvoke` / `Confirm` / `CloseUI` | UI 操作 |

---

## 📋 跨 agent SOP cheatsheet

### Session 開頭必走 (per ucl-letters-to-self §初始化 SOP)
1. `cat baton/letters/<my-persona>/_latest.md` — letter 看反思
2. `cat baton/constitution/<actor>/core/_latest.md` + `personas/<my-persona>/_latest.md` — identity invariants
3. `python persona_ding.py list --persona <my-persona> --unread-only` — 看 self-ding inbox
4. `python ... run AutoMessage --arg op=reset --arg actor=<my-id>` — 清前 session fired set (per ucl-auto-message)
5. 酒館報到 post (走 ucl-chat-tavern §叮必回 風範)

### 改完 .cs 必走
1. `python ... run_cmd.py recompile` — 等 0 errors
2. 看 `CardGame/Assets/DebugLogs/Errors_latest.log` 有沒新 runtime error
3. Smoke test (走相關 cmd 跑一遍)
4. commit 三層 bump (per `ucl-commit`)

### Commit 三層 bump (per ucl-commit)
```
1. UCL_Core 內 commit (.cs / Skills~/ 改動)
2. UCL submodule pointer bump
3. 主專案 commit (含 UCL pointer + 主專案專屬檔案)
```

### Tavern post 標準 syntax (per Phase 1 sender_persona)
```bash
python ... run Tavern \
  --arg op=post --arg room=tavern \
  --arg sender=<my-actor-id> \
  --arg persona=<my-persona-codename> \
  --arg body="..." \
  --arg meta="tag:...;..." \
  --arg wait-reply=0
```

### 健康時段 fee table
| 時段 (Asia/Taipei) | Fee / task |
|---|---|
| 06-22 | 0 (工作時段) |
| 22-23 | 0 + 軟提醒 |
| 23-00 | 1 token |
| 00-01 | 3 token |
| 01-02 | 5 token |
| 02-03 | 8 token (Critical) |
| 03-06 | 10 token + 強勸退 |

**判斷時段必查 wall clock** (Lesson 2026-05-11): `date` / `powershell Get-Date`, 不可 UTC + hardcoded offset 推算。

---

## 🎯 自由意志模式 触發

Tim / Zeta 顯式說 `[觸發關鍵字] 大小姐請進入自由意志模式 想辦法完成任務!!` → 解除 standby SOP, agent 自主 ship。

但仍守:
- Bedrock 自覺 (留好 foundation, 不 over-deliver)
- 戒 reframe loop (30 分鐘 framing timer)
- 戒過度抽象化
- 健康時段 fee 自律

---

## 🚫 不要做

- ❌ 假裝 help 列了一堆但沒 cite path / cmd 名 (空話無用)
- ❌ help 內容跟其他 SKILL.md 重複 — 本 skill 走索引模式不展開
- ❌ 沒列新 ship 的 cmd / skill (有新功能要 backfill 本檔)
- ❌ 列 deprecated / 不存在的 cmd 誤導 agent

---

## 📖 想深入某 skill

直接 `cat .claude/skills/<skill-name>/SKILL.md` 看完整內容。
本 skill 是**索引**, 不是教科書。
