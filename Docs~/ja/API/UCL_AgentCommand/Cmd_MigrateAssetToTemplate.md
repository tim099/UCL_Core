---
title: Cmd_MigrateAssetToTemplate — UCL_Asset .json をプロジェクトから Templates~ へ移行する
description: Agent Command — 指定された UCL_Asset サブクラスの .json を現在のプロジェクトの .BuiltinModules から UCL_Core の Templates~ にコピーします（クロスプロジェクトのテンプレートとなります）。UCL_CoreAssetBootstrap の AutoTemplatePush 自動配布メカニズムと連携して動作します。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_User]
tags: [agent-command, migration, template, asset]
related:
  - ucl_core:Docs~/{lang}/UCL_ModuleService/UCL_CoreBootstrap.md | UCL_CoreAssetBootstrap | Templates~ ↔ プロジェクト .BuiltinModules 双方向同期メカニズム
  - ucl_core:Docs~/{lang}/Workflows/Create_UCL_Asset_Workflow.md | Create UCL_Asset Workflow | 新規永続化アセット追加の SOP
---

# Cmd_MigrateAssetToTemplate

指定された `UCL_Asset` サブクラスの `.json` インスタンスを、現在のプロジェクトの `.BuiltinModules` から `UCL_Core` リポジトリの `Templates~` に移行し、**そのアセットをクロスプロジェクトのデフォルトテンプレート（範本）にします**。

---

## 1. 概要

### いつ使用するか

- 開発者が特定のプロジェクト内で `UCL_Asset` をカスタマイズした場合（例：`UCL_ChatTavernIdentityAsset` の `claude-da-xiaojie`）。
- このカスタマイズ内容をデフォルトテンプレートとして `UCL_Core` リポジトリに還元したい場合。
- その後、他のプロジェクトが `UCL_Core` を pull すると、[UCL_CoreBootstrap](../../UCL_ModuleService/UCL_CoreBootstrap.md) の **AutoTemplatePush** が自動的にそれを該当プロジェクトの `.BuiltinModules` に配布します。

### 既存メカニズムとの関係

| アクション | ツール |
|---|---|
| `UCL_Asset` インスタンスの新規追加 / 編集 | `UCL_SelectAssetPage` / `UCL_CommonEditPage`（Editor UI） |
| 編集済みアセットをテンプレート化する（**本コマンド**）| `Cmd_MigrateAssetToTemplate` |
| テンプレートを他のプロジェクトに自動配布 | [`AutoTemplatePushIfNeeded`](../../UCL_ModuleService/UCL_CoreBootstrap.md) (InitializeOnLoad) |
| 手動でのテンプレート配布トリガー | `Tools/UCL/Bootstrap/Push Templates → Modules (Force)` |

---

## 2. パラメータ

| パラメータ | 必須 | デフォルト | 説明 |
|---|---|---|---|
| `assetType` | ✅ | — | `UCL_Asset` サブクラスの短縮名（例：`UCL_ChatTavernIdentityAsset`）。大文字小文字を区別します。 |
| `id` | ✅ | — | 移行するアセットの ID（例：`claude-da-xiaojie`）。`*` を入力すると該当タイプの全アセットを移行します。 |
| `module` | ❌ | `Core` | ソースモジュール ID（複数モジュールのプロジェクトでのみ指定が必要）。 |
| `force` | ❌ | `false` | `true` = 既存のテンプレートを直接上書き。`false` = 存在する場合は skip。 |

---

## 3. パス仕様

```
src = <projectRoot>/Assets/.BuiltinModules/ModulesRoot/Modules/<module>/UCL_Assets/<assetType>/<id>.json
dst = <UCL_Core>/Templates~/Assets/.BuiltinModules/ModulesRoot/Modules/<module>/UCL_Assets/<assetType>/<id>.json
```

例（id=`claude-da-xiaojie`、assetType=`UCL_ChatTavernIdentityAsset`、module=`Core`）：
- src：`<project>/Assets/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets/UCL_ChatTavernIdentityAsset/claude-da-xiaojie.json`
- dst：`<UCL_Core>/Templates~/Assets/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets/UCL_ChatTavernIdentityAsset/claude-da-xiaojie.json`

パスの解決には `UCL_AssetPath.GetPath(BuiltinModules / TemplateModules)` を使用します。

---

## 4. 動作仕様

