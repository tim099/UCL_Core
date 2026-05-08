---
title: 新規 UCL_Asset サブクラス作成ワークフロー
description: ステップ順の SOP — UCL_Core フレームワークにおける新規永続化データタイプの作成。**すべて UCL_Asset<T> を継承する必要があり**、生の ScriptableObject や自作のセーブ処理は厳禁です。継承テンプレート、ID/SaveFolderPath の命名規則、AssetGroup 属性、JSON シリアライズ、Edit/Preview フック、およびよくある落とし穴について網羅しています。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/AssetCore/UCL_Assets/
namespace: UCL.Core
last_updated: 2026-05-08
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
aliases: [Create UCL Asset, アセットの追加, UCL_Assetサブクラス, 永続化データ]
tags: [workflow, asset, scriptableobject, persistence]
related:
  - ucl_core:Docs~/{lang}/Workflows/Create_EditorPage_Workflow.md | Create EditorPage Workflow | 新規 Page エントリー（本ドキュメントとペア — Page は UI、Asset はデータ）
  - ucl_core:Docs~/{lang}/Workflows/Validate_UCL_Asset_Workflow.md | Validate UCL Asset Workflow | UCL_Asset のシリアライズ検証（.json 編集後に検証）
  - ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_SelectAssetPage.md | UCL_SelectAssetPage | すべての UCL_Asset サブクラスを自動列挙する選択 UI（自作のリストビューは不要です）
---

# 🛠️ 新規 UCL_Asset サブクラス作成ワークフロー

> [!IMPORTANT]
> **UCL_Core システムにおいては、すべての永続化データは一律で `UCL_Asset<T>` を継承する必要があります**。以下の手法は厳禁です：
> - 生の `ScriptableObject` + `[CreateAssetMenu]`（UCL_ModuleService のモジュールパス解決と互換性がありません）
> - 自作の `File.WriteAllText` / `JsonUtility.ToJson` などのセーブ処理（車輪の再発明 ＋ モジュールパス解決がありません）
> - FileSystemWatcher や EditorApplication.update を用いた双方向同期（UCL_Asset 自体が唯一のソース（Source-of-truth）であり、ミラーリングは不要です）
>
> 設計哲学：**1つの .json ファイル = 1つの固有 ID を持つ UCL_Asset サブクラスのインスタンス**。IO、モジュールパス、エディター UI、シリアライズはすべて基底（base）クラスが自動的に処理します。サブクラスはフィールドの定義と、2つのコンストラクターを用意するだけです。

---

## 0. TL;DR — 最小限のスケルトン

```csharp
using UnityEngine;

namespace UCL.Core
{
    [UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Data)]
    public class UCL_<Name>Asset : UCL_Asset<UCL_<Name>Asset>
    {
        public const string DefaultID = "Default";

        public string m_SomeField = string.Empty;
        public int    m_SomeNumber = 0;

        public UCL_<Name>Asset() { ID = DefaultID; }
        public UCL_<Name>Asset(string iID) { Init(iID); }
    }
}
```

**これだけです**。`[CreateAssetMenu]` も、OnValidate も、FileSystemWatcher も不要。編集、保存、読み込みはすべて全自動です。

---

## 1. なぜ UCL_Asset を継承するのか？

| 要望 | 生の ScriptableObject | UCL_Asset |
|---|---|---|
| クロスモジュール配置 | ❌ `Assets/...` パスのハードコーディング | ✅ `UCL_ModuleService` によるモジュール相対パス |
| JSON シリアライズ | ⚠ 自作の ToJson/FromJson が必要 | ✅ 基底クラスに標準搭載の `SerializeToJson` |
| エディター編集 UI | ⚠ 自作の Custom Inspector が必要 | ✅ `DrawObjectData` リフレクションを用いて OnGUI で自動描画 |
| リスト / 選択 UI | ⚠ 自作の EditorWindow が必要 | ✅ `UCL_SelectAssetPage` リフレクションによる自動一覧表示 |
| Mod システム互換 | ❌ モジュールシステムのスコープ外 | ✅ 該当する `UCL_Module` に応じた自動切り替え |
| 差分（Git diff） | ⚠ アセットファイルがバイナリ YAML | ✅ `.json` テキストファイルなのでマージに優しい |

