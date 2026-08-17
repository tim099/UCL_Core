---
title: Cmd_Recompile API
description: 觸發 Unity 重新編譯腳本。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_Recompile.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-08-17
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_Recompile

> 觸發 Unity 重新編譯腳本。

## 1. 概覽

- **CommandType**：`Recompile`
- **原始碼 ShortDescription**：Trigger Unity script recompile (use Python `recompile` subcommand to wait until compile finishes).

**什麼時候用**：**改完任何 .cs 之後都要跑** —— Unity 失焦時不會自動重編，而 agent 寫檔幾乎都在失焦下發生。

## 2. 參數 (ArgsSchema)

- `refresh=Whether to also call AssetDatabase.Refresh() before requesting recompile (true|false, default true)`

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Recompile --arg refresh=true
```

## 3. 注意

- ⚠ `run_cmd.py run Recompile` 只**送出請求就返回**，不等編譯完成（刻意的：domain reload 會殺掉 in-flight 的 async Cmd）。
- 要等到編完並拿到錯誤清單，用 python 子命令：`python run_cmd.py recompile`。
- **編譯真的發生過的唯一憑據是 `check_compile.py` 沒標 STALE**（時間戳晚於你最後一次存檔）。

## 4. 關聯

- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [架構](./UCL_AgentCommand_Architecture.md)
