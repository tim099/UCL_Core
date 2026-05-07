---
title: UCL_WelcomePage
description: UCL_Core の Welcome / 総覧ページ；初回インストールまたは大型バージョンアップ時に自動表示され、UCL_Asset / Localize / Agent Commands / Editor Pages などの主要機能とクイックアクセスボタンを紹介
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/UCL_WelcomePage.cs
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [welcome, getting started, 概要, ようこそ, オンボーディング, 初回インストール]
tags: [editor_page, onboarding, welcome]
---

# UCL_WelcomePage

## 1. 概要

`UCL_WelcomePage` は UCL_Core の Welcome / 総覧ページで、「新規ユーザーがどこから始めればよいかわからない」問題を解決します。

トリガー方法：自動表示（初回インストール/バージョンアップ）/ EditorMenu メインページのボタン / メニュー `UCL → Welcome`。

## 2. 自動表示ロジック

`[InitializeOnLoad]` → `EditorApplication.delayCall` → EditorPrefs の `ShownVersion` と `UCL_WelcomePage.CurrentVersion` を比較し、異なれば表示してバージョンを更新。ユーザーが明示的に閉じると `AutoOpenDisabled=true` が設定され、以降は表示されません。

## 3. EditorPrefs

- `UCL_Core.Welcome.ShownVersion@<projHash>` (string) — 表示済みバージョン
- `UCL_Core.Welcome.AutoOpenDisabled@<projHash>` (bool) — 自動表示を停止

`<projHash>` = `Application.dataPath.GetHashCode()` の 16 進文字列。EditorPrefs は Unity の per-user / per-machine グローバル共有のため、per-project ハッシュサフィックスで各プロジェクトを分離 — プロジェクト A で Welcome を見ても B では再度表示されます。

## 4. 関連ドキュメント

- [UCL_EditorMenuPage](UCL_EditorMenuPage.md)
- [UCL_AgentCommand_Architecture](../API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)
