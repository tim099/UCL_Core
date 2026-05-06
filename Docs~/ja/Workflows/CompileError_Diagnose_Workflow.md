---
title: Unity コンパイルエラー診断ワークフロー
description: UCL_CompileErrorTracker が書く .compile_status.json + check_compile.py ツールで、Cmd システム自体が compile error で読み込めない卵が先か鶏が先か状況でも完全なエラー一覧を取得できる；dedupe / log fallback / session 境界検出 / 4 ステップ SOP / 8 大エラータイプ対照 / 実戦ケーススタディ含む
last_updated: 2026-05-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [コンパイルエラー, compile error, CS0103, CS1503, asmdef, debug, トラブルシューティング]
tags: [compile, debug, agent_commands, workflow]
---

# 🔧 Unity コンパイルエラー診断ワークフロー

> [!IMPORTANT]
> **問題**：コンパイル失敗時、Cmd システムも一緒に動作不能になる → 最も必要な時に使えない。**コアツールは standalone Python スクリプト** [`check_compile.py`](../../../Tools~/AgentCommands/check_compile.py)、**Cmd システムに依存しない**。

## 0. TL;DR

```bash
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only --fallback-log
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --watch --watch-timeout 60
```

終了コード：`0` clean / `2` エラーあり / `3` status ファイルなし。

## 1. 2 つのデータソース

- ⭐ `.compile_status.json` — Tracker が書く、**最新** の単一コンパイル結果
- Editor.log fallback — 複数のコンパイル試行を蓄積、dedupe + session 境界検出が必要

## 2. 4 ステップ SOP

1. check_compile.py 実行で dedupe 後のエラー数を確認
2. **Stale vs Fresh** クロス検証 — ファイルの対応行を開いてエラー記述が依然有効か確認
3. ルート原因を見つける（cascade エラーを 1 つずつ修正しない）
4. 修正後 Unity フォーカスで再コンパイル → ループ

## 3. よくあるエラータイプ

| Code | 典型的な修正 |
|---|---|
| CS0103 | using 追加 / 完全修飾 / 該当 type 自身のエラーを先に修正 |
| CS1503 (tuple lambda) | tuple を分解して個別パラメータに |
| CS0246 / CS0234 | asmdef references 不足 / namespace 誤り |

## 4. asmdef 越境

UCL_Core 単方向依存：`UCL_CoreEditor → UCL_Core`。`UCL_Core` は `UCL_CoreEditor` の type を見られない → type を UCL_Core asm に移動、または `EditorApplication.ExecuteMenuItem` 使用。

## 5. Tracker 自身の卵が先か鶏が先か

domain reload (前回コンパイル成功) 時のみ ctor 実行。初回起動でエラーがある場合 `.compile_status.json` は現れない → `--fallback-log` で Editor.log を解析。

## 6. 関連ドキュメント

- [Cmd_GetCompileErrors](../API/UCL_AgentCommand/Cmd_GetCompileErrors.md)（追加予定）
- [Create_Cmd_Workflow](Create_Cmd_Workflow.md)
- [UCL_AgentCommand_Architecture](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)

---

## 他言語

- 🇬🇧 [English](../../en/Workflows/CompileError_Diagnose_Workflow.md)
- 🇯🇵 日本語（本ファイル）
- 🇨🇳 [简体中文](../../zh-Hans/Workflows/CompileError_Diagnose_Workflow.md)
- 🇹🇼 [繁體中文](../../zh-Hant/Workflows/CompileError_Diagnose_Workflow.md)