**結論**：UCL_Core 自体が MOD に優しいアセットフレームワークです。追加する永続化データはすべて `UCL_Asset` を継承するべきです。Unity のシリアライズフックが必要な `UCL_LocalizeAsset` などの極めて特殊な例外を除き、この仕組みをバイパスしないでください。

---

## 2. 必須の構成要素

### 2.1 継承
```csharp
public class UCL_MyAsset : UCL_Asset<UCL_MyAsset>
```
ジェネリクス `T` はサブクラス自身です（CRTP テンプレートパターン）。

### 2.2 2つのコンストラクター

```csharp
public UCL_MyAsset() { ID = DefaultID; }    // 引数なし — リフレクション / new() 用
public UCL_MyAsset(string iID) { Init(iID); }  // ID 指定 — 明示的なインスタンス生成用
```

`UCL_Asset<T>` は `T : new()` という制約を持つため、**引数なしのコンストラクターは必須**です。

### 2.3 デフォルト ID 定数

```csharp
public const string DefaultID = "Default";
```

引数なしのコンストラクターがプレースホルダー用の ID としてこれを使用します（Init 時に上書きされます）。

### 2.4 フィールド（m_ プレフィックスの命名規則）

```csharp
public string m_DisplayName = string.Empty;
public List<string> m_Tags = new List<string>();
public Color m_TintColor = Color.white;
```

- フィールド名には `m_` プレフィックスを付与します（エディター UI で表示される際は、`UCL_LocalizeManager` によってプレフィックスが自動で取り除かれます）。
- デフォルト値はインラインの初期化子（initializer）で定義します。**コンストラクターの内部で new しないでください**（デシリアライズの際に上書きされる原因になります）。

---

## 3. オプションの属性（Attributes）

| 属性 | 用途 |
|---|---|
| `[UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.X)]` | アセットを Data / Config / Editor / Assembly などのグループに分類します（SelectAssetPage での並び順に影響） |
| `[UCL.Core.ATTR.UCL_Sort(int)]` | グループ内でのソート優先順位のヒント |
| `[HelpURL("ucl_core:Docs~/{lang}/...")]` | エディターの Inspector にドキュメントを開く「？」アイコンを表示します |

`UCL_ConfigAsset.cs` の例：
```csharp
[UCL.Core.ATTR.UCL_GroupIDAttribute(UCL_AssetGroup.Config)]
[UCL.Core.ATTR.UCL_Sort((int)UCL_AssetGroup.EditConfigType.UCL_ConfigAsset)]
public class UCL_ConfigAsset : UCL_Asset<UCL_ConfigAsset>
```

---

## 4. ファイル配置の仕様

基底クラスが配置パスを自動処理します：
- `SaveFolderPath` ➔ `<module>/UCL_Assets/<TypeName>/`
- `AssetPath` ➔ `<SaveFolderPath>/<ID>.json`
- 1つの ID ＝ 1つの `.json` ファイル（プレーンテキストで、git の差分追跡が容易です）

例（ID が `CurLangKey` の `UCL_ConfigAsset`）：
```
<module>/UCL_Assets/UCL_ConfigAsset/CurLangKey.json
  ➔ { "m_Value": "MyProj_CurLang" }
```

---

## 5. 編集・選択用の UI

### 5.1 UI 自作の禁止
- `UCL_CommonEditPage` があらゆる `UCL_Asset` の編集インターフェースを自動処理します — `UCL_CommonEditPage.Create(asset)` を呼び出すだけで編集画面が開きます。
- `UCL_SelectAssetPage` がリフレクションによりすべての `UCL_Asset` サブクラスを列挙し、自動でグループ化および検索が可能なリストを構築します。
- サブクラスにおいて `OnGUI` や `Preview` のカスタマイズをオーバーライドしない限り、基底クラスが `UCL_GUILayout.DrawObjectData` リフレクションを用いて自動的に UI を生成します。

### 5.2 基底クラスのエントリーポイントを使用する
```csharp
// 外部コードから編集画面を開く
UCL_CommonEditPage.Create(myAsset);

// 外部コードからアセット選択画面を開く（ユーザーにいずれかの ID を選ばせる）
// UCL_SelectAssetPage の規則に従います — 詳細は UCL_SelectAssetPage.md を参照
```

---

## 6. よくある落とし穴（Common Pitfalls）

