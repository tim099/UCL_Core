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
| **`FindAssetUsages`** ⭐ | [Cmd_FindAssetUsages](API/UCL_AgentCommand/Cmd_FindAssetUsages.md) | **Asset 参照の逆引き** — ターゲット Asset（例：RCG_CustomStatusData/Stun）について全 UCL_Asset サブクラスから参照箇所を抽出（dotted field path 付き）|
| **`Invoke`** ⭐ | [Cmd_Invoke](API/UCL_AgentCommand/Cmd_Invoke.md) | **汎用リフレクション呼び出し** — 文字列での記述（type / member / args）から Unity の任意の public static + instance method / property / field を動的に発火させ、API ごとに専用 Cmd を書かずに済みます。instance 呼び出しと変数チェーン（`target=$var` / `storeAs=...`）に対応し、複数の invoke を連結可能。解析+実行は `UCL.Core.UCL_ReflectionInvoker`（UtilCore、runtime-available、Cmd 以外からも呼び出し可）に切り出され、Type 解決は `AssemblyExtensions` の共有 cache を利用します |

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

## UCL_GUILayout / UCL_GUIStyle（IMGUI コンポーネント + スタイル層）

| ドキュメント | 説明 |
|---|---|
| 🎨 **[UCL_GUILayout_Overview](API/UCL_GUILayout/UCL_GUILayout_Overview.md)** ⭐ | **8 ファイルの partial class 全体ガイド** — 設計レイヤー、各ファイルの責務、API クイックリファレンス（用途別グループ化）、ファイル間の共通パターン（三段オーバーロード / `[SerializeReference]` ポリモーフィズム自動検出 / リフレクションキャッシュ）、出番は少ないが価値の高い 3 つのヘルパー（`IntFieldAuto` / `PopupSearchCache` / `DrawCopyPaste`） |
| 🎨 [UCL_GUIStyle_Overview](API/UCL_GUIStyle/UCL_GUIStyle_Overview.md) | **IMGUI スタイルのハブ** — `BoxStyle` / `ButtonStyle` / `LabelStyle` / `TextField/Area`、DPI 用グローバル `Scale`、EditorWindow / Runtime のデュアルキャッシュ、`LabelStyle` の禁則（インタラクティブコントロールには使用不可） |

---

## Architecture

| ドキュメント | 説明 |
|---|---|
| [Architecture/Polymorphism_In_UCL](Architecture/Polymorphism_In_UCL.md) ⭐ | **ポリモーフィズム支援アーキテクチャ** — `[SerializeReference]` × `UCLI_TypeListable` × `UCL_PolymorphicHelper` × `UCL_TypeReflectCache` の四者が GUI 編集と JSON シリアライズパスでどう連携するか；新ポリモーフィックフィールドの推奨パターン、UnityJsonSerializableObject 双方向例外、cache ctor が service を呼んではいけない理由 |

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
| 🛠️ [Create_Cmd_Workflow](Workflows/Create_Cmd_Workflow.md) | **新しい `Cmd_<Name>.cs` サブクラス作成 SOP** — 命名 / ファイル配置デシジョンツリー（UCL_Core vs 下流モジュール） / 標準テンプレート（CommandType / ShortDescription / ArgsSchema / HelpURL） / ExecuteAsync の指針 / Editor 内検証 / 8 つのよくある落とし穴 / **§9 文書配置の自動判定スキーム**（`source_root` frontmatter + `Cmd_ValidateDocPlacement`）|
| 🛠️ [Create_EditorPage_Workflow](Workflows/Create_EditorPage_Workflow.md) ⭐ | **新しい `UCL_CommonEditorPage` サブクラス作成 SOP** — 継承関係 / 必須・任意 override / TopBarButtons カスタマイズ / エントリポイントの接続（親ページ / Welcome カード / メニュー） / UI コンポーネント選択ガイド（UCL_GUILayout と UCL_GUIStyle ドキュメントへリンク） / HelpURL の `{lang}` プレースホルダ / 8 つのよくある落とし穴 / 受け入れチェックリスト |
| 🔁 [Edit_Recompile_Loop_Workflow](Workflows/Edit_Recompile_Loop_Workflow.md) ⭐ | **agent が .cs を編集した後の強制同期 SOP** — `Cmd_Recompile` + Python `recompile` サブコマンド + `.compile_status.json` の三点セット；Edit → recompile → 0 errors になって初めて継続、そうでなければ messages を読んでループで修正（≤ 5 ラウンド）、故障モード対応表付き |

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
