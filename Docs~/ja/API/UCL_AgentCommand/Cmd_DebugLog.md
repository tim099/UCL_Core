---
title: Cmd_DebugLog API
description: Unity コンソールにメッセージを出力する基本的なコマンドです。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Cmd_DebugLog.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-04
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# Cmd_DebugLog

## 1. 概要

`Cmd_DebugLog` は、接続テストや Unity エディターのコンソールにカスタムログメッセージを出力するための基本的な Agent Command です。

## 2. 引数スキーマ (Args Schema)

- `msg`: 出力する文字列メッセージ（任意、デフォルトは `"Hello World"`）。

## 3. 関連ドキュメント
- [UCL_AgentCommand API](./UCL_AgentCommand.md)
