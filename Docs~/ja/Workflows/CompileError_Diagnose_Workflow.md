---
title: Unity コンパイルエラー診断ワークフロー
description: UCL_CompileErrorTracker が書く .compile_status.json ＋ Senate CLI（unity-recompile / unity-compile-status。python check_compile.py は未退場）で、Cmd システム自体が compile error で読み込めない卵が先か鶏が先か状況でも完全なエラー一覧を取得できる；dedupe / log fallback / session 境界検出 / 4 ステップ SOP / 8 大エラータイプ対照 / 実戦ケーススタディ含む
last_updated: 2026-09-07
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [コンパイルエラー, compile error, CS0103, CS1503, asmdef, debug, トラブルシューティング]
tags: [compile, debug, agent_commands, workflow]
---

# 🔧 Unity コンパイルエラー診断ワークフロー

> [!IMPORTANT]
> **問題**：コンパイル失敗時、Cmd システムも一緒に動作不能になる → 最も必要な時に使えない。
>
> **主入口は Senate CLI**（2026-09-07 より）：`senate cmd unity-recompile`（起動＋**その回**が終わるまで待つ）／
> `senate cmd unity-compile-status`（現状を読むだけ、**Editor 不要**）。どちらも読むのは `.compile_status.json`
> だけで、**Cmd システムに依存しない**（コンパイルが壊れると Cmd も読み込めない、というのが本ワークフローの前提）。
>
> python [`check_compile.py`](../../../Tools~/AgentCommands/check_compile.py) は **未退場**。
> `--fallback-log`（Editor.log 解析）と `--editor-alive`（ハートビート）の 2 つは CLI 未移植で今も python だけが持つ。

## 0. TL;DR

```bash
# ⭐ .cs を直した後の既定ルート：再コンパイル起動＋**その回**が終わるまで待って出力
senate cmd unity-recompile --arg persona=<me>

# 現状を読むだけ（起動しない・Editor 不要）
senate cmd unity-compile-status

# ステータスファイルが無い → Editor.log フォールバック（**CLI 未移植、python のまま**）
python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only --fallback-log
```

> [!WARNING]
> ⛔ **`check_compile.py --watch` は偽のグリーンを返す（TASK-0154）—— `unity-recompile` に切り替えること。**
> 終了条件が `in_progress=false` だけで、起動前の時点で既に false ⇒ 前回のスナップショットを返す。
> 🩸 2026-09-07 実測：recompile 送出直後に `--watch` した結果、出たのは**3 日前**（`2026-09-04T17:14`）の
> `Errors: 0`、**しかも STALE バナー無し** —— `--watch` 無しなら同じツールが出す。
> `unity-recompile` の基準は**送出したその瞬間**（Submit の前に取得）なので、新構造にその穴は無い。

> [!CAUTION]
> ⛔ **どちらも計測対象は Unity assemblies のみで、`senate.exe` は含まない**（そちらは `dotnet build` ／ `build.sh` の出荷検収）。

終了コード（python 版）：`0` clean / `2` エラーあり / `3` status ファイルなし。

## 1. 2 つのデータソース

- ⭐ `.compile_status.json` — Tracker が書く、**最新** の単一コンパイル結果
- Editor.log fallback — 複数のコンパイル試行を蓄積、dedupe + session 境界検出が必要

## 2. 4 ステップ SOP

1. `senate cmd unity-recompile` を実行し dedupe 後のエラー数を確認
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
