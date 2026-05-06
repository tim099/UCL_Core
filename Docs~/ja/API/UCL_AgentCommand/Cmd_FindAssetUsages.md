---
title: Cmd_FindAssetUsages API
description: UCL_Asset 被参照箇所の逆引き — ターゲット Asset（例 RCG_CustomStatusData/Stun）を指定し、すべて（または指定された）UCL_Asset サブクラスのインスタンスをリフレクションで走査して、ターゲットを指す UCLI_AssetEntry とそのフィールドパスを抽出して出力する。
source_file: Assets/UCL/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/CMD/Cmd_FindAssetUsages.cs
namespace: UCL.Core.EditorLib.AgentCommands
last_updated: 2026-05-06
target_audience: [AI_Agent, Tools_Maintainer, Gameplay_Programmer]
---

# Cmd_FindAssetUsages

## 1. 概要

`Cmd_FindAssetUsages` は [Cmd_ResolveAssetReferences](Cmd_ResolveAssetReferences.md) の**逆方向**ツールです：

| | 順方向（Resolve） | 逆方向（FindUsages） |
|---|---|---|
| 起点 | 1 つの Asset | 1 つのターゲット Asset |
| 質問 | 「私は誰を参照しているか」 | 「**誰が私を参照しているか**」 |
| 出力 | 依存チェーン / スコープ manifest | 参照箇所リスト（フィールドパス付き）|

ターゲット Asset（例：`RCG_CustomStatusData/Stun`）を指定すると、`searchTypes` に列挙された（またはデフォルトでは全部の）UCL_Asset サブクラスのインスタンスを走査し、各 asset のフィールドツリーを再帰的にリフレクションし、ターゲットを指す `UCLI_AssetEntry` を見つけ、参照箇所を **dotted field path**（例：`m_Effects[2].m_Setting.m_StatusEntry`）として記録します。

典型的な用途：
- 「`RCG_CustomStatusData/Stun` を誰が使っている？」 → RCG_CardData / RCG_ItemData / RCG_EquipmentData / モンスタースキル等の参照元を列挙
- **リファクタリングの安全網**：Asset の削除・改名前に影響範囲を確認
- **バランス分析**：あるステータスが何枚のカードに付与/対抗されているか

## 2. 引数仕様 (Args Schema)

| 引数 | 必須 | デフォルト | 説明 |
|---|:-:|---|---|
| `targetType` | ✅ | — | 検索対象 Asset の C# 型名（例：`RCG_CustomStatusData`）|
| `targetIds` | ✅ | — | 検索対象 Asset ID（CSV、例：`Stun,Burn`）|
| `searchTypes` | ❌ | （全 UCL_Asset サブクラス）| 走査対象を限定する Asset 型の CSV |
| `outputPath` | ❌ | `AgentCommands/asset_usages_<Type>_<timestamp>.<ext>` | 出力先（Unity プロジェクトルート相対）|
| `format` | ❌ | `json` | 出力形式：`json` または `md` |
| `module` | ❌ | （全モジュール）| **使用側**のモジュールを限定 |
| `maxFieldDepth` | ❌ | `16` | リフレクションの再帰深度上限（サイクル対策）|

> [!TIP]
> 参照元の大まかなカテゴリが分かっている場合は `searchTypes` を**強く推奨**。大規模プロジェクトでは全件走査は数秒〜数十秒かかります。

## 3. 出力形式

### 3.1 JSON（デフォルト）

```json
{
  "command": "FindAssetUsages",
  "timestamp": "2026-05-06T12:34:56",
  "targetType": "RCG_CustomStatusData",
  "targetIds": ["Stun"],
  "scannedTypes": 24,
  "scannedAssets": 873,
  "totalHits": 5,
  "usagesByTarget": [
    {
      "targetKey": "RCG_CustomStatusData:Stun",
      "hitCount": 5,
      "hits": [
        {
          "usedByType": "RCG_ItemData",
          "usedById": "EmotionalDamage",
          "path": "CardGame/Assets/.BuiltinModules/.../RCG_ItemData/EmotionalDamage.json",
          "exists": true,
          "fieldPath": "$.m_ItemEffects[0].m_Setting.m_StatusEntry"
        }
      ]
    }
  ]
}
```

