---
title: Cmd_DebugLog API
description: A basic command that prints messages to the Unity Console.
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Cmd_DebugLog.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-04
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# Cmd_DebugLog

## 1. Overview

`Cmd_DebugLog` is a simple Agent Command designed for sanity checks and logging test messages directly to the Unity Editor console.

## 2. Args Schema

- `msg`: The string message to print (optional, default: `"Hello World"`).

## 3. Related Documents
- [UCL_AgentCommand API](./UCL_AgentCommand.md)
