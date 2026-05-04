---
title: UCL_AgentCommand API
description: Data model and framework layer for editor-side Agent Command execution.
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/UCL_AgentCommand.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-04
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# UCL_AgentCommand

## 1. Overview

`UCL_AgentCommand` is the primary data model for instructions queued into `AgentCommands/queue.json` by an AI agent. Each entry maps directly to a specific command type that can be dispatched by the in-editor runner via `UCL_AgentCommandRegistry`.

## 2. Properties

- `Id`: A unique string identifier. Conventionally: `yyyyMMdd-HHmmss-<typelower>`.
- `Type`: The case-insensitive command type key matching a registered handler.
- `Mode`: Either `"OneShot"` or `"Repeatable"`.
- `RunCount`: Integer representing the successful executions count.
- `Args`: Dictionary mapping string keys to string values.
- `Description`: Human-readable note from the agent for context.

## 3. Related Workflows
- Refer to [HelpURL_Workflow](file:///d:/Unity/EmblemOfValor/docs/Workflows/HelpURL_Workflow.md) for how HelpURL prefixing works across the project.
