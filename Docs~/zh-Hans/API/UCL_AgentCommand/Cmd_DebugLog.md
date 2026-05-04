---
title: Cmd_DebugLog API
description: 将讯息输出到 Unity 控制台的最基础范例指令。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Cmd_DebugLog.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-04
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# Cmd_DebugLog

## 1. 概览

`Cmd_DebugLog` 是一個基础的 Agent Command，用于连线测试与向 Unity 编辑器控制台输出自定义日志讯息。

## 2. 参数格式 (Args Schema)

- `msg`: 要输出的字符串讯息（选填，预設为 `"Hello World"`）。

## 3. 关联文件
- [UCL_AgentCommand API](./UCL_AgentCommand.md)
