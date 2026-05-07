---
title: UCL_AgentSkillManagerPage — Agent Skill インストール管理画面
description: UCL_Core/Skills~/ 内のワークフロースキルをワンクリックで各 AI エージェントにインストールするための IMGUI ビジュアルフロントエンド。初回起動時に UCL_WelcomePage の最前面に自動ポップアップし、オンボーディングの露出を強制します。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_EditorMenuPages/
namespace: UCL.Core.EditorLib.Page
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_User, Gameplay_Programmer]
related:
  - ucl_core:Skills~/README.md | Skills~ ソースディレクトリ | source-of-truth + manifest 仕様
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_WelcomePage.md | UCL_WelcomePage | 初回起動時にこのオンボーディング画面を自動でプッシュするメイン画面
  - ucl_core:Docs~/{lang}/Workflows/Hook_Setup_Workflow.md | Hook Setup Workflow | 新規プロジェクト向けオンボーディングパッケージ（フックとスキルを同時に設定）
---

# 🛠 UCL_AgentSkillManagerPage

> 一言で言えば：**IMGUI インターフェースで `Tools~/install_skills.py` を実行する画面**です。CLI の操作に不慣れな開発者の障壁となっていた「AI 向けスキルのインストール」を、ワンクリックのビジュアル画面に落とし込みました。

---

## 1. なぜ独立したページにするのか

従来、`DrawSkillsCard` は `UCL_WelcomePage` の中段にカードとして埋め込まれており、ユーザーが見落としがちでした。**この管理機能を独立したページとして分離し、Welcome 画面の初回起動時に最前面に自動プッシュすることで、以下のメリットが生まれます**：

- **露出の強制**：ユーザーが内容を確認・ボタンをクリックするか、「確認済み」にチェックを入れない限り、「戻る」ボタンで Welcome 画面に戻ることはできません。
- **十分な表示スペース**：Per-Agent × Per-Skill マトリックス（TODO、現在はプレースホルダー）を余裕を持って配置できます。
- **簡単な再表示**：Welcome 画面の「🛠 スキル管理画面を開く」ボタンや、メニューパス `UCL → Agent Skill Manager` からいつでも再表示できます。

---

## 2. 3つの表示方法

| 入り口 | トリガータイミング | コードエントリーポイント |
|---|---|---|
| 自動ポップアップ | Welcome 画面の初回起動時、または `AcknowledgedVersion` が現在のバージョンと一致しない場合 | `MaybeAutoPopupOnWelcome(controller)` が `UCL_WelcomePage.ContentOnGUI` の最初のフレームから呼び出されます |
| Welcome カード | ユーザーによる能動的なクリック | `UCL_AgentSkillManagerPage.Create()` |
| メニュー項目 | `UCL → Agent Skill Manager` | `OpenFromMenu()` ([MenuItem]) |

---

## 3. EditorPrefs

- **キー**: `UCL_Core.AgentSkill.AcknowledgedVersion@<ProjectFingerprint>`
- **値**: 現在のコンテンツバージョン（現在は `"1"`）
- **プロジェクトごとのネームスペース**: `Application.dataPath.GetHashCode()` をサフィックスとして追加することで、プロジェクト A での確認済み設定がプロジェクト B のポップアップを阻害するのを防ぎます。
- **コンテンツバージョンの更新**: バージョンが更新されると、EditorPrefs 内の古い値との不一致により自動ポップアップが再度トリガーされ、ユーザーに新機能の存在を確実に知らせます。

---

## 4. インストール状態の判定

`<host-project-root>/.claude/skills/.ucl_installed` または `.agents/rules/.ucl_installed` 内の `ucl_core_commit` フィールドを読み取ることで判定します：

| 状態 | 条件 | UI の色 |
|---|---|---|
| `NotInstalled` | グローバルマーカーファイルが存在しない | 黄 |
| `Synced` | ハッシュ値が UCL_Core の HEAD コミットと一致 | 緑 |
| `Stale` | ハッシュ値が UCL_Core の HEAD コミットと不一致 | 橙 |
| `UnknownHead` | git の HEAD コミットを取得できない | 青 |
| `NoProjectRoot` | .claude/ または .git/ ディレクトリが見つからない | 灰（無効化） |
| `NoUCLCore` | `UCL_EditorPath.CorePath` が空 | 灰（無効化） |

---

## 5. Per-Agent × Per-Skill マトリックス（TODO）

現在、`DrawAgentMatrixPlaceholder` は `Skills~/` 内のスキル名のみをリストアップしています（無効化されたトグル）。今後の対応項目は以下の通りです：

- **列**: 対象エージェント（`claude` / `cursor` / `antigravity` / `gemini`）
- **行**: スキル名
- **チェックボックス**: `install_skills.py --target X --include skill1,skill2` の直接制御
- **インストールマーカー**: エージェントごとのグローバルマーカーファイルの書き出し（`.ucl_installed.claude`、`.ucl_installed.antigravity` など）

*進捗ブロック解決済み：Antigravity のディレクトリ仕様が `.agents/rules/` に確定し、動的トリガー仕組みが構築されました。*

---

## 6. 複数プロジェクト間での利用

本画面は `UCL_Core/` 内に存在し、`Skills~/` と同じソースを共有しています。UCL_Core が他のプロジェクトに移行されると、この画面も自動的に追従します。プロジェクトごとの EditorPrefs により、複数プロジェクト間で設定が混同されることはありません。

唯一の前提条件：`<root>/.claude/skills/` または `<root>/.agents/rules/` がインストール対象ディレクトリとなることです。その他のエージェント用のターゲットパスは、`install_skills.py --target` によって動的に処理されます。
