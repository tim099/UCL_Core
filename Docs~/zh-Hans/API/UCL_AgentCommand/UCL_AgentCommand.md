---
title: UCL_AgentCommand API
description: 编辑器端 Agent Command 执行的数据模型与框架层文件。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/UCL_AgentCommand.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-04
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# UCL_AgentCommand

## 1. 概览

`UCL_AgentCommand` 是 AI 代理排队存入 `AgentCommands/queue.json` 中的指令的主要数据模型。每个项目直接对应到一个具体的指令类型，可由编辑器內的运行器经由 `UCL_AgentCommandRegistry` 来分發执行。

## 2. 属性

- `Id`: 唯一的字符串识别码。惯例为：`yyyyMMdd-HHmmss-<typelower>`。
- `Type`: 不区分大小写的指令类型标记，与已注册的 handler 匹配。
- `Mode`: `"OneShot"` 或 `"Repeatable"`。
- `RunCount`: 表示成功执行次数的整数。
- `Args`: 将字符串键对应到字符串值的 Dictionary。
- `Description`: 代理提供的易读注解，用于记录背景脉络。

## 3. 关联工作流程
- 关于项目中 HelpURL prefix 的运作方式，请参考 [HelpURL_Workflow](file:///d:/Unity/EmblemOfValor/docs/Workflows/HelpURL_Workflow.md)。
