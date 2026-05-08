---
title: Cmd_SeedTavernIdentityAssets — identities.json のメンバーリストから UCL_ChatTavernIdentityAsset テンプレートを生成する
description: Agent Command — identities.json を読み込み、各 identity に対する UCL_ChatTavernIdentityAsset .json テンプレートを生成します。m_Tags には対応する kind をあらかじめ入力し、その他の詳細情報フィールド（avatar、role_settings、color、catchphrases）は空のままにしてユーザーの編集を待ちます。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/ChatTavern/
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_User]
tags: [agent-command, tavern, identity, asset, bootstrap]
related:
  - ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_MigrateAssetToTemplate.md | Cmd_MigrateAssetToTemplate | 生成されたアセットを Templates~ に移動してデフォルトテンプレートにする
  - ucl_core:Docs~/{lang}/UCL_ModuleService/UCL_CoreBootstrap.md | UCL_CoreAssetBootstrap | Templates~ ↔ プロジェクト .BuiltinModules 双方向同期
---

# Cmd_SeedTavernIdentityAssets

`identities.json` の軽量な名簿（roster）に基づいて、各キャラクターの対応する `UCL_ChatTavernIdentityAsset` `.json` テンプレートを自動生成（seed）します。

---

## 1. 概要

### なぜ必要なのか

- `identities.json` は `Cmd_Tavern` や Python で使用される**軽量な名簿（lightweight roster）**（id / display_name / kind / created_at / last_seen_at）です。
- `UCL_ChatTavernIdentityAsset` は、より詳細な情報を持つ**リッチなペルソナ（rich persona）**のビューレイヤー（avatar / role_settings / color / catchphrases / tags）です。
- これら二つは独立していますが、通常リッチデータは特定のキャラクターに対応します。
- 特定のキャラクターに詳細なリッチデータを初めて追加したい場合、まずは対応する `.json` テンプレートの「殻」を生成し、その後 Unity エディターで編集する必要があります。

### 全体の流れ（一回限りの bootstrap）

```
1. (前提) identities.json に既に5つのキャラクターが登録されている（Cmd_Tavern op=join によって自然生成）
   ▼
2. Cmd_SeedTavernIdentityAssets を実行し、5つの UCL_ChatTavernIdentityAsset .json テンプレート殻を一撃で生成
   ▼ (生成先は <project>/Assets/.BuiltinModules/.../UCL_Assets/UCL_ChatTavernIdentityAsset/)
3. エディター内で UCL_SelectAssetPage を使用して UCL_ChatTavernIdentityAsset を検索 ➔ avatar / role_settings / catchphrases を編集
   ▼
4. (オプション) Cmd_MigrateAssetToTemplate id=* を実行してすべてのアセットを Templates~ に移行
   ▼
5. クロスプロジェクトへの配布 ➔ 他のプロジェクトが UCL_Core を pull すると AutoTemplatePush によって自動的に反映
```

---

## 2. パラメータ

| パラメータ | 必須 | デフォルト | 説明 |
|---|---|---|---|
| `force` | ❌ | `false` | `true` = 既存のアセットを上書き。`false` = skip。 |
| `onlyId` | ❌ | `""` | 指定した ID のキャラクターのみテンプレート作成（テストや個別追加用）。空 = 名簿の全員分。 |

---

## 3. 自動入力されるフィールド

| フィールド | 入力される内容 | 備考 |
|---|---|---|
| `ID` | identity.id | UCL_Asset システムの安定キー |
| `m_Tags` | `[<kind>]` | roster.kind（"agent" / "human" / "npc" / "system"）に一致し、一目で分類が分かります。 |
| `m_AvatarPath` | `""` | ユーザーの編集待ち（Inspector で Sprite パスをドラッグ＆ドロップ） |
| `m_RoleSettings` | `""` | ユーザーの編集待ち（ペルソナ用のテンプレート定義） |
| `m_ColorHex` | `""` | ユーザーの編集待ち（#RRGGBB 形式） |
| `m_Catchphrases` | `[]` | ユーザーの編集待ち（LLM のペルソナプロンプトで使用する箇条書き） |

---

## 4. パス仕様

```
src = AgentCommands/ChatTavern/identities.json (roster)
dst = <projectRoot>/Assets/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets/UCL_ChatTavernIdentityAsset/<id>.json
```

`UCL_Asset.Save()` API を呼び出し、`UCL_ModuleService` によって現在編集中のモジュールパスを解決します。

---

## 5. 使用例

```bash
# 名簿全員のテンプレートを自動生成（既存のものはデフォルトで上書きしません）
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run SeedTavernIdentityAssets

# 強制的に全員分を上書き生成
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run SeedTavernIdentityAssets --arg force=true

# 特定のキャラクター1つだけ生成（テストや個別追加用）
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run SeedTavernIdentityAssets --arg onlyId=claude-da-xiaojie
```

---

## 6. 完了後のアクション

コンソールに `created / skipped / failed` のカウント ➔ 次のアクションの推奨事項が出力されます：
- エディターを開き、`UCL_SelectAssetPage` から `UCL_ChatTavernIdentityAsset` を検索して編集します。
- 編集が終わったら、`Cmd_MigrateAssetToTemplate id=*` を実行して `Templates~` に移行します。

---

## 7. 関連ドキュメント

- [Cmd_MigrateAssetToTemplate.md](Cmd_MigrateAssetToTemplate.md) — 次のステップ：`Templates~` への移行
- [UCL_CoreBootstrap.md](../../UCL_ModuleService/UCL_CoreBootstrap.md) — `Templates~` システムの動作メカニズム
- [Create_UCL_Asset_Workflow.md](../../Workflows/Create_UCL_Asset_Workflow.md) — UCL_Asset フレームワークの基礎
