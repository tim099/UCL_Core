---
title: Cmd_LoginStatus API
description: 唯讀查詢 persona pool 與目前的 active lock（鏡像 `UCL_LoginStatusPage` 的資料，給 agent 走 RPC 用）。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/AwakenInit/Cmd_LoginStatus.cs
namespace: UCL.Core.EditorLib.AgentCommands.AwakenInit
last_updated: 2026-08-17
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_LoginStatus

> 唯讀查詢 persona pool 與目前的 active lock（鏡像 `UCL_LoginStatusPage` 的資料，給 agent 走 RPC 用）。

## 1. 概覽

- **CommandType**：`LoginStatus`
- **原始碼 ShortDescription**：Read-only persona pool + active lock 查詢 (鏡像 UCL_LoginStatusPage 資料, 給 agent RPC 用).

**什麼時候用**：要知道現在誰在線、誰的 lock 何時鎖的、某 persona 屬於哪個 agent 時。

## 2. 參數 (ArgsSchema)

- `filter_status=online|offline|all (default: all)`
- `filter_agent=<agent name> 篩 persona/lock 的 agent 欄 (default: '' 不篩)`
- `format=md|json|both (default: both) — md 走 _login_status.md, json 走 _login_status_latest.json`

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run LoginStatus --arg <k>=<v>
```

## 3. 注意

- 在線判準是 **lock 檔存在且未過期**，不是 persona registry 的 `status` 欄 ——登出流程沒走完時 `status` 會停在 online，拿它當來源會 @ 到不在的人。
- `format` 預設 `both`：md 落 `_login_status.md`、json 落 `_login_status_latest.json`。
- ⚠ **查不到 lock ≠ 沒人在線**，只代表讀不到在線紀錄。空清單被讀成「今天沒人」比讀成「查不到」危險。

## 4. 關聯

- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [架構](./UCL_AgentCommand_Architecture.md)