1. **`assetType` の検証**：アセンブリを走査して名前が一致し、実際に `UCL_Asset<T>` を継承しているクラスをリフレクションで検索。見つからない場合は fail。
2. **src / dst ディレクトリの計算**：`UCL_Assets/<TypeName>` サブフォルダー（[`UCL_ModulePath.ModuleRelativePath.GetAssetRelativePath`](../../../UCL_Core_Scripts/AssetCore/UCL_ModulePath.RelativePath.cs) に準拠）。
3. **単一ファイル vs すべて**：
   - `id=<具体的な ID>`：単一ファイルをコピー。
   - `id=*`：src 内のすべての `*.json` を列挙し、順次コピー。
4. **存在する場合の skip / 上書き**：`force` フラグの指定によります。
5. **完了**：`copied / skipped / missing` のカウント + src/dst パス + 「自動コミットは行われません」という警告を出力。

---

## 5. 使用例

### Python (run_cmd.py) から

```bash
# 単一アセットの移行
senate ucmd run MigrateAssetToTemplate \
    --arg assetType=UCL_ChatTavernIdentityAsset \
    --arg id=claude-da-xiaojie

# 全アセットの移行
senate ucmd run MigrateAssetToTemplate \
    --arg assetType=UCL_ChatTavernIdentityAsset \
    --arg id=*

# 強制上書き（テンプレートが既に存在していても上書き）
senate ucmd run MigrateAssetToTemplate \
    --arg assetType=UCL_ConfigAsset \
    --arg id=CurLangKey \
    --arg force=true

# モジュールを指定
senate ucmd run MigrateAssetToTemplate \
    --arg assetType=MyAsset \
    --arg id=MyID \
    --arg module=MyCustomModule
```

### UCL_AgentCommandsPage (Editor UI) から

`Tools/UCL/Agent Commands` ➔ `MigrateAssetToTemplate` を検索 ➔ 「Fill Example」でパラメータ例を自動入力 ➔ Run。

---

## 6. 完了後のアクション

⚠ **本コマンドは自動でコミットを行いません** — `Templates~` への書き込み完了後、[ucl-commit skill](../../../Skills~/ucl-commit/SKILL.md) の3層バンププロセスに従ってコミットを行う必要があります。

```bash
# 1. UCL_Core を Dev に切り替えてコミット
git -C <UCL_Core> switch Dev
git -C <UCL_Core> add Templates~
git -C <UCL_Core> commit -m "[feat] migrate <assetType>:<id> as default template"

# 2. UCL サブモジュールのバンプ
git -C <UCL> switch Dev
git -C <UCL> add UCL_Core
git -C <UCL> commit -m "[bump] UCL_Core <hash>"

# 3. メインプロジェクトのバンプ
git -C <project> add CardGame/Assets/UCL
git -C <project> commit -m "[bump] UCL <hash>"
```

詳細は [Commit_Workflow.md](../../Workflows/Commit_Workflow.md) を参照してください。

---

## 7. 失敗時のトラブルシューティング

| 状況 | 原因 | 解決策 |
|---|---|---|
| `UCL_Asset サブクラス 'X' が見つかりません` | タイプ名のスペルミス / 未コンパイル | スペルを確認（名前空間を含まない短縮名）+ `.cs` ファイルがコンパイルされているか確認 |
| `ソースディレクトリが存在しません` | 現在のプロジェクトに該当タイプのインスタンスがありません | エディターで `UCL_SelectAssetPage` を使用してアセットを1つ作成し、編集した後に再度移行を実行 |
| `ソースファイルが存在しません — skip` | 指定された ID に対応する `.json` が存在しません | ID のスペルを確認 / `id=*` で実際に何が存在するか確認 |
| `ターゲットが既に存在します (force=false) — skip` | テンプレートに既に同じファイルがあり、デフォルトでは上書きされません | `--arg force=true` を追加して強制上書きを実行 |
| `TemplateModules パスが見つかりません` | `UCL_CoreEditor.asmdef` が見つかりません | `UCL_Core` のパスが完全であるか確認 |

---

## 8. 関連ドキュメント

- [UCL_CoreBootstrap.md](../../UCL_ModuleService/UCL_CoreBootstrap.md) — `Templates~` システムおよび AutoTemplatePush メカニズムの全貌
- [Create_UCL_Asset_Workflow.md](../../Workflows/Create_UCL_Asset_Workflow.md) — 新規 `UCL_Asset` サブクラス追加の SOP
- [UCL_AgentCommand_Architecture.md](UCL_AgentCommand_Architecture.md) — Agent Command システムのアーキテクチャ
- [Commit_Workflow.md](../../Workflows/Commit_Workflow.md) — 3層サブモジュールのバンププロセス
- [Cmd_SeedTavernIdentityAssets.md](Cmd_SeedTavernIdentityAssets.md) — identities.json のメンバーリストから UCL_ChatTavernIdentityAsset テンプレートを生成する
