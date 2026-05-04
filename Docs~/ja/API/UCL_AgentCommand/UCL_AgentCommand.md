---
title: UCL_AgentCommand API
description: エディター側で Agent Command を実行するためのデータモデルおよびフレームワークレイヤーのドキュメントです。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/UCL_AgentCommand.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-04
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# UCL_AgentCommand

## 1. 概要

`UCL_AgentCommand` は、AI エージェントが `AgentCommands/queue.json` にキューとして格納する、コマンドの主要なデータモデルです。各項目は、登録されたハンドラーと一致する特定のコマンドタイプと直接マッピングされ、エディター側のランナーによって `UCL_AgentCommandRegistry` を通じて実行されます。

## 2. プロパティ

- `Id`: 一意の文字列識別子。慣例：`yyyyMMdd-HHmmss-<typelower>`。
- `Type`: 大文字と小文字を区別しないコマンドタイプキー（登録済みハンドラーと一致）。
- `Mode`: `"OneShot"` または `"Repeatable"`。
- `RunCount`: 正常に実行された回数。
- `Args`: 文字列のキーと値をマッピングする Dictionary。
- `Description`: 背景情報を記録するためにエージェントが提供する読みやすい注釈。

## 3. 関連ワークフロー
- プロジェクト内での HelpURL プレフィックスの動作については、[HelpURL_Workflow](file:///d:/Unity/EmblemOfValor/docs/Workflows/HelpURL_Workflow.md) を参照してください。
