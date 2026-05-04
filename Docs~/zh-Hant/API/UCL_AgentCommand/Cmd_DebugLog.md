---
title: Cmd_DebugLog API
description: 將訊息輸出到 Unity 控制台的最基礎範例指令。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Cmd_DebugLog.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-04
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# Cmd_DebugLog

## 1. 概覽

`Cmd_DebugLog` 是一個基礎的 Agent Command，用於連線測試與向 Unity 編輯器控制台輸出自訂日誌訊息。

## 2. 參數格式 (Args Schema)

- `msg`: 要輸出的字串訊息（選填，預設為 `"Hello World"`）。

## 3. 關聯文件
- [UCL_AgentCommand API](./UCL_AgentCommand.md)