### 3.2 Markdown（`format=md`）

ターゲット別にグループ化した表。0 ヒットのターゲットは `> No usages found.` ブロックとして表示されます（コマンド失敗との誤認防止）。

## 4. queue.json での呼び出し例

```json
{
  "Id": "20260506-find-stun-usages",
  "Type": "FindAssetUsages",
  "Mode": "OneShot",
  "Args": {
    "targetType": "RCG_CustomStatusData",
    "targetIds": "Stun",
    "searchTypes": "RCG_CardData,RCG_ItemData,RCG_EquipmentData",
    "format": "md"
  },
  "Description": "Stun を参照する全 Card / Item / Equipment を抽出"
}
```

## 5. 動作仕様

### 5.1 リフレクションの仕組み

- 各 `searchType` ごとに `UCL_Util<T>.Util.GetAllIDs()` で ID 列挙
- 各 ID について `UCL_Util<T>.Util.GetAsset(id, true)` でインスタンス取得
- インスタンスフィールド（public + private — UCL の JSON シリアライザはフィールドを使用）を走査
- `UCLI_AssetEntry` に出会ったら `(entry.AssetType, entry.ID)` をターゲット集合と比較
- ヒットしたら dotted field path を記録
- primitive / enum / string / `UnityEngine.Object` はスキップ
- `ReferenceEqualityComparer` でサイクル・共有子オブジェクトの重複展開を防止

### 5.2 Field Path 形式

- ルート：`$`
- フィールド：`.FieldName`
- コレクションインデックス：`[i]`
- 例：`$.m_Effects[2].m_Settings[0].m_StatusEntry`

> [!IMPORTANT]
> Field path は**リフレクション時の C# フィールド名**。通常は JSON シリアライズ後のキーと一致しますが、カスタムシリアライザを使う稀なケースでは異なる可能性があるため、JSON とのマッピングは C# クラス定義を真とすること。

### 5.3 ResolveAssetReferences との違い

| | ResolveAssetReferences | FindAssetUsages |
|---|---|---|
| 方向 | 順方向（起点 → 参照先） | 逆方向（ターゲット ← 参照元） |
| Asset 横断 | ✅ BFS 多層 | ❌ 直接参照箇所のみ |
| ヒット後 | 参照先を queue に積んで展開 | フィールドパスを記録して停止 |
| 主コスト | 参照チェーン長 | 全 asset 数 × 平均フィールド深さ |
| Field path | ❌ 記録しない | ✅ dotted path 記録 |

## 6. 制限

- 設計時の **Asset 参照（`UCLI_AssetEntry`）のみ**解析；Localize / Sprite キー（文字列）は対象外
- デフォルトでは全 UCL_Asset サブクラスを走査するため、大規模プロジェクトでは `searchTypes` を限定すること
- リフレクションは Editor モードでのみ動作（`#if UNITY_EDITOR`）
- 1 つのフィールドが複数のターゲット ID を指す場合、ヒットも複数件
- リフレクションフィールド名 ≠ JSON キーとなる稀なカスタムシリアライザケースに注意

## 7. 関連文書

- [Cmd_ResolveAssetReferences](Cmd_ResolveAssetReferences.md) — 順方向版
- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [Create_Cmd_Workflow](../../Workflows/Create_Cmd_Workflow.md) — 新規 Cmd 作成 SOP

---

## 他の言語

- 🇬🇧 [English](../../../en/API/UCL_AgentCommand/Cmd_FindAssetUsages.md)
- 🇯🇵 日本語（本ファイル）
- 🇨🇳 [简体中文](../../../zh-Hans/API/UCL_AgentCommand/Cmd_FindAssetUsages.md)
- 🇹🇼 [繁體中文](../../../zh-Hant/API/UCL_AgentCommand/Cmd_FindAssetUsages.md)
