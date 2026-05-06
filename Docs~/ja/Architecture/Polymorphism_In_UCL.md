---
title: UCL_Core ポリモーフィズム支援アーキテクチャ
description: [SerializeReference]、UCLI_TypeListable、UCL_PolymorphicHelper、UCL_TypeReflectCache の四つが UCL_Asset の編集（GUI）と シリアライズ（JSON）パスでどう連携するか、新しいポリモーフィックフィールドの推奨パターン。
source_root: Assets/UCL/UCL_Core/UCL_Core_Scripts/InterfaceCore/
namespace: UCL.Core
last_updated: 2026-05-06
target_audience: [Tools_Maintainer, Gameplay_Programmer, AI_Agent]
---

# UCL_Core ポリモーフィズム支援アーキテクチャ

## 1. 四つのコンポーネント

```
[SerializeReference] (Unity 標準 attribute、フィールドに付与)
UCLI_TypeListable / UCLI_TypeList (UCL_Core のインターフェイスマーカー、型に実装)
        │
        ▼
UCL_PolymorphicHelper       ← ポリモーフィズムの SSOT
  IsPolymorphicField / IsPolymorphicElement / GetConcreteSubtypes
        │
        ▼
UCL_TypeReflectCache        ← (Type, SaveMode) ごとのフィールド metadata、事前フィルタなし
  m_Entries: List<UCL_FieldEntry>
  UCL_FieldEntry: m_IsPolymorphicField / m_HideOnGUI / m_HideInJson / m_Conditional / ...
                  GetAttr<T>() — lazy + キャッシュ
        │
        ▼
GUI パス                                 JSON パス
  FieldInfoCache (adapter)                 SaveFieldsToJson
  TypeFieldInfoCache (adapter)             LoadFieldFromJson
  フィルタ: !m_HideOnGUI                   フィルタ: !m_HideInJson && !MulticastDelegate
                                           runtime: m_Conditional?.IsShow(obj)
```

## 2. 二つのポリモーフィズムシグナル

| シグナル | 付与先 | 意味 | トリガ |
|---|---|---|---|
| `[SerializeReference]` | **フィールド** | 「実行時の値は宣言型のサブクラスかも」 | GUI ドロップダウン、JSON ClassName ラップ |
| `UCLI_TypeListable` | **型**（インターフェイス実装）| 「コレクション要素として使う際にサブクラス情報を保持」 | JSON IList の per-item ClassName ラップ |

両者は併用可。単一フィールドなら `[SerializeReference]` だけで十分。コレクション要素は型側のインターフェイス実装か、Step 3a の wrapped 形式自動検知のいずれかが必要。

## 3. 標準パターン

### 3.1 単一フィールド

```csharp
public abstract class MyBase { ... }
public class MyConcrete : MyBase { ... }

public class MyOwner
{
    [SerializeReference] public MyBase m_Field;     // ✅ GUI ドロップダウン + JSON ラウンドトリップ
}
```

`MyBase` は `UCLI_TypeListable` を実装する必要なし。GUI は `UCL_PolymorphicHelper.GetConcreteSubtypes` でサブクラスを列挙、JSON は `{ClassName, ClassData}` 形式でラップ。

### 3.2 コレクション

```csharp
[SerializeReference] public List<MyBase> m_List;    // ✅ Step 3a 以降 list item もラウンドトリップ
```

JSON layout:

```json
"m_List": {
  "ClassName": "List<MyBase> AQN",
  "ClassData": [
    { "ClassName": "MyConcrete, ...", "ClassData": { ... } },
    ...
  ]
}
```

### 3.3 例外：`UnityJsonSerializableObject` 子クラス要素

要素自身の `SerializeToJson` / `DeserializeFromJson` が既にラッパーを生成。Save / Load 双方で検知して新パスをスキップ — double-wrap / 誤ルーティングを避ける。

## 4. リフレクションキャッシュの使い方

```csharp
var aCache = UCL_TypeReflectCache.Get(typeof(MyAsset), JsonConvert.SaveMode.Unity);
foreach (var aEntry in aCache.m_Entries)
{
    if (aEntry.m_HideInJson) continue;
    if (aEntry.m_IsPolymorphicField) { /* ポリモーフィックパス */ }
    var aFolderExp = aEntry.GetAttr<UCL_FolderExplorerAttribute>();  // lazy + キャッシュ
}
```

**事前フィルタなし** — GUI は `!m_HideOnGUI`、JSON は `!m_HideInJson && !m_IsMulticastDelegate`。両パスで同じフィールド集合を共有することがキャッシュ抽出の目的。

## 5. なぜ `UCL_FieldEntry` ctor で service を呼んではいけないのか

キャッシュは JSON 載入の早い段階で構築される（`UCL_LocalizeAsset` 自体の load でも構築が走る）。この時点で `UCL_ModuleService` / `UCL_LocalizeManager` 等の service は未 init かもしれない。

**禁忌**（早期載入で NRE / 循環を引き起こす）:

| 操作 | 理由 |
|---|---|
| `UCL_LocalizeManager.Get(header)` | LocalizeManager 自身の load → cache 構築 → LocalizeManager → 循環 → StackOverflow |
| `GetCustomAttributes(true)` | **全** attribute 実体を構築 — `[UCL_FolderExplorer]` 等 ctor 内で `UCL_ModuleService` を呼ぶ重型 attribute も含む（service 未 init で NRE） |

**安全**: `GetCustomAttribute<T>()` は T のみ構築。事前取得する flag（SerializeReference / HideInJson / Conditional / FormerlyAs / HideOnGUI / AlwaysExpendOnGUI）は service 依存のない attribute なので問題なし。

レアな attribute は `GetAttr<T>()` の lazy + dict キャッシュ経由 — GUI レンダリング等 service-ready なパスでのみ初回反射する。

## 6. JSON list ポリモーフィズム（Step 3a）

**Save**: `[SerializeReference] List<...>` → 外側 `{ClassName, ClassData}` + per-item `ObjectToJson`。要素が `UnityJsonSerializableObject` 子クラスの場合は元の `ObjectToJson(list)` パスへフォールバック。

**Load**: `LoadDataFromJson` IList ブランチで以下のいずれかを検知:
- `UCLI_TypeList(able)` 要素型（既存パス）
- 最初の item が `ClassName` キーを含む（新規自動検知）

両方とも per-item `JsonToObject`。`UnityJsonSerializableObject` 要素型は両側で除外 — `DataToObject` の専用ハンドラに任せる。

## 7. 関連文書

- 📋 [SerializeReference_Symmetry_Plan](../Plan/SerializeReference_Symmetry_Plan.md)
- 📖 [DevLog 00005](../../../DevLogs~/00005_2026-05-06.md)
- 🤖 [Cmd_DiagnoseAssetReflection](../API/UCL_AgentCommand/Cmd_DiagnoseAssetReflection.md)
- 🤖 [Cmd_FindAssetUsages](../API/UCL_AgentCommand/Cmd_FindAssetUsages.md)

---

## 他の言語

- 🇬🇧 [English](../../en/Architecture/Polymorphism_In_UCL.md)
- 🇯🇵 日本語（本ファイル）
- 🇨🇳 [简体中文](../../zh-Hans/Architecture/Polymorphism_In_UCL.md)
- 🇹🇼 [繁體中文](../../zh-Hant/Architecture/Polymorphism_In_UCL.md)
