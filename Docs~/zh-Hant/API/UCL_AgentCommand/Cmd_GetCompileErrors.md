---
title: Cmd_GetCompileErrors API
description: 讀 `UCL_CompileErrorTracker` 的 JSON，回報 Unity 編譯狀態。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_GetCompileErrors.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-08-17
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_GetCompileErrors

> 讀 `UCL_CompileErrorTracker` 的 JSON，回報 Unity 編譯狀態。

## 1. 概覽

- **CommandType**：`GetCompileErrors`
- **原始碼 ShortDescription**：Read UCL_CompileErrorTracker JSON and report Unity compile status. For broken-assembly cases, use Tools~/check_compile.py instead (standalone Python).

**什麼時候用**：想在 Editor 內拿編譯錯誤清單時。

## 2. 參數 (ArgsSchema)

- `errorsOnly=true|false (default false) — only list Error messages`
- `format=md|json (default md)`

```bash
senate ucmd run GetCompileErrors --arg <k>=<v>
```

## 3. 注意

- ⚠ **assembly 整個編不起來時這支也會失效**（它自己也在那個 assembly 裡）——那種情況要走 `Tools~/AgentCommands/check_compile.py`，那支是 standalone Python、不依賴 Cmd 系統。
- 改完 .cs 的完整手勢是 `senate ucmd run Recompile`（送編譯並等它跑完）；**Cmd 回 Success 只代表請求被收下，不代表編譯發生過。**

## 4. 關聯

- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [架構](./UCL_AgentCommand_Architecture.md)