| # | 落とし穴 | 症状 | 解決策 |
|---|---|---|---|
| 1 | `UCL_Asset<T>` ではなく `ScriptableObject` を継承している | SelectAssetPage にリストアップされず、`UCL_ModuleService` と連携できない | `UCL_Asset<T>` を継承するように変更します |
| 2 | 引数なしのコンストラクターがない | `UCL_Asset<T> where T : new()` のコンパイルが通らない | `public UCL_MyAsset() { ID = DefaultID; }` を追加します |
| 3 | コンストラクター内で `new List<>()` などを実行し、初期データを詰め込んでいる | シリアライズされた値が正常に読み込まれない（コンストラクターの new で上書きされるため） | インライン初期化子に移動します |
| 4 | フィールド名に `m_` プレフィックスがない | UI での表示名が UCL の標準規則と整合しません（機能はしますが非推奨です） | `m_FieldName` の形式に統一します |
| 5 | 自作の FileSystemWatcher や OnValidate によるセーブ書き戻しの双方向同期 | 重複処理やレースコンディション（競合）の原因になります | 自作の同期処理を削除します。UCL_Asset が唯一のソースです |
| 6 | `[CreateAssetMenu]` を使用して Project ウィンドウの右クリックから作成しようとする | 作成される `.asset` バイナリは UCL_Asset ではロードできません | 不要です。`CreateData(iID)` などのコード、または `SelectAssetPage` のフローを使用してください |
| 7 | EditorWindow を自作してアセットの一覧表示を独自に作っている | 車輪の再発明です | `UCL_SelectAssetPage` を直接使用してください |
| 8 | `.json` アセットとは別に `identities.json` のような単一ファイルの名簿管理を多重に作成している | 二重ソース管理によるデータ同期の問題が発生します | どちらか一方のソースオブトゥルースに統一してください（すべてアセットにするか、すべて単一ファイルにするか） |

---

## 7. このワークフローを適用するタイミング

- ユーザーから「新規にデータ X を追加したい」「設定ファイル X を作りたい」「ある状態を保存（永続化）したい」という要望があった時。
- エージェント自身が `[CreateAssetMenu]`、生の `ScriptableObject`、または独自に JSON のファイル IO 処理を記述しようとしていることに気づいた時 ➔ **一旦静止し**、まず本ドキュメントをお読みください。
- コードレビューの段階で、生の `ScriptableObject` を使用している実装を発見した時 ➔ `UCL_Asset` への移行を提案してください。

---

## 8. 実装の参考例

| アセット | 注目ポイント |
|---|---|
| `UCL_ConfigAsset` | 最もシンプルな構成（単一の `m_Value` 文字列）、`DefaultID` 規則の基礎 |
| `UCL_BundleAsset` | `IDisposable` の実装 ＋ フィールド UI のカスタマイズ（`UCLI_FieldOnGUI`） |
| `UCL_CSVAsset` | 大容量データの処理 ＋ 独自の OnGUI 描画 |
| `UCL_ChatTavernIdentityAsset` | キャラクターカード（rich persona） — `m_` プレフィックス付きの多様なフィールド、`List<string>` のリスト保持 |

---

## 9. 関連ドキュメント

- [Create_EditorPage_Workflow.md](Create_EditorPage_Workflow.md) — 対応する UI レイヤー（Page）の作成基準
- [Validate_UCL_Asset_Workflow.md](Validate_UCL_Asset_Workflow.md) — アセット json 変更時の自動検証（ValidateAssetFormat）
- [UCL_SelectAssetPage.md](../UCL_EditorPage/UCL_SelectAssetPage.md) — リスト一覧とアセット選択用 UI（アセットをリフレクションにより自動列挙）
- [UCL_CommonEditPage.md](../UCL_EditorPage/UCL_CommonEditPage.md) — アセット編集画面の仕様
- [Cmd_MigrateAssetToTemplate.md](../API/UCL_AgentCommand/Cmd_MigrateAssetToTemplate.md) — カスタマイズしたアセットを `Templates~` に還元してクロスプロジェクト配布に登録するフロー
- [UCL_CoreBootstrap.md](../UCL_ModuleService/UCL_CoreBootstrap.md) — `Templates~` とプロジェクト `.BuiltinModules` の双方向同期メカニズム
