---
title: UCL_Core ドキュメントインデックス
description: UCL_Core フレームワークの多言語ドキュメントエントリー — Agent Command システム、UCL_Asset アセットシステム、エディターページ、モジュールサービス。
last_updated: 2026-05-05
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# 📚 UCL_Core ドキュメントインデックス

> **UCL_Core** は、UCL フレームワークのコアモジュールです（エディターアセットシステム + モジュールサービス + Agent Command システム + エディター UI）。このファイルは日本語版ドキュメントのエントリーです。他の言語については、`Docs~/{en,ja,zh-Hans,zh-Hant}/index.md` を参照してください。

---

## ⭐ 主な機能：Agent Command システム

> **AI エージェントから Unity エディターへのクロスプロセス型コマンドシステム** — エージェントが `queue.json` にコマンドを書き込み、エディターがそれを実行して結果を書き戻します。このフレームワークにおいて**最も重要な AI 協働ツール**です。

### 必読
| ドキュメント | 説明 |
|---|---|
| 🤖 **[UCL_AgentCommand_Architecture](API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md)** ⭐⭐ | **全体アーキテクチャ** — コンポーネント図 / ライフサイクル / 自動検出 / トリガー方法 / queue.json スキーマ / 拡張性 |
| [UCL_AgentCommand](API/UCL_AgentCommand/UCL_AgentCommand.md) | 単一コマンドのデータモデル |
| [UCL_AgentCommandsPage](UCL_EditorPage/UCL_AgentCommandsPage.md) | エディター IMGUI ページ（人間向け UI） |

### 内蔵 Cmd API ドキュメント
| Cmd Type | API ドキュメント | 用途 |
|---|---|---|
| `DebugLog` | [Cmd_DebugLog](API/UCL_AgentCommand/Cmd_DebugLog.md) | 疎通確認 / 最もシンプルな例 |
| **`ResolveAssetReferences`** ⭐ | [Cmd_ResolveAssetReferences](API/UCL_AgentCommand/Cmd_ResolveAssetReferences.md) | **UCL_Asset チェーンの一括解決** — BFS + リフレクション + maxDepth + 重複排除。AI エージェント用に (AssetType, ID, JSON パス) のリストを出力します。 |
| **`ExportCommandCatalog`** ⭐ | [Cmd_ExportCommandCatalog](API/UCL_AgentCommand/Cmd_ExportCommandCatalog.md) | **現在登録されている全ハンドラーを Markdown カタログとして出力** — ページのボタンと描画ロジックを共有します。 |

### トリガー方法（4 つ）
1. エディター UI（`UCL_AgentCommandsPage`）ボタン
2. `Tools/UCL/Agent Commands/Run Pending` エディターメニュー
3. `AgentCommands/queue.json` を直接編集し、上記のいずれかのトリガーを呼び出す
4. **Python CLI ラッパー** — `Tools~/AgentCommands/run_cmd.py`（エージェント推奨）
5. **Unity Batchmode**（CI / 完全自動）

詳細な比較と例については、[UCL_AgentCommand_Architecture §7](API/UCL_AgentCommand/UCL_AgentCommand_Architecture.md#7-觸發方式對照) を参照してください。

---

## UCL_Asset アセットシステム

| ドキュメント | 説明 |
|---|---|
| [UCL_Asset API](API/UCL_Asset/) | アセットシリアライズ、Asset Entry、Common Editable インターフェース |

---

## エディターページ (UCL_EditorPage)

| ドキュメント | 説明 |
|---|---|
| [UCL_AgentCommandsPage](UCL_EditorPage/UCL_AgentCommandsPage.md) ⭐ | Agent Command メインページ（キュー管理 / 追加 / Run Pending / カタログ出力） |
| [UCL_CommonEditorPage](UCL_EditorPage/UCL_CommonEditorPage.md) | エディターページの共通基盤 |
| [UCL_ModuleEditPage](UCL_EditorPage/UCL_ModuleEditPage.md) | モジュール編集ページ |
| [UCL_ModuleServiceEditPage](UCL_EditorPage/UCL_ModuleServiceEditPage.md) | モジュールサービス編集ページ |
| [UCL_ModulePlayListPage](UCL_EditorPage/UCL_ModulePlayListPage.md) | モジュールプレイリストページ |
| [UCL_SelectAssetPage](UCL_EditorPage/UCL_SelectAssetPage.md) | アセットセレクターページ |

---

## UCL_ModuleService モジュールサービス

| ドキュメント | 説明 |
|---|---|
| [UCL_ModuleSystem_Architecture](UCL_ModuleService/UCL_ModuleSystem_Architecture.md) | モジュールシステム全体アーキテクチャ |
| [UCL_ModuleService_API](UCL_ModuleService/UCL_ModuleService_API.md) | サービス API |
| [UCL_Module_API](UCL_ModuleService/UCL_Module_API.md) | 単一モジュール API |
| [UCL_ModulePath_API](UCL_ModuleService/UCL_ModulePath_API.md) | パス計算 API |

---

## ワークフロー

| ドキュメント | 説明 |
|---|---|
| [HelpURL_Workflow](Workflows/HelpURL_Workflow.md) | `ucl_core:` / `eov_docs:` などのプレフィックスメカニズム |
| [Hardcoded_Localize](Workflows/Hardcoded_Localize.md) | ハードコードされたローカライズ文字列の処理 |

---

## 命名規則のクイックリファレンス

| パターン | 用途 |
|---|---|
| `Cmd_<TypeName>` | Agent Command Handler サブクラス（例: `Cmd_ResolveAssetReferences`） |
| `UCL_<Module>` | UCL フレームワーククラス |
| `UCL_<Page>Page` | エディター IMGUI ページ |
| `<NS>.EditorLib.AgentCommands` | Agent Command 名前空間 |

---

## クロスリポジトリリソース

- プロジェクトレベルのワークフロー（完全な Agent Command ワークフローおよびトラブルシューティングを含む）: [`docs/Workflows/AgentCommands_Workflow.md`](../../../../../../docs/Workflows/AgentCommands_Workflow.md)
- Python CLI ラッパー: [`Tools~/AgentCommands/run_cmd.py`](../../Tools~/AgentCommands/run_cmd.py)
- queue.json の場所: `AgentCommands/queue.json` (プロジェクトルートディレクトリ)

---

## その他の言語

- 🇬🇧 [English](../en/index.md)
- 🇯🇵 日本語（本ファイル）
- 🇨🇳 [简体中文](../zh-Hans/index.md)
- 🇹🇼 [繁體中文](../zh-Hant/index.md)
