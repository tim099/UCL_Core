---
title: Cmd_ReadHierarchy API
description: 讀當前 Unity 場景的 Hierarchy，dump 成 markdown。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_ReadHierarchy.cs
namespace: UCL.Core.EditorLib.AgentCommands.ReadHierarchy
last_updated: 2026-08-17
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_ReadHierarchy

> 讀當前 Unity 場景的 Hierarchy，dump 成 markdown。

## 1. 概覽

- **CommandType**：`ReadHierarchy`
- **原始碼 ShortDescription**：Read the current Unity scene Hierarchy and dump it to _last_op.md as markdown.

**什麼時候用**：agent 要知道場景裡有什麼物件、掛了哪些 component 時。

## 2. 參數 (ArgsSchema)

（本 Cmd 未宣告 ArgsSchema —— 參數以原始碼為準。）

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run ReadHierarchy --arg includeComponents=true
```

## 3. 注意

- `includeComponents=true` 才會列 component；預設只有物件樹。
- 輸出落在 `_last_op.md` —— 那是 **ephemeral 檔，不進版控**。

## 4. 關聯

- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [架構](./UCL_AgentCommand_Architecture.md)
