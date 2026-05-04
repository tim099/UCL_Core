---
title: UCL_AgentCommand API
description: 編輯器端 Agent Command 執行的資料模型與框架層文件。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/UCL_AgentCommand.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-04
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# UCL_AgentCommand

## 1. 概覽

`UCL_AgentCommand` 是 AI 代理排隊存入 `AgentCommands/queue.json` 中的指令的主要資料模型。每個項目直接對應到一個具體的指令類型，可由編輯器內的運行器經由 `UCL_AgentCommandRegistry` 來分發執行。

## 2. 屬性

- `Id`: 唯一的字串識別碼。慣例為：`yyyyMMdd-HHmmss-<typelower>`。
- `Type`: 不區分大小寫的指令類型標記，與已註冊的 handler 匹配。
- `Mode`: `"OneShot"` 或 `"Repeatable"`。
- `RunCount`: 表示成功執行次數的整數。
- `Args`: 將字串鍵對應到字串值的 Dictionary。
- `Description`: 代理提供的易讀註解，用於記錄背景脈絡。

## 3. 關聯工作流程
- 關於專案中 HelpURL prefix 的運作方式，請參考 [HelpURL_Workflow](file:///d:/Unity/EmblemOfValor/docs/Workflows/HelpURL_Workflow.md)。
